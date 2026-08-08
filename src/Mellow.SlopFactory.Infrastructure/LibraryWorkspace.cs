using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Persistence;
using Mellow.SlopFactory.Infrastructure.Storage;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Mellow.SlopFactory.Infrastructure;

internal sealed class LibraryWorkspace : ILibraryWorkspace
{
    private readonly LibraryLayout _layout;
    private readonly SqliteLibraryDatabase _database;
    private readonly FileStream _libraryLock;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private LibraryManifest _manifest;
    private bool _disposed;

    public LibraryWorkspace(LibraryLayout layout, LibraryDescriptor descriptor, LibraryManifest manifest, SqliteLibraryDatabase database, FileStream libraryLock)
    {
        _layout = layout;
        Descriptor = descriptor;
        _manifest = manifest;
        _database = database;
        _libraryLock = libraryLock;
    }

    public LibraryDescriptor Descriptor { get; private set; }

    public Task<LibraryFolderContents> GetFolderContentsAsync(string folderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetFolderContentsAsync(folderId, cancellationToken);
    }

    public Task<IReadOnlyList<FolderRecord>> GetActiveFoldersAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetFoldersByStateAsync(LibraryRecordState.Active, cancellationToken);
    }

    public Task<FileRecord> GetFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetFileAsync(fileId, cancellationToken);
    }

    public Task<FileContentHealth> RevalidateFileContentAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => RevalidateFileContentCoreAsync(fileId, cancellationToken), cancellationToken);
    }

    public Task<FileContentProvenance> GetFileContentProvenanceAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetFileContentProvenanceAsync(fileId, cancellationToken);
    }

    private async Task<FileContentHealth> RevalidateFileContentCoreAsync(string fileId, CancellationToken cancellationToken)
    {
        var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active file can be revalidated.");
        var path = _layout.ManagedFilePath(file.ManagedName);
        if (Directory.Exists(path))
        {
            var unsafeEntry = await _database.SetFileContentStateAsync(fileId, FileContentState.Changed, cancellationToken).ConfigureAwait(false);
            return new FileContentHealth(unsafeEntry, null, null, null);
        }
        if (!File.Exists(path))
        {
            var missing = await _database.SetFileContentStateAsync(fileId, FileContentState.Missing, cancellationToken).ConfigureAwait(false);
            return new FileContentHealth(missing, null, null, null);
        }
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            var unsafeFile = await _database.SetFileContentStateAsync(fileId, FileContentState.Changed, cancellationToken).ConfigureAwait(false);
            return new FileContentHealth(unsafeFile, null, info.Length, null);
        }
        var hash = await Hashing.Sha256Async(path, cancellationToken).ConfigureAwait(false);
        var mediaType = (await MediaTypeDetector.DetectAsync(path, cancellationToken).ConfigureAwait(false)).MediaType;
        var matches = info.Length == file.ByteSize && string.Equals(hash, file.ContentHash, StringComparison.Ordinal);
        var nextState = matches
            ? file.ContentState == FileContentState.Replaced ? FileContentState.Replaced : FileContentState.Healthy
            : FileContentState.Changed;
        var updated = await _database.SetFileContentStateAsync(fileId, nextState, cancellationToken).ConfigureAwait(false);
        return new FileContentHealth(updated, hash, info.Length, mediaType);
    }

    private async Task<FileRecord> GetVerifiedContentFileAsync(string fileId, CancellationToken cancellationToken)
    {
        var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active file can be viewed.");
        var path = _layout.ManagedFilePath(file.ManagedName);
        if (Directory.Exists(path))
        {
            _ = await RevalidateFileContentAsync(fileId, cancellationToken).ConfigureAwait(false);
            throw new LibraryValidationException("The managed path was replaced by an unsafe directory and cannot be used until reviewed.");
        }
        if (!File.Exists(path))
        {
            _ = await RevalidateFileContentAsync(fileId, cancellationToken).ConfigureAwait(false);
            throw new LibraryValidationException("The managed file is missing. Its record and metadata have been preserved.");
        }
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != file.ByteSize || !string.Equals(await Hashing.Sha256Async(path, cancellationToken).ConfigureAwait(false), file.ContentHash, StringComparison.Ordinal))
        {
            _ = await RevalidateFileContentAsync(fileId, cancellationToken).ConfigureAwait(false);
            throw new LibraryValidationException("The managed bytes changed outside SlopFactory and cannot be used until reviewed.");
        }
        if (file.ContentState is FileContentState.Missing or FileContentState.Changed)
        {
            return (await RevalidateFileContentAsync(fileId, cancellationToken).ConfigureAwait(false)).File;
        }
        return file;
    }

    public async Task<ManagedContentReplacementReview> ReviewManagedContentReplacementAsync(string fileId, string? sourcePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active || file.ContentState is not (FileContentState.Missing or FileContentState.Changed))
        {
            throw new LibraryValidationException("Managed content can be replaced only for a missing or changed active file.");
        }
        var managedPath = _layout.ManagedFilePath(file.ManagedName);
        var candidatePath = string.IsNullOrWhiteSpace(sourcePath) ? managedPath : Path.GetFullPath(sourcePath);
        var usesCurrent = string.Equals(candidatePath, managedPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (usesCurrent && file.ContentState == FileContentState.Missing) throw new LibraryValidationException("There are no current managed bytes to accept.");
        var candidate = await InspectReplacementCandidateAsync(candidatePath, cancellationToken).ConfigureAwait(false);
        var original = await _database.GetFileContentProvenanceAsync(fileId, cancellationToken).ConfigureAwait(false);
        var metadata = await _database.GetMetadataAsync(fileId, cancellationToken).ConfigureAwait(false);
        return new ManagedContentReplacementReview(file, original.OriginalContentHash, original.OriginalByteSize, original.OriginalMediaType, candidate.Hash, candidate.ByteSize, candidate.MediaType,
            usesCurrent, metadata.Count(item => !item.IsSensitive), metadata.Count(item => item.IsSensitive));
    }

    public Task<FileRecord> CommitManagedContentReplacementAsync(ManagedContentReplacementReview review, string? sourcePath, bool confirmDifferingReplacement, bool clearUserMetadata, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(review);
        return RunMutationAsync(() => CommitManagedContentReplacementCoreAsync(review, sourcePath, confirmDifferingReplacement, clearUserMetadata, cancellationToken), cancellationToken);
    }

    private async Task<FileRecord> CommitManagedContentReplacementCoreAsync(ManagedContentReplacementReview review, string? sourcePath, bool confirmDifferingReplacement, bool clearUserMetadata, CancellationToken cancellationToken)
    {
        var file = await _database.GetFileAsync(review.File.Id, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active || file.ContentState is not (FileContentState.Missing or FileContentState.Changed)) throw new LibraryValidationException("The file is no longer eligible for content replacement.");
        var original = await _database.GetFileContentProvenanceAsync(file.Id, cancellationToken).ConfigureAwait(false);
        if (original.OriginalContentHash != review.OriginalContentHash || original.OriginalByteSize != review.OriginalByteSize || original.OriginalMediaType != review.OriginalMediaType) throw new LibraryValidationException("The recorded provenance changed after replacement review.");
        var managedPath = _layout.ManagedFilePath(file.ManagedName);
        var candidatePath = string.IsNullOrWhiteSpace(sourcePath) ? managedPath : Path.GetFullPath(sourcePath);
        var usesCurrent = string.Equals(candidatePath, managedPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (usesCurrent != review.UsesCurrentManagedBytes) throw new LibraryValidationException("The replacement source changed after review.");
        var candidate = await InspectReplacementCandidateAsync(candidatePath, cancellationToken).ConfigureAwait(false);
        if (candidate.Hash != review.CandidateContentHash || candidate.ByteSize != review.CandidateByteSize || candidate.MediaType != review.CandidateMediaType) throw new LibraryValidationException("The replacement bytes changed after review. Review them again.");
        if (!review.RestoresOriginal && !confirmDifferingReplacement) throw new LibraryValidationException("Confirm the permanent differing-content replacement before continuing.");

        if (usesCurrent)
        {
            return await _database.AcceptFileContentAsync(file.Id, candidate.Hash, candidate.ByteSize, candidate.MediaType, review.RestoresOriginal, !review.RestoresOriginal && clearUserMetadata, cancellationToken).ConfigureAwait(false);
        }

        var stagedPath = _layout.StagingFilePath($"replacement-{LibraryRules.NewId()}.tmp");
        string? rollbackPath = null;
        try
        {
            var copied = await Hashing.CopyAndHashAsync(candidatePath, stagedPath, cancellationToken).ConfigureAwait(false);
            if (copied.Hash != candidate.Hash || copied.Bytes != candidate.ByteSize) throw new LibraryValidationException("The replacement source changed while it was copied.");
            if (File.Exists(managedPath))
            {
                var existing = new FileInfo(managedPath);
                if ((existing.Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("The managed path is redirected and cannot be replaced safely.");
                rollbackPath = _layout.StagingFilePath($"replacement-rollback-{LibraryRules.NewId()}.tmp");
                File.Move(managedPath, rollbackPath);
            }
            else if (Directory.Exists(managedPath)) throw new LibraryValidationException("The managed path was replaced by a directory and cannot be repaired automatically.");
            File.Move(stagedPath, managedPath);
            try
            {
                var accepted = await _database.AcceptFileContentAsync(file.Id, candidate.Hash, candidate.ByteSize, candidate.MediaType, review.RestoresOriginal, !review.RestoresOriginal && clearUserMetadata, cancellationToken).ConfigureAwait(false);
                TryDelete(rollbackPath);
                return accepted;
            }
            catch
            {
                TryDelete(managedPath);
                if (rollbackPath is not null && File.Exists(rollbackPath)) File.Move(rollbackPath, managedPath);
                throw;
            }
        }
        finally
        {
            TryDelete(stagedPath);
            TryDelete(rollbackPath);
        }
    }

    private static async Task<(string Hash, long ByteSize, string MediaType)> InspectReplacementCandidateAsync(string path, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path) || !File.Exists(path)) throw new LibraryValidationException("The replacement source is not an available regular file.");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("A redirected file cannot be used as replacement content.");
        var hash = await Hashing.Sha256Async(path, cancellationToken).ConfigureAwait(false);
        var mediaType = (await MediaTypeDetector.DetectAsync(path, cancellationToken).ConfigureAwait(false)).MediaType;
        return (hash, info.Length, mediaType);
    }

    public async Task<TextFileContent> ReadTextFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        const int maximumDisplayedCharacters = 1_048_576;
        var file = await GetVerifiedContentFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active file can be viewed.");
        if (!IsTextMediaType(file.MediaType)) throw new LibraryValidationException("This file is not a supported text format.");
        var path = GetManagedFilePath(file);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var prefix = new byte[3];
            var prefixLength = await stream.ReadAsync(prefix, cancellationToken).ConfigureAwait(false);
            if (prefixLength >= 2 && ((prefix[0] == 0xFF && prefix[1] == 0xFE) || (prefix[0] == 0xFE && prefix[1] == 0xFF)))
            {
                throw new LibraryValidationException("The built-in text viewer supports UTF-8 files only.");
            }
            stream.Position = prefixLength >= 3 && prefix.AsSpan().SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 65_536, leaveOpen: false);
            var buffer = new char[maximumDisplayedCharacters + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            var truncated = total > maximumDisplayedCharacters || reader.Peek() >= 0;
            return new TextFileContent(new string(buffer, 0, Math.Min(total, maximumDisplayedCharacters)), truncated, "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            throw new LibraryValidationException("The file contains invalid UTF-8 and cannot be shown in the built-in text viewer.");
        }
    }

    public async Task<TextSearchResult> SearchTextFileAsync(string fileId, string searchText, bool matchCase = false, int maximumResults = 200, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(searchText);
        if (searchText.Length == 0) throw new LibraryValidationException("Search text is required.");
        if (searchText.Any(character => character is '\r' or '\n' or '\0')) throw new LibraryValidationException("Text search must use a single line of text.");
        if (searchText.EnumerateRunes().Count() > LibraryRules.MaximumTextSearchScalars) throw new LibraryValidationException($"Text search cannot exceed {LibraryRules.MaximumTextSearchScalars} Unicode characters.");
        if (maximumResults is < 1 or > 1_000) throw new LibraryValidationException("The maximum text-search result count must be between 1 and 1,000.");

        var file = await GetVerifiedContentFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active file can be searched.");
        if (!IsTextMediaType(file.MediaType)) throw new LibraryValidationException("This file is not a supported text format.");

        const int bufferSize = 32_768;
        const int contextLength = 60;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<TextSearchMatch>(Math.Min(maximumResults, 200));
        long totalMatches = 0;
        long totalCharacters = 0;
        long nextCandidateOffset = 0;
        var carry = string.Empty;
        var path = GetManagedFilePath(file);

        void ProcessCandidates(string value, long valueOffset, long exclusiveOffset)
        {
            var localStart = (int)Math.Max(0, nextCandidateOffset - valueOffset);
            while (localStart <= value.Length - searchText.Length)
            {
                var found = value.IndexOf(searchText, localStart, comparison);
                if (found < 0) break;
                var absoluteOffset = valueOffset + found;
                if (absoluteOffset >= exclusiveOffset) break;
                totalMatches++;
                if (matches.Count < maximumResults)
                {
                    var snippetStart = Math.Max(0, found - contextLength);
                    var snippetEnd = Math.Min(value.Length, found + searchText.Length + contextLength);
                    matches.Add(new TextSearchMatch(absoluteOffset, value[snippetStart..snippetEnd], found - snippetStart, searchText.Length));
                }
                localStart = found + 1;
            }
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, bufferSize, leaveOpen: false);
            var buffer = new char[bufferSize];
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var combined = carry + new string(buffer, 0, read);
                var combinedOffset = totalCharacters - carry.Length;
                totalCharacters += read;
                var safeExclusiveOffset = Math.Max(0, totalCharacters - (searchText.Length + contextLength - 1));
                ProcessCandidates(combined, combinedOffset, safeExclusiveOffset);
                nextCandidateOffset = safeExclusiveOffset;
                var carryLength = Math.Min(combined.Length, searchText.Length + (contextLength * 2));
                carry = combined[^carryLength..];
            }
            ProcessCandidates(carry, totalCharacters - carry.Length, totalCharacters);
            return new TextSearchResult(totalMatches, matches);
        }
        catch (DecoderFallbackException)
        {
            throw new LibraryValidationException("The file contains invalid UTF-8 and cannot be searched by the built-in text viewer.");
        }
    }

    public async Task<RenderedMarkdownContent> RenderMarkdownFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await GetVerifiedContentFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active file can be rendered.");
        if (file.MediaType != "text/markdown") throw new LibraryValidationException("Rendered view is available only for Markdown files.");
        var text = await ReadTextFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (text.IsTruncated) throw new LibraryValidationException("This Markdown file is too large for safe rendered view. Plain-text partial view remains available.");
        if (text.Content.Length > LibraryRules.MaximumRenderedMarkdownCharacters)
        {
            throw new LibraryValidationException($"Markdown longer than {LibraryRules.MaximumRenderedMarkdownCharacters:N0} characters is shown as plain text to keep rendering bounded.");
        }
        return SafeMarkdownRenderer.Render(text.Content);
    }

    public async Task<ImageFileContent> ReadImageFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await GetVerifiedContentFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active file can be viewed.");
        if (!IsImageMediaType(file.MediaType)) throw new LibraryValidationException("This file is not a supported built-in image format.");
        if (file.ByteSize > LibraryRules.MaximumInlineImageBytes)
        {
            throw new LibraryValidationException($"Images larger than {LibraryRules.MaximumInlineImageBytes / 1_048_576} MiB cannot be displayed inline.");
        }
        var path = GetManagedFilePath(file);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.LongLength != file.ByteSize || !string.Equals(hash, file.ContentHash, StringComparison.Ordinal))
        {
            throw new LibraryValidationException("The managed image bytes no longer match the library record.");
        }
        ImageSafetyInspector.Validate(bytes, file.MediaType);
        return new ImageFileContent(file.MediaType, file.MediaType == "image/svg+xml" ? SvgSanitizer.Sanitize(bytes) : bytes);
    }

    public async Task<MediaPlaybackDescriptor> PrepareMediaPlaybackAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await GetVerifiedContentFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active media file can be played.");
        if (!IsPlayableMediaType(file.MediaType)) throw new LibraryValidationException("This file is not a supported built-in audio or video format.");
        var path = ValidateRegularManagedFile(file);
        var actualHash = await Hashing.Sha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, file.ContentHash, StringComparison.Ordinal))
        {
            throw new LibraryValidationException("The managed media bytes no longer match the library record.");
        }
        return new MediaPlaybackDescriptor(file.Id, file.MediaType, file.ByteSize, file.ContentHash);
    }

    public async Task<Stream> OpenMediaRangeAsync(string fileId, string expectedContentHash, long offset, long length, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (offset < 0 || length < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Media byte ranges cannot be negative.");
        var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active || file.ContentState is FileContentState.Missing or FileContentState.Changed || !IsPlayableMediaType(file.MediaType)) throw new LibraryValidationException("The media file is no longer available for playback.");
        if (!string.Equals(file.ContentHash, expectedContentHash, StringComparison.Ordinal)) throw new LibraryValidationException("The media file changed after playback was prepared.");
        if (offset > file.ByteSize || length > file.ByteSize - offset) throw new LibraryValidationException("The requested media byte range is outside the file.");
        var path = ValidateRegularManagedFile(file);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            if (stream.Length != file.ByteSize) throw new LibraryValidationException("The managed media size no longer matches the library record.");
            stream.Position = offset;
            return new BoundedReadStream(stream, length);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsTextMediaType(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType is "application/json" or "application/xml";

    private static bool IsImageMediaType(string mediaType) =>
        mediaType is "image/png" or "image/jpeg" or "image/webp" or "image/gif" or "image/svg+xml";

    private static bool IsPlayableMediaType(string mediaType) =>
        mediaType is "audio/mpeg" or "audio/wav" or "audio/aac" or "audio/mp4" or "audio/flac" or "audio/ogg" or "video/mp4";

    private string ValidateRegularManagedFile(FileRecord file)
    {
        var path = _layout.ManagedFilePath(file.ManagedName);
        if (Directory.Exists(path)) throw new LibraryValidationException("The managed media path is not a regular file.");
        if (!File.Exists(path)) throw new LibraryValidationException("The managed media file is missing.");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("Redirected managed media files cannot be played.");
        if (info.Length != file.ByteSize) throw new LibraryValidationException("The managed media size no longer matches the library record.");
        return path;
    }

    public Task<IReadOnlyList<FileRecord>> GetActiveFilesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetActiveFilesAsync(cancellationToken);
    }

    public Task<LibraryFileBrowseResult> BrowseFilesAsync(LibraryFileBrowseQuery query, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.FolderId)) throw new LibraryValidationException("A current folder is required.");
        var searchText = query.SearchText ?? string.Empty;
        if (searchText.Length > 256) throw new LibraryValidationException("Library search text cannot exceed 256 characters.");
        if (!Enum.IsDefined(query.Scope) || !Enum.IsDefined(query.MediaKind) || !Enum.IsDefined(query.Sort) || (query.Origin is not null && !Enum.IsDefined(query.Origin.Value)))
        {
            throw new LibraryValidationException("The library browser contains an unsupported filter or sort value.");
        }
        if (query.Offset < 0) throw new LibraryValidationException("The result offset cannot be negative.");
        if (query.PageSize is < 1 or > 200) throw new LibraryValidationException("The page size must be between 1 and 200.");
        if (query.ImportedFromInclusive is not null && query.ImportedBeforeExclusive is not null && query.ImportedFromInclusive >= query.ImportedBeforeExclusive)
        {
            throw new LibraryValidationException("The imported-from date must be earlier than the imported-through date.");
        }
        return _database.BrowseFilesAsync(query with { SearchText = searchText.Trim() }, cancellationToken);
    }

    public Task<IReadOnlyList<FileRecord>> GetRecycledFilesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetFilesByStateAsync(LibraryRecordState.Recycled, cancellationToken);
    }

    public Task<IReadOnlyList<FolderRecord>> GetRecycledFoldersAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetFoldersByStateAsync(LibraryRecordState.Recycled, cancellationToken);
    }

    public Task<IReadOnlyList<FileRecord>> GetRecycleBinFilesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetTopLevelDeletedFilesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<FolderRecord>> GetRecycleBinFoldersAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetTopLevelDeletedFoldersAsync(cancellationToken);
    }

    public Task<IReadOnlyList<RecycleBinEntry>> GetRecycleBinEntriesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetRecycleBinEntriesAsync(cancellationToken);
    }

    public Task<FolderRecord> CreateFolderAsync(string parentFolderId, string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.CreateFolderAsync(parentFolderId, name, cancellationToken), cancellationToken);
    }

    public Task<FolderRecord> RenameFolderAsync(string folderId, string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RenameFolderAsync(folderId, name, Descriptor.RootFolderId, Descriptor.GeneratedFolderId, cancellationToken), cancellationToken);
    }

    public Task<FolderRecord> MoveFolderAsync(string folderId, string destinationFolderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.MoveFolderAsync(folderId, destinationFolderId, Descriptor.RootFolderId, Descriptor.GeneratedFolderId, cancellationToken), cancellationToken);
    }

    public Task<FileRecord> RenameFileAsync(string fileId, string displayName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RenameFileAsync(fileId, displayName, cancellationToken), cancellationToken);
    }

    public Task<FileRecord> MoveFileAsync(string fileId, string destinationFolderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.MoveFileAsync(fileId, destinationFolderId, cancellationToken), cancellationToken);
    }

    public Task<BulkFileOperationResult> MoveFilesAsync(IReadOnlyCollection<string> fileIds, string destinationFolderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => ProcessFilesAsync(fileIds, fileId => _database.MoveFileAsync(fileId, destinationFolderId, cancellationToken), cancellationToken), cancellationToken);
    }

    public Task<FileRecord> DuplicateFileAsync(string fileId, string destinationFolderId, string displayName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => DuplicateFileCoreAsync(fileId, destinationFolderId, displayName, cancellationToken), cancellationToken);
    }

    private async Task<FileRecord> DuplicateFileCoreAsync(string fileId, string destinationFolderId, string displayName, CancellationToken cancellationToken)
    {
        var normalizedName = LibraryRules.NormalizeDisplayName(displayName, "File name");
        var source = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (source.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active, healthy file can be duplicated.");

        var duplicateId = LibraryRules.NewId();
        var managedName = duplicateId + Path.GetExtension(source.ManagedName);
        var sourcePath = _layout.ManagedFilePath(source.ManagedName);
        var stagingPath = _layout.StagingFilePath(duplicateId + ".duplicating");
        var managedPath = _layout.ManagedFilePath(managedName);
        try
        {
            var copied = await Hashing.CopyAndHashAsync(sourcePath, stagingPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(copied.Hash, source.ContentHash, StringComparison.Ordinal) || copied.Bytes != source.ByteSize)
            {
                throw new IOException("The managed source changed while it was being duplicated.");
            }

            File.Move(stagingPath, managedPath, false);
            stagingPath = string.Empty;
            var now = DateTimeOffset.UtcNow;
            var duplicate = new FileRecord(duplicateId, destinationFolderId, normalizedName, source.OriginalFileName, managedName, copied.Hash, copied.Bytes, source.MediaType,
                FileOrigin.UserCopy, LibraryRecordState.Active, now, now, null, null);
            try
            {
                await _database.InsertDuplicateFileAsync(source.Id, duplicate, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(managedPath);
                throw;
            }
            managedPath = string.Empty;
            return duplicate;
        }
        finally
        {
            TryDelete(stagingPath);
            TryDelete(managedPath);
        }
    }

    public Task<FileRecord> CreateEditedTextCopyAsync(
        string fileId,
        string destinationFolderId,
        string displayName,
        string content,
        TextCopyFormat format,
        bool copyUserMetadata,
        bool includeSensitiveMetadata,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => CreateEditedTextCopyCoreAsync(fileId, destinationFolderId, displayName, content, format, copyUserMetadata, includeSensitiveMetadata, cancellationToken), cancellationToken);
    }

    private async Task<FileRecord> CreateEditedTextCopyCoreAsync(
        string fileId,
        string destinationFolderId,
        string displayName,
        string content,
        TextCopyFormat format,
        bool copyUserMetadata,
        bool includeSensitiveMetadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (includeSensitiveMetadata && !copyUserMetadata) throw new LibraryValidationException("Sensitive metadata can be included only when user metadata copying is enabled.");
        var normalizedName = LibraryRules.NormalizeDisplayName(displayName, "File name");
        var source = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (source.State != LibraryRecordState.Active || !IsTextMediaType(source.MediaType))
        {
            throw new LibraryValidationException("Edit as Copy is available only for active supported text files.");
        }

        var (mediaType, extension) = format switch
        {
            TextCopyFormat.PlainText => ("text/plain", ".txt"),
            TextCopyFormat.Markdown => ("text/markdown", ".md"),
            TextCopyFormat.PreserveSourceFormat => (source.MediaType, Path.GetExtension(source.ManagedName)),
            _ => throw new LibraryValidationException("The selected text-copy format is unsupported.")
        };

        var utf8 = new UTF8Encoding(false, true);
        byte[] bytes;
        try { bytes = utf8.GetBytes(content); }
        catch (EncoderFallbackException) { throw new LibraryValidationException("The edited text contains an invalid Unicode sequence."); }
        if (bytes.Length > LibraryRules.MaximumEditableTextUtf8Bytes)
        {
            throw new LibraryValidationException($"Edited text cannot exceed {LibraryRules.MaximumEditableTextUtf8Bytes:N0} UTF-8 bytes.");
        }
        ValidateStructuredText(mediaType, content);

        var copyId = LibraryRules.NewId();
        var managedName = copyId + extension;
        var stagingPath = _layout.StagingFilePath(copyId + ".editing");
        var managedPath = _layout.ManagedFilePath(managedName);
        try
        {
            await using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            File.Move(stagingPath, managedPath, false);
            stagingPath = string.Empty;
            var now = DateTimeOffset.UtcNow;
            var copy = new FileRecord(copyId, destinationFolderId, normalizedName, normalizedName, managedName, hash, bytes.LongLength, mediaType,
                FileOrigin.EditedCopy, LibraryRecordState.Active, now, now, null, null);
            try
            {
                await _database.InsertEditedTextCopyAsync(source.Id, copy, copyUserMetadata, includeSensitiveMetadata, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(managedPath);
                throw;
            }
            managedPath = string.Empty;
            return copy;
        }
        finally
        {
            TryDelete(stagingPath);
            TryDelete(managedPath);
        }
    }

    private static void ValidateStructuredText(string mediaType, string content)
    {
        try
        {
            if (mediaType == "application/json")
            {
                using var _ = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
            }
            else if (mediaType == "application/xml")
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = LibraryRules.MaximumEditableTextUtf8Bytes };
                using var reader = XmlReader.Create(new StringReader(content), settings);
                while (reader.Read()) { }
            }
        }
        catch (JsonException exception) { throw new LibraryValidationException($"The edited JSON is invalid: {exception.Message}"); }
        catch (XmlException exception) { throw new LibraryValidationException($"The edited XML is invalid: {exception.Message}"); }
    }

    public Task<IReadOnlyList<ImportResult>> ImportAsync(IEnumerable<string> sourcePaths, string destinationFolderId, bool importDuplicates = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => ImportCoreAsync(sourcePaths, destinationFolderId, importDuplicates, null, returnCancellationResults: false, cancellationToken: cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<ImportResult>> ImportWithProgressAsync(IEnumerable<string> sourcePaths, string destinationFolderId, bool importDuplicates, IProgress<ImportProgress>? progress, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => ImportCoreAsync(sourcePaths, destinationFolderId, importDuplicates, progress, returnCancellationResults: true, cancellationToken: cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<ImportResult>> ImportCoreAsync(IEnumerable<string> sourcePaths, string destinationFolderId, bool importDuplicates, IProgress<ImportProgress>? progress, bool returnCancellationResults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        _ = await _database.GetFolderContentsAsync(destinationFolderId, cancellationToken).ConfigureAwait(false);
        var paths = sourcePaths.ToArray();
        var results = new List<ImportResult>();
        for (var itemIndex = 0; itemIndex < paths.Length; itemIndex++)
        {
            var sourcePath = paths[itemIndex];
            ImportCandidate? candidate = null;
            string? stagingPath = null;
            string? managedPath = null;
            try
            {
                var info = new FileInfo(sourcePath);
                if (!info.Exists) throw new FileNotFoundException("The selected source file no longer exists.", sourcePath);
                var displayName = LibraryRules.NormalizeDisplayName(info.Name, "File name");
                candidate = new ImportCandidate(info.FullName, displayName, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
                progress?.Report(new ImportProgress(itemIndex + 1, paths.Length, displayName, "Hashing source", 0, info.Length));
                var hash = await Hashing.Sha256Async(info.FullName, cancellationToken, bytes => progress?.Report(new ImportProgress(itemIndex + 1, paths.Length, displayName, "Hashing source", bytes, info.Length))).ConfigureAwait(false);
                var matches = await _database.FindByHashAsync(hash, info.Length, cancellationToken).ConfigureAwait(false);
                if (matches.Count > 0 && !importDuplicates)
                {
                    results.Add(new ImportResult(candidate, null, ImportOutcome.DuplicateSkipped, matches, null));
                    progress?.Report(new ImportProgress(itemIndex + 1, paths.Length, displayName, "Duplicate skipped", info.Length, info.Length));
                    continue;
                }

                var (mediaType, safeExtension) = await MediaTypeDetector.DetectAsync(info.FullName, cancellationToken).ConfigureAwait(false);
                var fileId = LibraryRules.NewId();
                var managedName = fileId + safeExtension;
                stagingPath = _layout.StagingFilePath(fileId + ".importing");
                managedPath = _layout.ManagedFilePath(managedName);
                progress?.Report(new ImportProgress(itemIndex + 1, paths.Length, displayName, "Copying into managed storage", 0, info.Length));
                var copied = await Hashing.CopyAndHashAsync(info.FullName, stagingPath, cancellationToken, bytes => progress?.Report(new ImportProgress(itemIndex + 1, paths.Length, displayName, "Copying into managed storage", bytes, info.Length))).ConfigureAwait(false);
                if (!string.Equals(copied.Hash, hash, StringComparison.Ordinal) || copied.Bytes != info.Length)
                {
                    throw new IOException("The source file changed while it was being imported.");
                }

                File.Move(stagingPath, managedPath, false);
                stagingPath = null;
                var resolvedName = await _database.ResolveAvailableFileNameAsync(destinationFolderId, displayName, cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var record = new FileRecord(fileId, destinationFolderId, resolvedName, displayName, managedName, hash, copied.Bytes, mediaType, FileOrigin.Imported, LibraryRecordState.Active, now, now, candidate.SourceLastModified, null);
                try
                {
                    await _database.InsertImportedFileAsync(record, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    TryDelete(managedPath);
                    throw;
                }
                managedPath = null;
                results.Add(new ImportResult(candidate, record, ImportOutcome.Imported, matches, null));
                progress?.Report(new ImportProgress(itemIndex + 1, paths.Length, displayName, "Completed", info.Length, info.Length));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(stagingPath);
                TryDelete(managedPath);
                if (!returnCancellationResults) throw;
                candidate ??= new ImportCandidate(sourcePath, Path.GetFileName(sourcePath), 0, null);
                results.Add(new ImportResult(candidate, null, ImportOutcome.Cancelled, [], null));
                for (var remaining = itemIndex + 1; remaining < paths.Length; remaining++)
                {
                    results.Add(new ImportResult(new ImportCandidate(paths[remaining], Path.GetFileName(paths[remaining]), 0, null), null, ImportOutcome.Cancelled, [], null));
                }
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException)
            {
                TryDelete(stagingPath);
                TryDelete(managedPath);
                candidate ??= new ImportCandidate(sourcePath, Path.GetFileName(sourcePath), 0, null);
                results.Add(new ImportResult(candidate, null, ImportOutcome.Failed, [], exception.Message));
            }
        }
        return results;
    }

    public Task<IReadOnlyList<MetadataEntry>> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetMetadataAsync(fileId, cancellationToken);
    }

    public Task<MetadataEntry> SetMetadataAsync(string fileId, string key, MetadataValueKind kind, string serializedValue, bool isSensitive, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.SetMetadataAsync(fileId, key, kind, serializedValue, isSensitive, cancellationToken), cancellationToken);
    }

    public Task<BulkFileOperationResult> SetMetadataForFilesAsync(IReadOnlyCollection<string> fileIds, string key, MetadataValueKind kind, string serializedValue, bool isSensitive, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalizedKey = LibraryRules.NormalizeMetadataKey(key);
        var validValue = LibraryRules.ValidateMetadataValue(kind, serializedValue);
        return RunMutationAsync(() => ProcessFilesAsync(fileIds, fileId => _database.SetMetadataAsync(fileId, normalizedKey, kind, validValue, isSensitive, cancellationToken), cancellationToken), cancellationToken);
    }

    public Task RemoveMetadataAsync(string fileId, string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RemoveMetadataAsync(fileId, key, cancellationToken), cancellationToken);
    }

    public Task<BulkFileOperationResult> RemoveMetadataFromFilesAsync(IReadOnlyCollection<string> fileIds, string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalizedKey = LibraryRules.NormalizeMetadataKey(key);
        return RunMutationAsync(() => ProcessFilesAsync(fileIds, fileId => _database.RemoveMetadataAsync(fileId, normalizedKey, cancellationToken), cancellationToken), cancellationToken);
    }

    public Task<MetadataEntry> RenameMetadataAsync(string fileId, string currentKey, string newKey, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RenameMetadataAsync(fileId, currentKey, newKey, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<FileLink>> GetLinksAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetLinksAsync(fileId, cancellationToken);
    }

    public Task<IReadOnlyList<FileLink>> GetRecycledLinksAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetExplicitlyRecycledLinksAsync(cancellationToken);
    }

    public Task<FileLink> CreateLinkAsync(string sourceFileId, string targetFileId, string label, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.CreateLinkAsync(sourceFileId, targetFileId, label, cancellationToken), cancellationToken);
    }

    public Task<FileLink> RelabelLinkAsync(string linkId, string label, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RelabelLinkAsync(linkId, label, cancellationToken), cancellationToken);
    }

    public Task<FileLink> ReverseLinkAsync(string linkId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.ReverseLinkAsync(linkId, cancellationToken), cancellationToken);
    }

    public Task RecycleLinkAsync(string linkId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RecycleLinkAsync(linkId, cancellationToken), cancellationToken);
    }

    public Task RestoreLinkAsync(string linkId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RestoreLinkAsync(linkId, cancellationToken), cancellationToken);
    }

    public Task PermanentlyDeleteLinkAsync(string linkId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.PermanentlyDeleteLinkAsync(linkId, cancellationToken), cancellationToken);
    }

    public Task RecycleFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RecycleFileAsync(fileId, cancellationToken), cancellationToken);
    }

    public Task<BulkFileOperationResult> RecycleFilesAsync(IReadOnlyCollection<string> fileIds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => ProcessFilesAsync(fileIds, fileId => _database.RecycleFileAsync(fileId, cancellationToken), cancellationToken), cancellationToken);
    }

    public Task RecycleFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RecycleFolderAsync(folderId, Descriptor.RootFolderId, Descriptor.GeneratedFolderId, cancellationToken), cancellationToken);
    }

    public Task RestoreFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => RestoreFileCoreAsync(fileId, cancellationToken), cancellationToken);
    }

    private async Task RestoreFileCoreAsync(string fileId, CancellationToken cancellationToken)
    {
        var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        ValidateManagedFilesForRestore([file]);
        await _database.RestoreFileAsync(fileId, cancellationToken).ConfigureAwait(false);
    }

    public Task RestoreFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => RestoreFolderCoreAsync(folderId, cancellationToken), cancellationToken);
    }

    private async Task RestoreFolderCoreAsync(string folderId, CancellationToken cancellationToken)
    {
        var files = await _database.GetFilesOwnedByRecycleBinItemAsync(new RecycleBinItemReference(RecycleBinItemKind.Folder, folderId), cancellationToken).ConfigureAwait(false);
        ValidateManagedFilesForRestore(files);
        await _database.RestoreFolderAsync(folderId, cancellationToken).ConfigureAwait(false);
    }

    public Task PermanentlyDeleteFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => PermanentlyDeleteFileCoreAsync(fileId, cancellationToken), cancellationToken);
    }

    private async Task PermanentlyDeleteFileCoreAsync(string fileId, CancellationToken cancellationToken)
    {
        var file = await _database.PrepareFileDeletionAsync(fileId, cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _layout.ManagedFilePath(file.ManagedName);
            DeleteManagedFile(path);
            await _database.DeleteFileRecordAsync(fileId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SlopFactoryException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            var sanitizedError = SanitizeRecycleBinError(exception);
            await TryRecordPermanentDeletionFailureAsync(new RecycleBinItemReference(RecycleBinItemKind.File, fileId), sanitizedError).ConfigureAwait(false);
            if (exception is Microsoft.Data.Sqlite.SqliteException) throw new LibraryValidationException(sanitizedError);
            throw;
        }
    }

    public Task PermanentlyDeleteFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => PermanentlyDeleteFolderCoreAsync(folderId, cancellationToken), cancellationToken);
    }

    private async Task PermanentlyDeleteFolderCoreAsync(string folderId, CancellationToken cancellationToken)
    {
        if (folderId == Descriptor.RootFolderId || folderId == Descriptor.GeneratedFolderId) throw new LibraryValidationException("Permanent library folders cannot be deleted.");
        var files = await _database.PrepareFolderDeletionAsync(folderId, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteManagedFile(_layout.ManagedFilePath(file.ManagedName));
            }
            await _database.DeleteFolderRecordAsync(folderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SlopFactoryException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            var sanitizedError = SanitizeRecycleBinError(exception);
            await TryRecordPermanentDeletionFailureAsync(new RecycleBinItemReference(RecycleBinItemKind.Folder, folderId), sanitizedError).ConfigureAwait(false);
            if (exception is Microsoft.Data.Sqlite.SqliteException) throw new LibraryValidationException(sanitizedError);
            throw;
        }
    }

    public Task<RecycleBinOperationResult> RestoreRecycleBinItemsAsync(IReadOnlyCollection<RecycleBinItemReference> items, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => ProcessRecycleBinItemsAsync(items, restore: true, cancellationToken), cancellationToken);
    }

    public async Task<RecycleBinRestorePreview> GetRecycleBinRestorePreviewAsync(IReadOnlyCollection<RecycleBinItemReference> items, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(items);
        var references = items.Distinct().ToArray();
        var entries = (await GetRecycleBinEntriesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(entry => entry.Reference);
        var ownedFiles = new Dictionary<RecycleBinItemReference, IReadOnlyList<FileRecord>>();
        foreach (var reference in references)
        {
            if (!entries.ContainsKey(reference)) throw new RecordNotFoundException("The selected recycle-bin item is no longer available.");
            ownedFiles[reference] = await _database.GetFilesOwnedByRecycleBinItemAsync(reference, cancellationToken).ConfigureAwait(false);
        }

        var blockers = new Dictionary<RecycleBinItemReference, List<string>>();
        var restorableSelectedFileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references.Where(reference => reference.Kind != RecycleBinItemKind.FileLink))
        {
            var reasons = (await _database.GetRestoreBlockersAsync(reference, new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false)).ToList();
            foreach (var file in ownedFiles[reference])
            {
                var fileBlocker = GetManagedFileRestoreBlocker(file);
                if (fileBlocker is not null) reasons.Add(fileBlocker);
            }
            blockers[reference] = reasons;
            if (reasons.Count == 0) restorableSelectedFileIds.UnionWith(ownedFiles[reference].Select(file => file.Id));
        }
        foreach (var reference in references.Where(reference => reference.Kind == RecycleBinItemKind.FileLink))
        {
            blockers[reference] = (await _database.GetRestoreBlockersAsync(reference, restorableSelectedFileIds, cancellationToken).ConfigureAwait(false)).ToList();
        }

        var previews = references.Select(reference =>
        {
            var entry = entries[reference];
            var effects = new List<string>();
            effects.Add(reference.Kind switch
            {
                RecycleBinItemKind.Folder => $"Restores {entry.OwnedFolderCount} folder(s) and {entry.OwnedFileCount} file(s) at their original locations.",
                RecycleBinItemKind.File => "Restores the file and its attached metadata at the original location.",
                _ => "Restores the directed file link after both endpoint files are active."
            });
            if (reference.Kind != RecycleBinItemKind.FileLink && entry.OwnedLinkCount > 0)
            {
                effects.Add($"Up to {entry.OwnedLinkCount} endpoint-owned link(s) may reactivate when both endpoints are available; explicitly recycled links remain recycled.");
            }
            return new RecycleBinRestorePreviewItem(entry, blockers.GetValueOrDefault(reference, []), effects);
        }).ToArray();
        return new RecycleBinRestorePreview(previews);
    }

    public Task<RecycleBinOperationResult> PermanentlyDeleteRecycleBinItemsAsync(IReadOnlyCollection<RecycleBinItemReference> items, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => ProcessRecycleBinItemsAsync(items, restore: false, cancellationToken), cancellationToken);
    }

    public Task<RecycleBinOperationResult> EmptyRecycleBinAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => EmptyRecycleBinCoreAsync(cancellationToken), cancellationToken);
    }

    private async Task<RecycleBinOperationResult> EmptyRecycleBinCoreAsync(CancellationToken cancellationToken)
    {
        var entries = await GetRecycleBinEntriesAsync(cancellationToken).ConfigureAwait(false);
        return await ProcessRecycleBinItemsAsync(entries.Select(entry => entry.Reference).ToArray(), restore: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibraryIntegrityReport> RunIntegrityScanAsync(IProgress<LibraryIntegrityScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var startedAt = DateTimeOffset.UtcNow;
        var findings = new List<LibraryIntegrityFinding>();
        var processed = 0;
        var total = 4;
        var complete = true;
        var cancelled = false;
        var mutationGateHeld = false;

        try
        {
            progress?.Report(new LibraryIntegrityScanProgress(processed, total, "Waiting for active library changes"));
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            mutationGateHeld = true;
            progress?.Report(new LibraryIntegrityScanProgress(processed, total, "Validating manifest"));
            try
            {
                var manifest = await LibraryManifestStore.ReadAsync(_layout, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(manifest.LibraryId, Descriptor.LibraryId, StringComparison.Ordinal) || manifest.SchemaVersion != Descriptor.SchemaVersion)
                {
                    findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.ManifestInvalid, null, null, null, "The manifest identity does not match the open library."));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException)
            {
                findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.ManifestInvalid, null, null, null, "The library manifest could not be validated."));
            }
            progress?.Report(new LibraryIntegrityScanProgress(++processed, total, "Validating database"));

            IReadOnlyList<FileRecord> files = [];
            var databaseRecordsAvailable = false;
            try
            {
                var databaseFindings = await _database.CheckIntegrityAsync(cancellationToken).ConfigureAwait(false);
                if (databaseFindings.Count > 0)
                {
                    findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.DatabaseInvalid, null, null, null, $"SQLite reported {databaseFindings.Count} integrity issue(s)."));
                    complete = false;
                }
                else
                {
                    files = await _database.GetFilesForIntegrityScanAsync(cancellationToken).ConfigureAwait(false);
                    databaseRecordsAvailable = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.DatabaseInvalid, null, null, null, "The library database could not be validated."));
                complete = false;
            }
            progress?.Report(new LibraryIntegrityScanProgress(++processed, total, "Validating required directories"));

            if (!Directory.Exists(_layout.ManagedPath))
            {
                findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.RequiredDirectoryMissing, null, null, null, "The managed media directory is missing."));
                complete = false;
            }
            if (!Directory.Exists(_layout.StagingPath))
            {
                findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.RequiredDirectoryMissing, null, null, null, "The staging directory is missing."));
            }
            progress?.Report(new LibraryIntegrityScanProgress(++processed, total, "Enumerating managed storage"));

            string[] managedEntries = [];
            if (Directory.Exists(_layout.ManagedPath))
            {
                try
                {
                    managedEntries = Directory.EnumerateFileSystemEntries(_layout.ManagedPath, "*", SearchOption.TopDirectoryOnly).ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.ManagedFileInaccessible, null, null, null, "Managed storage could not be enumerated."));
                    complete = false;
                }
            }
            processed++;
            total = processed + files.Count + managedEntries.Length;
            var knownManagedNames = files.Select(file => file.ManagedName).ToHashSet(StringComparer.Ordinal);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new LibraryIntegrityScanProgress(processed, total, "Hashing managed files"));
                var path = _layout.ManagedFilePath(file.ManagedName);
                try
                {
                    if (Directory.Exists(path))
                    {
                        findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.UnsafeManagedEntry, file.Id, file.ByteSize, null, "The recorded managed path is a directory instead of a regular file."));
                    }
                    else if (!File.Exists(path))
                    {
                        findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.ManagedFileMissing, file.Id, file.ByteSize, null, "The recorded managed file is missing."));
                    }
                    else
                    {
                        var info = new FileInfo(path);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.UnsafeManagedEntry, file.Id, file.ByteSize, null, "The recorded managed path is a symbolic link or reparse point."));
                        }
                        else
                        {
                            var actualSize = info.Length;
                            if (actualSize != file.ByteSize)
                            {
                                findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.ManagedFileSizeMismatch, file.Id, file.ByteSize, actualSize, "The managed file size differs from its database record."));
                            }
                            var actualHash = await Hashing.Sha256Async(path, cancellationToken).ConfigureAwait(false);
                            if (!string.Equals(actualHash, file.ContentHash, StringComparison.Ordinal))
                            {
                                findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.ManagedFileHashMismatch, file.Id, file.ByteSize, actualSize, "The managed file content hash differs from its database record."));
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.ManagedFileInaccessible, file.Id, file.ByteSize, null, "The recorded managed file could not be read safely."));
                }
                processed++;
            }

            foreach (var path in managedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new LibraryIntegrityScanProgress(processed, total, "Checking for orphan files"));
                var name = Path.GetFileName(path);
                if (databaseRecordsAvailable && !knownManagedNames.Contains(name))
                {
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0 || Directory.Exists(path))
                        {
                            findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.UnsafeManagedEntry, null, null, null, "Managed storage contains an unrecorded directory or redirected entry."));
                        }
                        else
                        {
                            findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.OrphanManagedFile, null, null, new FileInfo(path).Length, "Managed storage contains a regular file with no database record."));
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.ManagedFileInaccessible, null, null, null, "An unrecorded managed-storage entry could not be inspected safely."));
                    }
                }
                processed++;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            complete = false;
            cancelled = true;
        }
        finally
        {
            if (mutationGateHeld) _mutationGate.Release();
        }

        progress?.Report(new LibraryIntegrityScanProgress(processed, Math.Max(total, processed), cancelled ? "Scan cancelled" : "Scan finished"));
        return new LibraryIntegrityReport(Descriptor.LibraryId, Descriptor.SchemaVersion, startedAt, DateTimeOffset.UtcNow, complete && !cancelled, cancelled, findings);
    }

    public Task ValidateOpenLibraryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => ValidateOpenLibraryCoreAsync(cancellationToken), cancellationToken);
    }

    private async Task ValidateOpenLibraryCoreAsync(CancellationToken cancellationToken)
    {
        _layout.ValidateRequiredEntries();
        var manifest = await LibraryManifestStore.ReadAsync(_layout, cancellationToken).ConfigureAwait(false);
        if (manifest != _manifest) throw new LibraryValidationException("The library manifest changed outside SlopFactory.");
        var descriptor = await _database.ValidateAndDescribeAsync(manifest, _layout.RootPath, cancellationToken).ConfigureAwait(false);
        if (descriptor != Descriptor) throw new LibraryValidationException("The library database identity changed outside SlopFactory.");
        var integrityFindings = await _database.CheckIntegrityAsync(cancellationToken).ConfigureAwait(false);
        if (integrityFindings.Count > 0) throw new LibraryValidationException("The library database no longer passes its integrity check.");
    }

    public Task RenameLibraryAsync(string displayName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => RenameLibraryCoreAsync(displayName, cancellationToken), cancellationToken);
    }

    private async Task RenameLibraryCoreAsync(string displayName, CancellationToken cancellationToken)
    {
        var normalized = LibraryRules.NormalizeDisplayName(displayName, "Library name");
        var updatedManifest = _manifest with { DisplayName = normalized };
        await _database.RenameLibraryAsync(normalized, cancellationToken).ConfigureAwait(false);
        try
        {
            await LibraryManifestStore.WriteAsync(_layout, updatedManifest, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _database.RenameLibraryAsync(_manifest.DisplayName, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        _manifest = updatedManifest;
        Descriptor = Descriptor with { DisplayName = normalized };
    }

    public string GetManagedFilePath(FileRecord file)
    {
        ThrowIfDisposed();
        var path = _layout.ManagedFilePath(file.ManagedName);
        if (Directory.Exists(path) || !File.Exists(path)) throw new FileNotFoundException("The managed file is missing or is not a regular file.", path);
        if ((new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("The managed file path is a symbolic link or reparse point.");
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _libraryLock.Dispose();
            TryDelete(_layout.LockPath);
        }
        finally
        {
            _mutationGate.Release();
            _mutationGate.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private async Task<BulkFileOperationResult> ProcessFilesAsync(IReadOnlyCollection<string> fileIds, Func<string, Task> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0) throw new LibraryValidationException("Select at least one file.");
        var distinctIds = fileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctIds.Length != fileIds.Count) throw new LibraryValidationException("The file selection contains an invalid or duplicate item.");

        var results = new List<BulkFileOperationItemResult>(distinctIds.Length);
        foreach (var fileId in distinctIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileRecord? file = null;
            try
            {
                file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
                if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Bulk actions require active files.");
                await operation(fileId).ConfigureAwait(false);
                results.Add(new BulkFileOperationItemResult(file.Id, file.DisplayName, true, null));
            }
            catch (Exception exception) when (exception is SlopFactoryException or IOException or UnauthorizedAccessException)
            {
                results.Add(new BulkFileOperationItemResult(fileId, file?.DisplayName ?? "Unavailable file", false, exception.Message));
            }
        }
        return new BulkFileOperationResult(results);
    }

    private async Task RunMutationAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await operation().ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
    }

    private async Task<T> RunMutationAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation().ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void DeleteManagedFile(string path)
    {
        if (Directory.Exists(path)) throw new IOException("The managed file path was replaced by a directory; deletion is paused for review.");
        if (!File.Exists(path)) return;
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The managed file path is a symbolic link or reparse point; deletion is paused for review.");
        File.Delete(path);
    }

    private void ValidateManagedFilesForRestore(IEnumerable<FileRecord> files)
    {
        foreach (var file in files)
        {
            var blocker = GetManagedFileRestoreBlocker(file);
            if (blocker is not null) throw new LibraryValidationException(blocker);
        }
    }

    private string? GetManagedFileRestoreBlocker(FileRecord file)
    {
        try
        {
            var path = _layout.ManagedFilePath(file.ManagedName);
            if (Directory.Exists(path)) return $"Managed content for '{file.DisplayName}' was replaced by a directory and cannot be restored.";
            if (!File.Exists(path)) return $"Managed content for '{file.DisplayName}' is missing and cannot be restored.";
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return $"Managed content for '{file.DisplayName}' is a symbolic link or reparse point and cannot be restored.";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return $"Managed content for '{file.DisplayName}' cannot be accessed and cannot be restored.";
        }
        catch (IOException)
        {
            return $"Managed content for '{file.DisplayName}' is unavailable and cannot be restored.";
        }
    }

    private async Task<RecycleBinOperationResult> ProcessRecycleBinItemsAsync(IReadOnlyCollection<RecycleBinItemReference> items, bool restore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var entries = await GetRecycleBinEntriesAsync(cancellationToken).ConfigureAwait(false);
        var names = entries.ToDictionary(entry => entry.Reference, entry => entry.Name);
        var references = items.Distinct().OrderBy(reference => OperationOrder(reference.Kind, restore)).ThenBy(reference => reference.Id, StringComparer.Ordinal).ToArray();
        var results = new List<RecycleBinOperationItemResult>(references.Length);
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = names.GetValueOrDefault(reference, reference.Id);
            try
            {
                if (restore)
                {
                    switch (reference.Kind)
                    {
                        case RecycleBinItemKind.Folder: await RestoreFolderCoreAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        case RecycleBinItemKind.File: await RestoreFileCoreAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        case RecycleBinItemKind.FileLink: await _database.RestoreLinkAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        default: throw new LibraryValidationException("The recycle-bin item type is not supported.");
                    }
                }
                else
                {
                    switch (reference.Kind)
                    {
                        case RecycleBinItemKind.Folder: await PermanentlyDeleteFolderCoreAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        case RecycleBinItemKind.File: await PermanentlyDeleteFileCoreAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        case RecycleBinItemKind.FileLink: await _database.PermanentlyDeleteLinkAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        default: throw new LibraryValidationException("The recycle-bin item type is not supported.");
                    }
                }
                results.Add(new RecycleBinOperationItemResult(reference, name, true, null));
            }
            catch (Exception exception) when (exception is SlopFactoryException or IOException or UnauthorizedAccessException)
            {
                results.Add(new RecycleBinOperationItemResult(reference, name, false, SanitizeRecycleBinError(exception)));
            }
        }
        return new RecycleBinOperationResult(results);
    }

    private static int OperationOrder(RecycleBinItemKind kind, bool restore) => (restore, kind) switch
    {
        (true, RecycleBinItemKind.FileLink) => 1,
        (false, RecycleBinItemKind.FileLink) => 0,
        (false, _) => 1,
        _ => 0
    };

    private static string SanitizeRecycleBinError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Access was denied while processing managed content.",
        IOException when exception.Message.StartsWith("The managed file path", StringComparison.Ordinal) => exception.Message,
        IOException => "Managed content is in use or unavailable.",
        Microsoft.Data.Sqlite.SqliteException => "The library database could not finalize permanent deletion.",
        SlopFactoryException => exception.Message,
        _ => "Permanent deletion could not be completed."
    };

    private async Task TryRecordPermanentDeletionFailureAsync(RecycleBinItemReference reference, string sanitizedError)
    {
        try
        {
            await _database.RecordPermanentDeletionFailureAsync(reference, sanitizedError, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original deletion failure when its diagnostic record cannot be written.
        }
    }
}
