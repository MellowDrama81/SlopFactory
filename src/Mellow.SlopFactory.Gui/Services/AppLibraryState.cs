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
            var replacement = File.Exists(Path.Combine(fullPath, "slopfactory-library.json"))
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
            var previous = Workspace;
            Workspace = replacement;
            ActivePath = fullPath;
            Error = null;
            BrowserSession = new LibraryBrowserSession();
            _preferences.WriteString("active_library_path", fullPath);
            _recentLibraries.RecordOpened(replacement.Descriptor);
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
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
            Workspace = replacement;
            ActivePath = fullPath;
            Error = null;
            BrowserSession = new LibraryBrowserSession();
            _preferences.WriteString("active_library_path", fullPath);
            _recentLibraries.RecordOpened(replacement.Descriptor);
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
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
            Workspace = replacement;
            ActivePath = fullPath;
            Error = null;
            BrowserSession = new LibraryBrowserSession();
            _preferences.WriteString("active_library_path", fullPath);
            _recentLibraries.RecordOpened(replacement.Descriptor);
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
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
