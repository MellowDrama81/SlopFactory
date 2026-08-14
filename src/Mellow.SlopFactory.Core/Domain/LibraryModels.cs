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

public enum FileContentState
{
    Healthy = 0,
    Missing = 1,
    Changed = 2,
    Replaced = 3
}

public enum BuiltInPreviewKind
{
    Unsupported = 0,
    Text = 1,
    Image = 2,
    Media = 3
}

public static class BuiltInPreviewCapabilities
{
    public static BuiltInPreviewKind ForMediaType(string mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return BuiltInPreviewKind.Unsupported;
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType is "application/json" or "application/xml") return BuiltInPreviewKind.Text;
        if (mediaType is "image/png" or "image/jpeg" or "image/webp" or "image/gif" or "image/svg+xml") return BuiltInPreviewKind.Image;
        if (mediaType is "audio/mpeg" or "audio/wav" or "audio/aac" or "audio/mp4" or "audio/flac" or "audio/ogg" or "video/mp4") return BuiltInPreviewKind.Media;
        return BuiltInPreviewKind.Unsupported;
    }
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

public enum LibraryBrowseScope
{
    CurrentFolder = 0,
    EntireLibrary = 1
}

public enum LibraryFileSort
{
    Name = 0,
    ImportedNewest = 1,
    ModifiedNewest = 2,
    SizeLargest = 3,
    MediaType = 4
}

public enum LibraryMediaKind
{
    Any = 0,
    Text = 1,
    Image = 2,
    Audio = 3,
    Video = 4,
    Other = 5
}

public enum MetadataFilterOperator
{
    Equals = 0,
    DoesNotEqual = 1,
    Contains = 2,
    LessThan = 3,
    LessThanOrEqual = 4,
    GreaterThan = 5,
    GreaterThanOrEqual = 6,
    Exists = 7,
    DoesNotExist = 8,
    StructurallyEquals = 9
}

public enum RecycleBinItemKind
{
    Folder = 0,
    File = 1,
    FileLink = 2,
    Connection = 3,
    Model = 4,
    SavedSetting = 5,
    GenerationRecord = 6
}

public enum LibraryIntegrityIssueKind
{
    ManifestInvalid = 0,
    DatabaseInvalid = 1,
    RequiredDirectoryMissing = 2,
    ManagedFileMissing = 3,
    ManagedFileSizeMismatch = 4,
    ManagedFileHashMismatch = 5,
    UnsafeManagedEntry = 6,
    OrphanManagedFile = 7,
    ManagedFileInaccessible = 8
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
    string OriginalFileName,
    string ManagedName,
    string ContentHash,
    long ByteSize,
    string MediaType,
    FileOrigin Origin,
    LibraryRecordState State,
    DateTimeOffset ImportedAt,
    DateTimeOffset ModifiedAt,
    DateTimeOffset? SourceLastModified,
    DateTimeOffset? RecycledAt,
    FileContentState ContentState = FileContentState.Healthy);

public sealed record FileContentHealth(
    FileRecord File,
    string? ObservedContentHash,
    long? ObservedByteSize,
    string? ObservedMediaType);

public sealed record ManagedContentReplacementReview(
    FileRecord File,
    string OriginalContentHash,
    long OriginalByteSize,
    string OriginalMediaType,
    string CandidateContentHash,
    long CandidateByteSize,
    string CandidateMediaType,
    bool UsesCurrentManagedBytes,
    int OrdinaryMetadataCount,
    int SensitiveMetadataCount)
{
    public bool RestoresOriginal => CandidateByteSize == OriginalByteSize && string.Equals(CandidateContentHash, OriginalContentHash, StringComparison.Ordinal);
}

public sealed record FileContentProvenance(
    string OriginalContentHash,
    long OriginalByteSize,
    string OriginalMediaType,
    DateTimeOffset? ReplacedAt);

public sealed record FileDerivationProvenance(
    string? SourceFileId,
    FileOrigin Origin,
    FileIdentitySnapshot? DeletedSource = null);

public sealed record FileIdentitySnapshot(string DisplayName, string MediaType, string ContentHash);

public sealed record FileDerivationChainEntry(
    FileRecord File,
    FileOrigin? DerivedBy);

public sealed record ChangedContentInspection(
    FileRecord File,
    string ActualContentHash,
    long ActualByteSize,
    string ActualMediaType);

public sealed record LibraryFileBrowseQuery(
    string FolderId,
    LibraryBrowseScope Scope,
    string SearchText,
    LibraryMediaKind MediaKind,
    FileOrigin? Origin,
    DateTimeOffset? ImportedFromInclusive,
    DateTimeOffset? ImportedBeforeExclusive,
    LibraryFileSort Sort,
    int Offset = 0,
    int PageSize = 48,
    UserMetadataFilter? MetadataFilter = null);

public sealed record UserMetadataFilter(
    string Key,
    MetadataValueKind Kind,
    MetadataFilterOperator Operator,
    string? ComparisonValue);

public sealed record LibraryFileBrowseItem(
    FileRecord File,
    IReadOnlyList<string> MatchReasons);

public sealed record LibraryFileBrowseResult(
    IReadOnlyList<LibraryFileBrowseItem> Items,
    int TotalCount,
    int Offset,
    int PageSize,
    int MetadataMissingCount = 0,
    int MetadataIncompatibleTypeCount = 0)
{
    public bool HasPreviousPage => Offset > 0;
    public bool HasNextPage => Offset + Items.Count < TotalCount;
}

public sealed record MetadataEntry(
    string Id,
    string FileId,
    string Key,
    MetadataValueKind Kind,
    string SerializedValue,
    bool IsSensitive);

public sealed record BulkFileOperationItemResult(
    string FileId,
    string DisplayName,
    bool Succeeded,
    string? Error);

public sealed record BulkFileOperationResult(
    IReadOnlyList<BulkFileOperationItemResult> Items)
{
    public int SucceededCount => Items.Count(item => item.Succeeded);
    public int FailedCount => Items.Count - SucceededCount;
}

public sealed record BulkDuplicateProgress(int CurrentItem, int TotalItems, string DisplayName, bool Completed);

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
    int OwnedLinkCount,
    PermanentDeletionFailure? DeletionFailure,
    int OwnedModelCount = 0,
    int OwnedSavedSettingCount = 0);

public sealed record PermanentDeletionFailure(
    string SanitizedError,
    DateTimeOffset FailedAt);

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

public sealed record RecycleBinRestorePreviewItem(
    RecycleBinEntry Entry,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Effects)
{
    public bool CanRestore => BlockingReasons.Count == 0;
}

public sealed record RecycleBinRestorePreview(
    IReadOnlyList<RecycleBinRestorePreviewItem> Items)
{
    public int RestorableCount => Items.Count(item => item.CanRestore);
    public int BlockedCount => Items.Count - RestorableCount;
}

public sealed record LibraryIntegrityFinding(
    LibraryIntegrityIssueKind Kind,
    string? RecordId,
    long? ExpectedByteSize,
    long? ActualByteSize,
    string Summary);

public sealed record LibraryIntegrityScanProgress(
    int ProcessedItems,
    int TotalItems,
    string Stage);

public sealed record LibraryIntegrityReport(
    string LibraryId,
    int SchemaVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool IsComplete,
    bool WasCancelled,
    IReadOnlyList<LibraryIntegrityFinding> Findings);

public sealed record LibraryFolderContents(
    FolderRecord Folder,
    IReadOnlyList<FolderRecord> Folders,
    IReadOnlyList<FileRecord> Files);

public sealed record ImportCandidate(
    string SourcePath,
    string DisplayName,
    long ByteSize,
    DateTimeOffset? SourceLastModified,
    SourceZoneClassification SourceZone = SourceZoneClassification.Unknown);

public enum SourceZoneClassification
{
    Unknown = 0,
    LocalMachine = 1,
    Intranet = 2,
    Trusted = 3,
    Internet = 4,
    Restricted = 5
}

public sealed record ImportResult(
    ImportCandidate Candidate,
    FileRecord? File,
    ImportOutcome Outcome,
    IReadOnlyList<FileRecord> Matches,
    string? Error);

public sealed record ImportProgress(
    int ItemIndex,
    int TotalItems,
    string DisplayName,
    string Stage,
    long BytesProcessed,
    long TotalBytes);

public sealed record TextFileContent(
    string Content,
    bool IsTruncated,
    string EncodingName);

public sealed record TextSearchMatch(
    long CharacterOffset,
    string Snippet,
    int MatchStart,
    int MatchLength);

public sealed record TextSearchResult(
    long TotalMatches,
    IReadOnlyList<TextSearchMatch> Matches)
{
    public bool ResultsTruncated => TotalMatches > Matches.Count;
}

public sealed record MarkdownExternalLink(
    string Label,
    string Destination);

public sealed record RenderedMarkdownContent(
    string Html,
    IReadOnlyList<MarkdownExternalLink> ExternalLinks);

public sealed record ImageFileContent(
    string MediaType,
    byte[] Bytes);

public sealed record ImageTechnicalProperties(
    int? Width,
    int? Height,
    int? Orientation = null);

public sealed record MediaTechnicalProperties(
    TimeSpan? Duration,
    string? Container,
    string? AudioCodec,
    string? VideoCodec,
    int? ChannelCount,
    int? SampleRate,
    double? FrameRate,
    int? Width,
    int? Height,
    bool IsAvailable,
    string? UnavailableReason = null);

public sealed record SystemMetadataProperty(string Key, string DisplayName, string? Value);

public sealed record FileSystemMetadata(
    string FileId,
    IReadOnlyList<SystemMetadataProperty> Properties);

public enum ImportInventorySkipReason
{
    Hidden = 0,
    ProtectedOrSystem = 1,
    RedirectedOrReparse = 2,
    NotARegularFile = 3,
    Inaccessible = 4,
    LimitExceeded = 5
}

public sealed record ImportSourceSnapshot(
    string SourcePath,
    string DisplayName,
    string RelativeFolder,
    long ByteSize,
    DateTimeOffset LastWriteTime,
    string? ContentHash = null,
    SourceZoneClassification SourceZone = SourceZoneClassification.Unknown);

public sealed record ImportDuplicateGroup(
    long ByteSize,
    string ContentHash,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<FileRecord> LibraryMatches);

public sealed record RecursiveImportInventory(
    string InventoryId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ImportSourceSnapshot> Candidates,
    IReadOnlyList<string> VirtualFolders,
    IReadOnlyList<ImportDuplicateGroup> DuplicateGroups,
    IReadOnlyDictionary<ImportInventorySkipReason, int> SkippedCounts,
    IReadOnlyList<string> NameConflicts,
    string? LibraryId = null)
{
    public int EligibleCount => Candidates.Count;
    public long KnownBytes => Candidates.Sum(candidate => candidate.ByteSize);
}

public enum ImportDuplicateChoice
{
    Skip = 0,
    ImportAnyway = 1,
    RestoreExisting = 2
}

public sealed record ConfirmedImportCandidate(
    ImportSourceSnapshot Snapshot,
    ImportDuplicateChoice DuplicateChoice = ImportDuplicateChoice.Skip,
    string? ExistingFileId = null);

public enum ExportCollisionChoice
{
    Fail = 0,
    Replace = 1
}

public enum FileExportOutcome
{
    Exported = 0,
    Failed = 1,
    Cancelled = 2
}

public sealed record FileExportResult(
    string FileId,
    string DestinationPath,
    FileExportOutcome Outcome,
    long BytesWritten,
    string? ContentHash,
    string? Error);

public sealed record BulkExportPreflightItem(
    string FileId,
    string DisplayName,
    string SafeFileName,
    string DestinationPath,
    bool DestinationExists,
    bool HasSelectionCollision,
    string? BlockingReason);

public sealed record BulkExportPreflight(
    string PreviewId,
    string DestinationDirectory,
    IReadOnlyList<BulkExportPreflightItem> Items,
    string? LibraryId = null);

public sealed record BulkExportResult(IReadOnlyList<FileExportResult> Items)
{
    public int ExportedCount => Items.Count(item => item.Outcome == FileExportOutcome.Exported);
    public int FailedCount => Items.Count(item => item.Outcome == FileExportOutcome.Failed);
    public int CancelledCount => Items.Count(item => item.Outcome == FileExportOutcome.Cancelled);
}

public sealed record ExternalOpenCopy(
    string FileId,
    string Path,
    string MediaType,
    bool IsReadOnly);

public enum ExternalOpenSafety
{
    Allowed = 0,
    RequiresWarning = 1,
    BlockedActiveContent = 2,
    BlockedUnavailableContent = 3
}

public sealed record MetadataNormalizationItem(
    string FileId,
    string MetadataId,
    string Key,
    MetadataValueKind SourceKind,
    MetadataValueKind TargetKind,
    bool IsSensitive,
    bool IsConvertible,
    string? NormalizedValue,
    string? Error);

public sealed record MetadataNormalizationPreview(
    string PreviewId,
    IReadOnlyList<MetadataNormalizationItem> Items,
    string? LibraryId = null);

public sealed record MediaPlaybackDescriptor(
    string FileId,
    string MediaType,
    long ByteSize,
    string ContentHash);

public enum ImportOutcome
{
    Imported = 0,
    DuplicateSkipped = 1,
    Failed = 2,
    Cancelled = 3
}

public enum ProviderType
{
    OpenAi = 0,
    GenericOpenAiCompatible = 1,
    OneMinAi = 2,
    OpenRouter = 3,
    DeepInfra = 4
}

public enum GenerationMode
{
    Text = 0,
    Image = 1,
    Audio = 2,
    Video = 3
}

public enum ConnectionTestStatus
{
    Untested = 0,
    Success = 1,
    Failed = 2
}

public sealed record Connection(
    string Id,
    string Label,
    ProviderType ProviderType,
    string BaseUrl,
    string CredentialHeaderName,
    string AuthPrefix,
    bool HasCredential,
    ConnectionTestStatus LastTestStatus,
    DateTimeOffset? LastTestedAt,
    string? LastTestMessage,
    LibraryRecordState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    DateTimeOffset? RecycledAt,
    int? TimeoutSeconds = null,
    IReadOnlyList<ConnectionHeader>? AdditionalHeaders = null,
    GenericConnectionModalitySettings? GenericModalitySettings = null,
    string? CredentialRevisionId = null,
    bool CredentialRequiresRepair = false)
{
    public bool IsUnverified => LastTestStatus != ConnectionTestStatus.Success;
}

public sealed record ConnectionHeader(string Name, string Value);

public enum CredentialRevisionPurpose
{
    Candidate = 0,
    Active = 1
}

public sealed record CredentialLedgerRevision(string RevisionId, CredentialRevisionPurpose Purpose);

public sealed record CredentialLedgerConnectionSnapshot(
    string ConnectionId,
    bool HasCredential,
    string? CommittedRevisionId,
    IReadOnlyList<CredentialLedgerRevision> Revisions);

public sealed record CredentialPromotionResult(Connection Connection, IReadOnlyList<string> SupersededRevisionIds);

public sealed record GenericConnectionModalitySettings(
    bool ModelsEnabled,
    string? ModelsPathOverride,
    bool TextGenerationEnabled,
    string? TextGenerationPathOverride,
    bool ImageGenerationEnabled,
    string? ImageGenerationPathOverride)
{
    public static readonly GenericConnectionModalitySettings Default = new(true, null, true, null, true, null);
}

public enum TextResultFormat
{
    Markdown = 0,
    PlainText = 1
}

public sealed record Model(
    string Id,
    string ConnectionId,
    string Label,
    string ProviderModelId,
    GenerationMode Mode,
    bool SupportsSystemInstructions,
    LibraryRecordState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    DateTimeOffset? RecycledAt,
    bool NeedsReview = false,
    TextResultFormat TextFormat = TextResultFormat.Markdown);

public sealed record ProviderModelInfo(
    string ProviderModelId,
    string? DisplayLabel);

public sealed record ModelCatalogue(
    DateTimeOffset? RetrievedAt,
    bool PossiblyStale,
    IReadOnlyList<ProviderModelInfo> Entries);

public sealed record ConnectionTestResult(
    bool Success,
    string Message,
    string? FinalHost,
    bool SupportsModelDiscovery,
    IReadOnlyList<ProviderModelInfo>? DiscoveredModels = null);

public sealed record TextGenerationResult(
    IReadOnlyList<string> Texts,
    int? PromptTokens,
    int? CompletionTokens,
    int SafetyBlockedCount = 0);

public sealed record TextGenerationSourceImage(
    string MediaType,
    byte[] Bytes);

public enum GenerationStatus
{
    Completed = 0,
    Failed = 1,
    PartiallyCompleted = 2
}

public sealed record GenerationRecord(
    string Id,
    string? ModelId,
    string ModelLabel,
    string ProviderModelId,
    ProviderType ProviderType,
    GenerationMode Mode,
    string Prompt,
    string? SystemInstructions,
    int ResultCount,
    GenerationStatus Status,
    string? ErrorMessage,
    string DestinationFolderId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<string> ResultFileIds,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    string? SourceFileId = null,
    string? PromptImprovementRecordId = null,
    TextResultFormat? TextFormat = null,
    LibraryRecordState State = LibraryRecordState.Active,
    DateTimeOffset? RecycledAt = null,
    FileIdentitySnapshot? SourceFileTombstone = null,
    IReadOnlyList<FileIdentitySnapshot> TombstonedResults = default!,
    GenerationSettings Settings = default!,
    string? SecondarySourceFileId = null,
    FileIdentitySnapshot? SecondarySourceFileTombstone = null,
    string? TertiarySourceFileId = null,
    FileIdentitySnapshot? TertiarySourceFileTombstone = null,
    int SafetyBlockedCount = 0)
{
    public IReadOnlyList<FileIdentitySnapshot> TombstonedResults { get; init; } = TombstonedResults ?? [];
    public GenerationSettings Settings { get; init; } = Settings ?? GenerationSettings.Empty;
}

public sealed record PromptImprovementRecord(
    string Id,
    string? ModelId,
    string ModelLabel,
    string ProviderModelId,
    ProviderType ProviderType,
    string RawPrompt,
    string? Guidance,
    string TemplateVersion,
    GenerationStatus Status,
    string? ErrorMessage,
    IReadOnlyList<string> Candidates,
    int? PromptTokens,
    int? CompletionTokens,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record GenerationSettings(
    double? Temperature = null,
    double? TopP = null,
    int? MaxTokens = null,
    double? FrequencyPenalty = null,
    double? PresencePenalty = null)
{
    public static readonly GenerationSettings Empty = new();
}

/// <summary>
/// The lifecycle of a provider job tracked by the device-wide pending-job registry. This is
/// separate from a draft's local queue scheduling (queued/running before submission) and from a
/// terminal <see cref="GenerationStatus"/> (recorded only once a <see cref="GenerationRecord"/>
/// exists): it exists to let SlopFactory resume polling a submit-then-poll provider job across an
/// application restart, before any local outcome — success, failure or history record — exists yet.
/// </summary>
public enum AsyncRemoteJobPhase
{
    Submitted = 0,
    Processing = 1,
    MonitoringPaused = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

/// <summary>
/// A minimal device-local record of an in-flight asynchronous provider job, keyed by the draft that
/// submitted it rather than by generation-history ID, because no <see cref="GenerationRecord"/>
/// exists until the job reaches a terminal outcome. Never contains prompts, source content or
/// credentials, per the pending-job registry rules in plan.md.
/// </summary>
public sealed record AsyncRemoteJobRecord(
    string Id,
    string DraftId,
    ProviderType ProviderType,
    string ConnectionId,
    string ProviderJobId,
    AsyncRemoteJobPhase Phase,
    string? IdempotencyKey,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? LastPolledAt,
    DateTimeOffset? MonitoringDeadline);

public sealed record SavedGenerationSetting(
    string Id,
    string Title,
    string? ModelId,
    string ModelLabel,
    GenerationMode Mode,
    string Prompt,
    string? SystemInstructions,
    int ResultCount,
    string DestinationFolderId,
    LibraryRecordState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    DateTimeOffset? RecycledAt,
    string? SourceFileId = null,
    bool NeedsReview = false,
    int Revision = 1,
    GenerationSettings Settings = default!,
    string? SecondarySourceFileId = null,
    string? TertiarySourceFileId = null)
{
    public GenerationSettings Settings { get; init; } = Settings ?? GenerationSettings.Empty;
}

public sealed record GenerationDraft(
    string Id,
    string? CustomTitle,
    int TabOrder,
    string? ModelId,
    string Prompt,
    string? SystemInstructions,
    string? SourceFileId,
    int ResultCount,
    string DestinationFolderId,
    string? ImprovementModelId,
    string? ImprovementGuidance,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    GenerationSettings Settings = default!,
    string? SecondarySourceFileId = null,
    string? TertiarySourceFileId = null)
{
    public GenerationSettings Settings { get; init; } = Settings ?? GenerationSettings.Empty;
}
