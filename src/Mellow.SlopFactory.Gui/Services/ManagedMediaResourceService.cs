using Mellow.SlopFactory.Domain;
using System.Collections.Concurrent;
using System.Globalization;

namespace Mellow.SlopFactory.Gui.Services;

public sealed class ManagedMediaResourceService(AppLibraryState libraryState)
{
    private const string RoutePrefix = "/slopfactory-media/";
    private readonly ConcurrentDictionary<string, PlaybackGrant> _grants = new(StringComparer.Ordinal);

    public async Task<ManagedMediaSource> CreateAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var workspace = libraryState.Workspace ?? throw new LibraryValidationException("No library is open.");
        var descriptor = await workspace.PrepareMediaPlaybackAsync(fileId, cancellationToken).ConfigureAwait(false);
        var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _grants[token] = new PlaybackGrant(workspace.Descriptor.LibraryId, descriptor);
        return new ManagedMediaSource(token, RoutePrefix + token, descriptor.MediaType);
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) _grants.TryRemove(token, out _);
    }

    public void HandleWebResourceRequested(object? sender, WebViewWebResourceRequestedEventArgs args)
    {
        if (!TryGetToken(args.Uri, out var token)) return;
        args.Handled = true;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cache-Control"] = "no-store",
            ["X-Content-Type-Options"] = "nosniff"
        };
        if (!_grants.TryGetValue(token, out var grant) || libraryState.Workspace is not { } workspace ||
            !string.Equals(workspace.Descriptor.LibraryId, grant.LibraryId, StringComparison.Ordinal))
        {
            SetEmptyResponse(args, 404, "Not Found", headers);
            return;
        }
        if (!string.Equals(args.Method, "GET", StringComparison.OrdinalIgnoreCase) && !string.Equals(args.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            headers["Allow"] = "GET, HEAD";
            SetEmptyResponse(args, 405, "Method Not Allowed", headers);
            return;
        }

        var total = grant.Descriptor.ByteSize;
        var rangeHeader = args.Headers.FirstOrDefault(header => string.Equals(header.Key, "Range", StringComparison.OrdinalIgnoreCase)).Value;
        if (!TryResolveRange(rangeHeader, total, out var offset, out var length, out var partial))
        {
            headers["Content-Range"] = $"bytes */{total.ToString(CultureInfo.InvariantCulture)}";
            SetEmptyResponse(args, 416, "Range Not Satisfiable", headers);
            return;
        }

        headers["Accept-Ranges"] = "bytes";
        headers["Content-Type"] = grant.Descriptor.MediaType;
        headers["Content-Length"] = length.ToString(CultureInfo.InvariantCulture);
        if (partial) headers["Content-Range"] = $"bytes {offset}-{offset + length - 1}/{total}";
        var status = partial ? 206 : 200;
        var reason = partial ? "Partial Content" : "OK";
        if (string.Equals(args.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            SetEmptyResponse(args, status, reason, headers);
            return;
        }
        args.SetResponse(status, reason, headers, OpenRangeAsync(workspace, grant.Descriptor, offset, length));
    }

    private static void SetEmptyResponse(WebViewWebResourceRequestedEventArgs args, int status, string reason, IReadOnlyDictionary<string, string> headers) =>
        args.SetResponse(status, reason, headers, new MemoryStream());

    private static async Task<Stream?> OpenRangeAsync(Mellow.SlopFactory.Application.ILibraryWorkspace workspace, MediaPlaybackDescriptor descriptor, long offset, long length) =>
        await workspace.OpenMediaRangeAsync(descriptor.FileId, descriptor.ContentHash, offset, length).ConfigureAwait(false);

    private static bool TryGetToken(Uri uri, out string token)
    {
        token = string.Empty;
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
        if (!path.StartsWith(RoutePrefix, StringComparison.Ordinal)) return false;
        token = path[RoutePrefix.Length..];
        return token.Length == 64 && token.All(Uri.IsHexDigit);
    }

    internal static bool TryResolveRange(string? value, long total, out long offset, out long length, out bool partial)
    {
        offset = 0;
        length = total;
        partial = false;
        if (total < 0) return false;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(',')) return false;
        var range = value[6..].Trim();
        var dash = range.IndexOf('-');
        if (dash < 0 || dash != range.LastIndexOf('-') || total == 0) return false;
        var startText = range[..dash].Trim();
        var endText = range[(dash + 1)..].Trim();
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            length = Math.Min(suffix, total);
            offset = total - length;
        }
        else
        {
            if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset >= total) return false;
            var end = total - 1;
            if (endText.Length > 0 && (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < offset)) return false;
            end = Math.Min(end, total - 1);
            length = end - offset + 1;
        }
        partial = true;
        return true;
    }

    private sealed record PlaybackGrant(string LibraryId, MediaPlaybackDescriptor Descriptor);
}

public sealed record ManagedMediaSource(string Token, string Uri, string MediaType);
