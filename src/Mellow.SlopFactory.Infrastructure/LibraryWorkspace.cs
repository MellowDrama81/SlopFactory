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
    private readonly IExportCleanupJournal? _exportCleanupJournal;
    private readonly IExportFaultInjector _exportFaultInjector;
    private LibraryManifest _manifest;
    private bool _disposed;

    public LibraryWorkspace(LibraryLayout layout, LibraryDescriptor descriptor, LibraryManifest manifest, SqliteLibraryDatabase database, FileStream libraryLock, IExportCleanupJournal? exportCleanupJournal = null, IExportFaultInjector? exportFaultInjector = null)
    {
        _layout = layout;
        Descriptor = descriptor;
        _manifest = manifest;
        _database = database;
        _libraryLock = libraryLock;
        _exportCleanupJournal = exportCleanupJournal;
        _exportFaultInjector = exportFaultInjector ?? NullExportFaultInjector.Instance;
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

    public Task<FileDerivationProvenance?> GetFileDerivationProvenanceAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetFileDerivationProvenanceAsync(fileId, cancellationToken);
    }

    public async Task<IReadOnlyList<FileDerivationChainEntry>> GetFileDerivationChainAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var entries = new List<FileDerivationChainEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        for (var depth = 0; depth < 128 && seen.Add(current.Id); depth++)
        {
            var provenance = await _database.GetFileDerivationProvenanceAsync(current.Id, cancellationToken).ConfigureAwait(false);
            entries.Add(new FileDerivationChainEntry(current, current.State == LibraryRecordState.Active ? provenance?.Origin : null));
            if (current.State != LibraryRecordState.Active) break;
            if (provenance?.SourceFileId is not { } sourceId) break;
            try
            {
                current = await _database.GetFileAsync(sourceId, cancellationToken).ConfigureAwait(false);
                if (current.State != LibraryRecordState.Active) break;
            }
            catch (LibraryValidationException) { break; }
        }
        return entries;
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
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ManagedFileSafety.HasMultipleLinks(path))
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
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ManagedFileSafety.HasMultipleLinks(path) || info.Length != file.ByteSize || !string.Equals(await Hashing.Sha256Async(path, cancellationToken).ConfigureAwait(false), file.ContentHash, StringComparison.Ordinal))
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

    public async Task<ChangedContentInspection> InspectChangedContentAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active || file.ContentState != FileContentState.Changed) throw new LibraryValidationException("Only changed active managed content can be inspected.");
        var path = _layout.ManagedFilePath(file.ManagedName);
        if (Directory.Exists(path) || !File.Exists(path)) throw new LibraryValidationException("Changed content is no longer available to inspect.");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ManagedFileSafety.HasMultipleLinks(path)) throw new LibraryValidationException("Changed content is redirected or hard-linked and cannot be inspected safely.");
        var hash = await Hashing.Sha256Async(path, cancellationToken).ConfigureAwait(false);
        var mediaType = (await MediaTypeDetector.DetectAsync(path, cancellationToken).ConfigureAwait(false)).MediaType;
        return new ChangedContentInspection(file, hash, info.Length, mediaType);
    }

    public async Task<TextFileContent> ReadChangedTextFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var inspection = await InspectChangedContentAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (!IsTextMediaType(inspection.ActualMediaType)) throw new LibraryValidationException("The changed bytes are not a supported text format.");
        var path = _layout.ManagedFilePath(inspection.File.ManagedName);
        return await ReadTextContentAsync(path, cancellationToken).ConfigureAwait(false);
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
                if ((existing.Attributes & FileAttributes.ReparsePoint) != 0 || ManagedFileSafety.HasMultipleLinks(managedPath)) throw new LibraryValidationException("The managed path is redirected or hard-linked and cannot be replaced safely.");
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
        var file = await GetVerifiedContentFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only an active file can be viewed.");
        if (!IsTextMediaType(file.MediaType)) throw new LibraryValidationException("This file is not a supported text format.");
        return await ReadTextContentAsync(GetManagedFilePath(file), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TextFileContent> ReadTextContentAsync(string path, CancellationToken cancellationToken)
    {
        const int maximumDisplayedCharacters = 1_048_576;
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

    public async Task<ImageTechnicalProperties> GetImageTechnicalPropertiesAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await GetVerifiedContentFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (!IsImageMediaType(file.MediaType)) throw new LibraryValidationException("Technical image properties are available only for supported images.");
        if (file.MediaType == "image/svg+xml") return new ImageTechnicalProperties(null, null);
        var path = GetManagedFilePath(file);
        var probeLength = (int)Math.Min(file.ByteSize, 1_048_576);
        var bytes = new byte[probeLength];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        var (width, height) = ImageSafetyInspector.ReadDimensions(bytes.AsSpan(0, read), file.MediaType);
        var orientation = ImageSafetyInspector.ReadOrientation(bytes.AsSpan(0, read), file.MediaType);
        return new ImageTechnicalProperties(width, height, orientation);
    }

    public async Task<MediaTechnicalProperties> GetMediaTechnicalPropertiesAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await GetVerifiedContentFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (!IsPlayableMediaType(file.MediaType)) throw new LibraryValidationException("Technical media properties are available only for supported audio and video files.");
        return await MediaTechnicalInspector.InspectAsync(ValidateRegularManagedFile(file), file.MediaType, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileSystemMetadata> GetSystemMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        var properties = new List<SystemMetadataProperty>
        {
            new("mediaType", "Detected media type", file.MediaType),
            new("byteSize", "Byte size", file.ByteSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("contentState", "Content state", file.ContentState.ToString())
        };
        if (IsImageMediaType(file.MediaType) && ContentActionPolicy.CanUseManagedContent(file))
        {
            var image = await GetImageTechnicalPropertiesAsync(fileId, cancellationToken).ConfigureAwait(false);
            properties.Add(new("width", "Width", image.Width?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            properties.Add(new("height", "Height", image.Height?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            properties.Add(new("orientation", "Orientation", image.Orientation?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        else if (IsPlayableMediaType(file.MediaType) && ContentActionPolicy.CanUseManagedContent(file))
        {
            var media = await GetMediaTechnicalPropertiesAsync(fileId, cancellationToken).ConfigureAwait(false);
            properties.AddRange(new[]
            {
                new SystemMetadataProperty("duration", "Duration", media.Duration?.ToString()),
                new SystemMetadataProperty("audioCodec", "Audio codec", media.AudioCodec),
                new SystemMetadataProperty("videoCodec", "Video codec", media.VideoCodec),
                new SystemMetadataProperty("channels", "Channels", media.ChannelCount?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new SystemMetadataProperty("sampleRate", "Sample rate", media.SampleRate?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new SystemMetadataProperty("frameRate", "Frame rate", media.FrameRate?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new SystemMetadataProperty("width", "Width", media.Width?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new SystemMetadataProperty("height", "Height", media.Height?.ToString(System.Globalization.CultureInfo.InvariantCulture))
            });
        }
        return new FileSystemMetadata(file.Id, properties);
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
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ManagedFileSafety.HasMultipleLinks(path)) throw new LibraryValidationException("Redirected or hard-linked managed media files cannot be played.");
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

    public Task<BulkFileOperationResult> DuplicateFilesAsync(IReadOnlyCollection<string> fileIds, string destinationFolderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => DuplicateFilesCoreAsync(fileIds, destinationFolderId, cancellationToken), cancellationToken);
    }

    public Task<BulkFileOperationResult> DuplicateFilesWithProgressAsync(IReadOnlyCollection<string> fileIds, string destinationFolderId, IProgress<BulkDuplicateProgress>? progress, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => DuplicateFilesCoreAsync(fileIds, destinationFolderId, cancellationToken, progress), cancellationToken);
    }

    private async Task<BulkFileOperationResult> DuplicateFilesCoreAsync(IReadOnlyCollection<string> fileIds, string destinationFolderId, CancellationToken cancellationToken, IProgress<BulkDuplicateProgress>? progress = null)
    {
        var destination = await _database.GetFolderContentsAsync(destinationFolderId, cancellationToken).ConfigureAwait(false);
        if (destination.Folder.State != LibraryRecordState.Active) throw new LibraryValidationException("The duplicate destination folder must be active.");
        var ordered = fileIds.ToArray();
        var results = new List<BulkFileOperationItemResult>();
        for (var index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileId = ordered[index];
            var source = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
            progress?.Report(new BulkDuplicateProgress(index + 1, ordered.Length, source.DisplayName, false));
            try
            {
                if (source.ContentState is FileContentState.Missing or FileContentState.Changed) throw new LibraryValidationException("Only files with available managed content can be duplicated.");
                var name = await _database.ResolveAvailableFileNameAsync(destinationFolderId, source.DisplayName, cancellationToken).ConfigureAwait(false);
                _ = await DuplicateFileCoreAsync(fileId, destinationFolderId, name, cancellationToken).ConfigureAwait(false);
                results.Add(new BulkFileOperationItemResult(fileId, source.DisplayName, true, null));
            }
            catch (Exception exception) when (exception is SlopFactoryException or IOException or UnauthorizedAccessException)
            {
                results.Add(new BulkFileOperationItemResult(fileId, source.DisplayName, false, exception.Message));
            }
            progress?.Report(new BulkDuplicateProgress(index + 1, ordered.Length, source.DisplayName, true));
        }
        return new BulkFileOperationResult(results);
    }

    private async Task<FileRecord> DuplicateFileCoreAsync(string fileId, string destinationFolderId, string displayName, CancellationToken cancellationToken)
    {
        var normalizedName = LibraryRules.NormalizeDisplayName(displayName, "File name");
        var source = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (source.State != LibraryRecordState.Active || source.ContentState is FileContentState.Missing or FileContentState.Changed) throw new LibraryValidationException("Only an active file with available managed content can be duplicated.");

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
                var info = ValidateImportSource(sourcePath);
                var displayName = LibraryRules.NormalizeDisplayName(info.Name, "File name");
                candidate = new ImportCandidate(info.FullName, displayName, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), WindowsZoneClassifier.Read(info.FullName));
                EnsureImportStorageAvailable(info.Length);
                progress?.Report(new ImportProgress(itemIndex + 1, paths.Length, displayName, "Hashing source", 0, info.Length));
                var hash = await Hashing.Sha256Async(info.FullName, cancellationToken, bytes => progress?.Report(new ImportProgress(itemIndex + 1, paths.Length, displayName, "Hashing source", bytes, info.Length))).ConfigureAwait(false);
                var revalidated = ValidateImportSource(info.FullName);
                if (revalidated.Length != info.Length || revalidated.LastWriteTimeUtc != info.LastWriteTimeUtc)
                {
                    throw new IOException("The selected source file changed while it was being prepared for import.");
                }
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

                if (OperatingSystem.IsWindows()) File.SetAttributes(stagingPath, FileAttributes.Normal);
                else File.SetUnixFileMode(stagingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

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

    private static FileInfo ValidateImportSource(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new LibraryValidationException("An import source path is required.");
        var fullPath = Path.GetFullPath(sourcePath);
        if (Directory.Exists(fullPath)) throw new LibraryValidationException("Folders cannot be imported as individual files.");
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("The selected source file no longer exists.", fullPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("Redirected or symbolic-link source files cannot be imported directly.");
        return info;
    }

    private void EnsureImportStorageAvailable(long requiredBytes)
    {
        if (requiredBytes < 0) throw new LibraryValidationException("The selected source file has an invalid size.");
        long available;
        try
        {
            var root = Path.GetPathRoot(_layout.RootPath);
            if (string.IsNullOrWhiteSpace(root)) return;
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Capacity checks are advisory. The copy itself remains authoritative when the platform cannot report free space.
            return;
        }
        if (available < requiredBytes)
        {
            throw new IOException($"There is not enough available storage to import this file. At least {requiredBytes:N0} bytes are required for the managed copy.");
        }
    }

    public async Task<RecursiveImportInventory> BuildRecursiveImportInventoryAsync(IEnumerable<string> sourcePaths, bool includeHiddenFiles = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var candidates = new List<ImportSourceSnapshot>();
        var skipped = new Dictionary<ImportInventorySkipReason, int>();
        var folders = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        const int maximumEntries = 100_000;

        void Skip(ImportInventorySkipReason reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

        foreach (var selectedPath in sourcePaths.Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(selectedPath))
            {
                AddFile(selectedPath, string.Empty);
                continue;
            }
            if (!Directory.Exists(selectedPath)) { Skip(ImportInventorySkipReason.Inaccessible); continue; }
            var rootInfo = new DirectoryInfo(selectedPath);
            if (IsAlwaysExcluded(rootInfo.Attributes)) { Skip(ImportInventorySkipReason.RedirectedOrReparse); continue; }
            var rootRelative = LibraryRules.NormalizeDisplayName(rootInfo.Name, "Imported folder name");
            var pending = new Stack<(string Path, string Relative, int Depth)>();
            pending.Push((rootInfo.FullName, rootRelative, 0));
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                if (current.Depth > 64 || candidates.Count >= maximumEntries) { Skip(ImportInventorySkipReason.LimitExceeded); continue; }
                folders.Add(current.Relative);
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(current.Path).ToArray(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Skip(ImportInventorySkipReason.Inaccessible); continue; }
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Skip(ImportInventorySkipReason.Inaccessible); continue; }
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) { Skip((attributes & FileAttributes.ReparsePoint) != 0 ? ImportInventorySkipReason.RedirectedOrReparse : ImportInventorySkipReason.ProtectedOrSystem); continue; }
                    if ((attributes & FileAttributes.Hidden) != 0 && !includeHiddenFiles) { Skip(ImportInventorySkipReason.Hidden); continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push((entry, Path.Combine(current.Relative, Path.GetFileName(entry)), current.Depth + 1));
                    }
                    else AddFile(entry, current.Relative);
                }
            }
        }

        var hashed = new List<(ImportSourceSnapshot Snapshot, string Hash)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = await Hashing.Sha256Async(candidate.SourcePath, cancellationToken).ConfigureAwait(false);
            hashed.Add((candidate with { ContentHash = hash }, hash));
        }
        candidates = hashed.Select(item => item.Snapshot).ToList();
        var duplicateGroups = new List<ImportDuplicateGroup>();
        foreach (var group in hashed.GroupBy(item => (item.Snapshot.ByteSize, item.Hash)).Where(group => group.Count() > 1))
        {
            var matches = await _database.FindByHashAsync(group.Key.Hash, group.Key.ByteSize, cancellationToken).ConfigureAwait(false);
            duplicateGroups.Add(new ImportDuplicateGroup(group.Key.ByteSize, group.Key.Hash, group.Select(item => item.Snapshot.SourcePath).ToArray(), matches));
        }
        foreach (var group in hashed.GroupBy(item => (item.Snapshot.ByteSize, item.Hash)).Where(group => group.Count() == 1))
        {
            var matches = await _database.FindByHashAsync(group.Key.Hash, group.Key.ByteSize, cancellationToken).ConfigureAwait(false);
            if (matches.Count > 0) duplicateGroups.Add(new ImportDuplicateGroup(group.Key.ByteSize, group.Key.Hash, group.Select(item => item.Snapshot.SourcePath).ToArray(), matches));
        }
        var conflicts = candidates.GroupBy(candidate => Path.Combine(candidate.RelativeFolder, candidate.DisplayName), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var inventoryId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', candidates.OrderBy(item => item.SourcePath, StringComparer.Ordinal).Select(item => $"{item.SourcePath}\0{item.ByteSize}\0{item.LastWriteTime:O}")))));
        return new RecursiveImportInventory(inventoryId, DateTimeOffset.UtcNow, candidates, folders.OrderBy(value => value, StringComparer.Ordinal).ToArray(), duplicateGroups, skipped, conflicts, Descriptor.LibraryId);

        void AddFile(string path, string relative)
        {
            if (candidates.Count >= maximumEntries) { Skip(ImportInventorySkipReason.LimitExceeded); return; }
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { Skip(ImportInventorySkipReason.Inaccessible); return; }
                if (IsAlwaysExcluded(info.Attributes)) { Skip((info.Attributes & FileAttributes.ReparsePoint) != 0 ? ImportInventorySkipReason.RedirectedOrReparse : ImportInventorySkipReason.ProtectedOrSystem); return; }
                if ((info.Attributes & FileAttributes.Hidden) != 0 && !includeHiddenFiles) { Skip(ImportInventorySkipReason.Hidden); return; }
                candidates.Add(new ImportSourceSnapshot(info.FullName, LibraryRules.NormalizeDisplayName(info.Name, "File name"), relative, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), null, WindowsZoneClassifier.Read(info.FullName)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or SlopFactoryException) { Skip(ImportInventorySkipReason.Inaccessible); }
        }

        static bool IsAlwaysExcluded(FileAttributes attributes) => (attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Device)) != 0;
    }

    public Task<IReadOnlyList<ImportResult>> ImportConfirmedInventoryAsync(RecursiveImportInventory inventory, IReadOnlyList<ConfirmedImportCandidate> candidates, string destinationFolderId, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => ImportConfirmedInventoryCoreAsync(inventory, candidates, destinationFolderId, progress, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<ImportResult>> ImportConfirmedInventoryCoreAsync(RecursiveImportInventory inventory, IReadOnlyList<ConfirmedImportCandidate> candidates, string destinationFolderId, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(candidates);
        if (inventory.LibraryId is not null && !string.Equals(inventory.LibraryId, Descriptor.LibraryId, StringComparison.Ordinal)) throw new LibraryValidationException("An import inventory cannot be committed to a different library.");
        var frozen = inventory.Candidates.ToDictionary(item => item.SourcePath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (candidates.Any(item => !frozen.ContainsKey(item.Snapshot.SourcePath))) throw new LibraryValidationException("The confirmed import contains a source that was not in the reviewed inventory.");
        var results = new List<ImportResult>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var confirmed = candidates[index];
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = frozen[confirmed.Snapshot.SourcePath];
            try
            {
                var current = ValidateImportSource(snapshot.SourcePath);
                if (current.Length != snapshot.ByteSize)
                    throw new IOException("The selected source changed after import review.");
                if (new DateTimeOffset(current.LastWriteTimeUtc, TimeSpan.Zero) != snapshot.LastWriteTime)
                {
                    var currentHash = await Hashing.Sha256Async(current.FullName, cancellationToken).ConfigureAwait(false);
                    if (snapshot.ContentHash is null || !string.Equals(currentHash, snapshot.ContentHash, StringComparison.Ordinal)) throw new IOException("The selected source changed after import review.");
                }
                if (confirmed.DuplicateChoice == ImportDuplicateChoice.RestoreExisting)
                {
                    if (confirmed.ExistingFileId is null) throw new LibraryValidationException("Choose a recycled duplicate to restore.");
                    var existing = await _database.GetFileAsync(confirmed.ExistingFileId, cancellationToken).ConfigureAwait(false);
                    if (existing.State != LibraryRecordState.Recycled) throw new LibraryValidationException("Only a recycled duplicate can be restored during import.");
                    var restoration = await GetRecycleBinRestorePreviewAsync([new RecycleBinItemReference(RecycleBinItemKind.File, existing.Id)], cancellationToken).ConfigureAwait(false);
                    if (!restoration.Items.Single().CanRestore) throw new LibraryValidationException("The recycled duplicate cannot be restored until its normal restoration conflicts are resolved.");
                    await RestoreFileCoreAsync(existing.Id, cancellationToken).ConfigureAwait(false);
                    results.Add(new ImportResult(new ImportCandidate(snapshot.SourcePath, snapshot.DisplayName, snapshot.ByteSize, snapshot.LastWriteTime, snapshot.SourceZone), existing with { State = LibraryRecordState.Active }, ImportOutcome.DuplicateSkipped, [existing], null));
                    continue;
                }
                var target = await ResolveInventoryFolderAsync(destinationFolderId, snapshot.RelativeFolder, cancellationToken).ConfigureAwait(false);
                var itemResult = (await ImportCoreAsync([snapshot.SourcePath], target.FolderId, confirmed.DuplicateChoice == ImportDuplicateChoice.ImportAnyway, progress, true, cancellationToken).ConfigureAwait(false)).Single();
                results.Add(itemResult);
                if (itemResult.Outcome != ImportOutcome.Imported)
                {
                    foreach (var createdId in target.CreatedFolderIds.Reverse()) await _database.DeleteEmptyActiveFolderAsync(createdId, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException)
            {
                results.Add(new ImportResult(new ImportCandidate(snapshot.SourcePath, snapshot.DisplayName, snapshot.ByteSize, snapshot.LastWriteTime, snapshot.SourceZone), null, ImportOutcome.Failed, [], exception.Message));
            }
        }
        return results;
    }

    private async Task<(string FolderId, IReadOnlyList<string> CreatedFolderIds)> ResolveInventoryFolderAsync(string destinationFolderId, string relativeFolder, CancellationToken cancellationToken)
    {
        var current = destinationFolderId;
        var created = new List<string>();
        try
        {
            foreach (var raw in relativeFolder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = LibraryRules.NormalizeDisplayName(raw, "Imported folder name");
                var contents = await _database.GetFolderContentsAsync(current, cancellationToken).ConfigureAwait(false);
                var existing = contents.Folders.FirstOrDefault(folder => string.Equals(LibraryRules.ComparisonKey(folder.Name), LibraryRules.ComparisonKey(name), StringComparison.Ordinal));
                if (existing is not null) current = existing.Id;
                else
                {
                    var folder = await _database.CreateFolderAsync(current, name, cancellationToken).ConfigureAwait(false);
                    current = folder.Id;
                    created.Add(current);
                }
            }
            return (current, created);
        }
        catch
        {
            foreach (var createdId in created.AsEnumerable().Reverse()) await _database.DeleteEmptyActiveFolderAsync(createdId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task<FileExportResult> ExportFileAsync(string fileId, string destinationPath, ExportCollisionChoice collisionChoice = ExportCollisionChoice.Fail, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        ExportCoreAsync(fileId, destinationPath, collisionChoice, changedBytes: false, progress, cancellationToken);

    public Task<FileExportResult> ExportChangedBytesAsync(string fileId, string destinationPath, ExportCollisionChoice collisionChoice = ExportCollisionChoice.Fail, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        ExportCoreAsync(fileId, destinationPath, collisionChoice, changedBytes: true, progress, cancellationToken);

    public async Task<(FileExportResult Media, SidecarExportResult? Sidecar)> ExportFileWithSidecarAsync(string fileId, string destinationPath, ExportSidecarOptions sidecarOptions, ExportCollisionChoice collisionChoice = ExportCollisionChoice.Fail, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sidecarOptions);
        var media = await ExportFileAsync(fileId, destinationPath, collisionChoice, progress, cancellationToken).ConfigureAwait(false);
        if (media.Outcome != FileExportOutcome.Exported || !sidecarOptions.WriteSidecar) return (media, null);

        try
        {
            var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
            var generation = await _database.GetGenerationRecordForResultFileAsync(fileId, cancellationToken).ConfigureAwait(false);
            var json = ExportSidecarWriter.BuildJson(file, generation, sidecarOptions);
            var sidecarBytes = new UTF8Encoding(false).GetBytes(json);
            var sidecarPath = media.DestinationPath + ".slopfactory.json";
            var sidecarResult = await WriteBytesAtomicallyWithJournalAsync(fileId, sidecarBytes, sidecarPath, collisionChoice, cancellationToken).ConfigureAwait(false);
            var sidecarPathOrNull = sidecarResult.Outcome == FileExportOutcome.Exported ? sidecarResult.DestinationPath : null;
            return (media, new SidecarExportResult(sidecarPathOrNull, sidecarResult.Outcome, sidecarResult.Error));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException or ArgumentException)
        {
            return (media, new SidecarExportResult(null, FileExportOutcome.Failed, exception.Message));
        }
    }

    /// <summary>The same atomic temp-then-rename-then-read-back-verify-then-journal machinery
    /// <see cref="ExportCoreAsync"/> uses, adapted for an in-memory byte source (a sidecar document)
    /// rather than a copy from an existing managed file. Kept as a distinct helper rather than forcing
    /// a shared abstraction over two different source shapes (file-to-file copy vs. in-memory bytes).</summary>
    private async Task<FileExportResult> WriteBytesAtomicallyWithJournalAsync(string fileId, byte[] bytes, string destinationPath, ExportCollisionChoice collisionChoice, CancellationToken cancellationToken)
    {
        string? temporary = null;
        string? operationId = null;
        try
        {
            var destination = Path.GetFullPath(destinationPath);
            var parent = Path.GetDirectoryName(destination) ?? throw new LibraryValidationException("The export destination must have a parent directory.");
            Directory.CreateDirectory(parent);
            if ((new DirectoryInfo(parent).Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("A redirected directory cannot be used as an export destination.");
            if (Directory.Exists(destination)) throw new LibraryValidationException("The export destination is a directory.");
            if (File.Exists(destination) && collisionChoice == ExportCollisionChoice.Fail) throw new NameConflictException("A file already exists at the export destination.");
            if (File.Exists(destination) && (new FileInfo(destination).Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("A redirected file cannot be replaced during export.");
            temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.slopfactory-exporting");

            if (_exportCleanupJournal is not null)
            {
                operationId = await _exportCleanupJournal.RecordPlannedAsync(ExportCleanupObjectType.LocalTempFile, parent, Path.GetFileName(temporary), Path.GetFullPath(temporary), cancellationToken).ConfigureAwait(false);
            }

            await _exportFaultInjector.BeforeTempCreationAsync(cancellationToken).ConfigureAwait(false);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (operationId is not null) await _exportCleanupJournal!.ConfirmAsync(operationId, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await _exportFaultInjector.BeforeAtomicCommitAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, collisionChoice == ExportCollisionChoice.Replace);
            temporary = null;
            await _exportFaultInjector.BeforeJournalRemovalAsync(cancellationToken).ConfigureAwait(false);
            if (operationId is not null) await _exportCleanupJournal!.RemoveAsync(operationId, cancellationToken).ConfigureAwait(false);

            var readBackHash = await Hashing.Sha256Async(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(readBackHash, hash, StringComparison.Ordinal))
            {
                bool removed;
                try { File.Delete(destination); removed = true; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { removed = false; }
                var message = removed
                    ? "The exported sidecar did not match its content after being written and was removed; export did not complete."
                    : "The exported sidecar did not match its content after being written and could not be removed; the destination may be corrupt.";
                return new FileExportResult(fileId, destination, FileExportOutcome.VerificationFailed, bytes.LongLength, hash, message);
            }

            return new FileExportResult(fileId, destination, FileExportOutcome.Exported, bytes.LongLength, hash, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new FileExportResult(fileId, destinationPath, FileExportOutcome.Cancelled, 0, null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException or ArgumentException)
        {
            return new FileExportResult(fileId, destinationPath, FileExportOutcome.Failed, 0, null, exception.Message);
        }
        finally
        {
            TryDelete(temporary);
            if (operationId is not null && (temporary is null || !File.Exists(temporary)))
            {
                await _exportCleanupJournal!.RemoveAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<BulkExportPreflight> BuildBulkExportPreflightAsync(IReadOnlyCollection<string> fileIds, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fileIds);
        var directory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(directory)) throw new LibraryValidationException("Choose an existing export directory.");
        if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("A redirected directory cannot be used for bulk export.");
        var items = new List<BulkExportPreflightItem>();
        foreach (var fileId in fileIds.Distinct(StringComparer.Ordinal))
        {
            var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
            var safeName = SafeExportName(file.DisplayName);
            var destination = Path.Combine(directory, safeName);
            var reason = ContentActionPolicy.CanUseManagedContent(file) ? null : "Missing or changed content cannot be exported normally.";
            items.Add(new BulkExportPreflightItem(file.Id, file.DisplayName, safeName, destination, File.Exists(destination), false, reason));
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var collisions = items.GroupBy(item => item.SafeFileName, comparison).Where(group => group.Count() > 1).SelectMany(group => group.Select(item => item.FileId)).ToHashSet(StringComparer.Ordinal);
        items = items.Select(item => item with { HasSelectionCollision = collisions.Contains(item.FileId), BlockingReason = collisions.Contains(item.FileId) ? "Two selected files map to the same safe destination name." : item.BlockingReason }).ToList();
        var previewId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', items.Select(item => $"{item.FileId}\0{item.SafeFileName}\0{item.DestinationExists}")))));
        return new BulkExportPreflight(previewId, directory, items, Descriptor.LibraryId);
    }

    public async Task<BulkExportResult> ExportFilesAsync(BulkExportPreflight preflight, IReadOnlyDictionary<string, ExportCollisionChoice> collisionChoices, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(collisionChoices);
        if (preflight.LibraryId is not null && !string.Equals(preflight.LibraryId, Descriptor.LibraryId, StringComparison.Ordinal)) throw new LibraryValidationException("A bulk-export review cannot be committed from a different library.");
        var results = new List<FileExportResult>(preflight.Items.Count);
        for (var index = 0; index < preflight.Items.Count; index++)
        {
            var item = preflight.Items[index];
            if (cancellationToken.IsCancellationRequested)
            {
                results.AddRange(preflight.Items.Skip(index).Select(remaining => new FileExportResult(remaining.FileId, remaining.DestinationPath, FileExportOutcome.Cancelled, 0, null, null)));
                break;
            }
            if (item.BlockingReason is not null) { results.Add(new(item.FileId, item.DestinationPath, FileExportOutcome.Failed, 0, null, item.BlockingReason)); continue; }
            var choice = collisionChoices.GetValueOrDefault(item.FileId, ExportCollisionChoice.Fail);
            progress?.Report(new ImportProgress(index + 1, preflight.Items.Count, item.DisplayName, "Exporting", 0, 1));
            var result = await ExportFileAsync(item.FileId, item.DestinationPath, choice, cancellationToken: cancellationToken).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(new ImportProgress(index + 1, preflight.Items.Count, item.DisplayName, result.Outcome.ToString(), result.BytesWritten, Math.Max(result.BytesWritten, 1)));
        }
        return new BulkExportResult(results);
    }

    private static string SafeExportName(string displayName)
    {
        var name = Path.GetFileName(displayName.Trim());
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        name = name.TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") name = "export.bin";
        return name;
    }

    private async Task<FileExportResult> ExportCoreAsync(string fileId, string destinationPath, ExportCollisionChoice collisionChoice, bool changedBytes, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string? temporary = null;
        string? operationId = null;
        try
        {
            var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
            if (file.State != LibraryRecordState.Active) throw new LibraryValidationException("Only active files can be exported.");
            if (!changedBytes && !ContentActionPolicy.CanUseManagedContent(file)) throw new LibraryValidationException("Missing or changed managed content cannot be exported normally.");
            if (changedBytes && file.ContentState != FileContentState.Changed) throw new LibraryValidationException("Export Changed Bytes is available only for changed managed content.");
            var source = changedBytes ? ValidatePresentSafeManagedFile(file) : ValidateRegularManagedFile(file);
            var destination = Path.GetFullPath(destinationPath);
            var parent = Path.GetDirectoryName(destination) ?? throw new LibraryValidationException("The export destination must have a parent directory.");
            Directory.CreateDirectory(parent);
            if ((new DirectoryInfo(parent).Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("A redirected directory cannot be used as an export destination.");
            if (Directory.Exists(destination)) throw new LibraryValidationException("The export destination is a directory.");
            if (File.Exists(destination) && collisionChoice == ExportCollisionChoice.Fail) throw new NameConflictException("A file already exists at the export destination.");
            if (File.Exists(destination) && (new FileInfo(destination).Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("A redirected file cannot be replaced during export.");
            temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.slopfactory-exporting");

            // plan.md:603 — the journal durably records the planned temporary object before it's
            // created, so a crash in the narrow window before creation is still recoverable.
            if (_exportCleanupJournal is not null)
            {
                operationId = await _exportCleanupJournal.RecordPlannedAsync(ExportCleanupObjectType.LocalTempFile, parent, Path.GetFileName(temporary), Path.GetFullPath(temporary), cancellationToken).ConfigureAwait(false);
            }

            await _exportFaultInjector.BeforeTempCreationAsync(cancellationToken).ConfigureAwait(false);
            var copied = await Hashing.CopyAndHashAsync(source, temporary, cancellationToken, bytes => progress?.Report(bytes)).ConfigureAwait(false);
            if (operationId is not null) await _exportCleanupJournal!.ConfirmAsync(operationId, cancellationToken).ConfigureAwait(false);
            var expectedHash = changedBytes ? await Hashing.Sha256Async(source, cancellationToken).ConfigureAwait(false) : file.ContentHash;
            if (!string.Equals(copied.Hash, expectedHash, StringComparison.Ordinal) || copied.Bytes != new FileInfo(source).Length) throw new IOException("Export verification failed; the destination was not committed.");
            await _exportFaultInjector.BeforeAtomicCommitAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, collisionChoice == ExportCollisionChoice.Replace);
            temporary = null;
            await _exportFaultInjector.BeforeJournalRemovalAsync(cancellationToken).ConfigureAwait(false);
            if (operationId is not null) await _exportCleanupJournal!.RemoveAsync(operationId, cancellationToken).ConfigureAwait(false);

            // plan.md:649-652 — the outgoing stream above already matched the source, but that only
            // proves what was sent, not what physically landed at the destination (a filesystem quirk
            // or partial flush could still diverge). A mismatch here never marks the source library
            // record corrupt or changed — nothing above touches its ContentState at all — and it is
            // reported as a distinct outcome from an ordinary outgoing-stream failure, since the
            // destination path may already have replaced something the caller cannot assume was
            // "restored" merely because this attempt failed.
            var readBackHash = await Hashing.Sha256Async(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(readBackHash, copied.Hash, StringComparison.Ordinal))
            {
                bool removed;
                try { File.Delete(destination); removed = true; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { removed = false; }
                var message = removed
                    ? "The exported file did not match its source after being written and was removed; export did not complete."
                    : "The exported file did not match its source after being written and could not be removed; the destination may be corrupt.";
                return new FileExportResult(file.Id, destination, FileExportOutcome.VerificationFailed, copied.Bytes, copied.Hash, message);
            }

            return new FileExportResult(file.Id, destination, FileExportOutcome.Exported, copied.Bytes, copied.Hash, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new FileExportResult(fileId, destinationPath, FileExportOutcome.Cancelled, 0, null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException or ArgumentException)
        {
            return new FileExportResult(fileId, destinationPath, FileExportOutcome.Failed, 0, null, exception.Message);
        }
        finally
        {
            TryDelete(temporary);
            // Best-effort — a live (non-crash) failure still cleans up its own journal entry once the
            // temp file is confirmed gone. If deletion above silently failed, the entry is
            // deliberately left for a later IExportCleanupJournal.SweepAsync to find and retry —
            // exactly the crash-recovery path working as intended, not just for real crashes. Uses
            // CancellationToken.None since this cleanup must still run even if the operation itself
            // was cancelled.
            if (operationId is not null && (temporary is null || !File.Exists(temporary)))
            {
                await _exportCleanupJournal!.RemoveAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<ExternalOpenCopy> CreateExternalOpenCopyAsync(string fileId, string temporaryDirectory, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await GetVerifiedContentFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        var safety = ContentActionPolicy.GetExternalOpenSafety(file);
        if (safety is ExternalOpenSafety.BlockedActiveContent or ExternalOpenSafety.BlockedUnavailableContent) throw new LibraryValidationException("This content cannot be opened in another application safely.");
        var root = Path.GetFullPath(temporaryDirectory);
        Directory.CreateDirectory(root);
        if ((new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException("The temporary external-open directory cannot be redirected.");
        var safeName = $"{Guid.NewGuid():N}-{Path.GetFileName(file.DisplayName)}";
        var path = Path.Combine(root, safeName);
        var copied = await Hashing.CopyAndHashAsync(ValidateRegularManagedFile(file), path, cancellationToken).ConfigureAwait(false);
        if (copied.Bytes != file.ByteSize || !string.Equals(copied.Hash, file.ContentHash, StringComparison.Ordinal)) { TryDelete(path); throw new IOException("The external-open copy could not be verified."); }
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return new ExternalOpenCopy(file.Id, path, file.MediaType, true);
    }

    private string ValidatePresentSafeManagedFile(FileRecord file)
    {
        var path = _layout.ManagedFilePath(file.ManagedName);
        if (Directory.Exists(path) || !File.Exists(path)) throw new LibraryValidationException("The managed media path is not a present regular file.");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ManagedFileSafety.HasMultipleLinks(path)) throw new LibraryValidationException("Redirected or hard-linked managed media cannot be exported.");
        return path;
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

    public Task<BulkFileOperationResult> SetMetadataSensitivityForFilesAsync(IReadOnlyCollection<string> fileIds, string key, bool isSensitive, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalizedKey = LibraryRules.NormalizeMetadataKey(key);
        return RunMutationAsync(() => ProcessFilesAsync(fileIds, fileId => _database.SetMetadataSensitivityAsync(fileId, normalizedKey, isSensitive, cancellationToken), cancellationToken), cancellationToken);
    }

    public Task RemoveMetadataAsync(string fileId, string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RemoveMetadataAsync(fileId, key, cancellationToken), cancellationToken);
    }

    public async Task<MetadataNormalizationPreview> PreviewMetadataNormalizationAsync(IReadOnlyCollection<string> fileIds, string key, MetadataValueKind targetKind, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fileIds);
        var normalizedKey = LibraryRules.NormalizeMetadataKey(key);
        var items = new List<MetadataNormalizationItem>();
        foreach (var fileId in fileIds.Distinct(StringComparer.Ordinal))
        {
            var entry = (await _database.GetMetadataAsync(fileId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(value => string.Equals(LibraryRules.ComparisonKey(value.Key), LibraryRules.ComparisonKey(normalizedKey), StringComparison.Ordinal));
            if (entry is null) continue;
            try
            {
                var value = ConvertMetadataValue(entry, targetKind);
                items.Add(new MetadataNormalizationItem(fileId, entry.Id, entry.IsSensitive ? "Sensitive metadata" : entry.Key, entry.Kind, targetKind, entry.IsSensitive, true, entry.IsSensitive ? null : value, null));
            }
            catch (LibraryValidationException exception)
            {
                items.Add(new MetadataNormalizationItem(fileId, entry.Id, entry.IsSensitive ? "Sensitive metadata" : entry.Key, entry.Kind, targetKind, entry.IsSensitive, false, null, exception.Message));
            }
        }
        var previewId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', items.Select(item => $"{item.FileId}\0{item.MetadataId}\0{item.TargetKind}\0{item.NormalizedValue}")))));
        return new MetadataNormalizationPreview(previewId, items, Descriptor.LibraryId);
    }

    public Task<BulkFileOperationResult> CommitMetadataNormalizationAsync(MetadataNormalizationPreview preview, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.LibraryId is not null && !string.Equals(preview.LibraryId, Descriptor.LibraryId, StringComparison.Ordinal)) throw new LibraryValidationException("A metadata-normalization preview cannot be committed to a different library.");
        return RunMutationAsync(() => CommitMetadataNormalizationCoreAsync(preview, cancellationToken), cancellationToken);
    }

    private async Task<BulkFileOperationResult> CommitMetadataNormalizationCoreAsync(MetadataNormalizationPreview preview, CancellationToken cancellationToken)
    {
        var results = new List<BulkFileOperationItemResult>();
        foreach (var item in preview.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await _database.GetFileAsync(item.FileId, cancellationToken).ConfigureAwait(false);
            if (!item.IsConvertible) { results.Add(new(file.Id, file.DisplayName, false, item.Error ?? "The value is not convertible.")); continue; }
            var current = (await _database.GetMetadataAsync(item.FileId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(entry => entry.Id == item.MetadataId);
            if (current is null || current.Kind != item.SourceKind || current.IsSensitive != item.IsSensitive) { results.Add(new(file.Id, file.DisplayName, false, "The metadata changed after review.")); continue; }
            var normalizedValue = item.IsSensitive ? ConvertMetadataValue(current, item.TargetKind) : item.NormalizedValue ?? throw new LibraryValidationException("The reviewed normalized value is unavailable.");
            await _database.SetMetadataAsync(item.FileId, current.Key, item.TargetKind, normalizedValue, current.IsSensitive, cancellationToken).ConfigureAwait(false);
            results.Add(new(file.Id, file.DisplayName, true, null));
        }
        return new BulkFileOperationResult(results);
    }

    private static string ConvertMetadataValue(MetadataEntry entry, MetadataValueKind targetKind)
    {
        if (entry.Kind == targetKind) return LibraryRules.ValidateMetadataValue(targetKind, entry.SerializedValue);
        var value = entry.SerializedValue;
        if (targetKind == MetadataValueKind.Text) return LibraryRules.ValidateMetadataValue(targetKind, value);
        if (entry.Kind != MetadataValueKind.Text) throw new LibraryValidationException("Only text values can be normalized to a different structured metadata type.");
        return LibraryRules.ValidateMetadataValue(targetKind, value);
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
                RecycleBinItemKind.FileLink => "Restores the directed file link after both endpoint files are active.",
                RecycleBinItemKind.Connection => $"Restores the connection and its {entry.OwnedModelCount} model(s) and {entry.OwnedSavedSettingCount} saved setting(s).",
                RecycleBinItemKind.Model => $"Restores the model and its {entry.OwnedSavedSettingCount} saved setting(s).",
                RecycleBinItemKind.SavedSetting => "Restores the saved setting.",
                _ => "Restores the generation-history record."
            });
            if (reference.Kind is RecycleBinItemKind.Folder or RecycleBinItemKind.File && entry.OwnedLinkCount > 0)
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
        var checkpointPath = _layout.StagingFilePath("integrity-scan-checkpoint.json");
        var checkpoint = await ReadIntegrityCheckpointAsync(checkpointPath).ConfigureAwait(false);
        if (checkpoint is not null && !string.Equals(checkpoint.LibraryId, Descriptor.LibraryId, StringComparison.Ordinal)) checkpoint = null;
        var startedAt = checkpoint?.StartedAt ?? DateTimeOffset.UtcNow;
        var findings = checkpoint?.Findings.ToList() ?? [];
        var completedFileIds = checkpoint?.CompletedFileIds.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
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
            if (!Directory.Exists(_layout.PendingReviewPath))
            {
                findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.RequiredDirectoryMissing, null, null, null, "The pending-review directory is missing."));
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
                if (completedFileIds.Contains(file.Id)) { processed++; continue; }
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
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ManagedFileSafety.HasMultipleLinks(path))
                        {
                            findings.Add(new LibraryIntegrityFinding(LibraryIntegrityIssueKind.UnsafeManagedEntry, file.Id, file.ByteSize, null, "The recorded managed path is a symbolic link, reparse point, or hard link."));
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
                completedFileIds.Add(file.Id);
                await WriteIntegrityCheckpointAsync(checkpointPath, new IntegrityScanCheckpoint(Descriptor.LibraryId, startedAt, completedFileIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(), findings.Where(finding => finding.RecordId is not null).ToArray())).ConfigureAwait(false);
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
            await WriteIntegrityCheckpointAsync(checkpointPath, new IntegrityScanCheckpoint(Descriptor.LibraryId, startedAt, completedFileIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(), findings.Where(finding => finding.RecordId is not null).ToArray())).ConfigureAwait(false);
        }

        finally
        {
            if (mutationGateHeld) _mutationGate.Release();
        }

        if (!cancelled) TryDelete(checkpointPath);
        progress?.Report(new LibraryIntegrityScanProgress(processed, Math.Max(total, processed), cancelled ? "Scan cancelled" : "Scan finished"));
        return new LibraryIntegrityReport(Descriptor.LibraryId, Descriptor.SchemaVersion, startedAt, DateTimeOffset.UtcNow, complete && !cancelled, cancelled, findings);
    }

    private static async Task<IntegrityScanCheckpoint?> ReadIntegrityCheckpointAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<IntegrityScanCheckpoint>(stream, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { TryDelete(path); return null; }
    }

    private static async Task WriteIntegrityCheckpointAsync(string path, IntegrityScanCheckpoint checkpoint)
    {
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, checkpoint, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { TryDelete(temporary); }
    }

    private sealed record IntegrityScanCheckpoint(string LibraryId, DateTimeOffset StartedAt, IReadOnlyList<string> CompletedFileIds, IReadOnlyList<LibraryIntegrityFinding> Findings);

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

    public Task AdoptAsIndependentLibraryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => AdoptAsIndependentLibraryCoreAsync(cancellationToken), cancellationToken);
    }

    private async Task AdoptAsIndependentLibraryCoreAsync(CancellationToken cancellationToken)
    {
        var newLibraryId = LibraryRules.NewId();
        var previousManifest = _manifest;
        var adoptedManifest = previousManifest with { LibraryId = newLibraryId };
        await _database.UpdateLibraryIdAsync(newLibraryId, cancellationToken).ConfigureAwait(false);
        try
        {
            await LibraryManifestStore.WriteAsync(_layout, adoptedManifest, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _database.UpdateLibraryIdAsync(previousManifest.LibraryId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        _manifest = adoptedManifest;
        Descriptor = Descriptor with { LibraryId = newLibraryId };
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
        if ((new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0 || ManagedFileSafety.HasMultipleLinks(path)) throw new LibraryValidationException("The managed file path is a symbolic link, reparse point, or hard link.");
        return path;
    }

    public Task<IReadOnlyList<Connection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetActiveConnectionsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<Connection>> GetRecycledConnectionsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetRecycledConnectionsAsync(cancellationToken);
    }

    public Task<Connection> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetConnectionAsync(connectionId, cancellationToken);
    }

    public Task<Connection> CreateConnectionAsync(string label, ProviderType providerType, string baseUrl, string credentialHeaderName, string authPrefix, int? timeoutSeconds = null, IReadOnlyList<ConnectionHeader>? additionalHeaders = null, GenericConnectionModalitySettings? genericModalitySettings = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.CreateConnectionAsync(label, providerType, baseUrl, credentialHeaderName, authPrefix, timeoutSeconds, additionalHeaders, genericModalitySettings, cancellationToken), cancellationToken);
    }

    public Task<Connection> UpdateConnectionAsync(string connectionId, string label, string baseUrl, string credentialHeaderName, string authPrefix, int? timeoutSeconds = null, IReadOnlyList<ConnectionHeader>? additionalHeaders = null, GenericConnectionModalitySettings? genericModalitySettings = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.UpdateConnectionAsync(connectionId, label, baseUrl, credentialHeaderName, authPrefix, timeoutSeconds, additionalHeaders, genericModalitySettings, cancellationToken), cancellationToken);
    }

    public Task<Connection> SetConnectionCredentialStateAsync(string connectionId, bool hasCredential, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.SetConnectionCredentialStateAsync(connectionId, hasCredential, cancellationToken), cancellationToken);
    }

    public Task<Connection> SetConnectionTestResultAsync(string connectionId, bool success, string message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.SetConnectionTestResultAsync(connectionId, success, message, cancellationToken), cancellationToken);
    }

    public Task<Connection> ChangeConnectionProviderTypeAsync(string connectionId, ProviderType providerType, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.ChangeConnectionProviderTypeAsync(connectionId, providerType, cancellationToken), cancellationToken);
    }

    public Task<ModelCatalogue> GetModelCatalogueAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetModelCatalogueAsync(connectionId, cancellationToken);
    }

    public Task<ModelCatalogue> RefreshModelCatalogueAsync(string connectionId, IReadOnlyList<ProviderModelInfo> discoveredModels, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RefreshModelCatalogueAsync(connectionId, discoveredModels, cancellationToken), cancellationToken);
    }

    public Task<ModelCatalogue> MarkModelCatalogueRefreshFailedAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.MarkModelCatalogueRefreshFailedAsync(connectionId, cancellationToken), cancellationToken);
    }

    public Task RecycleConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RecycleConnectionAsync(connectionId, cancellationToken), cancellationToken);
    }

    public Task RestoreConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RestoreConnectionAsync(connectionId, cancellationToken), cancellationToken);
    }

    public Task PermanentlyDeleteConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.PermanentlyDeleteConnectionAsync(connectionId, cancellationToken), cancellationToken);
    }

    public Task<string> BeginCredentialCandidateAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.BeginCredentialCandidateAsync(connectionId, cancellationToken), cancellationToken);
    }

    public Task DiscardCredentialCandidateAsync(string connectionId, string revisionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.DiscardCredentialCandidateAsync(connectionId, revisionId, cancellationToken), cancellationToken);
    }

    public Task<CredentialPromotionResult> PromoteCredentialRevisionAsync(string connectionId, string revisionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.PromoteCredentialRevisionAsync(connectionId, revisionId, cancellationToken), cancellationToken);
    }

    public Task<Connection> MarkCredentialRequiresRepairAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.MarkCredentialRequiresRepairAsync(connectionId, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<CredentialLedgerConnectionSnapshot>> GetCredentialLedgerSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetCredentialLedgerSnapshotAsync(cancellationToken);
    }

    public Task DeleteCredentialLedgerRowAsync(string connectionId, string revisionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.DeleteCredentialLedgerRowAsync(connectionId, revisionId, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<Model>> GetActiveModelsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetActiveModelsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<Model>> GetRecycledModelsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetRecycledModelsAsync(cancellationToken);
    }

    public Task<Model> GetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetModelAsync(modelId, cancellationToken);
    }

    public Task<Model> CreateModelAsync(string label, string connectionId, string providerModelId, GenerationMode mode, bool supportsSystemInstructions, TextResultFormat textFormat = TextResultFormat.Markdown, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.CreateModelAsync(label, connectionId, providerModelId, mode, supportsSystemInstructions, textFormat, cancellationToken), cancellationToken);
    }

    public Task<Model> UpdateModelAsync(string modelId, string label, string providerModelId, GenerationMode mode, bool supportsSystemInstructions, TextResultFormat textFormat = TextResultFormat.Markdown, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.UpdateModelAsync(modelId, label, providerModelId, mode, supportsSystemInstructions, textFormat, cancellationToken), cancellationToken);
    }

    public Task<Model> MarkModelReviewedAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.MarkModelReviewedAsync(modelId, cancellationToken), cancellationToken);
    }

    public Task RecycleModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RecycleModelAsync(modelId, cancellationToken), cancellationToken);
    }

    public Task RestoreModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RestoreModelAsync(modelId, cancellationToken), cancellationToken);
    }

    public Task PermanentlyDeleteModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.PermanentlyDeleteModelAsync(modelId, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<GenerationRecord>> GetGenerationHistoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetGenerationHistoryAsync(cancellationToken);
    }

    public Task<GenerationRecord> GetGenerationRecordAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetGenerationRecordAsync(generationId, cancellationToken);
    }

    public Task<GenerationRecord?> GetGenerationRecordForResultFileAsync(string resultFileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetGenerationRecordForResultFileAsync(resultFileId, cancellationToken);
    }

    public Task<IReadOnlyList<GenerationRecord>> GetNonTerminalGenerationRecordsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetNonTerminalGenerationRecordsAsync(cancellationToken);
    }

    public Task<GenerationRecord> CreateQueuedGenerationRecordAsync(string modelId, string prompt, int resultCount, string destinationFolderId, string? systemInstructions = null, IReadOnlyList<GenerationSourceSlot>? sourceSlots = null, GenerationSettings? settings = null, string? promptImprovementRecordId = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => CreateQueuedGenerationRecordCoreAsync(modelId, prompt, resultCount, destinationFolderId, systemInstructions, sourceSlots, settings, promptImprovementRecordId, cancellationToken), cancellationToken);
    }

    private async Task<GenerationRecord> CreateQueuedGenerationRecordCoreAsync(string modelId, string prompt, int resultCount, string destinationFolderId, string? systemInstructions, IReadOnlyList<GenerationSourceSlot>? sourceSlots, GenerationSettings? settings, string? promptImprovementRecordId, CancellationToken cancellationToken)
    {
        var model = await _database.GetModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        var connectionRecord = await _database.GetConnectionAsync(model.ConnectionId, cancellationToken).ConfigureAwait(false);
        return await _database.CreateQueuedGenerationRecordAsync(model, connectionRecord.ProviderType, prompt, systemInstructions, resultCount, destinationFolderId, sourceSlots, settings, promptImprovementRecordId, cancellationToken).ConfigureAwait(false);
    }

    public Task<GenerationRecord> AdvanceGenerationStatusAsync(string generationRecordId, GenerationStatus status, GenerationHoldReason? holdReason = null, GenerationFailureReason? failureReason = null, int? position = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.AdvanceGenerationStatusAsync(generationRecordId, status, holdReason, failureReason, position, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<GenerationStatusTransition>> GetGenerationStatusHistoryAsync(string generationRecordId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetGenerationStatusHistoryAsync(generationRecordId, cancellationToken);
    }

    public Task<GenerationRecord> AbandonGenerationOutcomeAsync(string generationRecordId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => AbandonGenerationOutcomeCoreAsync(generationRecordId, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Abandon Recovery: the user gives up on ever resolving a record whose outcome cannot be
    /// confirmed, rather than leaving it open indefinitely awaiting reconciliation that may never
    /// come. Only meaningful for the specific statuses reconciliation would otherwise apply to —
    /// <see cref="GenerationStatus.SubmissionOutcomeUnknown"/> and <see cref="GenerationStatus.Paused"/>
    /// — so the record can't be silently discarded from an ordinary in-progress or already-terminal
    /// state. Finalizes to <see cref="GenerationStatus.Failed"/> with
    /// <see cref="GenerationFailureReason.AbandonedByUser"/>; the record itself already carries no
    /// actionable provider identifier (only the device-wide async-job registry does, for video, and
    /// that registry is scrubbed separately — see <c>DeleteAsyncRemoteJobAsync</c>), so no further
    /// sanitization is needed here.
    /// </summary>
    private async Task<GenerationRecord> AbandonGenerationOutcomeCoreAsync(string generationRecordId, CancellationToken cancellationToken)
    {
        var existing = await _database.GetGenerationRecordAsync(generationRecordId, cancellationToken).ConfigureAwait(false);
        if (existing.Status is not (GenerationStatus.SubmissionOutcomeUnknown or GenerationStatus.Paused))
        {
            throw new LibraryValidationException("Only a Submission Outcome Unknown or Paused generation can be abandoned.");
        }
        return await _database.AdvanceGenerationStatusAsync(generationRecordId, GenerationStatus.Failed, failureReason: GenerationFailureReason.AbandonedByUser, holdReason: null, position: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<GenerationRecord>> GetRecycledGenerationHistoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetRecycledGenerationHistoryAsync(cancellationToken);
    }

    public Task RecycleGenerationRecordAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RecycleGenerationRecordAsync(generationId, cancellationToken), cancellationToken);
    }

    public Task RestoreGenerationRecordAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RestoreGenerationRecordAsync(generationId, cancellationToken), cancellationToken);
    }

    public Task PermanentlyDeleteGenerationRecordAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.PermanentlyDeleteGenerationRecordAsync(generationId, cancellationToken), cancellationToken);
    }

    public Task<GenerationRecord> RecordTextGenerationResultAsync(string modelId, string prompt, int resultCount, string destinationFolderId, IReadOnlyList<string>? resultTexts, string? errorMessage, string? systemInstructions = null, int? promptTokens = null, int? completionTokens = null, IReadOnlyList<GenerationSourceSlot>? sourceSlots = null, string? promptImprovementRecordId = null, GenerationSettings? settings = null, int safetyBlockedCount = 0, string? existingGenerationRecordId = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => RecordTextGenerationResultCoreAsync(modelId, prompt, resultCount, destinationFolderId, resultTexts, errorMessage, systemInstructions, promptTokens, completionTokens, sourceSlots, promptImprovementRecordId, settings, safetyBlockedCount, existingGenerationRecordId, cancellationToken), cancellationToken);
    }

    public Task<GenerationRecord> RecordImageGenerationResultAsync(string modelId, string prompt, int resultCount, string destinationFolderId, IReadOnlyList<byte[]>? resultImages, string? errorMessage, string? promptImprovementRecordId = null, string? existingGenerationRecordId = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => RecordImageGenerationResultCoreAsync(modelId, prompt, resultCount, destinationFolderId, resultImages, errorMessage, promptImprovementRecordId, cancellationToken, existingGenerationRecordId: existingGenerationRecordId), cancellationToken);
    }

    /// <summary>
    /// Commits raw generated bytes (audio or video — anything whose commit shape is identical to
    /// image generation: decoded bytes in, one media-type-detected file out) exactly like
    /// <see cref="RecordImageGenerationResultAsync"/>. A single generic method is enough for both
    /// modes because the target <see cref="Model"/>'s own <see cref="Model.Mode"/> already
    /// determines whether the resulting <see cref="GenerationRecord"/> is Audio or Video — nothing
    /// mode-specific happens in the commit path itself.
    /// </summary>
    public Task<GenerationRecord> RecordMediaGenerationResultAsync(string modelId, string prompt, int resultCount, string destinationFolderId, IReadOnlyList<byte[]>? resultFiles, string? errorMessage, string? promptImprovementRecordId = null, double? actualCost = null, string? actualCostCurrency = null, bool wasCancelled = false, IReadOnlyList<string>? childErrorMessages = null, string? existingGenerationRecordId = null, IReadOnlyList<GenerationSourceSlot>? sourceSlots = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => RecordImageGenerationResultCoreAsync(modelId, prompt, resultCount, destinationFolderId, resultFiles, errorMessage, promptImprovementRecordId, cancellationToken, actualCost, actualCostCurrency, wasCancelled, childErrorMessages, existingGenerationRecordId, sourceSlots), cancellationToken);
    }

    public Task<IReadOnlyList<PromptImprovementRecord>> GetPromptImprovementHistoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetPromptImprovementHistoryAsync(cancellationToken);
    }

    public Task<PromptImprovementRecord> RecordPromptImprovementAttemptAsync(string modelId, string rawPrompt, string? guidance, string templateVersion, IReadOnlyList<string>? candidates, string? errorMessage, int? promptTokens = null, int? completionTokens = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => RecordPromptImprovementAttemptCoreAsync(modelId, rawPrompt, guidance, templateVersion, candidates, errorMessage, promptTokens, completionTokens, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<SavedGenerationSetting>> GetActiveSavedSettingsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetActiveSavedSettingsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<SavedGenerationSetting>> GetRecycledSavedSettingsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetRecycledSavedSettingsAsync(cancellationToken);
    }

    public Task<SavedGenerationSetting> GetSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetSavedSettingAsync(savedSettingId, cancellationToken);
    }

    public Task<SavedGenerationSetting> CreateSavedSettingAsync(string title, string? modelId, string prompt, int resultCount, string destinationFolderId, string? systemInstructions = null, GenerationSettings? settings = null, IReadOnlyList<GenerationSourceSlot>? sourceSlots = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.CreateSavedSettingAsync(title, modelId, prompt, resultCount, destinationFolderId, systemInstructions, settings, sourceSlots, cancellationToken), cancellationToken);
    }

    public Task<SavedGenerationSetting> UpdateSavedSettingAsync(string savedSettingId, int expectedRevision, string title, string? modelId, string prompt, int resultCount, string destinationFolderId, string? systemInstructions = null, GenerationSettings? settings = null, IReadOnlyList<GenerationSourceSlot>? sourceSlots = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.UpdateSavedSettingAsync(savedSettingId, expectedRevision, title, modelId, prompt, resultCount, destinationFolderId, systemInstructions, settings, sourceSlots, cancellationToken), cancellationToken);
    }

    public Task RecycleSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RecycleSavedSettingAsync(savedSettingId, cancellationToken), cancellationToken);
    }

    public Task RestoreSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.RestoreSavedSettingAsync(savedSettingId, cancellationToken), cancellationToken);
    }

    public Task PermanentlyDeleteSavedSettingAsync(string savedSettingId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.PermanentlyDeleteSavedSettingAsync(savedSettingId, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<GenerationDraft>> GetDraftsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetDraftsAsync(cancellationToken);
    }

    public Task<GenerationDraft> GetDraftAsync(string draftId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetDraftAsync(draftId, cancellationToken);
    }

    public Task<GenerationDraft> CreateDraftAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.CreateDraftAsync(Descriptor.GeneratedFolderId, cancellationToken), cancellationToken);
    }

    public Task<GenerationDraft> ReplaceDraftStateAsync(string draftId, string? customTitle, string? modelId, string prompt, string? systemInstructions, int resultCount, string destinationFolderId, string? improvementModelId, string? improvementGuidance, GenerationSettings? settings = null, IReadOnlyList<GenerationSourceSlot>? sourceSlots = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.ReplaceDraftStateAsync(draftId, customTitle, modelId, prompt, systemInstructions, resultCount, destinationFolderId, improvementModelId, improvementGuidance, settings, sourceSlots, cancellationToken), cancellationToken);
    }

    public Task<GenerationDraft> DuplicateDraftAsync(string draftId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.DuplicateDraftAsync(draftId, cancellationToken), cancellationToken);
    }

    public Task DeleteDraftAsync(string draftId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.DeleteDraftAsync(draftId, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<GenerationDraft>> ReorderDraftsAsync(IReadOnlyList<string> orderedDraftIds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.ReorderDraftsAsync(orderedDraftIds, cancellationToken), cancellationToken);
    }

    public Task<AsyncRemoteJobRecord> CreateAsyncRemoteJobAsync(string draftId, ProviderType providerType, string connectionId, string providerJobId, string? idempotencyKey, DateTimeOffset? monitoringDeadline, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.CreateAsyncRemoteJobAsync(draftId, providerType, connectionId, providerJobId, idempotencyKey, monitoringDeadline, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<AsyncRemoteJobRecord>> GetPendingAsyncRemoteJobsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetPendingAsyncRemoteJobsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AsyncRemoteJobRecord>> GetAsyncRemoteJobsForConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetAsyncRemoteJobsForConnectionAsync(connectionId, cancellationToken);
    }

    public Task<AsyncRemoteJobRecord> UpdateAsyncRemoteJobPhaseAsync(string asyncJobId, AsyncRemoteJobPhase phase, DateTimeOffset? lastPolledAt = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.UpdateAsyncRemoteJobPhaseAsync(asyncJobId, phase, lastPolledAt, cancellationToken), cancellationToken);
    }

    public Task DeleteAsyncRemoteJobAsync(string asyncJobId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.DeleteAsyncRemoteJobAsync(asyncJobId, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<AsyncRemoteJobRecord>> GetAsyncRemoteJobsForGenerationRecordAsync(string generationRecordId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetAsyncRemoteJobsForGenerationRecordAsync(generationRecordId, cancellationToken);
    }

    public Task<AsyncRemoteJobRecord> LinkAsyncRemoteJobToGenerationResultAsync(string asyncJobId, string generationRecordId, int position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => _database.LinkAsyncRemoteJobToGenerationResultAsync(asyncJobId, generationRecordId, position, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<PendingUnverifiedResult>> GetPendingUnverifiedResultsAsync(string generationRecordId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetPendingUnverifiedResultsAsync(generationRecordId, cancellationToken);
    }

    public Task<FileRecord> RetainUnverifiedResultAsync(string generationRecordId, int position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => RetainUnverifiedResultCoreAsync(generationRecordId, position, cancellationToken), cancellationToken);
    }

    public Task DiscardUnverifiedResultAsync(string generationRecordId, int position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => DiscardUnverifiedResultCoreAsync(generationRecordId, position, cancellationToken), cancellationToken);
    }

    public Task<FileRecord> ImportMissingResultAsync(string generationRecordId, int position, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunMutationAsync(() => ImportMissingResultCoreAsync(generationRecordId, position, bytes, cancellationToken), cancellationToken);
    }

    private async Task<FileRecord> RetainUnverifiedResultCoreAsync(string generationRecordId, int position, CancellationToken cancellationToken)
    {
        var pending = await _database.GetPendingUnverifiedResultAsync(generationRecordId, position, cancellationToken).ConfigureAwait(false);
        var record = await _database.GetGenerationRecordAsync(generationRecordId, cancellationToken).ConfigureAwait(false);
        var stagedPath = _layout.PendingReviewFilePath(pending.StagedFileName);
        var fileId = LibraryRules.NewId();
        var managedName = fileId + ".bin";
        var managedPath = _layout.ManagedFilePath(managedName);
        var committed = false;
        try
        {
            File.Move(stagedPath, managedPath, false);
            var safeLabel = new string(record.ModelLabel.Select(character => character is '/' or '\\' ? '_' : character).ToArray());
            var baseName = $"{safeLabel} unverified {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.bin";
            var resolvedName = await _database.ResolveAvailableFileNameAsync(record.DestinationFolderId, baseName, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            // MediaType is forced to a generic opaque type rather than the mismatched bytes' own
            // detected type (retained on the now-deleted pending row only as an audit trail) — the
            // whole point of this path is that the detected type never matched what was expected, so
            // trusting it here would let it slip back into image/audio/video-filtered pickers and
            // preview logic that this retention path must stay excluded from (see ContentActionPolicy).
            var fileRecord = new FileRecord(fileId, record.DestinationFolderId, resolvedName, resolvedName, managedName, pending.ContentHash, pending.ByteSize, "application/octet-stream",
                FileOrigin.UnverifiedProviderOutput, LibraryRecordState.Active, now, now, null, null);
            try
            {
                await _database.InsertImportedFileAsync(fileRecord, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(managedPath);
                throw;
            }
            committed = true;
            await _database.UpdateGenerationResultEntryAsync(generationRecordId, position, GenerationResultStatus.Committed, fileId, null, cancellationToken).ConfigureAwait(false);
            await _database.DeletePendingUnverifiedResultAsync(pending.Id, cancellationToken).ConfigureAwait(false);
            return fileRecord;
        }
        finally
        {
            if (!committed) TryDelete(managedPath);
        }
    }

    private async Task DiscardUnverifiedResultCoreAsync(string generationRecordId, int position, CancellationToken cancellationToken)
    {
        var pending = await _database.GetPendingUnverifiedResultAsync(generationRecordId, position, cancellationToken).ConfigureAwait(false);
        TryDelete(_layout.PendingReviewFilePath(pending.StagedFileName));
        await _database.UpdateGenerationResultEntryAsync(generationRecordId, position, GenerationResultStatus.Failed, null, "The result was discarded as an unverified binary.", cancellationToken).ConfigureAwait(false);
        await _database.DeletePendingUnverifiedResultAsync(pending.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits a late-recovered result for a position that previously failed only because its
    /// download failed after the provider had already completed the job — see
    /// <see cref="AsyncGenerationPollOutcome.CompletedDownloadFailed"/>/<c>GenerationQueueService
    /// .RetryMissingResultDownloadAsync</c>. Reuses the same stage-hash-detect-move-commit pipeline
    /// as an ordinary successful result (this genuinely is one, just recovered late), rather than
    /// the Retain-as-Unverified-Binary path, which is for bytes that never matched the expected type
    /// at all.
    /// </summary>
    private async Task<FileRecord> ImportMissingResultCoreAsync(string generationRecordId, int position, byte[] bytes, CancellationToken cancellationToken)
    {
        var record = await _database.GetGenerationRecordAsync(generationRecordId, cancellationToken).ConfigureAwait(false);
        var existing = record.Results.FirstOrDefault(entry => entry.Position == position);
        if (existing is not { Status: GenerationResultStatus.Failed }) throw new LibraryValidationException("This result position is not awaiting a missing-result import.");
        if (record.ModelId is null) throw new LibraryValidationException("The model used for this generation is no longer available.");
        var model = await _database.GetModelAsync(record.ModelId, cancellationToken).ConfigureAwait(false);

        var fileId = LibraryRules.NewId();
        var stagingPath = _layout.StagingFilePath(fileId + ".generating");
        var managedPath = string.Empty;
        try
        {
            await using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var (mediaType, extension) = await MediaTypeDetector.DetectAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            if (ExpectedMediaTypeCategory(model.Mode) is { } expectedCategory && !mediaType.StartsWith(expectedCategory, StringComparison.Ordinal))
            {
                throw new LibraryValidationException("The recovered result's bytes did not match the expected media type for this generation mode.");
            }
            var managedName = fileId + extension;
            managedPath = _layout.ManagedFilePath(managedName);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            File.Move(stagingPath, managedPath, false);
            stagingPath = string.Empty;
            var safeLabel = new string(record.ModelLabel.Select(character => character is '/' or '\\' ? '_' : character).ToArray());
            var resolvedName = await _database.ResolveAvailableFileNameAsync(record.DestinationFolderId, $"{safeLabel} {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}{extension}", cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var fileRecord = new FileRecord(fileId, record.DestinationFolderId, resolvedName, resolvedName, managedName, hash, bytes.LongLength, mediaType,
                FileOrigin.Generated, LibraryRecordState.Active, now, now, null, null);
            try
            {
                await _database.InsertImportedFileAsync(fileRecord, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(managedPath);
                throw;
            }
            managedPath = string.Empty;
            await _database.UpdateGenerationResultEntryAsync(generationRecordId, position, GenerationResultStatus.Committed, fileId, null, cancellationToken).ConfigureAwait(false);
            return fileRecord;
        }
        finally
        {
            TryDelete(stagingPath);
            TryDelete(managedPath);
        }
    }

    private async Task<GenerationRecord> RecordTextGenerationResultCoreAsync(string modelId, string prompt, int resultCount, string destinationFolderId, IReadOnlyList<string>? resultTexts, string? errorMessage, string? systemInstructions, int? promptTokens, int? completionTokens, IReadOnlyList<GenerationSourceSlot>? sourceSlots, string? promptImprovementRecordId, GenerationSettings? settings, int safetyBlockedCount, string? existingGenerationRecordId, CancellationToken cancellationToken)
    {
        var model = await _database.GetModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        var connectionRecord = await _database.GetConnectionAsync(model.ConnectionId, cancellationToken).ConfigureAwait(false);
        var resultFileIds = new List<string>();

        var (extension, mediaType) = model.TextFormat == TextResultFormat.PlainText ? (".txt", "text/plain") : (".md", "text/markdown");

        if (resultTexts is { Count: > 0 })
        {
            var utf8 = new UTF8Encoding(false, true);
            var safeLabel = new string(model.Label.Select(character => character is '/' or '\\' ? '_' : character).ToArray());
            var baseName = $"{safeLabel} {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}{extension}";

            foreach (var text in resultTexts)
            {
                byte[] bytes;
                try { bytes = utf8.GetBytes(text); }
                catch (EncoderFallbackException) { throw new LibraryValidationException("Generated text contains an invalid Unicode sequence."); }

                var fileId = LibraryRules.NewId();
                var managedName = fileId + extension;
                var stagingPath = _layout.StagingFilePath(fileId + ".generating");
                var managedPath = _layout.ManagedFilePath(managedName);
                var resolvedName = await _database.ResolveAvailableFileNameAsync(destinationFolderId, baseName, cancellationToken).ConfigureAwait(false);
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
                    var record = new FileRecord(fileId, destinationFolderId, resolvedName, resolvedName, managedName, hash, bytes.LongLength, mediaType,
                        FileOrigin.Generated, LibraryRecordState.Active, now, now, null, null);
                    try
                    {
                        await _database.InsertImportedFileAsync(record, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        TryDelete(managedPath);
                        throw;
                    }
                    managedPath = string.Empty;
                    resultFileIds.Add(fileId);
                }
                finally
                {
                    TryDelete(stagingPath);
                    TryDelete(managedPath);
                }
            }
        }

        var status = DetermineGenerationStatus(resultFileIds.Count, resultCount);
        return await _database.CreateGenerationRecordAsync(model, connectionRecord.ProviderType, prompt, systemInstructions, resultCount, status, errorMessage, destinationFolderId, resultFileIds, promptTokens, completionTokens, sourceSlots, promptImprovementRecordId, model.TextFormat, settings, safetyBlockedCount, cancellationToken, existingGenerationRecordId: existingGenerationRecordId).ConfigureAwait(false);
    }

    /// <summary>The top-level media-type prefix a mode's results must match to be committed, or
    /// null for a mode (Text) that does not commit raw bytes through this pipeline.</summary>
    private static string? ExpectedMediaTypeCategory(GenerationMode mode) => mode switch
    {
        GenerationMode.Image => "image/",
        GenerationMode.Audio => "audio/",
        GenerationMode.Video => "video/",
        _ => null
    };

    private static GenerationStatus DetermineGenerationStatus(int committedCount, int requestedCount, bool wasCancelled = false)
    {
        if (wasCancelled) return committedCount > 0 ? GenerationStatus.CancelledWithResults : GenerationStatus.Cancelled;
        if (committedCount <= 0) return GenerationStatus.Failed;
        return committedCount < requestedCount ? GenerationStatus.PartiallyCompleted : GenerationStatus.Completed;
    }

    private async Task<GenerationRecord> RecordImageGenerationResultCoreAsync(string modelId, string prompt, int resultCount, string destinationFolderId, IReadOnlyList<byte[]>? resultImages, string? errorMessage, string? promptImprovementRecordId, CancellationToken cancellationToken, double? actualCost = null, string? actualCostCurrency = null, bool wasCancelled = false, IReadOnlyList<string>? childErrorMessages = null, string? existingGenerationRecordId = null, IReadOnlyList<GenerationSourceSlot>? sourceSlots = null)
    {
        var model = await _database.GetModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        var connectionRecord = await _database.GetConnectionAsync(model.ConnectionId, cancellationToken).ConfigureAwait(false);
        var resultFileIds = new List<string>();
        var resultEntries = new List<GenerationResultEntry>();
        var committedFiles = new List<FileRecord>();
        var reservedResultNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingReviewCandidates = new List<(int Position, string StagedFileName, long ByteSize, string ContentHash, string DetectedMediaType)>();

        if (resultImages is { Count: > 0 })
        {
            var safeLabel = new string(model.Label.Select(character => character is '/' or '\\' ? '_' : character).ToArray());
            var baseName = $"{safeLabel} {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
            var position = 0;

            try
            {
                foreach (var bytes in resultImages)
                {
                var currentPosition = position++;
                var fileId = LibraryRules.NewId();
                var stagingPath = _layout.StagingFilePath(fileId + ".generating");
                var managedPath = string.Empty;
                try
                {
                    await using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    var (mediaType, extension) = await MediaTypeDetector.DetectAsync(stagingPath, cancellationToken).ConfigureAwait(false);
                    if (ExpectedMediaTypeCategory(model.Mode) is { } expectedCategory && !mediaType.StartsWith(expectedCategory, StringComparison.Ordinal))
                    {
                        // Bytes could not be validated as the expected media category (plan.md:
                        // "the application validates ... expected media category ... When bytes
                        // cannot be validated as the expected ... type, the result fails and no
                        // successful media record is created automatically"). A recognized rejection
                        // (error document/authentication page) is discarded exactly as before — the
                        // staged temporary file is cleaned up by the existing finally block below
                        // since stagingPath is never cleared to empty on that path. Otherwise the
                        // bytes are genuinely unrecognized, so per plan.md they're held durably for
                        // an explicit Retain-as-Unverified-Binary/Discard decision instead.
                        if (ProviderRejectionPayloadClassifier.IsRecognizedRejectionPayload(bytes, mediaType))
                        {
                            resultEntries.Add(new GenerationResultEntry(currentPosition, GenerationResultStatus.Failed, null, "The provider's result bytes did not match the expected media type for this generation mode."));
                        }
                        else
                        {
                            var pendingHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                            var pendingFileName = fileId + ".pending";
                            File.Move(stagingPath, _layout.PendingReviewFilePath(pendingFileName), false);
                            stagingPath = string.Empty;
                            pendingReviewCandidates.Add((currentPosition, pendingFileName, bytes.LongLength, pendingHash, mediaType));
                            resultEntries.Add(new GenerationResultEntry(currentPosition, GenerationResultStatus.PendingReview, null, "The provider's result bytes did not match the expected media type for this generation mode and were not recognized as an error or authentication response — review to retain as an unverified binary or discard."));
                        }
                        continue;
                    }
                    var managedName = fileId + extension;
                    managedPath = _layout.ManagedFilePath(managedName);
                    var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    File.Move(stagingPath, managedPath, false);
                    stagingPath = string.Empty;
                    var resolvedName = await _database.ResolveAvailableFileNameAsync(destinationFolderId, baseName + extension, cancellationToken).ConfigureAwait(false);
                    if (!reservedResultNames.Add(resolvedName))
                    {
                        for (var suffix = 2; ; suffix++)
                        {
                            var candidateName = $"{baseName} ({suffix}){extension}";
                            if (reservedResultNames.Add(candidateName))
                            {
                                resolvedName = candidateName;
                                break;
                            }
                        }
                    }
                    var now = DateTimeOffset.UtcNow;
                    var record = new FileRecord(fileId, destinationFolderId, resolvedName, resolvedName, managedName, hash, bytes.LongLength, mediaType,
                        FileOrigin.Generated, LibraryRecordState.Active, now, now, null, null);
                    committedFiles.Add(record);
                    managedPath = string.Empty;
                    resultFileIds.Add(fileId);
                    resultEntries.Add(new GenerationResultEntry(currentPosition, GenerationResultStatus.Committed, fileId, null));
                }
                finally
                {
                    TryDelete(stagingPath);
                    TryDelete(managedPath);
                }
                }
            }
            catch
            {
                foreach (var file in committedFiles) TryDelete(_layout.ManagedFilePath(file.ManagedName));
                throw;
            }
        }

        // Any position the provider never returned an attempt for at all (a real shortfall, distinct
        // from an attempt that came back but failed the category check above) uses the caller-supplied
        // per-position message when available — this is how a multi-job video group's real per-job
        // failure reasons reach the history record, rather than collapsing into one generic message.
        var shortfallStart = resultEntries.Count;
        for (var i = shortfallStart; i < resultCount; i++)
        {
            var messageIndex = i - shortfallStart;
            var message = childErrorMessages is not null && messageIndex < childErrorMessages.Count ? childErrorMessages[messageIndex] : (errorMessage ?? "The provider did not return a result for this position.");
            resultEntries.Add(new GenerationResultEntry(i, GenerationResultStatus.Failed, null, message));
        }

        var status = DetermineGenerationStatus(resultFileIds.Count, resultCount, wasCancelled);
        GenerationRecord generationRecord;
        try
        {
            generationRecord = await _database.CreateGenerationRecordAsync(model, connectionRecord.ProviderType, prompt, null, resultCount, status, errorMessage, destinationFolderId, resultFileIds, null, null, sourceSlots, promptImprovementRecordId, null, null, 0, cancellationToken, actualCost, actualCostCurrency, resultEntries, committedFiles, pendingReviewCandidates, existingGenerationRecordId).ConfigureAwait(false);
        }
        catch
        {
            foreach (var file in committedFiles) TryDelete(_layout.ManagedFilePath(file.ManagedName));
            foreach (var pending in pendingReviewCandidates) TryDelete(_layout.PendingReviewFilePath(pending.StagedFileName));
            throw;
        }
        return generationRecord;
    }

    private async Task<PromptImprovementRecord> RecordPromptImprovementAttemptCoreAsync(string modelId, string rawPrompt, string? guidance, string templateVersion, IReadOnlyList<string>? candidates, string? errorMessage, int? promptTokens, int? completionTokens, CancellationToken cancellationToken)
    {
        var model = await _database.GetModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        var connectionRecord = await _database.GetConnectionAsync(model.ConnectionId, cancellationToken).ConfigureAwait(false);
        var status = candidates is { Count: > 0 } ? GenerationStatus.Completed : GenerationStatus.Failed;
        return await _database.CreatePromptImprovementRecordAsync(model, connectionRecord.ProviderType, rawPrompt, guidance, templateVersion, status, errorMessage, candidates ?? [], promptTokens, completionTokens, cancellationToken).ConfigureAwait(false);
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
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ManagedFileSafety.HasMultipleLinks(path)) throw new IOException("The managed file path is a symbolic link, reparse point, or hard link; deletion is paused for review.");
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
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ManagedFileSafety.HasMultipleLinks(path)) return $"Managed content for '{file.DisplayName}' is a symbolic link, reparse point, or hard link and cannot be restored.";
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
                        case RecycleBinItemKind.Connection: await _database.RestoreConnectionAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        case RecycleBinItemKind.Model: await _database.RestoreModelAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        case RecycleBinItemKind.SavedSetting: await _database.RestoreSavedSettingAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        case RecycleBinItemKind.GenerationRecord: await _database.RestoreGenerationRecordAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
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
                        case RecycleBinItemKind.Connection: await _database.PermanentlyDeleteConnectionAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        case RecycleBinItemKind.Model: await _database.PermanentlyDeleteModelAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        case RecycleBinItemKind.SavedSetting: await _database.PermanentlyDeleteSavedSettingAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        case RecycleBinItemKind.GenerationRecord: await _database.PermanentlyDeleteGenerationRecordAsync(reference.Id, cancellationToken).ConfigureAwait(false); break;
                        default: throw new LibraryValidationException("The recycle-bin item type is not supported.");
                    }
                }
                results.Add(new RecycleBinOperationItemResult(reference, name, true, null));
            }
            catch (Exception exception) when (exception is SlopFactoryException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
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
