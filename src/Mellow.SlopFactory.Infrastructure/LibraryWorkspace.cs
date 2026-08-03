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

    public async Task<TextFileContent> ReadTextFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        const int maximumDisplayedCharacters = 1_048_576;
        var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
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

    public async Task<ImageFileContent> ReadImageFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await _database.GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
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
        return new ImageFileContent(file.MediaType, file.MediaType == "image/svg+xml" ? SvgSanitizer.Sanitize(bytes) : bytes);
    }

    private static bool IsTextMediaType(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType is "application/json" or "application/xml";

    private static bool IsImageMediaType(string mediaType) =>
        mediaType is "image/png" or "image/jpeg" or "image/webp" or "image/gif" or "image/svg+xml";

    public Task<IReadOnlyList<FileRecord>> GetActiveFilesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.GetActiveFilesAsync(cancellationToken);
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

    public Task<FolderRecord> CreateFolderAsync(string parentFolderId, string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.CreateFolderAsync(parentFolderId, name, cancellationToken);
    }

    public Task<FolderRecord> RenameFolderAsync(string folderId, string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RenameFolderAsync(folderId, name, Descriptor.RootFolderId, Descriptor.GeneratedFolderId, cancellationToken);
    }

    public Task<FolderRecord> MoveFolderAsync(string folderId, string destinationFolderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.MoveFolderAsync(folderId, destinationFolderId, Descriptor.RootFolderId, Descriptor.GeneratedFolderId, cancellationToken);
    }

    public Task<FileRecord> RenameFileAsync(string fileId, string displayName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RenameFileAsync(fileId, displayName, cancellationToken);
    }

    public Task<FileRecord> MoveFileAsync(string fileId, string destinationFolderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.MoveFileAsync(fileId, destinationFolderId, cancellationToken);
    }

    public async Task<FileRecord> DuplicateFileAsync(string fileId, string destinationFolderId, string displayName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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
            var duplicate = new FileRecord(duplicateId, destinationFolderId, normalizedName, managedName, copied.Hash, copied.Bytes, source.MediaType,
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

    public async Task<FileRecord> CreateEditedTextCopyAsync(
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
            var copy = new FileRecord(copyId, destinationFolderId, normalizedName, managedName, hash, bytes.LongLength, mediaType,
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

    public async Task<IReadOnlyList<ImportResult>> ImportAsync(IEnumerable<string> sourcePaths, string destinationFolderId, bool importDuplicates = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourcePaths);
        _ = await _database.GetFolderContentsAsync(destinationFolderId, cancellationToken).ConfigureAwait(false);
        var results = new List<ImportResult>();
        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportCandidate? candidate = null;
            string? stagingPath = null;
            string? managedPath = null;
            try
            {
                var info = new FileInfo(sourcePath);
                if (!info.Exists) throw new FileNotFoundException("The selected source file no longer exists.", sourcePath);
                var displayName = LibraryRules.NormalizeDisplayName(info.Name, "File name");
                candidate = new ImportCandidate(info.FullName, displayName, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
                var hash = await Hashing.Sha256Async(info.FullName, cancellationToken).ConfigureAwait(false);
                var matches = await _database.FindByHashAsync(hash, info.Length, cancellationToken).ConfigureAwait(false);
                if (matches.Count > 0 && !importDuplicates)
                {
                    results.Add(new ImportResult(candidate, null, ImportOutcome.DuplicateSkipped, matches, null));
                    continue;
                }

                var (mediaType, safeExtension) = await MediaTypeDetector.DetectAsync(info.FullName, cancellationToken).ConfigureAwait(false);
                var fileId = LibraryRules.NewId();
                var managedName = fileId + safeExtension;
                stagingPath = _layout.StagingFilePath(fileId + ".importing");
                managedPath = _layout.ManagedFilePath(managedName);
                var copied = await Hashing.CopyAndHashAsync(info.FullName, stagingPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(copied.Hash, hash, StringComparison.Ordinal) || copied.Bytes != info.Length)
                {
                    throw new IOException("The source file changed while it was being imported.");
                }

                File.Move(stagingPath, managedPath, false);
                stagingPath = null;
                var resolvedName = await _database.ResolveAvailableFileNameAsync(destinationFolderId, displayName, cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var record = new FileRecord(fileId, destinationFolderId, resolvedName, managedName, hash, copied.Bytes, mediaType, FileOrigin.Imported, LibraryRecordState.Active, now, now, candidate.SourceLastModified, null);
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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(stagingPath);
                TryDelete(managedPath);
                throw;
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
        return _database.SetMetadataAsync(fileId, key, kind, serializedValue, isSensitive, cancellationToken);
    }

    public Task RemoveMetadataAsync(string fileId, string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RemoveMetadataAsync(fileId, key, cancellationToken);
    }

    public Task<MetadataEntry> RenameMetadataAsync(string fileId, string currentKey, string newKey, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RenameMetadataAsync(fileId, currentKey, newKey, cancellationToken);
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
        return _database.CreateLinkAsync(sourceFileId, targetFileId, label, cancellationToken);
    }

    public Task<FileLink> RelabelLinkAsync(string linkId, string label, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RelabelLinkAsync(linkId, label, cancellationToken);
    }

    public Task<FileLink> ReverseLinkAsync(string linkId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.ReverseLinkAsync(linkId, cancellationToken);
    }

    public Task RecycleLinkAsync(string linkId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RecycleLinkAsync(linkId, cancellationToken);
    }

    public Task RestoreLinkAsync(string linkId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RestoreLinkAsync(linkId, cancellationToken);
    }

    public Task PermanentlyDeleteLinkAsync(string linkId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.PermanentlyDeleteLinkAsync(linkId, cancellationToken);
    }

    public Task RecycleFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RecycleFileAsync(fileId, cancellationToken);
    }

    public Task RecycleFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RecycleFolderAsync(folderId, Descriptor.RootFolderId, Descriptor.GeneratedFolderId, cancellationToken);
    }

    public Task RestoreFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RestoreFileAsync(fileId, cancellationToken);
    }

    public Task RestoreFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _database.RestoreFolderAsync(folderId, cancellationToken);
    }

    public async Task PermanentlyDeleteFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var file = await _database.PrepareFileDeletionAsync(fileId, cancellationToken).ConfigureAwait(false);
        var path = _layout.ManagedFilePath(file.ManagedName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
            await _database.DeleteFileRecordAsync(fileId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _database.RevertFileDeletionAsync(fileId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RenameLibraryAsync(string displayName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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
        if (!File.Exists(path)) throw new FileNotFoundException("The managed file is missing.", path);
        return path;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _libraryLock.Dispose();
        TryDelete(_layout.LockPath);
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
