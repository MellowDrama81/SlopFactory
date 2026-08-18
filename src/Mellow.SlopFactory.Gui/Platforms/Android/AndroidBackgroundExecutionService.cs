using Android.Content;
using AndroidX.Core.Content;

namespace Mellow.SlopFactory.Gui.Services;

internal sealed class AndroidBackgroundExecutionService : IBackgroundExecutionService
{
    private bool _running;

    public AndroidBackgroundExecutionService()
    {
        GenerationForegroundService.SuspendedByOperatingSystem += (_, _) => Suspended?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Suspended;

    public void EnsureRunning(string statusText)
    {
        var context = global::Android.App.Application.Context;
        if (context is null) return;
        // The notification permission is requested only when background transfer
        // behavior is first actually needed, not proactively at app launch.
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && ContextCompat.CheckSelfPermission(context, global::Android.Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
        {
            _ = MainActivity.Current?.RequestNotificationPermissionAsync();
        }
        GenerationForegroundService.Start(context, statusText);
        _running = true;
    }

    public void StopRunning()
    {
        if (!_running) return;
        var context = global::Android.App.Application.Context;
        if (context is not null) GenerationForegroundService.Stop(context);
        _running = false;
    }
}
