namespace Mellow.SlopFactory.Gui.Services;

public interface IDeviceEnergyStateProvider
{
    bool IsEnergySaverOn { get; }
    event EventHandler? Changed;
}
