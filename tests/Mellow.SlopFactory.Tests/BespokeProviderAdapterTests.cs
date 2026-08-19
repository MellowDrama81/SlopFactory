using System.Net;
using System.Text;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

/// <summary>
/// Tests for the four bespoke-shape adapters from providers.md's "Custom shape required" section:
/// Anthropic, Google Gemini, Cohere (each a genuinely different wire shape from
/// <see cref="OpenAiCompatibleProtocol"/>) and AI21 (reuses that shared protocol, so its coverage
/// mirrors <see cref="OpenAiCompatibleBatchProviderAdapterTests"/> rather than needing its own shape
/// assertions).
/// </summary>
public sealed class BespokeProviderAdapterTests
{
    private static Connection CreateConnection(ProviderType providerType, string baseUrl, string credentialHeaderName, string authPrefix) =>
        new("connection-1", "Test Connection", providerType, baseUrl, credentialHeaderName, authPrefix, false, ConnectionTestStatus.Untested, null, null,
            LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static Model CreateModel(string providerModelId, bool supportsSystemInstructions = true) =>
        new("model-1", "connection-1", "Test Model", providerModelId, GenerationMode.Text, supportsSystemInstructions, LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    // ---- Anthropic ----

    [Fact]
    public async Task AnthropicAdapterGeneratesTextAgainstMessagesEndpointWithRequiredHeaders()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
            Assert.Equal("secret-key", request.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            Assert.False(request.Headers.Contains("Authorization"));
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal("""{"model":"claude-sonnet-5","max_tokens":4096,"messages":[{"role":"user","content":"Write a haiku"}]}""", body);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"content":[{"type":"text","text":"Result"}],"usage":{"input_tokens":5,"output_tokens":2}}""");
        });
        var adapter = new AnthropicProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Anthropic, "https://api.anthropic.com/v1", "x-api-key", "");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("claude-sonnet-5"), "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
        Assert.Equal(5, result.PromptTokens);
        Assert.Equal(2, result.CompletionTokens);
    }

    [Fact]
    public async Task AnthropicAdapterSendsOneIndependentRequestPerResult()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            var body = "{\"content\":[{\"type\":\"text\",\"text\":\"Result " + callCount + "\"}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, body);
        });
        var adapter = new AnthropicProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Anthropic, "https://api.anthropic.com/v1", "x-api-key", "");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("claude-sonnet-5"), "secret-key", "Write a haiku", 2);

        Assert.Equal(2, callCount);
        Assert.Equal(2, result.Texts.Count);
    }

    [Fact]
    public async Task AnthropicAdapterSendsSystemAsATopLevelFieldNotAMessage()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"content":[{"type":"text","text":"Result"}]}""");
        });
        var adapter = new AnthropicProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Anthropic, "https://api.anthropic.com/v1", "x-api-key", "");

        await adapter.GenerateTextAsync(connection, CreateModel("claude-sonnet-5"), "secret-key", "Write a haiku", 1, systemInstructions: "Be terse.");

        Assert.Contains("\"system\":\"Be terse.\"", capturedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"role\":\"system\"", capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicAdapterListsModelsUsingDisplayNameNotName()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.anthropic.com/v1/models", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"data":[{"id":"claude-sonnet-5","display_name":"Claude Sonnet 5"}]}""");
        });
        var adapter = new AnthropicProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Anthropic, "https://api.anthropic.com/v1", "x-api-key", "");

        var models = await adapter.ListModelsAsync(connection, "secret-key");

        var model = Assert.Single(models);
        Assert.Equal("claude-sonnet-5", model.ProviderModelId);
        Assert.Equal("Claude Sonnet 5", model.DisplayLabel);
    }

    [Fact]
    public async Task AnthropicAdapterHasNoImageAudioOrVideoGenerationAndRejectsSourceImages()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new AnthropicProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Anthropic, "https://api.anthropic.com/v1", "x-api-key", "");
        var model = CreateModel("claude-sonnet-5");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(connection, model, "secret-key", "A fox", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "A cat"));
        TextGenerationSourceImage[] sourceImages = [new("image/png", [1, 2, 3])];
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateTextAsync(connection, model, "secret-key", "prompt", 1, sourceImages: sourceImages));
    }

    // ---- Google Gemini ----

    [Fact]
    public async Task GeminiAdapterGeneratesTextAgainstGenerateContentWithModelInThePath()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-pro:generateContent", request.RequestUri!.ToString());
            Assert.Equal("secret-key", request.Headers.GetValues("x-goog-api-key").Single());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal("""{"contents":[{"role":"user","parts":[{"text":"Write a haiku"}]}],"generationConfig":{"candidateCount":1}}""", body);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[{"text":"Result"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":2}}""");
        });
        var adapter = new GoogleGeminiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Gemini, "https://generativelanguage.googleapis.com/v1beta", "x-goog-api-key", "");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("gemini-3.1-pro"), "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
        Assert.Equal(5, result.PromptTokens);
        Assert.Equal(2, result.CompletionTokens);
    }

    [Fact]
    public async Task GeminiAdapterSendsCandidateCountRatherThanLoopingPerResult()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("\"candidateCount\":3", body, StringComparison.Ordinal);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"candidates":[{"content":{"parts":[{"text":"A"}]}},{"content":{"parts":[{"text":"B"}]}},{"content":{"parts":[{"text":"C"}]}}]}""");
        });
        var adapter = new GoogleGeminiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Gemini, "https://generativelanguage.googleapis.com/v1beta", "x-goog-api-key", "");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("gemini-3.1-pro"), "secret-key", "Write a haiku", 3);

        Assert.Equal(1, callCount);
        Assert.Equal(3, result.Texts.Count);
    }

    [Fact]
    public async Task GeminiAdapterListsModelsAndStripsTheModelsPrefixFromTheReturnedId()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"models":[{"name":"models/gemini-3.1-pro","displayName":"Gemini 3.1 Pro"}]}""");
        });
        var adapter = new GoogleGeminiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Gemini, "https://generativelanguage.googleapis.com/v1beta", "x-goog-api-key", "");

        var models = await adapter.ListModelsAsync(connection, "secret-key");

        var model = Assert.Single(models);
        Assert.Equal("gemini-3.1-pro", model.ProviderModelId);
        Assert.Equal("Gemini 3.1 Pro", model.DisplayLabel);
    }

    [Fact]
    public async Task GeminiAdapterTreatsASafetyFinishReasonAsSafetyBlocked()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
            """{"candidates":[{"finishReason":"SAFETY"}]}"""));
        var adapter = new GoogleGeminiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Gemini, "https://generativelanguage.googleapis.com/v1beta", "x-goog-api-key", "");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("gemini-3.1-pro"), "secret-key", "prompt", 1);

        Assert.Empty(result.Texts);
        Assert.Equal(1, result.SafetyBlockedCount);
    }

    [Fact]
    public async Task GeminiAdapterHasNoVideoGenerationAndRejectsSourceImagesForText()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new GoogleGeminiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Gemini, "https://generativelanguage.googleapis.com/v1beta", "x-goog-api-key", "");
        var model = CreateModel("gemini-3.1-pro");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "A cat"));
        TextGenerationSourceImage[] sourceImages = [new("image/png", [1, 2, 3])];
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateTextAsync(connection, model, "secret-key", "prompt", 1, sourceImages: sourceImages));
    }

    [Fact]
    public async Task GeminiAdapterGeneratesAudioAgainstGenerateContentWithResponseModalitiesAudioAndWrapsPcmAsWav()
    {
        // 16 bytes of arbitrary raw 16-bit PCM samples.
        byte[] pcmBytes = [1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0, 7, 0, 8, 0];
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-preview-tts:generateContent", request.RequestUri!.ToString());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal(
                """{"contents":[{"parts":[{"text":"Hello there"}]}],"generationConfig":{"responseModalities":["AUDIO"],"speechConfig":{"voiceConfig":{"prebuiltVoiceConfig":{"voiceName":"Kore"}}}}}""",
                body);
            var responseJson = "{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/L16;codec=pcm;rate=24000\",\"data\":\"" + Convert.ToBase64String(pcmBytes) + "\"}}]}}]}";
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, responseJson);
        });
        var adapter = new GoogleGeminiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Gemini, "https://generativelanguage.googleapis.com/v1beta", "x-goog-api-key", "");

        var results = await adapter.GenerateAudioAsync(connection, CreateModel("gemini-2.5-flash-preview-tts"), "secret-key", "Hello there", 1);

        var wav = Assert.Single(results);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        var sampleRateInHeader = BitConverter.ToInt32(wav, 24);
        Assert.Equal(24000, sampleRateInHeader);
        Assert.Equal(44 + pcmBytes.Length, wav.Length);
        Assert.Equal(pcmBytes, wav[44..]);
    }

    [Fact]
    public async Task GeminiAdapterGeneratesOneRequestPerAudioResult()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"candidates":[{"content":{"parts":[{"inlineData":{"mimeType":"audio/L16;rate=24000","data":"AAA="}}]}}]}""");
        });
        var adapter = new GoogleGeminiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Gemini, "https://generativelanguage.googleapis.com/v1beta", "x-goog-api-key", "");

        var results = await adapter.GenerateAudioAsync(connection, CreateModel("gemini-2.5-flash-preview-tts"), "secret-key", "Hello", 2);

        Assert.Equal(2, callCount);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GeminiAdapterGeneratesImageAgainstThePredictEndpointAndDecodesBase64Results()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/imagen-4.0-generate-001:predict", request.RequestUri!.ToString());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal("""{"instances":[{"prompt":"A watercolor fox"}],"parameters":{"sampleCount":1}}""", body);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, $$"""{"predictions":[{"bytesBase64Encoded":"{{Convert.ToBase64String(pngBytes)}}","mimeType":"image/png"}]}""");
        });
        var adapter = new GoogleGeminiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Gemini, "https://generativelanguage.googleapis.com/v1beta", "x-goog-api-key", "");

        var images = await adapter.GenerateImageAsync(connection, CreateModel("imagen-4.0-generate-001"), "secret-key", "A watercolor fox", 1);

        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task GeminiAdapterRejectsSourceImagesForImageGeneration()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent for an unimplemented edit shape."));
        var adapter = new GoogleGeminiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Gemini, "https://generativelanguage.googleapis.com/v1beta", "x-goog-api-key", "");
        TextGenerationSourceImage[] sourceImages = [new("image/png", [1, 2, 3])];

        await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            adapter.GenerateImageAsync(connection, CreateModel("imagen-4.0-generate-001"), "secret-key", "A fox", 1, sourceImages));
    }

    // ---- Cohere ----

    [Fact]
    public async Task CohereAdapterGeneratesTextAgainstChatEndpointWithMessageAndEmptyHistory()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.cohere.com/v1/chat", request.RequestUri!.ToString());
            Assert.Equal("Bearer secret-key", request.Headers.GetValues("Authorization").Single());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal("""{"model":"command-r-plus","message":"Write a haiku","chat_history":[]}""", body);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"text":"Result"}""");
        });
        var adapter = new CohereProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Cohere, "https://api.cohere.com/v1", "Authorization", "Bearer");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("command-r-plus"), "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task CohereAdapterSendsSystemInstructionsAsPreamble()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"text":"Result"}""");
        });
        var adapter = new CohereProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Cohere, "https://api.cohere.com/v1", "Authorization", "Bearer");

        await adapter.GenerateTextAsync(connection, CreateModel("command-r-plus"), "secret-key", "Write a haiku", 1, systemInstructions: "Be terse.");

        Assert.Contains("\"preamble\":\"Be terse.\"", capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CohereAdapterSendsOneIndependentRequestPerResult()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, $$"""{"text":"Result {{callCount}}"}""");
        });
        var adapter = new CohereProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Cohere, "https://api.cohere.com/v1", "Authorization", "Bearer");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("command-r-plus"), "secret-key", "Write a haiku", 2);

        Assert.Equal(2, callCount);
        Assert.Equal(2, result.Texts.Count);
    }

    [Fact]
    public async Task CohereAdapterListsModelsUsingTheNameField()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.cohere.com/v1/models", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"models":[{"name":"command-r-plus"}]}""");
        });
        var adapter = new CohereProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Cohere, "https://api.cohere.com/v1", "Authorization", "Bearer");

        var models = await adapter.ListModelsAsync(connection, "secret-key");

        Assert.Equal("command-r-plus", Assert.Single(models).ProviderModelId);
    }

    [Fact]
    public async Task CohereAdapterHasNoImageOrVideoGenerationAndAudioIsInputOnly()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new CohereProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.Cohere, "https://api.cohere.com/v1", "Authorization", "Bearer");
        var model = CreateModel("command-r-plus");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(connection, model, "secret-key", "A fox", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "A cat"));
    }

    // ---- AI21 ----

    [Fact]
    public async Task AI21AdapterGeneratesTextAgainstItsOwnChatCompletionsEndpoint()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.ai21.com/studio/v1/chat/completions", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenAiCompatibleChatCompletionResponseV1.Replace("__CONTENT__", "Result").Replace("__PROMPT_TOKENS__", "5").Replace("__COMPLETION_TOKENS__", "2"));
        });
        var adapter = new AI21ProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.AI21, "https://api.ai21.com/studio/v1", "Authorization", "Bearer");

        var result = await adapter.GenerateTextAsync(connection, CreateModel("jamba-large"), "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task AI21AdapterHasNoImageAudioOrVideoGeneration()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        var adapter = new AI21ProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.AI21, "https://api.ai21.com/studio/v1", "Authorization", "Bearer");
        var model = CreateModel("jamba-large");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(connection, model, "secret-key", "A fox", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello", 1));
        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "A cat"));
    }
}
