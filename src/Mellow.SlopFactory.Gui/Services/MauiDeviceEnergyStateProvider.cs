namespace Mellow.SlopFactory.Gui.Services;

internal sealed class MauiDeviceEnergyStateProvider : IDeviceEnergyStateProvider
{
    public MauiDeviceEnergyStateProvider()
    {
        Battery.Default.EnergySaverStatusChanged += OnEnergySaverStatusChanged;
    }

    public bool IsEnergySaverOn => Battery.Default.EnergySaverStatus == EnergySaverStatus.On;

    public event EventHandler? Changed;

    private void OnEnergySaverStatusChanged(object? sender, EnergySaverStatusChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
}
