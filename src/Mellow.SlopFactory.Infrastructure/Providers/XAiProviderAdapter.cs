using System.Net;
using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// xAI (`api.x.ai/v1`) exposes an OpenAI-compatible `chat/completions` endpoint for Grok text models,
/// and a separate `images/generations`-shaped endpoint for Grok Imagine — see providers.md and
/// <see cref="GenerateImageAsync"/>'s remarks. Audio (bundled only into video, not a standalone
/// endpoint) and video (up to 15s, native audio) still ride on an endpoint shape this pass did not
/// attempt to guess at, so neither is implemented.
/// </summary>
internal sealed class XAiProviderAdapter : IProviderAdapter
{
    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;

    public XAiProviderAdapter(HttpClient httpClient, Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _httpClient = httpClient;
        _resolveHost = resolveHost ?? Dns.GetHostAddressesAsync;
        _rateLimitTracker = rateLimitTracker;
    }

    public ProviderType ProviderType => ProviderType.XAi;

    public async Task<ConnectionTestResult> TestConnectionAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default)
    {
        var host = TryGetHost(connection.BaseUrl);
        try
        {
            var models = await ListModelsAsync(connection, apiKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ConnectionTestResult(true, "Connection succeeded.", host, true, models);
        }
        catch (ProviderAdapterException exception)
        {
            return new ConnectionTestResult(false, exception.Message, host, false);
        }
        catch (HttpRequestException exception)
        {
            return new ConnectionTestResult(false, $"Could not reach the connection: {exception.Message}", host, false);
        }
    }

    public async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, GenerationMode? mode = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "models"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
        return OpenAiCompatibleProtocol.ParseModelList(body);
    }

    public async Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, GenerationSettings? settings = null, CancellationToken cancellationToken = default)
    {
        OpenAiCompatibleProtocol.ValidateSystemInstructionsSupported(model, systemInstructions);
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "chat/completions"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        request.Content = new StringContent(OpenAiCompatibleProtocol.BuildChatCompletionRequestBody(model.ProviderModelId, prompt, resultCount, systemInstructions, sourceImages, settings), Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
        return OpenAiCompatibleProtocol.ParseChatCompletionResult(body);
    }

    /// <summary>
    /// Per xAI's public API reference: <c>POST /v1/images/generations</c>, body
    /// <c>{"model":"grok-2-image-1212","prompt":"...","n":N}</c> — xAI's documented behavior is that it
    /// only ever returns a hosted <c>url</c> per result (no <c>b64_json</c> option), unlike OpenAI's
    /// endpoint of the same name, so this never sends <c>response_format</c>. Each returned URL is a
    /// provider-hosted result — the same third-party-result shape as OpenRouter/1min.AI/ComfyUI — so it
    /// gets the same <see cref="ResultUrlValidator"/> host revalidation and DNS-rebinding-hardened
    /// handler (<see cref="DependencyInjection.CreateOpenRouterHttpHandler"/>). This shape was not
    /// exercised against a live account.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one image result must be requested.");
        if (sourceImages is { Count: > 0 })
        {
            throw new ProviderAdapterException("Reference-image editing is not implemented for xAI's Grok Imagine; only plain text-to-image generation is supported.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "images/generations"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        request.Content = new StringContent(BuildImageGenerationRequestBody(model.ProviderModelId, prompt, resultCount), Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));

        var urls = ParseImageResultUrls(body);
        var results = new List<byte[]>(urls.Count);
        foreach (var url in urls)
        {
            results.Add(await DownloadResultUrlAsync(url, connection, apiKey, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? voice = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Audio generation is not implemented for xAI: audio is bundled only into video generation, not offered as a standalone endpoint.");

    public Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not implemented for xAI: Grok Imagine's video endpoint shape was not verified.");

    public Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not implemented for xAI: Grok Imagine's video endpoint shape was not verified.");

    private static string BuildImageGenerationRequestBody(string providerModelId, string prompt, int resultCount)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteString("prompt", prompt);
            writer.WriteNumber("n", resultCount);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static List<string> ParseImageResultUrls(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderAdapterException("The provider's image response was not in the expected shape.");
            }

            var urls = new List<string>();
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String) continue;
                var url = urlElement.GetString();
                if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
            }

            if (urls.Count == 0) throw new ProviderAdapterException("The provider returned no usable image result URLs.");
            return urls;
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's image response was not valid JSON.");
        }
    }

    private async Task<byte[]> DownloadResultUrlAsync(string resultUrl, Connection connection, string? apiKey, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(resultUrl, UriKind.Absolute, out var currentUri))
        {
            throw new ProviderAdapterException("The provider returned a result URL that could not be parsed.");
        }

        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            await ResultUrlValidator.ValidateHostAsync(currentUri, _resolveHost, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            if (IsConnectionOrigin(connection.BaseUrl, currentUri))
            {
                OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
                OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
            }

            var (succeeded, statusCode, bytes, redirectLocation, mediaType, digestHeaders) = await OpenAiCompatibleProtocol.SendForBytesWithRedirectAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (succeeded)
            {
                if (!(string.IsNullOrWhiteSpace(mediaType) || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ProviderAdapterException($"The completed result declared unexpected media type '{mediaType}'.");
                }

                OpenAiCompatibleProtocol.VerifySha256Digest(bytes, digestHeaders);
                if (bytes.Length == 0) throw new ProviderAdapterException("The provider returned an empty result.");
                return bytes;
            }

            if (statusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirectLocation is null || !Uri.TryCreate(currentUri, redirectLocation, out currentUri)) throw new ProviderAdapterException("The result download redirect had no valid target URL.");
                continue;
            }

            throw new ProviderAdapterException($"Downloading the completed result failed: {OpenAiCompatibleProtocol.DescribeFailure(statusCode)}");
        }

        throw new ProviderAdapterException("The result download exceeded the maximum redirect count.");
    }

    private static bool IsConnectionOrigin(string connectionBaseUrl, Uri resultUri) =>
        Uri.TryCreate(connectionBaseUrl, UriKind.Absolute, out var connectionUri) &&
        string.Equals(connectionUri.Scheme, resultUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(connectionUri.Host, resultUri.Host, StringComparison.OrdinalIgnoreCase) &&
        connectionUri.Port == resultUri.Port;

    private static string? TryGetHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
