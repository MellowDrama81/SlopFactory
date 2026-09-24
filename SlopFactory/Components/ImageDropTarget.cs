using Microsoft.JSInterop;
using SlopFactory.Services;

namespace SlopFactory.Components;

// Receives images dropped on an element marked data-image-drop="@Id" (wired up in pane-docking.js):
// assets dragged from an Assets pane, or image files dragged in from the desktop.
public sealed class ImageDropTarget(IJSRuntime js, ProjectImageImporter importer, Func<string?> projectFolder, Func<string, Task> onImage) : IAsyncDisposable
{
    private DotNetObjectReference<ImageDropTarget>? reference;
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public async Task RegisterAsync()
    {
        if (reference is not null) return;
        reference = DotNetObjectReference.Create(this);
        await js.InvokeVoidAsync("recursiveDock.registerImageDrop", Id, reference);
    }

    [JSInvokable]
    public Task<string> DropAsset(string sourcePath, string sourceRoot) =>
        UseAsync(folder => importer.UseAssetAsync(folder, sourcePath, sourceRoot));

    [JSInvokable]
    public async Task<string> DropFile(string fileName, IJSStreamReference file)
    {
        try
        {
            if (file.Length > ProjectImageImporter.MaxFileSize) return Result(false, "The image exceeds the 1 GB file limit.");
            return await UseAsync(async folder =>
            {
                await using var stream = await file.OpenReadStreamAsync(maxAllowedSize: ProjectImageImporter.MaxFileSize);
                return await importer.ImportAsync(folder, fileName, stream);
            });
        }
        finally { await file.DisposeAsync(); }
    }

    private async Task<string> UseAsync(Func<string, Task<string>> resolve)
    {
        var folder = projectFolder();
        if (string.IsNullOrWhiteSpace(folder)) return Result(false, "Select a project before dropping an image.");
        string relativePath;
        try { relativePath = await resolve(folder); }
        catch (Exception ex) { return Result(false, ex.Message); }
        try { await onImage(relativePath); }
        catch (Exception ex) { return Result(false, $"Image is in assets, but could not be used: {ex.Message}"); }
        return Result(true, string.Empty);
    }

    private static string Result(bool success, string message) =>
        System.Text.Json.JsonSerializer.Serialize(new { success, message });

    public async ValueTask DisposeAsync()
    {
        if (reference is null) return;
        try { await js.InvokeVoidAsync("recursiveDock.unregisterImageDrop", Id); }
        catch (JSDisconnectedException) { /* The WebView is closing. */ }
        catch (TaskCanceledException) { /* The WebView is closing. */ }
        reference.Dispose();
        reference = null;
    }
}
