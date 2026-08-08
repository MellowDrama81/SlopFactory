using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;
using System.Security.Cryptography;
using System.Text;

namespace Mellow.SlopFactory.Gui.Services;

public sealed class PreviewCacheService : IDisposable
{
    private const int RendererVersion = 1;
    private const int ThumbnailSize = 320;
    private const long MinimumLimit = 67_108_864;
    private const long MaximumLimit = 8_589_934_592;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly SemaphoreSlim _workerGate = new(2, 2);
    private readonly string _root = Path.Combine(FileSystem.CacheDirectory, "SlopFactory", "Previews");

    public static long DefaultLimitBytes => OperatingSystem.IsAndroid() ? 268_435_456 : 1_073_741_824;

    public static long LimitBytes
    {
        get => Math.Clamp(Preferences.Default.Get("preview_cache_limit_bytes", DefaultLimitBytes), MinimumLimit, MaximumLimit);
        set => Preferences.Default.Set("preview_cache_limit_bytes", Math.Clamp(value, MinimumLimit, MaximumLimit));
    }

    public async Task<PreviewThumbnail> GetImageThumbnailAsync(ILibraryWorkspace workspace, FileRecord file, CancellationToken cancellationToken = default)
    {
        if (!file.MediaType.StartsWith("image/", StringComparison.Ordinal) || file.MediaType == "image/svg+xml")
        {
            return new PreviewThumbnail(null, "A static thumbnail is unavailable for this file type.", false);
        }
        var path = GetEntryPath(workspace.Descriptor.LibraryId, file.ContentHash, "image", ".png");
        await _workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = await ReadCachedAsync(path, cancellationToken).ConfigureAwait(false);
            if (cached is not null) return new PreviewThumbnail(ToDataUri(cached), null, true);

            var content = await workspace.ReadImageFileAsync(file.Id, cancellationToken).ConfigureAwait(false);
            byte[] thumbnail;
            try
            {
                using var sourceStream = new MemoryStream(content.Bytes, writable: false);
                using var source = PlatformImage.FromStream(sourceStream);
                using var resized = source.Downsize(ThumbnailSize);
                using var output = new MemoryStream();
                resized.Save(output, ImageFormat.Png);
                thumbnail = output.ToArray();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                return new PreviewThumbnail(null, "Preview Unavailable: the platform image decoder rejected this file.", false);
            }
            await WriteCachedAsync(path, thumbnail, cancellationToken).ConfigureAwait(false);
            return new PreviewThumbnail(ToDataUri(thumbnail), null, false);
        }
        catch (LibraryValidationException exception)
        {
            return new PreviewThumbnail(null, exception.Message, false);
        }
        finally { _workerGate.Release(); }
    }

    public Task<PreviewThumbnail> GetThumbnailAsync(ILibraryWorkspace workspace, FileRecord file, CancellationToken cancellationToken = default) =>
        file.MediaType == "video/mp4" ? GetVideoPosterAsync(workspace, file, cancellationToken) : GetImageThumbnailAsync(workspace, file, cancellationToken);

    private async Task<PreviewThumbnail> GetVideoPosterAsync(ILibraryWorkspace workspace, FileRecord file, CancellationToken cancellationToken)
    {
        var path = GetEntryPath(workspace.Descriptor.LibraryId, file.ContentHash, "video-poster", ".png");
        await _workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = await ReadCachedAsync(path, cancellationToken).ConfigureAwait(false);
            if (cached is not null) return new PreviewThumbnail(ToDataUri(cached), null, true);
            _ = await workspace.PrepareMediaPlaybackAsync(file.Id, cancellationToken).ConfigureAwait(false);
            byte[]? poster;
            try { poster = await ExtractVideoPosterAsync(workspace.GetManagedFilePath(file), cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                return new PreviewThumbnail(null, "Preview Unavailable: the platform video decoder could not extract a poster.", false);
            }
            if (poster is null || poster.Length == 0) return new PreviewThumbnail(null, "Preview Unavailable: this video has no decodable poster frame.", false);
            await WriteCachedAsync(path, poster, cancellationToken).ConfigureAwait(false);
            return new PreviewThumbnail(ToDataUri(poster), null, false);
        }
        catch (LibraryValidationException exception) { return new PreviewThumbnail(null, exception.Message, false); }
        finally { _workerGate.Release(); }
    }

    private static async Task<byte[]?> ExtractVideoPosterAsync(string path, CancellationToken cancellationToken)
    {
#if ANDROID
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        using var retriever = new Android.Media.MediaMetadataRetriever();
        retriever.SetDataSource(path);
        using var frame = retriever.GetFrameAtTime(0, Android.Media.Option.ClosestSync);
        if (frame is null) return null;
        var scale = Math.Min(1d, ThumbnailSize / (double)Math.Max(frame.Width, frame.Height));
        using var resized = Android.Graphics.Bitmap.CreateScaledBitmap(frame, Math.Max(1, (int)(frame.Width * scale)), Math.Max(1, (int)(frame.Height * scale)), true)
            ?? throw new InvalidOperationException("The platform video frame could not be resized.");
        using var output = new MemoryStream();
        if (!resized.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 90, output)) return null;
        return output.ToArray();
#else
        var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var thumbnail = await storageFile.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, ThumbnailSize, Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
        if (thumbnail is null || thumbnail.Size == 0) return null;
        using var input = thumbnail.AsStreamForRead();
        using var output = new MemoryStream();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
#endif
    }

    public async Task<PreviewCacheStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return new PreviewCacheStatus(CalculateUse(), LimitBytes); }
        finally { _cacheGate.Release(); }
    }

    public async Task SetLimitAsync(long limitBytes, CancellationToken cancellationToken = default)
    {
        LimitBytes = limitBytes;
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { TrimToLimit(); }
        finally { _cacheGate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        finally { _cacheGate.Release(); }
    }

    public async Task ForgetLibraryAsync(string libraryId, CancellationToken cancellationToken = default)
    {
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_root, libraryId);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        finally { _cacheGate.Release(); }
    }

    public async Task<PreviewRebuildResult> RebuildLibraryAsync(ILibraryWorkspace workspace, IProgress<PreviewRebuildProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await ForgetLibraryAsync(workspace.Descriptor.LibraryId, cancellationToken).ConfigureAwait(false);
        var files = (await workspace.GetActiveFilesAsync(cancellationToken).ConfigureAwait(false))
            .Where(file => (file.MediaType.StartsWith("image/", StringComparison.Ordinal) && file.MediaType != "image/svg+xml") || file.MediaType == "video/mp4")
            .ToArray();
        var rebuilt = 0;
        var unavailable = 0;
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            progress?.Report(new PreviewRebuildProgress(index + 1, files.Length, file.DisplayName));
            var preview = await GetThumbnailAsync(workspace, file, cancellationToken).ConfigureAwait(false);
            if (preview.DataUri is null) unavailable++;
            else rebuilt++;
        }
        return new PreviewRebuildResult(files.Length, rebuilt, unavailable);
    }

    private async Task<byte[]?> ReadCachedAsync(string path, CancellationToken cancellationToken)
    {
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return bytes;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { _cacheGate.Release(); }
    }

    private async Task WriteCachedAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            TrimToLimit();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { _cacheGate.Release(); }
    }

    private string GetEntryPath(string libraryId, string contentHash, string previewType, string extension)
    {
        var identity = $"{libraryId}|{contentHash}|{previewType}|{ThumbnailSize}|{RendererVersion}";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_root, libraryId, key + extension);
    }

    private long CalculateUse()
    {
        if (!Directory.Exists(_root)) return 0;
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(path).Length; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    private void TrimToLimit()
    {
        if (!Directory.Exists(_root)) return;
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(info => !info.Name.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderBy(info => info.LastWriteTimeUtc)
            .ToArray();
        var use = files.Sum(info => info.Length);
        foreach (var file in files)
        {
            if (use <= LimitBytes) break;
            try { use -= file.Length; file.Delete(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string ToDataUri(byte[] bytes) => $"data:image/png;base64,{Convert.ToBase64String(bytes)}";

    public void Dispose()
    {
        _cacheGate.Dispose();
        _workerGate.Dispose();
    }
}

public sealed record PreviewThumbnail(string? DataUri, string? Error, bool WasCached);
public sealed record PreviewCacheStatus(long UseBytes, long LimitBytes);
public sealed record PreviewRebuildProgress(int ProcessedItems, int TotalItems, string DisplayName);
public sealed record PreviewRebuildResult(int EligibleFiles, int RebuiltFiles, int UnavailableFiles);
