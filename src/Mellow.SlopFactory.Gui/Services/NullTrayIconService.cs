namespace Mellow.SlopFactory.Gui.Services;

/// <summary>No-op <see cref="ITrayIconService"/> for platforms without a notification-area
/// concept (Android). Registered instead of <c>WindowsTrayIconService</c> on non-Windows targets.</summary>
internal sealed class NullTrayIconService : ITrayIconService
{
    public void Show(string tooltip) { }
    public void Hide() { }
    public event EventHandler? OpenRequested { add { } remove { } }
    public event EventHandler? ExitRequested { add { } remove { } }
}
