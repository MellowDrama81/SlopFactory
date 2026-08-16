namespace Mellow.SlopFactory.Gui.Services;

/// <summary>plan.md:40 — the distribution channel, semantic version and platform build number shown in About and diagnostics.</summary>
public interface IAppBuildInfo
{
    string Channel { get; }
    string DisplayVersion { get; }
    string BuildNumber { get; }
    string ApplicationId { get; }

    /// <summary>
    /// plan.md:45 — a static HTTPS link to the official download page, with no SlopFactory-added
    /// tracking parameters. Null until a real release pipeline supplies one via
    /// /p:SlopFactoryDownloadPageUrl=..., since no official project site exists yet.
    /// </summary>
    string? DownloadPageUrl { get; }
}
