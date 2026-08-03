namespace Mellow.SlopFactory.Gui;

public partial class MainPage : ContentPage
{
    public MainPage(Services.ManagedMediaResourceService mediaResources)
    {
        InitializeComponent();
        blazorWebView.WebResourceRequested += mediaResources.HandleWebResourceRequested;
    }
}
