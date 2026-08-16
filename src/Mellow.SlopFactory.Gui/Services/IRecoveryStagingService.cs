namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// Coordinates <see cref="IPendingResultRegistryService"/>'s device-wide metadata index with the
/// actual staged bytes on disk (plan.md:322-334). Used when a provider result finishes downloading
/// but its destination library's volume is unavailable, so the bytes are not simply discarded.
/// </summary>
public interface IRecoveryStagingService
{
    IReadOnlyList<StagedResultEntry> GetAll();

    /// <summary>Writes <paramref name="bytes"/> into the device-wide staging folder and registers a
    /// new entry for them. Returns the new entry's ID.</summary>
    Task<string> StageAsync(string libraryId, string libraryDisplayName, string draftId, byte[] bytes, string safeFileName, string mediaType, CancellationToken cancellationToken = default);

    /// <summary>Reads a staged result's bytes back — used by **Export Copy** (plan.md:331) and by the
    /// sandboxed preview (plan.md:330). Throws if the entry is unknown.</summary>
    Task<byte[]> ReadBytesAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Removes both the registry entry and its staged bytes. plan.md:332-333 — exporting a
    /// copy never calls this; only an explicit discard (or a future successful reconcile) does.</summary>
    Task DiscardAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class RecoveryStagingService(IPendingResultRegistryService registry, IRecoveryStagingPathProvider paths) : IRecoveryStagingService
{
    public IReadOnlyList<StagedResultEntry> GetAll() => registry.GetAll();

    public async Task<string> StageAsync(string libraryId, string libraryDisplayName, string draftId, byte[] bytes, string safeFileName, string mediaType, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.StagingDirectory);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(StagedFilePath(id), bytes, cancellationToken).ConfigureAwait(false);
        registry.Add(new StagedResultEntry(id, libraryId, libraryDisplayName, draftId, safeFileName, mediaType, bytes.LongLength, DateTimeOffset.UtcNow));
        return id;
    }

    public Task<byte[]> ReadBytesAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!registry.GetAll().Any(entry => entry.Id == id)) throw new InvalidOperationException("The staged result is no longer tracked.");
        return File.ReadAllBytesAsync(StagedFilePath(id), cancellationToken);
    }

    public Task DiscardAsync(string id, CancellationToken cancellationToken = default)
    {
        registry.Remove(id);
        try { File.Delete(StagedFilePath(id)); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private string StagedFilePath(string id) => Path.Combine(paths.StagingDirectory, $"{id}.bin");
}
