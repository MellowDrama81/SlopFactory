using System.Text.Json;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Gui.Services;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class ExportCleanupJournalTests
{
    private sealed class FakeExportJournalStorage : IExportJournalStorage
    {
        public string Json = string.Empty;
        public string GetJournalJson() => Json;
        public void SetJournalJson(string json) => Json = json;
    }

    private sealed class FakeExportJournalSecretStore : IExportJournalSecretStore
    {
        private string? _secret;
        public Task<string?> GetSecretAsync() => Task.FromResult(_secret);
        public Task SetSecretAsync(string value) { _secret = value; return Task.CompletedTask; }
    }

    private sealed class TriggeredExportFaultInjector : IExportFaultInjector
    {
        public bool ThrowBeforeTempCreation;
        public bool ThrowBeforeAtomicCommit;
        public bool ThrowBeforeJournalRemoval;

        public Task BeforeTempCreationAsync(CancellationToken cancellationToken)
        {
            if (ThrowBeforeTempCreation) throw new IOException("simulated fault: before temp creation");
            return Task.CompletedTask;
        }

        public Task BeforeAtomicCommitAsync(CancellationToken cancellationToken)
        {
            if (ThrowBeforeAtomicCommit) throw new IOException("simulated fault: before atomic commit");
            return Task.CompletedTask;
        }

        public Task BeforeJournalRemovalAsync(CancellationToken cancellationToken)
        {
            if (ThrowBeforeJournalRemoval) throw new IOException("simulated fault: before journal removal");
            return Task.CompletedTask;
        }
    }

    private static ExportCleanupJournalService NewService(out FakeExportJournalStorage storage)
    {
        storage = new FakeExportJournalStorage();
        return new ExportCleanupJournalService(storage, new FakeExportJournalSecretStore());
    }

    private static int CountJournalEntries(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("entries").GetArrayLength();
    }

    [Fact]
    public async Task SweepTreatsAbsentTargetAsAlreadyAbsentAndRemovesEntry()
    {
        using var service = NewService(out var storage);
        var missing = Path.Combine(Path.GetTempPath(), $"slopfactory-missing-{Guid.NewGuid():N}.tmp");
        await service.RecordPlannedAsync(ExportCleanupObjectType.LocalTempFile, Path.GetTempPath(), Path.GetFileName(missing), missing);

        var reported = await service.SweepAsync();

        Assert.Empty(reported);
        Assert.Equal(0, CountJournalEntries(storage.Json));
    }

    [Fact]
    public async Task SweepDeletesConfirmedTempFileMatchingJournaledIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Child("orphan.slopfactory-exporting");
        await File.WriteAllTextAsync(path, "orphan");
        using var service = NewService(out var storage);
        var operationId = await service.RecordPlannedAsync(ExportCleanupObjectType.LocalTempFile, temporary.Path, "orphan.slopfactory-exporting", Path.GetFullPath(path));
        await service.ConfirmAsync(operationId);

        var reported = await service.SweepAsync();

        Assert.Empty(reported);
        Assert.False(File.Exists(path));
        Assert.Equal(0, CountJournalEntries(storage.Json));
    }

    [Fact]
    public async Task SweepLeavesTargetChangedEntryUntouchedAsCleanupPending()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Child("became-a-directory.slopfactory-exporting");
        Directory.CreateDirectory(path);
        using var service = NewService(out var storage);
        var operationId = await service.RecordPlannedAsync(ExportCleanupObjectType.LocalTempFile, temporary.Path, "became-a-directory.slopfactory-exporting", Path.GetFullPath(path));
        await service.ConfirmAsync(operationId);

        var reported = await service.SweepAsync();

        var entry = Assert.Single(reported);
        Assert.Equal(ExportCleanupState.CleanupPending, entry.State);
        Assert.True(Directory.Exists(path));
        Assert.Equal(1, CountJournalEntries(storage.Json));
    }

    [Fact]
    public async Task SweepAlwaysReportsAndroidDocumentUriAsCleanupPendingWithoutDeleting()
    {
        using var service = NewService(out var storage);
        var operationId = await service.RecordPlannedAsync(ExportCleanupObjectType.AndroidDocumentUri, "content://tree", "doc-name", "content://tree/doc-id");
        await service.ConfirmAsync(operationId);

        var reported = await service.SweepAsync();

        var entry = Assert.Single(reported);
        Assert.Equal(ExportCleanupState.CleanupPending, entry.State);
        Assert.Equal(1, CountJournalEntries(storage.Json));
    }

    [Fact]
    public async Task SweepIgnoresEntryWithTamperedIdentityWithoutDeletingOrThrowing()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Child("tampered.slopfactory-exporting");
        await File.WriteAllTextAsync(path, "data");
        using var service = NewService(out var storage);
        var operationId = await service.RecordPlannedAsync(ExportCleanupObjectType.LocalTempFile, temporary.Path, "tampered.slopfactory-exporting", Path.GetFullPath(path));
        await service.ConfirmAsync(operationId);

        // Hand-edit the persisted target identity (as if the entry were tampered with, or foreign)
        // so the entry's HMAC no longer verifies against the current secret.
        var tamperedTarget = Path.Combine(temporary.Path, "different-target.tmp");
        // The stored JSON escapes path separators, so match the encoded form rather than the raw path.
        var jsonEncodedOriginal = JsonSerializer.Serialize(Path.GetFullPath(path));
        var jsonEncodedTampered = JsonSerializer.Serialize(tamperedTarget);
        var tamperedJson = storage.Json.Replace(jsonEncodedOriginal, jsonEncodedTampered);
        Assert.NotEqual(storage.Json, tamperedJson);
        storage.Json = tamperedJson;

        var reported = await service.SweepAsync();

        Assert.Empty(reported);
        Assert.True(File.Exists(path));
        Assert.Equal(1, CountJournalEntries(storage.Json));
    }

    [Fact]
    public async Task LiveFaultBeforeTempCreationSelfHealsJournalAndLeavesNoOrphan()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "content");
        using var journal = NewService(out var storage);
        var injector = new TriggeredExportFaultInjector { ThrowBeforeTempCreation = true };
        await using var workspace = await LibraryWorkspaceFactory.CreateForFaultInjectionTestAsync(temporary.Child("library"), journal, injector);
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var destination = temporary.Child("export.txt");

        var result = await workspace.ExportFileAsync(file.Id, destination);

        Assert.Equal(FileExportOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.slopfactory-exporting", SearchOption.AllDirectories));
        Assert.Equal(0, CountJournalEntries(storage.Json));
    }

    [Fact]
    public async Task LiveFaultBeforeAtomicCommitSelfHealsJournalAndLeavesNoOrphan()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "content");
        using var journal = NewService(out var storage);
        var injector = new TriggeredExportFaultInjector { ThrowBeforeAtomicCommit = true };
        await using var workspace = await LibraryWorkspaceFactory.CreateForFaultInjectionTestAsync(temporary.Child("library"), journal, injector);
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var destination = temporary.Child("export.txt");

        var result = await workspace.ExportFileAsync(file.Id, destination);

        Assert.Equal(FileExportOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.slopfactory-exporting", SearchOption.AllDirectories));
        Assert.Equal(0, CountJournalEntries(storage.Json));
    }

    [Fact]
    public async Task LiveFaultBeforeJournalRemovalStillCommitsMediaButSelfHealsJournal()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "content");
        using var journal = NewService(out var storage);
        var injector = new TriggeredExportFaultInjector { ThrowBeforeJournalRemoval = true };
        await using var workspace = await LibraryWorkspaceFactory.CreateForFaultInjectionTestAsync(temporary.Child("library"), journal, injector);
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var destination = temporary.Child("export.txt");

        var result = await workspace.ExportFileAsync(file.Id, destination);

        // The atomic rename already committed the media before this boundary's fault fires, so the
        // destination exists with correct bytes even though the reported outcome is Failed (the
        // exception is caught generically after the commit and short-circuits the read-back
        // verification/Exported result). The journal still self-heals via the same finally block.
        Assert.Equal(FileExportOutcome.Failed, result.Outcome);
        Assert.True(File.Exists(destination));
        Assert.Equal("content", await File.ReadAllTextAsync(destination));
        Assert.Equal(0, CountJournalEntries(storage.Json));
    }
}
