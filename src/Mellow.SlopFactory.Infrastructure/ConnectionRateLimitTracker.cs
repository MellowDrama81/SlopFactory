using System.Collections.Concurrent;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure;

public sealed class ConnectionRateLimitTracker : IConnectionRateLimitTracker
{
    private readonly ConcurrentDictionary<string, RateLimitObservation> _observations = new();

    public RateLimitObservation? GetObservation(string connectionId) =>
        _observations.TryGetValue(connectionId, out var observation) ? observation : null;

    public void Record(string connectionId, RateLimitObservation observation) => _observations[connectionId] = observation;
}
