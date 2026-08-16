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
        builder.Services.AddSingleton<IRecoveryStagingPathProvider, MauiRecoveryStagingPathProvider>();
        builder.Services.AddSingleton<IPendingResultRegistryService, PendingResultRegistryService>();
        builder.Services.AddSingleton<IRecoveryStagingService, RecoveryStagingService>();
        builder.Services.AddSingleton<GenerationQueueService>();
        builder.Services.AddSingleton<IntegrityScanRecommendationService>();
        builder.Services.AddSingleton<IPlatformFileActionService, PlatformFileActionService>();
        builder.Services.AddSingleton<IAppPreferenceStore, MauiAppPreferenceStore>();
        builder.Services.AddSingleton<IThemePreferenceService, ThemePreferenceService>();
        builder.Services.AddSingleton<ISecureCredentialStore, MauiSecureCredentialStore>();
        builder.Services.AddSingleton<CredentialReconciliationService>();
        builder.Services.AddSingleton<IDeviceEnergyStateProvider, MauiDeviceEnergyStateProvider>();
        builder.Services.AddSingleton<IDeviceConnectivityStateProvider, MauiDeviceConnectivityStateProvider>();
        builder.Services.AddSingleton<AppLifecycleState>();
        builder.Services.AddSingleton<IAppLifecycleState>(services => services.GetRequiredService<AppLifecycleState>());
        builder.Services.AddSingleton<INotificationService, MauiNotificationService>();
        builder.Services.AddSingleton<GenerationNotificationCoordinator>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        return builder.Build();
    }
}
