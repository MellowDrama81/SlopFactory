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

        var metadata = await workspace.SetMetadataAsync(fileA.Id, "Rating", MetadataValueKind.Number, "4.5", false);
        var renamedMetadata = await workspace.RenameMetadataAsync(fileA.Id, "Rating", "Score");
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
        await workspace.RestoreFolderAsync(folder.Id);

        await workspace.RecycleFileAsync(file.Id);
        await workspace.PermanentlyDeleteFileAsync(file.Id);
        Assert.False(File.Exists(managedPath));
        Assert.DoesNotContain(await workspace.GetRecycledFilesAsync(), item => item.Id == file.Id);
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
