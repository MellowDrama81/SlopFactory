using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Mellow.SlopFactory.Tests;

/// <summary>
/// A shared, reusable fake HTTP transport for provider-adapter tests. The single-responder
/// constructors below are the original minimal shape every existing adapter test already uses;
/// <see cref="Sequenced(Func{HttpRequestMessage,HttpResponseMessage}[])"/> and the canned-response
/// builders extend it to cover the scenarios the Testing section of plan.md calls for — streaming,
/// asynchronous job submission/polling, rate limits, redirects and binary downloads — without every
/// adapter test hand-rolling its own call-counting closure.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = (request, _) => Task.FromResult(responder(request));
    }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _responder(request, cancellationToken);

    /// <summary>
    /// Returns each response in order for successive requests, one per call — the shape needed to
    /// test a submit-then-poll asynchronous job (submit, poll pending, poll pending, poll complete)
    /// or a multi-hop redirect chain without a hand-rolled call-count field in every test. Throws if
    /// called more times than responses were supplied, catching a test asserting the wrong call count.
    /// </summary>
    public static FakeHttpMessageHandler Sequenced(params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        var index = 0;
        var gate = new object();
        return new FakeHttpMessageHandler(request =>
        {
            int current;
            lock (gate) current = index++;
            if (current >= responders.Length) throw new InvalidOperationException($"The fake transport received more requests ({current + 1}) than the {responders.Length} responses it was configured with.");
            return responders[current](request);
        });
    }

    /// <summary>Overload for when each step's response does not need to inspect the request.</summary>
    public static FakeHttpMessageHandler Sequenced(params HttpResponseMessage[] responses) =>
        Sequenced(responses.Select(response => (Func<HttpRequestMessage, HttpResponseMessage>)(_ => response)).ToArray());

    public static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A 429 response with an optional Retry-After header, mirroring a provider rate limit.</summary>
    public static HttpResponseMessage RateLimited(TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
        if (retryAfter is { } value) response.Headers.RetryAfter = new RetryConditionHeaderValue(value);
        return response;
    }

    /// <summary>A same- or cross-host redirect response with the given Location.</summary>
    public static HttpResponseMessage Redirect(HttpStatusCode statusCode, string location)
    {
        var response = new HttpResponseMessage(statusCode);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    /// <summary>A raw binary download response, for provider-hosted result URLs and asset downloads.</summary>
    public static HttpResponseMessage BinaryResponse(byte[] bytes, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    /// <summary>
    /// A chunked `text/event-stream` response built from the given already-serialized per-event JSON
    /// payloads (OpenAI-style Server-Sent-Events streaming), each written as its own `data: ...`
    /// line, followed by the standard `data: [DONE]` terminator.
    /// </summary>
    public static HttpResponseMessage StreamingResponse(params string[] eventDataJsonPayloads)
    {
        var builder = new StringBuilder();
        foreach (var payload in eventDataJsonPayloads) builder.Append("data: ").Append(payload).Append("\n\n");
        builder.Append("data: [DONE]\n\n");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream") };
        return response;
    }
}
