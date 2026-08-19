using System.Text;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

/// <summary>
/// AI21 (`api.ai21.com/studio/v1`) is documented in providers.md as "OpenAI-adjacent but not identical"
/// — that assessment came from research alone, with no live call made against the real endpoint. This
/// adapter still reuses <see cref="OpenAiCompatibleProtocol"/>'s `chat/completions`-shaped helpers
/// exactly like the Mistral/Groq/Together AI/Fireworks AI/DeepSeek/Perplexity/xAI batch, on the same
/// "well-documented, standard-enough shape to implement without per-provider live verification" basis
/// those seven used — but AI21 carries a higher chance of a field-level mismatch than that batch and
/// should be the first of the group to get a real live-verification pass if it sees real use. No image,
/// audio, or video generation exists for the Jamba model family.
/// </summary>
internal sealed class AI21ProviderAdapter : IProviderAdapter
{
    private readonly HttpClient _httpClient;
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;

    public AI21ProviderAdapter(HttpClient httpClient, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _httpClient = httpClient;
        _rateLimitTracker = rateLimitTracker;
    }

    public ProviderType ProviderType => ProviderType.AI21;

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
        throw new ProviderAdapterException("Image generation is not available for AI21: it offers no image-generation API.");

    public Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? voice = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Audio generation is not available for AI21: it offers no audio-generation API.");

    public Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not available for AI21: it offers no video-generation API.");

    public Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default) =>
        throw new ProviderAdapterException("Video generation is not available for AI21: it offers no video-generation API.");

    private static string? TryGetHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
