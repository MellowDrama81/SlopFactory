using System.Net;
using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// OpenRouter uses its OpenAI-compatible base URL and standard Bearer authentication for chat and
/// model listing, reusing <see cref="OpenAiCompatibleProtocol"/> exactly like
/// <see cref="OpenAiProviderAdapter"/>. Image, audio and video generation use OpenRouter's own
/// modality-specific endpoints and schemas rather than the OpenAI-compatible surface (confirmed
/// against https://openrouter.ai/docs — see the request/response builders below for the exact shapes).
/// Video generation is asynchronous (submit-then-poll): a submitted job's bytes are not returned
/// inline but fetched from one or more authenticated result URLs once the job reports completed.
/// </summary>
internal sealed class OpenRouterProviderAdapter : IProviderAdapter
{
    private const string DefaultAudioVoice = "alloy";

    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;

    public OpenRouterProviderAdapter(HttpClient httpClient, Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _httpClient = httpClient;
        _resolveHost = resolveHost ?? Dns.GetHostAddressesAsync;
        _rateLimitTracker = rateLimitTracker;
    }

    public ProviderType ProviderType => ProviderType.OpenRouter;

    public async Task<ConnectionTestResult> TestConnectionAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default)
    {
        var host = TryGetHost(connection.BaseUrl);
        try
        {
            var models = await ListModelsAsync(connection, apiKey, cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "models"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess)
        {
            if (statusCode == HttpStatusCode.NotFound)
            {
                throw new ProviderAdapterException("Model discovery is not available at this base URL. The connection can still be saved and used manually.");
            }

            throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
        }

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

    public async Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "images"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        request.Content = new StringContent(BuildImageRequestBody(model.ProviderModelId, prompt, resultCount), Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
        return OpenAiCompatibleProtocol.ParseImageGenerationBytes(body);
    }

    public async Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one audio result must be requested.");
        var results = new List<byte[]>(resultCount);
        for (var index = 0; index < resultCount; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "audio/speech"));
            OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
            OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
            request.Content = new StringContent(BuildAudioSpeechRequestBody(model.ProviderModelId, prompt), Encoding.UTF8, "application/json");
            var (isSuccess, statusCode, bytes) = await OpenAiCompatibleProtocol.SendForBytesAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
            if (bytes.Length == 0) throw new ProviderAdapterException("The provider returned an empty audio result.");
            results.Add(bytes);
        }

        return results;
    }

    public async Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default)
    {
        // OpenRouter's video submit endpoint documents no image-to-video field — firstFrame is
        // never populated for this provider today (LibraryRules.GetInputSlotCapabilities doesn't
        // declare the capability), so it's accepted but ignored rather than guessed at.
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "videos"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        request.Content = new StringContent(BuildVideoSubmissionRequestBody(model.ProviderModelId, prompt), Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                throw new ProviderAdapterException("The provider's video submission response did not include a job ID.");
            }

            var jobId = idElement.GetString();
            if (string.IsNullOrWhiteSpace(jobId)) throw new ProviderAdapterException("The provider's video submission response did not include a job ID.");
            return new AsyncGenerationSubmission(jobId);
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's video submission response was not valid JSON.");
        }
    }

    public async Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, $"videos/{Uri.EscapeDataString(providerJobId)}"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() : null;
            switch (status)
            {
                case "completed":
                    var urls = new List<string>();
                    if (root.TryGetProperty("unsigned_urls", out var urlsElement) && urlsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in urlsElement.EnumerateArray())
                        {
                            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } url) urls.Add(url);
                        }
                    }
                    if (urls.Count == 0) return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Failed, null, "The provider reported the video as completed but returned no result URLs.");
                    var files = new List<byte[]>(urls.Count);
                    foreach (var url in urls)
                    {
                        // A malformed URL or one that fails SSRF host validation is not a transient
                        // download problem — retrying later would fail identically every time — so
                        // these deliberately still throw hard rather than becoming
                        // CompletedDownloadFailed, unlike the retryable network/HTTP failures below.
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var resultUri))
                        {
                            throw new ProviderAdapterException("The provider returned a video result URL that could not be parsed.");
                        }
                        // Validate the provider-supplied URL outside the retryable download block:
                        // a malformed or private initial target is a permanent security rejection.
                        await ResultUrlValidator.ValidateHostAsync(resultUri, _resolveHost, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            var bytes = await DownloadResultAsync(resultUri, connection, apiKey, cancellationToken).ConfigureAwait(false);
                            if (bytes.Length == 0) throw new ProviderAdapterException("The provider returned an empty video result.");
                            files.Add(bytes);
                        }
                        catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                        {
                            // The provider itself confirmed completion — only the download failed, so
                            // this is retryable (the provider's result may still be available) rather
                            // than a genuine provider-side failure. See AsyncGenerationPollOutcome
                            // .CompletedDownloadFailed.
                            return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.CompletedDownloadFailed, null, exception.Message);
                        }
                    }
                    return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, files, null, ParseCost(root));
                case "failed":
                    var errorMessage = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String ? errorElement.GetString() : null;
                    return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Failed, null, errorMessage ?? "The provider reported the video generation job as failed.");
                case "cancelled":
                    return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Failed, null, "The video generation job was cancelled.");
                case "expired":
                    return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Failed, null, "The video generation job expired before it completed.");
                default:
                    return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Processing, null, null);
            }
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's video status response was not valid JSON.");
        }
    }

    /// <summary>
    /// Extracts the provider-reported <c>usage.cost</c> field OpenRouter's video (and image)
    /// responses include. The currency is not itself part of that response field, but "USD" is
    /// correct: OpenRouter's own FAQ documents "OpenRouter uses a credit system where the base
    /// currency is US dollars. All of the pricing on our site and API is denoted in dollars."
    /// (https://openrouter.ai/docs/faq, confirmed 2026-08-18).
    /// </summary>
    private static AsyncGenerationCost? ParseCost(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        if (!usage.TryGetProperty("cost", out var costElement) || costElement.ValueKind != JsonValueKind.Number) return null;
        return new AsyncGenerationCost(costElement.GetDouble(), "USD");
    }

    private async Task<byte[]> DownloadResultAsync(Uri initialUri, Connection connection, string? apiKey, CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
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
                if (!IsAllowedVideoResultMediaType(mediaType))
                {
                    throw new ProviderAdapterException($"The completed video result declared unexpected media type '{mediaType}'.");
                }
                OpenAiCompatibleProtocol.VerifySha256Digest(bytes, digestHeaders);
                return bytes;
            }
            if (statusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirectLocation is null || !Uri.TryCreate(currentUri, redirectLocation, out currentUri)) throw new ProviderAdapterException("The provider result download redirect had no valid target URL.");
                continue;
            }
            throw new ProviderAdapterException($"Downloading the completed video result failed: {OpenAiCompatibleProtocol.DescribeFailure(statusCode)}");
        }
        throw new ProviderAdapterException("The provider result download exceeded the maximum redirect count.");
    }

    private static bool IsConnectionOrigin(string connectionBaseUrl, Uri resultUri) =>
        Uri.TryCreate(connectionBaseUrl, UriKind.Absolute, out var connectionUri) &&
        string.Equals(connectionUri.Scheme, resultUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(connectionUri.Host, resultUri.Host, StringComparison.OrdinalIgnoreCase) &&
        connectionUri.Port == resultUri.Port;

    private static bool IsAllowedVideoResultMediaType(string? mediaType) =>
        string.IsNullOrWhiteSpace(mediaType) ||
        mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);

    private static string BuildImageRequestBody(string providerModelId, string prompt, int resultCount)
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

    private static string BuildAudioSpeechRequestBody(string providerModelId, string prompt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteString("input", prompt);
            writer.WriteString("voice", DefaultAudioVoice);
            writer.WriteString("response_format", "mp3");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildVideoSubmissionRequestBody(string providerModelId, string prompt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteString("prompt", prompt);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? TryGetHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
