namespace SlopFactory;
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<Services.ComfyConnectionSettings>();
        builder.Services.AddSingleton<Services.ComfyConnectionTester>();
        builder.Services.AddSingleton<Services.WorkflowLibrary>();
        builder.Services.AddSingleton<Services.ProjectLibrary>();
        builder.Services.AddSingleton<Services.ProjectFolderPicker>();
        builder.Services.AddSingleton<Services.ProjectExporter>();
        builder.Services.AddSingleton<Services.AssetExporter>();
        builder.Services.AddSingleton<Services.WorkspaceLayoutStore>();
        builder.Services.AddSingleton<Services.WorkflowTemplateService>();
        builder.Services.AddSingleton<Services.ComfyGenerationService>();
        builder.Services.AddSingleton<Services.ComfyAssetReferenceStore>();
        builder.Services.AddSingleton<Services.AssetMaskStore>();
        builder.Services.AddSingleton<Services.PanelStore>();
        builder.Services.AddSingleton<Services.PagesStore>();
        builder.Services.AddSingleton<Services.PanelImageExporter>();
        builder.Services.AddSingleton<Services.PagePdfExporter>();
        builder.Services.AddSingleton<Services.ProjectImageImporter>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        return builder.Build();
    }
}
