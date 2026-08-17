using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Providers;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class FakeProviderServerTests
{
    private static Connection Connection(ProviderType providerType = ProviderType.OpenAi) =>
        new("connection-1", "Fake Provider", providerType, FakeProviderServer.BaseUri.ToString().TrimEnd('/'),
            "Authorization", "Bearer", false, ConnectionTestStatus.Untested, null, null,
            LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static Model Model(GenerationMode mode = GenerationMode.Text) =>
        new("model-1", "connection-1", "Fake Model", $"fake-{mode.ToString().ToLowerInvariant()}", mode,
            true, LibraryRecordState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task OpenAiAdapterUsesFakeProviderForAuthenticationDiscoveryAndSynchronousGeneration()
    {
        using var server = new FakeProviderServer();
        using var client = server.CreateClient();
        var adapter = new OpenAiProviderAdapter(client);

        var failed = await adapter.TestConnectionAsync(Connection(), "wrong-key");
        var models = await adapter.ListModelsAsync(Connection(), "test-key");
        var result = await adapter.GenerateTextAsync(Connection(), Model(), "test-key", "Write safely", 2);

        Assert.False(failed.Success);
        Assert.Equal(3, models.Count);
        Assert.Equal(["Fake response 1", "Fake response 2"], result.Texts);
        Assert.Equal(4, result.PromptTokens);
        Assert.Equal(3, result.CompletionTokens);
        Assert.Equal(3, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.True(request.HasAuthorization));
    }

    [Fact]
    public async Task FakeProviderSupportsStreamingAndModerationResponses()
    {
        using var streamingServer = new FakeProviderServer();
        using var streamingClient = streamingServer.CreateClient();
        using var streamingRequest = AuthorizedPost("chat/completions", """{"stream":true}""");

        var streamed = await streamingClient.SendAsync(streamingRequest);
        var streamBody = await streamed.Content.ReadAsStringAsync();

        Assert.Equal("text/event-stream", streamed.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Fake ", streamBody, StringComparison.Ordinal);
        Assert.Contains("response", streamBody, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", streamBody, StringComparison.Ordinal);

        using var moderationServer = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            ModerateTextResponses = true
        });
        using var moderationClient = moderationServer.CreateClient();
        var adapter = new OpenAiProviderAdapter(moderationClient);

        var moderated = await adapter.GenerateTextAsync(Connection(), Model(), "test-key", "blocked fixture", 1);

        Assert.Empty(moderated.Texts);
        Assert.Equal(1, moderated.SafetyBlockedCount);
    }

    [Fact]
    public async Task FakeProviderSupportsAsynchronousJobsAndBinaryDownloads()
    {
        using var server = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            AsyncPollsBeforeCompletion = 1
        });
        using var client = server.CreateClient();

        using var submitRequest = AuthorizedPost("videos", """{"model":"fake-video","prompt":"test"}""");
        var submission = await client.SendAsync(submitRequest);
        var submissionBody = await submission.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, submission.StatusCode);
        Assert.Contains("job-1", submissionBody, StringComparison.Ordinal);

        using var firstPollRequest = AuthorizedGet("videos/job-1");
        var firstPoll = await client.SendAsync(firstPollRequest);
        Assert.Contains("pending", await firstPoll.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var secondPollRequest = AuthorizedGet("videos/job-1");
        var secondPoll = await client.SendAsync(secondPollRequest);
        Assert.Contains("completed", await secondPoll.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var downloadRequest = AuthorizedGet("results/job-1");
        var download = await client.SendAsync(downloadRequest);
        Assert.Equal("video/mp4", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(server.Scenario.VideoBytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task FakeProviderSupportsRateLimitsRedirectsMalformedJsonAndErrors()
    {
        using var limitedServer = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            RateLimitRequests = 1,
            RetryAfter = TimeSpan.FromSeconds(2)
        });
        using var limitedClient = limitedServer.CreateClient();
        using var limitedRequest = AuthorizedGet("models");
        var limited = await limitedClient.SendAsync(limitedRequest);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), limited.Headers.RetryAfter!.Delta);

        using var redirectServer = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            RedirectResultDownloads = true
        });
        using var redirectClient = redirectServer.CreateClient();
        using var redirectRequest = AuthorizedGet("results/job-1");
        var redirected = await redirectClient.SendAsync(redirectRequest);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, redirected.StatusCode);
        Assert.Contains("/downloads/job-1", redirected.Headers.Location!.ToString(), StringComparison.Ordinal);

        using var malformedServer = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            ReturnMalformedJson = true
        });
        using var malformedClient = malformedServer.CreateClient();
        var malformedAdapter = new OpenAiProviderAdapter(malformedClient);
        await Assert.ThrowsAsync<ProviderAdapterException>(() => malformedAdapter.ListModelsAsync(Connection(), "test-key"));

        using var errorServer = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            NextErrorStatus = HttpStatusCode.ServiceUnavailable
        });
        using var errorClient = errorServer.CreateClient();
        using var errorRequest = AuthorizedGet("models");
        var error = await errorClient.SendAsync(errorRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Fact]
    public async Task FakeProviderInjectsDisconnectAndCancellationWithoutARealNetwork()
    {
        using var disconnectServer = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            NextTransportException = new HttpRequestException("fixture disconnect")
        });
        using var disconnectClient = disconnectServer.CreateClient();
        var adapter = new OpenAiProviderAdapter(disconnectClient);

        await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ListModelsAsync(Connection(), "test-key"));

        using var cancellationServer = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            WaitForCancellation = true
        });
        using var cancellationClient = cancellationServer.CreateClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var request = AuthorizedGet("models");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellationClient.SendAsync(request, cancellation.Token));
    }

    [Fact]
    public async Task FakeProviderCanReturnTruncatedStreamsAndPartialOrMalformedMediaResults()
    {
        using var truncatedServer = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            ReturnTruncatedStream = true
        });
        using var truncatedClient = truncatedServer.CreateClient();
        using var streamRequest = AuthorizedPost("chat/completions", """{"stream":true}""");
        var stream = await truncatedClient.SendAsync(streamRequest);
        var streamBody = await stream.Content.ReadAsStringAsync();
        Assert.Contains("data:", streamBody, StringComparison.Ordinal);
        Assert.DoesNotContain("[DONE]", streamBody, StringComparison.Ordinal);

        using var partialServer = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            ReturnedResultCount = 1
        });
        using var partialClient = partialServer.CreateClient();
        var partialAdapter = new OpenAiProviderAdapter(partialClient);
        var partial = await partialAdapter.GenerateImageAsync(Connection(), Model(GenerationMode.Image), "test-key", "fixture", 2);
        Assert.Single(partial);

        using var malformedServer = new FakeProviderServer(new FakeProviderServer.FakeProviderScenario
        {
            ReturnInvalidImageBase64 = true
        });
        using var malformedClient = malformedServer.CreateClient();
        var malformedAdapter = new OpenAiProviderAdapter(malformedClient);
        await Assert.ThrowsAsync<ProviderAdapterException>(() =>
            malformedAdapter.GenerateImageAsync(Connection(), Model(GenerationMode.Image), "test-key", "fixture", 1));
    }

    private static HttpRequestMessage AuthorizedGet(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        return request;
    }

    private static HttpRequestMessage AuthorizedPost(string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        return request;
    }
}
