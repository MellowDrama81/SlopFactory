using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Application;

public interface ILibraryWorkspace : IAsyncDisposable
{
    LibraryDescriptor Descriptor { get; }

    Task<LibraryFolderContents> GetFolderContentsAsync(string folderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FolderRecord>> GetActiveFoldersAsync(CancellationToken cancellationToken = default);
    Task<FileRecord> GetFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task<FileContentHealth> RevalidateFileContentAsync(string fileId, CancellationToken cancellationToken = default);
    Task<ChangedContentInspection> InspectChangedContentAsync(string fileId, CancellationToken cancellationToken = default);
    Task<TextFileContent> ReadChangedTextFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task<FileContentProvenance> GetFileContentProvenanceAsync(string fileId, CancellationToken cancellationToken = default);
    Task<FileDerivationProvenance?> GetFileDerivationProvenanceAsync(string fileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileDerivationChainEntry>> GetFileDerivationChainAsync(string fileId, CancellationToken cancellationToken = default);
    Task<ManagedContentReplacementReview> ReviewManagedContentReplacementAsync(string fileId, string? sourcePath, CancellationToken cancellationToken = default);
    Task<FileRecord> CommitManagedContentReplacementAsync(ManagedContentReplacementReview review, string? sourcePath, bool confirmDifferingReplacement, bool clearUserMetadata, CancellationToken cancellationToken = default);
    Task<TextFileContent> ReadTextFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task<TextSearchResult> SearchTextFileAsync(string fileId, string searchText, bool matchCase = false, int maximumResults = 200, CancellationToken cancellationToken = default);
    Task<RenderedMarkdownContent> RenderMarkdownFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task<ImageFileContent> ReadImageFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task<ImageTechnicalProperties> GetImageTechnicalPropertiesAsync(string fileId, CancellationToken cancellationToken = default);
    Task<MediaTechnicalProperties> GetMediaTechnicalPropertiesAsync(string fileId, CancellationToken cancellationToken = default);
    Task<FileSystemMetadata> GetSystemMetadataAsync(string fileId, CancellationToken cancellationToken = default);
    Task<MediaPlaybackDescriptor> PrepareMediaPlaybackAsync(string fileId, CancellationToken cancellationToken = default);
    Task<Stream> OpenMediaRangeAsync(string fileId, string expectedContentHash, long offset, long length, CancellationToken cancellationToken = default);
    Task<LibraryFileBrowseResult> BrowseFilesAsync(LibraryFileBrowseQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileRecord>> GetActiveFilesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileRecord>> GetRecycledFilesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FolderRecord>> GetRecycledFoldersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileRecord>> GetRecycleBinFilesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FolderRecord>> GetRecycleBinFoldersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecycleBinEntry>> GetRecycleBinEntriesAsync(CancellationToken cancellationToken = default);
    Task<FolderRecord> CreateFolderAsync(string parentFolderId, string name, CancellationToken cancellationToken = default);
    Task<FolderRecord> RenameFolderAsync(string folderId, string name, CancellationToken cancellationToken = default);
    Task<FolderRecord> MoveFolderAsync(string folderId, string destinationFolderId, CancellationToken cancellationToken = default);
    Task<FileRecord> RenameFileAsync(string fileId, string displayName, CancellationToken cancellationToken = default);
    Task<FileRecord> MoveFileAsync(string fileId, string destinationFolderId, CancellationToken cancellationToken = default);
    Task<BulkFileOperationResult> MoveFilesAsync(IReadOnlyCollection<string> fileIds, string destinationFolderId, CancellationToken cancellationToken = default);
    Task<FileRecord> DuplicateFileAsync(string fileId, string destinationFolderId, string displayName, CancellationToken cancellationToken = default);
    Task<BulkFileOperationResult> DuplicateFilesAsync(IReadOnlyCollection<string> fileIds, string destinationFolderId, CancellationToken cancellationToken = default);
    Task<BulkFileOperationResult> DuplicateFilesWithProgressAsync(IReadOnlyCollection<string> fileIds, string destinationFolderId, IProgress<BulkDuplicateProgress>? progress, CancellationToken cancellationToken = default);
    Task<FileRecord> CreateEditedTextCopyAsync(string fileId, string destinationFolderId, string displayName, string content, TextCopyFormat format, bool copyUserMetadata, bool includeSensitiveMetadata, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImportResult>> ImportAsync(IEnumerable<string> sourcePaths, string destinationFolderId, bool importDuplicates = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImportResult>> ImportWithProgressAsync(IEnumerable<string> sourcePaths, string destinationFolderId, bool importDuplicates, IProgress<ImportProgress>? progress, CancellationToken cancellationToken = default);
    Task<RecursiveImportInventory> BuildRecursiveImportInventoryAsync(IEnumerable<string> sourcePaths, bool includeHiddenFiles = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImportResult>> ImportConfirmedInventoryAsync(RecursiveImportInventory inventory, IReadOnlyList<ConfirmedImportCandidate> candidates, string destinationFolderId, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<FileExportResult> ExportFileAsync(string fileId, string destinationPath, ExportCollisionChoice collisionChoice = ExportCollisionChoice.Fail, IProgress<long>? progress = null, CancellationToken cancellationToken = default);
    Task<FileExportResult> ExportChangedBytesAsync(string fileId, string destinationPath, ExportCollisionChoice collisionChoice = ExportCollisionChoice.Fail, IProgress<long>? progress = null, CancellationToken cancellationToken = default);
    Task<BulkExportPreflight> BuildBulkExportPreflightAsync(IReadOnlyCollection<string> fileIds, string destinationDirectory, CancellationToken cancellationToken = default);
    Task<BulkExportResult> ExportFilesAsync(BulkExportPreflight preflight, IReadOnlyDictionary<string, ExportCollisionChoice> collisionChoices, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<ExternalOpenCopy> CreateExternalOpenCopyAsync(string fileId, string temporaryDirectory, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MetadataEntry>> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default);
    Task<MetadataEntry> SetMetadataAsync(string fileId, string key, MetadataValueKind kind, string serializedValue, bool isSensitive, CancellationToken cancellationToken = default);
    Task<BulkFileOperationResult> SetMetadataForFilesAsync(IReadOnlyCollection<string> fileIds, string key, MetadataValueKind kind, string serializedValue, bool isSensitive, CancellationToken cancellationToken = default);
    Task<BulkFileOperationResult> SetMetadataSensitivityForFilesAsync(IReadOnlyCollection<string> fileIds, string key, bool isSensitive, CancellationToken cancellationToken = default);
    Task<MetadataEntry> RenameMetadataAsync(string fileId, string currentKey, string newKey, CancellationToken cancellationToken = default);
    Task RemoveMetadataAsync(string fileId, string key, CancellationToken cancellationToken = default);
    Task<MetadataNormalizationPreview> PreviewMetadataNormalizationAsync(IReadOnlyCollection<string> fileIds, string key, MetadataValueKind targetKind, CancellationToken cancellationToken = default);
    Task<BulkFileOperationResult> CommitMetadataNormalizationAsync(MetadataNormalizationPreview preview, CancellationToken cancellationToken = default);
    Task<BulkFileOperationResult> RemoveMetadataFromFilesAsync(IReadOnlyCollection<string> fileIds, string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileLink>> GetLinksAsync(string fileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileLink>> GetRecycledLinksAsync(CancellationToken cancellationToken = default);
    Task<FileLink> CreateLinkAsync(string sourceFileId, string targetFileId, string label, CancellationToken cancellationToken = default);
    Task<FileLink> RelabelLinkAsync(string linkId, string label, CancellationToken cancellationToken = default);
    Task<FileLink> ReverseLinkAsync(string linkId, CancellationToken cancellationToken = default);
    Task RecycleLinkAsync(string linkId, CancellationToken cancellationToken = default);
    Task RestoreLinkAsync(string linkId, CancellationToken cancellationToken = default);
    Task PermanentlyDeleteLinkAsync(string linkId, CancellationToken cancellationToken = default);
    Task RecycleFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task<BulkFileOperationResult> RecycleFilesAsync(IReadOnlyCollection<string> fileIds, CancellationToken cancellationToken = default);
    Task RecycleFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task RestoreFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task RestoreFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task PermanentlyDeleteFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task PermanentlyDeleteFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task<RecycleBinOperationResult> RestoreRecycleBinItemsAsync(IReadOnlyCollection<RecycleBinItemReference> items, CancellationToken cancellationToken = default);
    Task<RecycleBinRestorePreview> GetRecycleBinRestorePreviewAsync(IReadOnlyCollection<RecycleBinItemReference> items, CancellationToken cancellationToken = default);
    Task<RecycleBinOperationResult> PermanentlyDeleteRecycleBinItemsAsync(IReadOnlyCollection<RecycleBinItemReference> items, CancellationToken cancellationToken = default);
    Task<RecycleBinOperationResult> EmptyRecycleBinAsync(CancellationToken cancellationToken = default);
    Task<LibraryIntegrityReport> RunIntegrityScanAsync(IProgress<LibraryIntegrityScanProgress>? progress = null, CancellationToken cancellationToken = default);
    Task ValidateOpenLibraryAsync(CancellationToken cancellationToken = default);
    Task AdoptAsIndependentLibraryAsync(CancellationToken cancellationToken = default);
    Task RenameLibraryAsync(string displayName, CancellationToken cancellationToken = default);
    string GetManagedFilePath(FileRecord file);

    Task<IReadOnlyList<Connection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Connection>> GetRecycledConnectionsAsync(CancellationToken cancellationToken = default);
    Task<Connection> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default);
    Task<Connection> CreateConnectionAsync(string label, ProviderType providerType, string baseUrl, string credentialHeaderName, string authPrefix, int? timeoutSeconds = null, IReadOnlyList<ConnectionHeader>? additionalHeaders = null, GenericConnectionModalitySettings? genericModalitySettings = null, CancellationToken cancellationToken = default);
    Task<Connection> UpdateConnectionAsync(string connectionId, string label, string baseUrl, string credentialHeaderName, string authPrefix, int? timeoutSeconds = null, IReadOnlyList<ConnectionHeader>? additionalHeaders = null, GenericConnectionModalitySettings? genericModalitySettings = null, CancellationToken cancellationToken = default);
    Task<Connection> SetConnectionCredentialStateAsync(string connectionId, bool hasCredential, CancellationToken cancellationToken = default);
    Task<Connection> SetConnectionTestResultAsync(string connectionId, bool success, string message, CancellationToken cancellationToken = default);
    Task<Connection> ChangeConnectionProviderTypeAsync(string connectionId, ProviderType providerType, CancellationToken cancellationToken = default);
    Task<ModelCatalogue> GetModelCatalogueAsync(string connectionId, CancellationToken cancellationToken = default);
    Task<ModelCatalogue> RefreshModelCatalogueAsync(string connectionId, IReadOnlyList<ProviderModelInfo> discoveredModels, CancellationToken cancellationToken = default);
    Task<ModelCatalogue> MarkModelCatalogueRefreshFailedAsync(string connectionId, CancellationToken cancellationToken = default);
    Task RecycleConnectionAsync(string connectionId, CancellationToken cancellationToken = default);
    Task RestoreConnectionAsync(string connectionId, CancellationToken cancellationToken = default);
    Task PermanentlyDeleteConnectionAsync(string connectionId, CancellationToken cancellationToken = default);

    Task<string> BeginCredentialCandidateAsync(string connectionId, CancellationToken cancellationToken = default);
    Task DiscardCredentialCandidateAsync(string connectionId, string revisionId, CancellationToken cancellationToken = default);
    Task<CredentialPromotionResult> PromoteCredentialRevisionAsync(string connectionId, string revisionId, CancellationToken cancellationToken = default);
    Task<Connection> MarkCredentialRequiresRepairAsync(string connectionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CredentialLedgerConnectionSnapshot>> GetCredentialLedgerSnapshotAsync(CancellationToken cancellationToken = default);
    Task DeleteCredentialLedgerRowAsync(string connectionId, string revisionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Model>> GetActiveModelsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Model>> GetRecycledModelsAsync(CancellationToken cancellationToken = default);
    Task<Model> GetModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task<Model> CreateModelAsync(string label, string connectionId, string providerModelId, GenerationMode mode, bool supportsSystemInstructions, TextResultFormat textFormat = TextResultFormat.Markdown, CancellationToken cancellationToken = default);
    Task<Model> UpdateModelAsync(string modelId, string label, string providerModelId, GenerationMode mode, bool supportsSystemInstructions, TextResultFormat textFormat = TextResultFormat.Markdown, CancellationToken cancellationToken = default);
    Task<Model> MarkModelReviewedAsync(string modelId, CancellationToken cancellationToken = default);
    Task RecycleModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task RestoreModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task PermanentlyDeleteModelAsync(string modelId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GenerationRecord>> GetGenerationHistoryAsync(CancellationToken cancellationToken = default);
    Task<GenerationRecord> GetGenerationRecordAsync(string generationId, CancellationToken cancellationToken = default);
    Task<GenerationRecord> RecordTextGenerationResultAsync(string modelId, string prompt, int resultCount, string destinationFolderId, IReadOnlyList<string>? resultTexts, string? errorMessage, string? systemInstructions = null, int? promptTokens = null, int? completionTokens = null, string? sourceFileId = null, string? promptImprovementRecordId = null, CancellationToken cancellationToken = default);
    Task<GenerationRecord> RecordImageGenerationResultAsync(string modelId, string prompt, int resultCount, string destinationFolderId, IReadOnlyList<byte[]>? resultImages, string? errorMessage, string? promptImprovementRecordId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PromptImprovementRecord>> GetPromptImprovementHistoryAsync(CancellationToken cancellationToken = default);
    Task<PromptImprovementRecord> RecordPromptImprovementAttemptAsync(string modelId, string rawPrompt, string? guidance, string templateVersion, IReadOnlyList<string>? candidates, string? errorMessage, int? promptTokens = null, int? completionTokens = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SavedGenerationSetting>> GetActiveSavedSettingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SavedGenerationSetting>> GetRecycledSavedSettingsAsync(CancellationToken cancellationToken = default);
    Task<SavedGenerationSetting> GetSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken = default);
    Task<SavedGenerationSetting> CreateSavedSettingAsync(string title, string? modelId, string prompt, int resultCount, string destinationFolderId, string? systemInstructions = null, string? sourceFileId = null, CancellationToken cancellationToken = default);
    Task<SavedGenerationSetting> UpdateSavedSettingAsync(string savedSettingId, int expectedRevision, string title, string? modelId, string prompt, int resultCount, string destinationFolderId, string? systemInstructions = null, string? sourceFileId = null, CancellationToken cancellationToken = default);
    Task RecycleSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken = default);
    Task RestoreSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken = default);
    Task PermanentlyDeleteSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GenerationDraft>> GetDraftsAsync(CancellationToken cancellationToken = default);
    Task<GenerationDraft> GetDraftAsync(string draftId, CancellationToken cancellationToken = default);
    Task<GenerationDraft> CreateDraftAsync(CancellationToken cancellationToken = default);
    Task<GenerationDraft> ReplaceDraftStateAsync(string draftId, string? customTitle, string? modelId, string prompt, string? systemInstructions, string? sourceFileId, int resultCount, string destinationFolderId, string? improvementModelId, string? improvementGuidance, CancellationToken cancellationToken = default);
    Task<GenerationDraft> DuplicateDraftAsync(string draftId, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(string draftId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GenerationDraft>> ReorderDraftsAsync(IReadOnlyList<string> orderedDraftIds, CancellationToken cancellationToken = default);
}

public interface ILibraryWorkspaceFactory
{
    Task<ILibraryWorkspace> CreateAsync(string rootPath, string displayName = "SlopFactory Library", CancellationToken cancellationToken = default);
    Task<ILibraryWorkspace> OpenAsync(string rootPath, CancellationToken cancellationToken = default);
    Task<ILibraryWorkspace> AdoptCopyAsync(string rootPath, CancellationToken cancellationToken = default);
}
