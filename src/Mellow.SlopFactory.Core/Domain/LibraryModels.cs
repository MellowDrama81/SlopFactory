namespace Mellow.SlopFactory.Domain;

public enum LibraryRecordState
{
    Active = 0,
    Recycled = 1,
    PendingPermanentDeletion = 2,
    Missing = 3,
    ContentChanged = 4,
    ContentReplaced = 5
}

public enum MetadataValueKind
{
    Text = 0,
    Number = 1,
    Boolean = 2,
    Date = 3,
    DateTime = 4,
    Json = 5
}

public enum FileOrigin
{
    Imported = 0,
    Generated = 1,
    UserCopy = 2,
    EditedCopy = 3,
    RecoveredProviderOutput = 4
}

public enum TextCopyFormat
{
    PreserveSourceFormat = 0,
    PlainText = 1,
    Markdown = 2
}

public enum RecycleBinItemKind
{
    Folder = 0,
    File = 1,
    FileLink = 2
}

public sealed record LibraryManifest(
    string FormatIdentity,
    int ManifestVersion,
    string LibraryId,
    string DisplayName,
    int SchemaVersion);

public sealed record LibraryDescriptor(
    string LibraryId,
    string DisplayName,
    string RootPath,
    string RootFolderId,
    string GeneratedFolderId,
    int SchemaVersion);

public sealed record FolderRecord(
    string Id,
    string? ParentId,
    string Name,
    LibraryRecordState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    DateTimeOffset? RecycledAt);

public sealed record FileRecord(
    string Id,
    string FolderId,
    string DisplayName,
    string ManagedName,
    string ContentHash,
    long ByteSize,
    string MediaType,
    FileOrigin Origin,
    LibraryRecordState State,
    DateTimeOffset ImportedAt,
    DateTimeOffset ModifiedAt,
    DateTimeOffset? SourceLastModified,
    DateTimeOffset? RecycledAt);

public sealed record MetadataEntry(
    string Id,
    string FileId,
    string Key,
    MetadataValueKind Kind,
    string SerializedValue,
    bool IsSensitive);

public sealed record FileLink(
    string Id,
    string SourceFileId,
    string TargetFileId,
    string Label,
    LibraryRecordState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RecycledAt,
    bool ExplicitlyRecycled);

public sealed record RecycleBinItemReference(
    RecycleBinItemKind Kind,
    string Id);

public sealed record RecycleBinEntry(
    RecycleBinItemReference Reference,
    string Name,
    string OriginalLocation,
    LibraryRecordState State,
    DateTimeOffset RecycledAt,
    int OwnedFolderCount,
    int OwnedFileCount,
    int OwnedLinkCount);

public sealed record RecycleBinOperationItemResult(
    RecycleBinItemReference Reference,
    string Name,
    bool Succeeded,
    string? Error);

public sealed record RecycleBinOperationResult(
    IReadOnlyList<RecycleBinOperationItemResult> Items)
{
    public int SucceededCount => Items.Count(item => item.Succeeded);
    public int FailedCount => Items.Count - SucceededCount;
}

public sealed record LibraryFolderContents(
    FolderRecord Folder,
    IReadOnlyList<FolderRecord> Folders,
    IReadOnlyList<FileRecord> Files);

public sealed record ImportCandidate(
    string SourcePath,
    string DisplayName,
    long ByteSize,
    DateTimeOffset? SourceLastModified);

public sealed record ImportResult(
    ImportCandidate Candidate,
    FileRecord? File,
    ImportOutcome Outcome,
    IReadOnlyList<FileRecord> Matches,
    string? Error);

public sealed record TextFileContent(
    string Content,
    bool IsTruncated,
    string EncodingName);

public sealed record ImageFileContent(
    string MediaType,
    byte[] Bytes);

public enum ImportOutcome
{
    Imported = 0,
    DuplicateSkipped = 1,
    Failed = 2,
    Cancelled = 3
}
