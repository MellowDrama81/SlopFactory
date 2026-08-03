using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Gui.Services;

public sealed record AppLibraryLocation(string Label, string Path, bool IsInternal);

public interface ILibraryLocationService
{
    string DefaultPath { get; }
    IReadOnlyList<AppLibraryLocation> GetAvailableLocations();
    bool IsAllowedPath(string path);
}

public sealed class LibraryLocationService : ILibraryLocationService
{
    public string DefaultPath
    {
        get
        {
#if WINDOWS
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mellow", "SlopFactory", "Library");
#else
            return Path.Combine(FileSystem.AppDataDirectory, "Library");
#endif
        }
    }

    public IReadOnlyList<AppLibraryLocation> GetAvailableLocations()
    {
#if ANDROID
        var results = new List<AppLibraryLocation> { new("Internal app storage", DefaultPath, true) };
        var context = Android.App.Application.Context;
        foreach (var directory in (context.GetExternalFilesDirs(null) ?? []).OfType<Java.IO.File>())
        {
            var path = Path.Combine(directory.AbsolutePath, "Library");
            if (!results.Any(item => string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(path), StringComparison.Ordinal)))
            {
                results.Add(new($"App storage on {directory.Name}", path, false));
            }
        }
        return results;
#else
        return [new("Default local library", DefaultPath, true)];
#endif
    }

    public bool IsAllowedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
#if ANDROID
        var fullPath = Path.GetFullPath(path);
        return GetAvailableLocations().Any(item => string.Equals(Path.GetFullPath(item.Path), fullPath, StringComparison.Ordinal));
#else
        if (!Path.IsPathFullyQualified(path)) return false;
        var fullPath = Path.GetFullPath(path);
        if (new Uri(fullPath).IsUnc) return false;
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return false;
        try { return new DriveInfo(root).DriveType != DriveType.Network; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
#endif
    }
}
