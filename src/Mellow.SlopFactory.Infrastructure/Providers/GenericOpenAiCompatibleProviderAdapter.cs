using System.Text;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Providers;

internal sealed class GenericOpenAiCompatibleProviderAdapter : IProviderAdapter
{
    private readonly HttpClient _httpClient;

    public GenericOpenAiCompatibleProviderAdapter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public ProviderType ProviderType => ProviderType.GenericOpenAiCompatible;

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
        var modalitySettings = connection.GenericModalitySettings ?? GenericConnectionModalitySettings.Default;
        if (!modalitySettings.ModelsEnabled)
        {
            throw new ProviderAdapterException("Model discovery is disabled for this connection.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, modalitySettings.ModelsPathOverride ?? "models"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken, allowRetry: true).ConfigureAwait(false);
        if (!isSuccess)
        {
            if (statusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new ProviderAdapterException("Model discovery is not available at this base URL. The connection can still be saved and used manually.");
            }

            throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
        }

        return OpenAiCompatibleProtocol.ParseModelList(body);
    }

    public async Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, TextGenerationSourceImage? sourceImage = null, GenerationSettings? settings = null, CancellationToken cancellationToken = default)
    {
        var modalitySettings = connection.GenericModalitySettings ?? GenericConnectionModalitySettings.Default;
        if (!modalitySettings.TextGenerationEnabled)
        {
            throw new ProviderAdapterException("Text generation is disabled for this connection.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, modalitySettings.TextGenerationPathOverride ?? "chat/completions"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        request.Content = new StringContent(OpenAiCompatibleProtocol.BuildChatCompletionRequestBody(model.ProviderModelId, prompt, resultCount, systemInstructions, sourceImage, settings), Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
        return OpenAiCompatibleProtocol.ParseChatCompletionResult(body);
    }

    public async Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default)
    {
        var modalitySettings = connection.GenericModalitySettings ?? GenericConnectionModalitySettings.Default;
        if (!modalitySettings.ImageGenerationEnabled)
        {
            throw new ProviderAdapterException("Image generation is disabled for this connection.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleProtocol.CombineUrl(connection.BaseUrl, modalitySettings.ImageGenerationPathOverride ?? "images/generations"));
        OpenAiCompatibleProtocol.ApplyAuthorization(request, connection, apiKey);
        OpenAiCompatibleProtocol.ApplyAdditionalHeaders(request, connection);
        request.Content = new StringContent(OpenAiCompatibleProtocol.BuildImageGenerationRequestBody(model.ProviderModelId, prompt, resultCount), Encoding.UTF8, "application/json");
        var (isSuccess, statusCode, body) = await OpenAiCompatibleProtocol.SendAsync(_httpClient, request, connection, cancellationToken).ConfigureAwait(false);
        if (!isSuccess) throw new ProviderAdapterException(OpenAiCompatibleProtocol.DescribeFailure(statusCode));
        return OpenAiCompatibleProtocol.ParseImageGenerationBytes(body);
    }

    private static string? TryGetHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
}
