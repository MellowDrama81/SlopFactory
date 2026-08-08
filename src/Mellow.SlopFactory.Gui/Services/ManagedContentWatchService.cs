using Mellow.SlopFactory.Application;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Gui.Services;

public sealed record ManagedContentWatchNotice(string? FileId, string DisplayName, FileContentState? State, string Message);

public sealed class ManagedContentWatchService : IDisposable
{
    private readonly AppLibraryState _libraries;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, ManagedContentWatchNotice> _notices = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _libraryWatcher;
    private CancellationTokenSource? _libraryValidation;
    private string? _libraryId;
    private bool _started;

    public ManagedContentWatchService(AppLibraryState libraries) => _libraries = libraries;

    public event EventHandler? Changed;
    public IReadOnlyList<ManagedContentWatchNotice> Notices { get { lock (_gate) return _notices.Values.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray(); } }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _libraries.Changed += OnLibraryChanged;
        Restart();
    }

    public void Dismiss(string key)
    {
        lock (_gate) _notices.Remove(key);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLibraryChanged(object? sender, EventArgs args) => Restart();

    private void Restart()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _libraryWatcher?.Dispose();
            _libraryWatcher = null;
            _libraryValidation?.Cancel();
            _libraryValidation = null;
            foreach (var cancellation in _pending.Values) { cancellation.Cancel(); cancellation.Dispose(); }
            _pending.Clear();
            _notices.Clear();
            var workspace = _libraries.Workspace;
            _libraryId = workspace?.Descriptor.LibraryId;
            if (workspace is not null)
            {
                var mediaPath = Path.Combine(workspace.Descriptor.RootPath, "media");
                if (Directory.Exists(mediaPath))
                {
                    _watcher = new FileSystemWatcher(mediaPath)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Attributes,
                        EnableRaisingEvents = true
                    };
                    _watcher.Changed += OnManagedPathChanged;
                    _watcher.Created += OnManagedPathChanged;
                    _watcher.Deleted += OnManagedPathChanged;
                    _watcher.Renamed += OnManagedPathChanged;
                    _watcher.Error += OnWatcherError;
                }
                _libraryWatcher = new FileSystemWatcher(workspace.Descriptor.RootPath)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Attributes,
                    EnableRaisingEvents = true
                };
                _libraryWatcher.Changed += OnLibraryPathChanged;
                _libraryWatcher.Created += OnLibraryPathChanged;
                _libraryWatcher.Deleted += OnLibraryPathChanged;
                _libraryWatcher.Renamed += OnLibraryPathChanged;
                _libraryWatcher.Error += OnLibraryWatcherError;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLibraryPathChanged(object sender, FileSystemEventArgs args)
    {
        var name = Path.GetFileName(args.FullPath);
        if (!string.Equals(name, "slopfactory-library.json", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && !string.Equals(name, "library.sqlite3", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
        CancellationTokenSource cancellation;
        string? expectedLibraryId;
        lock (_gate)
        {
            _libraryValidation?.Cancel();
            cancellation = new CancellationTokenSource();
            _libraryValidation = cancellation;
            expectedLibraryId = _libraryId;
        }
        _ = ValidateLibraryChangeAsync(expectedLibraryId, cancellation);
    }

    private async Task ValidateLibraryChangeAsync(string? expectedLibraryId, CancellationTokenSource cancellation)
    {
        try
        {
            var workspace = _libraries.Workspace;
            if (workspace is null || expectedLibraryId is null || workspace.Descriptor.LibraryId != expectedLibraryId) return;
            try
            {
                await workspace.ValidateOpenLibraryAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException)
            {
                await _libraries.CloseInvalidLibraryAsync(workspace, "The active library changed outside SlopFactory and was closed to protect its consistency. Review its location before reopening it.").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_libraryValidation, cancellation)) _libraryValidation = null;
            }
            cancellation.Dispose();
        }
    }

    private void OnManagedPathChanged(object sender, FileSystemEventArgs args)
    {
        var managedName = Path.GetFileName(args.FullPath);
        if (string.IsNullOrWhiteSpace(managedName)) return;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_pending.Remove(managedName, out var previous)) { previous.Cancel(); previous.Dispose(); }
            cancellation = new CancellationTokenSource();
            _pending[managedName] = cancellation;
        }
        _ = RevalidateAfterDebounceAsync(managedName, _libraryId, cancellation);
    }

    private async Task RevalidateAfterDebounceAsync(string managedName, string? expectedLibraryId, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(750, cancellation.Token).ConfigureAwait(false);
            var workspace = _libraries.Workspace;
            if (workspace is null || expectedLibraryId is null || workspace.Descriptor.LibraryId != expectedLibraryId) return;
            var file = (await workspace.GetActiveFilesAsync(cancellation.Token).ConfigureAwait(false)).FirstOrDefault(item => string.Equals(item.ManagedName, managedName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (file is null) return;
            var health = await workspace.RevalidateFileContentAsync(file.Id, cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (health.File.ContentState is FileContentState.Healthy or FileContentState.Replaced) _notices.Remove(file.Id);
                else _notices[file.Id] = new ManagedContentWatchNotice(file.Id, file.DisplayName, health.File.ContentState,
                    health.File.ContentState == FileContentState.Missing ? "Managed content is missing." : "Managed content changed outside SlopFactory.");
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SlopFactoryException)
        {
            lock (_gate) _notices[$"watch:{managedName}"] = new ManagedContentWatchNotice(null, "Managed storage", null, "A managed-file change could not be revalidated. Run a full integrity scan.");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(managedName, out var current) && ReferenceEquals(current, cancellation)) _pending.Remove(managedName);
            }
            cancellation.Dispose();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        lock (_gate) _notices["watcher"] = new ManagedContentWatchNotice(null, "Managed storage", null, "Some filesystem changes may have been missed. Run a full integrity scan.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLibraryWatcherError(object sender, ErrorEventArgs args)
    {
        ILibraryWorkspace? workspace;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _libraryWatcher)) return;
            workspace = _libraries.Workspace;
        }
        if (workspace is not null) _ = _libraries.CloseInvalidLibraryAsync(workspace, "SlopFactory could no longer monitor the active library's manifest and database, so it was closed as a precaution.");
    }

    public void Dispose()
    {
        if (_started) _libraries.Changed -= OnLibraryChanged;
        lock (_gate)
        {
            _watcher?.Dispose();
            _libraryWatcher?.Dispose();
            _libraryValidation?.Cancel();
            foreach (var cancellation in _pending.Values) { cancellation.Cancel(); cancellation.Dispose(); }
            _pending.Clear();
        }
    }
}
