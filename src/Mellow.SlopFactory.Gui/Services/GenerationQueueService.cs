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
    GenerationSettings? Settings = null,
    string? SecondarySourceFileId = null,
    string? TertiarySourceFileId = null);

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
    private readonly Dictionary<string, List<string>> _activeJobIdsByDraft = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GenerationJobOutcome>> _recentOutcomesByDraft = new(StringComparer.Ordinal);
    private const int MaxRecentOutcomesPerDraft = 10;
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

    private const string ConnectionCapPreferenceKeyPrefix = "slopfactory.queue.connectioncap.";
    private static int DefaultConnectionCap => 1;
    public static int MinConnectionCap => 1;
    public static int MaxConnectionCap => OperatingSystem.IsAndroid() ? 4 : 8;

    /// <summary>
    /// The configured concurrency limit for a specific connection — a device-local user preference
    /// about that connection's known rate limits, not an app-asserted provider fact, mirroring
    /// <see cref="DeviceCap"/> exactly but scoped per connection instead of device-wide.
    /// </summary>
    public int GetConnectionCap(string connectionId)
    {
        var stored = _preferences.ReadString(ConnectionCapPreferenceKeyPrefix + connectionId, DefaultConnectionCap.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, MinConnectionCap, MaxConnectionCap) : DefaultConnectionCap;
    }

    public void SetConnectionCap(string connectionId, int value)
    {
        var clamped = Math.Clamp(value, MinConnectionCap, MaxConnectionCap);
        _preferences.WriteString(ConnectionCapPreferenceKeyPrefix + connectionId, clamped.ToString(CultureInfo.InvariantCulture));
        RaiseChanged();
        Pump();
    }

    private static readonly TimeSpan DefaultVideoPollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _videoPollInterval;

    public GenerationQueueService(AppLibraryState libraries, IProviderAdapterResolver adapterResolver, ISecureCredentialStore credentials, IAppPreferenceStore preferences, IDeviceEnergyStateProvider energy, TimeSpan? videoPollInterval = null)
    {
        _libraries = libraries;
        _adapterResolver = adapterResolver;
        _credentials = credentials;
        _preferences = preferences;
        _energy = energy;
        _videoPollInterval = videoPollInterval ?? DefaultVideoPollInterval;
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
            AddActiveJobId(job.DraftId, job.JobId);
            RaiseChanged();
            Pump();
            return job.JobId;
        }
    }

    private void AddActiveJobId(string draftId, string jobId)
    {
        if (!_activeJobIdsByDraft.TryGetValue(draftId, out var list))
        {
            list = [];
            _activeJobIdsByDraft[draftId] = list;
        }
        list.Add(jobId);
    }

    private void RemoveActiveJobId(string draftId, string jobId)
    {
        if (_activeJobIdsByDraft.TryGetValue(draftId, out var list) && list.Remove(jobId) && list.Count == 0) _activeJobIdsByDraft.Remove(draftId);
    }

    private void RecordOutcome(string draftId, GenerationJobOutcome outcome)
    {
        if (!_recentOutcomesByDraft.TryGetValue(draftId, out var list))
        {
            list = [];
            _recentOutcomesByDraft[draftId] = list;
        }
        list.Insert(0, outcome);
        if (list.Count > MaxRecentOutcomesPerDraft) list.RemoveAt(list.Count - 1);
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
                RemoveActiveJobId(job.DraftId, jobId);
                RecordOutcome(job.DraftId, new GenerationJobOutcome(jobId, job.DraftId, null, null, CancelledBeforeSubmission: true, DateTimeOffset.UtcNow));
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

    /// <summary>The first (oldest-submitted) active job for a draft, or null if none — a convenience
    /// accessor for the common single-run case. Use <see cref="GetActiveJobIdsForDraft"/> for every
    /// concurrently active job on the draft.</summary>
    public string? GetActiveJobIdForDraft(string draftId)
    {
        lock (_gate) return _activeJobIdsByDraft.TryGetValue(draftId, out var list) && list.Count > 0 ? list[0] : null;
    }

    /// <summary>Every queued or running job currently submitted from this draft, oldest first.</summary>
    public IReadOnlyList<string> GetActiveJobIdsForDraft(string draftId)
    {
        lock (_gate) return _activeJobIdsByDraft.TryGetValue(draftId, out var list) ? list.ToArray() : [];
    }

    /// <summary>The most recently completed/cancelled job's outcome for a draft, or null if none — a
    /// convenience accessor for the common single-run case. Use
    /// <see cref="GetRecentOutcomesForDraft"/> for the full retained history.</summary>
    public GenerationJobOutcome? GetLastOutcomeForDraft(string draftId)
    {
        lock (_gate) return _recentOutcomesByDraft.TryGetValue(draftId, out var list) && list.Count > 0 ? list[0] : null;
    }

    /// <summary>Up to the 10 most recent terminal (completed/failed/cancelled) outcomes for a draft,
    /// newest first. Older terminal runs remain fully retained in generation history.</summary>
    public IReadOnlyList<GenerationJobOutcome> GetRecentOutcomesForDraft(string draftId)
    {
        lock (_gate) return _recentOutcomesByDraft.TryGetValue(draftId, out var list) ? list.ToArray() : [];
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
                    RemoveActiveJobId(job.DraftId, job.JobId);
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
                    _runningPerConnection.TryGetValue(connectionId, out var running);
                    if (running >= GetConnectionCap(connectionId)) continue;
                    started = queue.First!.Value;
                    queue.RemoveFirst();
                    started.Phase = GenerationJobPhase.Running;
                    _runningPerConnection[connectionId] = running + 1;
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
            RemoveActiveJobId(job.DraftId, job.JobId);
            RecordOutcome(job.DraftId, outcome);
            _runningPerConnection[job.ConnectionId] = _runningPerConnection.GetValueOrDefault(job.ConnectionId) - 1;
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
            else if (snapshot.Mode == GenerationMode.Audio)
            {
                IReadOnlyList<byte[]>? audioFiles = null;
                string? errorMessage = null;
                try
                {
                    audioFiles = await adapter.GenerateAudioAsync(connection, model, apiKey, snapshot.Prompt, snapshot.ResultCount, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                {
                    errorMessage = exception.Message;
                }

                record = await job.Workspace.RecordMediaGenerationResultAsync(model.Id, snapshot.Prompt, snapshot.ResultCount, snapshot.DestinationFolderId, audioFiles, errorMessage, snapshot.AcceptedImprovementRecordId, cancellationToken).ConfigureAwait(false);
            }
            else if (snapshot.Mode == GenerationMode.Video)
            {
                record = await ExecuteVideoGenerationAsync(job, connection, model, apiKey, adapter, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                TextGenerationSourceImage? sourceImage = null;
                if (snapshot.SourceFileId is not null)
                {
                    var sourceContent = await job.Workspace.ReadImageFileAsync(snapshot.SourceFileId, cancellationToken).ConfigureAwait(false);
                    sourceImage = new TextGenerationSourceImage(sourceContent.MediaType, sourceContent.Bytes);
                }
                TextGenerationSourceImage? secondarySourceImage = null;
                if (snapshot.SecondarySourceFileId is not null)
                {
                    var secondarySourceContent = await job.Workspace.ReadImageFileAsync(snapshot.SecondarySourceFileId, cancellationToken).ConfigureAwait(false);
                    secondarySourceImage = new TextGenerationSourceImage(secondarySourceContent.MediaType, secondarySourceContent.Bytes);
                }
                TextGenerationSourceImage? tertiarySourceImage = null;
                if (snapshot.TertiarySourceFileId is not null)
                {
                    var tertiarySourceContent = await job.Workspace.ReadImageFileAsync(snapshot.TertiarySourceFileId, cancellationToken).ConfigureAwait(false);
                    tertiarySourceImage = new TextGenerationSourceImage(tertiarySourceContent.MediaType, tertiarySourceContent.Bytes);
                }

                TextGenerationResult? result = null;
                string? errorMessage = null;
                try
                {
                    result = await adapter.GenerateTextAsync(connection, model, apiKey, snapshot.Prompt, snapshot.ResultCount, snapshot.SystemInstructions, sourceImage, snapshot.Settings, secondarySourceImage, tertiarySourceImage, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                {
                    errorMessage = exception.Message;
                }

                record = await job.Workspace.RecordTextGenerationResultAsync(model.Id, snapshot.Prompt, snapshot.ResultCount, snapshot.DestinationFolderId, result?.Texts, errorMessage, snapshot.SystemInstructions, result?.PromptTokens, result?.CompletionTokens, snapshot.SourceFileId, snapshot.AcceptedImprovementRecordId, snapshot.Settings, snapshot.SecondarySourceFileId, snapshot.TertiarySourceFileId, result?.SafetyBlockedCount ?? 0, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Submits an asynchronous video generation job, persists it in the device-wide pending-job
    /// registry so it is at least visible/inspectable if the process exits mid-poll, and polls until
    /// a terminal outcome before committing the result exactly like a synchronous generation.
    /// Known limitation, not yet addressed: this holds the job's queue submission slot for the
    /// entire poll duration rather than releasing it after durable provider acceptance as plan.md
    /// describes, and polling does not resume automatically after an application restart — both
    /// require further queue-scheduler work tracked in milestone3.md.
    /// </summary>
    private async Task<GenerationRecord> ExecuteVideoGenerationAsync(QueuedJob job, Connection connection, Model model, string? apiKey, IProviderAdapter adapter, CancellationToken cancellationToken)
    {
        var snapshot = job.Snapshot;
        string? errorMessage = null;
        IReadOnlyList<byte[]>? files = null;
        string? pendingAsyncJobId = null;
        try
        {
            var submission = await adapter.SubmitVideoGenerationAsync(connection, model, apiKey, snapshot.Prompt, cancellationToken).ConfigureAwait(false);
            var asyncJob = await job.Workspace.CreateAsyncRemoteJobAsync(job.DraftId, connection.ProviderType, connection.Id, submission.ProviderJobId, null, submission.MonitoringDeadline, cancellationToken).ConfigureAwait(false);
            pendingAsyncJobId = asyncJob.Id;

            AsyncGenerationPollResult pollResult;
            while (true)
            {
                await Task.Delay(_videoPollInterval, cancellationToken).ConfigureAwait(false);
                pollResult = await adapter.PollVideoGenerationAsync(connection, apiKey, submission.ProviderJobId, cancellationToken).ConfigureAwait(false);
                var phase = pollResult.Outcome switch
                {
                    AsyncGenerationPollOutcome.Processing => AsyncRemoteJobPhase.Processing,
                    AsyncGenerationPollOutcome.Completed => AsyncRemoteJobPhase.Completed,
                    _ => AsyncRemoteJobPhase.Failed
                };
                await job.Workspace.UpdateAsyncRemoteJobPhaseAsync(pendingAsyncJobId, phase, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                if (pollResult.Outcome != AsyncGenerationPollOutcome.Processing) break;
            }

            if (pollResult.Outcome == AsyncGenerationPollOutcome.Completed) files = pollResult.Files;
            else errorMessage = pollResult.ErrorMessage ?? "The provider reported the video generation job as failed.";
        }
        catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
        {
            errorMessage = exception.Message;
        }

        var record = await job.Workspace.RecordMediaGenerationResultAsync(model.Id, snapshot.Prompt, snapshot.ResultCount, snapshot.DestinationFolderId, files, errorMessage, snapshot.AcceptedImprovementRecordId, cancellationToken).ConfigureAwait(false);

        if (pendingAsyncJobId is not null)
        {
            try
            {
                await job.Workspace.DeleteAsyncRemoteJobAsync(pendingAsyncJobId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException)
            {
                // The generation already committed successfully above; failing to remove its now-stale
                // pending-job registry row is a harmless leftover, not a reason to report a completed
                // generation as failed.
            }
        }

        return record;
    }

    private static GenerationJobOutcome LocalFailureOutcome(QueuedJob job, string message) =>
        new(job.JobId, job.DraftId, null, message, false, DateTimeOffset.UtcNow);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
