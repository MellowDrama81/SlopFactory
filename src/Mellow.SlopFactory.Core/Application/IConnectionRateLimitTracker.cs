using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Application;

/// <summary>
/// Holds each connection's most recently observed rate-limit state in memory for the lifetime of
/// the process, so the queue scheduler and settings UI can read it without every caller re-deriving
/// it from raw response headers. Deliberately not persisted: the data is only meaningful for a few
/// minutes (per the reset window), so surviving a restart would just show stale numbers.
/// </summary>
public interface IConnectionRateLimitTracker
{
    RateLimitObservation? GetObservation(string connectionId);
    void Record(string connectionId, RateLimitObservation observation);
}
