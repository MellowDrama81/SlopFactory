using Mellow.SlopFactory.Gui.Services;

namespace Mellow.SlopFactory.Gui;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly ManagedMediaResourceService _mediaResources;
    private readonly IncomingImportService _incomingImports;
    private readonly ISensitiveRevealSessionService _sensitiveReveals;
    private readonly AppLifecycleState _lifecycle;

    public App(ManagedMediaResourceService mediaResources, IncomingImportService incomingImports, ISensitiveRevealSessionService sensitiveReveals, AppLifecycleState lifecycle)
    {
        InitializeComponent();
        _mediaResources = mediaResources;
        _incomingImports = incomingImports;
        _sensitiveReveals = sensitiveReveals;
        _lifecycle = lifecycle;
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
        return window;
    }
}
