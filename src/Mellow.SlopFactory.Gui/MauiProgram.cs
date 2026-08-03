using Mellow.SlopFactory.Gui.Services;
using Mellow.SlopFactory.Infrastructure;

namespace Mellow.SlopFactory.Gui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSlopFactoryInfrastructure();
        builder.Services.AddSingleton<ILibraryLocationService, LibraryLocationService>();
        builder.Services.AddSingleton<AppLibraryState>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        return builder.Build();
    }
}
