using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Mellow.SlopFactory.Gui.WinUI;

public partial class App : MauiWinUIApplication
{
    /// <summary>
    /// plan.md:352/355 — Windows permits one running SlopFactory process per signed-in user
    /// session; launching again activates the existing process instead of starting another. Checked
    /// and blocked on synchronously before <see cref="InitializeComponent"/> so a second launch never
    /// creates a visible window before exiting — <see cref="OnLaunched"/> would otherwise be free to
    /// run against a half-constructed app if this redirected and exited asynchronously instead.
    /// </summary>
    private const string SingleInstanceKey = "Mellow.SlopFactory.SingleInstance";

    public App()
    {
        var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        if (!mainInstance.IsCurrent)
        {
            // Forwards this launch's activation (including a file-activation payload) to the
            // existing process's own Activated handler below, then exits before any window is
            // created. File activation forwarded this way already requires the normal
            // IncomingImportService confirmation step before anything is imported (plan.md:356-357)
            // — no separate confirmation gate was needed here.
            mainInstance.RedirectActivationToAsync(activatedArgs).AsTask().GetAwaiter().GetResult();
            Environment.Exit(0);
        }

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
