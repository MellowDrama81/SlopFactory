using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Gui.Services;

public sealed class AppLibraryState : IAsyncDisposable
{
    private readonly ILibraryWorkspaceFactory _factory;
    private readonly ILibraryLocationService _locations;
    private readonly IRecentLibraryService _recentLibraries;
    private readonly ILibraryAvailabilityProbe _availability;
    private readonly IAppPreferenceStore _preferences;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppLibraryState(ILibraryWorkspaceFactory factory, ILibraryLocationService locations, IRecentLibraryService recentLibraries, ILibraryAvailabilityProbe availability, IAppPreferenceStore preferences)
    {
        _factory = factory;
        _locations = locations;
        _recentLibraries = recentLibraries;
        _availability = availability;
        _preferences = preferences;
    }

    public ILibraryWorkspace? Workspace { get; private set; }
    public string? Error { get; private set; }
    public bool IsInitialized { get; private set; }
    public string? ActivePath { get; private set; }
    public LibraryBrowserSession BrowserSession { get; private set; } = new();
    public event EventHandler? Changed;

    private readonly List<Func<ILibraryWorkspace, bool>> _keepOpenPredicates = [];
    private readonly Dictionary<string, ILibraryWorkspace> _backgroundWorkspaces = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a predicate consulted before disposing the outgoing workspace on a library switch
    /// (plan.md:423-424): if any registered predicate returns true for it, it is kept open in the
    /// background instead of disposed. <see cref="GenerationQueueService"/> registers one during
    /// startup so a library with active queued/running work is never torn out from under it merely
    /// because the user switched away. Not itself a DI registration (kept as a plain method call to
    /// avoid a circular constructor dependency between the two services).
    /// </summary>
    public void RegisterKeepOpenPredicate(Func<ILibraryWorkspace, bool> predicate) => _keepOpenPredicates.Add(predicate);

    private bool HasRegisteredActiveWork(ILibraryWorkspace workspace) => _keepOpenPredicates.Any(predicate => predicate(workspace));

    /// <summary>Every library kept open in the background for still-active work, for a global
    /// activity indicator (plan.md:425) grouped by library display name.</summary>
    public IReadOnlyList<(string LibraryId, string DisplayName, ILibraryWorkspace Workspace)> BackgroundLibraries =>
        _backgroundWorkspaces.Values.Select(workspace => (workspace.Descriptor.LibraryId, workspace.Descriptor.DisplayName, workspace)).ToArray();

    /// <summary>True if this library — active or kept open in the background — currently has
    /// registered active work, so **Forget Library** can refuse it (plan.md:428).</summary>
    public bool HasActiveWorkFor(string libraryId)
    {
        if (Workspace is not null && Workspace.Descriptor.LibraryId == libraryId && HasRegisteredActiveWork(Workspace)) return true;
        return _backgroundWorkspaces.ContainsKey(libraryId);
    }

    /// <summary>True while <paramref name="workspace"/> is either the active workspace or tracked in
    /// the background set — i.e. still genuinely open, as opposed to already disposed. Used by
    /// <see cref="GenerationQueueService"/> to decide whether a job's workspace reference is still
    /// safe to keep working against after a library switch.</summary>
    public bool IsWorkspaceOpen(ILibraryWorkspace workspace) =>
        ReferenceEquals(Workspace, workspace) || _backgroundWorkspaces.Values.Any(candidate => ReferenceEquals(candidate, workspace));

    /// <summary>
    /// Releases a background-kept workspace's lock once its last operation completes (plan.md:430).
    /// A no-op for the current active workspace (which has its own lifecycle) or a workspace not
    /// currently tracked in the background set, so a caller never needs to check which case applies
    /// first.
    /// </summary>
    public async Task ReleaseBackgroundWorkspaceIfIdleAsync(ILibraryWorkspace workspace)
    {
        if (ReferenceEquals(Workspace, workspace)) return;
        var libraryId = workspace.Descriptor.LibraryId;
        if (!_backgroundWorkspaces.TryGetValue(libraryId, out var tracked) || !ReferenceEquals(tracked, workspace)) return;
        _backgroundWorkspaces.Remove(libraryId);
        await workspace.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised while the current <see cref="Workspace"/> is still expected to be reachable,
    /// immediately before it is replaced or closed, so subscribers can flush in-flight state (e.g. a
    /// debounced autosave) to the outgoing workspace before it is disposed. Awaited in registration
    /// order. Not raised ahead of <see cref="CloseUnavailableLibraryAsync"/>, since that path only
    /// runs once the workspace's storage is already confirmed unreachable and a flush attempt could
    /// only add a doomed I/O wait, never succeed.
    /// </summary>
    public event Func<Task>? Closing;

    private async Task RaiseClosingAsync()
    {
        if (Closing is null) return;
        foreach (var handler in Closing.GetInvocationList().Cast<Func<Task>>())
        {
            await handler().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Best-effort flush ahead of a platform suspension (e.g. Android's process may be killed
    /// without warning). Reuses <see cref="Closing"/> so existing subscribers like Generate's
    /// autosave flush run unchanged. Only proceeds if no other library operation currently holds
    /// the state lock, since those operations already raise <see cref="Closing"/> themselves;
    /// skipping avoids racing a real switch/close that may dispose the workspace mid-flush.
    /// </summary>
    public async Task FlushForSuspensionAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try { await RaiseClosingAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private const string DirtyDraftsPreferenceKeyPrefix = "slopfactory.library.dirtydrafts.";
    private readonly Dictionary<string, int> _draftEditTokens = new();

    /// <summary>
    /// Draft IDs with device-local markers indicating in-memory edits may not have been persisted,
    /// either because a save is still pending/failed or because the process ended before autosave
    /// ran. Never contains draft content, only IDs. Reloaded whenever the active library changes.
    /// </summary>
    public IReadOnlyCollection<string> DirtyDraftIds { get; private set; } = [];

    /// <summary>
    /// Records that <paramref name="draftId"/> has an in-memory edit not yet confirmed persisted,
    /// and returns a token identifying this specific edit. Pass the returned token to
    /// <see cref="ClearDirtyDraft"/> once the corresponding save completes; the clear is skipped if
    /// a newer edit has arrived since, so a save in flight can never wipe the marker for an edit it
    /// didn't actually persist.
    /// </summary>
    public int MarkDraftDirty(string draftId)
    {
        var token = _draftEditTokens[draftId] = _draftEditTokens.GetValueOrDefault(draftId) + 1;
        if (Workspace is not null && !DirtyDraftIds.Contains(draftId))
        {
            DirtyDraftIds = [.. DirtyDraftIds, draftId];
            PersistDirtyDraftIds();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return token;
    }

    public void ClearDirtyDraft(string draftId, int expectedToken)
    {
        if (Workspace is null || !DirtyDraftIds.Contains(draftId) || _draftEditTokens.GetValueOrDefault(draftId) != expectedToken) return;
        DirtyDraftIds = DirtyDraftIds.Where(id => id != draftId).ToArray();
        PersistDirtyDraftIds();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DismissDirtyDrafts()
    {
        if (DirtyDraftIds.Count == 0) return;
        DirtyDraftIds = [];
        PersistDirtyDraftIds();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PersistDirtyDraftIds()
    {
        if (Workspace is null) return;
        _preferences.WriteString(DirtyDraftsPreferenceKeyPrefix + Workspace.Descriptor.LibraryId, string.Join(',', DirtyDraftIds));
    }

    private void LoadDirtyDraftIds()
    {
        DirtyDraftIds = Workspace is null
            ? []
            : _preferences.ReadString(DirtyDraftsPreferenceKeyPrefix + Workspace.Descriptor.LibraryId, string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Count of dirty-draft markers for any library, active or not — reads the same
    /// device-wide preference key <see cref="LoadDirtyDraftIds"/> keeps current for the active
    /// library, so it works for a library that's merely remembered too. Used to block
    /// **Forget Library** while unreconciled emergency draft markers exist (plan.md:459).</summary>
    public int GetDirtyDraftCount(string libraryId) =>
        _preferences.ReadString(DirtyDraftsPreferenceKeyPrefix + libraryId, string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>**Delete Recovery Drafts and Forget**'s draft-side step (plan.md:461): clears every
    /// dirty-draft marker for this library so **Forget Library** can proceed. Only removes the
    /// device-local marker, never the library's own persisted draft content.</summary>
    public void DeleteDirtyDraftsFor(string libraryId)
    {
        _preferences.WriteString(DirtyDraftsPreferenceKeyPrefix + libraryId, string.Empty);
        if (Workspace?.Descriptor.LibraryId == libraryId)
        {
            DirtyDraftIds = [];
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task InitializeAsync()
    {
        if (IsInitialized) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsInitialized) return;
            if (PlatformRuntimeSupport.GetUnsupportedMessage() is { } unsupportedMessage)
            {
                Error = unsupportedMessage;
                IsInitialized = true;
                return;
            }
            var path = _preferences.ReadString("active_library_path", _locations.DefaultPath);
            ActivePath = Path.GetFullPath(path);
            var remembered = _recentLibraries.GetAll().FirstOrDefault(item => SamePath(item.Path, path));
            try
            {
                if (!_locations.IsAllowedPath(path)) throw new LibraryValidationException("The saved library location is not an available application storage location.");
                if (remembered is not null && !_availability.IsAvailable(path, remembered.VolumeIdentity, out var unavailableStage))
                {
                    Error = "The remembered library is unavailable. Reconnect its storage, retry, choose another library, or forget this remembered location.";
                    _recentLibraries.RecordFailure(path, remembered.DisplayName, remembered.LibraryId, RememberedLibraryState.Unavailable, unavailableStage, NewDiagnosticId());
                    IsInitialized = true;
                    return;
                }
                Workspace = File.Exists(Path.Combine(path, "slopfactory-library.json")) ? await _factory.OpenAsync(path).ConfigureAwait(false) : await _factory.CreateAsync(path).ConfigureAwait(false);
                ActivePath = Path.GetFullPath(path);
                _recentLibraries.RecordOpened(Workspace.Descriptor);
                LoadDirtyDraftIds();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException)
            {
                var diagnostic = NewDiagnosticId();
                Error = $"The library could not be opened (stage: open; diagnostic: {diagnostic}). No automatic repair was attempted.";
                _recentLibraries.RecordFailure(path, remembered?.DisplayName ?? "Unavailable library", remembered?.LibraryId, RememberedLibraryState.Corrupt, "open", diagnostic);
            }
            IsInitialized = true;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RetryAsync()
    {
        if (ActivePath is null) return;
        await SwitchAsync(ActivePath, allowSamePathRetry: true).ConfigureAwait(false);
    }

    public Task SwitchAsync(string path) => SwitchAsync(path, allowSamePathRetry: false);

    private async Task SwitchAsync(string path, bool allowSamePathRetry)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_locations.IsAllowedPath(path)) throw new LibraryValidationException("That location is not an allowed application library location.");
            var fullPath = Path.GetFullPath(path);
            if (!allowSamePathRetry && Workspace is not null && string.Equals(fullPath, ActivePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            _recentLibraries.ValidateNoOverlap(fullPath);
            // If this path is already held open in the background (kept there by an earlier switch
            // away while it still had active work), reuse that instance rather than opening a second
            // exclusive lock on the same library — the lock file's FileShare.None would otherwise
            // reject it as "already open," which would be true but confusingly self-inflicted.
            var reusedBackgroundEntry = _backgroundWorkspaces.Values.FirstOrDefault(candidate => SamePath(candidate.Descriptor.RootPath, fullPath));
            ILibraryWorkspace replacement;
            if (reusedBackgroundEntry is not null)
            {
                replacement = reusedBackgroundEntry;
                _backgroundWorkspaces.Remove(reusedBackgroundEntry.Descriptor.LibraryId);
            }
            else
            {
                replacement = File.Exists(Path.Combine(fullPath, "slopfactory-library.json"))
                    ? await _factory.OpenAsync(fullPath).ConfigureAwait(false)
                    : await _factory.CreateAsync(fullPath).ConfigureAwait(false);
                var duplicate = _recentLibraries.GetAll().FirstOrDefault(item =>
                    string.Equals(item.LibraryId, replacement.Descriptor.LibraryId, StringComparison.Ordinal)
                    && !string.Equals(Path.GetFullPath(item.Path), fullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    && Directory.Exists(item.Path));
                if (duplicate is not null)
                {
                    await replacement.DisposeAsync().ConfigureAwait(false);
                    throw new LibraryValidationException("Another available location is already registered with this library ID. Open the registered location or remove the copied directory conflict.");
                }
            }
            var previous = Workspace;
            if (previous is not null) await RaiseClosingAsync().ConfigureAwait(false);
            Workspace = replacement;
            ActivePath = fullPath;
            Error = null;
            BrowserSession = new LibraryBrowserSession();
            _preferences.WriteString("active_library_path", fullPath);
            _recentLibraries.RecordOpened(replacement.Descriptor);
            LoadDirtyDraftIds();
            // plan.md:423-424 — a library with active queued/running work stays open and locked
            // rather than being disposed merely because the user switched away from it.
            if (previous is not null)
            {
                if (HasRegisteredActiveWork(previous)) _backgroundWorkspaces[previous.Descriptor.LibraryId] = previous;
                else await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(Path.Combine(fullPath, "slopfactory-library.json")))
            {
                var known = _recentLibraries.GetAll().FirstOrDefault(item => SamePath(item.Path, fullPath));
                var diagnostic = NewDiagnosticId();
                var state = RememberedLibraryState.Corrupt;
                var failureStage = "open";
                if (known is not null && !_availability.IsAvailable(fullPath, known.VolumeIdentity, out var unavailableStage)) { state = RememberedLibraryState.Unavailable; failureStage = unavailableStage; }
                _recentLibraries.RecordFailure(fullPath, known?.DisplayName ?? "Unavailable library", known?.LibraryId, state, failureStage, diagnostic);
            }
            throw;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RelinkAsync(string libraryId, string replacementPath)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var remembered = _recentLibraries.GetAll().SingleOrDefault(item => string.Equals(item.LibraryId, libraryId, StringComparison.Ordinal))
                ?? throw new LibraryValidationException("The remembered library could not be found.");
            if (_availability.IsAvailable(remembered.Path, remembered.VolumeIdentity, out _)) throw new LibraryValidationException("A moved library can be relinked only while its original remembered location is unavailable.");
            var fullPath = Path.GetFullPath(replacementPath);
            if (!_locations.IsAllowedPath(fullPath)) throw new LibraryValidationException("That location is not an allowed application library location.");
            var replacement = await _factory.OpenAsync(fullPath).ConfigureAwait(false);
            if (!string.Equals(replacement.Descriptor.LibraryId, libraryId, StringComparison.Ordinal))
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
                throw new LibraryValidationException("The selected library has a different permanent ID and cannot be used for relinking.");
            }
            var previous = Workspace;
            if (previous is not null) await RaiseClosingAsync().ConfigureAwait(false);
            Workspace = replacement;
            ActivePath = fullPath;
            Error = null;
            BrowserSession = new LibraryBrowserSession();
            _preferences.WriteString("active_library_path", fullPath);
            _recentLibraries.RecordOpened(replacement.Descriptor);
            LoadDirtyDraftIds();
            if (previous is not null)
            {
                if (HasRegisteredActiveWork(previous)) _backgroundWorkspaces[previous.Descriptor.LibraryId] = previous;
                else await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task AdoptCopyAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_locations.IsAllowedPath(path)) throw new LibraryValidationException("That location is not an allowed application library location.");
            var fullPath = Path.GetFullPath(path);
            var inspection = await _factory.OpenAsync(fullPath).ConfigureAwait(false);
            string copiedLibraryId;
            try { copiedLibraryId = inspection.Descriptor.LibraryId; }
            finally { await inspection.DisposeAsync().ConfigureAwait(false); }
            var sourceStillAvailable = Workspace is not null
                && string.Equals(Workspace.Descriptor.LibraryId, copiedLibraryId, StringComparison.Ordinal)
                && !SamePath(Workspace.Descriptor.RootPath, fullPath)
                && Directory.Exists(Workspace.Descriptor.RootPath);
            sourceStillAvailable |= _recentLibraries.GetAll().Any(item => string.Equals(item.LibraryId, copiedLibraryId, StringComparison.Ordinal)
                && !SamePath(item.Path, fullPath) && Directory.Exists(item.Path));
            if (!sourceStillAvailable) throw new LibraryValidationException("Only an available copied library with the same ID as another known library can be adopted.");

            var replacement = await _factory.AdoptCopyAsync(fullPath).ConfigureAwait(false);
            var previous = Workspace;
            if (previous is not null) await RaiseClosingAsync().ConfigureAwait(false);
            Workspace = replacement;
            ActivePath = fullPath;
            Error = null;
            BrowserSession = new LibraryBrowserSession();
            _preferences.WriteString("active_library_path", fullPath);
            _recentLibraries.RecordOpened(replacement.Descriptor);
            LoadDirtyDraftIds();
            if (previous is not null)
            {
                if (HasRegisteredActiveWork(previous)) _backgroundWorkspaces[previous.Descriptor.LibraryId] = previous;
                else await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task CloseInvalidLibraryAsync(ILibraryWorkspace expectedWorkspace, string message)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(Workspace, expectedWorkspace)) return;
            await RaiseClosingAsync().ConfigureAwait(false);
            Workspace = null;
            Error = message;
            BrowserSession = new LibraryBrowserSession();
            await expectedWorkspace.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task CloseUnavailableLibraryAsync(ILibraryWorkspace expectedWorkspace, string failureStage)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(Workspace, expectedWorkspace)) return;
            var path = ActivePath ?? expectedWorkspace.Descriptor.RootPath;
            _recentLibraries.RecordFailure(path, expectedWorkspace.Descriptor.DisplayName, expectedWorkspace.Descriptor.LibraryId, RememberedLibraryState.Unavailable, failureStage, NewDiagnosticId());
            Workspace = null;
            Error = "The active library became unavailable or read-only and was closed safely. Its remembered location was preserved.";
            BrowserSession = new LibraryBrowserSession();
            await expectedWorkspace.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Workspace is not null) await Workspace.DisposeAsync().ConfigureAwait(false);
        foreach (var background in _backgroundWorkspaces.Values) await background.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NewDiagnosticId() => Guid.NewGuid().ToString("N")[..12];

}

public enum LibraryBrowserViewMode
{
    List = 0,
    Grid = 1
}

public sealed class LibraryBrowserSession
{
    public string? FolderId { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public LibraryBrowseScope Scope { get; set; } = LibraryBrowseScope.CurrentFolder;
    public LibraryMediaKind MediaKind { get; set; } = LibraryMediaKind.Any;
    public FileOrigin? Origin { get; set; }
    public DateTime? ImportedFrom { get; set; }
    public DateTime? ImportedThrough { get; set; }
    public bool MetadataFilterEnabled { get; set; }
    public string MetadataKey { get; set; } = string.Empty;
    public MetadataValueKind MetadataKind { get; set; } = MetadataValueKind.Text;
    public MetadataFilterOperator MetadataOperator { get; set; } = MetadataFilterOperator.Equals;
    public string MetadataComparisonValue { get; set; } = string.Empty;
    public LibraryFileSort Sort { get; set; } = LibraryFileSort.Name;
    public LibraryBrowserViewMode ViewMode { get; set; } = LibraryBrowserViewMode.List;
    public int Offset { get; set; }
}
