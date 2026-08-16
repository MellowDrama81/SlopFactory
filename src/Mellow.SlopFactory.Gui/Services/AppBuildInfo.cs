using System.Reflection;

namespace Mellow.SlopFactory.Gui.Services;

internal sealed class AppBuildInfo : IAppBuildInfo
{
    public string Channel { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "SlopFactoryChannel")?.Value is { Length: > 0 } channel
            ? channel
            : "Development";

    public string DisplayVersion => Microsoft.Maui.ApplicationModel.AppInfo.Current.VersionString;

    public string BuildNumber => Microsoft.Maui.ApplicationModel.AppInfo.Current.BuildString;

    public string ApplicationId => Microsoft.Maui.ApplicationModel.AppInfo.Current.PackageName;

    public string? DownloadPageUrl { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "SlopFactoryDownloadPageUrl")?.Value is { Length: > 0 } url
            ? url
            : null;
}
