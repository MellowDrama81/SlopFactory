using System.Globalization;
using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;
using Mellow.SlopFactory.Infrastructure.Providers;

namespace Mellow.SlopFactory.Gui.Services;

public enum GenerationJobPhase
{
    Queued = 0,
    Running = 1,
    /// <summary>A submit-then-poll job whose provider submission durably succeeded and whose queue
    /// submission slot has therefore already been released — it no longer counts against the
    /// device-wide or per-connection concurrency cap, matching plan.md's "an asynchronous job
    /// releases its submission slot after the provider durably accepts it; later status polling
    /// does not consume a submission slot" rule.</summary>
    Monitoring = 2
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
        /// <summary>True once this job's submission slot has been released early (moved to
        /// <see cref="GenerationJobPhase.Monitoring"/>) — guards against double-releasing the slot
        /// and tells <see cref="RunJobAsync"/> not to decrement the counters again at the end.</summary>
        public bool SlotReleased;
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
    private readonly IConnectionRateLimitTracker? _rateLimitTracker;
    private readonly HashSet<string> _rateLimitPumpScheduled = new(StringComparer.Ordinal);

    public GenerationQueueService(AppLibraryState libraries, IProviderAdapterResolver adapterResolver, ISecureCredentialStore credentials, IAppPreferenceStore preferences, IDeviceEnergyStateProvider energy, TimeSpan? videoPollInterval = null, IConnectionRateLimitTracker? rateLimitTracker = null)
    {
        _libraries = libraries;
        _adapterResolver = adapterResolver;
        _credentials = credentials;
        _preferences = preferences;
        _energy = energy;
        _videoPollInterval = videoPollInterval ?? DefaultVideoPollInterval;
        _rateLimitTracker = rateLimitTracker;
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
                    if (IsConnectionOutOfRequestQuota(connectionId, out var resetsAt))
                    {
                        ScheduleRateLimitRetryPump(connectionId, resetsAt);
                        continue;
                    }
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

    /// <summary>
    /// Proactive backoff per plan.md: once a connection's last-observed remaining request quota
    /// (from the OpenAI-documented <c>x-ratelimit-remaining-requests</c> header — see
    /// <see cref="RateLimitHeaderParser"/>) hits zero and its reset window hasn't elapsed yet, new
    /// submissions on that connection wait rather than being sent into a near-certain 429.
    /// Already-running jobs are unaffected; this only gates the next job Pump() would otherwise start.
    /// </summary>
    private bool IsConnectionOutOfRequestQuota(string connectionId, out DateTimeOffset resetsAt)
    {
        resetsAt = default;
        var observation = _rateLimitTracker?.GetObservation(connectionId);
        if (observation is null || observation.RemainingRequests is not { } remaining || remaining > 0) return false;
        if (observation.ResetRequestsIn is not { } resetIn) return false;
        resetsAt = observation.ObservedAt + resetIn;
        return resetsAt > DateTimeOffset.UtcNow;
    }

    /// <summary>Whether the next queued job on this connection is currently being held back by the
    /// proactive rate-limit backoff above, for display on <c>/queue</c> — distinct from ordinary
    /// "waiting for a device/connection concurrency slot" queueing.</summary>
    public bool IsConnectionAwaitingRateLimitReset(string connectionId) => IsConnectionOutOfRequestQuota(connectionId, out _);

    private void ScheduleRateLimitRetryPump(string connectionId, DateTimeOffset resetsAt)
    {
        lock (_gate)
        {
            if (!_rateLimitPumpScheduled.Add(connectionId)) return;
        }
        var delay = resetsAt - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            lock (_gate) _rateLimitPumpScheduled.Remove(connectionId);
            Pump();
        }, TaskScheduler.Default);
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
            if (!job.SlotReleased)
            {
                _runningPerConnection[job.ConnectionId] = _runningPerConnection.GetValueOrDefault(job.ConnectionId) - 1;
                _runningTotal--;
            }
        }
        cancellation.Dispose();
        JobCompleted?.Invoke(this, outcome);
        RaiseChanged();
        Pump();
    }

    /// <summary>
    /// Releases a job's submission slot early — for a submit-then-poll job (video) whose provider
    /// submission durably succeeded, so it stops holding the device-wide/per-connection concurrency
    /// cap while it only polls for status rather than actively submitting. Idempotent: safe to call
    /// even if already released, though today's only caller only ever calls it once per job.
    /// </summary>
    private void ReleaseSubmissionSlotEarly(QueuedJob job)
    {
        lock (_gate)
        {
            if (job.SlotReleased) return;
            job.SlotReleased = true;
            job.Phase = GenerationJobPhase.Monitoring;
            _runningPerConnection[job.ConnectionId] = _runningPerConnection.GetValueOrDefault(job.ConnectionId) - 1;
            _runningTotal--;
        }
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

                record = await job.Workspace.RecordMediaGenerationResultAsync(model.Id, snapshot.Prompt, snapshot.ResultCount, snapshot.DestinationFolderId, audioFiles, errorMessage, snapshot.AcceptedImprovementRecordId, cancellationToken: cancellationToken).ConfigureAwait(false);
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
    /// Runs a video generation to completion. A request for more than one result submits that many
    /// independent provider jobs up front — <see cref="IProviderAdapter.SubmitVideoGenerationAsync"/>
    /// never accepts more than one result per call — and polls all of them as one indivisible group,
    /// matching plan.md's "a generation which requires multiple separate provider submissions
    /// occupies one queue position as an indivisible group" rule; the final record reflects whichever
    /// jobs actually completed (partial success is possible, matching every other multi-result mode).
    /// Each job is persisted in the device-wide pending-job registry so it is at least
    /// visible/inspectable if the process exits mid-poll.
    /// If cancellation fires after at least one job actually reached the provider, the generation
    /// still commits a real history record (<see cref="GenerationStatus.Cancelled"/> or
    /// <see cref="GenerationStatus.CancelledWithResults"/>) instead of silently discarding
    /// already-completed results — unlike Text/Image/Audio's synchronous cancellation (nothing sent
    /// yet, so reporting no record at all is correct there), this is real, already-resolved provider
    /// work and reporting it as "Cancelled Before Submission" would be false.
    /// Once at least one job is durably accepted, this releases the connection's queue submission
    /// slot immediately (<see cref="ReleaseSubmissionSlotEarly"/>) rather than holding it through the
    /// whole poll duration, matching plan.md's "an asynchronous job releases its submission slot
    /// after the provider durably accepts it" rule — the job moves to
    /// <see cref="GenerationJobPhase.Monitoring"/> and no longer counts against the device-wide or
    /// per-connection concurrency cap while only polling for status.
    /// Known limitation, not yet addressed: polling does not resume automatically after an
    /// application restart — that needs separate queue-scheduler work tracked in milestone3.md.
    /// </summary>
    private async Task<GenerationRecord> ExecuteVideoGenerationAsync(QueuedJob job, Connection connection, Model model, string? apiKey, IProviderAdapter adapter, CancellationToken cancellationToken)
    {
        var snapshot = job.Snapshot;
        var resultCount = Math.Max(1, snapshot.ResultCount);
        var submitted = new List<(string ProviderJobId, string AsyncRecordId)>();
        var files = new List<byte[]>();
        // One message per failed/missing position, in the order each failure was discovered — the
        // shared media commit path consumes these for its trailing "shortfall" positions, giving
        // each failed child in a multi-job group its own real reason instead of one generic message.
        var childErrorMessages = new List<string>();
        string? errorMessage = null;
        double? totalCost = null;
        string? costCurrency = null;

        try
        {
            for (var index = 0; index < resultCount; index++)
            {
                try
                {
                    var submission = await adapter.SubmitVideoGenerationAsync(connection, model, apiKey, snapshot.Prompt, cancellationToken).ConfigureAwait(false);
                    var asyncJob = await job.Workspace.CreateAsyncRemoteJobAsync(job.DraftId, connection.ProviderType, connection.Id, submission.ProviderJobId, null, submission.MonitoringDeadline, cancellationToken).ConfigureAwait(false);
                    submitted.Add((submission.ProviderJobId, asyncJob.Id));
                }
                catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                {
                    // A submission failure stops further submissions but never abandons jobs the
                    // provider already accepted — those are still polled to completion below. Only
                    // this one position gets the specific reason; any further never-attempted
                    // positions correctly fall back to a generic "no result" message below since
                    // they genuinely were never even submitted.
                    errorMessage ??= exception.Message;
                    childErrorMessages.Add(exception.Message);
                    break;
                }
            }

            if (submitted.Count > 0)
            {
                // At least one job was durably accepted by the provider — release this connection's
                // submission slot now instead of holding it through the whole poll duration below.
                ReleaseSubmissionSlotEarly(job);
            }

            var pending = new List<(string ProviderJobId, string AsyncRecordId)>(submitted);
            while (pending.Count > 0)
            {
                await Task.Delay(_videoPollInterval, cancellationToken).ConfigureAwait(false);
                foreach (var entry in pending.ToArray())
                {
                    AsyncGenerationPollResult pollResult;
                    try
                    {
                        pollResult = await adapter.PollVideoGenerationAsync(connection, apiKey, entry.ProviderJobId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                    {
                        errorMessage ??= exception.Message;
                        childErrorMessages.Add(exception.Message);
                        await job.Workspace.UpdateAsyncRemoteJobPhaseAsync(entry.AsyncRecordId, AsyncRemoteJobPhase.Failed, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                        pending.Remove(entry);
                        continue;
                    }

                    if (pollResult.Outcome == AsyncGenerationPollOutcome.Processing)
                    {
                        await job.Workspace.UpdateAsyncRemoteJobPhaseAsync(entry.AsyncRecordId, AsyncRemoteJobPhase.Processing, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var phase = pollResult.Outcome == AsyncGenerationPollOutcome.Completed ? AsyncRemoteJobPhase.Completed : AsyncRemoteJobPhase.Failed;
                    await job.Workspace.UpdateAsyncRemoteJobPhaseAsync(entry.AsyncRecordId, phase, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                    if (pollResult.Outcome == AsyncGenerationPollOutcome.Completed && pollResult.Files is { Count: > 0 })
                    {
                        files.AddRange(pollResult.Files);
                        if (pollResult.Cost is { } cost)
                        {
                            // Only a run-level total is kept, matching plan.md's "SlopFactory never
                            // divides a run total among output sidecars" rule — a per-child cost
                            // breakdown isn't modeled since there's no per-child result identity yet.
                            totalCost = (totalCost ?? 0) + cost.Amount;
                            costCurrency ??= cost.Currency;
                        }
                    }
                    else
                    {
                        var failureMessage = pollResult.ErrorMessage ?? "The provider reported a video generation job as failed.";
                        errorMessage ??= failureMessage;
                        childErrorMessages.Add(failureMessage);
                    }
                    pending.Remove(entry);
                }
            }
        }
        catch (OperationCanceledException) when (submitted.Count > 0)
        {
            // At least one job actually reached the provider — commit what's known so far using
            // CancellationToken.None for the commit itself (no further network calls happen here,
            // so there is nothing left to usefully cancel) rather than letting the cancellation
            // that already fired abort this bounded, local-only step too.
            var cancelledRecord = await job.Workspace.RecordMediaGenerationResultAsync(model.Id, snapshot.Prompt, resultCount, snapshot.DestinationFolderId, files.Count > 0 ? files : null, null, snapshot.AcceptedImprovementRecordId, totalCost, costCurrency, wasCancelled: true, childErrorMessages: childErrorMessages, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            foreach (var entry in submitted)
            {
                try
                {
                    await job.Workspace.DeleteAsyncRemoteJobAsync(entry.AsyncRecordId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException)
                {
                }
            }
            return cancelledRecord;
        }

        var record = await job.Workspace.RecordMediaGenerationResultAsync(model.Id, snapshot.Prompt, resultCount, snapshot.DestinationFolderId, files.Count > 0 ? files : null, files.Count == 0 ? errorMessage : null, snapshot.AcceptedImprovementRecordId, totalCost, costCurrency, childErrorMessages: childErrorMessages, cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var entry in submitted)
        {
            try
            {
                await job.Workspace.DeleteAsyncRemoteJobAsync(entry.AsyncRecordId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException)
            {
                // The generation already committed above; failing to remove a now-stale pending-job
                // registry row is a harmless leftover, not a reason to report the generation as failed.
            }
        }

        return record;
    }

    private static GenerationJobOutcome LocalFailureOutcome(QueuedJob job, string message) =>
        new(job.JobId, job.DraftId, null, message, false, DateTimeOffset.UtcNow);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
