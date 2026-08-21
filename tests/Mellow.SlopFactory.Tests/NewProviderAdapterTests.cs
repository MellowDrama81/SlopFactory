using System.Net;
using System.Text.Json;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Providers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class NewProviderAdapterTests
{
    private static Connection CreateConnection(ProviderType providerType, string baseUrl) =>
        new("connection-1", "Test Connection", providerType, baseUrl, "Authorization", "Bearer", false, ConnectionTestStatus.Untested, null, null,
            LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static Model CreateModel(string providerModelId) =>
        new("model-1", "connection-1", "Test Model", providerModelId, GenerationMode.Image, true, LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static byte[] ToPngBytes(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

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
    public async Task OpenRouterAdapterIncludesInputReferencesWhenSourceImagesAreProvided()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        byte[] sourceBytes = [10, 20, 30];
        var handler = new FakeHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal(
                """{"model":"bytedance-seed/seedream-4.5","prompt":"A watercolor fox","n":1,"input_references":[{"type":"image_url","image_url":{"url":"data:image/png;base64,__SOURCE_BASE64__"}}]}"""
                    .Replace("__SOURCE_BASE64__", Convert.ToBase64String(sourceBytes)),
                body);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterImageResponseV1.Replace("__BASE64__", Convert.ToBase64String(pngBytes)));
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");
        var model = CreateModel("bytedance-seed/seedream-4.5");
        TextGenerationSourceImage[] sourceImages = [new("image/png", sourceBytes)];

        var images = await adapter.GenerateImageAsync(connection, model, "secret-key", "A watercolor fox", 1, sourceImages);

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
    public async Task OpenRouterAdapterSendsTheCallerChosenVoiceWhenSuppliedRatherThanTheHardcodedDefault()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.BinaryResponse([1, 2, 3], "audio/mpeg");
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        await adapter.GenerateAudioAsync(connection, CreateModel("openai/gpt-4o-mini-tts"), "secret-key", "Hello there", 1, "nova");

        Assert.Contains("\"voice\":\"nova\"", capturedBody, StringComparison.Ordinal);
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
    public async Task DeepInfraAdapterUsesImagesEditsMultipartWhenSourceImagesAreProvided()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 9, 9];
        var sourceBytes = new byte[] { 10, 20, 30 };
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.deepinfra.com/v1/openai/images/edits", request.RequestUri!.ToString());
            Assert.StartsWith("multipart/form-data", request.Content!.Headers.ContentType!.ToString(), StringComparison.Ordinal);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("name=model", body, StringComparison.Ordinal);
            Assert.Contains("black-forest-labs/FLUX.1-Kontext-dev", body, StringComparison.Ordinal);
            Assert.Contains("name=image; filename=source.png", body, StringComparison.Ordinal);
            Assert.Contains("name=image; filename=source.jpg", body, StringComparison.Ordinal);
            Assert.Contains("name=image; filename=source.webp", body, StringComparison.Ordinal);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.DeepInfraImageResponseV1.Replace("__BASE64__", Convert.ToBase64String(pngBytes)));
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");
        TextGenerationSourceImage[] sourceImages =
        [
            new("image/png", sourceBytes),
            new("image/jpeg", [40, 50, 60]),
            new("image/webp", [70, 80, 90]),
        ];

        var images = await adapter.GenerateImageAsync(connection, CreateModel("black-forest-labs/FLUX.1-Kontext-dev"), "secret-key", "Make it watercolor", 1, sourceImages);

        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task OpenRouterAdapterConvertsTheMaskToFirstReferenceTransparencyAndAddsTheMaskInstruction()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        using var source = new Image<Rgba32>(2, 1);
        source[0, 0] = new Rgba32(10, 20, 30, 255);
        source[1, 0] = new Rgba32(40, 50, 60, 255);
        using var mask = new Image<Rgba32>(2, 1);
        mask[0, 0] = new Rgba32(0, 0, 0, 255);
        mask[1, 0] = new Rgba32(0, 0, 0, 0);
        var handler = new FakeHttpMessageHandler(request =>
        {
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = document.RootElement;
            Assert.Equal("The first reference image is the source image. Its transparent pixels are the edit mask. Fill only the transparent area according to the request; preserve all opaque pixels of the first reference image unchanged. Replace the tie", root.GetProperty("prompt").GetString());
            var dataUrl = root.GetProperty("input_references")[0].GetProperty("image_url").GetProperty("url").GetString();
            Assert.StartsWith("data:image/png;base64,", dataUrl, StringComparison.Ordinal);
            using var maskedSource = Image.Load<Rgba32>(Convert.FromBase64String(dataUrl!["data:image/png;base64,".Length..]));
            Assert.Equal((byte)0, maskedSource[0, 0].A);
            Assert.Equal(new Rgba32(10, 20, 30, 0), maskedSource[0, 0]);
            Assert.Equal(new Rgba32(40, 50, 60, 255), maskedSource[1, 0]);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.OpenRouterImageResponseV1.Replace("__BASE64__", Convert.ToBase64String(pngBytes)));
        });
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");

        var images = await adapter.GenerateImageAsync(connection, CreateModel("qwen/qwen-image-3"), "secret-key", "Replace the tie", 1,
            [new TextGenerationSourceImage("image/png", ToPngBytes(source))], new TextGenerationSourceImage("image/png", ToPngBytes(mask)));

        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task DeepInfraAdapterUsesCompatibleEditRouteForMultipleReferenceImages()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 9, 9];
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.deepinfra.com/v1/openai/images/edits", request.RequestUri!.ToString());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("name=image; filename=source.png", body, StringComparison.Ordinal);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.DeepInfraImageResponseV1.Replace("__BASE64__", Convert.ToBase64String(pngBytes)));
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");

        var images = await adapter.GenerateImageAsync(connection, CreateModel("Qwen/Qwen-Image-Edit"), "secret-key", "Combine the references", 1,
            [new TextGenerationSourceImage("image/png", [1, 2, 3]), new TextGenerationSourceImage("image/png", [4, 5, 6])]);

        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task DeepInfraAdapterIncludesPrivateMaskInImagesEditsMultipartRequest()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 9, 9];
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.deepinfra.com/v1/openai/images/edits", request.RequestUri!.ToString());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("name=image; filename=source.png", body, StringComparison.Ordinal);
            Assert.Contains("name=mask; filename=mask.png", body, StringComparison.Ordinal);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, ProviderContractFixtures.DeepInfraImageResponseV1.Replace("__BASE64__", Convert.ToBase64String(pngBytes)));
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");

        var images = await adapter.GenerateImageAsync(connection, CreateModel("Qwen/Qwen-Image-Edit"), "secret-key", "Replace the sky", 1,
            [new TextGenerationSourceImage("image/png", [1, 2, 3])], new TextGenerationSourceImage("image/png", [4, 5, 6]));

        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task DeepInfraAdapterGeneratesAudioAgainstTheAbsoluteAudioSpeechPathNotTheOpenAiCompatibleBase()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            Assert.Equal("https://api.deepinfra.com/v1/audio/speech", request.RequestUri!.ToString());
            Assert.Equal("Bearer secret-key", request.Headers.GetValues("Authorization").Single());
            return FakeHttpMessageHandler.BinaryResponse([1, 2, 3, callCount == 1 ? (byte)4 : (byte)5], "audio/mpeg");
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");
        var model = CreateModel("hexgrad/Kokoro-82M");

        var results = await adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello there", 2);

        Assert.Equal(2, callCount);
        Assert.Equal(2, results.Count);
        Assert.NotEqual(results[0], results[1]);
    }

    [Fact]
    public async Task DeepInfraAdapterSendsTheRequestedVoiceWhenSuppliedAndOmitsItOtherwise()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return FakeHttpMessageHandler.BinaryResponse([1, 2, 3], "audio/mpeg");
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");
        var model = CreateModel("hexgrad/Kokoro-82M");

        await adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello there", 1, "af_bella");
        Assert.Contains("\"voice\":\"af_bella\"", capturedBody);

        await adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello there", 1);
        Assert.DoesNotContain("\"voice\"", capturedBody);
    }

    [Fact]
    public async Task DeepInfraAdapterSubmitVideoGenerationReturnsTheProviderJobId()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.deepinfra.com/v1/videos", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"id":"videos_abc123","object":"video.generation.job","created_at":1786947130,"status":"queued","model":"PrunaAI/p-video","data":null,"error":null}""");
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");

        var submission = await adapter.SubmitVideoGenerationAsync(connection, CreateModel("PrunaAI/p-video"), "secret-key", "A cat riding a skateboard");

        Assert.Equal("videos_abc123", submission.ProviderJobId);
    }

    [Fact]
    public async Task DeepInfraAdapterSubmitVideoGenerationWritesTheFirstFrameAsADataUriWhenSupplied()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"id":"videos_abc123","object":"video.generation.job","created_at":1786947130,"status":"queued","model":"PrunaAI/p-video","data":null,"error":null}""");
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");
        byte[] frameBytes = [1, 2, 3];

        await adapter.SubmitVideoGenerationAsync(connection, CreateModel("PrunaAI/p-video"), "secret-key", "A cat riding a skateboard", new TextGenerationSourceImage("image/png", frameBytes));

        Assert.Contains($"\"image_url\":\"data:image/png;base64,{Convert.ToBase64String(frameBytes)}\"", capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepInfraAdapterSubmitVideoGenerationOmitsImageUrlWhenNoFirstFrameIsSupplied()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"id":"videos_abc123","object":"video.generation.job","created_at":1786947130,"status":"queued","model":"PrunaAI/p-video","data":null,"error":null}""");
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");

        await adapter.SubmitVideoGenerationAsync(connection, CreateModel("PrunaAI/p-video"), "secret-key", "A cat riding a skateboard");

        Assert.DoesNotContain("image_url", capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepInfraAdapterSubmitVideoGenerationSurfacesTheProviderErrorMessageWhenAModelDoesNotSupportAsyncJobs()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.BadRequest,
            """{"error":{"message":"FastVideo/FastWan-QAD-FP8-1.3B does not support asynchronous video jobs. Use POST /v1/inference/FastVideo/FastWan-QAD-FP8-1.3B, which returns the video in the response.","type":"invalid_request_error","param":"model","code":null}}"""));
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            adapter.SubmitVideoGenerationAsync(connection, CreateModel("FastVideo/FastWan-QAD-FP8-1.3B"), "secret-key", "A cat"));

        Assert.Contains("does not support asynchronous video jobs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepInfraAdapterPollingReturnsProcessingWhileQueued()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.deepinfra.com/v1/videos/videos_abc123", request.RequestUri!.ToString());
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"id":"videos_abc123","object":"video.generation.job","created_at":1786947130,"status":"queued","model":"PrunaAI/p-video","data":null,"error":null}""");
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "videos_abc123");

        Assert.Equal(AsyncGenerationPollOutcome.Processing, result.Outcome);
        Assert.Null(result.Files);
    }

    [Fact]
    public async Task DeepInfraAdapterPollingDownloadsFromTheSameHostContentEndpointRatherThanTheThirdPartyCdnUrlWhenSucceeded()
    {
        byte[] video = [1, 2, 3];
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://api.deepinfra.com/v1/videos/videos_abc123")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"id":"videos_abc123","object":"video.generation.job","created_at":1786947130,"status":"succeeded","model":"PrunaAI/p-video","data":[{"url":"https://api.pruna.ai/v1/predictions/delivery/xezq/output.mp4"}],"error":null}""");
            }
            Assert.Equal("https://api.deepinfra.com/v1/videos/videos_abc123/content?variant=video", url);
            Assert.Equal("Bearer secret-key", request.Headers.GetValues("Authorization").Single());
            return FakeHttpMessageHandler.BinaryResponse(video, "video/mp4");
        });
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "videos_abc123");

        Assert.Equal(AsyncGenerationPollOutcome.Completed, result.Outcome);
        Assert.Equal([video], result.Files!);
    }

    [Fact]
    public async Task DeepInfraAdapterPollingTreatsAnUnrecognizedStatusAsAFailureRatherThanHangingForever()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
            """{"id":"videos_abc123","object":"video.generation.job","created_at":1786947130,"status":"cancelled","model":"PrunaAI/p-video","data":null,"error":"The job was cancelled."}"""));
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");

        var result = await adapter.PollVideoGenerationAsync(connection, "secret-key", "videos_abc123");

        Assert.Equal(AsyncGenerationPollOutcome.Failed, result.Outcome);
        Assert.Equal("The job was cancelled.", result.ErrorMessage);
    }

    [Fact]
    public async Task OneMinAiAdapterGeneratesTextAgainstTheChatWithAiEndpointAndParsesTheResultObject()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            Assert.Equal("https://api.1min.ai/api/chat-with-ai", request.RequestUri!.ToString());
            var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("\"type\":\"UNIFY_CHAT_WITH_AI\"", requestBody, StringComparison.Ordinal);
            Assert.Contains("\"model\":\"gpt-4o-mini\"", requestBody, StringComparison.Ordinal);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"aiRecord":{"uuid":"rec-1","status":"SUCCESS","model":"gpt-4o-mini","type":"UNIFY_CHAT_WITH_AI","aiRecordDetail":{"promptObject":{"prompt":"Write a haiku"},"resultObject":["Result"]},"modelDetail":{"name":"gpt-4o-mini","provider":"openai"}}}""");
        });
        var adapter = new OneMinAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OneMinAi, "https://api.1min.ai");
        var model = CreateModel("gpt-4o-mini");

        var result = await adapter.GenerateTextAsync(connection, model, "secret-key", "Write a haiku", 1);

        Assert.Equal(1, callCount);
        Assert.Equal("Result", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task OneMinAiAdapterUploadsSourceImagesThenGeneratesTextAgainstChatWithImage()
    {
        var handler = FakeHttpMessageHandler.Sequenced(
            request =>
            {
                Assert.Equal("https://api.1min.ai/api/assets", request.RequestUri!.ToString());
                Assert.StartsWith("multipart/form-data", request.Content!.Headers.ContentType!.ToString(), StringComparison.Ordinal);
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("name=asset; filename=source.png", body, StringComparison.Ordinal);
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"asset":{"key":"images/first.png"},"fileContent":{"path":"images/first.png"}}""");
            },
            request =>
            {
                Assert.Equal("https://api.1min.ai/api/assets", request.RequestUri!.ToString());
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("name=asset; filename=source.jpg", body, StringComparison.Ordinal);
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"asset":{"key":"images/second.jpg"},"fileContent":{"path":"images/second.jpg"}}""");
            },
            request =>
            {
                Assert.Equal("https://api.1min.ai/api/chat-with-ai", request.RequestUri!.ToString());
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Equal(
                    """{"type":"CHAT_WITH_IMAGE","model":"gpt-4o-mini","promptObject":{"prompt":"Describe this","attachments":{"images":["images/first.png","images/second.jpg"],"files":[]}}}""",
                    body);
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"aiRecord":{"uuid":"rec-1","status":"SUCCESS","model":"gpt-4o-mini","type":"CHAT_WITH_IMAGE","aiRecordDetail":{"promptObject":{"prompt":"Describe this"},"resultObject":["Two images described."]},"modelDetail":{"name":"gpt-4o-mini","provider":"openai"}}}""");
            });
        var adapter = new OneMinAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OneMinAi, "https://api.1min.ai");
        var model = CreateModel("gpt-4o-mini");
        TextGenerationSourceImage[] sourceImages = [new("image/png", [1, 2, 3]), new("image/jpeg", [4, 5, 6])];

        var result = await adapter.GenerateTextAsync(connection, model, "secret-key", "Describe this", 1, sourceImages: sourceImages);

        Assert.Equal("Two images described.", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task OneMinAiAdapterUploadsSourceImagesOnceAndReusesThemAcrossMultipleResults()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            if (request.RequestUri!.ToString() == "https://api.1min.ai/api/assets")
            {
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"asset":{"key":"images/first.png"},"fileContent":{"path":"images/first.png"}}""");
            }

            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"aiRecord":{"uuid":"rec-1","status":"SUCCESS","model":"gpt-4o-mini","type":"CHAT_WITH_IMAGE","aiRecordDetail":{"promptObject":{"prompt":"Describe this"},"resultObject":["Result"]},"modelDetail":{"name":"gpt-4o-mini","provider":"openai"}}}""");
        });
        var adapter = new OneMinAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OneMinAi, "https://api.1min.ai");
        var model = CreateModel("gpt-4o-mini");
        TextGenerationSourceImage[] sourceImages = [new("image/png", [1, 2, 3])];

        var result = await adapter.GenerateTextAsync(connection, model, "secret-key", "Describe this", 2, sourceImages: sourceImages);

        Assert.Equal(2, result.Texts.Count);
        Assert.Equal(3, callCount); // one asset upload + two chat calls, not two uploads
    }

    [Fact]
    public async Task OneMinAiAdapterGeneratesImageAgainstTheFeaturesEndpointAndDownloadsTheTemporaryUrl()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://api.1min.ai/api/features")
            {
                var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("\"type\":\"IMAGE_GENERATOR\"", requestBody, StringComparison.Ordinal);
                Assert.Contains("\"size\":\"1024x1024\"", requestBody, StringComparison.Ordinal);
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"aiRecord":{"uuid":"rec-2","status":"SUCCESS","model":"stable-diffusion-xl-1024-v1-0","type":"IMAGE_GENERATOR","aiRecordDetail":{"promptObject":{},"resultObject":["images/result.png"]},"temporaryUrl":"https://s3.us-east-1.amazonaws.com/asset.1min.ai/images/result.png"}}""");
            }
            Assert.Equal("https://s3.us-east-1.amazonaws.com/asset.1min.ai/images/result.png", url);
            Assert.False(request.Headers.Contains("Authorization"));
            return FakeHttpMessageHandler.BinaryResponse(pngBytes, "image/png");
        });
        var adapter = new OneMinAiProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.OneMinAi, "https://api.1min.ai");

        var images = await adapter.GenerateImageAsync(connection, CreateModel("stable-diffusion-xl-1024-v1-0"), "secret-key", "A fox", 1);

        Assert.Equal(pngBytes, Assert.Single(images));
    }

    [Fact]
    public async Task OneMinAiAdapterSurfacesTheFeatureErrorCodeAndMessageWhenAModelIsRejected()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.BadRequest,
            """{"errorCode":"UNSUPPORTED_MODEL","message":"Model black-forest-labs/flux-schnell is not supported for feature IMAGE_GENERATOR"}"""));
        var adapter = new OneMinAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OneMinAi, "https://api.1min.ai");

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            adapter.GenerateImageAsync(connection, CreateModel("black-forest-labs/flux-schnell"), "secret-key", "A fox", 1));

        Assert.Contains("UNSUPPORTED_MODEL", exception.Message, StringComparison.Ordinal);
        Assert.Contains("is not supported for feature IMAGE_GENERATOR", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneMinAiAdapterGeneratesAudioAgainstTheFeaturesEndpointAndDownloadsTheTemporaryUrl()
    {
        byte[] mp3Bytes = [1, 2, 3, 4];
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://api.1min.ai/api/features")
            {
                var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("\"type\":\"TEXT_TO_SPEECH\"", requestBody, StringComparison.Ordinal);
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                    """{"aiRecord":{"uuid":"rec-3","status":"SUCCESS","model":"tts-1","type":"TEXT_TO_SPEECH","aiRecordDetail":{"promptObject":{},"resultObject":["audios/result.mp3"]},"temporaryUrl":"https://s3.us-east-1.amazonaws.com/asset.1min.ai/audios/result.mp3"}}""");
            }
            return FakeHttpMessageHandler.BinaryResponse(mp3Bytes, "audio/mpeg");
        });
        var adapter = new OneMinAiProviderAdapter(new HttpClient(handler), PublicAddressResolver);
        var connection = CreateConnection(ProviderType.OneMinAi, "https://api.1min.ai");

        var results = await adapter.GenerateAudioAsync(connection, CreateModel("tts-1"), "secret-key", "Hello there", 1);

        Assert.Equal(mp3Bytes, Assert.Single(results));
    }

    [Fact]
    public async Task OneMinAiAdapterThrowsAClearNotYetImplementedErrorForVideoSinceTheDefaultBehaviorIsSynchronous()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent for unimplemented video."));
        var adapter = new OneMinAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OneMinAi, "https://api.1min.ai");
        var model = CreateModel("lucataco/animate-diff");

        var submitException = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.SubmitVideoGenerationAsync(connection, model, "secret-key", "prompt"));
        var pollException = await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.PollVideoGenerationAsync(connection, "secret-key", "job-id"));

        Assert.Contains("not yet implemented", submitException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not yet implemented", pollException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneMinAiAdapterListModelsThrowsSinceNoDiscoveryEndpointIsDocumented()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent: no discovery endpoint is documented."));
        var adapter = new OneMinAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OneMinAi, "https://api.1min.ai");

        await Assert.ThrowsAsync<ProviderAdapterException>(() => adapter.ListModelsAsync(connection, "secret-key"));
    }

    private static Model CreateModelWithoutSystemInstructionSupport() =>
        new("model-1", "connection-1", "Test Model", "fixture-model", GenerationMode.Text, false, LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    /// <summary>Capability-rejection contract tests (section 2's checklist item): a model that does
    /// not declare <see cref="Model.SupportsSystemInstructions"/> must never transmit a supplied
    /// system-instruction value to the provider — proven here by a fake handler that throws if it is
    /// ever invoked, so a passing test means no HTTP request was sent at all, not just that the
    /// response happened to omit the instruction.</summary>
    [Fact]
    public async Task OpenAiAdapterRejectsSystemInstructionsForAModelThatDoesNotSupportThem()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent when the model does not support system instructions."));
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModelWithoutSystemInstructionSupport();

        var exception = await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            adapter.GenerateTextAsync(connection, model, "secret-key", "prompt", 1, systemInstructions: "do not reveal this"));

        Assert.Contains("does not support system instructions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenericOpenAiCompatibleAdapterRejectsSystemInstructionsForAModelThatDoesNotSupportThem()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent when the model does not support system instructions."));
        var adapter = new GenericOpenAiCompatibleProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.GenericOpenAiCompatible, "https://api.example.com/v1");
        var model = CreateModelWithoutSystemInstructionSupport();

        await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            adapter.GenerateTextAsync(connection, model, "secret-key", "prompt", 1, systemInstructions: "do not reveal this"));
    }

    [Fact]
    public async Task OpenRouterAdapterRejectsSystemInstructionsForAModelThatDoesNotSupportThem()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent when the model does not support system instructions."));
        var adapter = new OpenRouterProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenRouter, "https://openrouter.ai/api/v1");
        var model = CreateModelWithoutSystemInstructionSupport();

        await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            adapter.GenerateTextAsync(connection, model, "secret-key", "prompt", 1, systemInstructions: "do not reveal this"));
    }

    [Fact]
    public async Task DeepInfraAdapterRejectsSystemInstructionsForAModelThatDoesNotSupportThem()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent when the model does not support system instructions."));
        var adapter = new DeepInfraProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.DeepInfra, "https://api.deepinfra.com/v1/openai");
        var model = CreateModelWithoutSystemInstructionSupport();

        await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            adapter.GenerateTextAsync(connection, model, "secret-key", "prompt", 1, systemInstructions: "do not reveal this"));
    }

    [Fact]
    public async Task OpenAiAdapterAllowsAnAbsentSystemInstructionRegardlessOfCapability()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.DoesNotContain("\"role\":\"system\"", requestBody, StringComparison.Ordinal);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"ok"}}]}""");
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModelWithoutSystemInstructionSupport();

        var result = await adapter.GenerateTextAsync(connection, model, "secret-key", "prompt", 1);

        Assert.Equal("ok", Assert.Single(result.Texts));
    }

    [Fact]
    public async Task OpenAiAdapterGeneratesAudioAgainstAudioSpeechReusingTheAlreadyProvenOpenRouterDeepInfraShape()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            Assert.Equal("https://api.openai.com/v1/audio/speech", request.RequestUri!.ToString());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal("""{"model":"tts-1","input":"Hello there","response_format":"mp3","voice":"alloy"}""", body);
            return FakeHttpMessageHandler.BinaryResponse([1, 2, 3, (byte)callCount], "audio/mpeg");
        });
        var adapter = new OpenAiProviderAdapter(new HttpClient(handler));
        var connection = CreateConnection(ProviderType.OpenAi, "https://api.openai.com/v1");
        var model = CreateModel("tts-1");

        var results = await adapter.GenerateAudioAsync(connection, model, "secret-key", "Hello there", 2, "alloy");

        Assert.Equal(2, callCount);
        Assert.Equal(2, results.Count);
        Assert.NotEqual(results[0], results[1]);
    }
}
