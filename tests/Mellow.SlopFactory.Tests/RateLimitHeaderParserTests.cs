using Mellow.SlopFactory.Domain;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class RateLimitHeaderParserTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoRecognizedHeadersReturnsNull()
    {
        var observation = RateLimitHeaderParser.TryParse(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["content-type"] = "application/json" }, ObservedAt);
        Assert.Null(observation);
    }

    [Fact]
    public void ParsesRequestAndTokenLimitsIndependently()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-ratelimit-limit-requests"] = "5000",
            ["x-ratelimit-remaining-requests"] = "4999",
            ["x-ratelimit-reset-requests"] = "1s",
            ["x-ratelimit-limit-tokens"] = "160000",
            ["x-ratelimit-remaining-tokens"] = "159968",
            ["x-ratelimit-reset-tokens"] = "6m0s"
        };

        var observation = RateLimitHeaderParser.TryParse(headers, ObservedAt);

        Assert.NotNull(observation);
        Assert.Equal(5000, observation.LimitRequests);
        Assert.Equal(4999, observation.RemainingRequests);
        Assert.Equal(TimeSpan.FromSeconds(1), observation.ResetRequestsIn);
        Assert.Equal(160000, observation.LimitTokens);
        Assert.Equal(159968, observation.RemainingTokens);
        Assert.Equal(TimeSpan.FromMinutes(6), observation.ResetTokensIn);
        Assert.Equal(ObservedAt, observation.ObservedAt);
    }

    [Fact]
    public void HeadersAreCaseInsensitive()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-RateLimit-Remaining-Requests"] = "0"
        };
        var observation = RateLimitHeaderParser.TryParse(headers, ObservedAt);
        Assert.Equal(0, observation?.RemainingRequests);
    }

    [Fact]
    public void MalformedNumericValueIsSimplyOmittedNotThrown()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-ratelimit-remaining-requests"] = "not-a-number",
            ["x-ratelimit-limit-requests"] = "5000"
        };
        var observation = RateLimitHeaderParser.TryParse(headers, ObservedAt);
        Assert.Null(observation!.RemainingRequests);
        Assert.Equal(5000, observation.LimitRequests);
    }

    [Theory]
    [InlineData("1s", 1)]
    [InlineData("6m0s", 360)]
    [InlineData("1h2m3s", 3723)]
    [InlineData("500ms", 0.5)]
    [InlineData("1.5s", 1.5)]
    public void ParsesGoStyleDurationStrings(string raw, double expectedSeconds)
    {
        var parsed = RateLimitHeaderParser.TryParseDuration(raw);
        Assert.NotNull(parsed);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), parsed.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("5 apples")]
    [InlineData("1x")]
    public void UnrecognizedDurationFormatsReturnNullRatherThanAGuess(string raw)
    {
        Assert.Null(RateLimitHeaderParser.TryParseDuration(raw));
    }

    [Fact]
    public void NullDurationReturnsNull()
    {
        Assert.Null(RateLimitHeaderParser.TryParseDuration(null));
    }
}
