namespace Mellow.SlopFactory.Gui;

public partial class MainPage : ContentPage
{
    private readonly Services.IncomingImportService _incomingImports;
#if WINDOWS
    private Microsoft.UI.Xaml.FrameworkElement? _dropTarget;
#endif

    public MainPage(Services.ManagedMediaResourceService mediaResources, Services.IncomingImportService incomingImports)
    {
        InitializeComponent();
        _incomingImports = incomingImports;
        blazorWebView.WebResourceRequested += mediaResources.HandleWebResourceRequested;
#if WINDOWS
        blazorWebView.HandlerChanged += ConfigureWindowsDropTarget;
#endif
    }

#if WINDOWS
    private void ConfigureWindowsDropTarget(object? sender, EventArgs args)
    {
        if (_dropTarget is not null) _dropTarget.Drop -= HandleWindowsDrop;
        _dropTarget = blazorWebView.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
        if (_dropTarget is null) return;
        _dropTarget.AllowDrop = true;
        _dropTarget.Drop += HandleWindowsDrop;
    }

    private async void HandleWindowsDrop(object sender, Microsoft.UI.Xaml.DragEventArgs args)
    {
        try
        {
            if (!args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            var items = await args.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var item in items)
            {
                if (item is Windows.Storage.StorageFile file) paths.Add(file.Path);
                else if (item is Windows.Storage.StorageFolder folder) await AddFolderFilesAsync(folder, paths, 0);
            }
            _incomingImports.QueueLocalPaths(paths);
            args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _incomingImports.QueueFailure("Dropped items", "One or more dropped files could not be enumerated or read.");
        }
    }

    private static async Task AddFolderFilesAsync(Windows.Storage.StorageFolder folder, List<string> paths, int depth)
    {
        if (depth > 64 || paths.Count >= 100_000) return;
        try
        {
            if ((File.GetAttributes(folder.Path) & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0) return;
            foreach (var item in await folder.GetItemsAsync())
            {
                if (paths.Count >= 100_000) break;
                if (item is Windows.Storage.StorageFile file)
                {
                    if ((File.GetAttributes(file.Path) & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) == 0) paths.Add(file.Path);
                }
                else if (item is Windows.Storage.StorageFolder child) await AddFolderFilesAsync(child, paths, depth + 1);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
#endif
}
