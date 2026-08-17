using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mellow.SlopFactory.Tests;

/// <summary>
/// Stateful, in-memory provider used by adapter and queue tests. It deliberately implements a
/// compact OpenAI-shaped surface rather than starting a loopback listener, so ordinary tests remain
/// deterministic and never require network permission.
/// </summary>
internal sealed class FakeProviderServer : HttpMessageHandler
{
    private static readonly byte[] DefaultImageBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] DefaultVideoBytes = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70];
    private readonly ConcurrentDictionary<string, int> _pollCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FakeProviderRequest> _requests = new();
    private int _jobSequence;
    private int _remainingRateLimitedRequests;

    public FakeProviderServer(FakeProviderScenario? scenario = null)
    {
        Scenario = scenario ?? new FakeProviderScenario();
        _remainingRateLimitedRequests = Scenario.RateLimitRequests;
    }

    public static Uri BaseUri { get; } = new("https://fake-provider.test/v1/");

    public FakeProviderScenario Scenario { get; }

    public IReadOnlyList<FakeProviderRequest> Requests => _requests.ToArray();

    public HttpClient CreateClient() => new(this, disposeHandler: false) { BaseAddress = BaseUri };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _requests.Enqueue(new FakeProviderRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization is not null));

        if (Scenario.NextTransportException is { } transportException)
        {
            Scenario.NextTransportException = null;
            throw transportException;
        }

        if (Scenario.WaitForCancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        if (!HasValidAuthorization(request))
        {
            return Json(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid credential"}}""");
        }

        if (Interlocked.Decrement(ref _remainingRateLimitedRequests) >= 0)
        {
            var limited = Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"rate limited"}}""");
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(Scenario.RetryAfter);
            return limited;
        }

        if (Scenario.NextErrorStatus is { } errorStatus)
        {
            Scenario.NextErrorStatus = null;
            return Json(errorStatus, JsonSerializer.Serialize(new
            {
                error = new { message = $"forced {(int)errorStatus} response" }
            }));
        }

        var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        if (request.Method == HttpMethod.Get && path.EndsWith("/models", StringComparison.Ordinal))
        {
            return Scenario.ReturnMalformedJson
                ? Json(HttpStatusCode.OK, "{not-json")
                : Json(HttpStatusCode.OK, """{"data":[{"id":"fake-text","name":"Fake Text"},{"id":"fake-image","name":"Fake Image"},{"id":"fake-video","name":"Fake Video"}]}""");
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return ChatCompletion(body);
        }

        if (request.Method == HttpMethod.Post &&
            (path.EndsWith("/images/generations", StringComparison.Ordinal) || path.EndsWith("/images", StringComparison.Ordinal)))
        {
            return ImageGeneration(body);
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/audio/speech", StringComparison.Ordinal))
        {
            return Binary(HttpStatusCode.OK, Scenario.AudioBytes, "audio/mpeg");
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/videos", StringComparison.Ordinal))
        {
            var jobId = $"job-{Interlocked.Increment(ref _jobSequence).ToString(CultureInfo.InvariantCulture)}";
            var pollUrl = new Uri(BaseUri, $"videos/{jobId}");
            return Json(HttpStatusCode.Accepted, JsonSerializer.Serialize(new
            {
                id = jobId,
                polling_url = pollUrl,
                status = "pending"
            }));
        }

        if (request.Method == HttpMethod.Get && TryGetVideoJobId(path, out var polledJobId))
        {
            var pollCount = _pollCounts.AddOrUpdate(polledJobId, 1, (_, current) => current + 1);
            if (pollCount <= Scenario.AsyncPollsBeforeCompletion)
            {
                return Json(HttpStatusCode.OK, """{"status":"pending"}""");
            }

            var resultUrl = new Uri(BaseUri, $"results/{polledJobId}");
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                id = polledJobId,
                status = "completed",
                unsigned_urls = new[] { resultUrl }
            }));
        }

        if (request.Method == HttpMethod.Get && path.Contains("/results/", StringComparison.Ordinal))
        {
            if (Scenario.RedirectResultDownloads)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                response.Headers.Location = new Uri(BaseUri, $"downloads/{path.Split('/').Last()}");
                return response;
            }

            return Binary(HttpStatusCode.OK, Scenario.VideoBytes, "video/mp4");
        }

        if (request.Method == HttpMethod.Get && path.Contains("/downloads/", StringComparison.Ordinal))
        {
            return Binary(HttpStatusCode.OK, Scenario.VideoBytes, "video/mp4");
        }

        return Json(HttpStatusCode.NotFound, """{"error":{"message":"fake route not found"}}""");
    }

    private bool HasValidAuthorization(HttpRequestMessage request)
    {
        if (Scenario.RequiredBearerToken is null) return true;
        return request.Headers.Authorization is { Scheme: "Bearer" } authorization &&
               string.Equals(authorization.Parameter, Scenario.RequiredBearerToken, StringComparison.Ordinal);
    }

    private HttpResponseMessage ChatCompletion(string? body)
    {
        if (Scenario.ModerateTextResponses)
        {
            return Json(HttpStatusCode.OK, """{"choices":[{"finish_reason":"content_filter","message":{"content":""}}]}""");
        }

        using var document = ParseBody(body);
        var root = document.RootElement;
        if (root.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
        {
            if (Scenario.ReturnTruncatedStream)
            {
                return EventStream("data: {\"choices\":[{\"delta\":{\"content\":\"Fake \"}}]}\n\n");
            }

            return FakeHttpMessageHandler.StreamingResponse(
                """{"choices":[{"delta":{"content":"Fake "}}]}""",
                """{"choices":[{"delta":{"content":"response"}}]}""");
        }

        var requestedResultCount = root.TryGetProperty("n", out var count) && count.TryGetInt32(out var value) ? value : 1;
        var resultCount = Scenario.ReturnedResultCount ?? requestedResultCount;
        var choices = Enumerable.Range(1, Math.Max(0, resultCount))
            .Select(index => new { message = new { content = $"Fake response {index}" } });
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            choices,
            usage = new { prompt_tokens = 4, completion_tokens = 3 }
        }));
    }

    private HttpResponseMessage ImageGeneration(string? body)
    {
        using var document = ParseBody(body);
        var root = document.RootElement;
        var requestedResultCount = root.TryGetProperty("n", out var count) && count.TryGetInt32(out var value) ? value : 1;
        var resultCount = Scenario.ReturnedResultCount ?? requestedResultCount;
        var encoded = Scenario.ReturnInvalidImageBase64 ? "not-base64" : Convert.ToBase64String(DefaultImageBytes);
        var data = Enumerable.Range(0, Math.Max(0, resultCount)).Select(_ => new { b64_json = encoded });
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data }));
    }

    private static JsonDocument ParseBody(string? body)
    {
        try
        {
            return JsonDocument.Parse(body ?? "{}");
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static bool TryGetVideoJobId(string path, out string jobId)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var videosIndex = Array.FindLastIndex(segments, segment => segment == "videos");
        if (videosIndex >= 0 && videosIndex == segments.Length - 2)
        {
            jobId = segments[^1];
            return true;
        }

        jobId = string.Empty;
        return false;
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage EventStream(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "text/event-stream") };

    private static HttpResponseMessage Binary(HttpStatusCode statusCode, byte[] bytes, string mediaType)
    {
        var response = new HttpResponseMessage(statusCode) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    internal sealed class FakeProviderScenario
    {
        public string? RequiredBearerToken { get; init; } = "test-key";
        public int RateLimitRequests { get; init; }
        public TimeSpan RetryAfter { get; init; } = TimeSpan.Zero;
        public int AsyncPollsBeforeCompletion { get; init; } = 1;
        public bool ModerateTextResponses { get; init; }
        public bool RedirectResultDownloads { get; init; }
        public bool ReturnMalformedJson { get; init; }
        public bool ReturnTruncatedStream { get; init; }
        public bool ReturnInvalidImageBase64 { get; init; }
        public int? ReturnedResultCount { get; init; }
        public bool WaitForCancellation { get; init; }
        public Exception? NextTransportException { get; set; }
        public HttpStatusCode? NextErrorStatus { get; set; }
        public byte[] AudioBytes { get; init; } = [0x49, 0x44, 0x33, 0x04];
        public byte[] VideoBytes { get; init; } = DefaultVideoBytes;
    }
}

internal sealed record FakeProviderRequest(HttpMethod Method, Uri Uri, string? Body, bool HasAuthorization);
