using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Gui.Services;

public sealed class AppLibraryState : IAsyncDisposable
{
    private readonly ILibraryWorkspaceFactory _factory;
    private readonly ILibraryLocationService _locations;
    private readonly IRecentLibraryService _recentLibraries;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppLibraryState(ILibraryWorkspaceFactory factory, ILibraryLocationService locations, IRecentLibraryService recentLibraries)
    {
        _factory = factory;
        _locations = locations;
        _recentLibraries = recentLibraries;
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
            var path = Preferences.Default.Get("active_library_path", _locations.DefaultPath);
            try
            {
                if (!_locations.IsAllowedPath(path)) throw new LibraryValidationException("The saved library location is not an available application storage location.");
                Workspace = File.Exists(Path.Combine(path, "slopfactory-library.json"))
                    ? await _factory.OpenAsync(path).ConfigureAwait(false)
                    : await _factory.CreateAsync(path).ConfigureAwait(false);
                ActivePath = Path.GetFullPath(path);
                _recentLibraries.RecordOpened(Workspace.Descriptor);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException)
            {
                Error = exception.Message;
            }
            IsInitialized = true;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SwitchAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_locations.IsAllowedPath(path)) throw new LibraryValidationException("That location is not an allowed application library location.");
            var fullPath = Path.GetFullPath(path);
            if (Workspace is not null && string.Equals(fullPath, ActivePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
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
            Preferences.Default.Set("active_library_path", fullPath);
            _recentLibraries.RecordOpened(replacement.Descriptor);
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
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
            Preferences.Default.Set("active_library_path", fullPath);
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

    public async ValueTask DisposeAsync()
    {
        if (Workspace is not null) await Workspace.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
