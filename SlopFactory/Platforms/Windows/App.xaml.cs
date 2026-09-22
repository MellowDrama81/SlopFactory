using System.Diagnostics;

namespace SlopFactory.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        // Register before generated initialization so this diagnostic is written
        // before MAUI's DEBUG-only break-on-unhandled-exception handler runs.
        UnhandledException += OnUnhandledException;
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Debug.WriteLine("=== SlopFactory WinUI unhandled exception ===");
        Debug.WriteLine(e.Exception.ToString());
        Debug.WriteLine($"HRESULT: 0x{e.Exception.HResult:X8}");
        Debug.WriteLine("======================================================");
    }
}
