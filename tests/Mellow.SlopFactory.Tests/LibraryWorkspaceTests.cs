using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Mellow.SlopFactory.Gui.Services;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class LibraryWorkspaceTests
{
    [Theory]
    [InlineData("text/plain", BuiltInPreviewKind.Text)]
    [InlineData("application/json", BuiltInPreviewKind.Text)]
    [InlineData("image/jpeg", BuiltInPreviewKind.Image)]
    [InlineData("audio/wav", BuiltInPreviewKind.Media)]
    [InlineData("video/mp4", BuiltInPreviewKind.Media)]
    [InlineData("application/pdf", BuiltInPreviewKind.Unsupported)]
    [InlineData("application/octet-stream", BuiltInPreviewKind.Unsupported)]
    public void BuiltInPreviewCapabilitiesAllowOnlySupportedDetectedMediaTypes(string mediaType, BuiltInPreviewKind expected)
    {
        Assert.Equal(expected, BuiltInPreviewCapabilities.ForMediaType(mediaType));
    }

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
        Assert.Empty(Directory.EnumerateFiles(System.IO.Path.Combine(root, ".staging")));
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
    public void PlatformVersionPolicyEnforcesDocumentedMinimums()
    {
        Assert.False(PlatformVersionPolicy.IsSupported(SupportedPlatform.Windows, new Version(10, 0, 19044)));
        Assert.True(PlatformVersionPolicy.IsSupported(SupportedPlatform.Windows, new Version(10, 0, 19045)));
        Assert.True(PlatformVersionPolicy.IsSupported(SupportedPlatform.Windows, new Version(10, 0, 22000)));
        Assert.False(PlatformVersionPolicy.IsSupported(SupportedPlatform.Android, new Version(25, 0)));
        Assert.True(PlatformVersionPolicy.IsSupported(SupportedPlatform.Android, new Version(26, 0)));
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
    public async Task ADraftsLatestSavedStateSurvivesAnUncleanProcessExit()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        var first = await factory.CreateAsync(root);
        var connection = await first.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await first.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var draft = await first.CreateDraftAsync();
        draft = await first.ReplaceDraftStateAsync(draft.Id, "My Tab", model.Id, "autosaved prompt", "autosaved instructions", 2, first.Descriptor.GeneratedFolderId, null, null);

        // Simulate a hard process exit: Dispose only releases the exclusive lock file, never flushes
        // anything extra — every ReplaceDraftStateAsync call above already committed durably through
        // RunMutationAsync's own transaction, so nothing here should depend on a graceful shutdown
        // path running first.
        await first.DisposeAsync();

        await using var reopened = await factory.OpenAsync(root);
        var recovered = Assert.Single(await reopened.GetDraftsAsync());
        Assert.Equal(draft.Id, recovered.Id);
        Assert.Equal("My Tab", recovered.CustomTitle);
        Assert.Equal(model.Id, recovered.ModelId);
        Assert.Equal("autosaved prompt", recovered.Prompt);
        Assert.Equal("autosaved instructions", recovered.SystemInstructions);
        Assert.Equal(2, recovered.ResultCount);
    }

    [Fact]
    public async Task OpenRevalidatesRootStorageCapabilitiesWithoutLeavingProbeArtifacts()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }

        await using (var reopened = await factory.OpenAsync(root)) { }

        Assert.Empty(Directory.EnumerateFiles(root, ".slopfactory-capability-*", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, ".staging"), "capability-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task FailedCreateCleansUpEveryNewLibraryArtifact()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("cancelled-library");
        var factory = new LibraryWorkspaceFactory();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.CreateAsync(root, cancellationToken: new CancellationToken(canceled: true)));

        Assert.False(Directory.Exists(root));
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
    public async Task RecycleBinIntegratesGenerationRecordsForListingBatchRestoreAndPermanentDelete()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId, ["result"], null);
        var resultFileId = record.ResultFileIds[0];

        await workspace.RecycleGenerationRecordAsync(record.Id);
        var entry = Assert.Single(await workspace.GetRecycleBinEntriesAsync(), e => e.Reference.Kind == RecycleBinItemKind.GenerationRecord);
        Assert.Equal(new RecycleBinItemReference(RecycleBinItemKind.GenerationRecord, record.Id), entry.Reference);
        Assert.Equal("GPT", entry.Name);
        Assert.Equal("Generation History", entry.OriginalLocation);

        var restored = await workspace.RestoreRecycleBinItemsAsync([entry.Reference]);
        Assert.Equal(1, restored.SucceededCount);
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetGenerationRecordAsync(record.Id)).State);

        await workspace.RecycleGenerationRecordAsync(record.Id);
        var deleted = await workspace.PermanentlyDeleteRecycleBinItemsAsync([new RecycleBinItemReference(RecycleBinItemKind.GenerationRecord, record.Id)]);
        Assert.Equal(1, deleted.SucceededCount);
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetGenerationRecordAsync(record.Id));
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetFileAsync(resultFileId)).State);
    }

    [Fact]
    public async Task RecordTextGenerationResultGivesASafetyBlockedCandidateAStablePerPositionIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        IReadOnlyList<TextGenerationCandidate> candidates =
        [
            new TextGenerationCandidate(SafetyBlocked: false, Text: "first result"),
            new TextGenerationCandidate(SafetyBlocked: true, Text: null),
            new TextGenerationCandidate(SafetyBlocked: false, Text: "third result"),
        ];

        var record = await workspace.RecordTextGenerationResultAsync(model.Id, "a prompt", 3, workspace.Descriptor.GeneratedFolderId, ["first result", "third result"], null, safetyBlockedCount: 1, candidates: candidates);

        Assert.Equal(3, record.Results.Count);
        var committed = record.Results.Where(entry => entry.Status == GenerationResultStatus.Committed).OrderBy(entry => entry.Position).ToArray();
        Assert.Equal([0, 2], committed.Select(entry => entry.Position).ToArray());
        var blocked = Assert.Single(record.Results, entry => entry.Status == GenerationResultStatus.SafetyBlocked);
        Assert.Equal(1, blocked.Position);
        Assert.Null(blocked.FileId);
        Assert.Equal(GenerationStatus.PartiallyCompleted, record.Status);
    }

    [Fact]
    public async Task GetGenerationHistoryOnlyIncludesRecycledRecordsWhenExplicitlyAsked()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var active = await workspace.RecordTextGenerationResultAsync(model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId, ["result"], null);
        var recycled = await workspace.RecordTextGenerationResultAsync(model.Id, "another prompt", 1, workspace.Descriptor.GeneratedFolderId, ["result"], null);
        await workspace.RecycleGenerationRecordAsync(recycled.Id);

        var defaultHistory = await workspace.GetGenerationHistoryAsync();
        Assert.Equal(active.Id, Assert.Single(defaultHistory).Id);

        var fullHistory = await workspace.GetGenerationHistoryAsync(includeRecycled: true);
        Assert.Equal(2, fullHistory.Count);
        Assert.Contains(fullHistory, record => record.Id == active.Id);
        Assert.Contains(fullHistory, record => record.Id == recycled.Id);
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
    public async Task RecycledConnectionAppearsWithOwnedCountsAndHidesItsModelsAndSavedSettings()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Bin Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var modelA = await workspace.CreateModelAsync("Bin Model A", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var modelB = await workspace.CreateModelAsync("Bin Model B", connection.Id, "gpt-4o-mini", GenerationMode.Text, true);
        await workspace.CreateSavedSettingAsync("Bin Preset", modelA.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId);

        await workspace.RecycleConnectionAsync(connection.Id);

        var entries = await workspace.GetRecycleBinEntriesAsync();
        var entry = Assert.Single(entries, e => e.Reference.Kind == RecycleBinItemKind.Connection);
        Assert.Equal(connection.Id, entry.Reference.Id);
        Assert.Equal(2, entry.OwnedModelCount);
        Assert.Equal(1, entry.OwnedSavedSettingCount);
        Assert.DoesNotContain(entries, e => e.Reference.Kind == RecycleBinItemKind.Model);
        Assert.DoesNotContain(entries, e => e.Reference.Kind == RecycleBinItemKind.SavedSetting);
        _ = modelB;
    }

    [Fact]
    public async Task RecycledModelAppearsSeparatelyWhenItsConnectionStaysActive()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Active Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Standalone Model", connection.Id, "gpt-4o", GenerationMode.Text, true);
        await workspace.CreateSavedSettingAsync("Model Preset", model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId);

        await workspace.RecycleModelAsync(model.Id);

        var entries = await workspace.GetRecycleBinEntriesAsync();
        var entry = Assert.Single(entries, e => e.Reference.Kind == RecycleBinItemKind.Model);
        Assert.Equal(model.Id, entry.Reference.Id);
        Assert.Equal(connection.Label, entry.OriginalLocation);
        Assert.Equal(1, entry.OwnedSavedSettingCount);
        Assert.DoesNotContain(entries, e => e.Reference.Kind == RecycleBinItemKind.Connection);
        Assert.DoesNotContain(entries, e => e.Reference.Kind == RecycleBinItemKind.SavedSetting);
    }

    [Fact]
    public async Task RecycledSavedSettingWithNoModelReferenceStillAppearsInTheBin()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var saved = await workspace.CreateSavedSettingAsync("Modelless Preset", null, "a prompt", 1, workspace.Descriptor.GeneratedFolderId);

        await workspace.RecycleSavedSettingAsync(saved.Id);

        var entry = Assert.Single(await workspace.GetRecycleBinEntriesAsync(), e => e.Reference.Kind == RecycleBinItemKind.SavedSetting);
        Assert.Equal(saved.Id, entry.Reference.Id);
    }

    [Fact]
    public async Task RestorePreviewReportsConnectionModelAndSavedSettingLabelConflicts()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Duplicate Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Duplicate Model", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("Duplicate Preset", model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId);
        await workspace.RecycleSavedSettingAsync(saved.Id);
        await workspace.RecycleModelAsync(model.Id);
        await workspace.RecycleConnectionAsync(connection.Id);
        await workspace.CreateConnectionAsync("Duplicate Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");

        var preview = await workspace.GetRecycleBinRestorePreviewAsync([new RecycleBinItemReference(RecycleBinItemKind.Connection, connection.Id)]);

        var item = Assert.Single(preview.Items);
        Assert.Contains(item.BlockingReasons, reason => reason.Contains("already exist", StringComparison.OrdinalIgnoreCase));
        Assert.False(item.CanRestore);
    }

    [Fact]
    public async Task RestoringASavedSettingWithAnActiveModelReportsNoOwningModelBlocker()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Model", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("Preset", model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId);
        await workspace.RecycleSavedSettingAsync(saved.Id);

        var preview = await workspace.GetRecycleBinRestorePreviewAsync([new RecycleBinItemReference(RecycleBinItemKind.SavedSetting, saved.Id)]);

        var item = Assert.Single(preview.Items);
        Assert.Empty(item.BlockingReasons);
        Assert.True(item.CanRestore);
    }

    [Fact]
    public async Task BatchRestoreAndEmptyRecycleBinHandleConnectionsModelsAndSavedSettingsTogether()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Batch Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Batch Model", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var standaloneConnection = await workspace.CreateConnectionAsync("Standalone Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var standaloneModel = await workspace.CreateModelAsync("Standalone Model", standaloneConnection.Id, "gpt-4o", GenerationMode.Text, true);
        _ = model;

        await workspace.RecycleModelAsync(standaloneModel.Id);
        await workspace.RecycleConnectionAsync(connection.Id);
        var references = (await workspace.GetRecycleBinEntriesAsync()).Select(entry => entry.Reference).ToArray();

        var restored = await workspace.RestoreRecycleBinItemsAsync(references);

        Assert.Equal(2, restored.SucceededCount);
        Assert.Equal(0, restored.FailedCount);
        Assert.Empty(await workspace.GetRecycleBinEntriesAsync());
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetConnectionAsync(connection.Id)).State);
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetModelAsync(standaloneModel.Id)).State);

        await workspace.RecycleModelAsync(standaloneModel.Id);
        await workspace.RecycleConnectionAsync(connection.Id);
        var emptied = await workspace.EmptyRecycleBinAsync();

        Assert.Equal(2, emptied.SucceededCount);
        Assert.Equal(0, emptied.FailedCount);
        Assert.Empty(await workspace.GetRecycleBinEntriesAsync());
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetConnectionAsync(connection.Id));
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetModelAsync(standaloneModel.Id));
    }

    [Fact]
    public async Task PermanentlyDeletingAConnectionThroughTheBinCascadesItsModelsAndSavedSettings()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var connection = await workspace.CreateConnectionAsync("Delete Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("Delete Model", connection.Id, "gpt-4o", GenerationMode.Text, true);
        var saved = await workspace.CreateSavedSettingAsync("Delete Preset", model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId);
        await workspace.RecycleConnectionAsync(connection.Id);

        var result = await workspace.PermanentlyDeleteRecycleBinItemsAsync([new RecycleBinItemReference(RecycleBinItemKind.Connection, connection.Id)]);

        Assert.Equal(1, result.SucceededCount);
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetConnectionAsync(connection.Id));
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetModelAsync(model.Id));
        await Assert.ThrowsAsync<RecordNotFoundException>(() => workspace.GetSavedSettingAsync(saved.Id));
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
        var duplicateProvenance = await workspace.GetFileDerivationProvenanceAsync(duplicate.Id);

        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.NotEqual(source.ManagedName, duplicate.ManagedName);
        Assert.Equal(FileOrigin.UserCopy, duplicate.Origin);
        Assert.Equal(new FileDerivationProvenance(source.Id, FileOrigin.UserCopy), duplicateProvenance);
        Assert.Equal(source.ContentHash, duplicate.ContentHash);
        Assert.Equal(await File.ReadAllBytesAsync(workspace.GetManagedFilePath(source)), await File.ReadAllBytesAsync(workspace.GetManagedFilePath(duplicate)));
        var copiedMetadata = Assert.Single(await workspace.GetMetadataAsync(duplicate.Id));
        Assert.Equal("Note", copiedMetadata.Key);
        Assert.True(copiedMetadata.IsSensitive);
        Assert.Empty(await workspace.GetLinksAsync(duplicate.Id));
        await Assert.ThrowsAsync<NameConflictException>(() => workspace.DuplicateFileAsync(source.Id, destination.Id, "copy.txt"));
    }

    [Fact]
    public async Task DerivationChainUsesImmediateSourceIdsAcrossRenameAndMove()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("original.txt");
        await File.WriteAllTextAsync(sourcePath, "source");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var source = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;
        var folder = await workspace.CreateFolderAsync(workspace.Descriptor.RootFolderId, "Copies");
        var firstCopy = await workspace.DuplicateFileAsync(source.Id, folder.Id, "first.txt");
        var secondCopy = await workspace.DuplicateFileAsync(firstCopy.Id, folder.Id, "second.txt");
        await workspace.RenameFileAsync(firstCopy.Id, "renamed.txt");
        await workspace.MoveFileAsync(firstCopy.Id, workspace.Descriptor.RootFolderId);

        var chain = await workspace.GetFileDerivationChainAsync(secondCopy.Id);

        Assert.Equal([secondCopy.Id, firstCopy.Id, source.Id], chain.Select(entry => entry.File.Id));
        Assert.Equal([FileOrigin.UserCopy, FileOrigin.UserCopy, null], chain.Select(entry => entry.DerivedBy));
    }

    [Fact]
    public async Task DerivationRelationshipHidesWhileRecycledRestoresWithEndpointsAndSnapshotsPermanentSource()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("source.txt");
        await File.WriteAllTextAsync(sourcePath, "source");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var source = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;
        var copy = await workspace.DuplicateFileAsync(source.Id, workspace.Descriptor.RootFolderId, "copy.txt");

        await workspace.RecycleFileAsync(source.Id);
        Assert.Single(await workspace.GetFileDerivationChainAsync(copy.Id));
        await workspace.RestoreFileAsync(source.Id);
        Assert.Equal(2, (await workspace.GetFileDerivationChainAsync(copy.Id)).Count);
        await workspace.RecycleFileAsync(copy.Id);
        Assert.Single(await workspace.GetFileDerivationChainAsync(copy.Id));
        await workspace.RestoreFileAsync(copy.Id);
        await workspace.RecycleFileAsync(source.Id);
        await workspace.PermanentlyDeleteFileAsync(source.Id);

        var provenance = await workspace.GetFileDerivationProvenanceAsync(copy.Id);
        Assert.Null(provenance!.SourceFileId);
        Assert.Equal(new FileIdentitySnapshot(source.DisplayName, source.MediaType, source.ContentHash), provenance.DeletedSource);
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
        Assert.Equal(new FileDerivationProvenance(source.Id, FileOrigin.EditedCopy), await workspace.GetFileDerivationProvenanceAsync(ordinaryCopy.Id));
        Assert.Equal(new FileDerivationProvenance(source.Id, FileOrigin.EditedCopy), await workspace.GetFileDerivationProvenanceAsync(sensitiveCopy.Id));
        Assert.Equal(new FileDerivationProvenance(source.Id, FileOrigin.EditedCopy), await workspace.GetFileDerivationProvenanceAsync(cleanCopy.Id));
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
    public async Task RasterImageTechnicalPropertiesReadBoundedJpegOrientationWithoutChangingManagedBytes()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("oriented.jpg");
        var jpeg = Convert.FromHexString("FFD8FFE100224578696600004D4D002A00000008000101120003000000010006000000000000FFC00011080001000103011100021100031100FFD9");
        await File.WriteAllBytesAsync(sourcePath, jpeg);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;
        var managedPath = workspace.GetManagedFilePath(file);
        var originalBytes = await File.ReadAllBytesAsync(managedPath);

        var properties = await workspace.GetImageTechnicalPropertiesAsync(file.Id);

        Assert.Equal(1, properties.Width);
        Assert.Equal(1, properties.Height);
        Assert.Equal(6, properties.Orientation);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(managedPath));
        Assert.Equal(file.ContentHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(managedPath))).ToLowerInvariant());
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
    public void JsonMetadataValidationDoesNotEchoSensitiveInput()
    {
        var invalid = Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateMetadataValue(MetadataValueKind.Json, "{\"secret-token\": }"));
        var duplicate = Assert.Throws<LibraryValidationException>(() => LibraryRules.ValidateMetadataValue(MetadataValueKind.Json, "{\"secret-token\":1,\"secret-token\":2}"));

        Assert.Contains("line", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token", invalid.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", duplicate.Message, StringComparison.Ordinal);
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
            command.CommandText = "ALTER TABLE file_links DROP COLUMN explicitly_recycled; DROP TABLE permanent_deletion_failures; DROP TABLE file_content_provenance; DROP TABLE file_derivation_provenance; ALTER TABLE files DROP COLUMN original_name; ALTER TABLE files DROP COLUMN content_state; UPDATE library_info SET schema_version=1 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 1", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        Assert.Empty(await upgraded.GetRecycledLinksAsync());
        Assert.Empty(await upgraded.GetRecycleBinEntriesAsync());
        Assert.False(File.Exists(databasePath + ".upgrade-backup"));
        Assert.Contains("\"schemaVersion\": 41", await File.ReadAllTextAsync(manifestPath));
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
            command.CommandText = "DROP TABLE permanent_deletion_failures; DROP TABLE file_content_provenance; DROP TABLE file_derivation_provenance; ALTER TABLE files DROP COLUMN original_name; ALTER TABLE files DROP COLUMN content_state; UPDATE library_info SET schema_version=2 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 2", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
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
            command.CommandText = "DROP TABLE file_content_provenance; DROP TABLE file_derivation_provenance; ALTER TABLE files DROP COLUMN original_name; ALTER TABLE files DROP COLUMN content_state; UPDATE library_info SET schema_version=3 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 3", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        var file = await upgraded.GetFileAsync(fileId);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
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
            command.CommandText = "DROP TABLE file_content_provenance; DROP TABLE file_derivation_provenance; ALTER TABLE files DROP COLUMN content_state; UPDATE library_info SET schema_version=4 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 4", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
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
            command.CommandText = "DROP TABLE file_content_provenance; DROP TABLE file_derivation_provenance; UPDATE library_info SET schema_version=5 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 5", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        var provenance = await upgraded.GetFileContentProvenanceAsync(fileId);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        Assert.Equal(originalHash, provenance.OriginalContentHash);
        Assert.Null(provenance.ReplacedAt);
    }

    [Fact]
    public async Task OpeningVersionFourteenLibraryAddsModelCatalogueCache()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string connectionId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            connectionId = connection.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE connection_model_catalogue; ALTER TABLE connections DROP COLUMN catalogue_retrieved_at; ALTER TABLE connections DROP COLUMN catalogue_possibly_stale; UPDATE library_info SET schema_version=14 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 14", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var catalogue = await upgraded.GetModelCatalogueAsync(connectionId);
        Assert.Null(catalogue.RetrievedAt);
        Assert.False(catalogue.PossiblyStale);
        Assert.Empty(catalogue.Entries);
        var refreshed = await upgraded.RefreshModelCatalogueAsync(connectionId, [new ProviderModelInfo("gpt-4o", "GPT-4o")]);
        Assert.Equal("gpt-4o", Assert.Single(refreshed.Entries).ProviderModelId);
    }

    [Fact]
    public async Task OpeningVersionFifteenLibraryAddsConnectionTimeoutOverride()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string connectionId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            connectionId = connection.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE connections DROP COLUMN timeout_seconds; UPDATE library_info SET schema_version=15 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 15", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var reloaded = await upgraded.GetConnectionAsync(connectionId);
        Assert.Null(reloaded.TimeoutSeconds);
        var updated = await upgraded.UpdateConnectionAsync(connectionId, reloaded.Label, reloaded.BaseUrl, reloaded.CredentialHeaderName, reloaded.AuthPrefix, 45);
        Assert.Equal(45, updated.TimeoutSeconds);
    }

    [Fact]
    public async Task OpeningVersionSixteenLibraryAddsConnectionHeaders()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string connectionId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            connectionId = connection.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE connection_headers; UPDATE library_info SET schema_version=16 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 16", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var reloaded = await upgraded.GetConnectionAsync(connectionId);
        Assert.Empty(reloaded.AdditionalHeaders!);
        var updated = await upgraded.UpdateConnectionAsync(connectionId, reloaded.Label, reloaded.BaseUrl, reloaded.CredentialHeaderName, reloaded.AuthPrefix, reloaded.TimeoutSeconds, [new ConnectionHeader("X-Organization", "org_123")]);
        Assert.Equal("X-Organization", Assert.Single(updated.AdditionalHeaders!).Name);
    }

    [Fact]
    public async Task OpeningVersionSeventeenLibraryAddsGenericModalitySettings()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string connectionId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.GenericOpenAiCompatible, "https://gateway.example.com", "Authorization", "Bearer");
            connectionId = connection.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE connections DROP COLUMN generic_models_enabled; ALTER TABLE connections DROP COLUMN generic_models_path; ALTER TABLE connections DROP COLUMN generic_text_enabled; ALTER TABLE connections DROP COLUMN generic_text_path; ALTER TABLE connections DROP COLUMN generic_image_enabled; ALTER TABLE connections DROP COLUMN generic_image_path; UPDATE library_info SET schema_version=17 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 17", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var reloaded = await upgraded.GetConnectionAsync(connectionId);
        Assert.True(reloaded.GenericModalitySettings!.ModelsEnabled);
        Assert.True(reloaded.GenericModalitySettings!.TextGenerationEnabled);
        Assert.True(reloaded.GenericModalitySettings!.ImageGenerationEnabled);
        var updated = await upgraded.UpdateConnectionAsync(connectionId, reloaded.Label, reloaded.BaseUrl, reloaded.CredentialHeaderName, reloaded.AuthPrefix, reloaded.TimeoutSeconds, reloaded.AdditionalHeaders,
            new GenericConnectionModalitySettings(true, null, false, null, true, "v2/images/generations"));
        Assert.False(updated.GenericModalitySettings!.TextGenerationEnabled);
        Assert.Equal("v2/images/generations", updated.GenericModalitySettings!.ImageGenerationPathOverride);
    }

    [Fact]
    public async Task OpeningVersionEighteenLibraryAddsPromptImprovementHistory()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE generation_records DROP COLUMN prompt_improvement_record_id; DROP TABLE prompt_improvement_records; UPDATE library_info SET schema_version=18 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 18", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        Assert.Empty(await upgraded.GetPromptImprovementHistoryAsync());
        var improvement = await upgraded.RecordPromptImprovementAttemptAsync(modelId, "raw prompt", "guidance", "v1", ["candidate one"], null, 10, 5);
        Assert.Equal(GenerationStatus.Completed, improvement.Status);
        var record = await upgraded.RecordTextGenerationResultAsync(modelId, "candidate one", 1, upgraded.Descriptor.GeneratedFolderId, ["generated text"], null, promptImprovementRecordId: improvement.Id);
        Assert.Equal(improvement.Id, record.PromptImprovementRecordId);
    }

    [Fact]
    public async Task OpeningVersionNineteenLibraryAddsNeedsReviewColumns()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE models DROP COLUMN needs_review; ALTER TABLE saved_generation_settings DROP COLUMN needs_review; UPDATE library_info SET schema_version=19 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 19", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var reloaded = await upgraded.GetModelAsync(modelId);
        Assert.False(reloaded.NeedsReview);
        var updated = await upgraded.UpdateModelAsync(modelId, reloaded.Label, "gpt-4o-mini", reloaded.Mode, reloaded.SupportsSystemInstructions);
        Assert.True(updated.NeedsReview);
    }

    [Fact]
    public async Task OpeningVersionTwentyLibraryAddsTextResultFormat()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE models DROP COLUMN text_format; ALTER TABLE generation_records DROP COLUMN text_format; UPDATE library_info SET schema_version=20 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 20", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var reloaded = await upgraded.GetModelAsync(modelId);
        Assert.Equal(TextResultFormat.Markdown, reloaded.TextFormat);
        var updated = await upgraded.UpdateModelAsync(modelId, reloaded.Label, reloaded.ProviderModelId, reloaded.Mode, reloaded.SupportsSystemInstructions, TextResultFormat.PlainText);
        Assert.Equal(TextResultFormat.PlainText, updated.TextFormat);
        var record = await upgraded.RecordTextGenerationResultAsync(modelId, "a prompt", 1, upgraded.Descriptor.GeneratedFolderId, ["generated text"], null);
        Assert.Equal(TextResultFormat.PlainText, record.TextFormat);
        var file = await upgraded.GetFileAsync(record.ResultFileIds[0]);
        Assert.Equal("text/plain", file.MediaType);
        Assert.EndsWith(".txt", file.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningVersionTwentyOneLibraryAddsGenerationDrafts()
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
            command.CommandText = "DROP TABLE generation_drafts; UPDATE library_info SET schema_version=21 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 21", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var draft = await upgraded.CreateDraftAsync();
        Assert.Equal(upgraded.Descriptor.GeneratedFolderId, draft.DestinationFolderId);
        Assert.Equal(1, draft.ResultCount);
        var updated = await upgraded.ReplaceDraftStateAsync(draft.Id, "Custom Title", null, "a prompt", null, 2, upgraded.Descriptor.GeneratedFolderId, null, null);
        Assert.Equal("Custom Title", updated.CustomTitle);
        Assert.Equal("a prompt", updated.Prompt);
    }

    [Fact]
    public async Task OpeningVersionTwentyTwoLibraryAddsCredentialRevisionLedger()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string connectionId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            connectionId = connection.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE connection_credential_revisions; ALTER TABLE connections DROP COLUMN credential_revision_id; ALTER TABLE connections DROP COLUMN credential_requires_repair; UPDATE library_info SET schema_version=22 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 22", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var reloaded = await upgraded.GetConnectionAsync(connectionId);
        Assert.Null(reloaded.CredentialRevisionId);
        Assert.False(reloaded.CredentialRequiresRepair);
        var revisionId = await upgraded.BeginCredentialCandidateAsync(connectionId);
        var promotion = await upgraded.PromoteCredentialRevisionAsync(connectionId, revisionId);
        Assert.Equal(revisionId, promotion.Connection.CredentialRevisionId);
        Assert.True(promotion.Connection.HasCredential);
    }

    [Fact]
    public async Task OpeningVersionTwentyThreeLibraryAddsSavedSettingRevision()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string savedSettingId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            var saved = await created.CreateSavedSettingAsync("Preset", model.Id, "a prompt", 1, created.Descriptor.GeneratedFolderId);
            savedSettingId = saved.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE saved_generation_settings DROP COLUMN revision; UPDATE library_info SET schema_version=23 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 23", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var reloaded = await upgraded.GetSavedSettingAsync(savedSettingId);
        Assert.Equal(1, reloaded.Revision);
        var updated = await upgraded.UpdateSavedSettingAsync(savedSettingId, reloaded.Revision, reloaded.Title, reloaded.ModelId, "an updated prompt", reloaded.ResultCount, reloaded.DestinationFolderId);
        Assert.Equal(2, updated.Revision);
    }

    [Fact]
    public async Task OpeningVersionTwentyFourLibraryFixesGenerationResultFileDeletionCascade()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE generation_results RENAME TO generation_results_old;
                CREATE TABLE generation_results (id TEXT PRIMARY KEY,generation_id TEXT NOT NULL REFERENCES generation_records(id) ON DELETE CASCADE,file_id TEXT NOT NULL REFERENCES files(id),position INTEGER NOT NULL);
                INSERT INTO generation_results(id,generation_id,file_id,position) SELECT id,generation_id,file_id,position FROM generation_results_old;
                DROP TABLE generation_results_old;
                CREATE INDEX ix_generation_results_generation ON generation_results(generation_id);
                UPDATE library_info SET schema_version=24 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 24", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);

        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var record = await upgraded.RecordTextGenerationResultAsync(modelId, "a prompt", 1, upgraded.Descriptor.GeneratedFolderId, ["result"], null);
        var fileId = record.ResultFileIds[0];

        await upgraded.RecycleFileAsync(fileId);
        await upgraded.PermanentlyDeleteFileAsync(fileId);

        var reloaded = await upgraded.GetGenerationRecordAsync(record.Id);
        Assert.DoesNotContain(fileId, reloaded.ResultFileIds);
    }

    [Fact]
    public async Task OpeningVersionTwentyFiveLibraryAddsGenerationRecordLifecycleAndTombstoneColumns()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE generation_status_transitions;
                ALTER TABLE generation_records RENAME TO generation_records_old;
                CREATE TABLE generation_records (id TEXT PRIMARY KEY,model_id TEXT NULL REFERENCES models(id) ON DELETE SET NULL,model_label TEXT NOT NULL,provider_model_id TEXT NOT NULL,provider_type INTEGER NOT NULL,mode INTEGER NOT NULL,prompt TEXT NOT NULL,system_instructions TEXT NULL,result_count INTEGER NOT NULL,status INTEGER NOT NULL,error_message TEXT NULL,destination_folder_id TEXT NOT NULL,created_at TEXT NOT NULL,completed_at TEXT NULL,prompt_tokens INTEGER NULL,completion_tokens INTEGER NULL,source_file_id TEXT NULL REFERENCES files(id) ON DELETE SET NULL,prompt_improvement_record_id TEXT NULL REFERENCES prompt_improvement_records(id) ON DELETE SET NULL,text_format INTEGER NULL);
                INSERT INTO generation_records(id,model_id,model_label,provider_model_id,provider_type,mode,prompt,system_instructions,result_count,status,error_message,destination_folder_id,created_at,completed_at,prompt_tokens,completion_tokens,source_file_id,prompt_improvement_record_id,text_format)
                    SELECT id,model_id,model_label,provider_model_id,provider_type,mode,prompt,system_instructions,result_count,status,error_message,destination_folder_id,created_at,completed_at,prompt_tokens,completion_tokens,source_file_id,prompt_improvement_record_id,text_format FROM generation_records_old;
                DROP TABLE generation_records_old;
                CREATE INDEX ix_generation_records_created ON generation_records(created_at);
                ALTER TABLE generation_results RENAME TO generation_results_old;
                CREATE TABLE generation_results (id TEXT PRIMARY KEY,generation_id TEXT NOT NULL REFERENCES generation_records(id) ON DELETE CASCADE,file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,position INTEGER NOT NULL);
                INSERT INTO generation_results(id,generation_id,file_id,position) SELECT id,generation_id,file_id,position FROM generation_results_old;
                DROP TABLE generation_results_old;
                CREATE INDEX ix_generation_results_generation ON generation_results(generation_id);
                UPDATE library_info SET schema_version=25 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 25", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        var record = await upgraded.RecordTextGenerationResultAsync(modelId, "a prompt", 1, upgraded.Descriptor.GeneratedFolderId, ["result"], null);
        Assert.Equal(LibraryRecordState.Active, record.State);

        await upgraded.RecycleGenerationRecordAsync(record.Id);
        var recycled = await upgraded.GetGenerationRecordAsync(record.Id);
        Assert.Equal(LibraryRecordState.Recycled, recycled.State);
        await upgraded.RestoreGenerationRecordAsync(record.Id);

        var fileId = (await upgraded.GetGenerationRecordAsync(record.Id)).ResultFileIds[0];
        await upgraded.RecycleFileAsync(fileId);
        await upgraded.PermanentlyDeleteFileAsync(fileId);
        var reloaded = await upgraded.GetGenerationRecordAsync(record.Id);
        Assert.DoesNotContain(fileId, reloaded.ResultFileIds);
        Assert.Single(reloaded.TombstonedResults);
    }

    [Fact]
    public async Task OpeningVersionTwentySixLibraryAddsGenerationSettingsColumns()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE generation_records DROP COLUMN settings_temperature;
                ALTER TABLE generation_records DROP COLUMN settings_top_p;
                ALTER TABLE generation_records DROP COLUMN settings_max_tokens;
                ALTER TABLE generation_records DROP COLUMN settings_frequency_penalty;
                ALTER TABLE generation_records DROP COLUMN settings_presence_penalty;

                ALTER TABLE saved_generation_settings DROP COLUMN settings_temperature;
                ALTER TABLE saved_generation_settings DROP COLUMN settings_top_p;
                ALTER TABLE saved_generation_settings DROP COLUMN settings_max_tokens;
                ALTER TABLE saved_generation_settings DROP COLUMN settings_frequency_penalty;
                ALTER TABLE saved_generation_settings DROP COLUMN settings_presence_penalty;

                ALTER TABLE generation_drafts DROP COLUMN settings_temperature;
                ALTER TABLE generation_drafts DROP COLUMN settings_top_p;
                ALTER TABLE generation_drafts DROP COLUMN settings_max_tokens;
                ALTER TABLE generation_drafts DROP COLUMN settings_frequency_penalty;
                ALTER TABLE generation_drafts DROP COLUMN settings_presence_penalty;

                UPDATE library_info SET schema_version=26 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 26", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        var settings = new GenerationSettings(0.7, 0.9, 500, 0.5, -0.5);

        var draft = await upgraded.CreateDraftAsync();
        draft = await upgraded.ReplaceDraftStateAsync(draft.Id, null, modelId, "a prompt", null, 1, upgraded.Descriptor.GeneratedFolderId, null, null, settings);
        Assert.Equal(settings, draft.Settings);
        Assert.Equal(settings, (await upgraded.GetDraftAsync(draft.Id)).Settings);

        var saved = await upgraded.CreateSavedSettingAsync("Preset", modelId, "a prompt", 1, upgraded.Descriptor.GeneratedFolderId, settings: settings);
        Assert.Equal(settings, saved.Settings);
        Assert.Equal(settings, (await upgraded.GetSavedSettingAsync(saved.Id)).Settings);

        var record = await upgraded.RecordTextGenerationResultAsync(modelId, "a prompt", 1, upgraded.Descriptor.GeneratedFolderId, ["result"], null, settings: settings);
        Assert.Equal(settings, record.Settings);
        Assert.Equal(settings, (await upgraded.GetGenerationRecordAsync(record.Id)).Settings);
    }

    [Fact]
    public async Task CreateQueuedGenerationRecordRejectsASourceSlotRoleTheModelDoesNotDeclare()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var sourceFile = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        IReadOnlyList<GenerationSourceSlot> invalidSlots = [new(GenerationInputSlotRole.FirstFrame, sourceFile.Id, 0)];

        var exception = await Assert.ThrowsAsync<LibraryValidationException>(() =>
            workspace.CreateQueuedGenerationRecordAsync(model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId, sourceSlots: invalidSlots));

        Assert.Contains("FirstFrame", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetNonTerminalGenerationRecordsAsync());
    }

    [Fact]
    public async Task CreateQueuedGenerationRecordRejectsMoreReferenceImagesThanTheModelsCapabilityAllows()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var sourceFiles = new List<FileRecord>();
        for (var index = 0; index < 4; index++)
        {
            var path = temporary.Child($"source{index}.png");
            await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, (byte)index]);
            sourceFiles.Add(Assert.Single(await workspace.ImportAsync([path], workspace.Descriptor.RootFolderId)).File!);
        }
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        IReadOnlyList<GenerationSourceSlot> tooManySlots = sourceFiles.Select((file, index) => new GenerationSourceSlot(GenerationInputSlotRole.ReferenceImage, file.Id, index)).ToArray();

        await Assert.ThrowsAsync<LibraryValidationException>(() =>
            workspace.CreateQueuedGenerationRecordAsync(model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId, sourceSlots: tooManySlots));
    }

    [Fact]
    public async Task CreateQueuedGenerationRecordAcceptsAReferenceImageSlotWithinCapability()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var sourceFile = Assert.Single(await workspace.ImportAsync([sourcePath], workspace.Descriptor.RootFolderId)).File!;
        var connection = await workspace.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
        var model = await workspace.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
        IReadOnlyList<GenerationSourceSlot> validSlots = [new(GenerationInputSlotRole.ReferenceImage, sourceFile.Id, 0)];

        var record = await workspace.CreateQueuedGenerationRecordAsync(model.Id, "a prompt", 1, workspace.Descriptor.GeneratedFolderId, sourceSlots: validSlots);

        Assert.Single(record.SourceSlots);
    }

    [Fact]
    public async Task OpeningVersionTwentySevenLibraryAddsSecondaryAndTertiarySourceSlots()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        string sourceFileId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            modelId = model.Id;
            sourceFileId = Assert.Single(await created.ImportAsync([sourcePath], created.Descriptor.RootFolderId)).File!.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE generation_drafts DROP COLUMN secondary_source_file_id;
                ALTER TABLE generation_drafts DROP COLUMN tertiary_source_file_id;

                ALTER TABLE saved_generation_settings DROP COLUMN secondary_source_file_id;
                ALTER TABLE saved_generation_settings DROP COLUMN tertiary_source_file_id;

                ALTER TABLE generation_records DROP COLUMN secondary_source_file_id;
                ALTER TABLE generation_records DROP COLUMN secondary_tombstone_display_name;
                ALTER TABLE generation_records DROP COLUMN secondary_tombstone_media_type;
                ALTER TABLE generation_records DROP COLUMN secondary_tombstone_content_hash;
                ALTER TABLE generation_records DROP COLUMN tertiary_source_file_id;
                ALTER TABLE generation_records DROP COLUMN tertiary_tombstone_display_name;
                ALTER TABLE generation_records DROP COLUMN tertiary_tombstone_media_type;
                ALTER TABLE generation_records DROP COLUMN tertiary_tombstone_content_hash;

                UPDATE library_info SET schema_version=27 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 27", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        IReadOnlyList<GenerationSourceSlot> secondarySlot = [new(GenerationInputSlotRole.ReferenceImage, sourceFileId, 1)];
        IReadOnlyList<GenerationSourceSlot> tertiarySlot = [new(GenerationInputSlotRole.ReferenceImage, sourceFileId, 2)];

        var draft = await upgraded.CreateDraftAsync();
        draft = await upgraded.ReplaceDraftStateAsync(draft.Id, null, modelId, "a prompt", null, 1, upgraded.Descriptor.GeneratedFolderId, null, null, sourceSlots: secondarySlot);
        Assert.Contains(draft.SourceSlots, slot => slot.FileId == sourceFileId && slot.Order == 1);
        Assert.Contains((await upgraded.GetDraftAsync(draft.Id)).SourceSlots, slot => slot.FileId == sourceFileId && slot.Order == 1);

        var saved = await upgraded.CreateSavedSettingAsync("Preset", modelId, "a prompt", 1, upgraded.Descriptor.GeneratedFolderId, sourceSlots: tertiarySlot);
        Assert.Contains(saved.SourceSlots, slot => slot.FileId == sourceFileId && slot.Order == 2);
        Assert.Contains((await upgraded.GetSavedSettingAsync(saved.Id)).SourceSlots, slot => slot.FileId == sourceFileId && slot.Order == 2);

        var record = await upgraded.RecordTextGenerationResultAsync(modelId, "a prompt", 1, upgraded.Descriptor.GeneratedFolderId, ["result"], null, sourceSlots: secondarySlot);
        Assert.Contains(record.SourceSlots, slot => slot.FileId == sourceFileId && slot.Order == 1);
        Assert.Contains((await upgraded.GetGenerationRecordAsync(record.Id)).SourceSlots, slot => slot.FileId == sourceFileId && slot.Order == 1);
    }

    [Fact]
    public async Task OpeningVersionTwentyEightLibraryAddsSafetyBlockedCount()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE generation_records DROP COLUMN safety_blocked_count;

                UPDATE library_info SET schema_version=28 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 28", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        var record = await upgraded.RecordTextGenerationResultAsync(modelId, "a prompt", 1, upgraded.Descriptor.GeneratedFolderId, ["result"], null, safetyBlockedCount: 2);
        Assert.Equal(2, record.SafetyBlockedCount);
        Assert.Equal(2, (await upgraded.GetGenerationRecordAsync(record.Id)).SafetyBlockedCount);
    }

    [Fact]
    public async Task OpeningVersionTwentyNineLibraryAddsAsyncRemoteJobRegistry()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string connectionId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            connectionId = connection.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE async_remote_jobs;

                UPDATE library_info SET schema_version=29 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 29", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        var draft = await upgraded.CreateDraftAsync();
        var job = await upgraded.CreateAsyncRemoteJobAsync(draft.Id, ProviderType.OpenAi, connectionId, "remote-job", null, null);
        Assert.Equal(job.Id, Assert.Single(await upgraded.GetPendingAsyncRemoteJobsAsync()).Id);
    }

    [Fact]
    public async Task OpeningVersionThirtyLibraryAddsActualCostColumns()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE generation_records DROP COLUMN actual_cost;
                ALTER TABLE generation_records DROP COLUMN actual_cost_currency;

                UPDATE library_info SET schema_version=30 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 30", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        var record = await upgraded.RecordMediaGenerationResultAsync(modelId, "A cat on a skateboard", 1, upgraded.Descriptor.GeneratedFolderId, [[0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0]], null, actualCost: 0.25, actualCostCurrency: "USD");
        Assert.Equal(0.25, record.ActualCost);
        Assert.Equal("USD", record.ActualCostCurrency);
        var reloaded = await upgraded.GetGenerationRecordAsync(record.Id);
        Assert.Equal(0.25, reloaded.ActualCost);
        Assert.Equal("USD", reloaded.ActualCostCurrency);
    }

    [Fact]
    public async Task OpeningVersionThirtyOneLibraryAddsPerResultStatusColumns()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("Audio", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE generation_results DROP COLUMN status;
                ALTER TABLE generation_results DROP COLUMN result_error_message;

                UPDATE library_info SET schema_version=31 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 31", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        byte[] mp3SignatureBytes = [0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x21];
        var record = await upgraded.RecordMediaGenerationResultAsync(modelId, "Read this aloud", 1, upgraded.Descriptor.GeneratedFolderId, [mp3SignatureBytes], null);
        Assert.Equal(GenerationResultStatus.Committed, Assert.Single(record.Results).Status);
        var reloaded = await upgraded.GetGenerationRecordAsync(record.Id);
        Assert.Equal(GenerationResultStatus.Committed, Assert.Single(reloaded.Results).Status);
    }

    [Fact]
    public async Task OpeningVersionThirtyTwoLibraryAddsPendingUnverifiedResultsTable()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("Audio", connection.Id, "openai/gpt-4o-mini-tts", GenerationMode.Audio, false);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE pending_unverified_results;

                UPDATE library_info SET schema_version=32 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 32", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        byte[] pngSignatureBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        var record = await upgraded.RecordMediaGenerationResultAsync(modelId, "Read this aloud", 1, upgraded.Descriptor.GeneratedFolderId, [pngSignatureBytes], null);
        Assert.Equal(GenerationResultStatus.PendingReview, Assert.Single(record.Results).Status);
        Assert.Single(await upgraded.GetPendingUnverifiedResultsAsync(record.Id));
    }

    [Fact]
    public async Task OpeningVersionThirtyThreeLibraryAddsAsyncRemoteJobGenerationLinkColumns()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("Video", connection.Id, "google/veo-3.1", GenerationMode.Video, false);
            modelId = model.Id;
        }
        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX ix_async_remote_jobs_generation;
                ALTER TABLE async_remote_jobs DROP COLUMN generation_record_id;
                ALTER TABLE async_remote_jobs DROP COLUMN position;

                UPDATE library_info SET schema_version=33 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 33", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        var asyncJob = await upgraded.CreateAsyncRemoteJobAsync("draft-1", ProviderType.OpenRouter, "connection-id", "provider-job-1", null, null);
        var record = await upgraded.RecordMediaGenerationResultAsync(modelId, "A cat on a skateboard", 1, upgraded.Descriptor.GeneratedFolderId, null, "download failed");
        var linked = await upgraded.LinkAsyncRemoteJobToGenerationResultAsync(asyncJob.Id, record.Id, 0);
        Assert.Equal(record.Id, linked.GenerationRecordId);
        Assert.Equal(0, linked.Position);
        var found = Assert.Single(await upgraded.GetAsyncRemoteJobsForGenerationRecordAsync(record.Id));
        Assert.Equal(asyncJob.Id, found.Id);
    }

    [Fact]
    public async Task OpeningVersionThirtyFourLibraryAddsAdvancedGenerationSettingsColumns()
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
            command.CommandText = """
                ALTER TABLE generation_records DROP COLUMN settings_advanced_json;
                ALTER TABLE saved_generation_settings DROP COLUMN settings_advanced_json;
                ALTER TABLE generation_drafts DROP COLUMN settings_advanced_json;
                UPDATE library_info SET schema_version=34 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 34", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);
        var draft = await upgraded.CreateDraftAsync();
        await upgraded.ReplaceDraftStateAsync(draft.Id, null, null, "prompt", null, 1, upgraded.Descriptor.GeneratedFolderId, null, null,
            new GenerationSettings(AdvancedJson: "{\"response_format\":{\"type\":\"json_object\"}}"));

        Assert.Equal("{\"response_format\":{\"type\":\"json_object\"}}", (await upgraded.GetDraftAsync(draft.Id)).Settings.AdvancedJson);
    }

    [Fact]
    public async Task OpeningVersionThirtySixLibraryAddsSettingsFormatVersionColumnsAndTagsPreExistingRecordsWithTheImplicitOriginalFormat()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        string preMigrationRecordId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            modelId = model.Id;
            var record = await created.RecordTextGenerationResultAsync(modelId, "a prompt", 1, created.Descriptor.GeneratedFolderId, ["result"], null);
            preMigrationRecordId = record.Id;
        }

        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE generation_records DROP COLUMN settings_format_version;
                ALTER TABLE saved_generation_settings DROP COLUMN settings_format_version;
                UPDATE library_info SET schema_version=36 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 36", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        // A record written before this migration existed is retroactively tagged with the implicit
        // original format (1) rather than losing its version entirely or being misreported as current.
        var preMigrationRecord = await upgraded.GetGenerationRecordAsync(preMigrationRecordId);
        Assert.Equal(1, preMigrationRecord.SettingsFormatVersion);

        // A record created after the migration is tagged with the current format version.
        var postMigrationRecord = await upgraded.RecordTextGenerationResultAsync(modelId, "a newer prompt", 1, upgraded.Descriptor.GeneratedFolderId, ["result"], null);
        Assert.Equal(LibraryRules.CurrentGenerationSettingsFormatVersion, postMigrationRecord.SettingsFormatVersion);

        var savedSetting = await upgraded.CreateSavedSettingAsync("Saved", modelId, "a prompt", 1, upgraded.Descriptor.GeneratedFolderId);
        Assert.Equal(LibraryRules.CurrentGenerationSettingsFormatVersion, savedSetting.SettingsFormatVersion);
    }

    [Fact]
    public async Task OpeningVersionThirtySevenLibraryBackfillsGenerationSourceSlotsFromTheLegacyColumnsIncludingAnAlreadyTombstonedOne()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        var factory = new LibraryWorkspaceFactory();
        string modelId;
        string liveRecordId;
        string tombstonedRecordId;
        await using (var created = await factory.CreateAsync(root))
        {
            var connection = await created.CreateConnectionAsync("Connection", ProviderType.OpenAi, "https://api.openai.com/v1", "Authorization", "Bearer");
            var model = await created.CreateModelAsync("GPT", connection.Id, "gpt-4o", GenerationMode.Text, true);
            modelId = model.Id;
            var imported = Assert.Single(await created.ImportAsync([sourcePath], created.Descriptor.RootFolderId)).File!;
            var liveRecord = await created.RecordTextGenerationResultAsync(modelId, "a prompt", 1, created.Descriptor.GeneratedFolderId, ["result"], null);
            liveRecordId = liveRecord.Id;
            var tombstonedRecord = await created.RecordTextGenerationResultAsync(modelId, "another prompt", 1, created.Descriptor.GeneratedFolderId, ["result"], null);
            tombstonedRecordId = tombstonedRecord.Id;

            var databasePath = Path.Combine(root, "library.sqlite3");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
            await using var rawConnection = new SqliteConnection(connectionString);
            await rawConnection.OpenAsync();
            // Writes directly to the legacy source_file_id/tombstone_* columns that
            // CreateGenerationRecordAsync no longer populates, simulating rows left over from before
            // the generation_source_slots migration existed: one with a still-live source file, one
            // whose source was already permanently deleted (tombstone columns populated, source_file_id
            // NULL) before this migration ever ran.
            await using (var command = rawConnection.CreateCommand())
            {
                command.CommandText = "UPDATE generation_records SET source_file_id=$file WHERE id=$id;";
                command.Parameters.AddWithValue("$file", imported.Id);
                command.Parameters.AddWithValue("$id", liveRecordId);
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = rawConnection.CreateCommand())
            {
                command.CommandText = "UPDATE generation_records SET source_file_id=NULL, tombstone_source_display_name=$name, tombstone_source_media_type=$media, tombstone_source_content_hash=$hash WHERE id=$id;";
                command.Parameters.AddWithValue("$name", "deleted-source.png");
                command.Parameters.AddWithValue("$media", "image/png");
                command.Parameters.AddWithValue("$hash", "deadbeef");
                command.Parameters.AddWithValue("$id", tombstonedRecordId);
                await command.ExecuteNonQueryAsync();
            }
        }

        var databasePath2 = Path.Combine(root, "library.sqlite3");
        var connectionString2 = new SqliteConnectionStringBuilder { DataSource = databasePath2, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString2))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM generation_source_slots;
                UPDATE library_info SET schema_version=37 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 37", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        var liveRecordReloaded = await upgraded.GetGenerationRecordAsync(liveRecordId);
        var liveSlot = Assert.Single(liveRecordReloaded.SourceSlots);
        Assert.Equal(GenerationInputSlotRole.ReferenceImage, liveSlot.Role);
        Assert.Equal(0, liveSlot.Order);

        var tombstonedRecordReloaded = await upgraded.GetGenerationRecordAsync(tombstonedRecordId);
        Assert.Empty(tombstonedRecordReloaded.SourceSlots);
        var tombstonedSnapshot = Assert.Single(tombstonedRecordReloaded.SourceSlotSnapshots);
        Assert.Null(tombstonedSnapshot.FileId);
        Assert.Equal(0, tombstonedSnapshot.Order);
        Assert.Equal("deleted-source.png", tombstonedSnapshot.Identity.DisplayName);
        Assert.Equal("image/png", tombstonedSnapshot.Identity.MediaType);
        Assert.Equal("deadbeef", tombstonedSnapshot.Identity.ContentHash);
    }

    [Fact]
    public async Task OpeningVersionThirtyNineLibraryAddsTagsAndFileTagsTables()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        var sourcePath = temporary.Child("source.png");
        await File.WriteAllBytesAsync(sourcePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        string fileId;
        await using (var created = await factory.CreateAsync(root))
        {
            fileId = Assert.Single(await created.ImportAsync([sourcePath], created.Descriptor.RootFolderId)).File!.Id;
        }

        var databasePath = Path.Combine(root, "library.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE tags;
                DROP TABLE file_tags;
                UPDATE library_info SET schema_version=39 WHERE singleton=1;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var manifestPath = Path.Combine(root, "slopfactory-library.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"schemaVersion\": 41", "\"schemaVersion\": 39", StringComparison.Ordinal));

        await using var upgraded = await factory.OpenAsync(root);
        Assert.Equal(LibraryRules.SchemaVersion, upgraded.Descriptor.SchemaVersion);

        Assert.Empty(await upgraded.GetAllTagsAsync());
        await upgraded.SetTagsForFileAsync(fileId, ["Portrait", "Character"]);
        var fileTags = await upgraded.GetTagsForFileAsync(fileId);
        Assert.Equal(["Character", "Portrait"], fileTags.Select(tag => tag.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(2, (await upgraded.GetAllTagsAsync()).Count);
        var byFile = await upgraded.GetTagsForFilesAsync([fileId]);
        Assert.Equal(2, byFile[fileId].Count);
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
    public async Task BulkDuplicateProgressReportsCompletedCopyAndCancellationPreventsLaterCopies()
    {
        using var temporary = new TemporaryDirectory();
        var firstPath = temporary.Child("first.txt");
        var secondPath = temporary.Child("second.txt");
        await File.WriteAllTextAsync(firstPath, "first");
        await File.WriteAllTextAsync(secondPath, "second");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var files = await workspace.ImportAsync([firstPath, secondPath], workspace.Descriptor.RootFolderId);
        using var cancellation = new CancellationTokenSource();
        var reports = new List<BulkDuplicateProgress>();
        var progress = new InlineProgress<BulkDuplicateProgress>(item =>
        {
            reports.Add(item);
            if (item.CurrentItem == 1 && item.Completed) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.DuplicateFilesWithProgressAsync(files.Select(item => item.File!.Id).ToArray(), workspace.Descriptor.RootFolderId, progress, cancellation.Token));

        Assert.Contains(reports, item => item.CurrentItem == 1 && item.Completed);
        var contents = await workspace.GetFolderContentsAsync(workspace.Descriptor.RootFolderId);
        Assert.Contains(contents.Files, file => file.DisplayName == "first (2).txt");
        Assert.DoesNotContain(contents.Files, file => file.DisplayName == "second (2).txt");
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

    [Fact]
    public async Task RecursiveImportInventoryIsNonMutatingBoundedAndFreezesCandidates()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "visible.txt"), "visible");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "child.txt"), "child");
        var hidden = Path.Combine(source, "hidden.txt");
        await File.WriteAllTextAsync(hidden, "hidden");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));

        var inventory = await workspace.BuildRecursiveImportInventoryAsync([source]);

        Assert.Equal(2, inventory.EligibleCount);
        Assert.Equal(1, inventory.SkippedCounts[ImportInventorySkipReason.Hidden]);
        Assert.Contains(inventory.VirtualFolders, folder => folder.EndsWith("nested", StringComparison.Ordinal));
        Assert.Empty(await workspace.GetActiveFilesAsync());
        Assert.Single((await workspace.GetFolderContentsAsync(workspace.Descriptor.RootFolderId)).Folders);

        await File.WriteAllTextAsync(Path.Combine(source, "appeared-later.txt"), "later");
        var selected = inventory.Candidates.Select(candidate => new ConfirmedImportCandidate(candidate, ImportDuplicateChoice.ImportAnyway)).ToArray();
        var results = await workspace.ImportConfirmedInventoryAsync(inventory, selected, workspace.Descriptor.RootFolderId);

        Assert.Equal(2, results.Count(result => result.Outcome == ImportOutcome.Imported));
        Assert.DoesNotContain(await workspace.GetActiveFilesAsync(), file => file.DisplayName == "appeared-later.txt");
    }

    [Fact]
    public async Task RecursiveImportInventoryIncludesHiddenFilesOnlyWhenExplicitlyRequested()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source");
        Directory.CreateDirectory(source);
        var hidden = Path.Combine(source, "hidden.txt");
        await File.WriteAllTextAsync(hidden, "hidden");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));

        var defaultInventory = await workspace.BuildRecursiveImportInventoryAsync([source]);
        var includedInventory = await workspace.BuildRecursiveImportInventoryAsync([source], includeHiddenFiles: true);

        Assert.Empty(defaultInventory.Candidates);
        Assert.Equal(1, defaultInventory.SkippedCounts[ImportInventorySkipReason.Hidden]);
        Assert.Single(includedInventory.Candidates);
        Assert.Equal("hidden.txt", includedInventory.Candidates[0].DisplayName);
    }

    [Fact]
    public async Task ActiveDuplicatePreflightSupportsSkipAndImportAnywayWithoutMutatingTheExistingRecord()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("duplicate.txt");
        await File.WriteAllTextAsync(source, "duplicate content");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var existing = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var inventory = await workspace.BuildRecursiveImportInventoryAsync([source]);
        var match = Assert.Single(Assert.Single(inventory.DuplicateGroups).LibraryMatches);
        Assert.Equal(existing.Id, match.Id);
        Assert.Equal(LibraryRecordState.Active, match.State);

        var skipped = await workspace.ImportConfirmedInventoryAsync(inventory, [new ConfirmedImportCandidate(Assert.Single(inventory.Candidates), ImportDuplicateChoice.Skip)], workspace.Descriptor.RootFolderId);
        var imported = await workspace.ImportConfirmedInventoryAsync(inventory, [new ConfirmedImportCandidate(Assert.Single(inventory.Candidates), ImportDuplicateChoice.ImportAnyway)], workspace.Descriptor.RootFolderId);

        Assert.Equal(ImportOutcome.DuplicateSkipped, Assert.Single(skipped).Outcome);
        Assert.Equal(ImportOutcome.Imported, Assert.Single(imported).Outcome);
        Assert.Equal(2, (await workspace.GetActiveFilesAsync()).Count);
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetFileAsync(existing.Id)).State);
    }

    [Fact]
    public async Task RecycledDuplicatePreflightRestoresOnlyThroughTheNormalRestorePreview()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("recycled-duplicate.txt");
        await File.WriteAllTextAsync(source, "duplicate content");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var existing = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        await workspace.RecycleFileAsync(existing.Id);
        var inventory = await workspace.BuildRecursiveImportInventoryAsync([source]);
        var match = Assert.Single(Assert.Single(inventory.DuplicateGroups).LibraryMatches);
        Assert.Equal(LibraryRecordState.Recycled, match.State);

        var result = await workspace.ImportConfirmedInventoryAsync(inventory, [new ConfirmedImportCandidate(Assert.Single(inventory.Candidates), ImportDuplicateChoice.RestoreExisting, existing.Id)], workspace.Descriptor.RootFolderId);

        Assert.Equal(ImportOutcome.DuplicateSkipped, Assert.Single(result).Outcome);
        Assert.Equal(LibraryRecordState.Active, (await workspace.GetFileAsync(existing.Id)).State);
        Assert.Single(await workspace.GetActiveFilesAsync());
    }

    [Fact]
    public async Task PendingDeletionDuplicatePreflightCannotRestoreButCanImportANewRecord()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("pending-duplicate.txt");
        await File.WriteAllTextAsync(source, "duplicate content");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var existing = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        await workspace.RecycleFileAsync(existing.Id);
        var managedPath = workspace.GetManagedFilePath(existing);
        File.Delete(managedPath);
        Directory.CreateDirectory(managedPath);
        var deletion = await workspace.PermanentlyDeleteRecycleBinItemsAsync([new RecycleBinItemReference(RecycleBinItemKind.File, existing.Id)]);
        Assert.False(Assert.Single(deletion.Items).Succeeded);
        var inventory = await workspace.BuildRecursiveImportInventoryAsync([source]);
        var match = Assert.Single(Assert.Single(inventory.DuplicateGroups).LibraryMatches);
        Assert.Equal(LibraryRecordState.PendingPermanentDeletion, match.State);

        var result = await workspace.ImportConfirmedInventoryAsync(inventory, [new ConfirmedImportCandidate(Assert.Single(inventory.Candidates), ImportDuplicateChoice.ImportAnyway)], workspace.Descriptor.RootFolderId);

        Assert.Equal(ImportOutcome.Imported, Assert.Single(result).Outcome);
        Assert.Single(await workspace.GetActiveFilesAsync());
    }

    [Fact]
    public async Task ConfirmedInventoryRejectsChangedSourceIndependently()
    {
        using var temporary = new TemporaryDirectory();
        var first = temporary.Child("first.txt");
        var second = temporary.Child("second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var inventory = await workspace.BuildRecursiveImportInventoryAsync([first, second]);
        await File.AppendAllTextAsync(first, " changed");

        var results = await workspace.ImportConfirmedInventoryAsync(inventory, inventory.Candidates.Select(candidate => new ConfirmedImportCandidate(candidate, ImportDuplicateChoice.ImportAnyway)).ToArray(), workspace.Descriptor.RootFolderId);

        Assert.Equal(ImportOutcome.Failed, results.Single(result => result.Candidate.DisplayName == "first.txt").Outcome);
        Assert.Equal(ImportOutcome.Imported, results.Single(result => result.Candidate.DisplayName == "second.txt").Outcome);
    }

    [Fact]
    public async Task ExportIsVerifiedAtomicAndChangedBytesRemainRecoveryOnly()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "original");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var destination = temporary.Child("export.txt");

        var exported = await workspace.ExportFileAsync(file.Id, destination);

        Assert.Equal(FileExportOutcome.Exported, exported.Outcome);
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Equal(file.ContentHash, exported.ContentHash);
        var conflict = await workspace.ExportFileAsync(file.Id, destination);
        Assert.Equal(FileExportOutcome.Failed, conflict.Outcome);
        Assert.Equal("original", await File.ReadAllTextAsync(destination));

        await File.WriteAllTextAsync(workspace.GetManagedFilePath(file), "changed bytes");
        await workspace.RevalidateFileContentAsync(file.Id);
        Assert.Equal(FileExportOutcome.Failed, (await workspace.ExportFileAsync(file.Id, temporary.Child("normal.txt"))).Outcome);
        var recovery = await workspace.ExportChangedBytesAsync(file.Id, temporary.Child("recovery.txt"));
        Assert.Equal(FileExportOutcome.Exported, recovery.Outcome);
        Assert.Equal("changed bytes", await File.ReadAllTextAsync(temporary.Child("recovery.txt")));
        Assert.Equal(FileContentState.Changed, (await workspace.GetFileAsync(file.Id)).ContentState);
        Assert.Equal(file.ContentHash, (await workspace.GetFileContentProvenanceAsync(file.Id)).OriginalContentHash);
    }

    [Fact]
    public async Task SidecarIsNotWrittenWhenWriteSidecarOptionIsFalse()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "original");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var destination = temporary.Child("export.txt");

        var (media, sidecar) = await workspace.ExportFileWithSidecarAsync(file.Id, destination, ExportSidecarOptions.Default);

        Assert.Equal(FileExportOutcome.Exported, media.Outcome);
        Assert.Null(sidecar);
        Assert.False(File.Exists(destination + ".slopfactory.json"));
    }

    [Fact]
    public async Task SidecarWithNoOptInsContainsOnlyBaseFields()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "original");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var destination = temporary.Child("export.txt");

        var (media, sidecar) = await workspace.ExportFileWithSidecarAsync(file.Id, destination, ExportSidecarOptions.Default with { WriteSidecar = true });

        Assert.Equal(FileExportOutcome.Exported, media.Outcome);
        Assert.NotNull(sidecar);
        Assert.Equal(FileExportOutcome.Exported, sidecar!.Outcome);
        Assert.Equal(destination + ".slopfactory.json", sidecar.SidecarPath);
        var json = await File.ReadAllTextAsync(sidecar.SidecarPath!);
        Assert.DoesNotContain("\r\n", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("https://slopfactory.app/schema/sidecar/v1.json", root.GetProperty("$schema").GetString());
        Assert.Equal(1, root.GetProperty("sidecarSchemaVersion").GetInt32());
        Assert.Equal(file.MediaType, root.GetProperty("mediaType").GetString());
        Assert.Equal(file.ByteSize, root.GetProperty("byteSize").GetInt64());
        Assert.Equal(file.ContentHash, root.GetProperty("contentHash").GetString());
        Assert.False(root.TryGetProperty("displayName", out _));
        Assert.False(root.TryGetProperty("originalFileName", out _));
        Assert.False(root.TryGetProperty("fileId", out _));
        Assert.False(root.TryGetProperty("prompt", out _));
    }

    [Fact]
    public async Task SidecarFilenameOptInAddsDisplayAndOriginalFileName()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "original");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var destination = temporary.Child("export.txt");

        var (_, sidecar) = await workspace.ExportFileWithSidecarAsync(file.Id, destination, ExportSidecarOptions.Default with { WriteSidecar = true, IncludeFilenames = true });

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sidecar!.SidecarPath!));
        Assert.Equal(file.DisplayName, document.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(file.OriginalFileName, document.RootElement.GetProperty("originalFileName").GetString());
    }

    [Fact]
    public async Task SidecarOutputIsByteIdenticalAcrossRepeatedExportsOfTheSameFile()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "original");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var options = ExportSidecarOptions.Default with { WriteSidecar = true, IncludeFilenames = true };

        var (_, first) = await workspace.ExportFileWithSidecarAsync(file.Id, temporary.Child("export-1.txt"), options);
        var (_, second) = await workspace.ExportFileWithSidecarAsync(file.Id, temporary.Child("export-2.txt"), options);

        var firstBytes = await File.ReadAllBytesAsync(first!.SidecarPath!);
        var secondBytes = await File.ReadAllBytesAsync(second!.SidecarPath!);
        Assert.Equal(firstBytes, secondBytes);
    }

    [Fact]
    public async Task BuildSidecarPreviewProducesTheSameContentARealExportWouldWriteWithoutExportingAnything()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "original");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var options = ExportSidecarOptions.Default with { WriteSidecar = true, IncludeFilenames = true };

        var preview = await workspace.BuildSidecarPreviewAsync(file.Id, options);

        Assert.False(File.Exists(temporary.Child("export.txt") + ".slopfactory.json"));
        var (_, exported) = await workspace.ExportFileWithSidecarAsync(file.Id, temporary.Child("export.txt"), options);
        var actualBytes = await File.ReadAllBytesAsync(exported!.SidecarPath!);
        Assert.Equal(Encoding.UTF8.GetBytes(preview), actualBytes);
    }

    [Fact]
    public async Task SidecarFailureDoesNotAffectAlreadyCommittedMediaResult()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "original");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var destination = temporary.Child("export.txt");
        Directory.CreateDirectory(destination + ".slopfactory.json");

        var (media, sidecar) = await workspace.ExportFileWithSidecarAsync(file.Id, destination, ExportSidecarOptions.Default with { WriteSidecar = true });

        Assert.Equal(FileExportOutcome.Exported, media.Outcome);
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.NotNull(sidecar);
        Assert.Equal(FileExportOutcome.Failed, sidecar!.Outcome);
        Assert.NotNull(sidecar.Error);
    }

    [Fact]
    public async Task ExternalOpenUsesReadOnlyCopyAndNeverManagedPath()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "safe");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;

        var copy = await workspace.CreateExternalOpenCopyAsync(file.Id, temporary.Child("external"));

        Assert.NotEqual(workspace.GetManagedFilePath(file), copy.Path);
        Assert.True(copy.IsReadOnly);
        Assert.True((File.GetAttributes(copy.Path) & FileAttributes.ReadOnly) != 0);
        Assert.Equal("safe", await File.ReadAllTextAsync(copy.Path));
    }

    [Fact]
    public async Task WaveTechnicalMetadataIsReadOnlyAndMalformedMediaIsStored()
    {
        using var temporary = new TemporaryDirectory();
        var wave = temporary.Child("tone.wav");
        var bytes = new byte[48];
        "RIFF"u8.CopyTo(bytes); BitConverter.GetBytes(40).CopyTo(bytes, 4); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BitConverter.GetBytes(16).CopyTo(bytes, 16); BitConverter.GetBytes((short)1).CopyTo(bytes, 20); BitConverter.GetBytes((short)2).CopyTo(bytes, 22);
        BitConverter.GetBytes(48_000).CopyTo(bytes, 24); BitConverter.GetBytes(192_000).CopyTo(bytes, 28); BitConverter.GetBytes((short)4).CopyTo(bytes, 32); BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        "data"u8.CopyTo(bytes.AsSpan(36)); BitConverter.GetBytes(4).CopyTo(bytes, 40);
        await File.WriteAllBytesAsync(wave, bytes);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([wave], workspace.Descriptor.RootFolderId)).File!;

        var properties = await workspace.GetMediaTechnicalPropertiesAsync(file.Id);
        var system = await workspace.GetSystemMetadataAsync(file.Id);

        Assert.True(properties.IsAvailable);
        Assert.Equal(2, properties.ChannelCount);
        Assert.Equal(48_000, properties.SampleRate);
        Assert.Contains(system.Properties, item => item.Key == "audioCodec");
        Assert.Empty(await workspace.GetMetadataAsync(file.Id));
    }

    [Fact]
    public async Task MetadataNormalizationCommitsConvertibleValuesIndependentlyAndPreservesSensitivity()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var source1 = temporary.Child("one.txt");
        var source2 = temporary.Child("two.txt");
        await File.WriteAllTextAsync(source1, "one");
        await File.WriteAllTextAsync(source2, "two");
        var files = (await workspace.ImportAsync([source1, source2], workspace.Descriptor.RootFolderId)).Select(result => result.File!).ToArray();
        await workspace.SetMetadataAsync(files[0].Id, "rating", MetadataValueKind.Text, "12.5", true);
        await workspace.SetMetadataAsync(files[1].Id, "rating", MetadataValueKind.Text, "not a number", false);

        var preview = await workspace.PreviewMetadataNormalizationAsync(files.Select(file => file.Id).ToArray(), "rating", MetadataValueKind.Number);
        var result = await workspace.CommitMetadataNormalizationAsync(preview);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        var converted = Assert.Single(await workspace.GetMetadataAsync(files[0].Id));
        Assert.Equal(MetadataValueKind.Number, converted.Kind);
        Assert.True(converted.IsSensitive);
        Assert.Equal(MetadataValueKind.Text, Assert.Single(await workspace.GetMetadataAsync(files[1].Id)).Kind);
    }

    [Fact]
    public async Task RecursiveInventoryCancellationCreatesNoLibraryArtifacts()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source");
        Directory.CreateDirectory(source);
        for (var index = 0; index < 20; index++) await File.WriteAllTextAsync(Path.Combine(source, $"{index}.txt"), new string('x', 4096));
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.BuildRecursiveImportInventoryAsync([source], cancellationToken: cancellation.Token));

        Assert.Empty(await workspace.GetActiveFilesAsync());
        Assert.Single((await workspace.GetFolderContentsAsync(workspace.Descriptor.RootFolderId)).Folders);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(workspace.Descriptor.RootPath, "media")));
    }

    [Fact]
    public async Task IntegrityCheckpointResumesWithoutRepeatingCompletedRecords()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var sources = new[] { temporary.Child("one.txt"), temporary.Child("two.txt") };
        await File.WriteAllTextAsync(sources[0], "one");
        await File.WriteAllTextAsync(sources[1], "two");
        var files = (await workspace.ImportAsync(sources, workspace.Descriptor.RootFolderId)).Select(result => result.File!).ToArray();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<LibraryIntegrityScanProgress>(value => { if (value.Stage == "Hashing managed files" && value.ProcessedItems >= 5) cancellation.Cancel(); });

        var partial = await workspace.RunIntegrityScanAsync(progress, cancellation.Token);

        Assert.True(partial.WasCancelled);
        using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Descriptor.RootPath, ".staging", "integrity-scan-checkpoint.json")));
        var completedId = checkpoint.RootElement.GetProperty("CompletedFileIds")[0].GetString();
        var completedFile = files.Single(file => file.Id == completedId);
        await File.WriteAllTextAsync(workspace.GetManagedFilePath(completedFile), "changed after checkpoint");
        var resumed = await workspace.RunIntegrityScanAsync();
        Assert.True(resumed.IsComplete);
        Assert.DoesNotContain(resumed.Findings, finding => finding.RecordId == completedFile.Id);
        Assert.False(File.Exists(Path.Combine(workspace.Descriptor.RootPath, ".staging", "integrity-scan-checkpoint.json")));
    }

    [Fact]
    public async Task ActiveContentSafetyUsesDetectedBytesNotDisplayExtension()
    {
        using var temporary = new TemporaryDirectory();
        var disguised = temporary.Child("harmless.txt");
        await File.WriteAllBytesAsync(disguised, [0x4D, 0x5A, 0, 0, 0, 0, 0, 0]);
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([disguised], workspace.Descriptor.RootFolderId)).File!;

        Assert.Equal("application/x-msdownload", file.MediaType);
        Assert.Equal(ExternalOpenSafety.BlockedActiveContent, ContentActionPolicy.GetExternalOpenSafety(file));
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CreateExternalOpenCopyAsync(file.Id, temporary.Child("external")));
    }

    [Fact]
    public async Task CancelledExportLeavesNoDestinationOrPartialFile()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("large.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var destination = temporary.Child("cancelled.bin");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await workspace.ExportFileAsync(file.Id, destination, cancellationToken: cancellation.Token);

        Assert.Equal(FileExportOutcome.Cancelled, result.Outcome);
        Assert.False(File.Exists(destination));
        Assert.DoesNotContain(Directory.EnumerateFiles(temporary.Path), path => path.EndsWith(".slopfactory-exporting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportDoesNotCopyWindowsZoneUrlsOrAlternateStreams()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("download.txt");
        await File.WriteAllTextAsync(source, "content");
        await File.WriteAllTextAsync(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://secret.example/path\r\nReferrerUrl=https://private.example/\r\n");
        await File.WriteAllTextAsync(source + ":secret", "alternate");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));

        var result = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId));

        Assert.Equal(SourceZoneClassification.Internet, result.Candidate.SourceZone);
        Assert.Equal("content", await File.ReadAllTextAsync(workspace.GetManagedFilePath(result.File!)));
        Assert.False(File.Exists(workspace.GetManagedFilePath(result.File!) + ":Zone.Identifier"));
        Assert.False(File.Exists(workspace.GetManagedFilePath(result.File!) + ":secret"));
    }

    [Fact]
    public async Task ImportStoresManagedBytesWithSlopFactoryControlledAttributesAndPermissions()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("attributes.txt");
        await File.WriteAllTextAsync(source, "content");
        if (OperatingSystem.IsWindows()) File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.Hidden | FileAttributes.ReadOnly);
        else File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        try
        {
            var factory = new LibraryWorkspaceFactory();
            await using var workspace = await factory.CreateAsync(temporary.Child("library"));

            var result = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId));
            var managedPath = workspace.GetManagedFilePath(result.File!);

            Assert.Equal("content", await File.ReadAllTextAsync(managedPath));
            if (OperatingSystem.IsWindows()) Assert.Equal(0, (int)(File.GetAttributes(managedPath) & (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.ReparsePoint)));
            else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(managedPath));
        }
        finally
        {
            if (OperatingSystem.IsWindows() && File.Exists(source)) File.SetAttributes(source, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task ReviewedOperationsCannotCrossLibraryBoundaries()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var first = await factory.CreateAsync(temporary.Child("first-library"));
        await using var second = await factory.CreateAsync(temporary.Child("second-library"));
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "12");
        var inventory = await first.BuildRecursiveImportInventoryAsync([source]);

        await Assert.ThrowsAsync<LibraryValidationException>(() => second.ImportConfirmedInventoryAsync(inventory, inventory.Candidates.Select(item => new ConfirmedImportCandidate(item, ImportDuplicateChoice.ImportAnyway)).ToArray(), second.Descriptor.RootFolderId));

        var file = Assert.Single(await first.ImportAsync([source], first.Descriptor.RootFolderId)).File!;
        await first.SetMetadataAsync(file.Id, "rating", MetadataValueKind.Text, "12", false);
        var normalization = await first.PreviewMetadataNormalizationAsync([file.Id], "rating", MetadataValueKind.Number);
        await Assert.ThrowsAsync<LibraryValidationException>(() => second.CommitMetadataNormalizationAsync(normalization));
    }

    [Fact]
    public async Task BulkExportPreflightRequiresExplicitCollisionChoicesAndNeverRenamesSilently()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var sources = new[] { temporary.Child("one.txt"), temporary.Child("two.txt") };
        await File.WriteAllTextAsync(sources[0], "one");
        await File.WriteAllTextAsync(sources[1], "two");
        var files = (await workspace.ImportAsync(sources, workspace.Descriptor.RootFolderId)).Select(result => result.File!).ToArray();
        var destination = temporary.Child("exports");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "one.txt"), "existing");

        var preflight = await workspace.BuildBulkExportPreflightAsync(files.Select(file => file.Id).ToArray(), destination);
        var result = await workspace.ExportFilesAsync(preflight, new Dictionary<string, ExportCollisionChoice>());

        Assert.Equal(1, result.ExportedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "one.txt")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(destination, "two.txt")));
        Assert.Equal(2, Directory.EnumerateFiles(destination).Count());
    }

    [Fact]
    public async Task BulkExportWritesASidecarPerSuccessfullyExportedFileWithResultsIndexAlignedToMedia()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var sources = new[] { temporary.Child("one.txt"), temporary.Child("two.txt") };
        await File.WriteAllTextAsync(sources[0], "one");
        await File.WriteAllTextAsync(sources[1], "two");
        var files = (await workspace.ImportAsync(sources, workspace.Descriptor.RootFolderId)).Select(result => result.File!).ToArray();
        var destination = temporary.Child("exports");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "one.txt"), "existing");
        var preflight = await workspace.BuildBulkExportPreflightAsync(files.Select(file => file.Id).ToArray(), destination);

        var result = await workspace.ExportFilesAsync(preflight, new Dictionary<string, ExportCollisionChoice>(), ExportSidecarOptions.Default with { WriteSidecar = true });

        Assert.Equal(result.Items.Count, result.SidecarItems.Count);
        var oneIndex = Array.FindIndex(preflight.Items.ToArray(), item => item.DisplayName == "one.txt");
        var twoIndex = Array.FindIndex(preflight.Items.ToArray(), item => item.DisplayName == "two.txt");
        Assert.Equal(FileExportOutcome.Failed, result.Items[oneIndex].Outcome);
        Assert.Null(result.SidecarItems[oneIndex]);
        Assert.Equal(FileExportOutcome.Exported, result.Items[twoIndex].Outcome);
        Assert.NotNull(result.SidecarItems[twoIndex]);
        Assert.Equal(FileExportOutcome.Exported, result.SidecarItems[twoIndex]!.Outcome);
        Assert.True(File.Exists(Path.Combine(destination, "two.txt.slopfactory.json")));
        Assert.False(File.Exists(Path.Combine(destination, "one.txt.slopfactory.json")));
    }

    [Fact]
    public async Task BulkExportOmitsSidecarsEntirelyWhenNoSidecarOptionsAreSupplied()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var source = temporary.Child("one.txt");
        await File.WriteAllTextAsync(source, "one");
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var destination = temporary.Child("exports");
        Directory.CreateDirectory(destination);
        var preflight = await workspace.BuildBulkExportPreflightAsync([file.Id], destination);

        var result = await workspace.ExportFilesAsync(preflight, new Dictionary<string, ExportCollisionChoice>());

        Assert.Equal(FileExportOutcome.Exported, Assert.Single(result.Items).Outcome);
        Assert.Null(Assert.Single(result.SidecarItems));
        Assert.False(File.Exists(Path.Combine(destination, "one.txt.slopfactory.json")));
    }

    [Fact]
    public async Task FailedConfirmedImportDoesNotLeaveReviewedVirtualFolders()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("nested.txt");
        await File.WriteAllTextAsync(source, "before");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var inventory = await workspace.BuildRecursiveImportInventoryAsync([source]);
        inventory = inventory with { Candidates = inventory.Candidates.Select(candidate => candidate with { RelativeFolder = Path.Combine("parent", "child") }).ToArray() };
        await File.AppendAllTextAsync(source, " after review");

        var result = await workspace.ImportConfirmedInventoryAsync(inventory, inventory.Candidates.Select(candidate => new ConfirmedImportCandidate(candidate, ImportDuplicateChoice.ImportAnyway)).ToArray(), workspace.Descriptor.RootFolderId);

        Assert.Equal(ImportOutcome.Failed, Assert.Single(result).Outcome);
        var root = await workspace.GetFolderContentsAsync(workspace.Descriptor.RootFolderId);
        Assert.DoesNotContain(root.Folders, folder => folder.Name == "parent");
    }

    [Fact]
    public async Task MalformedMediaReportsUnavailableWithoutRejectingStoredBytes()
    {
        using var temporary = new TemporaryDirectory();
        var malformed = temporary.Child("malformed.wav");
        await File.WriteAllBytesAsync(malformed, "RIFF1234WAVE"u8.ToArray());
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([malformed], workspace.Descriptor.RootFolderId)).File!;

        var properties = await workspace.GetMediaTechnicalPropertiesAsync(file.Id);

        Assert.False(properties.IsAvailable);
        Assert.True(File.Exists(workspace.GetManagedFilePath(file)));
        Assert.Equal(FileContentState.Healthy, (await workspace.GetFileAsync(file.Id)).ContentState);
    }

    [Fact]
    public async Task CancelledMediaTechnicalProbeLeavesManagedBytesAndRecordUnchanged()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("cancelled.wav");
        await File.WriteAllBytesAsync(source, "RIFF0000WAVEfmt "u8.ToArray());
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        var managedPath = workspace.GetManagedFilePath(file);
        var originalBytes = await File.ReadAllBytesAsync(managedPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.GetMediaTechnicalPropertiesAsync(file.Id, cancellation.Token));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(managedPath));
        var current = await workspace.GetFileAsync(file.Id);
        Assert.Equal(file.ContentHash, current.ContentHash);
        Assert.Equal(file.ByteSize, current.ByteSize);
        Assert.Equal(FileContentState.Healthy, current.ContentState);
    }

    [Fact]
    public async Task MissingContentRejectsNormalExportAndExternalOpen()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "content");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var file = Assert.Single(await workspace.ImportAsync([source], workspace.Descriptor.RootFolderId)).File!;
        File.Delete(workspace.GetManagedFilePath(file));
        await workspace.RevalidateFileContentAsync(file.Id);

        Assert.Equal(FileExportOutcome.Failed, (await workspace.ExportFileAsync(file.Id, temporary.Child("export.txt"))).Outcome);
        await Assert.ThrowsAsync<LibraryValidationException>(() => workspace.CreateExternalOpenCopyAsync(file.Id, temporary.Child("external")));
    }

    [Fact]
    public async Task TimestampOnlyChangeAfterInventoryIsAcceptedWhenBytesStillMatch()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Child("source.txt");
        await File.WriteAllTextAsync(source, "unchanged bytes");
        var factory = new LibraryWorkspaceFactory();
        await using var workspace = await factory.CreateAsync(temporary.Child("library"));
        var inventory = await workspace.BuildRecursiveImportInventoryAsync([source]);
        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddMinutes(2));

        var results = await workspace.ImportConfirmedInventoryAsync(inventory, inventory.Candidates.Select(candidate => new ConfirmedImportCandidate(candidate, ImportDuplicateChoice.ImportAnyway)).ToArray(), workspace.Descriptor.RootFolderId);

        Assert.Equal(ImportOutcome.Imported, Assert.Single(results).Outcome);
    }

    [Fact]
    public async Task UnavailableActiveLibraryClosesSafelyAndPreservesTheRememberedLocation()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        var recent = new TestRecentLibraries();
        await using var state = new AppLibraryState(factory, new TestLocations(root), recent, new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        await state.SwitchAsync(root);
        var activeWorkspace = Assert.IsAssignableFrom<ILibraryWorkspace>(state.Workspace);

        await state.CloseUnavailableLibraryAsync(activeWorkspace, "not-writable");

        Assert.Null(state.Workspace);
        Assert.Equal(Path.GetFullPath(root), state.ActivePath);
        var remembered = Assert.Single(recent.Entries);
        Assert.Equal(RememberedLibraryState.Unavailable, remembered.State);
        Assert.Equal("not-writable", remembered.FailureStage);
        await using var reopened = await factory.OpenAsync(root);
        Assert.Equal(remembered.LibraryId, reopened.Descriptor.LibraryId);
    }

    [Fact]
    public async Task SensitiveRevealsClearWhenTheLibrarySwitchesOrBecomesUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        var firstRoot = temporary.Child("first-library");
        var secondRoot = temporary.Child("second-library");
        var factory = new LibraryWorkspaceFactory();
        await using (var first = await factory.CreateAsync(firstRoot)) { }
        await using (var second = await factory.CreateAsync(secondRoot)) { }
        var recent = new TestRecentLibraries();
        await using var state = new AppLibraryState(factory, new TestLocations(firstRoot, secondRoot), recent, new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        using var reveals = new SensitiveRevealSessionService(state);

        await state.SwitchAsync(firstRoot);
        reveals.Toggle("metadata-first");
        await state.SwitchAsync(secondRoot);
        Assert.False(reveals.IsRevealed("metadata-first"));

        reveals.Toggle("metadata-second");
        var activeWorkspace = Assert.IsAssignableFrom<ILibraryWorkspace>(state.Workspace);
        await state.CloseUnavailableLibraryAsync(activeWorkspace, "not-writable");
        Assert.False(reveals.IsRevealed("metadata-second"));
    }

    [Fact]
    public async Task RelinkAcceptsTheSameLibraryIdOnlyAfterItsOriginalLocationIsUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        var originalRoot = temporary.Child("original-library");
        var replacementRoot = temporary.Child("moved-library");
        var factory = new LibraryWorkspaceFactory();
        LibraryDescriptor descriptor;
        await using (var original = await factory.CreateAsync(originalRoot))
        {
            descriptor = original.Descriptor;
        }
        DirectoryCopy(originalRoot, replacementRoot);
        var recent = new TestRecentLibraries();
        recent.Entries.Add(new RecentLibrary(descriptor.LibraryId, descriptor.DisplayName, originalRoot, DateTimeOffset.UtcNow));
        await using var state = new AppLibraryState(factory, new TestLocations(originalRoot, replacementRoot), recent, new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());

        await state.RelinkAsync(descriptor.LibraryId, replacementRoot);

        Assert.NotNull(state.Workspace);
        Assert.Equal(Path.GetFullPath(replacementRoot), state.ActivePath);
        var remembered = Assert.Single(recent.Entries);
        Assert.Equal(descriptor.LibraryId, remembered.LibraryId);
        Assert.Equal(Path.GetFullPath(replacementRoot), remembered.Path);
    }

    [Fact]
    public async Task RelinkRejectsAReplacementWhileTheOriginalLocationIsStillAvailable()
    {
        using var temporary = new TemporaryDirectory();
        var originalRoot = temporary.Child("original-library");
        var factory = new LibraryWorkspaceFactory();
        LibraryDescriptor descriptor;
        await using (var original = await factory.CreateAsync(originalRoot))
        {
            descriptor = original.Descriptor;
        }
        var recent = new TestRecentLibraries();
        recent.Entries.Add(new RecentLibrary(descriptor.LibraryId, descriptor.DisplayName, originalRoot, DateTimeOffset.UtcNow));
        await using var state = new AppLibraryState(factory, new TestLocations(originalRoot), recent, new TestAvailabilityProbe(isAvailable: true), new TestPreferenceStore());

        var exception = await Assert.ThrowsAsync<LibraryValidationException>(() => state.RelinkAsync(descriptor.LibraryId, temporary.Child("replacement")));

        Assert.Contains("only while its original remembered location is unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Null(state.Workspace);
        Assert.Equal(originalRoot, Assert.Single(recent.Entries).Path);
    }

    [Fact]
    public async Task RelinkRejectsAReplacementWithADifferentPermanentLibraryId()
    {
        using var temporary = new TemporaryDirectory();
        var originalRoot = temporary.Child("original-library");
        var replacementRoot = temporary.Child("other-library");
        var factory = new LibraryWorkspaceFactory();
        LibraryDescriptor originalDescriptor;
        await using (var original = await factory.CreateAsync(originalRoot))
        {
            originalDescriptor = original.Descriptor;
        }
        await using (var replacement = await factory.CreateAsync(replacementRoot)) { }
        var recent = new TestRecentLibraries();
        recent.Entries.Add(new RecentLibrary(originalDescriptor.LibraryId, originalDescriptor.DisplayName, originalRoot, DateTimeOffset.UtcNow));
        await using var state = new AppLibraryState(factory, new TestLocations(originalRoot, replacementRoot), recent, new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());

        var exception = await Assert.ThrowsAsync<LibraryValidationException>(() => state.RelinkAsync(originalDescriptor.LibraryId, replacementRoot));

        Assert.Contains("different permanent ID", exception.Message, StringComparison.Ordinal);
        Assert.Null(state.Workspace);
        await using var reopened = await factory.OpenAsync(replacementRoot);
    }

    [Fact]
    public async Task FailedOpenBecomesASanitizedCorruptRememberedEntryWithoutAutomaticRepair()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("corrupt-library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        Directory.Delete(Path.Combine(root, "media"));
        var recent = new TestRecentLibraries();
        await using var state = new AppLibraryState(factory, new TestLocations(root), recent, new TestAvailabilityProbe(isAvailable: true), new TestPreferenceStore());

        await state.InitializeAsync();

        Assert.Null(state.Workspace);
        Assert.Contains("No automatic repair was attempted", state.Error, StringComparison.Ordinal);
        var remembered = Assert.Single(recent.Entries);
        Assert.Equal(RememberedLibraryState.Corrupt, remembered.State);
        Assert.Equal("open", remembered.FailureStage);
        Assert.Matches("^[a-f0-9]{12}$", remembered.DiagnosticId!);
        Assert.False(Directory.Exists(Path.Combine(root, "media")));
    }

    [Fact]
    public async Task ClosingIsRaisedBeforeASwitchWhileTheOutgoingWorkspaceIsStillValid()
    {
        using var temporary = new TemporaryDirectory();
        var firstRoot = temporary.Child("first-library");
        var secondRoot = temporary.Child("second-library");
        var factory = new LibraryWorkspaceFactory();
        await using (var first = await factory.CreateAsync(firstRoot)) { }
        await using (var second = await factory.CreateAsync(secondRoot)) { }
        await using var state = new AppLibraryState(factory, new TestLocations(firstRoot, secondRoot), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        await state.SwitchAsync(firstRoot);
        var invocationCount = 0;
        string? observedLibraryId = null;
        state.Closing += () =>
        {
            invocationCount++;
            observedLibraryId = state.Workspace?.Descriptor.LibraryId;
            return Task.CompletedTask;
        };

        await state.SwitchAsync(secondRoot);

        Assert.Equal(1, invocationCount);
        Assert.NotNull(observedLibraryId);
        Assert.NotEqual(state.Workspace!.Descriptor.LibraryId, observedLibraryId);
    }

    [Fact]
    public async Task ClosingIsNotRaisedOnTheFirstSwitchWhenNoWorkspaceWasPreviouslyOpen()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using var state = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        var invoked = false;
        state.Closing += () => { invoked = true; return Task.CompletedTask; };

        await state.SwitchAsync(root);

        Assert.False(invoked);
    }

    [Fact]
    public async Task ClosingIsRaisedBeforeAnInvalidLibraryCloses()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        await using var state = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: true), new TestPreferenceStore());
        await state.SwitchAsync(root);
        var activeWorkspace = Assert.IsAssignableFrom<ILibraryWorkspace>(state.Workspace);
        var invoked = false;
        state.Closing += () => { invoked = true; Assert.NotNull(state.Workspace); return Task.CompletedTask; };

        await state.CloseInvalidLibraryAsync(activeWorkspace, "invalid");

        Assert.True(invoked);
        Assert.Null(state.Workspace);
    }

    [Fact]
    public async Task ClosingIsNotRaisedBeforeAnUnavailableLibraryCloses()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        await using var state = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        await state.SwitchAsync(root);
        var activeWorkspace = Assert.IsAssignableFrom<ILibraryWorkspace>(state.Workspace);
        var invoked = false;
        state.Closing += () => { invoked = true; return Task.CompletedTask; };

        await state.CloseUnavailableLibraryAsync(activeWorkspace, "not-writable");

        Assert.False(invoked);
    }

    [Fact]
    public async Task MarkDraftDirtyThenClearDraftDirtyWithTheSameTokenRoundTripsThroughDirtyDraftIds()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        await using var state = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        await state.SwitchAsync(root);

        var token = state.MarkDraftDirty("draft-1");
        Assert.Equal(["draft-1"], state.DirtyDraftIds);

        state.ClearDirtyDraft("draft-1", token);

        Assert.Empty(state.DirtyDraftIds);
    }

    [Fact]
    public async Task MarkDraftDirtyIsIdempotentInDirtyDraftIdsButStillAdvancesTheEditToken()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        await using var state = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        await state.SwitchAsync(root);

        var firstToken = state.MarkDraftDirty("draft-1");
        var secondToken = state.MarkDraftDirty("draft-1");

        Assert.Equal(["draft-1"], state.DirtyDraftIds);
        Assert.NotEqual(firstToken, secondToken);
    }

    [Fact]
    public async Task ClearDirtyDraftIsANoOpWhenANewerEditTokenWasIssuedSinceTheCapturedToken()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        await using var state = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        await state.SwitchAsync(root);
        var staleToken = state.MarkDraftDirty("draft-1");
        state.MarkDraftDirty("draft-1");

        state.ClearDirtyDraft("draft-1", staleToken);

        Assert.Equal(["draft-1"], state.DirtyDraftIds);
    }

    [Fact]
    public async Task DirtyDraftIdsAreVisibleAfterInitializeAsyncOnAFreshInstanceSharingTheSamePreferenceStoreAndLibrary()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        var preferences = new TestPreferenceStore();
        await using (var first = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), preferences))
        {
            await first.SwitchAsync(root);
            first.MarkDraftDirty("draft-1");
        }

        await using var relaunched = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), preferences);
        await relaunched.InitializeAsync();

        Assert.Equal(["draft-1"], relaunched.DirtyDraftIds);
    }

    [Fact]
    public async Task DismissDirtyDraftsClearsAllMarkersForTheCurrentLibrary()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        var preferences = new TestPreferenceStore();
        await using var state = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), preferences);
        await state.SwitchAsync(root);
        state.MarkDraftDirty("draft-1");
        state.MarkDraftDirty("draft-2");

        state.DismissDirtyDrafts();

        Assert.Empty(state.DirtyDraftIds);
        await using var relaunched = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), preferences);
        await relaunched.InitializeAsync();
        Assert.Empty(relaunched.DirtyDraftIds);
    }

    [Fact]
    public async Task SwitchingToADifferentLibraryLoadsThatLibrarysOwnDirtyDraftIds()
    {
        using var temporary = new TemporaryDirectory();
        var firstRoot = temporary.Child("first-library");
        var secondRoot = temporary.Child("second-library");
        var factory = new LibraryWorkspaceFactory();
        await using (var first = await factory.CreateAsync(firstRoot)) { }
        await using (var second = await factory.CreateAsync(secondRoot)) { }
        await using var state = new AppLibraryState(factory, new TestLocations(firstRoot, secondRoot), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        await state.SwitchAsync(firstRoot);
        state.MarkDraftDirty("draft-in-first");

        await state.SwitchAsync(secondRoot);

        Assert.Empty(state.DirtyDraftIds);

        await state.SwitchAsync(firstRoot);

        Assert.Equal(["draft-in-first"], state.DirtyDraftIds);
    }

    [Fact]
    public async Task FlushForSuspensionAsyncRaisesClosing()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Child("library");
        var factory = new LibraryWorkspaceFactory();
        await using (var created = await factory.CreateAsync(root)) { }
        await using var state = new AppLibraryState(factory, new TestLocations(root), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        await state.SwitchAsync(root);
        var invocationCount = 0;
        state.Closing += () => { invocationCount++; return Task.CompletedTask; };

        await state.FlushForSuspensionAsync();

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task FlushForSuspensionAsyncSkipsWithoutBlockingWhenAnotherOperationAlreadyHoldsTheStateLock()
    {
        using var temporary = new TemporaryDirectory();
        var firstRoot = temporary.Child("first-library");
        var secondRoot = temporary.Child("second-library");
        var factory = new LibraryWorkspaceFactory();
        await using (var first = await factory.CreateAsync(firstRoot)) { }
        await using (var second = await factory.CreateAsync(secondRoot)) { }
        await using var state = new AppLibraryState(factory, new TestLocations(firstRoot, secondRoot), new TestRecentLibraries(), new TestAvailabilityProbe(isAvailable: false), new TestPreferenceStore());
        await state.SwitchAsync(firstRoot);
        var release = new TaskCompletionSource();
        var closingEntered = new TaskCompletionSource();
        state.Closing += async () => { closingEntered.SetResult(); await release.Task; };

        var switchTask = state.SwitchAsync(secondRoot);
        await closingEntered.Task;
        var invocationCount = 0;
        state.Closing += () => { invocationCount++; return Task.CompletedTask; };

        await state.FlushForSuspensionAsync();
        Assert.Equal(0, invocationCount);

        release.SetResult();
        await switchTask;
    }

    [Fact]
    public void DeferringAnIntegrityScanRecommendationDismissesItWithoutStartingAScan()
    {
        var recommendation = new IntegrityScanRecommendationService();
        recommendation.Recommend(IntegrityScanRecommendationReason.WatcherOverflow);

        recommendation.Defer();

        Assert.False(recommendation.IsRecommended);
        Assert.Empty(recommendation.Reasons);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TestLocations(params string[] paths) : ILibraryLocationService
    {
        public string DefaultPath => paths[0];
        public bool IsAllowedPath(string candidate) => paths.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(candidate), StringComparison.Ordinal));
    }

    private sealed class TestAvailabilityProbe(bool isAvailable) : ILibraryAvailabilityProbe
    {
        public bool IsAvailable(string path, string? expectedVolumeIdentity, out string failureStage)
        {
            failureStage = isAvailable ? string.Empty : "not-writable";
            return isAvailable;
        }
    }

    private sealed class TestPreferenceStore : IAppPreferenceStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string ReadString(string key, string defaultValue) => _values.TryGetValue(key, out var value) ? value : defaultValue;
        public void WriteString(string key, string value) => _values[key] = value;
    }

    private sealed class TestRecentLibraries : IRecentLibraryService
    {
        public List<RecentLibrary> Entries { get; } = [];
        public IReadOnlyList<RecentLibrary> GetAll() => Entries;
        public void RecordOpened(LibraryDescriptor descriptor)
        {
            Entries.RemoveAll(entry => entry.LibraryId == descriptor.LibraryId);
            Entries.Add(new RecentLibrary(descriptor.LibraryId, descriptor.DisplayName, descriptor.RootPath, DateTimeOffset.UtcNow));
        }
        public void RecordFailure(string path, string displayName, string? libraryId, RememberedLibraryState state, string failureStage, string diagnosticId)
        {
            Entries.RemoveAll(entry => entry.LibraryId == libraryId);
            Entries.Add(new RecentLibrary(libraryId!, displayName, path, DateTimeOffset.UtcNow, null, state, failureStage, diagnosticId));
        }
        public void ValidateNoOverlap(string candidatePath) { }
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
