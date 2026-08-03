using Mellow.SlopFactory.Gui.Services;

namespace Mellow.SlopFactory.Gui;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly ManagedMediaResourceService _mediaResources;

    public App(ManagedMediaResourceService mediaResources)
    {
        InitializeComponent();
        _mediaResources = mediaResources;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new MainPage(_mediaResources))
    {
        Title = "SlopFactory"
    };
}
