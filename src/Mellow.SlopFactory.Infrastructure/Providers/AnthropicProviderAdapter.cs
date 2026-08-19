using System.Net;
using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// Anthropic (`api.anthropic.com/v1`) is not OpenAI-shaped: chat lives at `POST /v1/messages`, auth is
/// an `x-api-key` header (not `Authorization: Bearer`) plus a required `anthropic-version` header this
/// adapter always sends (a protocol constant, not a user-configurable header), and the system prompt is
/// a top-level `system` field rather than a `role: "system"` message — a clean fit for
/// <see cref="Model.SupportsSystemInstructions"/>. There is no `n`/candidate-count request parameter,
/// so <paramref name="resultCount"/> in <see cref="GenerateTextAsync"/> means one independent request
/// per result, same as <see cref="OneMinAiProviderAdapter"/>. `max_tokens` is a <b>required</b> field
/// (unlike OpenAI's optional one) — <see cref="DefaultMaxTokens"/> is sent whenever
/// <see cref="GenerationSettings.MaxTokens"/> is not supplied. None of these shapes were exercised
/// against a live account — this adapter follows Anthropic's public API reference only.
/// <para>
/// No native image, audio, or video <b>generation</b> exists for Anthropic (confirmed in providers.md).
/// Anthropic's Messages API does accept image <b>input</b> (vision) via content blocks, but this
/// adapter does not translate <see cref="TextGenerationSourceImage"/> into that shape in this pass —
/// see <see cref="Domain.LibraryRules.GetInputSlotCapabilities"/>'s remarks, which withhold the
/// Text-mode reference-image slot for this provider specifically so the gap is invisible-by-design
/// rather than a silently dropped attachment.
/// </para>
/// </summary>
internal sealed class AnthropicProviderAdapter : IProviderAdapter
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxTokens = 4096;

    private readonly HttpClient _httpClient;
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;

    public AnthropicProviderAdapter(HttpClient httpClient, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _httpClient = httpClient;
        _rateLimitTracker = rateLimitTracker;
    }

    public ProviderType ProviderType => ProviderType.Anthropic;

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

    /// <summary>Confirmed shape per Anthropic's public API reference: <c>GET /v1/models</c> returns
    /// <c>{"data":[{"id":"claude-...","display_name":"..."}],...}</c> — a <c>display_name</c> field,
    /// not the <c>name</c> field <see cref="OpenAiCompatibleProtocol.ParseModelList"/> expects, so this
    /// adapter parses it directly rather than reusing that helper.</summary>
    public async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, GenerationMode? mode = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "models"));
        ApplyAnthropicHeaders(request, connection, apiKey);
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderAdapterException("The provider's model list response was not in the expected shape.");
            }

            var results = new List<ProviderModelInfo>();
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String) continue;
                var id = idElement.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                string? label = entry.TryGetProperty("display_name", out var labelElement) && labelElement.ValueKind == JsonValueKind.String ? labelElement.GetString() : null;
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
            throw new ProviderAdapterException("Reference-image input is not implemented for Anthropic in this app yet, even though the Messages API supports it.");
        }

        var normalizedSettings = LibraryRules.ValidateGenerationSettings(settings ?? GenerationSettings.Empty);
        var texts = new List<string>(resultCount);
        int? promptTokens = null;
        int? completionTokens = null;
        for (var index = 0; index < resultCount; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "messages"));
            ApplyAnthropicHeaders(request, connection, apiKey);
            request.Content = new StringContent(BuildMessagesRequestBody(model.ProviderModelId, prompt, systemInstructions, normalizedSettings), Encoding.UTF8, "application/json");
            var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, rateLimitTracker: _rateLimitTracker).ConfigureAwait(false);
            if (!isSuccess) throw new ProviderAdapterException(DescribeFailure(body, statusCode));

            var (text, usagePromptTokens, usageCompletionTokens) = ParseMessageResult(body);
            texts.Add(text);
            promptTokens = (promptTokens ?? 0) + usagePromptTokens;
            completionTokens = (completionTokens ?? 0) + usageCompletionTokens;
        }

        return new TextGenerationResult(texts, promptTokens, completionTokens);
    }

    public Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Image generation is not available for Anthropic: it offers no image-generation API.");

    public Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? voice = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Audio generation is not available for Anthropic: it offers no native audio-generation API.");

    public Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not available for Anthropic: it offers no general-purpose video-generation API.");

    public Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not available for Anthropic: it offers no general-purpose video-generation API.");

    private static void ApplyAnthropicHeaders(HttpRequestMessage request, Connection connection, string? apiKey)
    {
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
    }

    private static string BuildMessagesRequestBody(string providerModelId, string prompt, string? systemInstructions, GenerationSettings settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteNumber("max_tokens", settings.MaxTokens ?? DefaultMaxTokens);
            if (settings.Temperature is { } temperature) writer.WriteNumber("temperature", temperature);
            if (settings.TopP is { } topP) writer.WriteNumber("top_p", topP);
            if (!string.IsNullOrWhiteSpace(systemInstructions)) writer.WriteString("system", systemInstructions);
            if (settings.AdvancedJson is { } advancedJson)
            {
                using var advanced = JsonDocument.Parse(advancedJson);
                foreach (var property in advanced.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Confirmed shape per Anthropic's public API reference:
    /// <c>{"content":[{"type":"text","text":"..."}],"usage":{"input_tokens":N,"output_tokens":N},...}</c>.
    /// Concatenates every <c>text</c>-type content block, since a response can legitimately contain more
    /// than one (e.g. alongside a <c>thinking</c> block when extended thinking is enabled).</summary>
    private static (string Text, int PromptTokens, int CompletionTokens) ParseMessageResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderAdapterException("The provider's message response was not in the expected shape.");
            }

            var builder = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (!block.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String || typeElement.GetString() != "text") continue;
                if (!block.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String) continue;
                builder.Append(textElement.GetString());
            }

            if (builder.Length == 0) throw new ProviderAdapterException("The provider returned no usable text result.");

            var promptTokens = 0;
            var completionTokens = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("input_tokens", out var inputTokensElement) && inputTokensElement.ValueKind == JsonValueKind.Number) promptTokens = inputTokensElement.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var outputTokensElement) && outputTokensElement.ValueKind == JsonValueKind.Number) completionTokens = outputTokensElement.GetInt32();
            }

            return (builder.ToString(), promptTokens, completionTokens);
        }
        catch (JsonException)
        {
            throw new ProviderAdapterException("The provider's message response was not valid JSON.");
        }
    }

    /// <summary>Anthropic's documented error shape: <c>{"type":"error","error":{"type":"...","message":"..."}}</c>.</summary>
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
