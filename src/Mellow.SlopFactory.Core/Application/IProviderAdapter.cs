using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Application;

public interface IProviderAdapter
{
    ProviderType ProviderType { get; }

    Task<ConnectionTestResult> TestConnectionAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default);

    Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, TextGenerationSourceImage? sourceImage = null, GenerationSettings? settings = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default);
}

public interface IProviderAdapterResolver
{
    IProviderAdapter Resolve(ProviderType providerType);
}
