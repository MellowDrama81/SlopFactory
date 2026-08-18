namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// A single staged provider result — library ID, provider type, connection ID,
/// remote job ID and status, but never prompts or source content — extended with the minimum extra
/// fields the recovery-staging list requires to show (safe filename, media type, size,
/// generation identifier, validation status). Deliberately carries no prompt, model settings or
/// source-file content — only enough to let the user identify, preview, export or discard the file,
/// and to link it back to its owning library and draft once that library is available again.
/// </summary>
/// <param name="GenerationRecordId">The durable generation record this staged result belongs to —
/// its generation identifier. Null for an entry staged before this field existed; such
/// an entry can still be previewed/exported/discarded manually but is not eligible for automatic
/// reconciliation, which needs the record to commit into.</param>
/// <param name="Position">The result position within <paramref name="GenerationRecordId"/> this
/// staged file belongs to.</param>
public sealed record StagedResultEntry(
    string Id,
    string LibraryId,
    string LibraryDisplayName,
    string DraftId,
    string SafeFileName,
    string MediaType,
    long ByteSize,
    DateTimeOffset CreatedAt,
    string? GenerationRecordId = null,
    int? Position = null);

/// <summary>Device-wide index of staged results — deliberately a thin registry of metadata only; the
/// actual bytes live in <see cref="IRecoveryStagingPathProvider.StagingDirectory"/>, addressed by
/// <see cref="StagedResultEntry.Id"/>. Mirrors <see cref="IRecentLibraryService"/>'s
/// Preferences-backed JSON-list pattern, the closest existing precedent for a small device-wide
/// record list.</summary>
public interface IPendingResultRegistryService
{
    IReadOnlyList<StagedResultEntry> GetAll();
    void Add(StagedResultEntry entry);
    void Remove(string id);
}
