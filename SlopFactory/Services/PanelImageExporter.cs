namespace SlopFactory.Services;

public sealed class PanelImageExporter
{
    public async Task<string?> ExportAsync(string name, string format, string dataUrl)
    {
        var extension = format.ToLowerInvariant() switch
        {
            "jpeg" => ".jpg",
            "webp" => ".webp",
            _ => ".png"
        };
        var marker = ";base64,";
        var index = dataUrl.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException("The panel image could not be encoded for export.");
        var bytes = Convert.FromBase64String(dataUrl[(index + marker.Length)..]);
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            SuggestedFileName = string.IsNullOrWhiteSpace(name) ? "panel" : string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch))
        };
        picker.FileTypeChoices.Add($"{format.ToUpperInvariant()} image", [extension]);
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (window is null) throw new InvalidOperationException("A window is required to choose an export location.");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var destination = await picker.PickSaveFileAsync();
        if (destination is null) return null;
        await File.WriteAllBytesAsync(destination.Path, bytes);
        return destination.Path;
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Panel image export is currently available on Windows.");
#endif
    }
}
