using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// Foreground service keeping an active video-generation upload/poll alive while SlopFactory isn't
/// in the foreground (plan.md:263-272) — Android aggressively suspends ordinary background work,
/// unlike Windows. Started/stopped by <see cref="AndroidBackgroundExecutionService"/>, never on its
/// own (plan.md:271 — a generation is never started automatically during device boot, which is
/// trivially true here since nothing in this app has a boot receiver at all).
/// </summary>
[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class GenerationForegroundService : Service
{
    private const string ChannelId = "generation-background-transfer";
    private const int NotificationId = 9001;
    private const string StatusExtra = "statusText";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var statusText = intent?.GetStringExtra(StatusExtra) ?? "SlopFactory is transferring data.";
        StartForeground(NotificationId, BuildNotification(statusText));
        return StartCommandResult.Sticky;
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
            // plan.md:268 — warns that leaving may interrupt the operation, since this ongoing
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

    public static void Stop(Context context) => context.StopService(new Intent(context, typeof(GenerationForegroundService)));
}
