using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Application;

public interface ILibraryWorkspace : IAsyncDisposable
{
    LibraryDescriptor Descriptor { get; }

    Task<LibraryFolderContents> GetFolderContentsAsync(string folderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FolderRecord>> GetActiveFoldersAsync(CancellationToken cancellationToken = default);
    Task<FileRecord> GetFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task<FileContentHealth> RevalidateFileContentAsync(string fileId, CancellationToken cancellationToken = default);
    Task<FileContentProvenance> GetFileContentProvenanceAsync(string fileId, CancellationToken cancellationToken = default);
    Task<ManagedContentReplacementReview> ReviewManagedContentReplacementAsync(string fileId, string? sourcePath, CancellationToken cancellationToken = default);
    Task<FileRecord> CommitManagedContentReplacementAsync(ManagedContentReplacementReview review, string? sourcePath, bool confirmDifferingReplacement, bool clearUserMetadata, CancellationToken cancellationToken = default);
    Task<TextFileContent> ReadTextFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task<TextSearchResult> SearchTextFileAsync(string fileId, string searchText, bool matchCase = false, int maximumResults = 200, CancellationToken cancellationToken = default);
    Task<RenderedMarkdownContent> RenderMarkdownFileAsync(string fileId, CancellationToken cancellationToken = default);
    Task<ImageFileContent> ReadImageFileAsync(string fileId, CancellationToken cancellationToken = default);
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
    Task<FileRecord> CreateEditedTextCopyAsync(string fileId, string destinationFolderId, string displayName, string content, TextCopyFormat format, bool copyUserMetadata, bool includeSensitiveMetadata, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImportResult>> ImportAsync(IEnumerable<string> sourcePaths, string destinationFolderId, bool importDuplicates = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImportResult>> ImportWithProgressAsync(IEnumerable<string> sourcePaths, string destinationFolderId, bool importDuplicates, IProgress<ImportProgress>? progress, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MetadataEntry>> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default);
    Task<MetadataEntry> SetMetadataAsync(string fileId, string key, MetadataValueKind kind, string serializedValue, bool isSensitive, CancellationToken cancellationToken = default);
    Task<BulkFileOperationResult> SetMetadataForFilesAsync(IReadOnlyCollection<string> fileIds, string key, MetadataValueKind kind, string serializedValue, bool isSensitive, CancellationToken cancellationToken = default);
    Task<MetadataEntry> RenameMetadataAsync(string fileId, string currentKey, string newKey, CancellationToken cancellationToken = default);
    Task RemoveMetadataAsync(string fileId, string key, CancellationToken cancellationToken = default);
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
}

public interface ILibraryWorkspaceFactory
{
    Task<ILibraryWorkspace> CreateAsync(string rootPath, string displayName = "SlopFactory Library", CancellationToken cancellationToken = default);
    Task<ILibraryWorkspace> OpenAsync(string rootPath, CancellationToken cancellationToken = default);
    Task<ILibraryWorkspace> AdoptCopyAsync(string rootPath, CancellationToken cancellationToken = default);
}
