using Mellow.SlopFactory.Gui.Services;

namespace Mellow.SlopFactory.Gui;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly ManagedMediaResourceService _mediaResources;
    private readonly IncomingImportService _incomingImports;
    private readonly ISensitiveRevealSessionService _sensitiveReveals;

    public App(ManagedMediaResourceService mediaResources, IncomingImportService incomingImports, ISensitiveRevealSessionService sensitiveReveals)
    {
        InitializeComponent();
        _mediaResources = mediaResources;
        _incomingImports = incomingImports;
        _sensitiveReveals = sensitiveReveals;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage(_mediaResources, _incomingImports)) { Title = "SlopFactory" };
        window.Stopped += (_, _) => _sensitiveReveals.Clear();
        window.Destroying += (_, _) => _sensitiveReveals.Clear();
        return window;
    }
}
