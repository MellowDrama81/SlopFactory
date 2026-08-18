using System.Net;
using System.Net.Sockets;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// Validates a provider-hosted result URL (a downloadable video/asset URL returned in a provider's
/// JSON response, as opposed to inline base64 bytes) before it is fetched: a result URL from a
/// public-internet connection cannot target or redirect to loopback,
/// link-local, private, multicast or unspecified network addresses." Host resolution is injected
/// rather than hardcoded to <see cref="Dns"/> so callers — and this class's own tests — never
/// perform a real DNS lookup against a literal test hostname.
/// Callers must invoke this validation for the initial URL and each redirect target. OpenRouter's
/// result-download path does so with automatic redirects disabled.
/// </summary>
internal static class ResultUrlValidator
{
    public static async Task ValidateHostAsync(Uri uri, Func<string, CancellationToken, Task<IPAddress[]>> resolveHost, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ProviderAdapterException("Provider result URLs must use HTTPS.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await resolveHost(uri.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            throw new ProviderAdapterException($"Could not resolve the result host '{uri.Host}'.");
        }

        if (addresses.Length == 0 || Array.Exists(addresses, IsBlockedAddress))
        {
            throw new ProviderAdapterException("The provider result URL resolved to a disallowed network address.");
        }
    }

    /// <summary>True for loopback, unspecified, link-local, private and multicast/reserved
    /// addresses — every class a public-internet result URL must never resolve to.</summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0) return true; // 0.0.0.0/8 - "this network"
            if (bytes[0] == 10) return true; // 10.0.0.0/8 - private
            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return true; // 172.16.0.0/12 - private
            if (bytes[0] == 192 && bytes[1] == 168) return true; // 192.168.0.0/16 - private
            if (bytes[0] == 169 && bytes[1] == 254) return true; // 169.254.0.0/16 - link-local
            if (bytes[0] >= 224) return true; // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            if ((address.GetAddressBytes()[0] & 0xFE) == 0xFC) return true; // fc00::/7 - unique local
        }

        return false;
    }
}
