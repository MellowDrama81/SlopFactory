using Mellow.SlopFactory.Gui.Services;

namespace Mellow.SlopFactory.Gui;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly ManagedMediaResourceService _mediaResources;
    private readonly IncomingImportService _incomingImports;

    public App(ManagedMediaResourceService mediaResources, IncomingImportService incomingImports)
    {
        InitializeComponent();
        _mediaResources = mediaResources;
        _incomingImports = incomingImports;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new MainPage(_mediaResources, _incomingImports))
    {
        Title = "SlopFactory"
    };
}
