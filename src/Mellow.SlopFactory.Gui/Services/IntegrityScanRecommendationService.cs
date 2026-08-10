namespace Mellow.SlopFactory.Gui.Services;

public enum IntegrityScanRecommendationReason { WatcherOverflow, UnsafeVolumeRemoval, StorageInconsistency }

public sealed class IntegrityScanRecommendationService
{
    private readonly object _gate = new();
    private readonly HashSet<IntegrityScanRecommendationReason> _reasons = [];
    public event EventHandler? Changed;
    public bool IsRecommended { get { lock (_gate) return _reasons.Count > 0; } }
    public IReadOnlyCollection<IntegrityScanRecommendationReason> Reasons { get { lock (_gate) return _reasons.ToArray(); } }

    public void Recommend(IntegrityScanRecommendationReason reason)
    {
        lock (_gate) _reasons.Add(reason);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Defer()
    {
        lock (_gate) _reasons.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate) _reasons.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
