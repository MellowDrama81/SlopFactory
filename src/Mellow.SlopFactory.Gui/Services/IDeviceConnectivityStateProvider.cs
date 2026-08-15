namespace Mellow.SlopFactory.Gui.Services;

public interface IDeviceConnectivityStateProvider
{
    /// <summary>True when the device has no usable internet access at all — the queue should pause
    /// rather than let a new submission fail against a connection that was never reachable.</summary>
    bool IsOffline { get; }
    /// <summary>True when the device's current connection is metered (cellular) — distinct from
    /// <see cref="IsOffline"/>, since a metered connection is still usable, just governed by the
    /// device-wide metered-network transfer setting rather than blocked outright.</summary>
    bool IsMetered { get; }
    event EventHandler? Changed;
}
