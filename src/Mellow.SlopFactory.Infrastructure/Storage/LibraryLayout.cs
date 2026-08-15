using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Storage;

internal sealed class LibraryLayout
{
    public const string ManifestFileName = "slopfactory-library.json";
    public const string DatabaseFileName = "library.sqlite3";
    public const string ManagedDirectoryName = "media";
    public const string StagingDirectoryName = ".staging";
    /// <summary>Durably holds bytes that failed the expected-media-category check but weren't
    /// recognized as a rejection payload, pending the user's Retain/Discard decision. Distinct from
    /// <see cref="StagingDirectoryName"/> specifically because staging is transient (wiped on a
    /// failed create), while these bytes must survive until the user reviews them.</summary>
    public const string PendingReviewDirectoryName = ".pending-review";
    public const string LockFileName = ".slopfactory.lock";

    public LibraryLayout(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        ManifestPath = ContainedPath(ManifestFileName);
        DatabasePath = ContainedPath(DatabaseFileName);
        ManagedPath = ContainedPath(ManagedDirectoryName);
        StagingPath = ContainedPath(StagingDirectoryName);
        PendingReviewPath = ContainedPath(PendingReviewDirectoryName);
        LockPath = ContainedPath(LockFileName);
    }

    public string RootPath { get; }
    public string ManifestPath { get; }
    public string DatabasePath { get; }
    public string ManagedPath { get; }
    public string StagingPath { get; }
    public string PendingReviewPath { get; }
    public string LockPath { get; }

    public string ManagedFilePath(string managedName) => ContainedPath(ManagedDirectoryName, managedName);

    public string StagingFilePath(string name) => ContainedPath(StagingDirectoryName, name);

    public string PendingReviewFilePath(string name) => ContainedPath(PendingReviewDirectoryName, name);

    public void ValidateExistingRoot()
    {
        if (!Directory.Exists(RootPath))
        {
            throw new LibraryValidationException("The selected library location does not exist.");
        }

        var rootInfo = new DirectoryInfo(RootPath);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new LibraryValidationException("A library root cannot be a symbolic link or reparse-point redirection.");
        }
    }

    public void ValidateRequiredEntries()
    {
        ValidateRegularFile(ManifestPath, "library manifest");
        ValidateRegularFile(DatabasePath, "library database");
        ValidateDirectory(ManagedPath, "managed-media directory");
        if (Directory.Exists(StagingPath)) ValidateDirectory(StagingPath, "staging directory");
        if (Directory.Exists(PendingReviewPath)) ValidateDirectory(PendingReviewPath, "pending-review directory");
    }

    public void ValidateManagedDirectories()
    {
        ValidateDirectory(ManagedPath, "managed-media directory");
        ValidateDirectory(StagingPath, "staging directory");
        ValidateDirectory(PendingReviewPath, "pending-review directory");
    }

    public void EnsureManagedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(RootPath, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new LibraryValidationException("A managed operation resolved outside the library root.");
        }
    }

    private string ContainedPath(params string[] components)
    {
        var path = components.Aggregate(RootPath, Path.Combine);
        EnsureManagedPath(path);
        return path;
    }

    private static void ValidateDirectory(string path, string label)
    {
        if (!Directory.Exists(path)) throw new LibraryValidationException($"The {label} is missing.");
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException($"The {label} cannot be a symbolic link or reparse-point redirection.");
    }

    private static void ValidateRegularFile(string path, string label)
    {
        if (Directory.Exists(path) || !File.Exists(path)) throw new LibraryValidationException($"The {label} is missing or is not a regular file.");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new LibraryValidationException($"The {label} cannot be a symbolic link or reparse-point redirection.");
    }
}
