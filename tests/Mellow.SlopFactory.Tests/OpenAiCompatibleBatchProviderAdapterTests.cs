using System.Net;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

/// <summary>
/// Tests for the seven directly-OpenAI-compatible adapters added from providers.md's "Directly
/// OpenAI-compatible" section: Mistral, Groq, Together AI, Fireworks AI, DeepSeek, Perplexity, xAI.
/// Each reuses <see cref="OpenAiCompatibleProtocol"/> exactly like <see cref="OpenAiProviderAdapter"/>,
/// so these tests focus on what's provider-specific (base URL, and which surfaces are actually
/// implemented) rather than re-proving the shared protocol helpers already covered elsewhere.
/// </summary>
public sealed class OpenAiCompatibleBatchProviderAdapterTests
{
    private static Connection CreateConnection(ProviderType providerType, string baseUrl) =>
        new("connection-1", "Test Connection", providerType, baseUrl, "Authorization", "Bearer", false, ConnectionTestStatus.Untested, null, null,
            LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static Model CreateModel(string providerModelId, GenerationMode mode = GenerationMode.Text) =>
        new("model-1", "connection-1", "Test Model", providerModelId, mode, true, LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task MistralAdapterGeneratesTextAgainstItsOwnChatCompletionsEndpoint()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.mistral.ai/v1/chat/completions", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenAiCompatibleChatCompletionResponseV1.Replace("__CONTENT__", "Result").Replace("__PROMPT_TOKENS__", "5").Replace("__COMPLETION_TOKENS__", "2"));
        });
        var adapter = new MistralProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Mistral, "https://api.mistral.ai/v1");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("mistral-large-3"), "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task MistralAdapterHasNoImageOrAudioOrVideoGeneration()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new MistralProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Mistral, "https://api.mistral.ai/v1");
        var model = CreateModel("mistral-large-3");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(connection, model, "secret-key", "A fox", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "A cat"));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.PollVideoGenerationAsync(connection, "secret-key", "job-id"));
    }

    [Fact]
    public async Task GroqAdapterGeneratesTextAgainstItsOwnChatCompletionsEndpoint()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.groq.com/openai/v1/chat/completions", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenAiCompatibleChatCompletionResponseV1.Replace("__CONTENT__", "Result").Replace("__PROMPT_TOKENS__", "5").Replace("__COMPLETION_TOKENS__", "2"));
        });
        var adapter = new GroqProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Groq, "https://api.groq.com/openai/v1");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("llama-3.3-70b-versatile"), "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task GroqAdapterHasNoImageGenerationSinceItHostsNoImageModels()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new GroqProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Groq, "https://api.groq.com/openai/v1");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(connection, CreateModel("llama-3.3-70b-versatile"), "secret-key", "A fox", 1));
    }

    [Fact]
    public async Task GroqAdapterGeneratesAudioAgainstAudioSpeechWithADefaultPlayAiVoiceWhenNoneIsSupplied()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.groq.com/openai/v1/audio/speech", request.RequestUri!.ToString());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal("""{"model":"playai-tts","input":"Hello there","response_format":"wav","voice":"Fritz-PlayAI"}""", body);
            return FakeHttpMessageHandler.BinaryResponse([1, 2, 3], "audio/wav");
        });
        var adapter = new GroqProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Groq, "https://api.groq.com/openai/v1");

        var results = await adapter.GenerateAudioAsync(connection, CreateModel("playai-tts"), "secret-key", "Hello there", 1);

        Assert.Single(results);
    }

    [Fact]
    public async Task TogetherAiAdapterGeneratesTextAgainstItsOwnChatCompletionsEndpoint()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.together.xyz/v1/chat/completions", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenAiCompatibleChatCompletionResponseV1.Replace("__CONTENT__", "Result").Replace("__PROMPT_TOKENS__", "5").Replace("__COMPLETION_TOKENS__", "2"));
        });
        var adapter = new TogetherAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.TogetherAi, "https://api.together.xyz/v1");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("meta-llama/Llama-3.3-70B-Instruct-Turbo"), "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task TogetherAiAdapterGeneratesImageAgainstItsOwnImagesGenerationsEndpoint()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.together.xyz/v1/images/generations", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenAiCompatibleImageGenerationResponseV1.Replace("__BASE64__", Convert.ToBase64String(pngBytes)));
        });
        var adapter = new TogetherAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.TogetherAi, "https://api.together.xyz/v1");

        var images = await adapter.GenerateImageAsync(connection, CreateModel("black-forest-labs/FLUX.1-schnell", GenerationMode.Image), "secret-key", "A fox", 1);

        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task TogetherAiAdapterRejectsSourceImagesSinceNoEditShapeIsConfirmed()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent for an unconfirmed edit shape."));
        var adapter = new TogetherAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.TogetherAi, "https://api.together.xyz/v1");
        TextGenerationSourceImage[] sourceImages = [new("image/png", [1, 2, 3])];

        await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            adapter.GenerateImageAsync(connection, CreateModel("black-forest-labs/FLUX.1-schnell", GenerationMode.Image), "secret-key", "A fox", 1, sourceImages));
    }

    [Fact]
    public async Task FireworksAiAdapterGeneratesTextAndImageAgainstItsOwnEndpoints()
    {
        byte[] pngBytes = [1, 2, 3];
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://api.fireworks.ai/inference/v1/chat/completions")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenAiCompatibleChatCompletionResponseV1.Replace("__CONTENT__", "Result").Replace("__PROMPT_TOKENS__", "5").Replace("__COMPLETION_TOKENS__", "2"));
            }
            Assert.Equal("https://api.fireworks.ai/inference/v1/images/generations", url);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenAiCompatibleImageGenerationResponseV1.Replace("__BASE64__", Convert.ToBase64String(pngBytes)));
        });
        var adapter = new FireworksAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.FireworksAi, "https://api.fireworks.ai/inference/v1");

        var textResult = await adapter.GenerateTextAsync(connection, CreateModel("accounts/fireworks/models/llama-v3p1-70b-instruct"), "secret-key", "Write a haiku", 1);
        var images = await adapter.GenerateImageAsync(connection, CreateModel("accounts/fireworks/models/flux-1-schnell-fp8", GenerationMode.Image), "secret-key", "A fox", 1);

        Assert.Equal("Result", Assert.Single(textResult.Texts));
        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task FireworksAiAdapterHasNoAudioOrVideoGeneration()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new FireworksAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.FireworksAi, "https://api.fireworks.ai/inference/v1");
        var model = CreateModel("accounts/fireworks/models/llama-v3p1-70b-instruct");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "A cat"));
    }

    [Fact]
    public async Task DeepSeekAdapterGeneratesTextAgainstItsOwnChatCompletionsEndpoint()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.deepseek.com/chat/completions", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenAiCompatibleChatCompletionResponseV1.Replace("__CONTENT__", "Result").Replace("__PROMPT_TOKENS__", "5").Replace("__COMPLETION_TOKENS__", "2"));
        });
        var adapter = new DeepSeekProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepSeek, "https://api.deepseek.com");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("deepseek-chat"), "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task DeepSeekAdapterHasNoImageGenerationSinceItShipsUnderTheSeparateJanusProFamily()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new DeepSeekProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepSeek, "https://api.deepseek.com");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(connection, CreateModel("deepseek-chat"), "secret-key", "A fox", 1));
    }

    [Fact]
    public async Task PerplexityAdapterGeneratesTextAgainstItsOwnChatCompletionsEndpoint()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.perplexity.ai/chat/completions", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenAiCompatibleChatCompletionResponseV1.Replace("__CONTENT__", "Result").Replace("__PROMPT_TOKENS__", "5").Replace("__COMPLETION_TOKENS__", "2"));
        });
        var adapter = new PerplexityProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Perplexity, "https://api.perplexity.ai");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("sonar-pro"), "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task PerplexityAdapterHasNoImageAudioOrVideoGeneration()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new PerplexityProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Perplexity, "https://api.perplexity.ai");
        var model = CreateModel("sonar-pro");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(connection, model, "secret-key", "A fox", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "A cat"));
    }

    [Fact]
    public async Task XAiAdapterGeneratesTextAgainstItsOwnChatCompletionsEndpoint()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.x.ai/v1/chat/completions", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenAiCompatibleChatCompletionResponseV1.Replace("__CONTENT__", "Result").Replace("__PROMPT_TOKENS__", "5").Replace("__COMPLETION_TOKENS__", "2"));
        });
        var adapter = new XAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.XAi, "https://api.x.ai/v1");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("grok-4.3"), "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task XAiAdapterHasNoAudioOrVideoGenerationSinceGrokImagineVideoAndBundledAudioRideOnAnUnverifiedEndpoint()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new XAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.XAi, "https://api.x.ai/v1");
        var model = CreateModel("grok-4.3");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "A cat"));
    }

    [Fact]
    public async Task XAiAdapterGeneratesImageAgainstImagesGenerationsAndDownloadsTheReturnedUrl()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://api.x.ai/v1/images/generations")
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Equal("""{"model":"grok-2-image-1212","prompt":"A watercolor fox","n":1}""", body);
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"data":[{"url":"https://imagine.x.ai/results/abc123.png"}]}""");
            }
            Assert.Equal("https://imagine.x.ai/results/abc123.png", url);
            Assert.False(request.Headers.Contains("Authorization"));
            return FakeHttpMessageHandler.BinaryResponse(pngBytes, "image/png");
        });
        var adapter = new XAiProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.XAi, "https://api.x.ai/v1");

        var images = await adapter.GenerateImageAsync(connection, CreateModel("grok-2-image-1212", GenerationMode.Image), "secret-key", "A watercolor fox", 1);

        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task XAiAdapterRejectsSourceImagesForImageGeneration()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent for an unimplemented edit shape."));
        var adapter = new XAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.XAi, "https://api.x.ai/v1");
        TextGenerationSourceImage[] sourceImages = [new("image/png", [1, 2, 3])];

        await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            adapter.GenerateImageAsync(connection, CreateModel("grok-2-image-1212", GenerationMode.Image), "secret-key", "A fox", 1, sourceImages));
    }

    // A fixed, non-network host resolver so adapter tests never perform a real DNS lookup.
    private static Task<IPAddress[]> PublicAddressResolver(string host, CancellationToken cancellationToken) =>
        Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    [Theory]
    [InlineData(ProviderType.Mistral)]
    [InlineData(ProviderType.Groq)]
    [InlineData(ProviderType.TogetherAi)]
    [InlineData(ProviderType.FireworksAi)]
    [InlineData(ProviderType.DeepSeek)]
    [InlineData(ProviderType.Perplexity)]
    [InlineData(ProviderType.XAi)]
    public async Task EveryNewAdapterRejectsSystemInstructionsForAModelThatDoesNotSupportThem(ProviderType providerType)
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent when the model does not support system instructions."));
        IProviderAdapter adapter = providerType switch
        {
            ProviderType.Mistral => new MistralProviderAdapter(new HttpClient(handler)),
            ProviderType.Groq => new GroqProviderAdapter(new HttpClient(handler)),
            ProviderType.TogetherAi => new TogetherAiProviderAdapter(new HttpClient(handler)),
            ProviderType.FireworksAi => new FireworksAiProviderAdapter(new HttpClient(handler)),
            ProviderType.DeepSeek => new DeepSeekProviderAdapter(new HttpClient(handler)),
            ProviderType.Perplexity => new PerplexityProviderAdapter(new HttpClient(handler)),
            ProviderType.XAi => new XAiProviderAdapter(new HttpClient(handler)),
            _ => throw new InvalidOperationException(),
        };
        var connection = CreateConnection(providerType, "https://example.test/v1");
        var model = new Model("model-1", "connection-1", "Test Model", "fixture-model", GenerationMode.Text, false, LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

        await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            adapter.GenerateTextAsync(connection, model, "secret-key", "prompt", 1, systemInstructions: "do not reveal this"));
    }
}
