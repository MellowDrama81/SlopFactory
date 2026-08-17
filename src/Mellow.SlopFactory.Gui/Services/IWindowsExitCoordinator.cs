namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// Coordinates the Windows **Keep Running** / **Cancel Work and Exit** / **Return to App** decision
/// (plan.md:440-448) between the native window-close interception (Windows-only,
/// `Platforms/Windows/App.xaml.cs`) and the Blazor-rendered confirmation dialog
/// (`MainLayout.razor`). Registered on every platform (harmless — nothing on Android ever calls
/// <see cref="RequestDecision"/>, since Android has no equivalent window-close gate).
/// </summary>
public interface IWindowsExitCoordinator
{
    /// <summary>True while the confirmation dialog should be showing.</summary>
    bool PendingDecision { get; }

    /// <summary>Whether the user has previously chosen to remember **Keep Running**
    /// (plan.md:447) — read by the native close handler to skip the dialog entirely next time.</summary>
    bool RememberedKeepRunning { get; }

    void SetRememberedKeepRunning(bool value);

    /// <summary>Called by the native close handler once it has cancelled the close and there is no
    /// remembered choice to apply automatically.</summary>
    void RequestDecision();

    /// <summary>plan.md:441-442 — keeps the process and window state; the window itself is hidden
    /// by the native handler listening for <see cref="KeptRunning"/>, not by this call.</summary>
    void KeepRunning(bool remember);

    /// <summary>Flushes draft edits, cancels every active job, then raises <see cref="ExitConfirmed"/>
    /// so the native handler can perform the real process exit.</summary>
    Task CancelWorkAndExitAsync();

    /// <summary>plan.md:439 (semantics reused for the active-work dialog) — leaves everything
    /// unchanged; the window was already prevented from closing.</summary>
    void ReturnToApp();

    event EventHandler? Changed;
    event EventHandler? KeptRunning;
    event EventHandler? ExitConfirmed;
}

public sealed class WindowsExitCoordinator(GenerationQueueService queue, IAppPreferenceStore preferences, AppLibraryState? libraryState = null) : IWindowsExitCoordinator
{
    private const string RememberedKeepRunningKey = "slopfactory.windows.keeprunningremembered";

    public bool PendingDecision { get; private set; }

    public bool RememberedKeepRunning => preferences.ReadString(RememberedKeepRunningKey, bool.FalseString) == bool.TrueString;

    public void SetRememberedKeepRunning(bool value)
    {
        preferences.WriteString(RememberedKeepRunningKey, value ? bool.TrueString : bool.FalseString);
    }

    public void RequestDecision()
    {
        PendingDecision = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void KeepRunning(bool remember)
    {
        PendingDecision = false;
        if (remember) SetRememberedKeepRunning(true);
        KeptRunning?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CancelWorkAndExitAsync()
    {
        PendingDecision = false;
        if (libraryState is not null)
        {
            try { await libraryState.FlushForSuspensionAsync().ConfigureAwait(false); }
            // Process exit must remain available even if a best-effort draft flush fails. The normal
            // dirty-draft recovery marker preserves the user's ability to review unsaved work later.
            catch { }
        }
        foreach (var entry in queue.GetSnapshot()) queue.Cancel(entry.JobId);
        ExitConfirmed?.Invoke(this, EventArgs.Empty);
    }

    public void ReturnToApp()
    {
        PendingDecision = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;
    public event EventHandler? KeptRunning;
    public event EventHandler? ExitConfirmed;
}
