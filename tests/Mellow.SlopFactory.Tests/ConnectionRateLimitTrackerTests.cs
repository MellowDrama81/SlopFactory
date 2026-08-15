using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure;
using Xunit;

namespace Mellow.SlopFactory.Tests;

/// <summary>
/// <see cref="ConnectionRateLimitTracker"/> is registered as a single application-wide DI singleton
/// (not scoped per library), so it's the one piece of state this milestone introduced that could, if
/// keyed wrong, leak a rate-limit observation from one connection — potentially in a different
/// library entirely — into another. It's keyed strictly by connection ID, which
/// <c>LibraryRules.NewId()</c> never repeats across libraries, so this proves that isolation holds.
/// </summary>
public sealed class ConnectionRateLimitTrackerTests
{
    [Fact]
    public void ObservationsForDifferentConnectionsNeverLeakIntoEachOther()
    {
        var tracker = new ConnectionRateLimitTracker();
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var first = new RateLimitObservation(now, 5000, 10, "1s", TimeSpan.FromSeconds(1), null, null, null, null);
        var second = new RateLimitObservation(now, 3000, 0, "30s", TimeSpan.FromSeconds(30), null, null, null, null);

        tracker.Record("connection-a", first);
        tracker.Record("connection-b", second);

        Assert.Equal(first, tracker.GetObservation("connection-a"));
        Assert.Equal(second, tracker.GetObservation("connection-b"));
        Assert.NotEqual(tracker.GetObservation("connection-a"), tracker.GetObservation("connection-b"));
    }

    [Fact]
    public void AnUnknownConnectionHasNoObservation()
    {
        var tracker = new ConnectionRateLimitTracker();
        Assert.Null(tracker.GetObservation("never-seen"));
    }

    [Fact]
    public void RecordingAgainForTheSameConnectionReplacesRatherThanMergesTheObservation()
    {
        var tracker = new ConnectionRateLimitTracker();
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        tracker.Record("connection-a", new RateLimitObservation(now, 5000, 10, "1s", TimeSpan.FromSeconds(1), null, null, null, null));
        var updated = new RateLimitObservation(now.AddSeconds(1), 5000, 9, "1s", TimeSpan.FromSeconds(1), null, null, null, null);

        tracker.Record("connection-a", updated);

        Assert.Equal(updated, tracker.GetObservation("connection-a"));
    }
}
