using System.Net;
using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// 1min.ai (https://api.1min.ai) is not OpenAI-compatible: chat lives at its own
/// <c>POST /api/chat-with-ai</c> endpoint and every other modality shares one
/// <c>POST /api/features</c> envelope keyed by a <c>type</c>/<c>model</c>/<c>promptObject</c> triple,
/// so this adapter builds its own request/response handling rather than reusing
/// <see cref="OpenAiCompatibleProtocol"/>'s OpenAI-shaped helpers (it still reuses the protocol's
/// generic transport/auth/redirect-download plumbing). Shapes below are confirmed by live API calls
/// against one representative model per modality — see docs/developer/1minai-contract.md, which also
/// documents a live-confirmed case of a per-model docs page naming a model identifier
/// (`black-forest-labs/flux-schnell`) that the API actually rejects. Because of that, this adapter
/// only encodes the one image `promptObject` shape (Stable Diffusion XL's: prompt/samples/size) that
/// was actually verified live; the other ~40 documented image models and 9 of 10 video models use
/// their own undocumented-to-this-adapter field sets and are expected to fail with a clear
/// provider-returned error rather than a guessed request shape. Video generation is not implemented
/// at all: 1min.ai's video default is genuinely synchronous (confirmed live — a non-<c>async</c>
/// request blocks the HTTP connection for the full render), which doesn't fit this app's
/// submit-then-poll <see cref="IProviderAdapter.SubmitVideoGenerationAsync"/>/
/// <see cref="IProviderAdapter.PollVideoGenerationAsync"/> split, and the alternative
/// <c>async: true</c> + <c>GET /api/results/{uuid}</c> path that would fit it was never live-tested.
/// Model discovery is not implemented either — no models-listing endpoint is documented anywhere in
/// 1min.ai's API reference.
/// </summary>
internal sealed class OneMinAiProviderAdapter : IProviderAdapter
{
    private const string DefaultAudioVoice = "alloy";
    private const string ConfirmedImageSize = "1024x1024";

    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;

    public OneMinAiProviderAdapter(HttpClient httpClient, Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _httpClient = httpClient;
        _resolveHost = resolveHost ?? Dns.GetHostAddressesAsync;
        _rateLimitTracker = rateLimitTracker;
    }

    public ProviderType ProviderType => ProviderType.OneMinAi;

    public Task<ConnectionTestResult> TestConnectionAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConnectionTestResult(true, "1min.ai has no documented lightweight connectivity check; the connection will be validated on first use.", TryGetHost(connection.BaseUrl), false));

    public Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Model discovery is not available for 1min.ai: no model-listing endpoint is documented. The connection can still be saved and used manually.");

    public async Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, GenerationSettings? settings = null, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one text result must be requested.");
        if (sourceImages is { Count: > 0 })
        {
            throw new ProviderAdapterException("Image-conditioned text generation is not supported for 1min.ai: its attachment upload format was not confirmed by live testing.");
        }

        var effectivePrompt = string.IsNullOrWhiteSpace(systemInstructions) ? prompt : $"{systemInstructions}\n\n{prompt}";
        var texts = new List<string>(resultCount);
        for (var index = 0; index < resultCount; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "api/chat-with-ai"));
            OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
            OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
            request.Content = new StringContent(BuildChatRequestBody(model.ProviderModelId, effectivePrompt), Encoding.UTF8, "application/json");
            var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (!isSuccess) throw new ProviderAdapterException(DescribeChatFailure(body, statusCode));
            texts.Add(ParseChatResultText(body));
        }

        return new TextGenerationResult(texts, null, null);
    }

    public async Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one image result must be requested.");
        var results = new List<byte[]>(resultCount);
        for (var index = 0; index < resultCount; index++)
        {
            var temporaryUrl = await SubmitFeatureAsync(connection, apiKey, "IMAGE_GENERATOR", model.ProviderModelId, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("prompt", prompt);
                writer.WriteNumber("samples", 1);
                writer.WriteString("size", ConfirmedImageSize);
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            results.Add(await DownloadTemporaryUrlAsync(temporaryUrl, "image/", connection, apiKey, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one audio result must be requested.");
        var results = new List<byte[]>(resultCount);
        for (var index = 0; index < resultCount; index++)
        {
            var temporaryUrl = await SubmitFeatureAsync(connection, apiKey, "TEXT_TO_SPEECH", model.ProviderModelId, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("text", prompt);
                writer.WriteString("voice", DefaultAudioVoice);
                writer.WriteString("response_format", "mp3");
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            results.Add(await DownloadTemporaryUrlAsync(temporaryUrl, "audio/", connection, apiKey, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not yet implemented for 1min.ai: its default behavior is a synchronous request that blocks for the full render, which does not fit this app's submit-then-poll job model, and the alternative asynchronous path was never confirmed against a live call.");

    public Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not yet implemented for 1min.ai: its default behavior is a synchronous request that blocks for the full render, which does not fit this app's submit-then-poll job model, and the alternative asynchronous path was never confirmed against a live call.");

    /// <summary>Submits one <c>POST /api/features</c> request and returns the completed job's
    /// <c>temporaryUrl</c>. Always omits <c>async</c> (defaults to the confirmed synchronous
    /// behavior) so the call either returns a completed result or throws — there is no pending state
    /// to hand back to a caller.</summary>
    private async Task<string> SubmitFeatureAsync(Connection connection, string? apiKey, string featureType, string providerModelId, Action<Utf8JsonWriter> writePromptObject, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "api/features"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        request.Content = new StringContent(BuildFeatureRequestBody(featureType, providerModelId, writePromptObject), Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(DescribeFeatureFailure(body, statusCode));

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("aiRecord", out var aiRecord) || aiRecord.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderAdapterException("The provider's response did not include an aiRecord.");
            }

            var status = aiRecord.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() : null;
            if (!string.Equals(status, "SUCCESS", StringComparison.Ordinal))
            {
                throw new ProviderAdapterException($"The provider reported the {featureType} request as '{status ?? "unknown"}' rather than completing synchronously.");
            }

            var temporaryUrl = aiRecord.TryGetProperty("temporaryUrl", out var urlElement) && urlElement.ValueKind == JsonValueKind.String ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(temporaryUrl)) throw new ProviderAdapterException("The provider completed the request but did not return a result URL.");
            return temporaryUrl;
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's response was not valid JSON.");
        }
    }

    private static string ParseChatResultText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("aiRecord", out var aiRecord) || aiRecord.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderAdapterException("The provider's chat response did not include an aiRecord.");
            }

            var status = aiRecord.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() : null;
            if (!string.Equals(status, "SUCCESS", StringComparison.Ordinal))
            {
                throw new ProviderAdapterException($"The provider reported the chat request as '{status ?? "unknown"}'.");
            }

            if (!aiRecord.TryGetProperty("aiRecordDetail", out var detail) || detail.ValueKind != JsonValueKind.Object ||
                !detail.TryGetProperty("resultObject", out var resultObject) || resultObject.ValueKind != JsonValueKind.Array ||
                resultObject.GetArrayLength() == 0)
            {
                throw new ProviderAdapterException("The provider's chat response did not include a result.");
            }

            var first = resultObject[0];
            if (first.ValueKind != JsonValueKind.String) throw new ProviderAdapterException("The provider's chat response result was not text.");
            return first.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's chat response was not valid JSON.");
        }
    }

    /// <summary>1min.ai's chat endpoint reports errors as
    /// <c>{"success":false,"error":{"code":...,"message":...}}</c> (per its docs). Falls back to the
    /// generic status-code description when the body isn't that shape.</summary>
    private static string DescribeChatFailure(string body, HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.Object &&
                errorElement.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String &&
                messageElement.GetString() is { Length: > 0 } message)
            {
                return message;
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic description below.
        }

        return OpenAiCompatibleProtocol.DescribeFailure(statusCode);
    }

    /// <summary>1min.ai's feature endpoint reports errors as a top-level
    /// <c>{"errorCode":"...","message":"..."}</c> (confirmed live — see
    /// docs/developer/1minai-contract.md's Flux Schnell rejection example), not nested under an
    /// "error" object like chat's. Checks the feature shape first, then the chat shape defensively,
    /// then falls back to the generic status-code description.</summary>
    private static string DescribeFeatureFailure(string body, HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String &&
                messageElement.GetString() is { Length: > 0 } message)
            {
                var code = root.TryGetProperty("errorCode", out var codeElement) && codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString() : null;
                return string.IsNullOrEmpty(code) ? message : $"{code}: {message}";
            }

            if (root.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.Object &&
                errorElement.TryGetProperty("message", out var nestedMessage) &&
                nestedMessage.ValueKind == JsonValueKind.String &&
                nestedMessage.GetString() is { Length: > 0 } nested)
            {
                return nested;
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic description below.
        }

        return OpenAiCompatibleProtocol.DescribeFailure(statusCode);
    }

    private async Task<byte[]> DownloadTemporaryUrlAsync(string temporaryUrl, string allowedMediaTypePrefix, Connection connection, string? apiKey, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(temporaryUrl, UriKind.Absolute, out var currentUri))
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
                if (!IsAllowedResultMediaType(mediaType, allowedMediaTypePrefix))
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

    private static bool IsAllowedResultMediaType(string? mediaType, string allowedPrefix) =>
        string.IsNullOrWhiteSpace(mediaType) ||
        mediaType.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);

    private static bool IsConnectionOrigin(string connectionBaseUrl, Uri resultUri) =>
        Uri.TryCreate(connectionBaseUrl, UriKind.Absolute, out var connectionUri) &&
        string.Equals(connectionUri.Scheme, resultUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(connectionUri.Host, resultUri.Host, StringComparison.OrdinalIgnoreCase) &&
        connectionUri.Port == resultUri.Port;

    private static string BuildChatRequestBody(string providerModelId, string prompt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "UNIFY_CHAT_WITH_AI");
            writer.WriteString("model", providerModelId);
            writer.WriteStartObject("promptObject");
            writer.WriteString("prompt", prompt);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildFeatureRequestBody(string featureType, string providerModelId, Action<Utf8JsonWriter> writePromptObject)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", featureType);
            writer.WriteString("model", providerModelId);
            writer.WritePropertyName("promptObject");
            writePromptObject(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? TryGetHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
