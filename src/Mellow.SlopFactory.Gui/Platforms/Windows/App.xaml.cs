namespace Mellow.SlopFactory.Gui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnFileActivated(Windows.ApplicationModel.Activation.FileActivatedEventArgs args)
    {
        base.OnFileActivated(args);
        var service = IPlatformApplication.Current?.Services.GetService<Mellow.SlopFactory.Gui.Services.IncomingImportService>();
        service?.QueueLocalPaths(args.Files.OfType<Windows.Storage.StorageFile>().Select(file => file.Path));
    }
}
