using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
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
        var source = temporary.Child("pending.txt");
        await File.WriteAllTextAsync(source, "pending");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var managedPath = workspace.GetManagedFilePath(file);
        await workspace.RecycleFileAsync(file.Id);
        File.Delete(managedPath);
        Directory.CreateDirectory(managedPath);

        await Assert.ThrowsAsync<IOException>(() => workspace.PermanentlyDeleteFileAsync(file.Id));

        var pending = Assert.Single(await workspace.GetRecycleBinFilesAsync());
        Assert.Equal(LibraryRecordState.PendingPermanentDeletion, pending.State);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.RestoreFileAsync(file.Id));

        Directory.Delete(managedPath);
        await workspace.PermanentlyDeleteFileAsync(file.Id);
        Assert.Empty(await workspace.GetRecycleBinFilesAsync());
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

        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(png, image.Bytes);
        await File.WriteAllBytesAsync(workspace.GetManagedFilePath(file), [0x89, 0x50, 0x4E, 0x47]);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.ReadImageFileAsync(file.Id));
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
            command.CommandText = "ALTER TABLE file_links DROP COLUMN explicitly_recycled; UPDATE library_info SET schema_version=1 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(2, upgraded.Descriptor.SchemaVersion);
        Assert.Empty(await upgraded.GetRecycledLinksAsync());
        Assert.False(File.Exists(databasePath + ".upgrade-backup"));
        Assert.Contains("\"schemaVersion\": 2", await File.ReadAllTextAsync(manifestPath));
    }
}
