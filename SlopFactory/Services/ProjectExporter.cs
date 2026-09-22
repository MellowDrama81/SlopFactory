using System.IO.Compression;

namespace SlopFactory.Services;

public sealed class ProjectExporter
{
    public async Task<string?> ExportAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath)) throw new DirectoryNotFoundException("The project folder no longer exists.");
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = new DirectoryInfo(folderPath).Name
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
            await Task.Run(() => ZipFile.CreateFromDirectory(folderPath, temporaryArchive, CompressionLevel.Optimal, includeBaseDirectory: true));
            File.Copy(temporaryArchive, destination.Path, overwrite: true);
            return destination.Path;
        }
        finally
        {
            if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Project export is currently available on Windows.");
#endif
    }
}
