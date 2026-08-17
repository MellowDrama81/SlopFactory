using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Gui.Services;

/// <summary>Wraps the raw device-local storage <see cref="ExportCleanupJournalService"/> persists its
/// entries to — split out purely so tests can substitute an in-memory fake instead of the real
/// <c>Preferences.Default</c> (which needs a live MAUI platform context to run).</summary>
public interface IExportJournalStorage
{
    string GetJournalJson();
    void SetJournalJson(string json);
}

/// <summary>Wraps the OS secure-storage secret <see cref="ExportCleanupJournalService"/> authenticates
/// entries with — split out purely so tests can substitute an in-memory fake instead of the real
/// <c>SecureStorage.Default</c> (which needs a live MAUI platform context to run).</summary>
public interface IExportJournalSecretStore
{
    Task<string?> GetSecretAsync();
    Task SetSecretAsync(string value);
}

/// <summary>
/// <see cref="IExportCleanupJournal"/> implementation backed by device-local JSON (mirroring
/// <see cref="PendingResultRegistryService"/>'s shape) with each entry authenticated by an
/// HMAC-SHA256 keyed by a secret held in OS secure storage under its own key — a separate namespace
/// from connection credentials (plan.md:609), generated once on first use. An entry whose HMAC does
/// not verify against the current secret (tampered, or written by a build using a different/lost
/// secret) is never acted on by <see cref="SweepAsync"/> — it is left in the journal exactly as
/// found, never deleted or used to authorize a filesystem mutation. Storage access is behind
/// <see cref="IExportJournalStorage"/>/<see cref="IExportJournalSecretStore"/> — no direct reference
/// to <c>Preferences</c>/<c>SecureStorage</c> anywhere in this class — purely so this class's real
/// sweep/authentication logic can be linked into and run under the plain (non-MAUI) test project,
/// which has no live MAUI platform context for the real storage to work in. The real
/// <c>Preferences</c>/<c>SecureStorage</c>-backed implementations
/// (<c>PreferencesExportJournalStorage</c>/<c>SecureStorageExportJournalSecretStore</c>) are wired in
/// by the DI registration in <c>MauiProgram</c>, not defaulted here.
/// </summary>
public sealed class ExportCleanupJournalService(IExportJournalStorage storage, IExportJournalSecretStore secretStore) : IExportCleanupJournal, IDisposable
{
    private const int JournalSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IExportJournalStorage _storage = storage;
    private readonly IExportJournalSecretStore _secretStore = secretStore;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _secretGate = new(1, 1);
    private byte[]? _secret;

    public async Task<string> RecordPlannedAsync(ExportCleanupObjectType objectType, string parentPath, string opaqueName, string targetIdentity, CancellationToken cancellationToken = default)
    {
        var secret = await GetOrCreateSecretAsync(cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow;
        var entry = new PersistedEntry(operationId, (int)objectType, parentPath, opaqueName, targetIdentity, (int)ExportCleanupState.PlannedTemporary, createdAt, string.Empty);
        entry = entry with { Hmac = ComputeHmac(secret, entry) };
        lock (_gate)
        {
            var items = Read();
            items.Add(entry);
            Write(items);
        }

        return operationId;
    }

    public async Task ConfirmAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var secret = await GetOrCreateSecretAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var items = Read();
            var index = items.FindIndex(item => string.Equals(item.OperationId, operationId, StringComparison.Ordinal));
            if (index < 0) return;
            var updated = items[index] with { State = (int)ExportCleanupState.Confirmed };
            updated = updated with { Hmac = ComputeHmac(secret, updated) };
            items[index] = updated;
            Write(items);
        }
    }

    public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var items = Read();
            items.RemoveAll(item => string.Equals(item.OperationId, operationId, StringComparison.Ordinal));
            Write(items);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ExportCleanupEntry>> SweepAsync(CancellationToken cancellationToken = default)
    {
        var secret = await GetOrCreateSecretAsync(cancellationToken).ConfigureAwait(false);
        List<PersistedEntry> items;
        lock (_gate) items = Read();

        var kept = new List<PersistedEntry>();
        var reported = new List<ExportCleanupEntry>();

        foreach (var item in items)
        {
            if (!VerifyHmac(secret, item))
            {
                // Tampered, foreign, or written under a since-lost secret — never acted on.
                kept.Add(item);
                continue;
            }

            if ((ExportCleanupObjectType)item.ObjectType == ExportCleanupObjectType.AndroidDocumentUri)
            {
                // Deferred: a real SAF permission-loss/reauthorization cycle needs on-device
                // verification this app doesn't attempt yet — always reported, never deleted.
                var pending = item with { State = (int)ExportCleanupState.CleanupPending };
                kept.Add(pending);
                reported.Add(ToEntry(pending));
                continue;
            }

            var targetPath = Path.Combine(item.ParentPath, item.OpaqueName);
            if (!Path.Exists(targetPath))
            {
                // Already Absent — never created (crash before creation) or already committed away
                // (crash after the atomic rename but before journal removal). Either way, resolved.
                // Path.Exists (not File.Exists) so a directory now sitting at this path is not
                // misreported as absent — that case must fall through to the Target Changed branch
                // below instead.
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes;
            try { attributes = File.GetAttributes(targetPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var pending = item with { State = (int)ExportCleanupState.CleanupPending };
                kept.Add(pending);
                reported.Add(ToEntry(pending));
                continue;
            }

            var isPlainFile = (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
            var identity = Path.GetFullPath(targetPath);
            if (isPlainFile && string.Equals(identity, item.TargetIdentity, StringComparison.Ordinal))
            {
                try
                {
                    File.Delete(targetPath);
                    // Resolved — drop the entry.
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    var pending = item with { State = (int)ExportCleanupState.CleanupPending };
                    kept.Add(pending);
                    reported.Add(ToEntry(pending));
                }
            }
            else
            {
                // Target Changed (plan.md:610-612) — the path exists but no longer matches what was
                // journaled (different type, or a different file entirely); never delete it.
                var pending = item with { State = (int)ExportCleanupState.CleanupPending };
                kept.Add(pending);
                reported.Add(ToEntry(pending));
            }
        }

        lock (_gate) Write(kept);
        return reported;
    }

    private async Task<byte[]> GetOrCreateSecretAsync(CancellationToken cancellationToken)
    {
        if (_secret is { } cached) return cached;
        await _secretGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_secret is { } alreadySet) return alreadySet;
            string? stored;
            try { stored = await _secretStore.GetSecretAsync().ConfigureAwait(false); }
            catch { stored = null; }
            if (stored is not null)
            {
                try
                {
                    _secret = Convert.FromBase64String(stored);
                    return _secret;
                }
                catch (FormatException) { /* fall through and regenerate below */ }
            }

            var generated = RandomNumberGenerator.GetBytes(32);
            await _secretStore.SetSecretAsync(Convert.ToBase64String(generated)).ConfigureAwait(false);
            _secret = generated;
            return _secret;
        }
        finally
        {
            _secretGate.Release();
        }
    }

    private static string ComputeHmac(byte[] secret, PersistedEntry entry)
    {
        var payload = $"{entry.OperationId}|{entry.ObjectType}|{entry.ParentPath}|{entry.OpaqueName}|{entry.TargetIdentity}|{entry.CreatedAt:O}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(payload)));
    }

    private static bool VerifyHmac(byte[] secret, PersistedEntry entry)
    {
        var expected = ComputeHmac(secret, entry);
        byte[] expectedBytes, actualBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expected);
            actualBytes = Convert.FromHexString(entry.Hmac);
        }
        catch (FormatException)
        {
            return false;
        }

        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static ExportCleanupEntry ToEntry(PersistedEntry item) =>
        new(item.OperationId, (ExportCleanupObjectType)item.ObjectType, item.ParentPath, item.OpaqueName, item.TargetIdentity, (ExportCleanupState)item.State, item.CreatedAt);

    private List<PersistedEntry> Read()
    {
        var json = _storage.GetJournalJson();
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<JournalDocument>(json, JsonOptions)?.Entries ?? []; }
        catch (JsonException) { return []; }
    }

    private void Write(List<PersistedEntry> items) =>
        _storage.SetJournalJson(JsonSerializer.Serialize(new JournalDocument(JournalSchemaVersion, items), JsonOptions));

    private sealed record JournalDocument(int Version, List<PersistedEntry> Entries);

    private sealed record PersistedEntry(string OperationId, int ObjectType, string ParentPath, string OpaqueName, string TargetIdentity, int State, DateTimeOffset CreatedAt, string Hmac);

    public void Dispose() => _secretGate.Dispose();
}
