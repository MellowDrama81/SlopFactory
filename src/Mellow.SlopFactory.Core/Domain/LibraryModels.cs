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
    RecoveredProviderOutput = 4,
    /// <summary>A generation result whose bytes did not match the expected media category, whose
    /// content was not recognized as a rejection payload (error document/authentication page), and
    /// which the user explicitly chose to retain via <c>Retain as Unverified Binary</c> rather than
    /// discard. Export-only: never previewable, never opened externally, never usable as a
    /// generation source (see <c>ContentActionPolicy</c>).</summary>
    UnverifiedProviderOutput = 5
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

/// <summary>A private PNG editing mask owned by one library image. Masks deliberately are not
/// <see cref="FileRecord"/>s: they never appear in folders, search results, or exports.</summary>
public sealed record ImageMask(
    string Id,
    string OwnerFileId,
    string Label,
    string BaseContentHash,
    int Width,
    int Height,
    string ContentHash,
    DateTimeOffset CreatedAt);

/// <summary>
/// Named source-input slot roles. Only <see cref="ReferenceImage"/> (text generation, any provider;
/// image generation, OpenAI/OpenRouter/DeepInfra only) and <see cref="FirstFrame"/> (DeepInfra video only)
/// are ever assignable to a model today — see <see cref="LibraryRules.GetInputSlotCapabilities"/>. The
/// remaining values exist so a future confirmed provider capability is additive (one more capability
/// entry and adapter wiring), not a schema rework; no adapter documents them today.
/// </summary>
public enum GenerationInputSlotRole
{
    ReferenceImage = 0,
    Mask = 1,
    FirstFrame = 2,
    LastFrame = 3,
    SourceAudio = 4,
    SourceVideo = 5
}

/// <summary>One source-file assignment within a draft, saved setting or generation record.
/// <paramref name="Order"/> is the position within its role (0-based) for roles that allow more than
/// one file (e.g. multiple reference images).</summary>
/// <param name="FileId">The live library file this slot names. Null exactly when
/// <paramref name="SnapshotSourceGenerationId"/> is set — a slot always has exactly one of the two,
/// never both and never neither.</param>
/// <param name="AttachmentId">A private, owner-bound input attachment (currently an
/// <see cref="ImageMask"/> ID).  It is intentionally distinct from <paramref name="FileId"/>
/// so generation history never mistakes editing data for a browsable library file.</param>
/// <param name="SnapshotSourceGenerationId">Set only when this slot's bytes/identity should be
/// cloned forward from another <see cref="GenerationRecord"/>'s own already-captured snapshot
/// (<see cref="GenerationInputSlotRole.ReferenceImage"/>/<see cref="GenerationInputSlotRole.FirstFrame"/>
/// via <c>generation_input_snapshots</c>, <see cref="GenerationInputSlotRole.Mask"/> via that record's
/// own <c>attachment_snapshot_bytes</c>) rather than resolved from a live <see cref="FileRecord"/> or
/// <see cref="ImageMask"/> — the case where <b>Use Again</b> is replaying a historical generation whose
/// original source file (or, for a mask, owning image) has since been permanently deleted. Deliberately
/// never <paramref name="FileId"/> itself, which has a real foreign key and must never be overloaded
/// with a deleted or synthetic identifier. A generation record created from a slot like this captures
/// its own independent copy of the referenced bytes at creation time — it never depends on the source
/// generation surviving afterward.</param>
public sealed record GenerationSourceSlot(GenerationInputSlotRole Role, string? FileId, int Order, string? AttachmentId = null, string? SnapshotSourceGenerationId = null);

/// <summary>An immutable identity snapshot of a source slot as it was at generation-submission time,
/// captured once on <see cref="GenerationRecord"/> and never rewritten. Identity only (display
/// name/media type/hash via <see cref="FileIdentitySnapshot"/>) — not a byte-level copy; the source
/// file remains a library <see cref="FileRecord"/> referenced by <paramref name="FileId"/>, which
/// becomes null if that file is later permanently deleted (the snapshot itself survives).</summary>
/// <param name="AttachmentId">The private mask ID this slot named, when <see cref="Role"/> is
/// <see cref="GenerationInputSlotRole.Mask"/> — carried here (not just on the live
/// <see cref="GenerationSourceSlot"/>) so a historical mask can still be identified, and its retained
/// bytes located, even after both the mask row and its owning image have been permanently deleted.</param>
public sealed record GenerationSourceSlotSnapshot(GenerationInputSlotRole Role, int Order, string? FileId, FileIdentitySnapshot Identity, string? AttachmentId = null);

/// <summary>Describes how many files of a given role a model accepts. See
/// <see cref="LibraryRules.GetInputSlotCapabilities"/> for the (currently very small) set of
/// confirmed provider capabilities this can return.</summary>
public sealed record GenerationInputSlotCapability(GenerationInputSlotRole Role, int MinCount, int MaxCount, bool Required);

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
    UserMetadataFilter? MetadataFilter = null,
    IReadOnlyList<string>? TagIds = null);

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

/// <param name="IsInheritable">When true, generating a file using a source file carrying this tag
/// automatically applies the tag to every resulting generated file too.</param>
public sealed record Tag(string Id, string Name, bool IsInheritable = false);

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
    Cancelled = 2,
    /// <summary>A destination read-back mismatch after the outgoing stream already matched the
    /// source — distinct from <see cref="Failed"/> (an outgoing-stream mismatch
    /// caught before anything was committed) because the destination file may already have replaced
    /// something, so recovery guidance differs.</summary>
    VerificationFailed = 3
}

public sealed record FileExportResult(
    string FileId,
    string DestinationPath,
    FileExportOutcome Outcome,
    long BytesWritten,
    string? ContentHash,
    string? Error);

/// <summary>The kind of external object a <see cref="ExportCleanupEntry"/> tracks. Only
/// <see cref="LocalTempFile"/> is ever actually cleaned up by <c>IExportCleanupJournal.SweepAsync</c>
/// today — <see cref="AndroidDocumentUri"/> entries are authenticated and reported but never deleted
/// automatically, since a real SAF permission-loss/reauthorization cycle needs on-device
/// verification this app does not attempt yet.</summary>
public enum ExportCleanupObjectType
{
    LocalTempFile = 0,
    AndroidDocumentUri = 1
}

/// <summary>Lifecycle of one journal entry, mirroring the cleanup-journal states: a
/// <see cref="PlannedTemporary"/> entry is written before the temp object is created (so a crash in
/// that narrow interval is still visible), <see cref="Confirmed"/> once the object durably exists,
/// and <see cref="CleanupPending"/> for an entry a sweep found but could not safely act on (identity
/// mismatch, or an object type this app doesn't clean up automatically).</summary>
public enum ExportCleanupState
{
    PlannedTemporary = 0,
    Confirmed = 1,
    CleanupPending = 2
}

/// <summary>One export cleanup journal entry, tracking a not-yet-committed (or not-yet-cleaned-up)
/// external temporary object. Persisted with an HMAC (see <c>IExportCleanupJournal</c>) so a
/// tampered or foreign entry is never trusted for deletion — <c>SweepAsync</c> silently drops any
/// entry whose HMAC does not verify.</summary>
public sealed record ExportCleanupEntry(
    string OperationId,
    ExportCleanupObjectType ObjectType,
    string ParentPath,
    string OpaqueName,
    string TargetIdentity,
    ExportCleanupState State,
    DateTimeOffset CreatedAt);

public sealed record SidecarExportResult(string? SidecarPath, FileExportOutcome Outcome, string? Error);

/// <summary>Disclosure opt-ins for a `.slopfactory.json` sidecar. Every toggle defaults to minimal
/// disclosure (false) — every new export operation begins with privacy-minimal sidecar
/// defaults, never preselected. <see cref="IncludeSafetyMetadata"/> is accepted but is always a
/// documented no-op until a persisted, hash-bound safety classification exists (checklist Section
/// 10, not yet implemented) for the sidecar writer to read.</summary>
public sealed record ExportSidecarOptions(
    bool WriteSidecar = false,
    bool IncludePrompt = false,
    bool IncludeSensitiveMetadata = false,
    bool IncludeFilenames = false,
    bool IncludeInternalIdentifiers = false,
    bool IncludeUsageAndCost = false,
    bool IncludeAdvancedSettings = false,
    bool IncludeSafetyMetadata = false)
{
    public static readonly ExportSidecarOptions Default = new();
}

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

/// <summary><see cref="SidecarItems"/> is index-aligned with <see cref="Items"/> (position N's sidecar
/// outcome corresponds to position N's media outcome) — null at a position where no sidecar was ever
/// attempted (no sidecar options supplied, that item's media export didn't succeed, the item was
/// blocked, or the batch was cancelled before reaching it), never simply omitted, so a caller can
/// always zip the two lists together by position. Defaults to a same-length all-null list when
/// omitted, so existing callers that never pass <c>sidecarOptions</c> to
/// <see cref="Application.ILibraryWorkspace.ExportFilesAsync"/> keep working unchanged.</summary>
public sealed record BulkExportResult(IReadOnlyList<FileExportResult> Items, IReadOnlyList<SidecarExportResult?>? SidecarItems = null)
{
    public IReadOnlyList<SidecarExportResult?> SidecarItems { get; init; } = SidecarItems ?? Items.Select(_ => (SidecarExportResult?)null).ToArray();
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
    BlockedUnavailableContent = 3,
    /// <summary>An unverified provider-output binary (<see cref="FileOrigin.UnverifiedProviderOutput"/>)
    /// — export-only, never opened externally regardless of its detected media type.</summary>
    BlockedUnverifiedContent = 4
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
    DeepInfra = 4,
    ComfyUi = 5,
    /// <summary>`api.mistral.ai` — OpenAI-compatible chat/completions. Image (agent-tool based, not a
    /// plain endpoint) and audio (Voxtral, unconfirmed public API shape) are not implemented — see
    /// providers.md.</summary>
    Mistral = 6,
    /// <summary>`api.groq.com/openai/v1` — OpenAI-compatible chat/completions, no hosted image models.
    /// Audio (text-to-speech via PlayAI models on the documented `POST /audio/speech` endpoint) is
    /// implemented; Whisper (speech-to-text, input direction) has no surface in this app.</summary>
    Groq = 7,
    /// <summary>`api.together.xyz/v1` — OpenAI-compatible chat/completions and a plain
    /// images/generations-shaped endpoint. Audio/video are not implemented — shapes unconfirmed.</summary>
    TogetherAi = 8,
    /// <summary>`api.fireworks.ai/inference/v1` — OpenAI-compatible chat/completions and a plain
    /// images/generations-shaped endpoint. Audio (limited STT)/video are not implemented.</summary>
    FireworksAi = 9,
    /// <summary>`api.deepseek.com` — OpenAI-compatible chat/completions. Image generation ships under
    /// a separate Janus-Pro model family (not the chat API) and is not implemented; audio/video are
    /// not confirmed GA public API surfaces.</summary>
    DeepSeek = 10,
    /// <summary>`api.perplexity.ai` — OpenAI-compatible chat/completions (Sonar model tiers). No image,
    /// audio or video generation exists; grounded web search/citations has no home in this app's
    /// adapter surface today.</summary>
    Perplexity = 11,
    /// <summary>`api.x.ai/v1` — OpenAI-compatible chat/completions plus a separate
    /// `images/generations`-shaped endpoint for Grok Imagine (text-to-image only, no reference-image
    /// editing). Audio (bundled only into video, not standalone) and video are not implemented — their
    /// endpoint shapes were not verified.</summary>
    XAi = 12,
    /// <summary>`api.anthropic.com/v1` — bespoke Messages API (`POST /v1/messages`), not OpenAI-shaped:
    /// `x-api-key` auth (plus a required `anthropic-version` header), a top-level `system` field
    /// (maps to <see cref="Model.SupportsSystemInstructions"/> cleanly), and no `n`/candidate-count
    /// parameter (one request per requested result). No native image/audio/video generation exists.
    /// Text-mode reference-image (vision) input is not implemented in this pass despite Anthropic
    /// supporting it — see providers.md.</summary>
    Anthropic = 13,
    /// <summary>`generativelanguage.googleapis.com/v1beta` — bespoke `generateContent` API: the model
    /// ID is embedded in the URL path (`models/{id}:generateContent`), not the body; request shape is
    /// `contents`/`systemInstruction`/`generationConfig` (which does support a `candidateCount` for
    /// multiple results in one call), auth via `x-goog-api-key`. Text, Image (Imagen, via a separate
    /// `models/{id}:predict` endpoint, text-to-image only) and Audio (text-to-speech, reusing
    /// `generateContent` itself with `responseModalities:["AUDIO"]`) are implemented — Veo (video) is a
    /// genuinely separate, asynchronous API family not covered here. Text-mode reference-image input is
    /// not implemented in this pass despite Gemini supporting it.</summary>
    Gemini = 14,
    /// <summary>`api.cohere.com/v1` — bespoke Chat API (`POST /v1/chat`): `message`/`chat_history`
    /// request shape (not `messages`) with a `preamble` field for system instructions, no
    /// candidate-count parameter (one request per requested result). No image generation; audio is
    /// input-only (Transcribe/STT, no TTS) and not implemented. Text-mode reference-image input is not
    /// implemented in this pass.</summary>
    Cohere = 15,
    /// <summary>`api.ai21.com/studio/v1` — documented as an OpenAI-adjacent `chat/completions` shape;
    /// reuses <see cref="Infrastructure.Providers.OpenAiCompatibleProtocol"/> like the Mistral/Groq/etc.
    /// batch above. Text only — no image/audio/video generation exists.</summary>
    AI21 = 16
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

/// <param name="ComfyWorkflowTemplate">The raw API-format ComfyUI workflow JSON (exported from
/// ComfyUI's web UI via "Save (API format)"), with placeholder tokens (<c>{{PROMPT}}</c> required,
/// <c>{{SEED}}</c>/<c>{{UPLOADED_IMAGE_FILENAME}}</c> optional — see
/// <see cref="LibraryRules.ValidateComfyWorkflowTemplate"/>) substituted per generation by
/// <c>ComfyUiProviderAdapter</c>. Populated only when the owning connection's
/// <see cref="ProviderType"/> is <see cref="ProviderType.ComfyUi"/>; null for every other provider,
/// since no other adapter has a per-model workflow concept.</param>
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
    TextResultFormat TextFormat = TextResultFormat.Markdown,
    string? ComfyWorkflowTemplate = null);

/// <summary>Per-token pricing for one model, as reported by a provider's own model-listing endpoint
/// at the moment it was fetched — never bundled/guessed data (`docs/developer/architecture.md`'s
/// explicit "won't fabricate per-token/per-image pricing data" rule). Today only
/// <c>OpenRouterProviderAdapter</c>'s confirmed <c>/models</c> response populates this; every other
/// adapter's model list has no documented pricing field, so their entries always carry
/// <see langword="null"/>.</summary>
public sealed record ProviderModelPricing(decimal PromptCostPerToken, decimal CompletionCostPerToken, string Currency);

public sealed record ProviderModelInfo(
    string ProviderModelId,
    string? DisplayLabel,
    ProviderModelPricing? Pricing = null);

/// <param name="HasReliableUpperBound"><see langword="true"/> only when <c>UpperBound</c> reflects a
/// real cap (a configured <c>MaxTokens</c> setting) rather than being equal to
/// <c>LowerBound</c> because no cap on completion length is known.</param>
public sealed record GenerationCostEstimate(
    decimal LowerBound,
    decimal UpperBound,
    bool HasReliableUpperBound,
    string Currency,
    string Source,
    DateTimeOffset EffectiveAt);

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

/// <param name="Candidates">One entry per candidate the provider actually returned, in response
/// order — lets a safety-blocked candidate keep a stable per-position identity (section 10) instead
/// of only contributing to the aggregate <see cref="SafetyBlockedCount"/>. Null when an adapter
/// doesn't distinguish per-candidate order at all (every adapter except the shared OpenAI-compatible
/// protocol today) — callers fall back to the pre-existing aggregate-only behavior in that case.</param>
public sealed record TextGenerationResult(
    IReadOnlyList<string> Texts,
    int? PromptTokens,
    int? CompletionTokens,
    int SafetyBlockedCount = 0,
    IReadOnlyList<TextGenerationCandidate>? Candidates = null);

/// <summary>One provider-returned text candidate, before it becomes a committed file or a
/// <see cref="GenerationResultStatus.SafetyBlocked"/> result-entry. <see cref="Text"/> is null exactly
/// when <see cref="SafetyBlocked"/> is true — a blocked candidate has no usable content.</summary>
public sealed record TextGenerationCandidate(bool SafetyBlocked, string? Text);

public sealed record TextGenerationSourceImage(
    string MediaType,
    byte[] Bytes);

/// <summary>
/// The outcome of one poll of a provider's asynchronous video (or other long-running) generation job.
/// Distinct from <see cref="AsyncRemoteJobPhase"/> — that enum is the device-local persisted registry
/// state; this is the adapter's immediate report of what the provider said on this specific poll.
/// Completed result bytes carry no adapter-declared media type: the actual media type is detected
/// from the bytes themselves rather than trusted from the provider, matching the existing
/// image-result commit convention.
/// </summary>
public enum AsyncGenerationPollOutcome
{
    Processing = 0,
    Completed = 1,
    Failed = 2,
    /// <summary>The provider reported the job as completed, but downloading its result failed (a
    /// network error, an unexpected empty body, or a result URL rejected by
    /// <c>ResultUrlValidator</c>) — distinct from <see cref="Failed"/> specifically so the async-job
    /// registry row survives for a later <c>Refresh Provider Status</c>/<c>Import Missing Results</c>
    /// retry instead of being treated as a genuine provider-side failure.</summary>
    CompletedDownloadFailed = 3
}

/// <summary>A provider's acknowledgement that it accepted a submit-then-poll generation request.</summary>
public sealed record AsyncGenerationSubmission(
    string ProviderJobId,
    DateTimeOffset? MonitoringDeadline = null);

/// <summary>A provider-reported actual cost for one completed operation. Never estimated or
/// computed locally — only ever the exact value a provider's own response included.</summary>
public sealed record AsyncGenerationCost(double Amount, string Currency);

public sealed record AsyncGenerationPollResult(
    AsyncGenerationPollOutcome Outcome,
    IReadOnlyList<byte[]>? Files,
    string? ErrorMessage,
    AsyncGenerationCost? Cost = null);

/// <summary>
/// The normalized generation status vocabulary. Numeric codes for existing values are frozen
/// because <see cref="GenerationRecord.Status"/> is persisted as a raw integer — new values are
/// only ever appended, never renumbered.
/// </summary>
public enum GenerationStatus
{
    Completed = 0,
    Failed = 1,
    PartiallyCompleted = 2,
    /// <summary>Cancelled after at least one child provider job was actually submitted, but none
    /// completed before cancellation — distinct from a request cancelled before anything was ever
    /// sent, which creates no history record at all.</summary>
    Cancelled = 3,
    /// <summary>Cancelled after at least one child provider job was submitted, with one or more
    /// already completed and committed before cancellation took effect.</summary>
    CancelledWithResults = 4,
    /// <summary>Waiting for a queue slot; nothing has been sent to a provider yet.</summary>
    Queued = 5,
    /// <summary>Held for a nonterminal reason recorded in <see cref="GenerationRecord.HoldReason"/>.
    /// Resumes to <see cref="Queued"/> once the hold is explicitly resolved.</summary>
    Paused = 6,
    /// <summary>Resolving local inputs (model/connection lookup, reading local source files) before
    /// anything is transmitted to a provider.</summary>
    Preparing = 7,
    /// <summary>Transferring source data to a provider ahead of a separate submission call.</summary>
    Uploading = 8,
    /// <summary>The generation request is in flight to the provider.</summary>
    Submitting = 9,
    /// <summary>Transmission may have reached the provider, but acceptance cannot be confirmed —
    /// never automatically resubmitted or polled unless an adapter documents a reconciliation
    /// lookup.</summary>
    SubmissionOutcomeUnknown = 10,
    /// <summary>The provider accepted the request and is working on it.</summary>
    Processing = 11,
    /// <summary>An adapter-declared maximum monitoring lifetime was exceeded while the job was still
    /// reported as running; distinct from <see cref="Paused"/>. Resumes to <see cref="Processing"/>
    /// only via an explicit Resume Monitoring action.</summary>
    MonitoringPaused = 12,
    /// <summary>The provider completed the job and its result bytes are being downloaded.</summary>
    DownloadingResults = 13,
    /// <summary>Results are downloaded but not yet committed to the library (e.g. its removable-
    /// storage volume is unavailable).</summary>
    AwaitingLibrary = 14,
    /// <summary>Cancellation was requested for a provider that does not support cancelling accepted
    /// work; the provider may continue processing (and billing) despite the request.</summary>
    CancellationRequested = 15,
    /// <summary>Cancelled after every child result had already completed and committed — distinct
    /// from <see cref="CancelledWithResults"/>, where only some children completed.</summary>
    CompletedBeforeCancellation = 16,
    /// <summary>Cancelled before any child provider job was ever submitted.</summary>
    CancelledBeforeSubmission = 17
}

/// <summary>The reason a <see cref="GenerationStatus.Paused"/> generation is held. Meaningful only
/// when <see cref="GenerationRecord.Status"/> is <see cref="GenerationStatus.Paused"/>.</summary>
public enum GenerationHoldReason
{
    ConnectionLost = 0,
    RestartConfirmation = 1,
    MeteredNetwork = 2,
    DependencyChanged = 3,
    Other = 4
}

/// <summary>Additional detail on a <see cref="GenerationStatus.Failed"/> generation. Meaningful only
/// when <see cref="GenerationRecord.Status"/> is <see cref="GenerationStatus.Failed"/>.</summary>
public enum GenerationFailureReason
{
    /// <summary>A reconciliation lookup confirmed the provider no longer has this job (e.g.
    /// Not Found/Expired) rather than merely observing a transport error.</summary>
    RemoteJobUnavailable = 0,
    /// <summary>The user explicitly gave up on resolving a <see cref="GenerationStatus.Paused"/> or
    /// <see cref="GenerationStatus.SubmissionOutcomeUnknown"/> record — Abandon Recovery — rather
    /// than the outcome being confirmed by a reconciliation lookup.</summary>
    AbandonedByUser = 1,
    /// <summary>The operating system suspended or timed out the background execution this job was
    /// running under (e.g. Android killed the foreground service) rather than the provider reporting
    /// a failure — Android execution suspension and timeout are recorded separately from
    /// provider failure.</summary>
    ExecutionSuspended = 2
}

/// <summary>One recorded status change for a <see cref="GenerationRecord"/>, timestamped so restart
/// recovery and history views never need to infer state from transient data.
/// <paramref name="Position"/> is null for an aggregate-level transition and set for a transition
/// scoped to one child result.</summary>
public sealed record GenerationStatusTransition(
    string Id,
    string GenerationRecordId,
    int? Position,
    GenerationStatus Status,
    GenerationHoldReason? HoldReason,
    GenerationFailureReason? FailureReason,
    DateTimeOffset OccurredAt);

public enum GenerationResultStatus
{
    Committed = 0,
    Failed = 1,
    /// <summary>The provider returned non-empty bytes that did not match the expected media
    /// category, and those bytes were not recognized as an error document, authentication page or
    /// provider-blocked payload — so instead of an automatic discard, the result awaits an explicit
    /// <c>Retain as Unverified Binary</c>/<c>Discard</c> decision (see
    /// <c>ILibraryWorkspace.GetPendingUnverifiedResultsAsync</c>).</summary>
    PendingReview = 2,
    /// <summary>The provider itself declined to return this candidate on safety/content-policy
    /// grounds (e.g. OpenAI-compatible `finish_reason: content_filter`) — distinct from
    /// <see cref="Failed"/> because retrying the identical request would predictably be blocked again
    /// too, so it is never offered through <c>Retry Failed/Missing Results Only</c>.</summary>
    SafetyBlocked = 3
}

/// <summary>
/// One requested result position's individual outcome within a multi-result generation — e.g. a
/// video generation requesting 3 results where the 2nd job failed independently of the other two.
/// Scoped to Image/Audio/Video today: Text's shortfall causes (safety-blocked candidates, invalid
/// Unicode) are already surfaced through <see cref="GenerationRecord.SafetyBlockedCount"/> and the
/// aggregate committed-vs-requested comparison, so text generations synthesize only <c>Committed</c>
/// entries here rather than duplicating that existing signal.
/// </summary>
public sealed record GenerationResultEntry(
    int Position,
    GenerationResultStatus Status,
    string? FileId,
    string? ErrorMessage);

/// <summary>Bytes staged durably (outside the <c>files</c> table) awaiting the user's explicit
/// <c>Retain as Unverified Binary</c>/<c>Discard</c> decision for a <see
/// cref="GenerationResultStatus.PendingReview"/> result. Never appears in normal library browsing —
/// it only becomes a real <see cref="FileRecord"/> if retained.</summary>
public sealed record PendingUnverifiedResult(
    string Id,
    string GenerationRecordId,
    int Position,
    string StagedFileName,
    long ByteSize,
    string ContentHash,
    string DetectedMediaType,
    DateTimeOffset CreatedAt);

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
    string? PromptImprovementRecordId = null,
    TextResultFormat? TextFormat = null,
    LibraryRecordState State = LibraryRecordState.Active,
    DateTimeOffset? RecycledAt = null,
    IReadOnlyList<FileIdentitySnapshot> TombstonedResults = default!,
    GenerationSettings Settings = default!,
    int SafetyBlockedCount = 0,
    double? ActualCost = null,
    string? ActualCostCurrency = null,
    IReadOnlyList<GenerationResultEntry> Results = default!,
    /// <summary>Meaningful only when <see cref="Status"/> is <see cref="GenerationStatus.Paused"/>.</summary>
    GenerationHoldReason? HoldReason = null,
    /// <summary>Meaningful only when <see cref="Status"/> is <see cref="GenerationStatus.Failed"/>.</summary>
    GenerationFailureReason? FailureReason = null,
    /// <summary>See <see cref="LibraryRules.CurrentGenerationSettingsFormatVersion"/>. Set once when
    /// this record is created/finalized and never rewritten afterward — a stale value on an older
    /// record is expected and meaningful, not a bug.</summary>
    int SettingsFormatVersion = LibraryRules.CurrentGenerationSettingsFormatVersion,
    /// <summary>Replaces the former fixed source-file/tombstone triple — see
    /// <see cref="GenerationInputSlotRole"/>.</summary>
    IReadOnlyList<GenerationSourceSlot> SourceSlots = default!,
    /// <summary>Immutable identity snapshot of <see cref="SourceSlots"/> as they were at submission
    /// time — see <see cref="GenerationSourceSlotSnapshot"/>.</summary>
    IReadOnlyList<GenerationSourceSlotSnapshot> SourceSlotSnapshots = default!)
{
    public IReadOnlyList<FileIdentitySnapshot> TombstonedResults { get; init; } = TombstonedResults ?? [];
    public GenerationSettings Settings { get; init; } = Settings ?? GenerationSettings.Empty;
    public IReadOnlyList<GenerationResultEntry> Results { get; init; } = Results ?? [];
    public IReadOnlyList<GenerationSourceSlot> SourceSlots { get; init; } = SourceSlots ?? [];
    public IReadOnlyList<GenerationSourceSlotSnapshot> SourceSlotSnapshots { get; init; } = SourceSlotSnapshots ?? [];
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
    double? PresencePenalty = null,
    string? AdvancedJson = null)
{
    public static readonly GenerationSettings Empty = new();
}

/// <summary>Which <see cref="GenerationSettings"/> fields a specific provider+mode combination
/// actually transmits to the provider — see <see cref="LibraryRules.GetGenerationSettingsCapabilities"/>
/// for which adapters honor which flags today.</summary>
[Flags]
public enum GenerationSettingsCapability
{
    None = 0,
    Temperature = 1 << 0,
    TopP = 1 << 1,
    MaxTokens = 1 << 2,
    FrequencyPenalty = 1 << 3,
    PresencePenalty = 1 << 4,
    AdvancedJson = 1 << 5
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
    Cancelled = 5,
    /// <summary>The provider completed this job, but downloading its result failed; the registry row
    /// is kept (rather than deleted like every other terminal phase) so a later
    /// <c>Refresh Provider Status</c>/<c>Import Missing Results</c> action can retry the download
    /// while the provider's result remains available.</summary>
    CompletedAwaitingDownload = 6
}

/// <summary>
/// A minimal device-local record of an in-flight asynchronous provider job, keyed by the draft that
/// submitted it rather than by generation-history ID, because no <see cref="GenerationRecord"/>
/// exists until the job reaches a terminal outcome. Never contains prompts, source content or
/// credentials.
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
    DateTimeOffset? MonitoringDeadline,
    /// <summary>Set only once the generation this job belongs to has committed a history record —
    /// null before then, since no <see cref="GenerationRecord"/> exists yet. Together with
    /// <see cref="Position"/>, lets a <see cref="AsyncRemoteJobPhase.CompletedAwaitingDownload"/> row
    /// (which survives past the normal end-of-generation registry cleanup) be retried against the
    /// exact result position it belongs to.</summary>
    string? GenerationRecordId = null,
    int? Position = null);

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
    bool NeedsReview = false,
    int Revision = 1,
    GenerationSettings Settings = default!,
    /// <summary>See <see cref="LibraryRules.CurrentGenerationSettingsFormatVersion"/>. Set once at
    /// creation/update time and never rewritten afterward — a stale value on an older record is
    /// expected and meaningful, not a bug.</summary>
    int SettingsFormatVersion = LibraryRules.CurrentGenerationSettingsFormatVersion,
    IReadOnlyList<GenerationSourceSlot> SourceSlots = default!)
{
    public GenerationSettings Settings { get; init; } = Settings ?? GenerationSettings.Empty;
    public IReadOnlyList<GenerationSourceSlot> SourceSlots { get; init; } = SourceSlots ?? [];
}

public sealed record GenerationDraft(
    string Id,
    string? CustomTitle,
    int TabOrder,
    string? ModelId,
    string Prompt,
    string? SystemInstructions,
    int ResultCount,
    string DestinationFolderId,
    string? ImprovementModelId,
    string? ImprovementGuidance,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    GenerationSettings Settings = default!,
    IReadOnlyList<GenerationSourceSlot> SourceSlots = default!)
{
    public GenerationSettings Settings { get; init; } = Settings ?? GenerationSettings.Empty;
    public IReadOnlyList<GenerationSourceSlot> SourceSlots { get; init; } = SourceSlots ?? [];
}
