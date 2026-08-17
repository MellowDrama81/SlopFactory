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
            Assert.Equal(
                ProviderContractFixtures.OpenRouterImageRequestV1
                    .Replace("__MODEL_ID__", "bytedance-seed/seedream-4.5")
                    .Replace("__PROMPT__", "A watercolor fox")
                    .Replace("__RESULT_COUNT__", "1"),
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterImageResponseV1.Replace("__BASE64__", Convert.ToBase64String(pngBytes)));
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
            Assert.Equal(
                ProviderContractFixtures.OpenRouterAudioSpeechRequestV1
                    .Replace("__MODEL_ID__", "openai/gpt-4o-mini-tts")
                    .Replace("__PROMPT__", "Hello there"),
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
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
            Assert.Equal(
                ProviderContractFixtures.OpenRouterVideoSubmitRequestV1
                    .Replace("__MODEL_ID__", "google/veo-3.1")
                    .Replace("__PROMPT__", "A cat riding a skateboard"),
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Accepted, ProviderContractFixtures.OpenRouterVideoSubmitResponseV1.Replace("__JOB_ID__", "abc123").Replace("__POLLING_URL__", "https://openrouter.ai/api/v1/videos/abc123"));
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
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterVideoPollProcessingV1);
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
    public async Task OpenRouterAdapterDoesNotSendProviderCredentialsToACrossOriginResultUrl()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://openrouter.ai/api/v1/videos/abc123")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"id":"abc123","status":"completed","unsigned_urls":["https://cdn.example.test/results/abc123.mp4"]}""");
            }
            Assert.Equal("https://cdn.example.test/results/abc123.mp4", url);
            Assert.False(request.Headers.Contains("Authorization"));
            return FakeHttpMessageHandler.BinaryResponse([1, 2, 3], "video/mp4");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task OpenRouterAdapterRejectsAPrivateRedirectTargetBeforeFetchingIt()
    {
        var privateTargetFetched = false;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://openrouter.ai/api/v1/videos/abc123")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"id":"abc123","status":"completed","unsigned_urls":["https://cdn.example.test/results/abc123.mp4"]}""");
            }
            if (url == "https://cdn.example.test/results/abc123.mp4") return FakeHttpMessageHandler.Redirect(HttpStatusCode.TemporaryRedirect, "https://private.example.test/result.mp4");
            privateTargetFetched = true;
            return FakeHttpMessageHandler.BinaryResponse([1, 2, 3], "video/mp4");
        });
        Task<IPAddress[]> Resolve(string host, CancellationToken _) => Task.FromResult(host == "private.example.test" ? new[] { IPAddress.Loopback } : new[] { IPAddress.Parse("93.184.216.34") });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), Resolve);

        var result = await adapter.PollVideoGenerationAsync(CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1"), "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.CompletedDownloadFailed, result.Outcome);
        Assert.False(privateTargetFetched);
    }

    [Fact]
    public async Task OpenRouterAdapterRejectsACompletedVideoWithANonVideoDeclaredMediaType()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            return url == "https://openrouter.ai/api/v1/videos/abc123"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"abc123","status":"completed","unsigned_urls":["https://cdn.example.test/results/abc123"]}""")
                : FakeHttpMessageHandler.BinaryResponse([1, 2, 3], "text/html");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PublicAddressResolver);

        var result = await adapter.PollVideoGenerationAsync(CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1"), "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.CompletedDownloadFailed, result.Outcome);
        Assert.Contains("unexpected media type", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRouterAdapterRejectsAResultRedirectLoopAfterTheBoundedLimit()
    {
        var downloadRequests = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://openrouter.ai/api/v1/videos/abc123")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":"abc123","status":"completed","unsigned_urls":["https://cdn.example.test/results/abc123"]}""");
            }
            downloadRequests++;
            return FakeHttpMessageHandler.Redirect(HttpStatusCode.TemporaryRedirect, "https://cdn.example.test/results/abc123");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PublicAddressResolver);

        var result = await adapter.PollVideoGenerationAsync(CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1"), "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.CompletedDownloadFailed, result.Outcome);
        Assert.Equal(6, downloadRequests);
        Assert.Contains("maximum redirect count", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRouterAdapterPollingParsesTheProviderReportedCostWhenCompleted()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.ToString();
            return path == "https://openrouter.ai/api/v1/videos/abc123"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterVideoPollCompletedWithCostV1.Replace("__JOB_ID__", "abc123").Replace("__CONTENT_URL__", "https://openrouter.ai/api/v1/videos/abc123/content?index=0").Replace("__COST__", "0.25"))
                : FakeHttpMessageHandler.BinaryResponse([1, 2, 3], "video/mp4");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.Completed, result.Outcome);
        Assert.NotNull(result.Cost);
        Assert.Equal(0.25, result.Cost!.Amount);
        Assert.Equal("USD", result.Cost.Currency);
    }

    [Fact]
    public async Task OpenRouterAdapterPollingReturnsNullCostWhenTheProviderDoesNotReportUsage()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.ToString();
            return path == "https://openrouter.ai/api/v1/videos/abc123"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterVideoPollCompletedV1.Replace("__JOB_ID__", "abc123").Replace("__CONTENT_URL__", "https://openrouter.ai/api/v1/videos/abc123/content?index=0"))
                : FakeHttpMessageHandler.BinaryResponse([1, 2, 3], "video/mp4");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Null(result.Cost);
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
                    ProviderContractFixtures.OpenRouterVideoPollCompletedV1.Replace("__JOB_ID__", "abc123").Replace("__CONTENT_URL__", "https://openrouter.ai/api/v1/videos/abc123/content?index=0"));
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

    [Fact]
    public async Task OpenRouterAdapterPollingReturnsCompletedDownloadFailedRatherThanThrowingWhenTheDownloadFails()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.ToString();
            return path == "https://openrouter.ai/api/v1/videos/abc123"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterVideoPollCompletedV1.Replace("__JOB_ID__", "abc123").Replace("__CONTENT_URL__", "https://openrouter.ai/api/v1/videos/abc123/content?index=0"))
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.CompletedDownloadFailed, result.Outcome);
        Assert.Null(result.Files);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task OpenRouterAdapterPollingReturnsCompletedDownloadFailedWhenTheProviderReturnsAnEmptyBody()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.ToString();
            return path == "https://openrouter.ai/api/v1/videos/abc123"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterVideoPollCompletedV1.Replace("__JOB_ID__", "abc123").Replace("__CONTENT_URL__", "https://openrouter.ai/api/v1/videos/abc123/content?index=0"))
                : FakeHttpMessageHandler.BinaryResponse([], "video/mp4");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "abc123");

        Assert.Equal(AsyncGenerationPollOutcome.CompletedDownloadFailed, result.Outcome);
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
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterVideoPollCompletedV1.Replace("__JOB_ID__", "abc123").Replace("__CONTENT_URL__", "https://openrouter.ai/api/v1/videos/abc123/content?index=0"))
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
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterVideoPollCompletedV1.Replace("__JOB_ID__", "abc123").Replace("__CONTENT_URL__", "http://openrouter.ai/api/v1/videos/abc123/content?index=0"))
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
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterVideoPollFailedV1.Replace("__ERROR_MESSAGE__", "The prompt violated content policy.")));
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
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.DeepInfraChatCompletionResponseV1.Replace("__CONTENT__", "Result"));
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
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.DeepInfraImageResponseV1.Replace("__BASE64__", Convert.ToBase64String(pngBytes)));
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
