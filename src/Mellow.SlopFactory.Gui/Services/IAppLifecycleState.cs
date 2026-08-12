namespace Mellow.SlopFactory.Gui.Services;

public interface IAppLifecycleState
{
    bool IsForeground { get; }
    event EventHandler? Changed;
}

public sealed class AppLifecycleState : IAppLifecycleState
{
    public bool IsForeground { get; private set; } = true;

    public event EventHandler? Changed;

    public void SetForeground(bool value)
    {
        if (IsForeground == value) return;
        IsForeground = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
