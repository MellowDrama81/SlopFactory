namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// Android's user-initiated/foreground background-execution mechanism for keeping active transfers
/// alive while the app isn't in the foreground (plan.md:263-272). A no-op everywhere except
/// Android, where the OS otherwise aggressively suspends background work; Windows has no equivalent
/// restriction (a Win32 process keeps running regardless of window visibility, handled instead by
/// Windows notification-area behavior).
/// </summary>
public interface IBackgroundExecutionService
{
    /// <summary>Starts (or updates the ongoing notification of) background execution — called
    /// whenever at least one job is <see cref="GenerationJobPhase.Running"/> or
    /// <see cref="GenerationJobPhase.Monitoring"/>. Safe to call repeatedly; it only actually starts
    /// the underlying service once (plan.md:266's "required ongoing notification").</summary>
    void EnsureRunning(string statusText);

    /// <summary>Stops background execution once no job needs it anymore. Safe to call even if
    /// nothing is running.</summary>
    void StopRunning();

    /// <summary>Raised when the OS revoked or timed out background execution on its own — never
    /// raised by an app-initiated <see cref="StopRunning"/> call. Lets a caller record this
    /// distinctly from a provider failure (plan.md's "Android execution suspension and timeout are
    /// recorded separately from provider failure"). Never raised on platforms with no such
    /// restriction (e.g. Windows).</summary>
    event EventHandler? Suspended;
}
