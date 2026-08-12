#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#elif ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
#endif

namespace Mellow.SlopFactory.Gui.Services;

internal sealed class MauiNotificationService : INotificationService
{
#if ANDROID
    private const string ChannelId = "generation-notifications";
    private const string RecordIdExtra = "generationRecordId";
#endif

    public event EventHandler<string>? Tapped;

    public MauiNotificationService()
    {
#if WINDOWS
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        AppNotificationManager.Default.Register();
#elif ANDROID
        var channel = new NotificationChannel(ChannelId, "Generation notifications", NotificationImportance.Default);
        var manager = (NotificationManager?)Android.App.Application.Context.GetSystemService(Context.NotificationService);
        manager?.CreateNotificationChannel(channel);
#endif
    }

    public Task<bool> RequestPermissionAsync()
    {
#if WINDOWS
        return Task.FromResult(true);
#elif ANDROID
        return RequestAndroidPermissionAsync();
#else
        return Task.FromResult(false);
#endif
    }

    public void Show(string recordId, string title, string body)
    {
#if WINDOWS
        var notification = new AppNotificationBuilder()
            .AddArgument("recordId", recordId)
            .AddText(title)
            .AddText(body)
            .BuildNotification();
        AppNotificationManager.Default.Show(notification);
#elif ANDROID
        ShowAndroidNotification(recordId, title, body);
#endif
    }

#if WINDOWS
    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("recordId", out var recordId)) Tapped?.Invoke(this, recordId);
    }
#elif ANDROID
    private static async Task<bool> RequestAndroidPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return true;
        var activity = MainActivity.Current ?? throw new InvalidOperationException("The Android activity is unavailable.");
        if (ContextCompat.CheckSelfPermission(activity, Android.Manifest.Permission.PostNotifications) == Permission.Granted) return true;
        return await activity.RequestNotificationPermissionAsync().ConfigureAwait(false);
    }

    private static void ShowAndroidNotification(string recordId, string title, string body)
    {
        var context = Android.App.Application.Context ?? throw new InvalidOperationException("The Android application context is unavailable.");
        var intent = new Intent(context, typeof(MainActivity));
        intent.PutExtra(RecordIdExtra, recordId);
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(context, recordId.GetHashCode(), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
#pragma warning disable CS8602 // NotificationCompat.Builder's fluent setters are bound as returning a nullable Builder even though they never return null.
        var notification = new NotificationCompat.Builder(context, ChannelId)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetSmallIcon(context.ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.SymDefAppIcon)
            .SetAutoCancel(true)
            .SetContentIntent(pendingIntent)
            .Build();
#pragma warning restore CS8602
        var manager = NotificationManagerCompat.From(context) ?? throw new InvalidOperationException("The Android notification manager is unavailable.");
        manager.Notify(recordId.GetHashCode(), notification!);
    }

    internal void RaiseTapped(string recordId) => Tapped?.Invoke(this, recordId);
#endif
}
