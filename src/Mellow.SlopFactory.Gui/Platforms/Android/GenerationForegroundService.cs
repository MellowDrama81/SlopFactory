using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// Foreground service keeping an active video-generation upload/poll alive while SlopFactory isn't
/// in the foreground — Android aggressively suspends ordinary background work,
/// unlike Windows. Started/stopped by <see cref="AndroidBackgroundExecutionService"/>, never on its
/// own — a generation is never started automatically during device boot, which is
/// trivially true here since nothing in this app has a boot receiver at all.
/// </summary>
[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class GenerationForegroundService : Service
{
    private const string ChannelId = "generation-background-transfer";
    private const int NotificationId = 9001;
    private const string StatusExtra = "statusText";

    /// <summary>Set by <see cref="Stop"/> immediately before the app itself requests teardown, so
    /// <see cref="OnDestroy"/> can tell an app-initiated stop apart from the OS revoking or timing
    /// out background execution on its own. Static because the service instance is owned and
    /// recreated by the OS, not by app code, so there is no instance to hold this on ahead of
    /// creation. Unverified on-device — see manual test coverage in
    /// IMPLEMENTATION_COMPLETION_CHECKLIST.md section 15.</summary>
    private static bool _stopRequestedByApp;

    /// <summary>Raised from <see cref="OnDestroy"/> when this service was torn down without a prior
    /// app-initiated <see cref="Stop"/> call — the OS killed or timed out the foreground service on
    /// its own. Static for the same reason <see cref="_stopRequestedByApp"/> is:
    /// <see cref="AndroidBackgroundExecutionService"/> has no direct reference to the OS-owned
    /// service instance to subscribe to.</summary>
    public static event EventHandler? SuspendedByOperatingSystem;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var statusText = intent?.GetStringExtra(StatusExtra) ?? "SlopFactory is transferring data.";
        StartForeground(NotificationId, BuildNotification(statusText));
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        if (_stopRequestedByApp)
        {
            _stopRequestedByApp = false;
        }
        else
        {
            SuspendedByOperatingSystem?.Invoke(null, EventArgs.Empty);
        }
        base.OnDestroy();
    }

    private Notification BuildNotification(string statusText)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) is null)
        {
            manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Background transfers", NotificationImportance.Low));
        }
#pragma warning disable CS8602 // NotificationCompat.Builder's fluent setters are bound as returning a nullable Builder even though they never return null.
        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("SlopFactory")
            .SetContentText(statusText)
            // Warns that leaving may interrupt the operation, since this ongoing
            // notification is itself the visible warning that background execution is in progress
            // and Android's own OS-level suspension risk still applies if it's dismissed/denied.
            .SetOngoing(true)
            .SetSmallIcon(ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.SymDefAppIcon)
            .Build()!;
#pragma warning restore CS8602
    }

    public static void Start(Context context, string statusText)
    {
        var intent = new Intent(context, typeof(GenerationForegroundService));
        intent.PutExtra(StatusExtra, statusText);
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    public static void Stop(Context context)
    {
        _stopRequestedByApp = true;
        context.StopService(new Intent(context, typeof(GenerationForegroundService)));
    }
}
