namespace SlopFactory.Services;

// Brings images into a project's assets folder so pickers and editors can reference them
// by project-relative path (for example "assets/imported/hero.png").
public sealed class ProjectImageImporter(ProjectLibrary projectLibrary)
{
    public const long MaxFileSize = 1024L * 1024 * 1024;

    public static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp";

    private static readonly FilePickerFileType ImageFileTypes = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.WinUI] = [".jpg", ".jpeg", ".png", ".gif", ".webp"],
        [DevicePlatform.Android] = ["image/jpeg", "image/png", "image/gif", "image/webp"]
    });

    // Asks the user for an image file and imports it; returns null when they cancel.
    public async Task<string?> PickAndImportAsync(string projectFolder)
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose an image to import", FileTypes = ImageFileTypes });
        if (file is null) return null;
        await using var source = await file.OpenReadAsync();
        return await ImportAsync(projectFolder, Path.GetFileName(file.FileName), source);
    }

    // Copies an image file into assets/imported with empty metadata.
    public async Task<string> ImportAsync(string projectFolder, string fileName, Stream source)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !IsImage(fileName))
            throw new InvalidOperationException("Choose a JPG, PNG, GIF, or WebP image.");
        var project = Path.GetFullPath(projectFolder);
        if (!Directory.Exists(project)) throw new DirectoryNotFoundException("The selected project folder is unavailable.");

        var importedFolder = Path.Combine(project, "assets", "imported");
        Directory.CreateDirectory(importedFolder);
        var destination = UniquePath(importedFolder, fileName);
        string? metadataPath = null;
        try
        {
            await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(target);
            var relativePath = Path.GetRelativePath(project, destination).Replace('\\', '/');
            metadataPath = AssetMetadataPaths.ForRelativeAsset(project, relativePath, createDirectory: true);
            var metadata = new { asset = relativePath, name = Path.GetFileName(destination), tags = Array.Empty<string>() };
            await File.WriteAllTextAsync(metadataPath, System.Text.Json.JsonSerializer.Serialize(metadata));
            return relativePath;
        }
        catch
        {
            try { File.Delete(destination); } catch { /* Keep the original import error. */ }
            if (metadataPath is not null)
            {
                try { File.Delete(metadataPath); } catch { /* Keep the original import error. */ }
            }
            throw;
        }
    }

    // Resolves an image dragged from an Assets pane. Images from another registered project are copied into assets/imported.
    public async Task<string> UseAssetAsync(string projectFolder, string sourcePath, string sourceRoot)
    {
        var project = Normalize(projectFolder);
        var source = Path.GetFullPath(sourcePath);
        var root = Normalize(sourceRoot);
        var sourceProjectRegistered = projectLibrary.GetAll().Any(item => Directory.Exists(item.FolderPath) &&
            string.Equals(Normalize(Path.Combine(item.FolderPath, "assets")), root, StringComparison.OrdinalIgnoreCase));
        if (!sourceProjectRegistered || !IsInFolder(source, root) || !File.Exists(source) || !IsImage(source))
            throw new InvalidOperationException("Drop an image asset from a registered project.");

        if (string.Equals(root, Normalize(Path.Combine(project, "assets")), StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(project, source).Replace('\\', '/');
        await using var stream = File.OpenRead(source);
        return await ImportAsync(project, Path.GetFileName(source), stream);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool IsInFolder(string path, string folder) => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string UniquePath(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var path = Path.Combine(folder, fileName);
        for (var suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++)
            path = Path.Combine(folder, $"{stem} ({suffix}){extension}");
        return path;
    }
}
