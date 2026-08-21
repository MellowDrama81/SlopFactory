using System.Globalization;
using System.Linq;
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
    /// device-wide or per-connection concurrency cap. An asynchronous job releases its submission
    /// slot once the provider durably accepts it; later status polling does not consume one.</summary>
    Monitoring = 2,
    /// <summary>A still-queued (not yet submitted) job paused because a source file or destination
    /// folder it depends on was recycled or permanently deleted
    /// (<see cref="GenerationQueueEntry.NonRunnable"/> distinguishes the two — permanent deletion
    /// also sets that flag). Retains its queue position and never proceeds using the recycled/deleted
    /// dependency automatically; restoring every paused dependency returns it to <see cref="Queued"/>,
    /// while a permanently deleted dependency requires the user to cancel and resubmit from the
    /// originating tab.</summary>
    DependencyRecycled = 3,
    /// <summary>A never-submitted job held after the device lost connectivity. It keeps its
    /// position and resumes only after the user explicitly resumes the queue.</summary>
    PausedConnectionLost = 4,
    /// <summary>A never-submitted job held by the configured metered-network policy. It keeps its
    /// position and resumes only after the user explicitly resumes the queue.</summary>
    PausedMeteredNetwork = 5
}

/// <summary>Device-wide policy for starting a new submission while the device's current connection
/// is metered (cellular) — distinct from being offline entirely, which always pauses regardless of
/// this setting.</summary>
public enum MeteredNetworkTransferPolicy
{
    /// <summary>Start new submissions on a metered connection exactly as on any other.</summary>
    Allow = 0,
    /// <summary>Never auto-start a new submission on a metered connection; require an explicit
    /// <see cref="GenerationQueueService.ResumeQueue"/> first (a coarse, queue-wide "ask" rather than
    /// a per-job interactive prompt).</summary>
    Ask = 1,
    /// <summary>Never start a new submission on a metered connection; only an unmetered connection
    /// (or an explicit resume) allows one to start.</summary>
    WifiOnly = 2
}

/// <param name="Voice">An Audio-mode preset voice identifier — see
/// <see cref="Domain.LibraryRules.SupportsAudioVoiceSelection"/>. Deliberately not persisted through
/// <c>GenerationDraft</c>/history/Use Again in this pass (an explicit, documented scope cut, not an
/// oversight): it lives only in this in-memory submission snapshot.</param>
public sealed record GenerationJobSnapshot(
    string DraftId,
    string SubmittedTabTitle,
    GenerationMode Mode,
    string ModelId,
    string Prompt,
    string? SystemInstructions,
    int ResultCount,
    string DestinationFolderId,
    string? AcceptedImprovementRecordId,
    GenerationSettings? Settings = null,
    IReadOnlyList<GenerationSourceSlot>? SourceSlots = null,
    string? Voice = null);

public sealed record GenerationJobStatusSnapshot(string JobId, string DraftId, GenerationJobPhase Phase, int? QueuePosition);

/// <param name="NonRunnable">True once a dependency this job needs was permanently deleted rather
/// than merely recycled — restoring a dependency can never clear this, so the job can only ever be
/// cancelled and resubmitted.</param>
public sealed record GenerationQueueEntry(
    string JobId,
    string DraftId,
    string SubmittedTabTitle,
    string ConnectionId,
    string ModelId,
    string Prompt,
    GenerationJobPhase Phase,
    int? QueuePosition,
    bool NonRunnable = false);

/// <param name="StagedForRecovery">True when a video result finished at the provider but could not
/// be committed because its destination library's volume was unavailable, and the already-downloaded
/// bytes were staged into device-wide recovery storage instead of being discarded.
/// <see cref="Record"/> is null in that case — the result exists only in recovery staging, not as a
/// generation-history record, until the library returns and the user resolves it.</param>
public sealed record GenerationJobOutcome(
    string JobId,
    string DraftId,
    GenerationRecord? Record,
    string? LocalErrorMessage,
    bool CancelledBeforeSubmission,
    DateTimeOffset CompletedAt,
    bool StagedForRecovery = false);

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
        /// <summary>True once a dependency (source file or destination folder) this job needs was
        /// permanently deleted — terminal, never cleared by a later restore.</summary>
        public bool NonRunnable;
        /// <summary>IDs of currently-recycled dependencies (source file/destination folder) keeping
        /// this job paused at <see cref="GenerationJobPhase.DependencyRecycled"/> — the job only
        /// returns to <see cref="GenerationJobPhase.Queued"/> once this set is empty again and it is
        /// not <see cref="NonRunnable"/>, so two independently recycled dependencies both have to be
        /// restored before the job resumes.</summary>
        public readonly HashSet<string> RecycledDependencyIds = new(StringComparer.Ordinal);
        /// <summary>The task creating this job's durable <see cref="GenerationStatus.Queued"/> record
        /// (see <see cref="CreateQueuedRecordAsync"/>). Awaited by <see cref="RunJobAsync"/> before
        /// execution starts so <see cref="GenerationRecordId"/> is populated (or confirmed
        /// unavailable) first.</summary>
        public Task<GenerationRecord?>? RecordCreation;
        /// <summary>Set before cancelling this job in response to
        /// <see cref="IBackgroundExecutionService.Suspended"/>, so its
        /// <see cref="OperationCanceledException"/> handler can record the OS suspending background
        /// execution as the cause instead of an ordinary cancellation or provider failure.</summary>
        public bool SuspendedByOperatingSystem;
        /// <summary>True once a request may have actually been transmitted to a provider (the
        /// <see cref="GenerationStatus.Submitting"/> transition was reached) — distinguishes, on
        /// cancellation, "nothing was ever sent" from "acceptance can no longer be confirmed."</summary>
        public bool SubmissionAttempted;
        /// <summary>Set once <see cref="RecordCreation"/> completes successfully. Null if creation
        /// failed (a transient storage failure) — status transitions are then skipped and the job
        /// runs exactly as it did before this durable record existed.</summary>
        public string? GenerationRecordId;
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

    private const string MeteredNetworkPolicyPreferenceKey = "slopfactory.queue.meterednetworkpolicy";
    private bool _connectionLostLatched;
    private bool _meteredPauseLatched;
    private readonly HashSet<string> _connectionsWithConnectivityOverride = new(StringComparer.Ordinal);
    private readonly HashSet<string> _jobsWithConnectivityOverride = new(StringComparer.Ordinal);
    // Set by ResumeQueue() so a manual resume actually lets a job start even while still offline/
    // metered (the whole point of an explicit override) instead of immediately re-latching on the
    // very next Pump() call; cleared the moment a real connectivity transition is observed, since a
    // fresh network state deserves a fresh pause decision rather than inheriting an old override.
    private bool _connectivityOverrideActive;

    /// <summary>
    /// True once the device has been observed offline while the queue had (or was offered) work to
    /// start — stays true even after connectivity returns, requiring an explicit manual resume,
    /// until <see cref="ResumeQueue"/> is called explicitly. Never affects an already-running job.
    /// </summary>
    public bool IsPausedForConnectionLost { get { lock (_gate) return _connectionLostLatched; } }

    /// <summary>True once a metered connection blocked a new submission under the current
    /// <see cref="MeteredNetworkPolicy"/> (`WifiOnly`, or `Ask` without an explicit resume) — same
    /// manual-resume semantics as <see cref="IsPausedForConnectionLost"/>.</summary>
    public bool IsPausedForMeteredNetwork { get { lock (_gate) return _meteredPauseLatched; } }

    /// <summary>
    /// Clears both connectivity-driven pause latches and re-pumps every queue immediately. Callers
    /// that need narrower approval can instead use <see cref="ResumeQueueForConnection"/> or
    /// <see cref="ResumeJob"/>.
    /// would be redundant — every connection's queue resumes together.
    /// This historical assumption is superseded by the narrower actions described above.
    /// </summary>
    public void ResumeQueue()
    {
        lock (_gate)
        {
            _connectionLostLatched = false;
            _meteredPauseLatched = false;
            _connectivityOverrideActive = true;
            ResumeConnectivityPausedJobsLocked();
        }
        RaiseChanged();
        Pump();
    }

    /// <summary>Explicitly resumes every pre-submission job for one connection while retaining the
    /// connectivity pause for other connections. The override is cleared by the next observed network
    /// transition, so a changed network always requires a fresh user decision.</summary>
    public void ResumeQueueForConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_gate)
        {
            _connectionsWithConnectivityOverride.Add(connectionId);
            ResumeConnectivityPausedJobsLocked(connectionId);
        }
        RaiseChanged();
        Pump();
    }

    /// <summary>Explicitly resumes one connectivity-paused job without approving any other job on
    /// its connection. Returns <see langword="false"/> when the job is not awaiting a connectivity
    /// decision (for example, it is already running or blocked on a recycled dependency).</summary>
    public bool ResumeJob(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_gate)
        {
            if (!_jobsById.TryGetValue(jobId, out var job) || !IsConnectivityPaused(job.Phase)) return false;
            _jobsWithConnectivityOverride.Add(jobId);
            job.Phase = GenerationJobPhase.Queued;
        }
        RaiseChanged();
        Pump();
        return true;
    }

    public MeteredNetworkTransferPolicy MeteredNetworkPolicy
    {
        get
        {
            var stored = _preferences.ReadString(MeteredNetworkPolicyPreferenceKey, ((int)MeteredNetworkTransferPolicy.Allow).ToString(CultureInfo.InvariantCulture));
            return int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && Enum.IsDefined(typeof(MeteredNetworkTransferPolicy), value)
                ? (MeteredNetworkTransferPolicy)value
                : MeteredNetworkTransferPolicy.Allow;
        }
    }

    public void SetMeteredNetworkPolicy(MeteredNetworkTransferPolicy policy)
    {
        _preferences.WriteString(MeteredNetworkPolicyPreferenceKey, ((int)policy).ToString(CultureInfo.InvariantCulture));
        RaiseChanged();
        Pump();
    }

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
    private readonly IDeviceConnectivityStateProvider? _connectivity;
    private readonly IRecoveryStagingService? _recoveryStaging;
    private readonly ILibraryAvailabilityProbe? _availabilityProbe;
    private readonly IDiagnosticsLogger? _diagnostics;
    private readonly IBackgroundExecutionService? _backgroundExecution;
    private readonly IGenerationFaultInjector _faultInjector;

    public GenerationQueueService(AppLibraryState libraries, IProviderAdapterResolver adapterResolver, ISecureCredentialStore credentials, IAppPreferenceStore preferences, IDeviceEnergyStateProvider energy, TimeSpan? videoPollInterval = null, IConnectionRateLimitTracker? rateLimitTracker = null, IDeviceConnectivityStateProvider? connectivity = null, IRecoveryStagingService? recoveryStaging = null, ILibraryAvailabilityProbe? availabilityProbe = null, IDiagnosticsLogger? diagnostics = null, IBackgroundExecutionService? backgroundExecution = null, IGenerationFaultInjector? faultInjector = null)
    {
        _libraries = libraries;
        _adapterResolver = adapterResolver;
        _credentials = credentials;
        _preferences = preferences;
        _energy = energy;
        _videoPollInterval = videoPollInterval ?? DefaultVideoPollInterval;
        _rateLimitTracker = rateLimitTracker;
        _connectivity = connectivity;
        _recoveryStaging = recoveryStaging;
        _availabilityProbe = availabilityProbe;
        _diagnostics = diagnostics;
        _backgroundExecution = backgroundExecution;
        _faultInjector = faultInjector ?? NullGenerationFaultInjector.Instance;
        if (_backgroundExecution is not null) _backgroundExecution.Suspended += OnBackgroundExecutionSuspended;
    }

    public event EventHandler? Changed;
    public event EventHandler<GenerationJobOutcome>? JobCompleted;

    /// <summary>Cancels every currently running or monitoring job in response to the OS revoking or
    /// timing out background execution on its own — see <see cref="IBackgroundExecutionService
    /// .Suspended"/> — marking each so its cancellation handler records this distinctly from an
    /// ordinary cancellation or provider failure.</summary>
    private void OnBackgroundExecutionSuspended(object? sender, EventArgs args)
    {
        List<QueuedJob> affected;
        lock (_gate)
        {
            affected = _jobsById.Values.Where(job => job.Phase is GenerationJobPhase.Running or GenerationJobPhase.Monitoring).ToList();
            foreach (var job in affected) job.SuspendedByOperatingSystem = true;
        }
        foreach (var job in affected) job.Cancellation?.Cancel();
    }

    public int QueuedCount { get { lock (_gate) return _jobsById.Values.Count(job => IsPreSubmissionPhase(job.Phase)); } }
    public int RunningCount { get { lock (_gate) return _runningTotal; } }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _libraries.Changed += OnLibraryChanged;
        _energy.Changed += OnEnergyStateChanged;
        if (_connectivity is not null) _connectivity.Changed += OnConnectivityChanged;
        _ = ResumePendingDownloadsAsync();
        _ = ResumeInFlightGenerationsAsync();
        _ = ReconcileStagedResultsAsync();
    }

    /// <summary>
    /// Automatic staged-result reconciliation: when the intended library returns, the staged result
    /// is moved into it atomically and the staged copy is deleted. Every staged entry
    /// tagged with a generation record belonging to the now-open library is committed into that
    /// record; the staged copy is only discarded once that commit durably succeeds. Entries staged
    /// before generation-record linkage existed (<see cref="StagedResultEntry.GenerationRecordId"/>
    /// is null) are left for manual export/discard only — there is nothing to reconcile them into.
    /// Called once when the queue starts and again whenever the active library changes.
    /// </summary>
    private async Task ReconcileStagedResultsAsync()
    {
        if (_recoveryStaging is null) return;
        var workspace = _libraries.Workspace;
        if (workspace is null) return;
        var libraryId = workspace.Descriptor.LibraryId;
        IReadOnlyList<StagedResultEntry> staged;
        try
        {
            staged = _recoveryStaging.GetAll();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var groups = staged
            .Where(entry => entry.LibraryId == libraryId && entry.GenerationRecordId is not null)
            .GroupBy(entry => entry.GenerationRecordId!);
        foreach (var group in groups)
        {
            try
            {
                await ReconcileStagedGenerationGroupAsync(workspace, group.Key, group.OrderBy(entry => entry.Position).ToArray()).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Left staged for a later attempt (next Start()/library switch, or a manual export or
                // discard) — this loop must never let one bad group stop reconciliation of the rest.
            }
        }
    }

    private async Task ReconcileStagedGenerationGroupAsync(ILibraryWorkspace workspace, string generationRecordId, StagedResultEntry[] entries)
    {
        GenerationRecord record;
        try
        {
            record = await workspace.GetGenerationRecordAsync(generationRecordId).ConfigureAwait(false);
        }
        catch (RecordNotFoundException)
        {
            return;
        }
        if (record.ModelId is null) return;
        Model model;
        try
        {
            model = await workspace.GetModelAsync(record.ModelId).ConfigureAwait(false);
        }
        catch (RecordNotFoundException)
        {
            return;
        }

        var files = new List<byte[]>(entries.Length);
        foreach (var entry in entries)
        {
            files.Add(await _recoveryStaging!.ReadBytesAsync(entry.Id).ConfigureAwait(false));
        }

        await workspace.RecordMediaGenerationResultAsync(model.Id, record.Prompt, record.ResultCount, record.DestinationFolderId, files, null, record.PromptImprovementRecordId, existingGenerationRecordId: record.Id, sourceSlots: record.SourceSlots).ConfigureAwait(false);

        // Only reached once the commit above durably succeeded — the staged copy is deleted only
        // after reconciliation, never before.
        foreach (var entry in entries)
        {
            await _recoveryStaging!.DiscardAsync(entry.Id).ConfigureAwait(false);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Restores generation records left non-terminal by a crash or restart — restart recovery must
    /// not infer state from transient UI data. A record still at
    /// <see cref="GenerationStatus.Queued"/> or <see cref="GenerationStatus.Preparing"/> never reached
    /// a provider, so it is safe to silently re-enter the queue from the durable record itself (which
    /// already carries the full request shape). Every other nonterminal status may have already
    /// reached the provider, so it is never auto-resubmitted: a video job with a still-tracked
    /// <see cref="AsyncRemoteJobRecord"/> is left exactly as <see cref="ResumePendingDownloadsAsync"/>
    /// already leaves a <see cref="AsyncRemoteJobPhase.CompletedAwaitingDownload"/> job — visible,
    /// discardable, unresolved — while anything else with no polling handle at all (the synchronous
    /// Text/Image/Audio case, or a video submission that crashed before its registry row was written)
    /// advances to <see cref="GenerationStatus.SubmissionOutcomeUnknown"/> instead of being silently
    /// lost or silently resubmitted. Called once when the queue starts and again whenever the active
    /// library changes.
    /// </summary>
    private async Task ResumeInFlightGenerationsAsync()
    {
        var workspace = _libraries.Workspace;
        if (workspace is null) return;
        IReadOnlyList<GenerationRecord> pending;
        try
        {
            pending = await workspace.GetNonTerminalGenerationRecordsAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException)
        {
            return;
        }
        foreach (var record in pending)
        {
            try
            {
                if (record.Status is GenerationStatus.Queued or GenerationStatus.Preparing)
                {
                    await ResumeSafeRecordAsync(workspace, record).ConfigureAwait(false);
                }
                else if (record.Status != GenerationStatus.SubmissionOutcomeUnknown)
                {
                    var linkedJobs = await workspace.GetAsyncRemoteJobsForGenerationRecordAsync(record.Id).ConfigureAwait(false);
                    if (linkedJobs.Count == 0)
                    {
                        await workspace.AdvanceGenerationStatusAsync(record.Id, GenerationStatus.SubmissionOutcomeUnknown).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Left for a later manual pass — this loop must never let one bad record stop
                // recovery of the rest.
            }
        }
    }

    private async Task ResumeSafeRecordAsync(ILibraryWorkspace workspace, GenerationRecord record)
    {
        if (record.ModelId is null) return;
        Model model;
        try
        {
            model = await workspace.GetModelAsync(record.ModelId).ConfigureAwait(false);
        }
        catch (RecordNotFoundException)
        {
            return;
        }
        var snapshot = new GenerationJobSnapshot(
            DraftId: record.Id,
            SubmittedTabTitle: record.Prompt,
            Mode: record.Mode,
            ModelId: record.ModelId,
            Prompt: record.Prompt,
            SystemInstructions: record.SystemInstructions,
            ResultCount: record.ResultCount,
            DestinationFolderId: record.DestinationFolderId,
            AcceptedImprovementRecordId: record.PromptImprovementRecordId,
            Settings: record.Settings,
            SourceSlots: record.SourceSlots);
        EnqueueExisting(record, snapshot, model.ConnectionId, workspace);
    }

    /// <summary>Re-enters the queue for a generation record that already durably exists (restart
    /// recovery) instead of creating a new one, unlike <see cref="Enqueue"/>.</summary>
    private void EnqueueExisting(GenerationRecord record, GenerationJobSnapshot snapshot, string connectionId, ILibraryWorkspace workspace)
    {
        lock (_gate)
        {
            var job = new QueuedJob
            {
                JobId = LibraryRules.NewId(),
                DraftId = snapshot.DraftId,
                ConnectionId = connectionId,
                Snapshot = snapshot,
                Workspace = workspace,
                Phase = GenerationJobPhase.Queued,
                GenerationRecordId = record.Id,
                RecordCreation = Task.FromResult<GenerationRecord?>(record)
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
        }
    }

    /// <summary>
    /// When the application reopens, it resumes polling incomplete asynchronous jobs — scoped to
    /// the one case resumable without the original submission context
    /// (prompt/model/settings), which per-async-job persistence deliberately never retains: a job
    /// the provider already confirmed <see cref="AsyncRemoteJobPhase.CompletedAwaitingDownload"/> can
    /// be retried purely from its existing generation-record position, via the same
    /// <see cref="RetryMissingResultDownloadAsync"/> used by **Refresh Provider Status**. A job still
    /// genuinely in flight (Submitted/Processing/MonitoringPaused) has no persisted context to resume
    /// a poll loop into and is left as visible, discardable "unresolved" state (`Connections.razor`)
    /// instead — attempting to fabricate one would mean guessing at a request that was never
    /// recorded. Called once when the queue starts (for whichever library is already open) and again
    /// every time the active library changes (open, switch or reopen).
    /// </summary>
    private async Task ResumePendingDownloadsAsync()
    {
        var workspace = _libraries.Workspace;
        if (workspace is null) return;
        IReadOnlyList<AsyncRemoteJobRecord> pending;
        try
        {
            pending = await workspace.GetPendingAsyncRemoteJobsAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException)
        {
            return;
        }
        foreach (var job in pending.Where(job => job.Phase == AsyncRemoteJobPhase.CompletedAwaitingDownload))
        {
            try
            {
                await RetryMissingResultDownloadAsync(job.Id).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException)
            {
                // Left as an unresolved row for a later manual Refresh Provider Status attempt.
            }
        }
    }

    private void OnEnergyStateChanged(object? sender, EventArgs args)
    {
        RaiseChanged();
        Pump();
    }

    private void OnConnectivityChanged(object? sender, EventArgs args)
    {
        // A genuine network transition deserves a fresh pause decision rather than inheriting
        // whatever global or per-connection override an earlier resume call left active.
        lock (_gate)
        {
            _connectivityOverrideActive = false;
            _connectionsWithConnectivityOverride.Clear();
            _jobsWithConnectivityOverride.Clear();
        }
        RaiseChanged();
        Pump();
    }

    public string Enqueue(GenerationJobSnapshot snapshot, string connectionId)
    {
        var workspace = _libraries.Workspace ?? throw new InvalidOperationException("No library is open.");
        QueuedJob job;
        lock (_gate)
        {
            job = new QueuedJob
            {
                JobId = LibraryRules.NewId(),
                DraftId = snapshot.DraftId,
                ConnectionId = connectionId,
                Snapshot = snapshot,
                Workspace = workspace,
                Phase = GenerationJobPhase.Queued
            };
            // Kicked off while still holding _gate, and before Pump() below can schedule this job
            // onto another thread: starting an async method only runs synchronously up to its first
            // await (issuing the database write, not waiting on it), so this doesn't block Enqueue or
            // hold the lock across I/O — but it does guarantee RecordCreation is populated before
            // RunJobAsync could ever observe this job and check it.
            job.RecordCreation = CreateQueuedRecordAsync(job, workspace);
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
        }
        return job.JobId;
    }

    /// <summary>Creates this job's durable <see cref="GenerationStatus.Queued"/> record so it exists
    /// from the moment it enters the queue, not only once it starts running. A transient storage
    /// failure here degrades to the pre-existing in-memory-only behavior (logged, not thrown) rather
    /// than dropping the job — <see cref="RunJobAsync"/> still runs it normally, just without status
    /// persistence.</summary>
    private async Task<GenerationRecord?> CreateQueuedRecordAsync(QueuedJob job, ILibraryWorkspace workspace)
    {
        try
        {
            var snapshot = job.Snapshot;
            var record = await workspace.CreateQueuedGenerationRecordAsync(snapshot.ModelId, snapshot.Prompt, snapshot.ResultCount, snapshot.DestinationFolderId, snapshot.SystemInstructions, snapshot.SourceSlots, snapshot.Settings, snapshot.AcceptedImprovementRecordId).ConfigureAwait(false);
            lock (_gate) job.GenerationRecordId = record.Id;
            return record;
        }
        catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException)
        {
            _diagnostics?.Log(new DiagnosticLogEntry(DateTimeOffset.UtcNow, OperationType: "Generation.QueuedRecordCreationFailed", LocalRecordId: null, SanitizedError: exception.Message, IsVerbose: _diagnostics.VerboseEnabled));
            return null;
        }
    }

    /// <summary>Best-effort status advance for a queued job's durable record — a no-op if record
    /// creation hasn't completed (or failed). Fire-and-forget: this is intermediate telemetry, not
    /// the terminal outcome write, which stays awaited via <c>existingGenerationRecordId</c> on the
    /// final <c>Record*GenerationResultAsync</c> call, so losing an intermediate transition here never
    /// loses the final one.</summary>
    private void AdvanceLocked(QueuedJob job, GenerationStatus status, GenerationHoldReason? hold = null, GenerationFailureReason? failureReason = null)
    {
        string? recordId;
        lock (_gate) recordId = job.GenerationRecordId;
        if (recordId is null) return;
        _ = AdvanceGenerationStatusSafeAsync(job.Workspace, recordId, status, hold, failureReason);
    }

    private static async Task AdvanceGenerationStatusSafeAsync(ILibraryWorkspace workspace, string generationRecordId, GenerationStatus status, GenerationHoldReason? hold, GenerationFailureReason? failureReason = null)
    {
        try
        {
            await workspace.AdvanceGenerationStatusAsync(generationRecordId, status, hold, failureReason).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException or LibraryValidationException)
        {
        }
    }

    /// <summary>Same as <see cref="AdvanceLocked"/>, but for a job that may still be mid-creation of
    /// its durable record (e.g. cancelled the instant it was enqueued) — waits for
    /// <see cref="QueuedJob.RecordCreation"/> first instead of silently skipping the transition when
    /// <see cref="QueuedJob.GenerationRecordId"/> isn't populated yet.</summary>
    private static void AdvanceAfterCreationLocked(QueuedJob job, GenerationStatus status, GenerationHoldReason? hold = null) => _ = AdvanceAfterCreationAsync(job, status, hold);

    private static async Task AdvanceAfterCreationAsync(QueuedJob job, GenerationStatus status, GenerationHoldReason? hold)
    {
        if (job.RecordCreation is not null)
        {
            try { await job.RecordCreation.ConfigureAwait(false); }
            catch { return; }
        }
        var recordId = job.GenerationRecordId;
        if (recordId is null) return;
        await AdvanceGenerationStatusSafeAsync(job.Workspace, recordId, status, hold).ConfigureAwait(false);
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
        QueuedJob? cancelledBeforeSubmission = null;
        var changed = false;
        lock (_gate)
        {
            if (!_jobsById.TryGetValue(jobId, out var job)) return;
            if (IsPreSubmissionPhase(job.Phase))
            {
                _queues[job.ConnectionId].Remove(job);
                _jobsById.Remove(jobId);
                _jobsWithConnectivityOverride.Remove(jobId);
                RemoveActiveJobId(job.DraftId, jobId);
                RecordOutcome(job.DraftId, new GenerationJobOutcome(jobId, job.DraftId, null, null, CancelledBeforeSubmission: true, DateTimeOffset.UtcNow));
                changed = true;
                cancelledBeforeSubmission = job;
            }
            else
            {
                toCancel = job.Cancellation;
            }
        }
        toCancel?.Cancel();
        if (cancelledBeforeSubmission is not null) AdvanceAfterCreationLocked(cancelledBeforeSubmission, GenerationStatus.CancelledBeforeSubmission);
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

    /// <summary>Whether this specific still-queued job's connection is currently being held back by
    /// the rate-limit backoff in <see cref="Pump"/> — the per-job equivalent of
    /// <see cref="IsConnectionAwaitingRateLimitReset"/>, for a run card that only knows its own job
    /// ID rather than a connection ID.</summary>
    public bool IsJobAwaitingRateLimitReset(string jobId)
    {
        lock (_gate)
        {
            return _jobsById.TryGetValue(jobId, out var job) && job.Phase == GenerationJobPhase.Queued && IsConnectionOutOfRequestQuota(job.ConnectionId, out _);
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
                entries.Add(new GenerationQueueEntry(job.JobId, job.DraftId, job.Snapshot.SubmittedTabTitle, job.ConnectionId, job.Snapshot.ModelId, job.Snapshot.Prompt, job.Phase, ComputeQueuePosition(job), job.NonRunnable));
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
        if (!IsPreSubmissionPhase(job.Phase)) return null;
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
        List<CancellationTokenSource> toCancel = [];
        lock (_gate)
        {
            foreach (var job in _jobsById.Values.ToArray())
            {
                // A job whose library was switched away from keeps running as long as that workspace
                // is still open (either still active, or kept open in the
                // background by AppLibraryState because this same check told it to). Only a job
                // whose workspace was actually disposed (switched away from with no active work, or
                // now genuinely closed) needs to be torn down here.
                if (_libraries.IsWorkspaceOpen(job.Workspace)) continue;
                if (IsPreSubmissionPhase(job.Phase))
                {
                    _queues[job.ConnectionId].Remove(job);
                    _jobsById.Remove(job.JobId);
                    _jobsWithConnectivityOverride.Remove(job.JobId);
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
        _ = ResumePendingDownloadsAsync();
        _ = ResumeInFlightGenerationsAsync();
        _ = ReconcileStagedResultsAsync();
    }

    private static bool ReferencesFile(GenerationJobSnapshot snapshot, string fileId) =>
        (snapshot.SourceSlots ?? []).Any(slot => slot.FileId == fileId);

    private static bool ReferencesFolder(GenerationJobSnapshot snapshot, string folderId) => snapshot.DestinationFolderId == folderId;

    /// <summary>True while a still-<see cref="GenerationJobPhase.Running"/> job is actively reading or
    /// uploading this file as a source input — the narrow "actively in use" sense that blocks
    /// recycling, distinct from a merely-<see cref="GenerationJobPhase.Queued"/> job that
    /// references the same file (which pauses instead of blocking, see <see cref="NotifyFileRecycled"/>).</summary>
    public bool IsFileActivelyInUse(string fileId)
    {
        lock (_gate) return _jobsById.Values.Any(job => job.Phase == GenerationJobPhase.Running && ReferencesFile(job.Snapshot, fileId));
    }

    /// <summary>Same as <see cref="IsFileActivelyInUse"/> but for a destination folder.</summary>
    public bool IsFolderActivelyInUse(string folderId)
    {
        lock (_gate) return _jobsById.Values.Any(job => job.Phase == GenerationJobPhase.Running && ReferencesFolder(job.Snapshot, folderId));
    }

    /// <summary>True while a connection has a submitted (<see cref="GenerationJobPhase.Running"/> or
    /// <see cref="GenerationJobPhase.Monitoring"/>) job against it — the user must cancel or wait for
    /// these rather than recycling the connection out from under them. A merely
    /// <see cref="GenerationJobPhase.Queued"/> job never blocks recycling; see
    /// <see cref="CancelQueuedJobsForConnection"/> for the cascade that handles those instead.</summary>
    public bool IsConnectionActivelyInUse(string connectionId)
    {
        lock (_gate) return _jobsById.Values.Any(job => job.ConnectionId == connectionId && job.Phase is GenerationJobPhase.Running or GenerationJobPhase.Monitoring);
    }

    /// <summary>Same as <see cref="IsConnectionActivelyInUse"/> but for a model.</summary>
    public bool IsModelActivelyInUse(string modelId)
    {
        lock (_gate) return _jobsById.Values.Any(job => job.Snapshot.ModelId == modelId && job.Phase is GenerationJobPhase.Running or GenerationJobPhase.Monitoring);
    }

    /// <summary>True while any job (queued, running, monitoring or dependency-paused) still belongs
    /// to this workspace — the predicate <see cref="AppLibraryState.RegisterKeepOpenPredicate"/>
    /// registers so a library the user switches away from stays open and locked while it has active
    /// work, rather than being disposed and taking that work down with it.</summary>
    public bool HasActiveWorkFor(ILibraryWorkspace workspace)
    {
        lock (_gate) return _jobsById.Values.Any(job => ReferenceEquals(job.Workspace, workspace));
    }

    /// <summary>Count of active jobs against this workspace, for a global activity indicator grouped
    /// by library.</summary>
    public int GetActiveJobCountForWorkspace(ILibraryWorkspace workspace)
    {
        lock (_gate) return _jobsById.Values.Count(job => ReferenceEquals(job.Workspace, workspace));
    }

    /// <summary>Submitted-tab titles of every still-queued (never yet submitted) job that depends on
    /// this connection — for a recycle-confirmation cascade warning, since these jobs must be
    /// included in it.</summary>
    public IReadOnlyList<string> GetQueuedJobTitlesForConnection(string connectionId)
    {
        lock (_gate) return _jobsById.Values.Where(job => job.ConnectionId == connectionId && IsPreSubmissionPhase(job.Phase)).Select(job => job.Snapshot.SubmittedTabTitle).ToArray();
    }

    /// <summary>Same as <see cref="GetQueuedJobTitlesForConnection"/> but for a model.</summary>
    public IReadOnlyList<string> GetQueuedJobTitlesForModel(string modelId)
    {
        lock (_gate) return _jobsById.Values.Where(job => job.Snapshot.ModelId == modelId && IsPreSubmissionPhase(job.Phase)).Select(job => job.Snapshot.SubmittedTabTitle).ToArray();
    }

    /// <summary>Submitted-tab titles of every job (queued or already dependency-paused) that
    /// references this file, for a recycle/permanent-deletion preview.</summary>
    public IReadOnlyList<string> GetQueuedJobTitlesForFile(string fileId)
    {
        lock (_gate) return _jobsById.Values.Where(job => IsPreSubmissionPhase(job.Phase) && ReferencesFile(job.Snapshot, fileId)).Select(job => job.Snapshot.SubmittedTabTitle).ToArray();
    }

    /// <summary>Same as <see cref="GetQueuedJobTitlesForFile"/> but for a destination folder.</summary>
    public IReadOnlyList<string> GetQueuedJobTitlesForFolder(string folderId)
    {
        lock (_gate) return _jobsById.Values.Where(job => IsPreSubmissionPhase(job.Phase) && ReferencesFolder(job.Snapshot, folderId)).Select(job => job.Snapshot.SubmittedTabTitle).ToArray();
    }

    /// <summary>Cascade-cancels every still-queued (never yet submitted) job depending on this
    /// connection — called after the user confirms a recycle-connection cascade warning.
    /// A job that already reached <see cref="GenerationJobPhase.Running"/> or
    /// <see cref="GenerationJobPhase.Monitoring"/> is never touched here; recycling is blocked
    /// entirely while one of those exists (see <see cref="IsConnectionActivelyInUse"/>), so by the
    /// time this runs only genuinely never-submitted jobs remain.</summary>
    public void CancelQueuedJobsForConnection(string connectionId)
    {
        List<string> toCancel;
        lock (_gate) toCancel = _jobsById.Values.Where(job => job.ConnectionId == connectionId && IsPreSubmissionPhase(job.Phase)).Select(job => job.JobId).ToList();
        foreach (var jobId in toCancel) Cancel(jobId);
    }

    /// <summary>Same as <see cref="CancelQueuedJobsForConnection"/> but for a model.</summary>
    public void CancelQueuedJobsForModel(string modelId)
    {
        List<string> toCancel;
        lock (_gate) toCancel = _jobsById.Values.Where(job => job.Snapshot.ModelId == modelId && IsPreSubmissionPhase(job.Phase)).Select(job => job.JobId).ToList();
        foreach (var jobId in toCancel) Cancel(jobId);
    }

    private void MarkDependencyRecycled(string dependencyId, Func<GenerationJobSnapshot, string, bool> references, bool ignoreWhenSnapshotted = false)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var job in _jobsById.Values)
            {
                if (!IsPreSubmissionPhase(job.Phase) || !references(job.Snapshot, dependencyId) || (ignoreWhenSnapshotted && job.GenerationRecordId is not null && job.Snapshot.Mode == GenerationMode.Image)) continue;
                if (job.RecycledDependencyIds.Add(dependencyId)) changed = true;
                if (job.Phase != GenerationJobPhase.DependencyRecycled) { job.Phase = GenerationJobPhase.DependencyRecycled; changed = true; }
            }
        }
        if (changed) RaiseChanged();
    }

    private void MarkDependencyRestored(string dependencyId, Func<GenerationJobSnapshot, string, bool> references)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var job in _jobsById.Values)
            {
                if (job.Phase != GenerationJobPhase.DependencyRecycled || !references(job.Snapshot, dependencyId) || !job.RecycledDependencyIds.Remove(dependencyId)) continue;
                changed = true;
                if (job.RecycledDependencyIds.Count == 0 && !job.NonRunnable) job.Phase = GetConnectivityPhaseLocked();
            }
        }
        if (changed) { RaiseChanged(); Pump(); }
    }

    private void MarkDependencyPermanentlyDeleted(string dependencyId, Func<GenerationJobSnapshot, string, bool> references, bool ignoreWhenSnapshotted = false)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var job in _jobsById.Values)
            {
                if (!IsPreSubmissionPhase(job.Phase) || !references(job.Snapshot, dependencyId) || (ignoreWhenSnapshotted && job.GenerationRecordId is not null && job.Snapshot.Mode == GenerationMode.Image)) continue;
                job.NonRunnable = true;
                if (job.Phase != GenerationJobPhase.DependencyRecycled) job.Phase = GenerationJobPhase.DependencyRecycled;
                changed = true;
            }
        }
        if (changed) RaiseChanged();
    }

    /// <summary>Pauses every still-queued job whose source-image slot references this file —
    /// called after a source file is recycled.</summary>
    public void NotifyFileRecycled(string fileId) => MarkDependencyRecycled(fileId, ReferencesFile, ignoreWhenSnapshotted: true);

    /// <summary>Resumes a paused job once every dependency it was waiting on (this file included) is
    /// restored — called after a source file is restored from the recycle bin.</summary>
    public void NotifyFileRestored(string fileId) => MarkDependencyRestored(fileId, ReferencesFile);

    /// <summary>Marks every job referencing this file as permanently non-runnable —
    /// called after a source file is permanently deleted.</summary>
    public void NotifyFilePermanentlyDeleted(string fileId) => MarkDependencyPermanentlyDeleted(fileId, ReferencesFile, ignoreWhenSnapshotted: true);

    /// <summary>Same as <see cref="NotifyFileRecycled"/> but for a destination folder.</summary>
    public void NotifyFolderRecycled(string folderId) => MarkDependencyRecycled(folderId, ReferencesFolder);

    /// <summary>Same as <see cref="NotifyFileRestored"/> but for a destination folder.</summary>
    public void NotifyFolderRestored(string folderId) => MarkDependencyRestored(folderId, ReferencesFolder);

    /// <summary>Same as <see cref="NotifyFilePermanentlyDeleted"/> but for a destination folder.</summary>
    public void NotifyFolderPermanentlyDeleted(string folderId) => MarkDependencyPermanentlyDeleted(folderId, ReferencesFolder);

    private static bool IsConnectivityPaused(GenerationJobPhase phase) =>
        phase is GenerationJobPhase.PausedConnectionLost or GenerationJobPhase.PausedMeteredNetwork;

    private static bool IsPreSubmissionPhase(GenerationJobPhase phase) =>
        phase is GenerationJobPhase.Queued or GenerationJobPhase.DependencyRecycled or
            GenerationJobPhase.PausedConnectionLost or GenerationJobPhase.PausedMeteredNetwork;

    private GenerationJobPhase GetConnectivityPhaseLocked() =>
        _connectionLostLatched ? GenerationJobPhase.PausedConnectionLost :
        _meteredPauseLatched ? GenerationJobPhase.PausedMeteredNetwork :
        GenerationJobPhase.Queued;

    private bool IsConnectivityPauseOverriddenLocked(QueuedJob job) =>
        _connectivityOverrideActive || _connectionsWithConnectivityOverride.Contains(job.ConnectionId) ||
        _jobsWithConnectivityOverride.Contains(job.JobId);

    private void ApplyConnectivityPauseLocked()
    {
        var phase = GetConnectivityPhaseLocked();
        foreach (var job in _jobsById.Values)
        {
            if (!IsConnectivityPauseOverriddenLocked(job) &&
                (job.Phase == GenerationJobPhase.Queued || IsConnectivityPaused(job.Phase))) job.Phase = phase;
        }
    }

    private void ResumeConnectivityPausedJobsLocked(string? connectionId = null)
    {
        if (connectionId is null && (_connectionLostLatched || _meteredPauseLatched)) return;
        foreach (var job in _jobsById.Values)
        {
            if ((connectionId is null || job.ConnectionId == connectionId) && IsConnectivityPaused(job.Phase)) job.Phase = GenerationJobPhase.Queued;
        }
    }

    private void Pump()
    {
        while (true)
        {
            QueuedJob? started = null;
            lock (_gate)
            {
                // Offline/metered-network gating happens once per Pump() call, before scanning for
                // work to start — never affects a job already running, matching the energy-saver
                // cap's own "only ever stops new starts" contract. Skipped entirely while a manual
                // ResumeQueue() override is active, so resuming actually lets a job start now rather
                // than immediately re-latching on this very call.
                if (!_connectivityOverrideActive)
                {
                    if (_connectivity?.IsOffline == true) _connectionLostLatched = true;
                    if (_connectivity?.IsMetered == true)
                    {
                        var policy = MeteredNetworkPolicy;
                        if (policy is MeteredNetworkTransferPolicy.WifiOnly or MeteredNetworkTransferPolicy.Ask) _meteredPauseLatched = true;
                    }
                }
                if (_connectionLostLatched || _meteredPauseLatched) ApplyConnectivityPauseLocked();
                ResumeConnectivityPausedJobsLocked();

                if (_runningTotal >= EffectiveDeviceCap) break;
                var count = _connectionOrder.Count;
                for (var i = 0; i < count; i++)
                {
                    var index = (_cursor + i) % count;
                    var connectionId = _connectionOrder[index];
                    if (!_queues.TryGetValue(connectionId, out var queue) || queue.Count == 0) continue;
                    // A dependency-recycled job keeps its place in the queue but is
                    // never itself eligible to start — later, unaffected jobs on the same connection
                    // must still be able to run around it rather than stalling behind it.
                    var node = queue.First;
                    while (node is not null && node.Value.Phase != GenerationJobPhase.Queued) node = node.Next;
                    if (node is null) continue;
                    _runningPerConnection.TryGetValue(connectionId, out var running);
                    if (running >= GetConnectionCap(connectionId)) continue;
                    if (IsConnectionOutOfRequestQuota(connectionId, out var resetsAt))
                    {
                        ScheduleRateLimitRetryPump(connectionId, resetsAt);
                        continue;
                    }
                    started = node.Value;
                    queue.Remove(node);
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
    /// Proactive backoff: once a connection's last-observed remaining request quota
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

        if (job.RecordCreation is not null)
        {
            try { await job.RecordCreation.ConfigureAwait(false); }
            catch { /* already logged in CreateQueuedRecordAsync */ }
        }

        var outcome = await ExecuteAsync(job, cancellation.Token).ConfigureAwait(false);

        lock (_gate)
        {
            _jobsById.Remove(job.JobId);
            _jobsWithConnectivityOverride.Remove(job.JobId);
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
        // A background-kept library's lock is released once its last operation
        // completes. A no-op for the still-active workspace or one with other jobs still pending.
        if (!HasActiveWorkFor(job.Workspace)) _ = _libraries.ReleaseBackgroundWorkspaceIfIdleAsync(job.Workspace);
        _diagnostics?.Log(new DiagnosticLogEntry(
            DateTimeOffset.UtcNow,
            OperationType: outcome.StagedForRecovery ? "Generation.StagedForRecovery" : outcome.Record is not null ? "Generation.Completed" : outcome.CancelledBeforeSubmission ? "Generation.CancelledBeforeSubmission" : "Generation.Failed",
            LocalRecordId: outcome.Record?.Id,
            SanitizedError: outcome.LocalErrorMessage,
            IsVerbose: _diagnostics.VerboseEnabled));
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

    /// <summary>Resolves a reference-image/first-frame source slot's bytes for submission. Prefers
    /// this job's own durable record's already-captured snapshot (populated at creation time for both
    /// live-file and retained-snapshot slots alike — see
    /// <c>LibraryWorkspace.CreateQueuedGenerationRecordCoreAsync</c>) so submission never re-reads a
    /// live file that might have been recycled/deleted since the job was queued. Falls back to a live
    /// file read only if this job's own record was never durably created (a transient storage failure
    /// at enqueue time), and further falls back to reading straight from the slot's own retained
    /// snapshot source when even the live file is unavailable because the slot was already
    /// snapshot-backed (Use Again after the original file was permanently deleted).</summary>
    private static Task<ImageFileContent> ReadReferenceSourceContentAsync(QueuedJob job, GenerationSourceSlot slot, CancellationToken cancellationToken) =>
        job.GenerationRecordId is { } sourceRecordId
            ? job.Workspace.ReadGenerationInputSnapshotAsync(sourceRecordId, slot.Role, slot.Order, cancellationToken)
            : slot.FileId is { } liveFileId
                ? job.Workspace.ReadImageFileAsync(liveFileId, cancellationToken)
                : job.Workspace.ReadGenerationInputSnapshotAsync(slot.SnapshotSourceGenerationId!, slot.Role, slot.Order, cancellationToken);

    /// <summary>The mask counterpart of <see cref="ReadReferenceSourceContentAsync"/>.</summary>
    private static Task<byte[]> ReadMaskContentAsync(QueuedJob job, GenerationSourceSlot maskSlot, string maskId, CancellationToken cancellationToken) =>
        job.GenerationRecordId is { } recordId
            ? job.Workspace.ReadGenerationMaskSnapshotAsync(recordId, maskId, cancellationToken)
            : maskSlot.FileId is not null
                ? job.Workspace.ReadImageMaskAsync(maskId, cancellationToken)
                : job.Workspace.ReadGenerationMaskSnapshotAsync(maskSlot.SnapshotSourceGenerationId!, maskId, cancellationToken);

    private async Task<GenerationJobOutcome> ExecuteAsync(QueuedJob job, CancellationToken cancellationToken)
    {
        var snapshot = job.Snapshot;
        AdvanceLocked(job, GenerationStatus.Preparing);
        try
        {
            await _faultInjector.BeforePrepareReadAsync(cancellationToken).ConfigureAwait(false);
            var models = await job.Workspace.GetActiveModelsAsync(cancellationToken).ConfigureAwait(false);
            var model = models.FirstOrDefault(candidate => candidate.Id == snapshot.ModelId);
            if (model is null) return LocalFailureOutcome(job, "The model configured for this submission is no longer available.");
            var connections = await job.Workspace.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
            var connection = connections.FirstOrDefault(candidate => candidate.Id == model.ConnectionId);
            if (connection is not { HasCredential: true, CredentialRequiresRepair: false, CredentialRevisionId: { } revisionId }) return LocalFailureOutcome(job, "The connection configured for this submission is no longer available.");

            var adapter = _adapterResolver.Resolve(connection.ProviderType);
            var apiKey = await _credentials.GetActiveAsync(job.Workspace.Descriptor.LibraryId, connection.Id, revisionId).ConfigureAwait(false);

            GenerationRecord? record;
            var stagedForRecovery = false;
            if (snapshot.Mode == GenerationMode.Image)
            {
                var referenceImageSlots = (snapshot.SourceSlots ?? []).Where(slot => slot.Role == GenerationInputSlotRole.ReferenceImage).OrderBy(slot => slot.Order).ToArray();
                var sourceImages = new List<TextGenerationSourceImage>(referenceImageSlots.Length);
                foreach (var slot in referenceImageSlots)
                {
                    var sourceContent = await ReadReferenceSourceContentAsync(job, slot, cancellationToken).ConfigureAwait(false);
                    sourceImages.Add(new TextGenerationSourceImage(sourceContent.MediaType, sourceContent.Bytes));
                }
                var maskSlot = (snapshot.SourceSlots ?? []).SingleOrDefault(slot => slot.Role == GenerationInputSlotRole.Mask);
                TextGenerationSourceImage? mask = maskSlot?.AttachmentId is { } maskId
                    ? new TextGenerationSourceImage("image/png", await ReadMaskContentAsync(job, maskSlot, maskId, cancellationToken).ConfigureAwait(false))
                    : null;

                IReadOnlyList<byte[]>? images = null;
                string? errorMessage = null;
                try
                {
                    AdvanceLocked(job, GenerationStatus.Submitting);
                    job.SubmissionAttempted = true;
                    images = await adapter.GenerateImageAsync(connection, model, apiKey, snapshot.Prompt, snapshot.ResultCount, sourceImages, mask, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                {
                    errorMessage = exception.Message;
                }

                record = await job.Workspace.RecordImageGenerationResultAsync(model.Id, snapshot.Prompt, snapshot.ResultCount, snapshot.DestinationFolderId, images, errorMessage, snapshot.AcceptedImprovementRecordId, existingGenerationRecordId: job.GenerationRecordId, sourceSlots: snapshot.SourceSlots, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else if (snapshot.Mode == GenerationMode.Audio)
            {
                IReadOnlyList<byte[]>? audioFiles = null;
                string? errorMessage = null;
                try
                {
                    AdvanceLocked(job, GenerationStatus.Submitting);
                    job.SubmissionAttempted = true;
                    audioFiles = await adapter.GenerateAudioAsync(connection, model, apiKey, snapshot.Prompt, snapshot.ResultCount, snapshot.Voice, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                {
                    errorMessage = exception.Message;
                }

                record = await job.Workspace.RecordMediaGenerationResultAsync(model.Id, snapshot.Prompt, snapshot.ResultCount, snapshot.DestinationFolderId, audioFiles, errorMessage, snapshot.AcceptedImprovementRecordId, existingGenerationRecordId: job.GenerationRecordId, sourceSlots: snapshot.SourceSlots, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else if (snapshot.Mode == GenerationMode.Video)
            {
                (record, stagedForRecovery) = await ExecuteVideoGenerationAsync(job, connection, model, apiKey, adapter, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var referenceImageSlots = (snapshot.SourceSlots ?? []).Where(slot => slot.Role == GenerationInputSlotRole.ReferenceImage).OrderBy(slot => slot.Order).ToArray();
                var sourceImages = new List<TextGenerationSourceImage>(referenceImageSlots.Length);
                foreach (var slot in referenceImageSlots)
                {
                    var sourceContent = await ReadReferenceSourceContentAsync(job, slot, cancellationToken).ConfigureAwait(false);
                    sourceImages.Add(new TextGenerationSourceImage(sourceContent.MediaType, sourceContent.Bytes));
                }

                TextGenerationResult? result = null;
                string? errorMessage = null;
                try
                {
                    AdvanceLocked(job, GenerationStatus.Submitting);
                    job.SubmissionAttempted = true;
                    result = await adapter.GenerateTextAsync(connection, model, apiKey, snapshot.Prompt, snapshot.ResultCount, snapshot.SystemInstructions, sourceImages, snapshot.Settings, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
                {
                    errorMessage = exception.Message;
                }

                record = await job.Workspace.RecordTextGenerationResultAsync(model.Id, snapshot.Prompt, snapshot.ResultCount, snapshot.DestinationFolderId, result?.Texts, errorMessage, snapshot.SystemInstructions, result?.PromptTokens, result?.CompletionTokens, snapshot.SourceSlots, snapshot.AcceptedImprovementRecordId, snapshot.Settings, result?.SafetyBlockedCount ?? 0, existingGenerationRecordId: job.GenerationRecordId, candidates: result?.Candidates, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return new GenerationJobOutcome(job.JobId, job.DraftId, record, null, false, DateTimeOffset.UtcNow, stagedForRecovery);
        }
        catch (OperationCanceledException)
        {
            if (job.SuspendedByOperatingSystem)
            {
                // The OS suspending background execution is a known, local cause — recorded as a
                // distinct Failed reason rather than blaming the provider (Android execution
                // suspension and timeout are recorded separately from provider failure),
                // matching how any other local failure (e.g. LocalFailureOutcome) always finalizes to
                // a terminal state rather than leaving the record stranded non-terminal.
                AdvanceLocked(job, GenerationStatus.Failed, failureReason: GenerationFailureReason.ExecutionSuspended);
                return new GenerationJobOutcome(job.JobId, job.DraftId, null, "Background execution was suspended by the operating system.", CancelledBeforeSubmission: false, DateTimeOffset.UtcNow);
            }
            // Whether transmission reached the provider is genuinely unknown once a request is
            // already in flight — finalize immediately to SubmissionOutcomeUnknown rather than
            // leaving the durable record stranded non-terminal until a restart happens to sweep it
            // up; nothing was ever sent if cancellation landed before Submitting was even reached.
            AdvanceLocked(job, job.SubmissionAttempted ? GenerationStatus.SubmissionOutcomeUnknown : GenerationStatus.CancelledBeforeSubmission);
            return new GenerationJobOutcome(job.JobId, job.DraftId, null, null, CancelledBeforeSubmission: false, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Microsoft.Data.Sqlite.SqliteException is caught here too: a genuine storage failure
            // during the final commit's SQLite write throws that type directly (LibraryWorkspace's
            // mutation wrapper does no exception translation), not IOException/SlopFactoryException —
            // without this, such a failure would escape RunJobAsync's fire-and-forget call site as an
            // unobserved task exception, silently losing already-decoded result bytes with no outcome
            // recorded at all. A real gap found while adding recovery staging, not merely theoretical.
            return LocalFailureOutcome(job, exception.Message);
        }
    }

    /// <summary>
    /// Runs a video generation to completion. A request for more than one result submits that many
    /// independent provider jobs up front — <see cref="IProviderAdapter.SubmitVideoGenerationAsync"/>
    /// never accepts more than one result per call — and polls all of them as one indivisible group;
    /// a generation which requires multiple separate provider submissions occupies one queue
    /// position as an indivisible group. The final record reflects whichever
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
    /// whole poll duration — an asynchronous job releases its submission slot after the provider
    /// durably accepts it, so the job moves to
    /// <see cref="GenerationJobPhase.Monitoring"/> and no longer counts against the device-wide or
    /// per-connection concurrency cap while only polling for status.
    /// Known limitation, not yet addressed: polling does not resume automatically after an
    /// application restart — that needs separate queue-scheduler work.
    /// If the final commit fails because the destination library's volume is disconnected
    /// (checked via <see cref="ILibraryAvailabilityProbe"/> only once bytes are already in hand —
    /// never for an ordinary provider/validation failure), the downloaded result files are staged
    /// into device-wide recovery storage instead of being discarded and the
    /// returned record is null with <c>StagedForRecovery</c> true. Both new dependencies are
    /// optional constructor params — a harness that omits them gets the pre-recovery-staging
    /// behavior (an ordinary local failure) unchanged.
    /// </summary>
    private async Task<(GenerationRecord? Record, bool StagedForRecovery)> ExecuteVideoGenerationAsync(QueuedJob job, Connection connection, Model model, string? apiKey, IProviderAdapter adapter, CancellationToken cancellationToken)
    {
        var snapshot = job.Snapshot;
        var resultCount = Math.Max(1, snapshot.ResultCount);
        var submitted = new List<(string ProviderJobId, string AsyncRecordId)>();
        var files = new List<byte[]>();
        // One message per failed/missing position, in the order each failure was discovered — the
        // shared media commit path consumes these for its trailing "shortfall" positions, giving
        // each failed child in a multi-job group its own real reason instead of one generic message.
        var childErrorMessages = new List<string>();
        // Maps an async job whose provider job completed but whose download failed to the index its
        // message occupies in childErrorMessages — since the shared commit path assigns shortfall
        // positions as files.Count + messageIndex (in childErrorMessages order), this is what lets
        // the async-job registry row be linked to its exact result position after the commit below,
        // instead of being deleted like every other terminal job.
        var downloadFailedMessageIndexByAsyncId = new Dictionary<string, int>(StringComparer.Ordinal);
        string? errorMessage = null;
        double? totalCost = null;
        string? costCurrency = null;

        var firstFrameSlot = (snapshot.SourceSlots ?? []).FirstOrDefault(slot => slot.Role == GenerationInputSlotRole.FirstFrame);
        TextGenerationSourceImage? firstFrame = null;
        if (firstFrameSlot is not null)
        {
            var firstFrameContent = await ReadReferenceSourceContentAsync(job, firstFrameSlot, cancellationToken).ConfigureAwait(false);
            firstFrame = new TextGenerationSourceImage(firstFrameContent.MediaType, firstFrameContent.Bytes);
        }

        try
        {
            AdvanceLocked(job, GenerationStatus.Submitting);
            job.SubmissionAttempted = true;
            for (var index = 0; index < resultCount; index++)
            {
                try
                {
                    var submission = await adapter.SubmitVideoGenerationAsync(connection, model, apiKey, snapshot.Prompt, firstFrame, cancellationToken).ConfigureAwait(false);
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
                AdvanceLocked(job, GenerationStatus.Processing);
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

                    if (pollResult.Outcome == AsyncGenerationPollOutcome.CompletedDownloadFailed)
                    {
                        // The provider itself confirmed completion — only the download failed, so
                        // this position is retryable via Refresh Provider Status/Import Missing
                        // Results rather than a genuine provider-side failure. The registry row is
                        // kept (linked to its result position after the commit below) instead of
                        // being deleted like every other terminal outcome.
                        var failureMessage = pollResult.ErrorMessage ?? "The provider completed this result, but downloading it failed.";
                        downloadFailedMessageIndexByAsyncId[entry.AsyncRecordId] = childErrorMessages.Count;
                        errorMessage ??= failureMessage;
                        childErrorMessages.Add(failureMessage);
                        await job.Workspace.UpdateAsyncRemoteJobPhaseAsync(entry.AsyncRecordId, AsyncRemoteJobPhase.CompletedAwaitingDownload, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                        pending.Remove(entry);
                        continue;
                    }

                    var phase = pollResult.Outcome == AsyncGenerationPollOutcome.Completed ? AsyncRemoteJobPhase.Completed : AsyncRemoteJobPhase.Failed;
                    await job.Workspace.UpdateAsyncRemoteJobPhaseAsync(entry.AsyncRecordId, phase, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                    if (pollResult.Outcome == AsyncGenerationPollOutcome.Completed && pollResult.Files is { Count: > 0 })
                    {
                        files.AddRange(pollResult.Files);
                        if (pollResult.Cost is { } cost)
                        {
                            // Only a run-level total is kept — SlopFactory never divides a run total
                            // among output sidecars — a per-child cost
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
            var cancelledRecord = await job.Workspace.RecordMediaGenerationResultAsync(model.Id, snapshot.Prompt, resultCount, snapshot.DestinationFolderId, files.Count > 0 ? files : null, null, snapshot.AcceptedImprovementRecordId, totalCost, costCurrency, wasCancelled: true, childErrorMessages: childErrorMessages, existingGenerationRecordId: job.GenerationRecordId, sourceSlots: snapshot.SourceSlots, cancellationToken: CancellationToken.None).ConfigureAwait(false);
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
            return (cancelledRecord, false);
        }

        GenerationRecord? record;
        var stagedForRecovery = false;
        try
        {
            record = await job.Workspace.RecordMediaGenerationResultAsync(model.Id, snapshot.Prompt, resultCount, snapshot.DestinationFolderId, files.Count > 0 ? files : null, files.Count == 0 ? errorMessage : null, snapshot.AcceptedImprovementRecordId, totalCost, costCurrency, childErrorMessages: childErrorMessages, existingGenerationRecordId: job.GenerationRecordId, sourceSlots: snapshot.SourceSlots, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (files.Count > 0 && _recoveryStaging is not null && _availabilityProbe is not null
            && exception is IOException or Microsoft.Data.Sqlite.SqliteException
            && !_availabilityProbe.IsAvailable(job.Workspace.Descriptor.RootPath, null, out _))
        {
            // The destination volume is gone — not some other storage fault, which is why the
            // availability probe is checked only after catching a storage-shaped exception, never
            // for an ordinary provider/validation failure. Stage every already-downloaded file
            // instead of silently discarding real, already-completed provider work,
            // tagged with its intended generation record/position so a later reconciliation pass can
            // commit it into that exact record once the library returns.
            for (var position = 0; position < files.Count; position++)
            {
                await _recoveryStaging.StageAsync(job.Workspace.Descriptor.LibraryId, job.Workspace.Descriptor.DisplayName, job.DraftId, files[position], $"video-result-{Guid.NewGuid():N}.mp4", "video/mp4", job.GenerationRecordId, position, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            AdvanceLocked(job, GenerationStatus.AwaitingLibrary);
            record = null;
            stagedForRecovery = true;
        }

        if (!stagedForRecovery)
        {
            foreach (var entry in submitted)
            {
                // A download-failed job's position matches the shared commit path's own
                // shortfall-slot assignment (committed files first, then one shortfall slot per
                // childErrorMessages entry in order) — see downloadFailedMessageIndexByAsyncId's own
                // comment above.
                if (downloadFailedMessageIndexByAsyncId.TryGetValue(entry.AsyncRecordId, out var messageIndex))
                {
                    try
                    {
                        await _faultInjector.BeforePostCommitCleanupAsync(cancellationToken).ConfigureAwait(false);
                        await job.Workspace.LinkAsyncRemoteJobToGenerationResultAsync(entry.AsyncRecordId, record!.Id, files.Count + messageIndex, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException)
                    {
                        // Fall through and try to delete it instead — an un-linked row would
                        // otherwise be invisible to Refresh Provider Status but still show up as an
                        // unresolved job.
                    }
                }
                try
                {
                    await _faultInjector.BeforePostCommitCleanupAsync(cancellationToken).ConfigureAwait(false);
                    await job.Workspace.DeleteAsyncRemoteJobAsync(entry.AsyncRecordId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or SlopFactoryException or ObjectDisposedException)
                {
                    // The generation already committed above; failing to remove a now-stale
                    // pending-job registry row is a harmless leftover, not a reason to report the
                    // generation as failed.
                }
            }
        }

        return (record, stagedForRecovery);
    }

    /// <summary>Minimum time between two **Refresh Provider Status** attempts for the same async job
    /// — a repeat click inside this window is rejected locally without making a provider request,
    /// rather than letting rapid repeated clicks hammer the provider.</summary>
    private static readonly TimeSpan RefreshProviderStatusThrottle = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, DateTimeOffset> _lastRefreshAttemptByAsyncJobId = new(StringComparer.Ordinal);

    /// <summary>
    /// Retries downloading a result whose provider job completed but whose initial download failed
    /// (<see cref="AsyncRemoteJobPhase.CompletedAwaitingDownload"/>) — the **Refresh Provider
    /// Status**/**Import Missing Results** action. Re-polls the same provider job ID; since the
    /// provider already reported it completed once, a successful poll now always returns
    /// <see cref="AsyncGenerationPollOutcome.Completed"/> or the same
    /// <see cref="AsyncGenerationPollOutcome.CompletedDownloadFailed"/> again (never back to
    /// <see cref="AsyncGenerationPollOutcome.Processing"/>). On success, commits the recovered bytes
    /// into the existing generation record's failed position and removes the registry row; on a
    /// repeat download failure, the row is left exactly as it was for a later retry.
    /// </summary>
    public async Task<bool> RetryMissingResultDownloadAsync(string asyncJobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_lastRefreshAttemptByAsyncJobId.TryGetValue(asyncJobId, out var lastAttempt) && DateTimeOffset.UtcNow - lastAttempt < RefreshProviderStatusThrottle)
            {
                return false;
            }
            _lastRefreshAttemptByAsyncJobId[asyncJobId] = DateTimeOffset.UtcNow;
        }

        var workspace = _libraries.Workspace ?? throw new InvalidOperationException("No library is open.");
        var jobs = await workspace.GetPendingAsyncRemoteJobsAsync(cancellationToken).ConfigureAwait(false);
        var asyncJob = jobs.FirstOrDefault(candidate => candidate.Id == asyncJobId);
        if (asyncJob is not { Phase: AsyncRemoteJobPhase.CompletedAwaitingDownload, GenerationRecordId: { } generationRecordId, Position: { } position })
        {
            return false;
        }

        var connections = await workspace.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
        var connection = connections.FirstOrDefault(candidate => candidate.Id == asyncJob.ConnectionId);
        if (connection is not { HasCredential: true, CredentialRequiresRepair: false, CredentialRevisionId: { } revisionId })
        {
            return false;
        }

        var adapter = _adapterResolver.Resolve(connection.ProviderType);
        var apiKey = await _credentials.GetActiveAsync(workspace.Descriptor.LibraryId, connection.Id, revisionId).ConfigureAwait(false);

        AsyncGenerationPollResult pollResult;
        try
        {
            pollResult = await adapter.PollVideoGenerationAsync(connection, apiKey, asyncJob.ProviderJobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ProviderAdapterException or HttpRequestException)
        {
            return false;
        }

        if (pollResult.Outcome != AsyncGenerationPollOutcome.Completed || pollResult.Files is not { Count: > 0 } files)
        {
            return false;
        }

        // One submitted job fills exactly one result position; if the provider's unsigned_urls[]
        // ever returned more than one entry for a single job, only the first is recoverable here —
        // the same single-job/single-position assumption the original commit path already makes.
        await workspace.ImportMissingResultAsync(generationRecordId, position, files[0], cancellationToken).ConfigureAwait(false);
        await workspace.DeleteAsyncRemoteJobAsync(asyncJobId, cancellationToken).ConfigureAwait(false);
        lock (_gate) _lastRefreshAttemptByAsyncJobId.Remove(asyncJobId);
        RaiseChanged();
        return true;
    }

    /// <summary>A local failure never reaches a provider, so it finalizes this job's durable record
    /// to <see cref="GenerationStatus.Failed"/> directly rather than leaving it stranded at whatever
    /// nonterminal status it last reached (e.g. <see cref="GenerationStatus.Preparing"/>).</summary>
    private GenerationJobOutcome LocalFailureOutcome(QueuedJob job, string message)
    {
        AdvanceLocked(job, GenerationStatus.Failed);
        return new(job.JobId, job.DraftId, null, message, false, DateTimeOffset.UtcNow);
    }

    private void RaiseChanged()
    {
        UpdateBackgroundExecution();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Starts/stops Android's foreground background-execution service to match
    /// whether any job actually needs it — <see cref="GenerationJobPhase.Running"/> (actively
    /// uploading/reading) or <see cref="GenerationJobPhase.Monitoring"/> (actively polling a
    /// submitted async job). A merely <see cref="GenerationJobPhase.Queued"/> job needs no
    /// background execution yet — this is used for active transfers rather than
    /// indefinite provider-status polling. A no-op on every other platform
    /// (<see cref="NullBackgroundExecutionService"/>).
    /// </summary>
    private void UpdateBackgroundExecution()
    {
        if (_backgroundExecution is null) return;
        int running, monitoring;
        lock (_gate)
        {
            running = _jobsById.Values.Count(job => job.Phase == GenerationJobPhase.Running);
            monitoring = _jobsById.Values.Count(job => job.Phase == GenerationJobPhase.Monitoring);
        }
        if (running + monitoring > 0) _backgroundExecution.EnsureRunning($"{running + monitoring} generation(s) in progress");
        else _backgroundExecution.StopRunning();
    }
}
