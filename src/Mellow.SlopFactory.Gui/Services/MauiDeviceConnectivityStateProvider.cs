namespace Mellow.SlopFactory.Gui.Services;

internal sealed class MauiDeviceConnectivityStateProvider : IDeviceConnectivityStateProvider
{
    public MauiDeviceConnectivityStateProvider()
    {
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
    }

    public bool IsOffline => Connectivity.Current.NetworkAccess != NetworkAccess.Internet;

    public bool IsMetered => Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.Cellular);

    public event EventHandler? Changed;

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
}
