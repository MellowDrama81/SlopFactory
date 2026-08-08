using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class LibraryWorkspaceTests
{
    [Fact]
    public async Task CreateInitializesManifestDatabaseAndPermanentFolders()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();

        await using var workspace = await factory.CreateAsync(root, "My Library");

        Assert.Equal("My Library", workspace.Descriptor.DisplayName);
        Assert.True(File.Exists(System.IO.Path.Combine(root, "slopfactory-library.json")));
        Assert.True(File.Exists(System.IO.Path.Combine(root, "library.sqlite3")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(root, "media")));
        var contents = await workspace.GetFolderContentsAsync(workspace.Descriptor.RootFolderId);
        Assert.Contains(contents.Folders, folder => folder.Id == workspace.Descriptor.GeneratedFolderId && folder.Name == "Generated");
        var libraryId = workspace.Descriptor.LibraryId;
        var libraryPath = workspace.Descriptor.RootPath;
        await workspace.RenameLibraryAsync("Renamed Library");
        Assert.Equal("Renamed Library", workspace.Descriptor.DisplayName);
        Assert.Equal(libraryId, workspace.Descriptor.LibraryId);
        Assert.Equal(libraryPath, workspace.Descriptor.RootPath);
        Assert.Contains("\"displayName\": \"Renamed Library\"", await File.ReadAllTextAsync(Path.Combine(root, "slopfactory-library.json")));
    }

    [Fact]
    public async Task OpenRejectsConcurrentWriterAndSucceedsAfterDispose()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        var first = await factory.CreateAsync(root);

        await Assert.ThrowsAsync<LibraryLockedException>(() => factory.OpenAsync(root));
        await first.DisposeAsync();

        await using var reopened = await factory.OpenAsync(root);
        Assert.Equal(first.Descriptor.LibraryId, reopened.Descriptor.LibraryId);
    }

    [Fact]
    public async Task CreateRejectsNonEmptyInvalidDirectory()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(temporary.Child("unrelated.txt"), "keep me");
        var factory = new LibraryWorkspaceFactory();

        await Assert.ThrowsAsync<LibraryValidationException>(() => factory.CreateAsync(temporary.Path));
        Assert.True(File.Exists(temporary.Child("unrelated.txt")));
    }

    [Fact]
    public void DefaultIntegrityReportExportContainsOnlyDefaultDiagnosticFields()
    {
        var report = new LibraryIntegrityReport("library-id", 6, new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 8, 0, 1, 0, TimeSpan.Zero), true, false,
            [new LibraryIntegrityFinding(LibraryIntegrityIssueKind.ManagedFileHashMismatch, "opaque-record", 10, 11, "The managed file content hash differs from its database record.")]);

        using var document = JsonDocument.Parse(IntegrityReportExporter.SerializeDefault(report));
        var root = document.RootElement;

        Assert.Equal("slopfactory.integrity-report/v1", root.GetProperty("format").GetString());
        Assert.Equal("library-id", root.GetProperty("libraryId").GetString());
        var finding = root.GetProperty("findings")[0];
        Assert.Equal("ManagedFileHashMismatch", finding.GetProperty("category").GetString());
        Assert.False(root.TryGetProperty("contentHash", out _));
        Assert.False(root.TryGetProperty("displayName", out _));
        Assert.False(root.TryGetProperty("managedPath", out _));
    }

    [Fact]
    public async Task CreateRejectsWindowsNetworkPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var factory = new LibraryWorkspaceFactory();

        var exception = await Assert.ThrowsAsync<LibraryValidationException>(() => factory.CreateAsync(@"\\example.invalid\SlopFactory\Library"));

        Assert.Contains("Network locations", exception.Message);
    }

    [Fact]
    public async Task OpeningRejectsManagedDirectoryReplacedByARegularFile()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        var mediaPath = Path.Combine(root, "media");
        Directory.Delete(mediaPath);
        await File.WriteAllTextAsync(mediaPath, "not a directory");

        var exception = await Assert.ThrowsAsync<LibraryValidationException>(() => factory.OpenAsync(root));

        Assert.Contains("managed-media directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportCopiesBytesAndSkipsDuplicateByDefault()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var source = temporary.Child("sample.txt");
        await File.WriteAllTextAsync(source, "SlopFactory test content", Encoding.UTF8);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);

        var first = await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId);
        var duplicate = await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId);

        var imported = Assert.Single(first);
        Assert.Equal(ImportOutcome.Imported, imported.Outcome);
        Assert.NotNull(imported.File);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Equal(expectedHash, imported.File.ContentHash);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(workspace.GetManagedFilePath(imported.File)));
        Assert.Equal(ImportOutcome.DuplicateSkipped, Assert.Single(duplicate).Outcome);
        Assert.Single(Assert.Single(duplicate).Matches);
    }

    [Fact]
    public async Task ExplicitDuplicateImportCreatesDistinctManagedFileAndSuffixName()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("sample.txt");
        await File.WriteAllTextAsync(source, "same bytes");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));

        var first = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var second = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId, importDuplicates: true)).File!;

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.ManagedName, second.ManagedName);
        Assert.Equal("sample (2).txt", second.DisplayName);
    }

    [Fact]
    public async Task ProgressImportCancellationCleansActiveStagingAndReportsRemainingItems()
    {
        using var temporary = new TemporaryDirectory();
        var firstPath = temporary.Child("first.bin");
        var secondPath = temporary.Child("second.bin");
        await File.WriteAllBytesAsync(firstPath, new byte[2_500_000]);
        await File.WriteAllBytesAsync(secondPath, new byte[32]);
        var factory = new LibraryWorkspaceFactory();
        var root = temporary.Child("library");
        await using var workspace = await factory.CreateAsync(root);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ImportProgress>(value =>
        {
            if (value.Stage == "Copying into managed storage" && value.BytesProcessed > 0) cancellation.Cancel();
        });

        var results = await workspace.ImportWithProgressAsync([firstPath, secondPath], workspace.Descriptor.RootFolderId, false, progress, cancellation.Token);

        Assert.Equal([ImportOutcome.Cancelled, ImportOutcome.Cancelled], results.Select(result => result.Outcome));
        Assert.Empty(await workspace.GetActiveFilesAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ".staging")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "media")));
    }

    [Fact]
    public async Task MetadataLinksAndRecycleStateRemainConsistent()
    {
        using var temporary = new TemporaryDirectory();
        var sourceA = temporary.Child("a.txt");
        var sourceB = temporary.Child("b.txt");
        await File.WriteAllTextAsync(sourceA, "a");
        await File.WriteAllTextAsync(sourceB, "b");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var imports = await workspace.ImportAsync([sourceA, sourceB], workspace.Descriptor.RootFolderId);
        var fileA = imports[0].File!;
        var fileB = imports[1].File!;

        var importedModifiedAt = fileA.ModifiedAt;
        await Task.Delay(5);
        var metadata = await workspace.SetMetadataAsync(fileA.Id, "Rating", MetadataValueKind.Number, "4.5", false);
        var setModifiedAt = (await workspace.GetFileAsync(fileA.Id)).ModifiedAt;
        await Task.Delay(5);
        var renamedMetadata = await workspace.RenameMetadataAsync(fileA.Id, "Rating", "Score");
        var renamedModifiedAt = (await workspace.GetFileAsync(fileA.Id)).ModifiedAt;
        await workspace.SetMetadataAsync(fileA.Id, "Temporary", MetadataValueKind.Boolean, "true", false);
        await Task.Delay(5);
        var beforeRemove = (await workspace.GetFileAsync(fileA.Id)).ModifiedAt;
        await workspace.RemoveMetadataAsync(fileA.Id, "Temporary");
        var removedModifiedAt = (await workspace.GetFileAsync(fileA.Id)).ModifiedAt;
        var link = await workspace.CreateLinkAsync(fileA.Id, fileB.Id, "variation of");
        var relabelled = await workspace.RelabelLinkAsync(link.Id, "source for");
        var reversed = await workspace.ReverseLinkAsync(link.Id);
        _ = await workspace.CreateLinkAsync(fileA.Id, fileB.Id, "source for");
        await Assert.ThrowsAsync<NameConflictException>(() => workspace.ReverseLinkAsync(link.Id));
        await workspace.RecycleLinkAsync(link.Id);
        Assert.True(Assert.Single(await workspace.GetRecycledLinksAsync(), item => item.Id == link.Id).ExplicitlyRecycled);
        await workspace.RestoreLinkAsync(link.Id);
        await workspace.RecycleFileAsync(fileA.Id);

        Assert.Equal("Rating", metadata.Key);
        Assert.Equal("Score", renamedMetadata.Key);
        Assert.True(setModifiedAt > importedModifiedAt);
        Assert.True(renamedModifiedAt > setModifiedAt);
        Assert.True(removedModifiedAt > beforeRemove);
        Assert.Equal("source for", relabelled.Label);
        Assert.Equal(fileB.Id, reversed.SourceFileId);
        Assert.Equal(fileA.Id, reversed.TargetFileId);
        Assert.True((await workspace.GetMetadataAsync(fileA.Id)).Single().SerializedValue == "4.5");
        Assert.Equal(LibraryRecordState.Recycled, (await workspace.GetLinksAsync(fileB.Id)).Single(item => item.Id == link.Id).State);
        Assert.DoesNotContain(await workspace.GetRecycledLinksAsync(), item => item.Id == link.Id);
        Assert.Contains(await workspace.GetRecycledFilesAsync(), file => file.Id == fileA.Id);

        await workspace.RestoreFileAsync(fileA.Id);
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetLinksAsync(fileB.Id)).Single(item => item.Id == link.Id).State);
        await workspace.RecycleLinkAsync(link.Id);
        await workspace.PermanentlyDeleteLinkAsync(link.Id);
        Assert.DoesNotContain(await workspace.GetLinksAsync(fileB.Id), item => item.Id == link.Id);
    }

    [Fact]
    public async Task BulkFileActionsCommitIndependentlyAndReportFailures()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var paths = new[] { temporary.Child("alpha.txt"), temporary.Child("beta.txt"), temporary.Child("gamma.txt") };
        foreach (var path in paths) await File.WriteAllTextAsync(path, Path.GetFileName(path));
        var files = (await workspace.ImportAsync(paths, workspace.Descriptor.RootFolderId)).Select(result => result.File!).ToArray();
        await workspace.RecycleFileAsync(files[2].Id);

        var set = await workspace.SetMetadataForFilesAsync(files.Select(file => file.Id).ToArray(), "Reviewed", MetadataValueKind.Boolean, "true", true);

        Assert.Equal(2, set.SucceededCount);
        Assert.Equal(1, set.FailedCount);
        foreach (var file in files[..2]) Assert.Equal("true", Assert.Single(await workspace.GetMetadataAsync(file.Id)).SerializedValue);
        Assert.Empty(await workspace.GetMetadataAsync(files[2].Id));

        var removed = await workspace.RemoveMetadataFromFilesAsync(files[..2].Select(file => file.Id).ToArray(), "Reviewed");
        Assert.Equal(2, removed.SucceededCount);
        foreach (var file in files[..2]) Assert.Empty(await workspace.GetMetadataAsync(file.Id));

        var destination = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Destination");
        var conflictPath = temporary.Child("conflict-alpha.txt");
        await File.WriteAllTextAsync(conflictPath, "conflict");
        var conflict = Assert.Single(await workspace.ImportAsync([conflictPath], destination.Id)).File!;
        await workspace.RenameFileAsync(conflict.Id, files[0].DisplayName);
        var moved = await workspace.MoveFilesAsync(files[..2].Select(file => file.Id).ToArray(), destination.Id);

        Assert.Equal(1, moved.SucceededCount);
        Assert.Equal(files[0].Id, Assert.Single(moved.Items, item => !item.Succeeded).FileId);
        Assert.Equal(workspace.Descriptor.RootFolderId, (await workspace.GetFileAsync(files[0].Id)).FolderId);
        Assert.Equal(destination.Id, (await workspace.GetFileAsync(files[1].Id)).FolderId);

        var recycled = await workspace.RecycleFilesAsync(files[..2].Select(file => file.Id).ToArray());
        Assert.Equal(2, recycled.SucceededCount);
        foreach (var file in files[..2]) Assert.Equal(LibraryRecordState.Recycled, (await workspace.GetFileAsync(file.Id)).State);
    }

    [Fact]
    public async Task RecyclingFolderIncludesDescendantsAndPermanentDeletionRemovesBytes()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("nested.txt");
        await File.WriteAllTextAsync(source, "nested");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var folder = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Folder");
        var file = Assert.Single(await workspace.ImportAsync([source], folder.Id)).File!;
        var managedPath = workspace.GetManagedFilePath(file);

        await workspace.RecycleFolderAsync(folder.Id);
        Assert.Contains(await workspace.GetRecycledFoldersAsync(), item => item.Id == folder.Id);
        Assert.Contains(await workspace.GetRecycledFilesAsync(), item => item.Id == file.Id);
        Assert.Equal(folder.Id, Assert.Single(await workspace.GetRecycleBinFoldersAsync()).Id);
        Assert.DoesNotContain(await workspace.GetRecycleBinFilesAsync(), item => item.Id == file.Id);
        await workspace.RestoreFolderAsync(folder.Id);

        await workspace.RecycleFileAsync(file.Id);
        await workspace.PermanentlyDeleteFileAsync(file.Id);
        Assert.False(File.Exists(managedPath));
        Assert.DoesNotContain(await workspace.GetRecycledFilesAsync(), item => item.Id == file.Id);
    }

    [Fact]
    public async Task PermanentFolderDeletionRemovesSubtreeBytesRecordsAndLinks()
    {
        using var temporary = new TemporaryDirectory();
        var sourceA = temporary.Child("inside-a.txt");
        var sourceB = temporary.Child("inside-b.txt");
        var outsideSource = temporary.Child("outside.txt");
        await File.WriteAllTextAsync(sourceA, "a");
        await File.WriteAllTextAsync(sourceB, "b");
        await File.WriteAllTextAsync(outsideSource, "outside");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var parent = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Parent");
        var child = await workspace.CreateFolderAsync(parent.Id, "Child");
        var fileA = Assert.Single(await workspace.ImportAsync([sourceA], parent.Id)).File!;
        var fileB = Assert.Single(await workspace.ImportAsync([sourceB], child.Id)).File!;
        var outside = Assert.Single(await workspace.ImportAsync([outsideSource], workspace.Descriptor.RootFolderId)).File!;
        var pathA = workspace.GetManagedFilePath(fileA);
        var pathB = workspace.GetManagedFilePath(fileB);
        var link = await workspace.CreateLinkAsync(outside.Id, fileB.Id, "contains");

        await workspace.RecycleFolderAsync(parent.Id);
        await workspace.PermanentlyDeleteFolderAsync(parent.Id);

        Assert.False(File.Exists(pathA));
        Assert.False(File.Exists(pathB));
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetFileAsync(fileA.Id));
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetFileAsync(fileB.Id));
        Assert.DoesNotContain(await workspace.GetLinksAsync(outside.Id), item => item.Id == link.Id);
        Assert.Empty(await workspace.GetRecycleBinFoldersAsync());
    }

    [Fact]
    public async Task FailedPermanentFileDeletionRemainsPendingAndCanBeRetried()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var source = temporary.Child("pending.txt");
        await File.WriteAllTextAsync(source, "pending");
        var factory = new LibraryWorkspaceFactory();
        string fileId;
        string managedPath;
        await using (var workspace = await factory.CreateAsync(root))
        {
            var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
            fileId = file.Id;
            managedPath = workspace.GetManagedFilePath(file);
            await workspace.RecycleFileAsync(file.Id);
            File.Delete(managedPath);
            Directory.CreateDirectory(managedPath);

            await Assert.ThrowsAsync<IOException>(() => workspace.PermanentlyDeleteFileAsync(file.Id));

            var pending = Assert.Single(await workspace.GetRecycleBinEntriesAsync());
            Assert.Equal(LibraryRecordState.PendingPermanentDeletion, pending.State);
            Assert.NotNull(pending.DeletionFailure);
            Assert.Contains("replaced by a directory", pending.DeletionFailure.SanitizedError, StringComparison.Ordinal);
            Assert.DoesNotContain(root, pending.DeletionFailure.SanitizedError, StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RestoreFileAsync(file.Id));
        }

        await using var reopened = await factory.OpenAsync(root);
        var persisted = Assert.Single(await reopened.GetRecycleBinEntriesAsync());
        Assert.NotNull(persisted.DeletionFailure);
        Assert.Equal(fileId, persisted.Reference.Id);
        Directory.Delete(managedPath);
        await reopened.PermanentlyDeleteFileAsync(fileId);
        Assert.Empty(await reopened.GetRecycleBinEntriesAsync());
    }

    [Fact]
    public async Task FailedPermanentFolderDeletionRemainsPendingAndCanBeRetried()
    {
        using var temporary = new TemporaryDirectory();
        var sourceA = temporary.Child("folder-pending-a.txt");
        var sourceB = temporary.Child("folder-pending-b.txt");
        await File.WriteAllTextAsync(sourceA, "a");
        await File.WriteAllTextAsync(sourceB, "b");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var folder = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Pending folder");
        var fileA = Assert.Single(await workspace.ImportAsync([sourceA], folder.Id)).File!;
        var fileB = Assert.Single(await workspace.ImportAsync([sourceB], folder.Id)).File!;
        var unsafePath = workspace.GetManagedFilePath(fileB);
        File.Delete(unsafePath);
        Directory.CreateDirectory(unsafePath);
        await workspace.RecycleFolderAsync(folder.Id);

        await Assert.ThrowsAsync<IOException>(() => workspace.PermanentlyDeleteFolderAsync(folder.Id));

        var pending = Assert.Single(await workspace.GetRecycleBinFoldersAsync());
        Assert.Equal(LibraryRecordState.PendingPermanentDeletion, pending.State);
        var pendingEntry = Assert.Single(await workspace.GetRecycleBinEntriesAsync());
        Assert.NotNull(pendingEntry.DeletionFailure);
        Assert.Contains("replaced by a directory", pendingEntry.DeletionFailure.SanitizedError, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetRecycleBinFilesAsync());
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RestoreFolderAsync(folder.Id));

        Directory.Delete(unsafePath);
        await workspace.PermanentlyDeleteFolderAsync(folder.Id);
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetFileAsync(fileA.Id));
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetFileAsync(fileB.Id));
        Assert.Empty(await workspace.GetRecycleBinFoldersAsync());
    }

    [Fact]
    public async Task RecycleBinEntriesIncludeOriginalLocationsAndFolderCascadeCounts()
    {
        using var temporary = new TemporaryDirectory();
        var sourceA = temporary.Child("summary-a.txt");
        var sourceB = temporary.Child("summary-b.txt");
        var outsideSource = temporary.Child("summary-outside.txt");
        await File.WriteAllTextAsync(sourceA, "a");
        await File.WriteAllTextAsync(sourceB, "b");
        await File.WriteAllTextAsync(outsideSource, "outside");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var parent = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Summary parent");
        var child = await workspace.CreateFolderAsync(parent.Id, "Summary child");
        _ = Assert.Single(await workspace.ImportAsync([sourceA], parent.Id)).File!;
        var childFile = Assert.Single(await workspace.ImportAsync([sourceB], child.Id)).File!;
        var outside = Assert.Single(await workspace.ImportAsync([outsideSource], workspace.Descriptor.RootFolderId)).File!;
        await workspace.CreateLinkAsync(outside.Id, childFile.Id, "references");

        await workspace.RecycleFolderAsync(parent.Id);

        var entry = Assert.Single(await workspace.GetRecycleBinEntriesAsync());
        Assert.Equal(new RecycleBinItemReference(RecycleBinItemKind.Folder, parent.Id), entry.Reference);
        Assert.Equal("Library", entry.OriginalLocation);
        Assert.Equal(2, entry.OwnedFolderCount);
        Assert.Equal(2, entry.OwnedFileCount);
        Assert.Equal(1, entry.OwnedLinkCount);
    }

    [Fact]
    public async Task BatchRestoreAndEmptyRecycleBinOrderLinksSafely()
    {
        using var temporary = new TemporaryDirectory();
        var sourceA = temporary.Child("batch-a.txt");
        var sourceB = temporary.Child("batch-b.txt");
        await File.WriteAllTextAsync(sourceA, "a");
        await File.WriteAllTextAsync(sourceB, "b");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var fileA = Assert.Single(await workspace.ImportAsync([sourceA], workspace.Descriptor.RootFolderId)).File!;
        var fileB = Assert.Single(await workspace.ImportAsync([sourceB], workspace.Descriptor.RootFolderId)).File!;
        var link = await workspace.CreateLinkAsync(fileA.Id, fileB.Id, "batch link");
        await workspace.RecycleLinkAsync(link.Id);
        await workspace.RecycleFileAsync(fileA.Id);
        var references = (await workspace.GetRecycleBinEntriesAsync()).Select(entry => entry.Reference).ToArray();

        var restored = await workspace.RestoreRecycleBinItemsAsync(references);

        Assert.Equal(2, restored.SucceededCount);
        Assert.Equal(0, restored.FailedCount);
        Assert.Empty(await workspace.GetRecycleBinEntriesAsync());
        Assert.Equal(LibraryRecordState.Active, Assert.Single(await workspace.GetLinksAsync(fileA.Id)).State);

        await workspace.RecycleLinkAsync(link.Id);
        await workspace.RecycleFileAsync(fileA.Id);
        var emptied = await workspace.EmptyRecycleBinAsync();

        Assert.Equal(2, emptied.SucceededCount);
        Assert.Equal(0, emptied.FailedCount);
        Assert.Empty(await workspace.GetRecycleBinEntriesAsync());
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetFileAsync(fileA.Id));
        Assert.Empty(await workspace.GetLinksAsync(fileB.Id));
    }

    [Fact]
    public async Task BatchPermanentDeletionContinuesAfterAnItemFails()
    {
        using var temporary = new TemporaryDirectory();
        var blockedSource = temporary.Child("batch-blocked.txt");
        var deletableSource = temporary.Child("batch-deletable.txt");
        await File.WriteAllTextAsync(blockedSource, "blocked");
        await File.WriteAllTextAsync(deletableSource, "deletable");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var blocked = Assert.Single(await workspace.ImportAsync([blockedSource], workspace.Descriptor.RootFolderId)).File!;
        var deletable = Assert.Single(await workspace.ImportAsync([deletableSource], workspace.Descriptor.RootFolderId)).File!;
        await workspace.RecycleFileAsync(blocked.Id);
        await workspace.RecycleFileAsync(deletable.Id);
        var blockedPath = workspace.GetManagedFilePath(blocked);
        File.Delete(blockedPath);
        Directory.CreateDirectory(blockedPath);

        var result = await workspace.PermanentlyDeleteRecycleBinItemsAsync([
            new RecycleBinItemReference(RecycleBinItemKind.File, blocked.Id),
            new RecycleBinItemReference(RecycleBinItemKind.File, deletable.Id)]);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains(result.Items, item => item.Reference.Id == blocked.Id && !item.Succeeded && item.Error is not null);
        Assert.Contains(await workspace.GetRecycleBinEntriesAsync(), item => item.Reference.Id == blocked.Id && item.State == LibraryRecordState.PendingPermanentDeletion);
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetFileAsync(deletable.Id));
        Directory.Delete(blockedPath);
    }

    [Fact]
    public async Task RestorePreviewReportsFileAndFolderNameConflictsBeforeMutation()
    {
        using var temporary = new TemporaryDirectory();
        var originalSource = temporary.Child("conflict.txt");
        await File.WriteAllTextAsync(originalSource, "original");
        var replacementDirectory = temporary.Child("replacement");
        Directory.CreateDirectory(replacementDirectory);
        var replacementSource = Path.Combine(replacementDirectory, "conflict.txt");
        await File.WriteAllTextAsync(replacementSource, "replacement");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var original = Assert.Single(await workspace.ImportAsync([originalSource], workspace.Descriptor.RootFolderId)).File!;
        var recycledFolder = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Conflicting folder");
        await workspace.RecycleFileAsync(original.Id);
        await workspace.RecycleFolderAsync(recycledFolder.Id);
        _ = Assert.Single(await workspace.ImportAsync([replacementSource], workspace.Descriptor.RootFolderId)).File!;
        _ = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Conflicting folder");

        var preview = await workspace.GetRecycleBinRestorePreviewAsync([
            new RecycleBinItemReference(RecycleBinItemKind.File, original.Id),
            new RecycleBinItemReference(RecycleBinItemKind.Folder, recycledFolder.Id)]);

        Assert.Equal(0, preview.RestorableCount);
        Assert.Equal(2, preview.BlockedCount);
        Assert.All(preview.Items, item => Assert.Contains(item.BlockingReasons, reason => reason.Contains("conflict", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(LibraryRecordState.Recycled, (await workspace.GetFileAsync(original.Id)).State);
        await Assert.ThrowsAsync<NameConflictException>(() => workspace.RestoreFileAsync(original.Id));
        Assert.Equal(LibraryRecordState.Recycled, (await workspace.GetFileAsync(original.Id)).State);
    }

    [Fact]
    public async Task RestorePreviewResolvesSelectedLinkEndpointsAndBlocksMissingManagedContent()
    {
        using var temporary = new TemporaryDirectory();
        var sourceA = temporary.Child("restore-a.txt");
        var sourceB = temporary.Child("restore-b.txt");
        await File.WriteAllTextAsync(sourceA, "a");
        await File.WriteAllTextAsync(sourceB, "b");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var fileA = Assert.Single(await workspace.ImportAsync([sourceA], workspace.Descriptor.RootFolderId)).File!;
        var fileB = Assert.Single(await workspace.ImportAsync([sourceB], workspace.Descriptor.RootFolderId)).File!;
        var link = await workspace.CreateLinkAsync(fileA.Id, fileB.Id, "restore dependency");
        await workspace.RecycleLinkAsync(link.Id);
        await workspace.RecycleFileAsync(fileA.Id);
        var fileReference = new RecycleBinItemReference(RecycleBinItemKind.File, fileA.Id);
        var linkReference = new RecycleBinItemReference(RecycleBinItemKind.FileLink, link.Id);

        var linkOnly = await workspace.GetRecycleBinRestorePreviewAsync([linkReference]);
        Assert.False(Assert.Single(linkOnly.Items).CanRestore);

        var together = await workspace.GetRecycleBinRestorePreviewAsync([fileReference, linkReference]);
        Assert.All(together.Items, item => Assert.True(item.CanRestore));

        File.Delete(workspace.GetManagedFilePath(fileA));
        var missing = await workspace.GetRecycleBinRestorePreviewAsync([fileReference, linkReference]);
        Assert.Equal(2, missing.BlockedCount);
        Assert.Contains(missing.Items.Single(item => item.Entry.Reference == fileReference).BlockingReasons, reason => reason.Contains("missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(missing.Items.Single(item => item.Entry.Reference == linkReference).BlockingReasons, reason => reason.Contains("included", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RestoreFileAsync(fileA.Id));
    }

    [Fact]
    public async Task FilesAndFolderSubtreesCanBeRenamedAndMovedWithoutMovingManagedBytes()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "organization");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var parent = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Parent");
        var child = await workspace.CreateFolderAsync(parent.Id, "Child");
        var destination = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Destination");
        var imported = Assert.Single(await workspace.ImportAsync([source], parent.Id)).File!;
        var managedPath = workspace.GetManagedFilePath(imported);

        var renamedFile = await workspace.RenameFileAsync(imported.Id, "renamed.txt");
        var movedFile = await workspace.MoveFileAsync(imported.Id, destination.Id);
        var renamedChild = await workspace.RenameFolderAsync(child.Id, "Renamed Child");
        var movedChild = await workspace.MoveFolderAsync(child.Id, destination.Id);

        Assert.Equal("renamed.txt", renamedFile.DisplayName);
        Assert.Equal(destination.Id, movedFile.FolderId);
        Assert.Equal("Renamed Child", renamedChild.Name);
        Assert.Equal(destination.Id, movedChild.ParentId);
        Assert.Equal(managedPath, workspace.GetManagedFilePath(await workspace.GetFileAsync(imported.Id)));
        Assert.Empty((await workspace.GetFolderContentsAsync(parent.Id)).Files);
        Assert.Contains((await workspace.GetFolderContentsAsync(destination.Id)).Files, file => file.Id == imported.Id);

        await workspace.MoveFolderAsync(child.Id, parent.Id);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.MoveFolderAsync(parent.Id, child.Id));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RenameFolderAsync(workspace.Descriptor.GeneratedFolderId, "Other"));
    }

    [Fact]
    public async Task DuplicateStreamsNewManagedFileAndCopiesMetadataButNotLinks()
    {
        using var temporary = new TemporaryDirectory();
        var sourceA = temporary.Child("a.txt");
        var sourceB = temporary.Child("b.txt");
        await File.WriteAllTextAsync(sourceA, "duplicate me");
        await File.WriteAllTextAsync(sourceB, "link target");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var destination = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Copies");
        var imported = await workspace.ImportAsync([sourceA, sourceB], workspace.Descriptor.RootFolderId);
        var source = imported[0].File!;
        var target = imported[1].File!;
        await workspace.SetMetadataAsync(source.Id, "Note", MetadataValueKind.Text, "copied", true);
        await workspace.CreateLinkAsync(source.Id, target.Id, "related");

        var duplicate = await workspace.DuplicateFileAsync(source.Id, destination.Id, "copy.txt");

        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.NotEqual(source.ManagedName, duplicate.ManagedName);
        Assert.Equal(FileOrigin.UserCopy, duplicate.Origin);
        Assert.Equal(source.ContentHash, duplicate.ContentHash);
        Assert.Equal(await File.ReadAllBytesAsync(workspace.GetManagedFilePath(source)), await File.ReadAllBytesAsync(workspace.GetManagedFilePath(duplicate)));
        var copiedMetadata = Assert.Single(await workspace.GetMetadataAsync(duplicate.Id));
        Assert.Equal("Note", copiedMetadata.Key);
        Assert.True(copiedMetadata.IsSensitive);
        Assert.Empty(await workspace.GetLinksAsync(duplicate.Id));
        await Assert.ThrowsAsync<NameConflictException>(() => workspace.DuplicateFileAsync(source.Id, destination.Id, "copy.txt"));
    }

    [Fact]
    public async Task ImportRejectsDirectorySourcesWithoutBlockingOtherFiles()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("safe.txt");
        var directory = temporary.Child("selected-folder");
        await File.WriteAllTextAsync(source, "safe");
        Directory.CreateDirectory(directory);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));

        var results = await workspace.ImportAsync([directory, source], workspace.Descriptor.RootFolderId);

        Assert.Equal(ImportOutcome.Failed, results[0].Outcome);
        Assert.Contains("Folders cannot", results[0].Error);
        Assert.Equal(ImportOutcome.Imported, results[1].Outcome);
        Assert.Single(await workspace.GetActiveFilesAsync());
    }

    [Fact]
    public async Task TextViewerReadsStrictUtf8AndBoundsDisplayedContent()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("sample.cs");
        await File.WriteAllTextAsync(source, new string('x', 1_048_577), new UTF8Encoding(false));
        var invalid = temporary.Child("invalid.txt");
        await File.WriteAllBytesAsync(invalid, [0xC3, 0x28]);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var imports = await workspace.ImportAsync([source, invalid], workspace.Descriptor.RootFolderId);

        var content = await workspace.ReadTextFileAsync(imports[0].File!.Id);

        Assert.Equal("text/x-csharp", imports[0].File!.MediaType);
        Assert.Equal(1_048_576, content.Content.Length);
        Assert.True(content.IsTruncated);
        Assert.Equal("UTF-8", content.EncodingName);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReadTextFileAsync(imports[1].File!.Id));
    }

    [Fact]
    public async Task TextSearchScansBeyondDisplayedPrefixAndBoundsReturnedMatches()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("large.txt");
        var content = new string('a', 32_766) + "NEEDLE" + new string('b', 1_020_000) + "needle";
        await File.WriteAllTextAsync(source, content, new UTF8Encoding(false));
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;

        var insensitive = await workspace.SearchTextFileAsync(file.Id, "needle", matchCase: false, maximumResults: 1);
        var sensitive = await workspace.SearchTextFileAsync(file.Id, "needle", matchCase: true);

        Assert.Equal(2, insensitive.TotalMatches);
        Assert.Single(insensitive.Matches);
        Assert.True(insensitive.ResultsTruncated);
        Assert.Contains("NEEDLE", insensitive.Matches[0].Snippet, StringComparison.Ordinal);
        Assert.Single(sensitive.Matches);
        Assert.True(sensitive.Matches[0].CharacterOffset > 1_048_576);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.SearchTextFileAsync(file.Id, string.Empty));
    }

    [Fact]
    public async Task MarkdownRendererEmitsOnlyEncodedStaticMarkupAndSeparatesExternalLinks()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("unsafe.md");
        await File.WriteAllTextAsync(source, """
            # Heading

            <script>alert('raw html')</script>

            ![tracker](https://example.com/tracker.png)
            [Official site](https://example.com/path?q=1)
            [Unsafe](javascript:alert(1))

            - **Strong** item
            - `code`
            """);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;

        var rendered = await workspace.RenderMarkdownFileAsync(file.Id);

        Assert.Contains("<h1>Heading</h1>", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", rendered.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", rendered.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", rendered.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<strong>Strong</strong>", rendered.Html, StringComparison.Ordinal);
        var link = Assert.Single(rendered.ExternalLinks);
        Assert.Equal("Official site", link.Label);
        Assert.Equal("https://example.com/path?q=1", link.Destination);
    }

    [Fact]
    public async Task EditAsCopyWritesUtf8WithoutChangingOriginalAndHonorsMetadataChoices()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("note.md");
        await File.WriteAllTextAsync(sourcePath, "# Original", new UTF8Encoding(false));
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var destination = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Edited");
        var source = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;
        await workspace.SetMetadataAsync(source.Id, "Category", MetadataValueKind.Text, "draft", false);
        await workspace.SetMetadataAsync(source.Id, "Private", MetadataValueKind.Text, "secret", true);
        var originalBytes = await File.ReadAllBytesAsync(workspace.GetManagedFilePath(source));

        var ordinaryCopy = await workspace.CreateEditedTextCopyAsync(source.Id, destination.Id, "note edited.md", "# Edited\n", TextCopyFormat.PreserveSourceFormat, true, false);
        var sensitiveCopy = await workspace.CreateEditedTextCopyAsync(source.Id, destination.Id, "note private.md", "# Private\n", TextCopyFormat.Markdown, true, true);
        var cleanCopy = await workspace.CreateEditedTextCopyAsync(source.Id, destination.Id, "note clean.txt", "Plain\n", TextCopyFormat.PlainText, false, false);

        Assert.Equal(FileOrigin.EditedCopy, ordinaryCopy.Origin);
        Assert.Equal("text/markdown", ordinaryCopy.MediaType);
        Assert.Equal("# Edited\n", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(workspace.GetManagedFilePath(ordinaryCopy))));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(workspace.GetManagedFilePath(source)));
        Assert.Equal("Category", Assert.Single(await workspace.GetMetadataAsync(ordinaryCopy.Id)).Key);
        Assert.Equal(2, (await workspace.GetMetadataAsync(sensitiveCopy.Id)).Count);
        Assert.Empty(await workspace.GetMetadataAsync(cleanCopy.Id));
        Assert.Equal("text/plain", cleanCopy.MediaType);
        Assert.EndsWith(".txt", cleanCopy.ManagedName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditAsCopyValidatesPreservedStructuredFormatsBeforeWriting()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("data.json");
        await File.WriteAllTextAsync(sourcePath, "{\"valid\":true}");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var source = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CreateEditedTextCopyAsync(
            source.Id, workspace.Descriptor.RootFolderId, "invalid.json", "{", TextCopyFormat.PreserveSourceFormat, false, false));

        Assert.DoesNotContain((await workspace.GetFolderContentsAsync(workspace.Descriptor.RootFolderId)).Files, file => file.DisplayName == "invalid.json");
    }

    [Fact]
    public async Task RasterImageViewerReturnsVerifiedManagedBytes()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("pixel.png");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(sourcePath, png);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        var image = await workspace.ReadImageFileAsync(file.Id);
        var properties = await workspace.GetImageTechnicalPropertiesAsync(file.Id);

        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(png, image.Bytes);
        Assert.Equal(1, properties.Width);
        Assert.Equal(1, properties.Height);
        await File.WriteAllBytesAsync(workspace.GetManagedFilePath(file), [0x89, 0x50, 0x4E, 0x47]);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReadImageFileAsync(file.Id));
    }

    [Fact]
    public async Task RasterImageViewerRejectsUnsafeDeclaredDimensionsBeforeBrowserDecode()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("oversized.png");
        var pngHeader = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(pngHeader, 0);
        "IHDR"u8.CopyTo(pngHeader.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(16, 4), 50_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(20, 4), 50_000);
        await File.WriteAllBytesAsync(sourcePath, pngHeader);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        var exception = await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReadImageFileAsync(file.Id));

        Assert.Contains("Preview Too Complex or Large", exception.Message, StringComparison.Ordinal);
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetFileAsync(file.Id)).State);
    }

    [Fact]
    public async Task MediaPlaybackVerifiesContentAndReturnsBoundedSeekRanges()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("sample.wav");
        var wav = new byte[128];
        "RIFF"u8.CopyTo(wav);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        for (var index = 12; index < wav.Length; index++) wav[index] = (byte)index;
        await File.WriteAllBytesAsync(sourcePath, wav);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        var playback = await workspace.PrepareMediaPlaybackAsync(file.Id);
        await using var range = await workspace.OpenMediaRangeAsync(file.Id, playback.ContentHash, 25, 17);
        var bytes = new byte[32];
        var read = await range.ReadAsync(bytes);

        Assert.Equal("audio/wav", playback.MediaType);
        Assert.Equal(17, read);
        Assert.Equal(wav.AsSpan(25, 17).ToArray(), bytes.AsSpan(0, read).ToArray());
        Assert.Equal(0, await range.ReadAsync(bytes));
        await range.DisposeAsync();

        wav[30] ^= 0xFF;
        await File.WriteAllBytesAsync(workspace.GetManagedFilePath(file), wav);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.PrepareMediaPlaybackAsync(file.Id));
    }

    [Theory]
    [InlineData("track.aac", "//FQ", "audio/aac")]
    [InlineData("track.flac", "ZkxhQw==", "audio/flac")]
    public async Task ImportDetectsAdditionalSupportedAudioSignatures(string name, string base64Prefix, string expectedMediaType)
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child(name);
        var bytes = Convert.FromBase64String(base64Prefix).Concat(new byte[64]).ToArray();
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));

        var file = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        Assert.Equal(expectedMediaType, file.MediaType);
    }

    [Fact]
    public async Task SvgViewerRemovesActiveContentAndExternalReferences()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("unsafe.svg");
        await File.WriteAllTextAsync(sourcePath, """
            <svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)" viewBox="0 0 10 10">
              <script>alert(2)</script>
              <image href="https://example.com/tracker.png" />
              <path d="M0 0L10 10" style="filter:url(https://example.com/x)" fill="url(https://example.com/y)" />
              <use href="#local" />
            </svg>
            """);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;

        var image = await workspace.ReadImageFileAsync(file.Id);
        var sanitized = Encoding.UTF8.GetString(image.Bytes);

        Assert.Equal("image/svg+xml", image.MediaType);
        Assert.DoesNotContain("script", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https:", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<path", sanitized, StringComparison.Ordinal);
        Assert.Contains("href=\"#local\"", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataValidationRejectsDuplicateJsonPropertiesAndReservedKeys()
    {
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateMetadataValue(MetadataValueKind.Json, "{\"a\":1,\"a\":2}"));
        Assert.Throws<LibraryValidationException>(() => LibraryRules.NormalizeMetadataKey("slopfactory.secret"));
        Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateMetadataValue(MetadataValueKind.DateTime, "2026-08-03T12:00:00"));
        Assert.Equal("CON.txt", LibraryRules.NormalizeDisplayName("CON.txt"));
    }

    [Fact]
    public async Task IntegrityScanHashesRecycledManagedFilesWithoutMutatingRecords()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("integrity-recycled.txt");
        await File.WriteAllTextAsync(source, "original");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        await workspace.RecycleFileAsync(file.Id);

        var clean = await workspace.RunIntegrityScanAsync();

        Assert.True(clean.IsComplete);
        Assert.False(clean.WasCancelled);
        Assert.Empty(clean.Findings);

        await File.WriteAllTextAsync(workspace.GetManagedFilePath(file), "changed content");
        var changed = await workspace.RunIntegrityScanAsync();

        Assert.Contains(changed.Findings, finding => finding.Kind == LibraryIntegrityIssueKind.ManagedFileHashMismatch && finding.RecordId == file.Id);
        Assert.Equal(LibraryRecordState.Recycled, (await workspace.GetFileAsync(file.Id)).State);
    }

    [Fact]
    public async Task IntegrityScanReportsMissingAndOrphanManagedFilesWithoutRepairingThem()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var source = temporary.Child("integrity-missing.txt");
        await File.WriteAllTextAsync(source, "missing");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        File.Delete(workspace.GetManagedFilePath(file));
        var orphanPath = Path.Combine(root, "media", "orphan.bin");
        await File.WriteAllBytesAsync(orphanPath, [1, 2, 3, 4]);

        var report = await workspace.RunIntegrityScanAsync();

        Assert.True(report.IsComplete);
        Assert.Contains(report.Findings, finding => finding.Kind == LibraryIntegrityIssueKind.ManagedFileMissing && finding.RecordId == file.Id);
        Assert.Contains(report.Findings, finding => finding.Kind == LibraryIntegrityIssueKind.OrphanManagedFile && finding.RecordId is null && finding.ActualByteSize == 4);
        Assert.True(File.Exists(orphanPath));
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetFileAsync(file.Id)).State);
    }

    [Fact]
    public async Task ContentRevalidationPreservesRecordsAndBlocksChangedBytes()
    {
        using var temporary = new TemporaryDirectory();
        var sourceA = temporary.Child("a.txt");
        var sourceB = temporary.Child("b.txt");
        await File.WriteAllTextAsync(sourceA, "original");
        await File.WriteAllTextAsync(sourceB, "linked");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var files = (await workspace.ImportAsync([sourceA, sourceB], workspace.Descriptor.RootFolderId)).Select(result => result.File!).ToArray();
        await workspace.SetMetadataAsync(files[0].Id, "Note", MetadataValueKind.Text, "preserved", false);
        var link = await workspace.CreateLinkAsync(files[0].Id, files[1].Id, "related");
        var managedPath = workspace.GetManagedFilePath(files[0]);

        File.Delete(managedPath);
        var missing = await workspace.RevalidateFileContentAsync(files[0].Id);
        Assert.Equal(FileContentState.Missing, missing.File.ContentState);
        Assert.Null(missing.ObservedContentHash);
        Assert.Single(await workspace.GetMetadataAsync(files[0].Id));
        Assert.Contains(await workspace.GetLinksAsync(files[0].Id), item => item.Id == link.Id && item.State == LibraryRecordState.Active);

        File.Copy(sourceA, managedPath);
        var restored = await workspace.RevalidateFileContentAsync(files[0].Id);
        Assert.Equal(FileContentState.Healthy, restored.File.ContentState);
        Assert.Equal(files[0].ContentHash, restored.ObservedContentHash);

        await File.WriteAllTextAsync(managedPath, "external change");
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReadTextFileAsync(files[0].Id));
        var changed = await workspace.GetFileAsync(files[0].Id);
        Assert.Equal(FileContentState.Changed, changed.ContentState);
        Assert.Equal(LibraryRecordState.Active, changed.State);
        Assert.Single(await workspace.GetMetadataAsync(files[0].Id));
        Assert.Contains(await workspace.GetLinksAsync(files[0].Id), item => item.Id == link.Id && item.State == LibraryRecordState.Active);

        File.Copy(sourceA, managedPath, true);
        Assert.Equal(FileContentState.Healthy, (await workspace.RevalidateFileContentAsync(files[0].Id)).File.ContentState);
        Assert.Equal("original", (await workspace.ReadTextFileAsync(files[0].Id)).Content);
    }

    [Fact]
    public async Task ChangedTextCanBeInspectedWithoutAcceptingIt()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("changed-inspection.txt");
        await File.WriteAllTextAsync(source, "recorded bytes");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        await File.WriteAllTextAsync(workspace.GetManagedFilePath(file), "changed bytes");

        Assert.Equal(FileContentState.Changed, (await workspace.RevalidateFileContentAsync(file.Id)).File.ContentState);
        var inspection = await workspace.InspectChangedContentAsync(file.Id);
        var text = await workspace.ReadChangedTextFileAsync(file.Id);

        Assert.NotEqual(file.ContentHash, inspection.ActualContentHash);
        Assert.Equal("text/plain", inspection.ActualMediaType);
        Assert.Equal("changed bytes", text.Content);
        Assert.Equal(FileContentState.Changed, (await workspace.GetFileAsync(file.Id)).ContentState);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReadTextFileAsync(file.Id));
    }

    [Fact]
    public async Task ManagedContentReplacementPreservesImmutableOriginalAndRequiresDifferingConfirmation()
    {
        using var temporary = new TemporaryDirectory();
        var originalPath = temporary.Child("original.txt");
        var linkedPath = temporary.Child("linked.txt");
        await File.WriteAllTextAsync(originalPath, "original bytes");
        await File.WriteAllTextAsync(linkedPath, "linked");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var files = (await workspace.ImportAsync([originalPath, linkedPath], workspace.Descriptor.RootFolderId)).Select(result => result.File!).ToArray();
        await workspace.SetMetadataAsync(files[0].Id, "Note", MetadataValueKind.Text, "keep", false);
        await workspace.SetMetadataAsync(files[0].Id, "Secret", MetadataValueKind.Text, "concealed", true);
        var link = await workspace.CreateLinkAsync(files[0].Id, files[1].Id, "related");
        var originalHash = files[0].ContentHash;
        var managedPath = workspace.GetManagedFilePath(files[0]);
        await File.WriteAllTextAsync(managedPath, "replacement bytes");
        Assert.Equal(FileContentState.Changed, (await workspace.RevalidateFileContentAsync(files[0].Id)).File.ContentState);

        var review = await workspace.ReviewManagedContentReplacementAsync(files[0].Id, null);
        Assert.True(review.UsesCurrentManagedBytes);
        Assert.False(review.RestoresOriginal);
        Assert.Equal(originalHash, review.OriginalContentHash);
        Assert.Equal(1, review.OrdinaryMetadataCount);
        Assert.Equal(1, review.SensitiveMetadataCount);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CommitManagedContentReplacementAsync(review, null, false, false));
        Assert.Equal(FileContentState.Changed, (await workspace.GetFileAsync(files[0].Id)).ContentState);

        var replaced = await workspace.CommitManagedContentReplacementAsync(review, null, true, false);
        Assert.Equal(FileContentState.Replaced, replaced.ContentState);
        Assert.NotEqual(originalHash, replaced.ContentHash);
        Assert.Equal(2, (await workspace.GetMetadataAsync(files[0].Id)).Count);
        Assert.Contains(await workspace.GetLinksAsync(files[0].Id), item => item.Id == link.Id && item.State == LibraryRecordState.Active);

        await File.WriteAllTextAsync(managedPath, "tampered again");
        _ = await workspace.RevalidateFileContentAsync(files[0].Id);
        var restoreReview = await workspace.ReviewManagedContentReplacementAsync(files[0].Id, originalPath);
        Assert.True(restoreReview.RestoresOriginal);
        Assert.Equal(originalHash, restoreReview.OriginalContentHash);
        var restored = await workspace.CommitManagedContentReplacementAsync(restoreReview, originalPath, false, true);
        Assert.Equal(FileContentState.Healthy, restored.ContentState);
        Assert.Equal(originalHash, restored.ContentHash);
        Assert.Equal(2, (await workspace.GetMetadataAsync(files[0].Id)).Count);
        Assert.Equal("original bytes", (await workspace.ReadTextFileAsync(files[0].Id)).Content);
    }

    [Fact]
    public async Task MissingContentCanBeReplacedAndMetadataClearedTransactionally()
    {
        using var temporary = new TemporaryDirectory();
        var originalPath = temporary.Child("missing.txt");
        var replacementPath = temporary.Child("replacement.bin");
        await File.WriteAllTextAsync(originalPath, "original");
        await File.WriteAllBytesAsync(replacementPath, [0, 1, 2, 3, 4]);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([originalPath], workspace.Descriptor.RootFolderId)).File!;
        await workspace.SetMetadataAsync(file.Id, "Remove", MetadataValueKind.Boolean, "true", true);
        File.Delete(workspace.GetManagedFilePath(file));
        _ = await workspace.RevalidateFileContentAsync(file.Id);

        var review = await workspace.ReviewManagedContentReplacementAsync(file.Id, replacementPath);
        var replaced = await workspace.CommitManagedContentReplacementAsync(review, replacementPath, true, true);

        Assert.Equal(FileContentState.Replaced, replaced.ContentState);
        Assert.Equal("application/octet-stream", replaced.MediaType);
        Assert.Empty(await workspace.GetMetadataAsync(file.Id));
        Assert.Equal(await File.ReadAllBytesAsync(replacementPath), await File.ReadAllBytesAsync(workspace.GetManagedFilePath(replaced)));
    }

    [Fact]
    public async Task CancelledIntegrityScanReturnsAnIncompletePartialReport()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("integrity-cancel.txt");
        await File.WriteAllTextAsync(source, new string('x', 100_000));
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        _ = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<LibraryIntegrityScanProgress>(value =>
        {
            if (value.Stage == "Hashing managed files") cancellation.Cancel();
        });

        var report = await workspace.RunIntegrityScanAsync(progress, cancellation.Token);

        Assert.True(report.WasCancelled);
        Assert.False(report.IsComplete);
        Assert.True(report.FinishedAt >= report.StartedAt);
    }

    [Fact]
    public async Task IntegrityScanAllowsReadsWhileMutationsWaitAndRemainCancellable()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("integrity-gate.txt");
        await File.WriteAllTextAsync(source, new string('g', 100_000));
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        _ = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var scanHoldingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScan = new ManualResetEventSlim(false);
        var paused = 0;
        var progress = new InlineProgress<LibraryIntegrityScanProgress>(value =>
        {
            if (value.Stage == "Hashing managed files" && Interlocked.Exchange(ref paused, 1) == 0)
            {
                scanHoldingGate.TrySetResult();
                releaseScan.Wait();
            }
        });
        var scanTask = Task.Run(() => workspace.RunIntegrityScanAsync(progress));
        await scanHoldingGate.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            var waitingMutation = workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "After scan");
            await Task.Delay(100);
            Assert.False(waitingMutation.IsCompleted);
            Assert.Single(await workspace.GetActiveFilesAsync());

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Cancelled", cancellation.Token));

            releaseScan.Set();
            var report = await scanTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(report.IsComplete);
            var created = await waitingMutation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("After scan", created.Name);
        }
        finally
        {
            releaseScan.Set();
        }
    }

    [Fact]
    public async Task OpeningVersionOneLibraryUpgradesDatabaseAndManifestWithRollbackCleanup()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }

        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE file_links DROP COLUMN explicitly_recycled; DROP TABLE permanent_deletion_failures; DROP TABLE file_content_provenance; ALTER TABLE files DROP COLUMN original_name; ALTER TABLE files DROP COLUMN content_state; UPDATE library_info SET schema_version=1 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 6", "\"schemaVersion\": 1", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(6, upgraded.Descriptor.SchemaVersion);
        Assert.Empty(await upgraded.GetRecycledLinksAsync());
        Assert.Empty(await upgraded.GetRecycleBinEntriesAsync());
        Assert.False(File.Exists(databasePath + ".upgrade-backup"));
        Assert.Contains("\"schemaVersion\": 6", await File.ReadAllTextAsync(manifestPath));
    }

    [Fact]
    public async Task OpeningVersionTwoLibraryAddsPermanentDeletionFailureStorage()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE permanent_deletion_failures; DROP TABLE file_content_provenance; ALTER TABLE files DROP COLUMN original_name; ALTER TABLE files DROP COLUMN content_state; UPDATE library_info SET schema_version=2 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 6", "\"schemaVersion\": 2", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(6, upgraded.Descriptor.SchemaVersion);
        Assert.Empty(await upgraded.GetRecycleBinEntriesAsync());
    }

    [Fact]
    public async Task OpeningVersionThreeLibraryAddsOriginalFilenameFromDisplayName()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var source = temporary.Child("before-upgrade.txt");
        await File.WriteAllTextAsync(source, "content");
        var factory = new LibraryWorkspaceFactory();
        string fileId;
        await using (var created = await factory.CreateAsync(root))
        {
            fileId = Assert.Single(await created.ImportAsync([source], created.Descriptor.RootFolderId)).File!.Id;
            await created.RenameFileAsync(fileId, "current-name.txt");
        }

        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE file_content_provenance; ALTER TABLE files DROP COLUMN original_name; ALTER TABLE files DROP COLUMN content_state; UPDATE library_info SET schema_version=3 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 6", "\"schemaVersion\": 3", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        var file = await upgraded.GetFileAsync(fileId);
        Assert.Equal(6, upgraded.Descriptor.SchemaVersion);
        Assert.Equal("current-name.txt", file.OriginalFileName);
    }

    [Fact]
    public async Task OpeningVersionFourLibraryAddsContentHealthState()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var source = temporary.Child("healthy.txt");
        await File.WriteAllTextAsync(source, "healthy");
        var factory = new LibraryWorkspaceFactory();
        string fileId;
        await using (var created = await factory.CreateAsync(root))
        {
            fileId = Assert.Single(await created.ImportAsync([source], created.Descriptor.RootFolderId)).File!.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE file_content_provenance; ALTER TABLE files DROP COLUMN content_state; UPDATE library_info SET schema_version=4 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 6", "\"schemaVersion\": 4", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(6, upgraded.Descriptor.SchemaVersion);
        Assert.Equal(FileContentState.Healthy, (await upgraded.GetFileAsync(fileId)).ContentState);
    }

    [Fact]
    public async Task OpeningVersionFiveLibraryAddsImmutableContentProvenance()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var source = temporary.Child("before-provenance.txt");
        await File.WriteAllTextAsync(source, "original identity");
        var factory = new LibraryWorkspaceFactory();
        string fileId;
        string originalHash;
        await using (var created = await factory.CreateAsync(root))
        {
            var file = Assert.Single(await created.ImportAsync([source], created.Descriptor.RootFolderId)).File!;
            fileId = file.Id;
            originalHash = file.ContentHash;
        }

        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE file_content_provenance; UPDATE library_info SET schema_version=5 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 6", "\"schemaVersion\": 5", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        var provenance = await upgraded.GetFileContentProvenanceAsync(fileId);
        Assert.Equal(6, upgraded.Descriptor.SchemaVersion);
        Assert.Equal(originalHash, provenance.OriginalContentHash);
        Assert.Null(provenance.ReplacedAt);
    }

    [Fact]
    public async Task OpenLibraryValidationRejectsUnexpectedManifestOrDatabaseIdentityChanges()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(root);
        await workspace.ValidateOpenLibraryAsync();

        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("SlopFactory Library", "External rename", StringComparison.Ordinal));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ValidateOpenLibraryAsync());

        await File.WriteAllTextAsync(manifestPath, manifest);
        await workspace.ValidateOpenLibraryAsync();
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE library_info SET display_name='External database rename' WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ValidateOpenLibraryAsync());
    }

    [Fact]
    public async Task AdoptingACopiedLibraryAssignsANewIdentityAndPreservesLocalRecords()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("source.txt");
        await File.WriteAllTextAsync(sourcePath, "copied content");
        var originalRoot = temporary.Child("original-library");
        var copiedRoot = temporary.Child("copied-library");
        var factory = new LibraryWorkspaceFactory();
        string originalLibraryId;
        string fileId;
        await using (var original = await factory.CreateAsync(originalRoot))
        {
            originalLibraryId = original.Descriptor.LibraryId;
            var file = Assert.Single(await original.ImportAsync([sourcePath], original.Descriptor.RootFolderId)).File!;
            fileId = file.Id;
            await original.SetMetadataAsync(file.Id, "Retained", MetadataValueKind.Text, "value", false);
        }
        DirectoryCopy(originalRoot, copiedRoot);

        await using (var adopted = await factory.AdoptCopyAsync(copiedRoot))
        {
            Assert.NotEqual(originalLibraryId, adopted.Descriptor.LibraryId);
            Assert.Equal("copied content", (await adopted.ReadTextFileAsync(fileId)).Content);
            Assert.Equal("value", Assert.Single(await adopted.GetMetadataAsync(fileId)).SerializedValue);
        }

        await using var reopened = await factory.OpenAsync(copiedRoot);
        Assert.NotEqual(originalLibraryId, reopened.Descriptor.LibraryId);
        Assert.Equal("copied content", (await reopened.ReadTextFileAsync(fileId)).Content);
    }

    [Fact]
    public async Task DetectableManagedHardLinksAreBlockedAndReported()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("source.txt");
        var externalPath = temporary.Child("external.txt");
        await File.WriteAllTextAsync(sourcePath, "same bytes");
        await File.WriteAllTextAsync(externalPath, "same bytes");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;
        var managedPath = workspace.GetManagedFilePath(file);
        File.Delete(managedPath);
        CreateHardLink(managedPath, externalPath);

        Assert.Equal(FileContentState.Changed, (await workspace.RevalidateFileContentAsync(file.Id)).File.ContentState);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReadTextFileAsync(file.Id));
        var report = await workspace.RunIntegrityScanAsync();
        Assert.Contains(report.Findings, finding => finding.Kind == LibraryIntegrityIssueKind.UnsafeManagedEntry && finding.RecordId == file.Id);
    }

    [Fact]
    public async Task BulkDuplicateUsesIndependentOutcomesAndNumericSuffixes()
    {
        using var temporary = new TemporaryDirectory();
        var firstPath = temporary.Child("first.txt");
        var secondPath = temporary.Child("second.txt");
        await File.WriteAllTextAsync(firstPath, "first");
        await File.WriteAllTextAsync(secondPath, "second");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var first = Assert.Single(await workspace.ImportAsync([firstPath], workspace.Descriptor.RootFolderId)).File!;
        var second = Assert.Single(await workspace.ImportAsync([secondPath], workspace.Descriptor.RootFolderId)).File!;
        await workspace.SetMetadataAsync(first.Id, "Copied", MetadataValueKind.Text, "yes", false);
        await workspace.RecycleFileAsync(second.Id);

        var result = await workspace.DuplicateFilesAsync([first.Id, second.Id], workspace.Descriptor.RootFolderId);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        var copied = Assert.Single(await workspace.GetActiveFilesAsync(), file => file.Origin == FileOrigin.UserCopy);
        Assert.Equal("first (2).txt", copied.DisplayName);
        Assert.Equal("yes", Assert.Single(await workspace.GetMetadataAsync(copied.Id)).SerializedValue);
    }

    [Fact]
    public async Task BulkMetadataSensitivityPreservesValuesAndReportsMissingEntries()
    {
        using var temporary = new TemporaryDirectory();
        var firstPath = temporary.Child("first.txt");
        var secondPath = temporary.Child("second.txt");
        await File.WriteAllTextAsync(firstPath, "first");
        await File.WriteAllTextAsync(secondPath, "second");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var first = Assert.Single(await workspace.ImportAsync([firstPath], workspace.Descriptor.RootFolderId)).File!;
        var second = Assert.Single(await workspace.ImportAsync([secondPath], workspace.Descriptor.RootFolderId)).File!;
        await workspace.SetMetadataAsync(first.Id, "Private", MetadataValueKind.Json, "{\"preserve\":true}", false);

        var marked = await workspace.SetMetadataSensitivityForFilesAsync([first.Id, second.Id], "Private", true);

        Assert.Equal(1, marked.SucceededCount);
        Assert.Equal(1, marked.FailedCount);
        var sensitive = Assert.Single(await workspace.GetMetadataAsync(first.Id));
        Assert.True(sensitive.IsSensitive);
        Assert.Equal(MetadataValueKind.Json, sensitive.Kind);
        Assert.Equal("{\"preserve\":true}", sensitive.SerializedValue);

        var ordinary = await workspace.SetMetadataSensitivityForFilesAsync([first.Id], "Private", false);
        Assert.Equal(1, ordinary.SucceededCount);
        Assert.False(Assert.Single(await workspace.GetMetadataAsync(first.Id)).IsSensitive);
    }

    [Fact]
    public async Task LibraryBrowserSearchesNamesAndTypedMetadataWithoutDisclosingSensitiveKeys()
    {
        using var temporary = new TemporaryDirectory();
        var alphaPath = temporary.Child("alpha-original.txt");
        var betaPath = temporary.Child("beta.txt");
        await File.WriteAllTextAsync(alphaPath, "alpha");
        await File.WriteAllTextAsync(betaPath, "beta");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var nested = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Nested");
        var alpha = Assert.Single(await workspace.ImportAsync([alphaPath], workspace.Descriptor.RootFolderId)).File!;
        var beta = Assert.Single(await workspace.ImportAsync([betaPath], nested.Id)).File!;
        await workspace.RenameFileAsync(alpha.Id, "renamed.txt");
        await workspace.SetMetadataAsync(alpha.Id, "Category", MetadataValueKind.Text, "Landscape", false);
        await workspace.SetMetadataAsync(beta.Id, "PrivateCode", MetadataValueKind.Text, "Needle", true);
        await workspace.SetMetadataAsync(beta.Id, "Profile", MetadataValueKind.Json, "{\"subject\":\"Sunset\",\"count\":4,\"ready\":true}", false);

        LibraryFileBrowseQuery Query(string search, LibraryBrowseScope scope = LibraryBrowseScope.EntireLibrary) =>
            new(workspace.Descriptor.RootFolderId, scope, search, LibraryMediaKind.Any, null, null, null, LibraryFileSort.Name, 0, 20);

        var original = Assert.Single((await workspace.BrowseFilesAsync(Query("alpha-original"))).Items);
        Assert.Equal(alpha.Id, original.File.Id);
        Assert.Contains("Matched original filename", original.MatchReasons);
        Assert.Equal("alpha-original.txt", original.File.OriginalFileName);

        var ordinary = Assert.Single((await workspace.BrowseFilesAsync(Query("Landscape"))).Items);
        Assert.Contains("Matched user metadata: Category", ordinary.MatchReasons);

        var jsonScalar = Assert.Single((await workspace.BrowseFilesAsync(Query("Sunset"))).Items);
        Assert.Equal(beta.Id, jsonScalar.File.Id);
        Assert.Contains("Matched user metadata: Profile", jsonScalar.MatchReasons);
        Assert.Single((await workspace.BrowseFilesAsync(Query("subject"))).Items);
        Assert.Empty((await workspace.BrowseFilesAsync(Query("{\"subject\""))).Items);
        Assert.Empty((await workspace.BrowseFilesAsync(Query("%"))).Items);
        Assert.Empty((await workspace.BrowseFilesAsync(Query("_"))).Items);

        var sensitive = Assert.Single((await workspace.BrowseFilesAsync(Query("Needle"))).Items);
        Assert.Equal(["Matched user metadata"], sensitive.MatchReasons);
        Assert.DoesNotContain("PrivateCode", string.Join(' ', sensitive.MatchReasons), StringComparison.Ordinal);
        Assert.Empty((await workspace.BrowseFilesAsync(Query("Needle", LibraryBrowseScope.CurrentFolder))).Items);
    }

    [Fact]
    public async Task LibraryBrowserAppliesFiltersAndStablePaging()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        foreach (var name in new[] { "charlie.txt", "alpha.txt", "bravo.txt" })
        {
            var path = temporary.Child(name);
            await File.WriteAllTextAsync(path, name);
            _ = await workspace.ImportAsync([path], workspace.Descriptor.RootFolderId);
        }

        var first = await workspace.BrowseFilesAsync(new LibraryFileBrowseQuery(workspace.Descriptor.RootFolderId, LibraryBrowseScope.CurrentFolder, string.Empty,
            LibraryMediaKind.Text, FileOrigin.Imported, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), LibraryFileSort.Name, 0, 2));
        var second = await workspace.BrowseFilesAsync(new LibraryFileBrowseQuery(workspace.Descriptor.RootFolderId, LibraryBrowseScope.CurrentFolder, string.Empty,
            LibraryMediaKind.Text, FileOrigin.Imported, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), LibraryFileSort.Name, 2, 2));

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(["alpha.txt", "bravo.txt"], first.Items.Select(item => item.File.DisplayName));
        Assert.True(first.HasNextPage);
        Assert.Equal("charlie.txt", Assert.Single(second.Items).File.DisplayName);
        Assert.True(second.HasPreviousPage);
        var future = await workspace.BrowseFilesAsync(new LibraryFileBrowseQuery(workspace.Descriptor.RootFolderId, LibraryBrowseScope.EntireLibrary, string.Empty,
            LibraryMediaKind.Any, null, DateTimeOffset.UtcNow.AddDays(1), null, LibraryFileSort.Name));
        Assert.Empty(future.Items);
    }

    [Fact]
    public async Task LibraryBrowserAppliesStrictTypedMetadataFilters()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var files = new List<FileRecord>();
        foreach (var name in new[] { "ten.txt", "five.txt", "text.txt", "missing.txt", "json.txt" })
        {
            var path = temporary.Child(name);
            await File.WriteAllTextAsync(path, name);
            files.Add(Assert.Single(await workspace.ImportAsync([path], workspace.Descriptor.RootFolderId)).File!);
        }
        await workspace.SetMetadataAsync(files[0].Id, "Rating", MetadataValueKind.Number, "10.0", false);
        await workspace.SetMetadataAsync(files[1].Id, "Rating", MetadataValueKind.Number, "5", false);
        await workspace.SetMetadataAsync(files[2].Id, "Rating", MetadataValueKind.Text, "TEN", true);
        await workspace.SetMetadataAsync(files[4].Id, "Profile", MetadataValueKind.Json, "{\"count\":4.0,\"tags\":[\"a\",\"b\"]}", false);
        await workspace.SetMetadataAsync(files[3].Id, "Profile", MetadataValueKind.Json, "null", false);
        await workspace.SetMetadataAsync(files[0].Id, "Captured", MetadataValueKind.DateTime, "2026-08-03T08:00:00+08:00", false);

        LibraryFileBrowseQuery Query(UserMetadataFilter filter) => new(workspace.Descriptor.RootFolderId, LibraryBrowseScope.CurrentFolder, string.Empty,
            LibraryMediaKind.Any, null, null, null, LibraryFileSort.Name, 0, 20, filter);

        var number = await workspace.BrowseFilesAsync(Query(new UserMetadataFilter("rating", MetadataValueKind.Number, MetadataFilterOperator.GreaterThan, "6")));
        Assert.Equal(files[0].Id, Assert.Single(number.Items).File.Id);
        Assert.Equal(2, number.MetadataMissingCount);
        Assert.Equal(1, number.MetadataIncompatibleTypeCount);
        Assert.Contains("Matched user metadata filter", number.Items[0].MatchReasons);

        var text = await workspace.BrowseFilesAsync(Query(new UserMetadataFilter("Rating", MetadataValueKind.Text, MetadataFilterOperator.Contains, "ten")));
        Assert.Equal(files[2].Id, Assert.Single(text.Items).File.Id);

        var json = await workspace.BrowseFilesAsync(Query(new UserMetadataFilter("Profile", MetadataValueKind.Json, MetadataFilterOperator.StructurallyEquals, "{\"tags\":[\"a\",\"b\"],\"count\":4}")));
        Assert.Equal(files[4].Id, Assert.Single(json.Items).File.Id);
        var wrongArrayOrder = await workspace.BrowseFilesAsync(Query(new UserMetadataFilter("Profile", MetadataValueKind.Json, MetadataFilterOperator.StructurallyEquals, "{\"tags\":[\"b\",\"a\"],\"count\":4}")));
        Assert.Empty(wrongArrayOrder.Items);
        var jsonExists = await workspace.BrowseFilesAsync(Query(new UserMetadataFilter("Profile", MetadataValueKind.Json, MetadataFilterOperator.Exists, null)));
        Assert.Equal(2, jsonExists.TotalCount);
        var jsonNull = await workspace.BrowseFilesAsync(Query(new UserMetadataFilter("Profile", MetadataValueKind.Json, MetadataFilterOperator.StructurallyEquals, "null")));
        Assert.Equal(files[3].Id, Assert.Single(jsonNull.Items).File.Id);
        var jsonMissing = await workspace.BrowseFilesAsync(Query(new UserMetadataFilter("Profile", MetadataValueKind.Json, MetadataFilterOperator.DoesNotExist, null)));
        Assert.Equal(3, jsonMissing.TotalCount);

        var instant = await workspace.BrowseFilesAsync(Query(new UserMetadataFilter("Captured", MetadataValueKind.DateTime, MetadataFilterOperator.Equals, "2026-08-03T00:00:00Z")));
        Assert.Equal(files[0].Id, Assert.Single(instant.Items).File.Id);

        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.BrowseFilesAsync(Query(new UserMetadataFilter("Rating", MetadataValueKind.Number, MetadataFilterOperator.Contains, "1"))));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static void DirectoryCopy(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (var file in Directory.EnumerateFiles(sourcePath)) File.Copy(file, Path.Combine(destinationPath, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(sourcePath)) DirectoryCopy(directory, Path.Combine(destinationPath, Path.GetFileName(directory)));
    }

    private static void CreateHardLink(string linkPath, string existingPath)
    {
        if (!CreateHardLinkNative(linkPath, existingPath, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkNative(string fileName, string existingFileName, IntPtr securityAttributes);
}
