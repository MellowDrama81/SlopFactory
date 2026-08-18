using System.Text.RegularExpressions;

namespace Mellow.SlopFactory.Domain;

/// <summary>
/// A single connection's most recently observed per-connection
/// rate-limit state (last observed limit, remaining, reset time). Request and token dimensions are
/// independent since a provider can report either, both or neither on a given response.
/// </summary>
public sealed record RateLimitObservation(
    DateTimeOffset ObservedAt,
    int? LimitRequests,
    int? RemainingRequests,
    string? ResetRequestsRaw,
    TimeSpan? ResetRequestsIn,
    int? LimitTokens,
    int? RemainingTokens,
    string? ResetTokensRaw,
    TimeSpan? ResetTokensIn)
{
    public bool HasAnyData => LimitRequests is not null || RemainingRequests is not null || LimitTokens is not null || RemainingTokens is not null;
}

/// <summary>
/// Parses the OpenAI-documented <c>x-ratelimit-*</c> response headers
/// (limit/remaining/reset for both requests and tokens). Confirmed against OpenAI's own API
/// documentation, which every adapter routed through <c>OpenAiCompatibleProtocol</c> emulates for
/// its request/response bodies — but since only OpenAI's docs confirm these exact header names and
/// the reset-value duration-string format, parsing is purely defensive: headers that are absent or
/// don't match the expected shape are simply not reported, never guessed at or fabricated for a
/// provider that hasn't confirmed the same contract.
/// </summary>
public static class RateLimitHeaderParser
{
    // Matches a Go-style duration string as OpenAI documents for its reset headers (e.g. "1s",
    // "6m0s", "1h2m3.5s", "500ms"). Each component is a decimal number immediately followed by its
    // unit; components may repeat and combine in descending unit order.
    private static readonly Regex DurationComponent = new(@"(?<value>\d+(\.\d+)?)(?<unit>h|ms|m|s)", RegexOptions.Compiled);

    public static RateLimitObservation? TryParse(IReadOnlyDictionary<string, string> headers, DateTimeOffset observedAt)
    {
        var limitRequests = TryGetInt(headers, "x-ratelimit-limit-requests");
        var remainingRequests = TryGetInt(headers, "x-ratelimit-remaining-requests");
        var resetRequestsRaw = TryGetString(headers, "x-ratelimit-reset-requests");
        var limitTokens = TryGetInt(headers, "x-ratelimit-limit-tokens");
        var remainingTokens = TryGetInt(headers, "x-ratelimit-remaining-tokens");
        var resetTokensRaw = TryGetString(headers, "x-ratelimit-reset-tokens");

        var observation = new RateLimitObservation(
            observedAt, limitRequests, remainingRequests, resetRequestsRaw, TryParseDuration(resetRequestsRaw),
            limitTokens, remainingTokens, resetTokensRaw, TryParseDuration(resetTokensRaw));
        return observation.HasAnyData ? observation : null;
    }

    public static TimeSpan? TryParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var matches = DurationComponent.Matches(value);
        if (matches.Count == 0) return null;

        var total = TimeSpan.Zero;
        var consumedLength = 0;
        foreach (Match match in matches)
        {
            if (!double.TryParse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture, out var amount)) return null;
            total += match.Groups["unit"].Value switch
            {
                "h" => TimeSpan.FromHours(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "ms" => TimeSpan.FromMilliseconds(amount),
                "s" => TimeSpan.FromSeconds(amount),
                _ => TimeSpan.Zero
            };
            consumedLength += match.Length;
        }
        // If the matched components don't account for the whole string, something unexpected is
        // mixed in (e.g. a format this provider doesn't actually share with OpenAI) — report nothing
        // rather than a partial, possibly-wrong duration.
        return consumedLength == value.Trim().Length ? total : null;
    }

    private static int? TryGetInt(IReadOnlyDictionary<string, string> headers, string key) =>
        headers.TryGetValue(key, out var value) && int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? TryGetString(IReadOnlyDictionary<string, string> headers, string key) =>
        headers.TryGetValue(key, out var value) ? value : null;
}
