using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Application;

public interface IProviderAdapter
{
    ProviderType ProviderType { get; }

    Task<ConnectionTestResult> TestConnectionAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default);

    /// <summary><paramref name="sourceImages"/> is an ordered list of reference images (the
    /// <see cref="Domain.GenerationInputSlotRole.ReferenceImage"/> slot) — any length, not fixed at
    /// three.</summary>
    Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, GenerationSettings? settings = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Infrastructure.Providers.ProviderAdapterException"/> for a provider adapter with no verified audio generation API.</summary>
    Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits one asynchronous video generation job (never more than one per call — a caller wanting
    /// several results submits several independent jobs). Throws
    /// <see cref="Infrastructure.Providers.ProviderAdapterException"/> for a provider adapter with no
    /// verified video generation API. <paramref name="firstFrame"/> is the
    /// <see cref="Domain.GenerationInputSlotRole.FirstFrame"/> slot (image-to-video); only adapters
    /// that declare that capability via <see cref="Domain.LibraryRules.GetInputSlotCapabilities"/>
    /// use it — others ignore it since no non-null value should ever reach them.
    /// </summary>
    Task<AsyncGenerationSubmission> SubmitVideoGenerationAsync(Connection connection, Model model, string? apiKey, string prompt, TextGenerationSourceImage? firstFrame = null, CancellationToken cancellationToken = default);

    /// <summary>One poll step for a job previously returned by <see cref="SubmitVideoGenerationAsync"/>.</summary>
    Task<AsyncGenerationPollResult> PollVideoGenerationAsync(Connection connection, string? apiKey, string providerJobId, CancellationToken cancellationToken = default);
}

public interface IProviderAdapterResolver
{
    IProviderAdapter Resolve(ProviderType providerType);
}
