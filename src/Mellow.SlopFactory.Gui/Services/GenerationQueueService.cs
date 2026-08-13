using System.Globalization;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Providers;

namespace Mellow.SlopFactory.Gui.Services;

public enum GenerationJobPhase
{
    Queued = 0,
    Running = 1
}

public sealed record GenerationJobSnapshot(
    string DraftId,
    string SubmittedTabTitle,
    GenerationMode Mode,
    string ModelId,
    string Prompt,
    string? SystemInstructions,
    string? SourceFileId,
    int ResultCount,
    string DestinationFolderId,
    string? AcceptedImprovementRecordId,
    GenerationSettings? Settings = null);

public sealed record GenerationJobStatusSnapshot(string JobId, string DraftId, GenerationJobPhase Phase, int? QueuePosition);

public sealed record GenerationQueueEntry(
    string JobId,
    string DraftId,
    string SubmittedTabTitle,
    string ConnectionId,
    string ModelId,
    string Prompt,
    GenerationJobPhase Phase,
    int? QueuePosition);

public sealed record GenerationJobOutcome(
    string JobId,
    string DraftId,
    GenerationRecord? Record,
    string? LocalErrorMessage,
    bool CancelledBeforeSubmission,
    DateTimeOffset CompletedAt);

public sealed class GenerationQueueService
{
    private readonly AppLibraryState _libraries;
    private readonly IProviderAdapterResolver _adapterResolver;
    private readonly ISecureCredentialStore _credentials;
    private readonly IAppPreferenceStore _preferences;
    private readonly IDeviceEnergyStateProvider _energy;
    private readonly object _gate = new();

    private sealed class QueuedJob
    {
        public required string JobId;
        public required string DraftId;
        public required string ConnectionId;
        public required GenerationJobSnapshot Snapshot;
        public required ILibraryWorkspace Workspace;
        public GenerationJobPhase Phase;
        public CancellationTokenSource? Cancellation;
    }

    private readonly Dictionary<string, LinkedList<QueuedJob>> _queues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _runningPerConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QueuedJob> _jobsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeJobIdByDraft = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GenerationJobOutcome> _lastOutcomeByDraft = new(StringComparer.Ordinal);
    private readonly List<string> _connectionOrder = new();
    private int _cursor;
    private int _runningTotal;
    private bool _started;

    private const string DeviceCapPreferenceKey = "slopfactory.queue.devicecap";
    private static int DefaultDeviceCap => OperatingSystem.IsAndroid() ? 2 : 3;
    public static int MinDeviceCap => 1;
    public static int MaxDeviceCap => OperatingSystem.IsAndroid() ? 4 : 8;

    public int DeviceCap
    {
        get
        {
            var stored = _preferences.ReadString(DeviceCapPreferenceKey, DefaultDeviceCap.ToString(CultureInfo.InvariantCulture));
            return int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, MinDeviceCap, MaxDeviceCap) : DefaultDeviceCap;
        }
    }

    public void SetDeviceCap(int value)
    {
        var clamped = Math.Clamp(value, MinDeviceCap, MaxDeviceCap);
        _preferences.WriteString(DeviceCapPreferenceKey, clamped.ToString(CultureInfo.InvariantCulture));
        RaiseChanged();
        Pump();
    }

    /// <summary>
    /// The cap actually enforced by the pump loop right now. Reduced to 1 while the OS reports
    /// energy-saver mode is on, regardless of the configured <see cref="DeviceCap"/> — this only
    /// ever stops new jobs from starting; it never cancels one already running.
    /// </summary>
    public int EffectiveDeviceCap => _energy.IsEnergySaverOn ? 1 : DeviceCap;

    /// <summary>Whether the OS-reported energy-saver constraint is currently in effect.</summary>
    public bool EnergySaverCapActive => _energy.IsEnergySaverOn;

    public GenerationQueueService(AppLibraryState libraries, IProviderAdapterResolver adapterResolver, ISecureCredentialStore credentials, IAppPreferenceStore preferences, IDeviceEnergyStateProvider energy)
    {
        _libraries = libraries;
        _adapterResolver = adapterResolver;
        _credentials = credentials;
        _preferences = preferences;
        _energy = energy;
    }

    public event EventHandler? Changed;
    public event EventHandler<GenerationJobOutcome>? JobCompleted;

    public int QueuedCount { get { lock (_gate) return _jobsById.Values.Count(job => job.Phase == GenerationJobPhase.Queued); } }
    public int RunningCount { get { lock (_gate) return _runningTotal; } }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _libraries.Changed += OnLibraryChanged;
        _energy.Changed += OnEnergyStateChanged;
    }

    private void OnEnergyStateChanged(object? sender, EventArgs args)
    {
        RaiseChanged();
        Pump();
    }

    public string Enqueue(GenerationJobSnapshot snapshot, string connectionId)
    {
        var workspace = _libraries.Workspace ?? throw new InvalidOperationException("No library is open.");
        lock (_gate)
        {
            if (_activeJobIdByDraft.TryGetValue(snapshot.DraftId, out var existingJobId)) return existingJobId;
            var job = new QueuedJob
            {
                JobId = LibraryRules.NewId(),
                DraftId = snapshot.DraftId,
                ConnectionId = connectionId,
                Snapshot = snapshot,
                Workspace = workspace,
                Phase = GenerationJobPhase.Queued
            };
            if (!_queues.TryGetValue(connectionId, out var queue))
            {
                queue = new LinkedList<QueuedJob>();
                _queues[connectionId] = queue;
                _connectionOrder.Add(connectionId);
            }
            queue.AddLast(job);
            _jobsById[job.JobId] = job;
            _activeJobIdByDraft[job.DraftId] = job.JobId;
            RaiseChanged();
            Pump();
            return job.JobId;
        }
    }

    public void Cancel(string jobId)
    {
        CancellationTokenSource? toCancel = null;
        var changed = false;
        lock (_gate)
        {
            if (!_jobsById.TryGetValue(jobId, out var job)) return;
            if (job.Phase == GenerationJobPhase.Queued)
            {
                _queues[job.ConnectionId].Remove(job);
                _jobsById.Remove(jobId);
                if (_activeJobIdByDraft.TryGetValue(job.DraftId, out var activeId) && activeId == jobId) _activeJobIdByDraft.Remove(job.DraftId);
                _lastOutcomeByDraft[job.DraftId] = new GenerationJobOutcome(jobId, job.DraftId, null, null, CancelledBeforeSubmission: true, DateTimeOffset.UtcNow);
                changed = true;
            }
            else
            {
                toCancel = job.Cancellation;
            }
        }
        toCancel?.Cancel();
        if (changed) RaiseChanged();
    }

    public GenerationJobStatusSnapshot? GetJobStatus(string jobId)
    {
        lock (_gate)
        {
            if (!_jobsById.TryGetValue(jobId, out var job)) return null;
            return new GenerationJobStatusSnapshot(job.JobId, job.DraftId, job.Phase, ComputeQueuePosition(job));
        }
    }

    public string? GetActiveJobIdForDraft(string draftId)
    {
        lock (_gate) return _activeJobIdByDraft.TryGetValue(draftId, out var jobId) ? jobId : null;
    }

    public GenerationJobOutcome? GetLastOutcomeForDraft(string draftId)
    {
        lock (_gate) return _lastOutcomeByDraft.TryGetValue(draftId, out var outcome) ? outcome : null;
    }

    public IReadOnlyList<GenerationQueueEntry> GetSnapshot()
    {
        lock (_gate)
        {
            var entries = new List<GenerationQueueEntry>();
            foreach (var job in _jobsById.Values)
            {
                entries.Add(new GenerationQueueEntry(job.JobId, job.DraftId, job.Snapshot.SubmittedTabTitle, job.ConnectionId, job.Snapshot.ModelId, job.Snapshot.Prompt, job.Phase, ComputeQueuePosition(job)));
            }
            return entries.OrderBy(entry => entry.ConnectionId, StringComparer.Ordinal).ThenBy(entry => entry.Phase).ThenBy(entry => entry.QueuePosition ?? 0).ToList();
        }
    }

    public void ReorderQueuedJobs(string connectionId, IReadOnlyList<string> orderedJobIds)
    {
        var changed = false;
        lock (_gate)
        {
            if (!_queues.TryGetValue(connectionId, out var queue)) return;
            var currentIds = queue.Select(job => job.JobId).ToArray();
            if (orderedJobIds.Count != currentIds.Length || !new HashSet<string>(orderedJobIds, StringComparer.Ordinal).SetEquals(currentIds))
            {
                throw new InvalidOperationException("The supplied job order does not match the connection's current queued jobs.");
            }
            var byId = queue.ToDictionary(job => job.JobId, StringComparer.Ordinal);
            queue.Clear();
            foreach (var id in orderedJobIds) queue.AddLast(byId[id]);
            changed = true;
        }
        if (changed) RaiseChanged();
    }

    private int? ComputeQueuePosition(QueuedJob job)
    {
        if (job.Phase != GenerationJobPhase.Queued) return null;
        var position = 1;
        foreach (var candidate in _queues[job.ConnectionId])
        {
            if (ReferenceEquals(candidate, job)) break;
            position++;
        }
        return position;
    }

    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        var current = _libraries.Workspace;
        List<CancellationTokenSource> toCancel = [];
        lock (_gate)
        {
            foreach (var job in _jobsById.Values.ToArray())
            {
                if (ReferenceEquals(job.Workspace, current)) continue;
                if (job.Phase == GenerationJobPhase.Queued)
                {
                    _queues[job.ConnectionId].Remove(job);
                    _jobsById.Remove(job.JobId);
                    if (_activeJobIdByDraft.TryGetValue(job.DraftId, out var activeId) && activeId == job.JobId) _activeJobIdByDraft.Remove(job.DraftId);
                }
                else if (job.Cancellation is { } cancellation)
                {
                    toCancel.Add(cancellation);
                }
            }
        }
        foreach (var cancellation in toCancel) cancellation.Cancel();
        RaiseChanged();
    }

    private void Pump()
    {
        while (true)
        {
            QueuedJob? started = null;
            lock (_gate)
            {
                if (_runningTotal >= EffectiveDeviceCap) break;
                var count = _connectionOrder.Count;
                for (var i = 0; i < count; i++)
                {
                    var index = (_cursor + i) % count;
                    var connectionId = _connectionOrder[index];
                    if (!_queues.TryGetValue(connectionId, out var queue) || queue.Count == 0) continue;
                    if (_runningPerConnection.TryGetValue(connectionId, out var running) && running > 0) continue;
                    started = queue.First!.Value;
                    queue.RemoveFirst();
                    started.Phase = GenerationJobPhase.Running;
                    _runningPerConnection[connectionId] = 1;
                    _runningTotal++;
                    _cursor = (index + 1) % count;
                    break;
                }
            }
            if (started is null) break;
            _ = RunJobAsync(started);
        }
    }

    private async Task RunJobAsync(QueuedJob job)
    {
        var cancellation = new CancellationTokenSource();
        lock (_gate) job.Cancellation = cancellation;
        RaiseChanged();

        var outcome = await ExecuteAsync(job, cancellation.Token).ConfigureAwait(false);

        lock (_gate)
        {
            _jobsById.Remove(job.JobId);
            if (_activeJobIdByDraft.TryGetValue(job.DraftId, out var activeId) && activeId == job.JobId) _activeJobIdByDraft.Remove(job.DraftId);
            _lastOutcomeByDraft[job.DraftId] = outcome;
            _runningPerConnection[job.ConnectionId] = 0;
            _runningTotal--;
        }
        cancellation.Dispose();
        JobCompleted?.Invoke(this, outcome);
        RaiseChanged();
        Pump();
    }

    private async Task<GenerationJobOutcome> ExecuteAsync(QueuedJob job, CancellationToken cancellationToken)
    {
        var snapshot = job.Snapshot;
        try
        {
            var models = await job.Workspace.GetActiveModelsAsync(cancellationToken).ConfigureAwait(false);
            var model = models.FirstOrDefault(candidate => candidate.Id == snapshot.ModelId);
            if (model is null) return LocalFailureOutcome(job, "The model configured for this submission is no longer available.");
            var connections = await job.Workspace.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
            var connection = connections.FirstOrDefault(candidate => candidate.Id == model.ConnectionId);
            if (connection is not { HasCredential: true, CredentialRequiresRepair: false, CredentialRevisionId: { } revisionId }) return LocalFailureOutcome(job, "The connection configured for this submission is no longer available.");

            var adapter = _adapterResolver.Resolve(connection.ProviderType);
            var apiKey = await _credentials.GetActiveAsync(job.Workspace.Descriptor.LibraryId, connection.Id, revisionId).ConfigureAwait(false);

            GenerationRecord record;
            if (snapshot.Mode == GenerationMode.Image)
            {
                IReadOnlyList<byte[]>? images = null;
                string? errorMessage = null;
                try
                {
                    images = await adapter.GenerateImageAsync(connection, model, apiKey, snapshot.Prompt, snapshot.ResultCount, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                {
                    errorMessage = exception.Message;
                }

                record = await job.Workspace.RecordImageGenerationResultAsync(model.Id, snapshot.Prompt, snapshot.ResultCount, snapshot.DestinationFolderId, images, errorMessage, snapshot.AcceptedImprovementRecordId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                TextGenerationSourceImage? sourceImage = null;
                if (snapshot.SourceFileId is not null)
                {
                    var sourceContent = await job.Workspace.ReadImageFileAsync(snapshot.SourceFileId, cancellationToken).ConfigureAwait(false);
                    sourceImage = new TextGenerationSourceImage(sourceContent.MediaType, sourceContent.Bytes);
                }

                TextGenerationResult? result = null;
                string? errorMessage = null;
                try
                {
                    result = await adapter.GenerateTextAsync(connection, model, apiKey, snapshot.Prompt, snapshot.ResultCount, snapshot.SystemInstructions, sourceImage, snapshot.Settings, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                {
                    errorMessage = exception.Message;
                }

                record = await job.Workspace.RecordTextGenerationResultAsync(model.Id, snapshot.Prompt, snapshot.ResultCount, snapshot.DestinationFolderId, result?.Texts, errorMessage, snapshot.SystemInstructions, result?.PromptTokens, result?.CompletionTokens, snapshot.SourceFileId, snapshot.AcceptedImprovementRecordId, snapshot.Settings, cancellationToken).ConfigureAwait(false);
            }

            return new GenerationJobOutcome(job.JobId, job.DraftId, record, null, false, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return new GenerationJobOutcome(job.JobId, job.DraftId, null, null, CancelledBeforeSubmission: false, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException or ObjectDisposedException)
        {
            return LocalFailureOutcome(job, exception.Message);
        }
    }

    private static GenerationJobOutcome LocalFailureOutcome(QueuedJob job, string message) =>
        new(job.JobId, job.DraftId, null, message, false, DateTimeOffset.UtcNow);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
