using System.Net;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class FakeHttpMessageHandlerTests
{
    [Fact]
    public async Task SequencedReturnsEachResponseInOrderForSuccessiveRequests()
    {
        var handler = FakeHttpMessageHandler.Sequenced(
            _ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Accepted, """{"status":"processing"}"""),
            _ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Accepted, """{"status":"processing"}"""),
            _ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"status":"completed"}"""));
        using var client = new HttpClient(handler);

        var first = await client.GetAsync("https://example.test/job/1");
        var second = await client.GetAsync("https://example.test/job/1");
        var third = await client.GetAsync("https://example.test/job/1");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Contains("processing", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Contains("completed", await third.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SequencedThrowsWhenCalledMoreTimesThanResponsesWereSupplied()
    {
        var handler = FakeHttpMessageHandler.Sequenced(FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, "{}"));
        using var client = new HttpClient(handler);

        await client.GetAsync("https://example.test/job/1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://example.test/job/1"));
    }

    [Fact]
    public async Task RateLimitedCarriesTheRetryAfterHeaderWhenSupplied()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.RateLimited(TimeSpan.FromSeconds(5)));
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.test/generate");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
    }

    [Fact]
    public async Task RedirectCarriesTheLocationHeaderAndDoesNotAutoFollow()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Redirect(HttpStatusCode.Found, "https://other-host.test/result/1"));
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.test/result/1");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://other-host.test/result/1", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task BinaryResponseReturnsExactBytesAndDeclaredMediaType()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.BinaryResponse(bytes, "image/png"));
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.test/result.png");
        var downloaded = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, downloaded);
    }

    [Fact]
    public async Task StreamingResponseEmitsEachEventFollowedByTheDoneTerminator()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.StreamingResponse(
            """{"delta":"Hello"}""",
            """{"delta":" world"}"""));
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.test/chat/completions");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("""data: {"delta":"Hello"}""", body, StringComparison.Ordinal);
        Assert.Contains("""data: {"delta":" world"}""", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
    }
}
