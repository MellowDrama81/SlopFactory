using System.Security.Cryptography;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Gui.Services;

public interface IPlatformFileActionService
{
    bool HasPermissionBlock { get; }
    Task PickRecursiveImportFolderAsync(bool includeHiddenFiles, CancellationToken cancellationToken = default);
    /// <summary><paramref name="sidecarOptions"/> is honored only on the Windows local-filesystem
    /// export path; on Android (SAF), <c>Sidecar</c> in the result is always null regardless of what
    /// was requested — a SAF sidecar would need its own separate document write with its own
    /// permission/verification handling, deferred (see docs/developer/architecture.md, "External
    /// export cleanup journal and sidecars").</summary>
    Task<(FileExportResult Media, SidecarExportResult? Sidecar)> ExportAsync(ILibraryWorkspace workspace, FileRecord file, bool changedBytes, ExportSidecarOptions? sidecarOptions = null, CancellationToken cancellationToken = default);
    /// <summary>Writes arbitrary bytes not owned by any library to a user-chosen destination — used
    /// by recovery staging's **Export Copy**, which has no <see cref="FileRecord"/> or
    /// <see cref="ILibraryWorkspace"/> to export from. Returns true if the user completed the save,
    /// false if they cancelled the picker.</summary>
    Task<bool> ExportRawBytesAsync(byte[] bytes, string suggestedFileName, string mediaType, CancellationToken cancellationToken = default);
    Task OpenExternallyAsync(ILibraryWorkspace workspace, FileRecord file, CancellationToken cancellationToken = default);
    void OpenApplicationSettings();
}

#pragma warning disable CS9113 // exportCleanupJournal is used only in the #elif ANDROID branch below; unread on other target frameworks.
public sealed class PlatformFileActionService(IncomingImportService incoming, IExportCleanupJournal exportCleanupJournal) : IPlatformFileActionService
#pragma warning restore CS9113
{
    public bool HasPermissionBlock { get; private set; }
    public async Task PickRecursiveImportFolderAsync(bool includeHiddenFiles, CancellationToken cancellationToken = default)
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWindowsPicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var additions = new List<IncomingImportItem>();
        AddWindowsFolder(folder.Path, folder.Name, includeHiddenFiles, additions, 0, cancellationToken);
        incoming.QueueLocalItems(additions);
#elif ANDROID
        try
        {
            var activity = MainActivity.Current ?? throw new InvalidOperationException("The Android activity is unavailable.");
            var tree = await activity.PickDocumentTreeAsync(cancellationToken).ConfigureAwait(false);
            if (tree is null) return;
            await StageAndroidTreeAsync(activity, tree, includeHiddenFiles, cancellationToken).ConfigureAwait(false);
            HasPermissionBlock = false;
        }
        catch (Java.Lang.SecurityException)
        {
            HasPermissionBlock = true;
            incoming.QueueFailure("Selected folder", "The document provider denied access. Choose another folder or open system settings if access was permanently denied.");
        }
#else
        await Task.CompletedTask;
#endif
    }

    public async Task<(FileExportResult Media, SidecarExportResult? Sidecar)> ExportAsync(ILibraryWorkspace workspace, FileRecord file, bool changedBytes, ExportSidecarOptions? sidecarOptions = null, CancellationToken cancellationToken = default)
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(file.DisplayName) };
        var extension = Path.GetExtension(file.DisplayName);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".bin";
        picker.FileTypeChoices.Add("Detected file", [extension]);
        InitializeWindowsPicker(picker);
        var destination = await picker.PickSaveFileAsync();
        if (destination is null) return (new FileExportResult(file.Id, string.Empty, FileExportOutcome.Cancelled, 0, null, null), null);
        if (changedBytes)
        {
            return (await workspace.ExportChangedBytesAsync(file.Id, destination.Path, ExportCollisionChoice.Replace, cancellationToken: cancellationToken), null);
        }
        return await workspace.ExportFileWithSidecarAsync(file.Id, destination.Path, sidecarOptions ?? ExportSidecarOptions.Default, ExportCollisionChoice.Replace, cancellationToken: cancellationToken);
#elif ANDROID
        var activity = MainActivity.Current ?? throw new InvalidOperationException("The Android activity is unavailable.");
        var uri = await activity.CreateDocumentAsync(file.DisplayName, file.MediaType, cancellationToken).ConfigureAwait(false);
        if (uri is null) return (new FileExportResult(file.Id, string.Empty, FileExportOutcome.Cancelled, 0, null, null), null);
        var temporary = Path.Combine(FileSystem.CacheDirectory, $"export-{Guid.NewGuid():N}-{Path.GetFileName(file.DisplayName)}");
        // This local cache file (distinct from ExportFileAsync's own internal temp sibling, which
        // LibraryWorkspace already journals itself) is what could be orphaned if the app crashes
        // after the workspace export below succeeds but before this method's own finally block runs.
        var operationId = await exportCleanupJournal.RecordPlannedAsync(ExportCleanupObjectType.LocalTempFile, FileSystem.CacheDirectory, Path.GetFileName(temporary), Path.GetFullPath(temporary), cancellationToken).ConfigureAwait(false);
        try
        {
            var staged = changedBytes
                ? await workspace.ExportChangedBytesAsync(file.Id, temporary, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await workspace.ExportFileAsync(file.Id, temporary, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (staged.Outcome != FileExportOutcome.Exported) return (staged, null);
            await exportCleanupJournal.ConfirmAsync(operationId, cancellationToken).ConfigureAwait(false);
            await using (var source = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = activity.ContentResolver?.OpenOutputStream(uri, "wt") ?? throw new IOException("The selected document destination could not be opened."))
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await using var verify = activity.ContentResolver?.OpenInputStream(uri) ?? throw new IOException("The exported document could not be reopened for verification.");
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(verify, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, staged.ContentHash, StringComparison.Ordinal)) throw new IOException("The exported document failed byte-for-byte verification.");
            HasPermissionBlock = false;
            // Sidecar support does not extend to the Android SAF destination — see this interface's
            // own remarks. sidecarOptions is intentionally not consulted here.
            return (staged with { DestinationPath = "Android document provider" }, null);
        }
        catch (Java.Lang.SecurityException)
        {
            try { activity.ContentResolver?.Delete(uri, null, null); } catch (Exception exception) when (exception is Java.Lang.SecurityException or InvalidOperationException) { }
            HasPermissionBlock = true;
            return (new FileExportResult(file.Id, string.Empty, FileExportOutcome.Failed, 0, null, "The document provider denied access. Choose another destination or open system settings if access was permanently denied."), null);
        }
        catch
        {
            try { activity.ContentResolver?.Delete(uri, null, null); } catch (Exception exception) when (exception is Java.Lang.SecurityException or InvalidOperationException) { }
            throw;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            if (!File.Exists(temporary)) await exportCleanupJournal.RemoveAsync(operationId, CancellationToken.None).ConfigureAwait(false);
        }
#else
        return (await Task.FromResult(new FileExportResult(file.Id, string.Empty, FileExportOutcome.Failed, 0, null, "Export is unavailable on this platform.")), null);
#endif
    }

    public async Task<bool> ExportRawBytesAsync(byte[] bytes, string suggestedFileName, string mediaType, CancellationToken cancellationToken = default)
    {
#if WINDOWS
        var extension = Path.GetExtension(suggestedFileName);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".bin";
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName) };
        picker.FileTypeChoices.Add("Recovered file", [extension]);
        InitializeWindowsPicker(picker);
        var destination = await picker.PickSaveFileAsync();
        if (destination is null) return false;
        await File.WriteAllBytesAsync(destination.Path, bytes, cancellationToken).ConfigureAwait(false);
        return true;
#elif ANDROID
        var activity = MainActivity.Current ?? throw new InvalidOperationException("The Android activity is unavailable.");
        var uri = await activity.CreateDocumentAsync(suggestedFileName, mediaType, cancellationToken).ConfigureAwait(false);
        if (uri is null) return false;
        try
        {
            await using var destination = activity.ContentResolver?.OpenOutputStream(uri, "wt") ?? throw new IOException("The selected document destination could not be opened.");
            await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            HasPermissionBlock = false;
            return true;
        }
        catch (Java.Lang.SecurityException)
        {
            try { activity.ContentResolver?.Delete(uri, null, null); } catch (Exception exception) when (exception is Java.Lang.SecurityException or InvalidOperationException) { }
            HasPermissionBlock = true;
            throw new IOException("The document provider denied access. Choose another destination or open system settings if access was permanently denied.");
        }
#else
        return await Task.FromResult(false);
#endif
    }

    public async Task OpenExternallyAsync(ILibraryWorkspace workspace, FileRecord file, CancellationToken cancellationToken = default)
    {
        var copyRoot = Path.Combine(FileSystem.CacheDirectory, "SlopFactory", "ExternalOpen", workspace.Descriptor.LibraryId);
        var copy = await workspace.CreateExternalOpenCopyAsync(file.Id, copyRoot, cancellationToken).ConfigureAwait(false);
        await Launcher.Default.OpenAsync(new OpenFileRequest("Open read-only SlopFactory copy", new ReadOnlyFile(copy.Path, copy.MediaType))).ConfigureAwait(false);
    }

    public void OpenApplicationSettings() => AppInfo.Current.ShowSettingsUI();

#if WINDOWS
    private static void InitializeWindowsPicker(object picker)
    {
        var windows = Microsoft.Maui.Controls.Application.Current?.Windows;
        var window = windows is { Count: > 0 } ? windows[0].Handler?.PlatformView as Microsoft.UI.Xaml.Window : null;
        if (window is null) throw new InvalidOperationException("The application window is unavailable.");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
    }

    private static void AddWindowsFolder(string path, string relative, bool includeHidden, List<IncomingImportItem> additions, int depth, CancellationToken cancellationToken)
    {
        if (depth > 64 || additions.Count >= 100_000) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(path).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0 || !includeHidden && (attributes & FileAttributes.Hidden) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) AddWindowsFolder(entry, Path.Combine(relative, Path.GetFileName(entry)), includeHidden, additions, depth + 1, cancellationToken);
                else
                {
                    var info = new FileInfo(entry);
                    additions.Add(new IncomingImportItem(info.FullName, info.Name, info.Length, false, relative));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }
#endif

#if ANDROID
    private async Task StageAndroidTreeAsync(MainActivity activity, Android.Net.Uri tree, bool includeHidden, CancellationToken cancellationToken)
    {
        var resolver = activity.ContentResolver ?? throw new IOException("The Android document provider is unavailable.");
        var rootId = Android.Provider.DocumentsContract.GetTreeDocumentId(tree) ?? throw new IOException("The selected document tree has no stable document ID.");
        var rootName = "Imported folder";
        var pending = new Stack<(string Id, string Relative, int Depth)>();
        pending.Push((rootId, rootName, 0));
        var count = 0;
        while (pending.Count > 0 && count < 100_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (current.Depth > 64) continue;
            var children = Android.Provider.DocumentsContract.BuildChildDocumentsUriUsingTree(tree, current.Id);
            if (children is null) continue;
            using var cursor = resolver.Query(children, [Android.Provider.DocumentsContract.Document.ColumnDocumentId, Android.Provider.DocumentsContract.Document.ColumnDisplayName, Android.Provider.DocumentsContract.Document.ColumnMimeType], null, null, null);
            if (cursor is null) continue;
            while (cursor.MoveToNext() && count < 100_000)
            {
                var id = cursor.GetString(0);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = cursor.GetString(1) ?? "unnamed";
                var type = cursor.GetString(2);
                if (!includeHidden && name.Length > 0 && name[0] == '.') continue;
                if (type == Android.Provider.DocumentsContract.Document.MimeTypeDir) { pending.Push((id, Path.Combine(current.Relative, name), current.Depth + 1)); continue; }
                var document = Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(tree, id);
                if (document is null) continue;
                await using var source = resolver.OpenInputStream(document);
                if (source is null) continue;
                await incoming.StageAndQueueAsync(source, name, current.Relative, cancellationToken).ConfigureAwait(false);
                count++;
            }
        }
    }
#endif
}
