using System.Net;
using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// DeepInfra's OpenAI-compatible surface (https://api.deepinfra.com/v1/openai) covers chat, model
/// listing and image generation (including image editing) with the same request/response shapes and
/// relative paths as OpenAI's own API. Audio and video generation live
/// under a different absolute path root (https://api.deepinfra.com/v1/audio/speech,
/// https://api.deepinfra.com/v1/videos) rather than under the OpenAI-compatible base, so those two
/// operations build absolute request URIs from the connection's scheme/host/port instead of
/// <see cref="OpenAiCompatibleProtocol.CombineUrl"/>. Shapes confirmed by live API calls — see
/// docs/developer/deepinfra-audio-video-contract.md. Video generation is asynchronous
/// (submit-then-poll): a completed job's bytes are fetched from DeepInfra's own same-host
/// <c>/v1/videos/{id}/content</c> endpoint rather than the third-party CDN URL the poll response also
/// reports, which avoids trusting an arbitrary result host. Not every DeepInfra video model supports
/// this submit-then-poll job API — an unsupported model is surfaced as a normal provider error, since
/// there is no confirmed contract for the alternative synchronous endpoint some models require.
/// </summary>
internal sealed class DeepInfraProviderAdapter : IProviderAdapter
{
    private readonly HttpClient _httpClient;
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;

    public DeepInfraProviderAdapter(HttpClient httpClient, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _httpClient = httpClient;
        _rateLimitTracker = rateLimitTracker;
    }

    public ProviderType ProviderType => ProviderType.DeepInfra;

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

    /// <summary>Confirmed against DeepInfra's per-model API reference pages (e.g.
    /// https://deepinfra.com/black-forest-labs/FLUX.1-Kontext-dev/api,
    /// https://deepinfra.com/Qwen/Qwen-Image-Edit/api): <c>POST {base}/images/edits</c> exists under
    /// the same OpenAI-compatible base as <c>images/generations</c>, multipart/form-data with an
    /// <c>image</c> field, <c>model</c>/<c>n</c>/<c>size</c> — the identical shape OpenAI's own
    /// <c>images/edits</c> uses, so this reuses <see cref="OpenAiCompatibleProtocol.BuildImageEditMultipartContent"/>
    /// exactly like <see cref="OpenAiProviderAdapter"/>, repeating the <c>image</c> field once per source
    /// image the same way for every model — no per-model capability check happens here or in
    /// <see cref="LibraryRules.GetInputSlotCapabilities"/>.
    /// <para>
    /// Whether a given DeepInfra model actually uses more than one supplied image is <b>not
    /// guaranteed</b> and varies by model, live-tested (2026-08-19) by sending two distinctly-colored
    /// solid images in both orderings: <c>black-forest-labs/FLUX.1-Kontext-dev</c> silently discarded the
    /// first image every time (the second always won, never combined; an <c>image[]</c> array-style
    /// field name was also rejected outright with HTTP 422 rather than working as an alternative), while
    /// <c>black-forest-labs/FLUX-2-klein-9b</c> genuinely used both — order-sensitive output drawing on
    /// both source colors — but with real run-to-run result-quality variance confirmed in a later round
    /// with real-content images (same request repeated twice produced very different results). None of
    /// this is enforced or special-cased at the capability level: every DeepInfra image model is offered
    /// the same up-to-3 <c>ReferenceImage</c> slots as OpenAI/OpenRouter, and a model whose backend can't
    /// really use them either silently keeps only its last image (Kontext-dev's behavior) or surfaces a
    /// normal, already-handled provider error/quality issue — not something this adapter or the
    /// capability schema tries to predict per model ID.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, CancellationToken cancellationToken = default)
    {
        var hasSourceImages = sourceImages is { Count: > 0 };
        if (hasSourceImages)
        {
            var (isSuccess, statusCode, body) = await SendImageEditAsync(connection, model, apiKey, prompt, resultCount, sourceImages!, null, cancellationToken).ConfigureAwait(false);
            if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
            return OpenAiCompatibleProtocol.ParseImageGenerationBytes(body);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "images/generations"), UriKind.Absolute));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        request.Content = new StringContent(OpenAiCompatibleProtocol.BuildImageGenerationRequestBody(model.ProviderModelId, prompt, resultCount), Encoding.UTF8, "application/json");
        var (generationSucceeded, generationStatusCode, generationBody) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!generationSucceeded) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(generationStatusCode));
        return OpenAiCompatibleProtocol.ParseImageGenerationBytes(generationBody);
    }

    public async Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage>? sourceImages, TextGenerationSourceImage? mask, CancellationToken cancellationToken = default)
    {
        if (mask is null) return await GenerateImageAsync(connection, model, apiKey, prompt, resultCount, sourceImages, cancellationToken).ConfigureAwait(false);
        if (sourceImages is not { Count: > 0 }) throw new ProviderAdapterException("A mask requires a source image.");

        var (isSuccess, statusCode, body) = await SendImageEditAsync(connection, model, apiKey, prompt, resultCount, sourceImages, mask, cancellationToken).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
        return OpenAiCompatibleProtocol.ParseImageGenerationBytes(body);
    }

    private async Task<(bool IsSuccess, HttpStatusCode StatusCode, string Body)> SendImageEditAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage> sourceImages, TextGenerationSourceImage? mask, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "images/edits"), UriKind.Absolute));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        request.Content = OpenAiCompatibleProtocol.BuildImageEditMultipartContent(model.ProviderModelId, prompt, resultCount, sourceImages, mask);
        return await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? voice = null, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one audio result must be requested.");
        var results = new List<byte[]>(resultCount);
        for (var index = 0; index < resultCount; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildAbsoluteUri(connection.BaseUrl, "/v1/audio/speech"));
            OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
            OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
            request.Content = new StringContent(BuildAudioSpeechRequestBody(model.ProviderModelId, prompt, voice), Encoding.UTF8, "application/json");
            var (isSuccess, statusCode, bytes) = await OpenAiCompatibleProtocol.SendForBytesAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
            if (bytes.Length == 0) throw new ProviderAdapterException("The provider returned an empty audio result.");
            results.Add(bytes);
        }

        return results;
    }

    public async Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildAbsoluteUri(connection.BaseUrl, "/v1/videos"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        request.Content = new StringContent(BuildVideoSubmissionRequestBody(model.ProviderModelId, prompt, firstFrame), Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(DescribeDeepInfraFailure(body, statusCode));

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String || idElement.GetString() is not { Length: > 0 } jobId)
            {
                throw new ProviderAdapterException("The provider's video submission response did not include a job ID.");
            }

            return new AsyncGenerationSubmission(jobId);
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's video submission response was not valid JSON.");
        }
    }

    public async Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildAbsoluteUri(connection.BaseUrl, $"/v1/videos/{Uri.EscapeDataString(providerJobId)}"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(DescribeDeepInfraFailure(body, statusCode));

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() : null;
            switch (status)
            {
                case "succeeded":
                    try
                    {
                        var bytes = await DownloadVideoContentAsync(connection, apiKey, providerJobId, cancellationToken).ConfigureAwait(false);
                        if (bytes.Length == 0) throw new ProviderAdapterException("The provider returned an empty video result.");
                        return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Completed, new[] { bytes }, null);
                    }
                    catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                    {
                        // The provider itself confirmed completion — only the download failed, so this
                        // is retryable (the result may still be available) rather than a genuine
                        // provider-side failure. See AsyncGenerationPollOutcome.CompletedDownloadFailed.
                        return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.CompletedDownloadFailed, null, exception.Message);
                    }
                // "queued" is the only in-progress value DeepInfra's docs/live testing confirmed.
                // "processing" is not documented but treated as in-progress defensively, matching the
                // shape other job statuses in this codebase use; any other value (including a
                // genuinely unknown one) is treated as a terminal failure per
                // docs/developer/deepinfra-audio-video-contract.md, since a failure status string was
                // never directly observed and silently treating unknowns as still-processing could hang
                // forever.
                case "queued":
                case "processing":
                    return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Processing, null, null);
                default:
                    var errorMessage = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String ? errorElement.GetString() : null;
                    return new AsyncGenerationPollResult(AsyncGenerationPollOutcome.Failed, null, errorMessage ?? $"The provider reported an unexpected video status '{status}'.");
            }
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's video status response was not valid JSON.");
        }
    }

    private async Task<byte[]> DownloadVideoContentAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildAbsoluteUri(connection.BaseUrl, $"/v1/videos/{Uri.EscapeDataString(providerJobId)}/content", "variant=video"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var (isSuccess, statusCode, bytes) = await OpenAiCompatibleProtocol.SendForBytesAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException($"Downloading the completed video result failed: {OpenAiCompatibleProtocol.DescribeFailure(statusCode)}");
        return bytes;
    }

    /// <summary>
    /// Builds an absolute request URI rooted at the connection's scheme/host/port rather than at
    /// <c>connection.BaseUrl</c>'s path: DeepInfra's audio and video endpoints live under
    /// <c>/v1/audio/speech</c> and <c>/v1/videos</c>, a different path root than the
    /// <c>/v1/openai/...</c> base used for chat/image/models, so simple path concatenation onto
    /// <c>BaseUrl</c> would produce the wrong URL.
    /// </summary>
    private static Uri BuildAbsoluteUri(string baseUrl, string absolutePath, string? query = null)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ProviderAdapterException("The connection's base URL is not valid.");
        }

        var builder = new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port)
        {
            Path = absolutePath,
            Query = query ?? string.Empty,
        };
        return builder.Uri;
    }

    /// <summary>
    /// DeepInfra reports request errors as <c>{"error":{"message":...}}</c> (confirmed live — see
    /// docs/developer/deepinfra-audio-video-contract.md), which carries far more useful detail than
    /// the generic status-code description (e.g. naming exactly which model doesn't support the
    /// async video job API). Falls back to the generic description when the body isn't that shape.
    /// </summary>
    private static string DescribeDeepInfraFailure(string body, HttpStatusCode statusCode)
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

    private static string BuildAudioSpeechRequestBody(string providerModelId, string prompt, string? voice)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteString("input", prompt);
            writer.WriteString("response_format", "mp3");
            if (!string.IsNullOrWhiteSpace(voice)) writer.WriteString("voice", voice);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary><paramref name="firstFrame"/> maps to DeepInfra's documented optional
    /// <c>image_url</c> submit field (image-to-video), written as a <c>data:</c> URI — confirmed by
    /// docs/developer/deepinfra-audio-video-contract.md to accept either an <c>http(s)</c> URL or a
    /// <c>data:</c> URI, and matching the inline-data-URI convention this codebase already uses for
    /// chat image conditioning.</summary>
    private static string BuildVideoSubmissionRequestBody(string providerModelId, string prompt, TextGenerationSourceImage? firstFrame)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteString("prompt", prompt);
            if (firstFrame is not null)
            {
                writer.WriteString("image_url", $"data:{firstFrame.MediaType};base64,{Convert.ToBase64String(firstFrame.Bytes)}");
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? TryGetHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
