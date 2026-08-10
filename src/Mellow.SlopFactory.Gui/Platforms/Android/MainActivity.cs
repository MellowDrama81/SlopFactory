using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Mellow.SlopFactory.Gui.Services;

namespace Mellow.SlopFactory.Gui;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
[IntentFilter(new[] { Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const int PickTreeRequest = 4101;
    private const int CreateDocumentRequest = 4102;
    private TaskCompletionSource<Android.Net.Uri?>? _pickTreeCompletion;
    private TaskCompletionSource<Android.Net.Uri?>? _createDocumentCompletion;
    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
        QueueSharedContent(Intent);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        _pickTreeCompletion?.TrySetCanceled();
        _createDocumentCompletion?.TrySetCanceled();
        base.OnDestroy();
    }

    public Task<Android.Net.Uri?> PickDocumentTreeAsync(CancellationToken cancellationToken)
    {
        if (_pickTreeCompletion is not null) throw new InvalidOperationException("A document-tree picker is already open.");
        _pickTreeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => _pickTreeCompletion?.TrySetCanceled(cancellationToken));
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
        StartActivityForResult(intent, PickTreeRequest);
        return _pickTreeCompletion.Task;
    }

    public Task<Android.Net.Uri?> CreateDocumentAsync(string displayName, string mediaType, CancellationToken cancellationToken)
    {
        if (_createDocumentCompletion is not null) throw new InvalidOperationException("A create-document picker is already open.");
        _createDocumentCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => _createDocumentCompletion?.TrySetCanceled(cancellationToken));
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.SetType(string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType);
        intent.PutExtra(Intent.ExtraTitle, displayName);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        StartActivityForResult(intent, CreateDocumentRequest);
        return _createDocumentCompletion.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        var uri = resultCode == Result.Ok ? data?.Data : null;
        if (requestCode == PickTreeRequest)
        {
            var completion = _pickTreeCompletion;
            _pickTreeCompletion = null;
            if (uri is not null)
            {
                try { ContentResolver?.TakePersistableUriPermission(uri, data?.Flags is { } flags ? flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission) : ActivityFlags.GrantReadUriPermission); }
                catch (Java.Lang.SecurityException) { }
            }
            completion?.TrySetResult(uri);
        }
        else if (requestCode == CreateDocumentRequest)
        {
            var completion = _createDocumentCompletion;
            _createDocumentCompletion = null;
            completion?.TrySetResult(uri);
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        QueueSharedContent(intent);
    }

    private void QueueSharedContent(Intent? intent)
    {
        if (intent?.Action is not (Intent.ActionSend or Intent.ActionSendMultiple)) return;
        var uris = new Dictionary<string, Android.Net.Uri>(StringComparer.Ordinal);
        if (intent.ClipData is { } clip)
        {
            for (var index = 0; index < clip.ItemCount; index++)
            {
                if (clip.GetItemAt(index)?.Uri is { } uri) uris[uri.ToString() ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = uri;
            }
        }
#pragma warning disable CS0618, CS0619, CA1422
        if (intent.Action == Intent.ActionSend && intent.GetParcelableExtra(Intent.ExtraStream) is Android.Net.Uri single)
        {
            uris[single.ToString() ?? "single"] = single;
        }
        if (intent.Action == Intent.ActionSendMultiple && intent.GetParcelableArrayListExtra(Intent.ExtraStream) is { } multiple)
        {
            foreach (var value in multiple)
            {
                if (value is Android.Net.Uri uri) uris[uri.ToString() ?? Guid.NewGuid().ToString("N")] = uri;
            }
        }
#pragma warning restore CS0618, CS0619, CA1422
        if (uris.Count > 0) _ = StageSharedContentAsync(uris.Values);
    }

    private async Task StageSharedContentAsync(IEnumerable<Android.Net.Uri> uris)
    {
        var service = IPlatformApplication.Current?.Services.GetService<IncomingImportService>();
        if (service is null) return;
        foreach (var uri in uris)
        {
            try
            {
                await using var stream = ContentResolver?.OpenInputStream(uri);
                if (stream is null) continue;
                await service.StageAndQueueAsync(stream, GetDisplayName(uri));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Java.Lang.SecurityException or Java.IO.FileNotFoundException)
            {
                service.QueueFailure(GetDisplayName(uri), "Shared content could not be read. Its permission may have expired or the provider may be unavailable.");
            }
        }
    }

    private string GetDisplayName(Android.Net.Uri uri)
    {
        if (ContentResolver is null) return uri.LastPathSegment ?? "shared-file.bin";
        try
        {
            using var cursor = ContentResolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (index >= 0 && cursor.GetString(index) is { Length: > 0 } name) return name;
            }
        }
        catch (Exception exception) when (exception is Java.Lang.SecurityException or ArgumentException) { }
        return uri.LastPathSegment ?? "shared-file.bin";
    }
}
