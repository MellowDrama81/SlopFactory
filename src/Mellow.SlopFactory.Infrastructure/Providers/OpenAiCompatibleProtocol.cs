using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

internal static class OpenAiCompatibleProtocol
{
    public static string CombineUrl(string baseUrl, string relativePath) => $"{baseUrl}/{relativePath.TrimStart('/')}";

    private const int MaxRetryAttempts = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    // allowRetry must only be set for idempotent requests (model listing). A generation-submission request is never
    // safe to retry automatically without provider-confirmed idempotency-key support, which this application does
    // not implement, so its failures are surfaced on the first attempt.
    public static async Task<(bool IsSuccess, HttpStatusCode StatusCode, string Body)> SendAsync(HttpClient httpClient, HttpRequestMessage request, Connection connection, CancellationToken cancellationToken, bool allowRetry = false, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        var timeoutSeconds = connection.TimeoutSeconds ?? LibraryRules.DefaultConnectionTimeoutSeconds;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            var currentRequest = request;
            for (var attempt = 0; ; attempt++)
            {
                using var response = await httpClient.SendAsync(currentRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                RecordRateLimitObservation(rateLimitTracker, connection, response);
                var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

                if (!allowRetry || response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxRetryAttempts)
                {
                    return (response.IsSuccessStatusCode, response.StatusCode, body);
                }

                await Task.Delay(ComputeRetryDelay(response, attempt), linkedCts.Token).ConfigureAwait(false);
                currentRequest = await CloneRequestAsync(request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProviderAdapterException($"The request timed out after {timeoutSeconds} seconds.");
        }
    }

    /// <summary>
    /// Best-effort capture of the OpenAI-documented <c>x-ratelimit-*</c> headers (see
    /// <see cref="RateLimitHeaderParser"/>) — silently a no-op when no tracker was supplied or none
    /// of the expected headers are present, since not every OpenAI-compatible provider is confirmed
    /// to emit them.
    /// </summary>
    private static void RecordRateLimitObservation(IConnectionRateLimitTracker? rateLimitTracker, Connection connection, HttpResponseMessage response)
    {
        if (rateLimitTracker is null) return;
        var headers = response.Headers.ToDictionary(header => header.Key, header => header.Value.FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        if (RateLimitHeaderParser.TryParse(headers, DateTimeOffset.UtcNow) is { } observation)
        {
            rateLimitTracker.Record(connection.Id, observation);
        }
    }

    /// <summary>
    /// Like <see cref="SendAsync"/> but reads the response as raw bytes rather than a decoded string —
    /// required for binary responses (audio synthesis, video/image downloads) where
    /// <see cref="HttpContent.ReadAsStringAsync()"/> would corrupt non-UTF-8 bytes.
    /// <paramref name="allowRetry"/> follows the exact same rule as <see cref="SendAsync"/>: only set
    /// it for a request with no side effect if repeated (a result download, never a paid generation
    /// call like audio synthesis, which stays retry-free without a confirmed idempotency key).
    /// </summary>
    public static async Task<(bool IsSuccess, HttpStatusCode StatusCode, byte[] Bytes)> SendForBytesAsync(HttpClient httpClient, HttpRequestMessage request, Connection connection, CancellationToken cancellationToken, bool allowRetry = false, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        var timeoutSeconds = connection.TimeoutSeconds ?? LibraryRules.DefaultConnectionTimeoutSeconds;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            var currentRequest = request;
            for (var attempt = 0; ; attempt++)
            {
                using var response = await httpClient.SendAsync(currentRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                RecordRateLimitObservation(rateLimitTracker, connection, response);

                if (!allowRetry || response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxRetryAttempts)
                {
                    var bytes = await ReadResponseBytesAsync(response.Content, linkedCts.Token).ConfigureAwait(false);
                    return (response.IsSuccessStatusCode, response.StatusCode, bytes);
                }

                await Task.Delay(ComputeRetryDelay(response, attempt), linkedCts.Token).ConfigureAwait(false);
                currentRequest = await CloneRequestAsync(request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProviderAdapterException($"The request timed out after {timeoutSeconds} seconds.");
        }
    }

    /// <summary>Raw-byte variant that preserves an explicit redirect location for callers that must
    /// validate each target rather than allowing the HTTP handler to follow it implicitly.</summary>
    public static async Task<(bool IsSuccess, HttpStatusCode StatusCode, byte[] Bytes, Uri? RedirectLocation, string? MediaType, IReadOnlyList<string> DigestHeaders)> SendForBytesWithRedirectAsync(HttpClient httpClient, HttpRequestMessage request, Connection connection, CancellationToken cancellationToken, bool allowRetry = false, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        var timeoutSeconds = connection.TimeoutSeconds ?? LibraryRules.DefaultConnectionTimeoutSeconds;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            var currentRequest = request;
            for (var attempt = 0; ; attempt++)
            {
                using var response = await httpClient.SendAsync(currentRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                RecordRateLimitObservation(rateLimitTracker, connection, response);
                if (!allowRetry || response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxRetryAttempts)
                {
                    var bytes = await ReadResponseBytesAsync(response.Content, linkedCts.Token).ConfigureAwait(false);
                    var digests = response.Headers.TryGetValues("Content-Digest", out var contentDigest)
                        ? contentDigest.Concat(response.Headers.TryGetValues("Digest", out var digest) ? digest : []).ToArray()
                        : response.Headers.TryGetValues("Digest", out var legacyDigest) ? legacyDigest.ToArray() : [];
                    return (response.IsSuccessStatusCode, response.StatusCode, bytes, response.Headers.Location, response.Content.Headers.ContentType?.MediaType, digests);
                }
                await Task.Delay(ComputeRetryDelay(response, attempt), linkedCts.Token).ConfigureAwait(false);
                currentRequest = await CloneRequestAsync(request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProviderAdapterException($"The request timed out after {timeoutSeconds} seconds.");
        }
    }

    private static TimeSpan ComputeRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta) return ClampRetryDelay(delta);
            if (retryAfter.Date is { } date) return ClampRetryDelay(date - DateTimeOffset.UtcNow);
        }

        var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        return ClampRetryDelay(baseDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)));
    }

    internal static async Task<byte[]> ReadResponseBytesAsync(HttpContent content, CancellationToken cancellationToken, long maximumBytes = LibraryRules.MaximumProviderResultBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new ProviderAdapterException($"The provider result exceeds the {maximumBytes / 1_048_576:N0} MiB download limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        long totalBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            totalBytes += read;
            if (totalBytes > maximumBytes)
            {
                throw new ProviderAdapterException($"The provider result exceeds the {maximumBytes / 1_048_576:N0} MiB download limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return destination.ToArray();
    }

    internal static void VerifySha256Digest(byte[] bytes, IReadOnlyList<string> digestHeaders)
    {
        foreach (var value in digestHeaders)
        {
            foreach (var item in value.Split(','))
            {
                var separator = item.IndexOf('=');
                if (separator <= 0 || !item[..separator].Trim().Equals("sha-256", StringComparison.OrdinalIgnoreCase)) continue;
                var encoded = item[(separator + 1)..].Trim();
                if (encoded.Length < 3 || encoded[0] != ':' || encoded[^1] != ':') throw new ProviderAdapterException("The provider supplied an invalid SHA-256 result digest.");
                try
                {
                    var expected = Convert.FromBase64String(encoded[1..^1]);
                    if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(bytes))) throw new ProviderAdapterException("The provider result did not match its SHA-256 digest.");
                    return;
                }
                catch (FormatException)
                {
                    throw new ProviderAdapterException("The provider supplied an invalid SHA-256 result digest.");
                }
            }
        }
    }

    private static TimeSpan ClampRetryDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero) return TimeSpan.Zero;
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    public static void ApplyAuthorization(HttpRequestMessage request, Connection connection, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return;
        var value = string.IsNullOrEmpty(connection.AuthPrefix) ? apiKey : $"{connection.AuthPrefix} {apiKey}";
        request.Headers.TryAddWithoutValidation(connection.CredentialHeaderName, value);
    }

    public static void ApplyAdditionalHeaders(HttpRequestMessage request, Connection connection)
    {
        if (connection.AdditionalHeaders is null) return;
        foreach (var header in connection.AdditionalHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }
    }

    public static IReadOnlyList<ProviderModelInfo> ParseModelList(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var dataElement = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
                : throw new ProviderAdapterException("The provider's model list response was not in the expected shape.");

            var results = new List<ProviderModelInfo>();
            foreach (var entry in dataElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String) continue;
                var id = idElement.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                string? label = null;
                if (entry.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String) label = nameElement.GetString();
                results.Add(new ProviderModelInfo(id, label, ParseModelPricing(entry)));
            }

            return results;
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's model list response was not valid JSON.");
        }
    }

    /// <summary>Parses a model-list entry's optional <c>pricing</c> object (confirmed live for
    /// OpenRouter: <c>{"prompt":"0.00000045","completion":"0.0000032",...}</c>, decimal-string
    /// USD-per-token values). No other adapter's real <c>/models</c> response documents this field,
    /// so an entry without it — including every OpenAI/generic-compatible/DeepInfra response —
    /// simply yields <see langword="null"/> here. The USD assumption mirrors the already-confirmed
    /// one <c>ParseCost</c> makes for OpenRouter's actual-cost reporting (OpenRouter's FAQ: "the base
    /// currency is US dollars").</summary>
    private static ProviderModelPricing? ParseModelPricing(JsonElement entry)
    {
        if (!entry.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object) return null;
        if (!TryGetDecimalString(pricing, "prompt", out var promptCost)) return null;
        if (!TryGetDecimalString(pricing, "completion", out var completionCost)) return null;
        return new ProviderModelPricing(promptCost, completionCost, "USD");

        static bool TryGetDecimalString(JsonElement obj, string propertyName, out decimal value)
        {
            value = 0;
            return obj.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
                && decimal.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>
    /// Rejects a non-empty <paramref name="systemInstructions"/> value outright when
    /// <paramref name="model"/> doesn't declare <see cref="Model.SupportsSystemInstructions"/>,
    /// rather than silently forwarding it into the request body. The GUI (<c>Generate.razor</c>)
    /// already nulls this field out before submission for a non-supporting model, but that is a
    /// single call site's own discipline, not a guarantee — this is the actual wire boundary, and the
    /// one place every current and future caller of <see cref="IProviderAdapter.GenerateTextAsync"/>
    /// passes through, so it is where a capability violation must be caught for certain.
    /// </summary>
    public static void ValidateSystemInstructionsSupported(Model model, string? systemInstructions)
    {
        if (!string.IsNullOrWhiteSpace(systemInstructions) && !model.SupportsSystemInstructions)
        {
            throw new ProviderAdapterException($"The model '{model.Label}' does not support system instructions.");
        }
    }

    public static string BuildChatCompletionRequestBody(string providerModelId, string prompt, int resultCount, string? systemInstructions = null, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, GenerationSettings? settings = null)
    {
        var normalizedSettings = LibraryRules.ValidateGenerationSettings(settings ?? GenerationSettings.Empty);
        sourceImages ??= [];
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteNumber("n", resultCount);
            if (normalizedSettings.Temperature is { } temperature) writer.WriteNumber("temperature", temperature);
            if (normalizedSettings.TopP is { } topP) writer.WriteNumber("top_p", topP);
            if (normalizedSettings.MaxTokens is { } maxTokens) writer.WriteNumber("max_tokens", maxTokens);
            if (normalizedSettings.FrequencyPenalty is { } frequencyPenalty) writer.WriteNumber("frequency_penalty", frequencyPenalty);
            if (normalizedSettings.PresencePenalty is { } presencePenalty) writer.WriteNumber("presence_penalty", presencePenalty);
            if (normalizedSettings.AdvancedJson is { } advancedJson)
            {
                using var advanced = JsonDocument.Parse(advancedJson);
                foreach (var property in advanced.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteStartArray("messages");
            if (!string.IsNullOrWhiteSpace(systemInstructions))
            {
                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", systemInstructions);
                writer.WriteEndObject();
            }
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            if (sourceImages.Count == 0)
            {
                writer.WriteString("content", prompt);
            }
            else
            {
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", prompt);
                writer.WriteEndObject();
                foreach (var image in sourceImages)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WriteStartObject("image_url");
                    writer.WriteString("url", $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static TextGenerationResult ParseChatCompletionResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderAdapterException("The provider's chat completion response was not in the expected shape.");
            }

            var results = new List<string>();
            var safetyBlockedCount = 0;
            // Response order, one entry per choice that was either safety-blocked or produced usable
            // text — the same condition set as results/safetyBlockedCount above, so a malformed choice
            // (no message/content) stays silently ignored exactly as before, never a fabricated
            // candidate. Lets a safety-blocked candidate keep a stable per-position identity in
            // GenerationRecord.Results instead of only contributing to the aggregate count.
            var candidates = new List<TextGenerationCandidate>();
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object) continue;
                var isSafetyBlocked = choice.TryGetProperty("finish_reason", out var finishReasonElement)
                    && finishReasonElement.ValueKind == JsonValueKind.String
                    && finishReasonElement.GetString() == "content_filter";
                if (isSafetyBlocked)
                {
                    safetyBlockedCount++;
                    candidates.Add(new TextGenerationCandidate(SafetyBlocked: true, Text: null));
                    continue;
                }
                if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.String) continue;
                var text = contentElement.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    results.Add(text);
                    candidates.Add(new TextGenerationCandidate(SafetyBlocked: false, Text: text));
                }
            }

            if (results.Count == 0 && safetyBlockedCount == 0) throw new ProviderAdapterException("The provider returned no usable text results.");

            int? promptTokens = null;
            int? completionTokens = null;
            if (document.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var promptTokensElement) && promptTokensElement.ValueKind == JsonValueKind.Number) promptTokens = promptTokensElement.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var completionTokensElement) && completionTokensElement.ValueKind == JsonValueKind.Number) completionTokens = completionTokensElement.GetInt32();
            }

            return new TextGenerationResult(results, promptTokens, completionTokens, safetyBlockedCount, candidates);
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's chat completion response was not valid JSON.");
        }
    }

    public static string BuildImageGenerationRequestBody(string providerModelId, string prompt, int resultCount)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteString("prompt", prompt);
            writer.WriteNumber("n", resultCount);
            writer.WriteString("response_format", "b64_json");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Builds the multipart/form-data body for OpenAI's confirmed <c>POST /images/edits</c> contract
    /// (image-to-image editing): repeated <c>image</c> file fields (one per source image), plus
    /// <c>prompt</c>/<c>model</c>/<c>n</c>/<c>response_format</c> fields — the same fields
    /// <see cref="BuildImageGenerationRequestBody"/> sends as JSON for the no-source-image
    /// <c>images/generations</c> path. The response shape (<c>data[].b64_json</c>) is identical, so
    /// <see cref="ParseImageGenerationBytes"/> handles both.
    /// </summary>
    public static MultipartFormDataContent BuildImageEditMultipartContent(string providerModelId, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage> sourceImages)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(providerModelId), "model" },
            { new StringContent(prompt), "prompt" },
            { new StringContent(resultCount.ToString(System.Globalization.CultureInfo.InvariantCulture)), "n" },
            { new StringContent("b64_json"), "response_format" }
        };
        foreach (var image in sourceImages)
        {
            var imageContent = new ByteArrayContent(image.Bytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image.MediaType);
            content.Add(imageContent, "image", $"source{ImageFileExtension(image.MediaType)}");
        }

        return content;
    }

    private static string ImageFileExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".bin"
    };

    public static IReadOnlyList<byte[]> ParseImageGenerationBytes(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderAdapterException("The provider's image generation response was not in the expected shape.");
            }

            var results = new List<byte[]>();
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("b64_json", out var encoded) || encoded.ValueKind != JsonValueKind.String) continue;
                var value = encoded.GetString();
                if (string.IsNullOrEmpty(value)) continue;
                try
                {
                    results.Add(Convert.FromBase64String(value));
                }
                catch (FormatException)
                {
                    throw new ProviderAdapterException("The provider returned an image result that was not valid base64 data.");
                }
            }

            if (results.Count == 0) throw new ProviderAdapterException("The provider returned no usable image results.");
            return results;
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's image generation response was not valid JSON.");
        }
    }

    public static string DescribeFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => "Authentication failed. Check the API key.",
        HttpStatusCode.Forbidden => "The request was forbidden by the provider.",
        HttpStatusCode.NotFound => "The model listing endpoint was not found at this base URL.",
        HttpStatusCode.TooManyRequests => "The provider is rate limiting requests.",
        _ when (int)statusCode >= 500 => "The provider reported a server error.",
        _ => $"The provider returned an unexpected response ({(int)statusCode})."
    };
}
