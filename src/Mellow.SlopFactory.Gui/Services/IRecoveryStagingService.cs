using Mellow.SlopFactory.Domain;

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
    /// new entry for them. Returns the new entry's ID. <paramref name="generationRecordId"/>/
    /// <paramref name="position"/> link the entry back to the durable generation record it belongs
    /// to (plan.md:329's "generation identifier"), enabling automatic reconciliation once the
    /// intended library returns.</summary>
    Task<string> StageAsync(string libraryId, string libraryDisplayName, string draftId, byte[] bytes, string safeFileName, string mediaType, string? generationRecordId = null, int? position = null, CancellationToken cancellationToken = default);

    /// <summary>Streams a provider result into recovery storage without first materializing it in
    /// memory. The stream is bounded by the shared provider-result limit.</summary>
    Task<string> StageFromStreamAsync(string libraryId, string libraryDisplayName, string draftId, Stream source, string safeFileName, string mediaType, long? declaredLength = null, string? generationRecordId = null, int? position = null, CancellationToken cancellationToken = default);

    /// <summary>Reads a staged result's bytes back — used by **Export Copy** (plan.md:331) and by the
    /// sandboxed preview (plan.md:330). Throws if the entry is unknown.</summary>
    Task<byte[]> ReadBytesAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Opens the staged result for streaming consumers such as reconciliation or export.</summary>
    Task<Stream> OpenReadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Removes both the registry entry and its staged bytes. plan.md:332-333 — exporting a
    /// copy never calls this; only an explicit discard (or a future successful reconcile) does.</summary>
    Task DiscardAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class RecoveryStagingService(IPendingResultRegistryService registry, IRecoveryStagingPathProvider paths) : IRecoveryStagingService
{
    public IReadOnlyList<StagedResultEntry> GetAll() => registry.GetAll();

    public async Task<string> StageAsync(string libraryId, string libraryDisplayName, string draftId, byte[] bytes, string safeFileName, string mediaType, string? generationRecordId = null, int? position = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        await using var source = new MemoryStream(bytes, writable: false);
        return await StageFromStreamAsync(libraryId, libraryDisplayName, draftId, source, safeFileName, mediaType, bytes.LongLength, generationRecordId, position, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StageFromStreamAsync(string libraryId, string libraryDisplayName, string draftId, Stream source, string safeFileName, string mediaType, long? declaredLength = null, string? generationRecordId = null, int? position = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (declaredLength is > LibraryRules.MaximumProviderResultBytes)
        {
            throw new LibraryValidationException($"The provider result exceeds the {LibraryRules.MaximumProviderResultBytes / 1_048_576:N0} MiB download limit.");
        }

        Directory.CreateDirectory(paths.StagingDirectory);
        var id = Guid.NewGuid().ToString("N");
        var path = StagedFilePath(id);
        long length = 0;
        try
        {
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var buffer = new byte[81_920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                if (length > LibraryRules.MaximumProviderResultBytes) throw new LibraryValidationException($"The provider result exceeds the {LibraryRules.MaximumProviderResultBytes / 1_048_576:N0} MiB download limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            registry.Add(new StagedResultEntry(id, libraryId, libraryDisplayName, draftId, safeFileName, mediaType, length, DateTimeOffset.UtcNow, generationRecordId, position));
            return id;
        }
        catch
        {
            try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public Task<byte[]> ReadBytesAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!registry.GetAll().Any(entry => entry.Id == id)) throw new InvalidOperationException("The staged result is no longer tracked.");
        return File.ReadAllBytesAsync(StagedFilePath(id), cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!registry.GetAll().Any(entry => entry.Id == id)) throw new InvalidOperationException("The staged result is no longer tracked.");
        Stream stream = new FileStream(StagedFilePath(id), FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DiscardAsync(string id, CancellationToken cancellationToken = default)
    {
        registry.Remove(id);
        try { File.Delete(StagedFilePath(id)); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private string StagedFilePath(string id) => Path.Combine(paths.StagingDirectory, $"{id}.bin");
}
