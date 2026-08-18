namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// The Windows notification-area icon shown while SlopFactory keeps running with active work
/// after the main window is closed. A no-op everywhere except Windows — there is
/// no tray-icon equivalent on Android; its own background-work indicator is a persistent
/// notification instead (Android Background Work).
/// </summary>
public interface ITrayIconService
{
    /// <summary>Shows the icon (or updates its tooltip if already shown) with the given
    /// aggregate-status text.</summary>
    void Show(string tooltip);

    /// <summary>Removes the icon. Safe to call even if it was never shown.</summary>
    void Hide();

    /// <summary>Raised when the user activates the icon (double-click, or "Open SlopFactory" from
    /// its context menu) — the caller should reopen/restore the main window.</summary>
    event EventHandler? OpenRequested;

    /// <summary>Raised when the user chooses "Exit" from the icon's context menu.</summary>
    event EventHandler? ExitRequested;
}
