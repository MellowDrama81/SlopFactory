using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Gui.Services;

public sealed class GenerationNotificationCoordinator
{
    private const string EnabledPreferenceKey = "slopfactory.notifications.generationenabled";

    private readonly GenerationQueueService _queue;
    private readonly IAppLifecycleState _lifecycle;
    private readonly INotificationService _notifications;
    private readonly IAppPreferenceStore _preferences;
    private bool _started;

    public GenerationNotificationCoordinator(GenerationQueueService queue, IAppLifecycleState lifecycle, INotificationService notifications, IAppPreferenceStore preferences)
    {
        _queue = queue;
        _lifecycle = lifecycle;
        _notifications = notifications;
        _preferences = preferences;
    }

    public bool Enabled => _preferences.ReadString(EnabledPreferenceKey, bool.FalseString) == bool.TrueString;

    /// <summary>Set/cleared by the generation-history detail page so its own record never re-shows a redundant OS notification.</summary>
    public string? VisibleGenerationRecordId { get; set; }

    /// <summary>Raised when a finished job should surface an OS notification. Localizing and actually showing it is left to a UI-layer subscriber (which already has <c>IStringLocalizer</c> access), not this service.</summary>
    public event EventHandler<GenerationRecord>? NotifyRequested;

    public async Task<bool> SetEnabledAsync(bool value)
    {
        if (value && !await _notifications.RequestPermissionAsync().ConfigureAwait(false))
        {
            _preferences.WriteString(EnabledPreferenceKey, bool.FalseString);
            return false;
        }
        _preferences.WriteString(EnabledPreferenceKey, value.ToString());
        return true;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _queue.JobCompleted += OnJobCompleted;
    }

    private void OnJobCompleted(object? sender, GenerationJobOutcome outcome)
    {
        if (!Enabled || _lifecycle.IsForeground || outcome.Record is null) return;
        if (VisibleGenerationRecordId == outcome.Record.Id) return;
        NotifyRequested?.Invoke(this, outcome.Record);
    }
}
