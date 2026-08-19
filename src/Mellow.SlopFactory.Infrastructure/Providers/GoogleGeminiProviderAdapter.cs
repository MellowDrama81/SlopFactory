using System.Net;
using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// Google Gemini (`generativelanguage.googleapis.com/v1beta`) is not OpenAI-shaped: the model ID is
/// embedded in the URL path (`POST /v1beta/models/{id}:generateContent`), not the request body; the
/// request uses `contents`/`systemInstruction`/`generationConfig` rather than `messages`; and auth is
/// an `x-goog-api-key` header. Unlike Anthropic/Cohere, `generationConfig.candidateCount` genuinely
/// supports multiple results in one call, so <paramref name="resultCount"/> in
/// <see cref="GenerateTextAsync"/> maps directly onto it rather than needing a request-per-result loop.
/// None of these shapes were exercised against a live account — this adapter follows Google's public
/// API reference only.
/// <para>
/// Text, Image and Audio are implemented. Image generation uses Imagen's
/// `POST /v1beta/models/{id}:predict` endpoint (a genuinely different API family from
/// `generateContent`, sharing only the base URL/auth) — see <see cref="GenerateImageAsync"/>'s
/// remarks. Audio (text-to-speech) reuses `generateContent` itself with
/// `generationConfig.responseModalities:["AUDIO"]` and a `speechConfig` — see
/// <see cref="GenerateAudioAsync"/>'s remarks, including the raw-PCM-to-WAV wrapping it does that
/// Imagen/text don't need. Video (Veo) remains unimplemented: it is a genuinely different, asynchronous
/// long-running-operation API (`predictLongRunning` + operation polling) whose exact response envelope
/// this pass did not have enough confidence in to guess at for something this consequential to get
/// wrong — see providers.md, which flags Gemini overall as "really 3-4 separate integrations bundled
/// under one account." Gemini's `generateContent` input also supports inline image data (`inlineData`
/// parts) for vision, but this adapter does not translate <see cref="TextGenerationSourceImage"/> into
/// that shape in this pass — see <see cref="Domain.LibraryRules.GetInputSlotCapabilities"/>'s remarks,
/// which withhold the Text-mode reference-image slot for this provider specifically. Imagen's own
/// reference-image/edit variants are not implemented either — only plain text-to-image.
/// </para>
/// </summary>
internal sealed class GoogleGeminiProviderAdapter : IProviderAdapter
{
    private const string ModelPathPrefix = "models/";

    private readonly HttpClient _httpClient;
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;

    public GoogleGeminiProviderAdapter(HttpClient httpClient, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _httpClient = httpClient;
        _rateLimitTracker = rateLimitTracker;
    }

    public ProviderType ProviderType => ProviderType.Gemini;

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

    /// <summary>Confirmed shape per Google's public API reference:
    /// <c>{"models":[{"name":"models/gemini-3.1-pro","displayName":"..."}]}</c>. The returned
    /// <c>name</c> carries a <c>models/</c> prefix — stripped here so the stored
    /// <see cref="ProviderModelInfo.ProviderModelId"/> is the bare ID, with
    /// <see cref="GenerateTextAsync"/> re-adding the prefix when it builds the path URL.</summary>
    public async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, GenerationMode? mode = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "models"));
        ApplyGeminiHeaders(request, connection, apiKey);
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
                var id = name.StartsWith(ModelPathPrefix, StringComparison.Ordinal) ? name[ModelPathPrefix.Length..] : name;
                if (id.Length == 0) continue;
                string? label = entry.TryGetProperty("displayName", out var labelElement) && labelElement.ValueKind == JsonValueKind.String ? labelElement.GetString() : null;
                results.Add(new ProviderModelInfo(id, label));
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
            throw new ProviderAdapterException("Reference-image input is not implemented for Gemini in this app yet, even though generateContent supports it.");
        }

        var normalizedSettings = LibraryRules.ValidateGenerationSettings(settings ?? GenerationSettings.Empty);
        var path = $"{ModelPathPrefix}{model.ProviderModelId}:generateContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, path));
        ApplyGeminiHeaders(request, connection, apiKey);
        request.Content = new StringContent(BuildGenerateContentRequestBody(prompt, resultCount, systemInstructions, normalizedSettings), Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));
        return ParseGenerateContentResult(body);
    }

    /// <summary>Confirmed shape per Google's public Imagen API reference:
    /// <c>POST /v1beta/models/{id}:predict</c>, body <c>{"instances":[{"prompt":"..."}],
    /// "parameters":{"sampleCount":N}}</c>, response
    /// <c>{"predictions":[{"bytesBase64Encoded":"...","mimeType":"image/png"}]}</c>. Text-to-image
    /// only — Imagen's reference-image/edit request variants are not implemented, matching this
    /// adapter's Text-mode decision not to translate <see cref="TextGenerationSourceImage"/> into
    /// Google's content shape yet.</summary>
    public async Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one image result must be requested.");
        if (sourceImages is { Count: > 0 })
        {
            throw new ProviderAdapterException("Reference-image editing is not implemented for Gemini's Imagen; only plain text-to-image generation is supported.");
        }

        var path = $"{ModelPathPrefix}{model.ProviderModelId}:predict";
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, path));
        ApplyGeminiHeaders(request, connection, apiKey);
        request.Content = new StringContent(BuildPredictRequestBody(prompt, resultCount), Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));
        return ParsePredictImageBytes(body);
    }

    /// <summary>
    /// Confirmed real feature per Google's public API reference: Gemini TTS reuses `generateContent`
    /// itself (not a separate endpoint) with `generationConfig.responseModalities:["AUDIO"]` and a
    /// `speechConfig.voiceConfig.prebuiltVoiceConfig.voiceName`. There is no confirmed candidate-count
    /// behavior for audio responses, so <paramref name="resultCount"/> means one independent request
    /// per result, like <see cref="GenerateTextAsync"/>'s Anthropic/Cohere counterparts. The response's
    /// `inlineData` carries <b>raw 16-bit PCM audio</b>, not a self-describing container (typically
    /// declared as <c>audio/L16;rate=24000</c> or similar) — <see cref="WrapPcmAsWav"/> wraps it in a
    /// minimal WAV header, parsing the sample rate out of the declared MIME type when present (falling
    /// back to Gemini's documented default, 24 kHz mono), so the returned bytes are an actually
    /// playable/saveable file rather than a raw sample dump this app's media detection couldn't
    /// recognize.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? voice = null, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one audio result must be requested.");
        var path = $"{ModelPathPrefix}{model.ProviderModelId}:generateContent";
        var results = new List<byte[]>(resultCount);
        for (var index = 0; index < resultCount; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, path));
            ApplyGeminiHeaders(request, connection, apiKey);
            request.Content = new StringContent(BuildSpeechGenerationRequestBody(prompt, voice), Encoding.UTF8, "application/json");
            var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));
            results.Add(ParseSpeechAudioBytes(body));
        }

        return results;
    }

    // Veo's request/response shape (predictLongRunning + operation polling, with a video file reference
    // in the completed operation's response body) was not confident enough to guess at field-for-field
    // for something this consequential to get wrong — unlike Text/Image/Audio above, which all reuse
    // shapes with well-established, stable field names.
    public Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not implemented for Gemini: Veo is a separate, asynchronous API family from generateContent, not covered by this adapter.");

    public Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not implemented for Gemini: Veo is a separate, asynchronous API family from generateContent, not covered by this adapter.");

    private static void ApplyGeminiHeaders(HttpRequestMessage request, Connection connection, string? apiKey)
    {
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
    }

    private static string BuildGenerateContentRequestBody(string prompt, int resultCount, string? systemInstructions, GenerationSettings settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(systemInstructions))
            {
                writer.WriteStartObject("systemInstruction");
                writer.WriteStartArray("parts");
                writer.WriteStartObject();
                writer.WriteString("text", systemInstructions);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteStartArray("contents");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("parts");
            writer.WriteStartObject();
            writer.WriteString("text", prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("generationConfig");
            writer.WriteNumber("candidateCount", resultCount);
            if (settings.Temperature is { } temperature) writer.WriteNumber("temperature", temperature);
            if (settings.TopP is { } topP) writer.WriteNumber("topP", topP);
            if (settings.MaxTokens is { } maxTokens) writer.WriteNumber("maxOutputTokens", maxTokens);
            writer.WriteEndObject();
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

    private static string BuildPredictRequestBody(string prompt, int resultCount)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("instances");
            writer.WriteStartObject();
            writer.WriteString("prompt", prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("parameters");
            writer.WriteNumber("sampleCount", resultCount);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private const string DefaultVoiceName = "Kore";

    private static string BuildSpeechGenerationRequestBody(string prompt, string? voice)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("contents");
            writer.WriteStartObject();
            writer.WriteStartArray("parts");
            writer.WriteStartObject();
            writer.WriteString("text", prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("generationConfig");
            writer.WriteStartArray("responseModalities");
            writer.WriteStringValue("AUDIO");
            writer.WriteEndArray();
            writer.WriteStartObject("speechConfig");
            writer.WriteStartObject("voiceConfig");
            writer.WriteStartObject("prebuiltVoiceConfig");
            writer.WriteString("voiceName", string.IsNullOrWhiteSpace(voice) ? DefaultVoiceName : voice);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static byte[] ParseSpeechAudioBytes(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            {
                throw new ProviderAdapterException("The provider's speech response was not in the expected shape.");
            }

            var firstCandidate = candidates[0];
            if (!firstCandidate.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object ||
                !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderAdapterException("The provider's speech response did not include audio content.");
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("inlineData", out var inlineData) || inlineData.ValueKind != JsonValueKind.Object) continue;
                if (!inlineData.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.String || dataElement.GetString() is not { Length: > 0 } base64) continue;
                var mimeType = inlineData.TryGetProperty("mimeType", out var mimeTypeElement) && mimeTypeElement.ValueKind == JsonValueKind.String ? mimeTypeElement.GetString() : null;
                byte[] pcm;
                try
                {
                    pcm = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    throw new ProviderAdapterException("The provider returned audio that was not valid base64.");
                }

                return WrapPcmAsWav(pcm, ExtractSampleRate(mimeType));
            }

            throw new ProviderAdapterException("The provider's speech response did not include usable audio data.");
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's speech response was not valid JSON.");
        }
    }

    /// <summary>Gemini's declared audio MIME type looks like <c>audio/L16;codec=pcm;rate=24000</c> —
    /// extracts the <c>rate=</c> value, falling back to Gemini's documented default (24 kHz) when the
    /// field is absent or unparsable.</summary>
    private static int ExtractSampleRate(string? mimeType)
    {
        const int defaultSampleRate = 24000;
        if (string.IsNullOrEmpty(mimeType)) return defaultSampleRate;
        var rateIndex = mimeType.IndexOf("rate=", StringComparison.OrdinalIgnoreCase);
        if (rateIndex < 0) return defaultSampleRate;
        var start = rateIndex + "rate=".Length;
        var end = start;
        while (end < mimeType.Length && char.IsDigit(mimeType[end])) end++;
        return end > start && int.TryParse(mimeType.AsSpan(start, end - start), out var parsed) ? parsed : defaultSampleRate;
    }

    /// <summary>Wraps raw 16-bit little-endian mono PCM samples in a minimal 44-byte canonical WAV
    /// header so the bytes are a self-describing, playable file rather than a sample dump this app's
    /// media-type detection (and any downstream player) couldn't otherwise recognize.</summary>
    private static byte[] WrapPcmAsWav(byte[] pcmData, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);

        using var stream = new MemoryStream(44 + pcmData.Length);
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + pcmData.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write("data"u8);
            writer.Write(pcmData.Length);
            writer.Write(pcmData);
        }

        return stream.ToArray();
    }

    private static List<byte[]> ParsePredictImageBytes(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("predictions", out var predictions) || predictions.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderAdapterException("The provider's predict response was not in the expected shape.");
            }

            var results = new List<byte[]>();
            foreach (var prediction in predictions.EnumerateArray())
            {
                if (prediction.ValueKind != JsonValueKind.Object || !prediction.TryGetProperty("bytesBase64Encoded", out var bytesElement) || bytesElement.ValueKind != JsonValueKind.String) continue;
                var base64 = bytesElement.GetString();
                if (string.IsNullOrEmpty(base64)) continue;
                try
                {
                    results.Add(Convert.FromBase64String(base64));
                }
                catch (FormatException)
                {
                    throw new ProviderAdapterException("The provider returned an image that was not valid base64.");
                }
            }

            if (results.Count == 0) throw new ProviderAdapterException("The provider returned no usable image results.");
            return results;
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's predict response was not valid JSON.");
        }
    }

    /// <summary>Confirmed shape per Google's public API reference:
    /// <c>{"candidates":[{"content":{"parts":[{"text":"..."}]},"finishReason":"STOP"}],
    /// "usageMetadata":{"promptTokenCount":N,"candidatesTokenCount":N}}</c>. A candidate whose
    /// <c>finishReason</c> is <c>SAFETY</c> is treated as safety-blocked, mirroring
    /// <see cref="OpenAiCompatibleProtocol.ParseChatCompletionResult"/>'s <c>content_filter</c>
    /// handling.</summary>
    private static TextGenerationResult ParseGenerateContentResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderAdapterException("The provider's generateContent response was not in the expected shape.");
            }

            var texts = new List<string>();
            var safetyBlockedCount = 0;
            var candidateResults = new List<TextGenerationCandidate>();
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object) continue;
                var isSafetyBlocked = candidate.TryGetProperty("finishReason", out var finishReasonElement)
                    && finishReasonElement.ValueKind == JsonValueKind.String
                    && finishReasonElement.GetString() == "SAFETY";
                if (isSafetyBlocked)
                {
                    safetyBlockedCount++;
                    candidateResults.Add(new TextGenerationCandidate(SafetyBlocked: true, Text: null));
                    continue;
                }

                if (!candidate.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Object ||
                    !contentElement.TryGetProperty("parts", out var partsElement) || partsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var builder = new StringBuilder();
                foreach (var part in partsElement.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String) continue;
                    builder.Append(textElement.GetString());
                }

                if (builder.Length > 0)
                {
                    texts.Add(builder.ToString());
                    candidateResults.Add(new TextGenerationCandidate(SafetyBlocked: false, Text: builder.ToString()));
                }
            }

            if (texts.Count == 0 && safetyBlockedCount == 0) throw new ProviderAdapterException("The provider returned no usable text results.");

            int? promptTokens = null;
            int? completionTokens = null;
            if (root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("promptTokenCount", out var promptTokensElement) && promptTokensElement.ValueKind == JsonValueKind.Number) promptTokens = promptTokensElement.GetInt32();
                if (usage.TryGetProperty("candidatesTokenCount", out var completionTokensElement) && completionTokensElement.ValueKind == JsonValueKind.Number) completionTokens = completionTokensElement.GetInt32();
            }

            return new TextGenerationResult(texts, promptTokens, completionTokens, safetyBlockedCount, candidateResults);
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's generateContent response was not valid JSON.");
        }
    }

    /// <summary>Google's documented error shape: <c>{"error":{"code":N,"message":"...","status":"..."}}</c>.</summary>
    private static string DescribeFailure(string body, HttpStatusCode statusCode)
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

    private static string? TryGetHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
