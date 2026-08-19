using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Application;

public interface IProviderAdapter
{
    ProviderType ProviderType { get; }

    Task<ConnectionTestResult> TestConnectionAsync(Connection connection, string? apiKey, CancellationToken cancellationToken = default);

    /// <summary><paramref name="mode"/> is an optional hint for narrowing the returned catalogue to
    /// models that produce that output type; only <see cref="ProviderType.OpenRouter"/> honors it
    /// today (its confirmed <c>output_modalities</c> query parameter — see
    /// https://openrouter.ai/docs/guides/overview/models) — other adapters ignore it and always
    /// return their full unfiltered catalogue, since no other provider's <c>/models</c> endpoint has a
    /// confirmed modality filter.</summary>
    Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(Connection connection, string? apiKey, GenerationMode? mode = null, CancellationToken cancellationToken = default);

    /// <summary><paramref name="sourceImages"/> is an ordered list of reference images (the
    /// <see cref="Domain.GenerationInputSlotRole.ReferenceImage"/> slot) — any length, not fixed at
    /// three.</summary>
    Task<TextGenerationResult> GenerateTextAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? systemInstructions = null, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, GenerationSettings? settings = null, CancellationToken cancellationToken = default);

    /// <summary><paramref name="sourceImages"/> is the same ordered
    /// <see cref="Domain.GenerationInputSlotRole.ReferenceImage"/> slot list <see cref="GenerateTextAsync"/>
    /// takes, here used for image-to-image editing rather than multimodal chat input; only adapters
    /// that declare the capability for <see cref="Domain.GenerationMode.Image"/> via
    /// <see cref="Domain.LibraryRules.GetInputSlotCapabilities"/> use it — others ignore it since no
    /// non-null value should ever reach them.</summary>
    Task<IReadOnlyList<byte[]>> GenerateImageAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, IReadOnlyList<TextGenerationSourceImage>? sourceImages = null, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Infrastructure.Providers.ProviderAdapterException"/> for a provider
    /// adapter with no verified audio generation API. <paramref name="voice"/> is a provider-specific
    /// preset voice identifier; only adapters that declare
    /// <see cref="Domain.LibraryRules.SupportsAudioVoiceSelection"/> read it — others ignore it since
    /// no non-null value should ever reach them.</summary>
    Task<IReadOnlyList<byte[]>> GenerateAudioAsync(Connection connection, Model model, string? apiKey, string prompt, int resultCount, string? voice = null, CancellationToken cancellationToken = default);

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
