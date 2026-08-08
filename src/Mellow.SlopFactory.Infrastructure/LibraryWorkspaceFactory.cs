using Microsoft.Data.Sqlite;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Persistence;
using Mellow.SlopFactory.Infrastructure.Storage;

namespace Mellow.SlopFactory.Infrastructure;

public sealed class LibraryWorkspaceFactory : ILibraryWorkspaceFactory
{
    public async Task<ILibraryWorkspace> CreateAsync(string rootPath, string displayName = "SlopFactory Library", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var normalizedName = LibraryRules.NormalizeDisplayName(displayName, "Library name");
        var fullPath = Path.GetFullPath(rootPath);
        ValidateLocalStoragePath(fullPath);
        var createdRoot = !Directory.Exists(fullPath);
        if (createdRoot)
        {
            Directory.CreateDirectory(fullPath);
        }
        else if (Directory.EnumerateFileSystemEntries(fullPath).Any())
        {
            throw new LibraryValidationException("A new library can be created only in an empty directory.");
        }

        var layout = new LibraryLayout(fullPath);
        FileStream? libraryLock = null;
        try
        {
            layout.ValidateExistingRoot();
            libraryLock = AcquireLock(layout);
            Directory.CreateDirectory(layout.ManagedPath);
            Directory.CreateDirectory(layout.StagingPath);
            var libraryId = LibraryRules.NewId();
            var rootFolderId = LibraryRules.NewId();
            var generatedFolderId = LibraryRules.NewId();
            var manifest = new LibraryManifest(LibraryRules.FormatIdentity, LibraryRules.ManifestVersion, libraryId, normalizedName, LibraryRules.SchemaVersion);
            await SqliteLibraryDatabase.InitializeAsync(layout.DatabasePath, manifest, rootFolderId, generatedFolderId, cancellationToken).ConfigureAwait(false);
            await LibraryManifestStore.WriteAsync(layout, manifest, cancellationToken).ConfigureAwait(false);
            layout.ValidateRequiredEntries();
            var database = new SqliteLibraryDatabase(layout.DatabasePath);
            var descriptor = await database.ValidateAndDescribeAsync(manifest, layout.RootPath, cancellationToken).ConfigureAwait(false);
            return new LibraryWorkspace(layout, descriptor, manifest, database, libraryLock);
        }
        catch
        {
            libraryLock?.Dispose();
            TryDelete(layout.LockPath);
            if (createdRoot)
            {
                TryDeleteTree(fullPath);
            }
            throw;
        }
    }

    public async Task<ILibraryWorkspace> OpenAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ValidateLocalStoragePath(Path.GetFullPath(rootPath));
        var layout = new LibraryLayout(rootPath);
        layout.ValidateExistingRoot();
        var initialManifest = await LibraryManifestStore.ReadAsync(layout, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(layout.DatabasePath) || !Directory.Exists(layout.ManagedPath))
        {
            throw new LibraryValidationException("The manifest, database and managed-media directory do not form a complete library.");
        }
        layout.ValidateRequiredEntries();

        var libraryLock = AcquireLock(layout);
        try
        {
            var lockedManifest = await LibraryManifestStore.ReadAsync(layout, cancellationToken).ConfigureAwait(false);
            if (lockedManifest != initialManifest)
            {
                throw new LibraryValidationException("The library manifest changed while the library was opening.");
            }
            Directory.CreateDirectory(layout.StagingPath);
            layout.ValidateManagedDirectories();
            var currentManifest = await UpgradeIfRequiredAsync(layout, lockedManifest, cancellationToken).ConfigureAwait(false);
            var database = new SqliteLibraryDatabase(layout.DatabasePath);
            var descriptor = await database.ValidateAndDescribeAsync(currentManifest, layout.RootPath, cancellationToken).ConfigureAwait(false);
            return new LibraryWorkspace(layout, descriptor, currentManifest, database, libraryLock);
        }
        catch
        {
            libraryLock.Dispose();
            throw;
        }
    }

    public async Task<ILibraryWorkspace> AdoptCopyAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var workspace = await OpenAsync(rootPath, cancellationToken).ConfigureAwait(false);
        try
        {
            await workspace.AdoptAsIndependentLibraryAsync(cancellationToken).ConfigureAwait(false);
            return workspace;
        }
        catch
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static FileStream AcquireLock(LibraryLayout layout)
    {
        try
        {
            return new FileStream(layout.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4_096, FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new LibraryLockedException($"The library is already open by another process: {exception.Message}");
        }
    }

    private static void ValidateLocalStoragePath(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (new Uri(fullPath).IsUnc) throw new LibraryValidationException("Network locations cannot host a SlopFactory library.");
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root)) throw new LibraryValidationException("The library location must be on a local volume.");
            try
            {
                if (new DriveInfo(root).DriveType == DriveType.Network) throw new LibraryValidationException("Network locations cannot host a SlopFactory library.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new LibraryValidationException("The library location's storage volume could not be validated.");
            }
        }

        if (Directory.Exists(fullPath) && (new DirectoryInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new LibraryValidationException("A redirected directory cannot host a SlopFactory library.");
        }
        if (OperatingSystem.IsWindows() && Directory.Exists(fullPath) && (new DirectoryInfo(fullPath).Attributes & FileAttributes.Offline) != 0)
        {
            throw new LibraryValidationException("This library location is an online-only placeholder. Make it fully available on this device before opening it.");
        }
    }

    private static async Task<LibraryManifest> UpgradeIfRequiredAsync(LibraryLayout layout, LibraryManifest manifest, CancellationToken cancellationToken)
    {
        var backupPath = layout.DatabasePath + ".upgrade-backup";
        if (manifest.SchemaVersion == LibraryRules.SchemaVersion)
        {
            TryDelete(backupPath);
            return manifest;
        }

        if (File.Exists(backupPath))
        {
            SqliteConnection.ClearAllPools();
            File.Copy(backupPath, layout.DatabasePath, true);
            TryDelete(layout.DatabasePath + "-wal");
            TryDelete(layout.DatabasePath + "-shm");
            TryDelete(backupPath);
        }

        await SqliteLibraryDatabase.CheckpointForBackupAsync(layout.DatabasePath, cancellationToken).ConfigureAwait(false);
        File.Copy(layout.DatabasePath, backupPath, false);
        var upgradedManifest = manifest with { SchemaVersion = LibraryRules.SchemaVersion };
        var completed = false;
        try
        {
            await SqliteLibraryDatabase.UpgradeAsync(layout.DatabasePath, manifest.SchemaVersion, cancellationToken).ConfigureAwait(false);
            await LibraryManifestStore.WriteAsync(layout, upgradedManifest, cancellationToken).ConfigureAwait(false);
            completed = true;
            return upgradedManifest;
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            File.Copy(backupPath, layout.DatabasePath, true);
            TryDelete(layout.DatabasePath + "-wal");
            TryDelete(layout.DatabasePath + "-shm");
            await LibraryManifestStore.WriteAsync(layout, manifest, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (completed) TryDelete(backupPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
