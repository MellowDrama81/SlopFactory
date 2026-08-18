using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class ProviderAdapterTests
{
    private static Connection CreateConnection(ProviderType providerType, string baseUrl, int? timeoutSeconds = null, IReadOnlyList<ConnectionHeader>? additionalHeaders = null,
        GenericConnectionModalitySettings? genericModalitySettings = null) =>
        new("connection-1", "Test Connection", providerType, baseUrl, "Authorization", "Bearer", false, ConnectionTestStatus.Untested, null, null,
            LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, timeoutSeconds, additionalHeaders, genericModalitySettings);

    private static Model CreateModel(string providerModelId = "gpt-4o") =>
        new("model-1", "connection-1", "Test Model", providerModelId, GenerationMode.Text, true, LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task OpenAiAdapterListsModelsAndSendsBearerAuthorization()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri!.ToString());
            Assert.Equal("Bearer secret-key", request.Headers.GetValues("Authorization").Single());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"data":[{"id":"gpt-4o","name":"GPT-4o"},{"id":"gpt-4o-mini"}]}""");
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");

        var models = await adapter.ListModelsAsync(connection, "secret-key");

        Assert.Equal(2, models.Count);
        Assert.Contains(models, model => model.ProviderModelId == "gpt-4o" && model.DisplayLabel == "GPT-4o");
        Assert.Contains(models, model => model.ProviderModelId == "gpt-4o-mini" && model.DisplayLabel is null);
    }

    [Fact]
    public async Task OpenRouterAdapterParsesPerModelPricingFromTheModelListResponse()
    {
        var handler = new FakeHttpMessageHandler(request =>
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"data":[{"id":"qwen/qwen3.8-27b","name":"Qwen 3.8 27B","pricing":{"prompt":"0.00000045","completion":"0.0000032","input_cache_read":"0.00000005"}},{"id":"no-pricing/model","name":"No Pricing"}]}"""));
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var models = await adapter.ListModelsAsync(connection, "secret-key");

        var priced = models.Single(model => model.ProviderModelId == "qwen/qwen3.8-27b");
        Assert.NotNull(priced.Pricing);
        Assert.Equal(0.00000045m, priced.Pricing!.PromptCostPerToken);
        Assert.Equal(0.0000032m, priced.Pricing.CompletionCostPerToken);
        Assert.Equal("USD", priced.Pricing.Currency);

        var unpriced = models.Single(model => model.ProviderModelId == "no-pricing/model");
        Assert.Null(unpriced.Pricing);
    }

    [Fact]
    public async Task OpenAiAdapterTestConnectionReportsAuthenticationFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");

        var result = await adapter.TestConnectionAsync(connection, "bad-key");

        Assert.False(result.Success);
        Assert.Contains("Authentication failed", result.Message, StringComparison.Ordinal);
        Assert.Equal("api.openai.com", result.FinalHost);
        Assert.False(result.SupportsModelDiscovery);
    }

    [Fact]
    public async Task OpenAiAdapterTestConnectionReportsUnreachableHostWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("Name or service not known"));
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");

        var result = await adapter.TestConnectionAsync(connection, "any-key");

        Assert.False(result.Success);
        Assert.False(result.SupportsModelDiscovery);
    }

    [Fact]
    public async Task GenericAdapterTreatsMissingModelListingEndpointAsNonFatal()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var adapter = new GenericOpenAiCompatibleProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.GenericOpenAiCompatible, "http://localhost:8080/v1");

        var result = await adapter.TestConnectionAsync(connection, null);

        Assert.False(result.Success);
        Assert.Contains("saved and used manually", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericAdapterOmitsAuthorizationHeaderWhenNoApiKeyIsSupplied()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.False(request.Headers.Contains("Authorization"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") };
        });
        var adapter = new GenericOpenAiCompatibleProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.GenericOpenAiCompatible, "http://localhost:8080/v1");

        var models = await adapter.ListModelsAsync(connection, null);

        Assert.Empty(models);
    }

    [Fact]
    public async Task OpenAiAdapterGeneratesMultipleTextCandidatesFromChatCompletions()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("Bearer secret-key", request.Headers.GetValues("Authorization").Single());
            Assert.Equal(
                ProviderContractFixtures.OpenAiCompatibleChatCompletionRequestV1
                    .Replace("__MODEL_ID__", "gpt-4o")
                    .Replace("__RESULT_COUNT__", "2")
                    .Replace("__PROMPT__", "Write a haiku"),
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"First candidate"}},{"message":{"content":"Second candidate"}}]}""");
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel();

        var result = await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 2);

        Assert.Equal(["First candidate", "Second candidate"], result.Texts);
        Assert.Null(result.PromptTokens);
        Assert.Null(result.CompletionTokens);
        Assert.Equal(0, result.SafetyBlockedCount);
    }

    [Fact]
    public async Task OpenAiAdapterCountsAContentFilterBlockedChoiceSeparatelyFromSuccessfulOnes()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"Allowed candidate"}},{"finish_reason":"content_filter","message":{"content":""}}]}""", Encoding.UTF8, "application/json")
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel();

        var result = await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 2);

        Assert.Equal(["Allowed candidate"], result.Texts);
        Assert.Equal(1, result.SafetyBlockedCount);
        Assert.NotNull(result.Candidates);
        Assert.Equal(2, result.Candidates!.Count);
        Assert.False(result.Candidates[0].SafetyBlocked);
        Assert.Equal("Allowed candidate", result.Candidates[0].Text);
        Assert.True(result.Candidates[1].SafetyBlocked);
        Assert.Null(result.Candidates[1].Text);
    }

    [Fact]
    public async Task OpenAiAdapterKeepsCandidateOrderStableWhenTheBlockedChoiceComesFirst()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"finish_reason":"content_filter","message":{"content":""}},{"message":{"content":"Second candidate"}}]}""", Encoding.UTF8, "application/json")
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel();

        var result = await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 2);

        Assert.NotNull(result.Candidates);
        Assert.Equal(2, result.Candidates!.Count);
        Assert.True(result.Candidates[0].SafetyBlocked);
        Assert.False(result.Candidates[1].SafetyBlocked);
        Assert.Equal("Second candidate", result.Candidates[1].Text);
    }

    [Fact]
    public async Task OpenAiAdapterReturnsNoTextsWithASafetyBlockedCountWhenEveryChoiceIsContentFiltered()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"finish_reason":"content_filter","message":{"content":""}}]}""", Encoding.UTF8, "application/json")
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel();

        var result = await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 1);

        Assert.Empty(result.Texts);
        Assert.Equal(1, result.SafetyBlockedCount);
    }

    [Fact]
    public async Task OpenAiAdapterParsesTokenUsageFromChatCompletionResponse()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"Result"}}],"usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}""", Encoding.UTF8, "application/json")
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel();

        var result = await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 1);

        Assert.Equal(["Result"], result.Texts);
        Assert.Equal(12, result.PromptTokens);
        Assert.Equal(34, result.CompletionTokens);
    }

    [Fact]
    public async Task OpenAiAdapterSendsMultiPartContentWhenASourceImageIsSupplied()
    {
        var imageBytes = new byte[] { 1, 2, 3, 4 };
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"Result"}}]}""", Encoding.UTF8, "application/json")
            };
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel();

        await adapter.GenerateTextAsync(connection, model, "secret-key", "Describe this image", 1, null, [new TextGenerationSourceImage("image/png", imageBytes)]);

        using var document = System.Text.Json.JsonDocument.Parse(capturedBody!);
        var userMessage = document.RootElement.GetProperty("messages")[0];
        Assert.Equal("user", userMessage.GetProperty("role").GetString());
        var contentParts = userMessage.GetProperty("content");
        Assert.Equal(2, contentParts.GetArrayLength());
        Assert.Equal("text", contentParts[0].GetProperty("type").GetString());
        Assert.Equal("Describe this image", contentParts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", contentParts[1].GetProperty("type").GetString());
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(imageBytes)}", contentParts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task OpenAiAdapterSendsUpToThreeOrderedImagePartsWhenMultipleSourceSlotsAreSupplied()
    {
        var primaryBytes = new byte[] { 1 };
        var secondaryBytes = new byte[] { 2 };
        var tertiaryBytes = new byte[] { 3 };
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"Result"}}]}""", Encoding.UTF8, "application/json")
            };
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel();

        await adapter.GenerateTextAsync(connection, model, "secret-key", "Describe these images", 1, null,
            [new TextGenerationSourceImage("image/png", primaryBytes), new TextGenerationSourceImage("image/png", secondaryBytes), new TextGenerationSourceImage("image/png", tertiaryBytes)]);

        using (var document = System.Text.Json.JsonDocument.Parse(capturedBody!))
        {
            var contentParts = document.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal(4, contentParts.GetArrayLength());
            Assert.Equal("text", contentParts[0].GetProperty("type").GetString());
            Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(primaryBytes)}", contentParts[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(secondaryBytes)}", contentParts[2].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(tertiaryBytes)}", contentParts[3].GetProperty("image_url").GetProperty("url").GetString());
        }

        await adapter.GenerateTextAsync(connection, model, "secret-key", "Describe this image", 1, null, [new TextGenerationSourceImage("image/png", secondaryBytes)]);
        using (var document = System.Text.Json.JsonDocument.Parse(capturedBody!))
        {
            var contentParts = document.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal(2, contentParts.GetArrayLength());
            Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(secondaryBytes)}", contentParts[1].GetProperty("image_url").GetProperty("url").GetString());
        }
    }

    [Fact]
    public async Task OpenAiAdapterIncludesASystemMessageOnlyWhenSystemInstructionsAreSupplied()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"Result"}}]}""", Encoding.UTF8, "application/json")
            };
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel();

        await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 1, "Respond only in French.");
        using (var document = System.Text.Json.JsonDocument.Parse(capturedBody!))
        {
            var messages = document.RootElement.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Equal("Respond only in French.", messages[0].GetProperty("content").GetString());
            Assert.Equal("user", messages[1].GetProperty("role").GetString());
        }

        await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 1);
        using (var document = System.Text.Json.JsonDocument.Parse(capturedBody!))
        {
            var messages = document.RootElement.GetProperty("messages");
            Assert.Equal(1, messages.GetArrayLength());
            Assert.Equal("user", messages[0].GetProperty("role").GetString());
        }
    }

    [Fact]
    public async Task OpenAiAdapterWritesOnlyExplicitlySetGenerationSettingsAndOmitsProviderDefaults()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"Result"}}]}""", Encoding.UTF8, "application/json")
            };
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel();

        await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 1, settings: new GenerationSettings(0.7, 0.9, 500, 0.5, -0.5));
        using (var document = System.Text.Json.JsonDocument.Parse(capturedBody!))
        {
            var root = document.RootElement;
            Assert.Equal(0.7, root.GetProperty("temperature").GetDouble());
            Assert.Equal(0.9, root.GetProperty("top_p").GetDouble());
            Assert.Equal(500, root.GetProperty("max_tokens").GetInt32());
            Assert.Equal(0.5, root.GetProperty("frequency_penalty").GetDouble());
            Assert.Equal(-0.5, root.GetProperty("presence_penalty").GetDouble());
        }

        await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 1, settings: new GenerationSettings(AdvancedJson: "{\"response_format\":{\"type\":\"json_object\"}}"));
        using (var document = System.Text.Json.JsonDocument.Parse(capturedBody!))
        {
            Assert.Equal("json_object", document.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        }

        await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 1);
        using (var document = System.Text.Json.JsonDocument.Parse(capturedBody!))
        {
            var root = document.RootElement;
            Assert.False(root.TryGetProperty("temperature", out _));
            Assert.False(root.TryGetProperty("top_p", out _));
            Assert.False(root.TryGetProperty("max_tokens", out _));
            Assert.False(root.TryGetProperty("frequency_penalty", out _));
            Assert.False(root.TryGetProperty("presence_penalty", out _));
        }
    }

    [Fact]
    public async Task OpenAiAdapterGenerateTextThrowsSanitizedExceptionOnProviderError()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel();

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateTextAsync(connection, model, "bad-key", "Write a haiku", 1));

        Assert.Contains("Authentication failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericAdapterGenerateTextParsesSingleCandidateResponse()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"Local result"}}]}""", Encoding.UTF8, "application/json")
        });
        var adapter = new GenericOpenAiCompatibleProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.GenericOpenAiCompatible, "http://localhost:8080/v1");
        var model = CreateModel("local-model");

        var result = await adapter.GenerateTextAsync(connection, model, null, "Write a haiku", 1);

        Assert.Equal(["Local result"], result.Texts);
    }

    [Fact]
    public async Task OpenAiAdapterGeneratesImagesFromBase64Response()
    {
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        var encoded = Convert.ToBase64String(imageBytes);
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.openai.com/v1/images/generations", request.RequestUri!.ToString());
            Assert.Equal(
                ProviderContractFixtures.OpenAiCompatibleImageGenerationRequestV1
                    .Replace("__MODEL_ID__", "gpt-image-1")
                    .Replace("__PROMPT__", "A watercolor fox")
                    .Replace("__RESULT_COUNT__", "1"),
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                ProviderContractFixtures.OpenAiCompatibleImageGenerationResponseV1.Replace("__BASE64__", encoded));
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel("gpt-image-1");

        var images = await adapter.GenerateImageAsync(connection, model, "secret-key", "A watercolor fox", 1);

        Assert.Equal(imageBytes, Assert.Single(images));
    }

    [Fact]
    public async Task OpenAiAdapterGenerateImageThrowsSanitizedExceptionOnProviderError()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel("gpt-image-1");

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateImageAsync(connection, model, "secret-key", "A watercolor fox", 1));

        Assert.Contains("rate limiting", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericAdapterGenerateImageParsesMultipleCandidates()
    {
        var firstBytes = new byte[] { 1, 2, 3 };
        var secondBytes = new byte[] { 4, 5, 6 };
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"data":[{"b64_json":"{{Convert.ToBase64String(firstBytes)}}"},{"b64_json":"{{Convert.ToBase64String(secondBytes)}}"}]}""", Encoding.UTF8, "application/json")
        });
        var adapter = new GenericOpenAiCompatibleProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.GenericOpenAiCompatible, "http://localhost:8080/v1");
        var model = CreateModel("local-image-model");

        var images = await adapter.GenerateImageAsync(connection, model, null, "A watercolor fox", 2);

        Assert.Equal(2, images.Count);
        Assert.Equal(firstBytes, images[0]);
        Assert.Equal(secondBytes, images[1]);
    }

    [Fact]
    public async Task ConnectionTimeoutOverrideThrowsProviderAdapterExceptionDistinctFromUserCancellation()
    {
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") };
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1", timeoutSeconds: 1);

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.ListModelsAsync(connection, "secret-key"));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UserCancellationDuringATimedRequestThrowsOperationCanceledExceptionNotProviderAdapterException()
    {
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") };
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1", timeoutSeconds: 60);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<TaskCanceledException>(() => adapter.ListModelsAsync(connection, "secret-key", cancellation.Token));
    }

    [Fact]
    public async Task AdditionalConnectionHeadersAreSentAlongsideAuthorization()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer secret-key", request.Headers.GetValues("Authorization").Single());
            Assert.Equal("org_123", request.Headers.GetValues("X-Organization").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
            };
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1", additionalHeaders: [new ConnectionHeader("X-Organization", "org_123")]);

        await adapter.ListModelsAsync(connection, "secret-key");
    }

    [Fact]
    public async Task GenericAdapterUsesPerModalityPathOverride()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("http://localhost:8080/v1/v2/images/generate", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"b64_json":"AQID"}]}""", Encoding.UTF8, "application/json")
            };
        });
        var adapter = new GenericOpenAiCompatibleProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.GenericOpenAiCompatible, "http://localhost:8080/v1",
            genericModalitySettings: new GenericConnectionModalitySettings(true, null, true, null, true, "v2/images/generate"));
        var model = CreateModel("local-image-model");

        var images = await adapter.GenerateImageAsync(connection, model, null, "A watercolor fox", 1);

        Assert.Single(images);
    }

    [Fact]
    public async Task GenericAdapterRejectsDisabledModalityWithoutIssuingARequest()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("The request should never be sent for a disabled modality."));
        var adapter = new GenericOpenAiCompatibleProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.GenericOpenAiCompatible, "http://localhost:8080/v1",
            genericModalitySettings: new GenericConnectionModalitySettings(true, null, false, null, true, null));
        var model = CreateModel("local-text-model");

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateTextAsync(connection, model, null, "Hello", 1));

        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListModelsRetriesOnRateLimitingThenSucceeds()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
                rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return rateLimited;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"gpt-4o"}]}""", Encoding.UTF8, "application/json")
            };
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");

        var models = await adapter.ListModelsAsync(connection, "secret-key");

        Assert.Equal(2, callCount);
        Assert.Equal("gpt-4o", Assert.Single(models).ProviderModelId);
    }

    [Fact]
    public async Task ListModelsGivesUpAfterBoundedRetriesAndReportsRateLimiting()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return rateLimited;
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.ListModelsAsync(connection, "secret-key"));

        Assert.Contains("rate limiting", exception.Message, StringComparison.Ordinal);
        Assert.True(callCount is > 1 and <= 5, $"Expected a small, bounded number of attempts; got {callCount}.");
    }

    [Fact]
    public async Task GenerateTextDoesNotAutomaticallyRetryOnRateLimiting()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return rateLimited;
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel("gpt-4o");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateTextAsync(connection, model, "secret-key", "Hello", 1));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task AdapterRecordsRateLimitHeadersIntoTheInjectedTrackerOnASuccessfulResponse()
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"Hi"}}]}""", Encoding.UTF8, "application/json") };
            response.Headers.Add("x-ratelimit-limit-requests", "5000");
            response.Headers.Add("x-ratelimit-remaining-requests", "4999");
            response.Headers.Add("x-ratelimit-reset-requests", "1s");
            return response;
        });
        var tracker = new ConnectionRateLimitTracker();
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler), tracker);
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel("gpt-4o");

        await adapter.GenerateTextAsync(connection, model, "secret-key", "Hello", 1);

        var observation = tracker.GetObservation(connection.Id);
        Assert.NotNull(observation);
        Assert.Equal(5000, observation!.LimitRequests);
        Assert.Equal(4999, observation.RemainingRequests);
        Assert.Equal(TimeSpan.FromSeconds(1), observation.ResetRequestsIn);
    }

    [Fact]
    public async Task AdapterLeavesTheTrackerUntouchedWhenNoRateLimitHeadersArePresent()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"Hi"}}]}""", Encoding.UTF8, "application/json") });
        var tracker = new ConnectionRateLimitTracker();
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler), tracker);
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel("gpt-4o");

        await adapter.GenerateTextAsync(connection, model, "secret-key", "Hello", 1);

        Assert.Null(tracker.GetObservation(connection.Id));
    }
}
