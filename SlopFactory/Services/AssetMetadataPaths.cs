namespace SlopFactory.Services;

public static class AssetMetadataPaths
{
    // assets/characters/hero.png -> metadata/characters/hero.png.json
    public static string ForAsset(string projectFolder, string assetPath, bool createDirectory = false)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(projectFolder), Path.GetFullPath(assetPath)).Replace('\\', '/');
        return ForRelativeAsset(projectFolder, relative, createDirectory);
    }

    public static string ForRelativeAsset(string projectFolder, string relativeAssetPath, bool createDirectory = false)
    {
        var project = Path.GetFullPath(projectFolder);
        var relative = relativeAssetPath.Replace('\\', '/');
        var assetsRoot = Path.GetFullPath(Path.Combine(project, "assets"));
        var asset = Path.GetFullPath(Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!asset.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !relative.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Asset metadata must belong to a file inside the project's assets folder.");

        var insideAssets = Path.GetRelativePath(assetsRoot, asset);
        var destination = Path.Combine(project, "metadata", insideAssets + ".json");
        if (createDirectory) Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        return destination;
    }

    public static void PruneEmptyFolders(string projectFolder, string folderPath)
    {
        var root = Path.GetFullPath(Path.Combine(projectFolder, "metadata"));
        var folder = Path.GetFullPath(folderPath);
        while (folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder)!;
        }
    }

}
