using System.Net;
using System.Text;
using System.Text.Json;
using System.Linq;
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
    public static async Task<(bool IsSuccess, HttpStatusCode StatusCode, string Body)> SendAsync(HttpClient httpClient, HttpRequestMessage request, Connection connection, CancellationToken cancellationToken, bool allowRetry = false)
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
                results.Add(new ProviderModelInfo(id, label));
            }

            return results;
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's model list response was not valid JSON.");
        }
    }

    public static string BuildChatCompletionRequestBody(string providerModelId, string prompt, int resultCount, string? systemInstructions = null, TextGenerationSourceImage? sourceImage = null, GenerationSettings? settings = null, TextGenerationSourceImage? secondarySourceImage = null, TextGenerationSourceImage? tertiarySourceImage = null)
    {
        TextGenerationSourceImage?[] sourceImages = [sourceImage, secondarySourceImage, tertiarySourceImage];
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteNumber("n", resultCount);
            if (settings?.Temperature is { } temperature) writer.WriteNumber("temperature", temperature);
            if (settings?.TopP is { } topP) writer.WriteNumber("top_p", topP);
            if (settings?.MaxTokens is { } maxTokens) writer.WriteNumber("max_tokens", maxTokens);
            if (settings?.FrequencyPenalty is { } frequencyPenalty) writer.WriteNumber("frequency_penalty", frequencyPenalty);
            if (settings?.PresencePenalty is { } presencePenalty) writer.WriteNumber("presence_penalty", presencePenalty);
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
            if (sourceImages.All(image => image is null))
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
                    if (image is null) continue;
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
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.String) continue;
                var text = contentElement.GetString();
                if (!string.IsNullOrEmpty(text)) results.Add(text);
            }

            if (results.Count == 0) throw new ProviderAdapterException("The provider returned no usable text results.");

            int? promptTokens = null;
            int? completionTokens = null;
            if (document.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var promptTokensElement) && promptTokensElement.ValueKind == JsonValueKind.Number) promptTokens = promptTokensElement.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var completionTokensElement) && completionTokensElement.ValueKind == JsonValueKind.Number) completionTokens = completionTokensElement.GetInt32();
            }

            return new TextGenerationResult(results, promptTokens, completionTokens);
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
