using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Mellow.SlopFactory.Gui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
        AppInstance.GetCurrent().Activated += OnAppActivated;
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        QueueActivatedFiles(AppInstance.GetCurrent().GetActivatedEventArgs());
    }

    private static void OnAppActivated(object? sender, AppActivationArguments args) => QueueActivatedFiles(args);

    private static void QueueActivatedFiles(AppActivationArguments args)
    {
        if (args.Kind != ExtendedActivationKind.File || args.Data is not FileActivatedEventArgs fileActivation) return;
        var service = IPlatformApplication.Current?.Services.GetService<Mellow.SlopFactory.Gui.Services.IncomingImportService>();
        service?.QueueLocalPaths(fileActivation.Files.OfType<Windows.Storage.StorageFile>().Select(file => file.Path));
    }
}
