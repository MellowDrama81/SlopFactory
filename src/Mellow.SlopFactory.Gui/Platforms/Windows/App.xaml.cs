using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Mellow.SlopFactory.Gui.Services;

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
        HookWindowClosing();
    }

    private static void OnAppActivated(object? sender, AppActivationArguments args) => QueueActivatedFiles(args);

    private static void QueueActivatedFiles(AppActivationArguments args)
    {
        if (args.Kind != ExtendedActivationKind.File || args.Data is not FileActivatedEventArgs fileActivation) return;
        var service = IPlatformApplication.Current?.Services.GetService<Mellow.SlopFactory.Gui.Services.IncomingImportService>();
        service?.QueueLocalPaths(fileActivation.Files.OfType<Windows.Storage.StorageFile>().Select(file => file.Path));
    }

    /// <summary>
    /// plan.md:440-448 — with active local work, closing the main window offers **Keep Running**/
    /// **Cancel Work and Exit**/**Return to App** instead of exiting immediately. Uses the native
    /// WinUI <c>AppWindow.Closing</c> event (cancelable) rather than MAUI's cross-platform
    /// <c>Window.Destroying</c> (used elsewhere in this app only for best-effort, non-blocking
    /// cleanup — it cannot actually stop a close from proceeding), since only the native event can
    /// genuinely gate whether the window closes.
    /// </summary>
    private static void HookWindowClosing()
    {
        var services = IPlatformApplication.Current?.Services;
        var coordinator = services?.GetService<IWindowsExitCoordinator>();
        var trayIcon = services?.GetService<ITrayIconService>();
        var queue = services?.GetService<GenerationQueueService>();
        if (coordinator is null || trayIcon is null || queue is null) return;

        var windows = Microsoft.Maui.Controls.Application.Current?.Windows;
        var window = windows is { Count: > 0 } ? windows[0].Handler?.PlatformView as Microsoft.UI.Xaml.Window : null;
        if (window is null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        if (appWindow is null) return;

        appWindow.Closing += (_, closingArgs) =>
        {
            // plan.md:434 — with no active work, closing exits normally; only active work gates it.
            if (queue.RunningCount == 0 && queue.QueuedCount == 0) return;
            closingArgs.Cancel = true;
            if (coordinator.RememberedKeepRunning) { coordinator.KeepRunning(remember: true); return; }
            coordinator.RequestDecision();
        };
        coordinator.KeptRunning += (_, _) =>
        {
            // plan.md:441 — places SlopFactory in the notification area; the tray icon itself is
            // shown by whoever handles KeptRunning at the Blazor layer (MainLayout.razor), since
            // that is also where the aggregate-status tooltip text is known.
            appWindow.Hide();
        };
        trayIcon.OpenRequested += (_, _) =>
        {
            appWindow.Show();
            window.Activate();
        };
        trayIcon.ExitRequested += async (_, _) => await coordinator.CancelWorkAndExitAsync();
        coordinator.ExitConfirmed += (_, _) => Environment.Exit(0);
    }
}
