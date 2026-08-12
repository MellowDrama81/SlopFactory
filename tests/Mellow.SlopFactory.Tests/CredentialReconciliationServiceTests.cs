using Microsoft.Data.Sqlite;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Gui.Services;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class CredentialReconciliationServiceTests
{
    private static async Task<(string Root, ILibraryWorkspace Workspace, string LibraryId, CredentialReconciliationService Service, FakeSecureCredentialStore Store)> CreateHarnessAsync(TemporaryDirectory temporary)
    {
        var root = temporary.Child("library");
        var libraries = new AppLibraryState(new LibraryWorkspaceFactory(), new FakeLibraryLocationService(root), new FakeRecentLibraryService(), new LibraryAvailabilityProbe(), new FakeAppPreferenceStore());
        await libraries.InitializeAsync();
        var store = new FakeSecureCredentialStore();
        var service = new CredentialReconciliationService(libraries, store);
        return (root, libraries.Workspace!, libraries.Workspace!.Descriptor.LibraryId, service, store);
    }

    [Fact]
    public async Task ReconcileRemovesAnOrphanedCandidateFoundAtStartup()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, libraryId, service, store) = await CreateHarnessAsync(temporary);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var revisionId = await workspace.BeginCredentialCandidateAsync(connection.Id);
        await store.SetCandidateAsync(libraryId, connection.Id, revisionId, "candidate-key");

        await service.ReconcileAsync(workspace, libraryId);

        var snapshot = Assert.Single(await workspace.GetCredentialLedgerSnapshotAsync(), s => s.ConnectionId == connection.Id);
        Assert.Empty(snapshot.Revisions);
        Assert.Null(await store.GetCandidateAsync(libraryId, connection.Id, revisionId));
    }

    [Fact]
    public async Task ReconcileFlagsRequiresRepairWhenTheCommittedPointerHasNoMatchingActiveRow()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, libraryId, service, store) = await CreateHarnessAsync(temporary);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var revisionId = await workspace.BeginCredentialCandidateAsync(connection.Id);
        await store.SetActiveAsync(libraryId, connection.Id, revisionId, "active-key");
        await workspace.PromoteCredentialRevisionAsync(connection.Id, revisionId);
        await workspace.DeleteCredentialLedgerRowAsync(connection.Id, revisionId);

        await service.ReconcileAsync(workspace, libraryId);

        var reloaded = await workspace.GetConnectionAsync(connection.Id);
        Assert.True(reloaded.CredentialRequiresRepair);
        Assert.Equal(revisionId, reloaded.CredentialRevisionId);
    }

    [Fact]
    public async Task ReconcileFlagsRequiresRepairWhenTheActiveSecureStorageValueIsMissing()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, libraryId, service, store) = await CreateHarnessAsync(temporary);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var revisionId = await workspace.BeginCredentialCandidateAsync(connection.Id);
        await workspace.PromoteCredentialRevisionAsync(connection.Id, revisionId);

        await service.ReconcileAsync(workspace, libraryId);

        var reloaded = await workspace.GetConnectionAsync(connection.Id);
        Assert.True(reloaded.CredentialRequiresRepair);
    }

    [Fact]
    public async Task ReconcileCleansUpAStraySupersededActiveLedgerRowUnderAValidPointer()
    {
        using var temporary = new TemporaryDirectory();
        var (root, workspace, libraryId, service, store) = await CreateHarnessAsync(temporary);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");

        var currentRevisionId = await workspace.BeginCredentialCandidateAsync(connection.Id);
        await store.SetActiveAsync(libraryId, connection.Id, currentRevisionId, "current-key");
        await workspace.PromoteCredentialRevisionAsync(connection.Id, currentRevisionId);

        const string straySupersededRevisionId = "stray-superseded-revision";
        await store.SetActiveAsync(libraryId, connection.Id, straySupersededRevisionId, "stray-key");
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var sqlite = new SqliteConnection(connectionString))
        {
            await sqlite.OpenAsync();
            await using var command = sqlite.CreateCommand();
            command.CommandText = "INSERT INTO connection_credential_revisions(connection_id,revision_id,purpose,created_at) VALUES($cid,$rid,1,$now);";
            command.Parameters.AddWithValue("$cid", connection.Id);
            command.Parameters.AddWithValue("$rid", straySupersededRevisionId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await service.ReconcileAsync(workspace, libraryId);

        var snapshot = Assert.Single(await workspace.GetCredentialLedgerSnapshotAsync(), s => s.ConnectionId == connection.Id);
        var revision = Assert.Single(snapshot.Revisions);
        Assert.Equal(currentRevisionId, revision.RevisionId);
        Assert.Null(await store.GetActiveAsync(libraryId, connection.Id, straySupersededRevisionId));
        Assert.Equal("current-key", await store.GetActiveAsync(libraryId, connection.Id, currentRevisionId));
        var reloaded = await workspace.GetConnectionAsync(connection.Id);
        Assert.False(reloaded.CredentialRequiresRepair);
    }

    [Fact]
    public async Task ReconcileSilentlyAdoptsALegacyCredentialWithoutRequiringRepairOrAlteringItsTestStatus()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, libraryId, service, store) = await CreateHarnessAsync(temporary);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        connection = await workspace.SetConnectionCredentialStateAsync(connection.Id, true);
        connection = await workspace.SetConnectionTestResultAsync(connection.Id, true, "Looked good last time.");
        await store.SetLegacyAsync(libraryId, connection.Id, "legacy-key");

        await service.ReconcileAsync(workspace, libraryId);

        var reloaded = await workspace.GetConnectionAsync(connection.Id);
        Assert.False(reloaded.CredentialRequiresRepair);
        Assert.NotNull(reloaded.CredentialRevisionId);
        Assert.Equal(ConnectionTestStatus.Success, reloaded.LastTestStatus);
        Assert.Equal("Looked good last time.", reloaded.LastTestMessage);
        Assert.Equal("legacy-key", await store.GetActiveAsync(libraryId, connection.Id, reloaded.CredentialRevisionId!));
        Assert.Null(await store.GetLegacyAsync(libraryId, connection.Id));
    }

    [Fact]
    public async Task ReconcileMarksRequiresRepairWhenALegacyHasCredentialFlagHasNoLegacySecureStorageValue()
    {
        using var temporary = new TemporaryDirectory();
        var (_, workspace, libraryId, service, store) = await CreateHarnessAsync(temporary);
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        await workspace.SetConnectionCredentialStateAsync(connection.Id, true);

        await service.ReconcileAsync(workspace, libraryId);

        var reloaded = await workspace.GetConnectionAsync(connection.Id);
        Assert.True(reloaded.CredentialRequiresRepair);
        Assert.Null(reloaded.CredentialRevisionId);
    }

    private sealed class FakeSecureCredentialStore : ISecureCredentialStore
    {
        private readonly Dictionary<string, string> _active = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _candidate = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _legacy = new(StringComparer.Ordinal);

        public Task<string?> GetActiveAsync(string libraryId, string connectionId, string revisionId) =>
            Task.FromResult(_active.TryGetValue(ActiveKey(libraryId, connectionId, revisionId), out var value) ? value : null);
        public Task SetActiveAsync(string libraryId, string connectionId, string revisionId, string value)
        {
            _active[ActiveKey(libraryId, connectionId, revisionId)] = value;
            return Task.CompletedTask;
        }
        public Task RemoveActiveAsync(string libraryId, string connectionId, string revisionId)
        {
            _active.Remove(ActiveKey(libraryId, connectionId, revisionId));
            return Task.CompletedTask;
        }

        public Task<string?> GetCandidateAsync(string libraryId, string connectionId, string revisionId) =>
            Task.FromResult(_candidate.TryGetValue(CandidateKey(libraryId, connectionId, revisionId), out var value) ? value : null);
        public Task SetCandidateAsync(string libraryId, string connectionId, string revisionId, string value)
        {
            _candidate[CandidateKey(libraryId, connectionId, revisionId)] = value;
            return Task.CompletedTask;
        }
        public Task RemoveCandidateAsync(string libraryId, string connectionId, string revisionId)
        {
            _candidate.Remove(CandidateKey(libraryId, connectionId, revisionId));
            return Task.CompletedTask;
        }

        public Task<string?> GetLegacyAsync(string libraryId, string connectionId) =>
            Task.FromResult(_legacy.TryGetValue(LegacyKey(libraryId, connectionId), out var value) ? value : null);
        public Task SetLegacyAsync(string libraryId, string connectionId, string value)
        {
            _legacy[LegacyKey(libraryId, connectionId)] = value;
            return Task.CompletedTask;
        }
        public Task RemoveLegacyAsync(string libraryId, string connectionId)
        {
            _legacy.Remove(LegacyKey(libraryId, connectionId));
            return Task.CompletedTask;
        }

        private static string ActiveKey(string libraryId, string connectionId, string revisionId) => $"active:{libraryId}:{connectionId}:{revisionId}";
        private static string CandidateKey(string libraryId, string connectionId, string revisionId) => $"candidate:{libraryId}:{connectionId}:{revisionId}";
        private static string LegacyKey(string libraryId, string connectionId) => $"legacy:{libraryId}:{connectionId}";
    }

    private sealed class FakeLibraryLocationService(string defaultPath) : ILibraryLocationService
    {
        public string DefaultPath => defaultPath;
        public bool IsAllowedPath(string path) => true;
    }

    private sealed class FakeRecentLibraryService : IRecentLibraryService
    {
        public IReadOnlyList<RecentLibrary> GetAll() => [];
        public void RecordOpened(LibraryDescriptor descriptor) { }
        public void RecordFailure(string path, string displayName, string? libraryId, RememberedLibraryState state, string failureStage, string diagnosticId) { }
        public void ValidateNoOverlap(string candidatePath) { }
    }

    private sealed class FakeAppPreferenceStore : IAppPreferenceStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string ReadString(string key, string defaultValue) => _values.TryGetValue(key, out var value) ? value : defaultValue;
        public void WriteString(string key, string value) => _values[key] = value;
    }
}
