using System.Net;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class NewProviderAdapterTests
{
    private static Connection CreateConnection(ProviderType providerType, string baseUrl) =>
        new("connection-1", "Test Connection", providerType, baseUrl, "Authorization", "Bearer", false, ConnectionTestStatus.Untested, null, null,
            LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static Model CreateModel(string providerModelId) =>
        new("model-1", "connection-1", "Test Model", providerModelId, GenerationMode.Image, true, LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task OpenRouterAdapterGeneratesImageAgainstTheImagesEndpointAndDecodesBase64Results()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/images", request.RequestUri!.ToString());
            Assert.Equal("Bearer secret-key", request.Headers.GetValues("Authorization").Single());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{Convert.ToBase64String(pngBytes)}}"}]}""");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");
        var model = CreateModel("bytedance-seed/seedream-4.5");

        var images = await adapter.GenerateImageAsync(connection, model, "secret-key", "A watercolor fox", 1);

        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task OpenRouterAdapterGeneratesAudioWithOneRequestPerResult()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            Assert.Equal("https://openrouter.ai/api/v1/audio/speech", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.BinaryResponse([1, 2, 3, (byte)callCount], "audio/mpeg");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");
        var model = CreateModel("openai/gpt-4o-mini-tts");

        var results = await adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello there", 2);

        Assert.Equal(2, callCount);
        Assert.Equal(2, results.Count);
        Assert.NotEqual(results[0], results[1]);
    }

    [Fact]
    public async Task OpenRouterAdapterSubmitVideoGenerationReturnsTheProviderJobId()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/videos", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Accepted, """{"id":"abc123","polling_url":"https://openrouter.ai/api/v1/videos/abc123","status":"pending"}""");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");
        var model = CreateModel("google/veo-3.1");

        var submission = await adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "A cat riding a skateboard");

        Assert.Equal("abc123", submission.ProviderJobId);
    }

    [Fact]
    public async Task OpenRouterAdapterSubmitVideoGenerationThrowsWhenNoJobIdIsReturned()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Accepted, """{"status":"pending"}"""));
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, CreateModel("google/veo-3.1"), "secret-key", "A cat"));
    }

    [Fact]
    public async Task OpenRouterAdapterPollingReturnsProcessingWhilePending()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/videos/abc123", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"status":"pending"}""");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.Processing, result.Outcome);
        Assert.Null(result.Files);
    }

    [Fact]
    public async Task OpenRouterAdapterPollingDownloadsEveryResultUrlWhenCompleted()
    {
        byte[] firstVideo = [1, 2, 3];
        byte[] secondVideo = [4, 5, 6];
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.ToString();
            if (path == "https://openrouter.ai/api/v1/videos/abc123")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"id":"abc123","status":"completed","unsigned_urls":["https://openrouter.ai/api/v1/videos/abc123/content?index=0","https://openrouter.ai/api/v1/videos/abc123/content?index=1"]}""");
            }
            if (path == "https://openrouter.ai/api/v1/videos/abc123/content?index=0")
            {
                Assert.Equal("Bearer secret-key", request.Headers.GetValues("Authorization").Single());
                return FakeHttpMessageHandler.BinaryResponse(firstVideo, "video/mp4");
            }
            if (path == "https://openrouter.ai/api/v1/videos/abc123/content?index=1")
            {
                return FakeHttpMessageHandler.BinaryResponse(secondVideo, "video/mp4");
            }
            throw new InvalidOperationException($"Unexpected request to {path}.");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.Completed, result.Outcome);
        Assert.Equal([firstVideo, secondVideo], result.Files!);
    }

    [Fact]
    public async Task OpenRouterAdapterRetriesARateLimitedResultDownload()
    {
        byte[] video = [1, 2, 3];
        var downloadAttempts = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.ToString();
            if (path == "https://openrouter.ai/api/v1/videos/abc123")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"id":"abc123","status":"completed","unsigned_urls":["https://openrouter.ai/api/v1/videos/abc123/content?index=0"]}""");
            }
            if (path == "https://openrouter.ai/api/v1/videos/abc123/content?index=0")
            {
                downloadAttempts++;
                return downloadAttempts == 1 ? FakeHttpMessageHandler.RateLimited(TimeSpan.Zero) : FakeHttpMessageHandler.BinaryResponse(video, "video/mp4");
            }
            throw new InvalidOperationException($"Unexpected request to {path}.");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Equal(2, downloadAttempts);
        Assert.Equal(AsyncGenerationPollOutcome.Completed, result.Outcome);
        Assert.Equal(video, Assert.Single(result.Files!));
    }

    // A fixed, non-network host resolver so adapter tests never perform a real DNS lookup —
    // "never contact real providers" applies to name resolution too, not just HTTP calls.
    private static Task<IPAddress[]> PublicAddressResolver(string host, CancellationToken cancellationToken) =>
        Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    private static Task<IPAddress[]> PrivateAddressResolver(string host, CancellationToken cancellationToken) =>
        Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") });

    [Fact]
    public async Task OpenRouterAdapterPollingRejectsAResultUrlThatResolvesToAPrivateAddress()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.ToString();
            return path == "https://openrouter.ai/api/v1/videos/abc123"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"abc123","status":"completed","unsigned_urls":["https://openrouter.ai/api/v1/videos/abc123/content?index=0"]}""")
                : throw new InvalidOperationException("The download must never be attempted once host validation rejects the result URL.");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PrivateAddressResolver);
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123"));

        Assert.Contains("disallowed network address", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRouterAdapterPollingRejectsAnHttpResultUrl()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.ToString();
            return path == "https://openrouter.ai/api/v1/videos/abc123"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"abc123","status":"completed","unsigned_urls":["http://openrouter.ai/api/v1/videos/abc123/content?index=0"]}""")
                : throw new InvalidOperationException("The download must never be attempted for a non-HTTPS result URL.");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123"));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRouterAdapterPollingReturnsFailedWithTheProviderErrorMessage()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"status":"failed","error":"The prompt violated content policy."}"""));
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.Failed, result.Outcome);
        Assert.Equal("The prompt violated content policy.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("expired")]
    public async Task OpenRouterAdapterPollingTreatsCancelledAndExpiredAsFailedRatherThanLoopingForever(string status)
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, $$"""{"status":"{{status}}"}"""));
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.Failed, result.Outcome);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task OpenRouterAdapterHonorsRateLimitRetryWhenPollingJobStatus()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return callCount == 1
                ? FakeHttpMessageHandler.RateLimited(TimeSpan.Zero)
                : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"status":"pending"}""");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Equal(2, callCount);
        Assert.Equal(AsyncGenerationPollOutcome.Processing, result.Outcome);
    }

    [Fact]
    public async Task DeepInfraAdapterReusesTheOpenAiCompatibleShapeForTextGeneration()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.deepinfra.com/v1/openai/chat/completions", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"content":"Result"}}]}""");
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");
        var model = CreateModel("meta-llama/Meta-Llama-3-70B-Instruct");

        var result = await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 1);

        Assert.Equal("Result", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task DeepInfraAdapterReusesTheOpenAiCompatibleShapeForImageGeneration()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 9, 9];
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.deepinfra.com/v1/openai/images/generations", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{Convert.ToBase64String(pngBytes)}}"}]}""");
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");

        var images = await adapter.GenerateImageAsync(connection, CreateModel("black-forest-labs/FLUX-1-schnell"), "secret-key", "A fox", 1);

        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task DeepInfraAdapterThrowsAClearNotYetImplementedErrorForAudioAndVideo()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent for an unimplemented modality."));
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");
        var model = CreateModel("some-model");

        var audioException = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.GenerateAudioAsync(connection, model, "secret-key", "text", 1));
        var submitException = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "prompt"));
        var pollException = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.PollVideoGenerationAsync(connection, "secret-key", "job-id"));

        Assert.Contains("not yet implemented", audioException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not yet implemented", submitException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not yet implemented", pollException.Message, StringComparison.OrdinalIgnoreCase);
    }
}
