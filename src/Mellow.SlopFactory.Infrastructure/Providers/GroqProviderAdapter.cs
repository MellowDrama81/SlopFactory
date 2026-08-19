using System.Text;
using System.Text.Json;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// Groq (`api.groq.com/openai/v1`) exposes an OpenAI-compatible `chat/completions` endpoint — its
/// entire value proposition is inference speed (custom LPU hardware) on hosted open-weight text
/// models, not model novelty, so this adapter reuses <see cref="OpenAiCompatibleProtocol"/> exactly
/// like <see cref="OpenAiProviderAdapter"/>. Groq does not host any image-generation model at all
/// (unlike Mistral/xAI, there is no partial-capability caveat here — it is simply absent). Audio is
/// implemented as text-to-speech only, via Groq's documented OpenAI-compatible `POST /audio/speech`
/// endpoint (PlayAI-based TTS models) — the same shape <see cref="OpenAiProviderAdapter"/>/
/// <see cref="OpenRouterProviderAdapter"/>/<see cref="DeepInfraProviderAdapter"/> already implement.
/// Groq's hosted Whisper models are speech-to-text (input direction), which this app's
/// <see cref="GenerateAudioAsync"/> has no surface for, so they stay out of scope here regardless.
/// </summary>
internal sealed class GroqProviderAdapter : IProviderAdapter
{
    private readonly HttpClient _httpClient;
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;

    public GroqProviderAdapter(HttpClient httpClient, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _httpClient = httpClient;
        _rateLimitTracker = rateLimitTracker;
    }

    public ProviderType ProviderType => ProviderType.Groq;

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

    public Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Image generation is not available for Groq: it does not host any image-generation models.");

    public async Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? voice = null, CancellationToken cancellationToken = default)
    {
        if (resultCount < 1) throw new ProviderAdapterException("At least one audio result must be requested.");
        var results = new List<byte[]>(resultCount);
        for (var index = 0; index < resultCount; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, "audio/speech"));
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

    public Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not available for Groq: it does not offer a video-generation API.");

    public Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not available for Groq: it does not offer a video-generation API.");

    private static string BuildAudioSpeechRequestBody(string providerModelId, string prompt, string? voice)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", providerModelId);
            writer.WriteString("input", prompt);
            writer.WriteString("response_format", "wav");
            writer.WriteString("voice", string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private const string DefaultVoice = "Fritz-PlayAI";

    private static string? TryGetHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
