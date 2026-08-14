using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Application;

public interface IProviderAdapter
{
    ProviderType ProviderType { get; }

    Task<ConnectionTestResult> TestConnectionAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default);

    Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, TextGenerationSourceImage? sourceImage = null, GenerationSettings? settings = null, TextGenerationSourceImage? secondarySourceImage = null, TextGenerationSourceImage? tertiarySourceImage = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Infrastructure.Providers.ProviderAdapterException"/> for a provider adapter with no verified audio generation API.</summary>
    Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits one asynchronous video generation job (never more than one per call — a caller wanting
    /// several results submits several independent jobs). Throws
    /// <see cref="Infrastructure.Providers.ProviderAdapterException"/> for a provider adapter with no
    /// verified video generation API.
    /// </summary>
    Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, CancellationToken cancellationToken = default);

    /// <summary>One poll step for a job previously returned by <see cref="SubmitVideoGenerationAsync"/>.</summary>
    Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default);
}

public interface IProviderAdapterResolver
{
    IProviderAdapter Resolve(ProviderType providerType);
}
