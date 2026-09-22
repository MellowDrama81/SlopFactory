using System.Net;

namespace SlopFactory.Services;

public sealed record ConnectionTestResult(bool Succeeded, string Message);

public sealed class ComfyConnectionTester
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<ConnectionTestResult> TestAsync(ComfyConnectionSettings settings)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildTestEndpoint(settings));
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                request.Headers.Add("X-API-Key", settings.ApiKey.Trim());
            }

            using var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
                return new(true, "Connection successful. No workflow was submitted.");

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(false, "The server rejected the API key or requires one."),
                _ => new(false, $"The server returned {(int)response.StatusCode} {response.ReasonPhrase}.")
            };
        }
        catch (UriFormatException)
        {
            return new(false, "Enter a valid server URL before testing.");
        }
        catch (TaskCanceledException)
        {
            return new(false, "The connection timed out. Check the server address and network.");
        }
        catch (HttpRequestException)
        {
            return new(false, "Could not reach the server. Check the address and network.");
        }
        catch (Exception)
        {
            return new(false, "The connection test could not be completed.");
        }
    }

    private static Uri BuildTestEndpoint(ComfyConnectionSettings settings)
    {
        var server = new Uri(settings.ServerUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        if (!string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new UriFormatException();

        return string.Equals(server.Host, "cloud.comfy.org", StringComparison.OrdinalIgnoreCase)
            ? new Uri(server, "api/user")
            : new Uri(server, "system_stats");
    }
}
