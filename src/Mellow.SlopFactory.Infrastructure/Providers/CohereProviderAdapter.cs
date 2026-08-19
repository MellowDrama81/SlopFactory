using System.Net;
using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// Cohere (`api.cohere.com/v1`) is not OpenAI-shaped: `POST /v1/chat` takes a single `message` string
/// plus a `chat_history` array (not a `messages` array), and a top-level `preamble` field for system
/// instructions — a clean fit for <see cref="Model.SupportsSystemInstructions"/>, sent as an empty
/// `chat_history` here since this app has no multi-turn chat concept. There is no candidate-count
/// request parameter, so <paramref name="resultCount"/> in <see cref="GenerateTextAsync"/> means one
/// independent request per result, same as <see cref="OneMinAiProviderAdapter"/>/
/// <see cref="AnthropicProviderAdapter"/>. None of these shapes were exercised against a live account —
/// this adapter follows Cohere's public v1 Chat API reference only.
/// <para>
/// Cohere offers no image-generation API. Audio is input-only (Transcribe/STT); there is no TTS/audio
/// output, so <see cref="GenerateAudioAsync"/> is not implemented. There is no video-generation API.
/// Cohere's chat request does accept image input via Aya Vision, but this adapter does not translate
/// <see cref="TextGenerationSourceImage"/> into that shape in this pass — see
/// <see cref="Domain.LibraryRules.GetInputSlotCapabilities"/>'s remarks, which withhold the Text-mode
/// reference-image slot for this provider specifically.
/// </para>
/// </summary>
internal sealed class CohereProviderAdapter : IProviderAdapter
{
    private readonly HttpClient _httpClient;
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;

    public CohereProviderAdapter(HttpClient httpClient, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _httpClient = httpClient;
        _rateLimitTracker = rateLimitTracker;
    }

    public ProviderType ProviderType => ProviderType.Cohere;

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

    /// <summary>Confirmed shape per Cohere's public API reference:
    /// <c>{"models":[{"name":"command-r-plus",...}]}</c>.</summary>
    public async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, GenerationMode? mode = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "models"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderAdapterException("The provider's model list response was not in the expected shape.");
            }

            var results = new List<ProviderModelInfo>();
            foreach (var entry in models.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) continue;
                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                results.Add(new ProviderModelInfo(name, null));
            }

            return results;
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's model list response was not valid JSON.");
        }
    }

    public async Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, GenerationSettings? settings = null, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one text result must be requested.");
        OpenAiCompatibleProtocol.ValidateSystemInstructionsSupported(model, systemInstructions);
        if (sourceImages is { Count: > 0 })
        {
            throw new ProviderAdapterException("Reference-image input is not implemented for Cohere in this app yet, even though Aya Vision supports it.");
        }

        var normalizedSettings = LibraryRules.ValidateGenerationSettings(settings ?? GenerationSettings.Empty);
        var texts = new List<string>(resultCount);
        for (var index = 0; index < resultCount; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "chat"));
            OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
            OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
            request.Content = new StringContent(BuildChatRequestBody(model.ProviderModelId, prompt, systemInstructions, normalizedSettings), Encoding.UTF8, "application/json");
            var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));
            texts.Add(ParseChatResultText(body));
        }

        return new TextGenerationResult(texts, null, null);
    }

    public Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Image generation is not available for Cohere: it offers no image-generation API.");

    public Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? voice = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Audio generation is not available for Cohere: Transcribe is input-only (speech-to-text); there is no text-to-speech output.");

    public Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not available for Cohere: it offers no video-generation API.");

    public Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not available for Cohere: it offers no video-generation API.");

    private static string BuildChatRequestBody(string providerModelId, string prompt, string? systemInstructions, GenerationSettings settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteString("message", prompt);
            writer.WriteStartArray("chat_history");
            writer.WriteEndArray();
            if (!string.IsNullOrWhiteSpace(systemInstructions)) writer.WriteString("preamble", systemInstructions);
            if (settings.Temperature is { } temperature) writer.WriteNumber("temperature", temperature);
            if (settings.TopP is { } topP) writer.WriteNumber("p", topP);
            if (settings.MaxTokens is { } maxTokens) writer.WriteNumber("max_tokens", maxTokens);
            if (settings.FrequencyPenalty is { } frequencyPenalty) writer.WriteNumber("frequency_penalty", frequencyPenalty);
            if (settings.PresencePenalty is { } presencePenalty) writer.WriteNumber("presence_penalty", presencePenalty);
            if (settings.AdvancedJson is { } advancedJson)
            {
                using var advanced = JsonDocument.Parse(advancedJson);
                foreach (var property in advanced.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Confirmed shape per Cohere's public v1 Chat API reference: <c>{"text":"...", ...}</c>.</summary>
    private static string ParseChatResultText(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String || textElement.GetString() is not { Length: > 0 } text)
            {
                throw new ProviderAdapterException("The provider's chat response did not include usable text.");
            }

            return text;
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's chat response was not valid JSON.");
        }
    }

    /// <summary>Cohere's documented error shape: <c>{"message":"..."}</c>.</summary>
    private static string DescribeFailure(string body, HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var messageElement) &&
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

    private static string? TryGetHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
