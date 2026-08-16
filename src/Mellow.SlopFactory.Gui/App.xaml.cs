using Mellow.SlopFactory.Gui.Services;

namespace Mellow.SlopFactory.Gui;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly ManagedMediaResourceService _mediaResources;
    private readonly IncomingImportService _incomingImports;
    private readonly ISensitiveRevealSessionService _sensitiveReveals;
    private readonly AppLifecycleState _lifecycle;
    private readonly AppLibraryState _libraryState;
    private readonly IDiagnosticsLogger _diagnostics;

    public App(ManagedMediaResourceService mediaResources, IncomingImportService incomingImports, ISensitiveRevealSessionService sensitiveReveals, AppLifecycleState lifecycle, AppLibraryState libraryState, IDiagnosticsLogger diagnostics)
    {
        InitializeComponent();
        _mediaResources = mediaResources;
        _incomingImports = incomingImports;
        _sensitiveReveals = sensitiveReveals;
        _lifecycle = lifecycle;
        _libraryState = libraryState;
        _diagnostics = diagnostics;
        _diagnostics.MarkSessionStarted();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage(_mediaResources, _incomingImports)) { Title = "SlopFactory" };
        window.Stopped += (_, _) => _sensitiveReveals.Clear();
        window.Destroying += (_, _) => _sensitiveReveals.Clear();
        window.Activated += (_, _) => _lifecycle.SetForeground(true);
        window.Deactivated += (_, _) => _lifecycle.SetForeground(false);
        window.Resumed += (_, _) => _lifecycle.SetForeground(true);
        window.Stopped += (_, _) => _lifecycle.SetForeground(false);
        window.Stopped += (_, _) => _ = _libraryState.FlushForSuspensionAsync();
        window.Destroying += (_, _) => _ = _libraryState.FlushForSuspensionAsync();
        // plan.md:184-185's crash detection relies on this NOT having run last time — Destroying is
        // the closest existing signal to "the app is genuinely closing," the same one
        // FlushForSuspensionAsync above already treats as the graceful-shutdown point.
        window.Destroying += (_, _) => _diagnostics.MarkSessionEndedNormally();
        return window;
    }
}
