using System.IO.Compression;

namespace SlopFactory.Services;

public sealed class AssetExporter
{
    public async Task<string?> ExportSelectionAsync(IEnumerable<string> assetPaths)
    {
        var paths = assetPaths.Where(path => File.Exists(path) || Directory.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) throw new FileNotFoundException("No selected assets exist.");
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "assets"
        };
        picker.FileTypeChoices.Add("ZIP archive", [".zip"]);
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (window is null) throw new InvalidOperationException("A window is required to choose an export location.");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var destination = await picker.PickSaveFileAsync();
        if (destination is null) return null;
        var temporaryArchive = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}.zip");
        try
        {
            await Task.Run(() => CreateSelectionArchive(paths, temporaryArchive));
            File.Copy(temporaryArchive, destination.Path, overwrite: true);
            return destination.Path;
        }
        finally
        {
            if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Asset export is currently available on Windows.");
#endif
    }

    private static void CreateSelectionArchive(IEnumerable<string> paths, string archivePath)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
                continue;
            }
            var folderName = new DirectoryInfo(path).Name;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                archive.CreateEntryFromFile(file, Path.Combine(folderName, Path.GetRelativePath(path, file)).Replace('\\', '/'), CompressionLevel.Optimal);
        }
    }

    public async Task<string?> ExportAsync(string assetPath)
    {
        var isFolder = Directory.Exists(assetPath);
        if (!isFolder && !File.Exists(assetPath)) throw new FileNotFoundException("The selected asset no longer exists.", assetPath);
#if WINDOWS
        var extension = isFolder ? ".zip" : Path.GetExtension(assetPath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".bin";
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = isFolder ? new DirectoryInfo(assetPath).Name : Path.GetFileNameWithoutExtension(assetPath)
        };
        picker.FileTypeChoices.Add("Asset file", [extension]);
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (window is null) throw new InvalidOperationException("A window is required to choose an export location.");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var destination = await picker.PickSaveFileAsync();
        if (destination is null) return null;
        if (isFolder)
        {
            var temporaryArchive = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}.zip");
            try
            {
                await Task.Run(() => ZipFile.CreateFromDirectory(assetPath, temporaryArchive, CompressionLevel.Optimal, includeBaseDirectory: true));
                File.Copy(temporaryArchive, destination.Path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
            }
        }
        else
        {
            await using var input = File.OpenRead(assetPath);
            await using var output = File.Create(destination.Path);
            await input.CopyToAsync(output);
        }
        return destination.Path;
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Asset export is currently available on Windows.");
#endif
    }
}
