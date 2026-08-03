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
        builder.Services.AddSingleton<ManagedMediaResourceService>();
        builder.Services.AddSingleton<PreviewCacheService>();
        builder.Services.AddSingleton<IRecentLibraryService, RecentLibraryService>();
        builder.Services.AddSingleton<IncomingImportService>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        return builder.Build();
    }
}
