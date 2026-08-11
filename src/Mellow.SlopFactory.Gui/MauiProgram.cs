using Mellow.SlopFactory.Gui.Services;
using Mellow.SlopFactory.Infrastructure;

namespace Mellow.SlopFactory.Gui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddLocalization();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSlopFactoryInfrastructure();
        builder.Services.AddSingleton<ILibraryLocationService, LibraryLocationService>();
        builder.Services.AddSingleton<ILibraryAvailabilityProbe, LibraryAvailabilityProbe>();
        builder.Services.AddSingleton<AppLibraryState>();
        builder.Services.AddSingleton<ManagedMediaResourceService>();
        builder.Services.AddSingleton<PreviewCacheService>();
        builder.Services.AddSingleton<IRecentLibraryService, RecentLibraryService>();
        builder.Services.AddSingleton<ISensitiveMetadataDisclosureService, SensitiveMetadataDisclosureService>();
        builder.Services.AddSingleton<ISensitiveRevealSessionService, SensitiveRevealSessionService>();
        builder.Services.AddSingleton<IncomingImportService>();
        builder.Services.AddSingleton<ManagedContentWatchService>();
        builder.Services.AddSingleton<GenerationQueueService>();
        builder.Services.AddSingleton<IntegrityScanRecommendationService>();
        builder.Services.AddSingleton<IPlatformFileActionService, PlatformFileActionService>();
        builder.Services.AddSingleton<IAppPreferenceStore, MauiAppPreferenceStore>();
        builder.Services.AddSingleton<IThemePreferenceService, ThemePreferenceService>();
        builder.Services.AddSingleton<ISecureCredentialStore, MauiSecureCredentialStore>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        return builder.Build();
    }
}
