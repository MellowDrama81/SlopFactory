namespace Mellow.SlopFactory.Gui.Services;

/// <summary>No-op <see cref="IBackgroundExecutionService"/> for platforms with no background-execution
/// restriction to work around (Windows). Registered instead of the real Android service there.</summary>
internal sealed class NullBackgroundExecutionService : IBackgroundExecutionService
{
    public void EnsureRunning(string statusText) { }
    public void StopRunning() { }
    // Never raised: Windows has no OS-level background-execution restriction to be suspended from.
    public event EventHandler? Suspended { add { } remove { } }
}
