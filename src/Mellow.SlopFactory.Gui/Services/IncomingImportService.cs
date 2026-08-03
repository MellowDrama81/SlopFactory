namespace Mellow.SlopFactory.Gui.Services;

public sealed record IncomingImportItem(string Path, string DisplayName, long? ByteSize, bool IsTemporary);
public sealed record IncomingImportFailure(string DisplayName, string Message);

public sealed class IncomingImportService
{
    private readonly object _gate = new();
    private readonly List<IncomingImportItem> _pending = [];
    private readonly List<IncomingImportFailure> _failures = [];
    private readonly string _stagingRoot = Path.Combine(FileSystem.CacheDirectory, "SlopFactory", "IncomingImports");

    public IncomingImportService()
    {
        try
        {
            if (Directory.Exists(_stagingRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(_stagingRoot)) TryDeleteDirectory(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public event EventHandler? PendingChanged;
    public int PendingCount { get { lock (_gate) return _pending.Count + _failures.Count; } }

    public IReadOnlyList<IncomingImportItem> TakePending()
    {
        IncomingImportItem[] result;
        lock (_gate)
        {
            result = _pending.ToArray();
            _pending.Clear();
        }
        if (result.Length > 0) PendingChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public IReadOnlyList<IncomingImportFailure> TakeFailures()
    {
        IncomingImportFailure[] result;
        lock (_gate)
        {
            result = _failures.ToArray();
            _failures.Clear();
        }
        if (result.Length > 0) PendingChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void QueueFailure(string displayName, string message)
    {
        lock (_gate) _failures.Add(new IncomingImportFailure(SafeFileName(displayName), message));
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void QueueLocalPaths(IEnumerable<string> paths)
    {
        var additions = new List<IncomingImportItem>();
        foreach (var path in paths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                additions.Add(new IncomingImportItem(info.FullName, info.Name, info.Length, false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        if (additions.Count == 0) return;
        lock (_gate) _pending.AddRange(additions);
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReturnPending(IEnumerable<IncomingImportItem> items)
    {
        var additions = items.ToArray();
        if (additions.Length == 0) return;
        lock (_gate)
        {
            foreach (var item in additions)
            {
                if (_pending.All(existing => !string.Equals(existing.Path, item.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) _pending.Add(item);
            }
        }
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StageAndQueueAsync(Stream source, string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var safeName = SafeFileName(displayName);
        var directory = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, safeName);
        try
        {
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var item = new IncomingImportItem(path, safeName, new FileInfo(path).Length, true);
            lock (_gate) _pending.Add(item);
            PendingChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public Task DiscardAsync(IncomingImportItem item)
    {
        if (!item.IsTemporary) return Task.CompletedTask;
        var fullPath = Path.GetFullPath(item.Path);
        var relative = Path.GetRelativePath(_stagingRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) return Task.CompletedTask;
        TryDeleteDirectory(Path.GetDirectoryName(fullPath)!);
        return Task.CompletedTask;
    }

    private static string SafeFileName(string displayName)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "shared-file.bin" : Path.GetFileName(displayName.Trim());
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        return string.IsNullOrWhiteSpace(name) ? "shared-file.bin" : name;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
