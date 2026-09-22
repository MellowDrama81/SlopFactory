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
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        return builder.Build();
    }
}
