using Mellow.SlopFactory.Domain;
using System.Text.Json;

namespace Mellow.SlopFactory.Gui.Services;

public enum RememberedLibraryState { Available, Unavailable, Corrupt }

public sealed record RecentLibrary(
    string LibraryId,
    string DisplayName,
    string Path,
    DateTimeOffset LastOpenedAt,
    string? VolumeIdentity = null,
    RememberedLibraryState State = RememberedLibraryState.Available,
    string? FailureStage = null,
    string? DiagnosticId = null);

public interface IRecentLibraryService
{
    IReadOnlyList<RecentLibrary> GetAll();
    void RecordOpened(LibraryDescriptor descriptor);
    void Forget(string libraryId, string path);
    void RecordFailure(string path, string displayName, string? libraryId, RememberedLibraryState state, string failureStage, string diagnosticId);
    void ValidateNoOverlap(string candidatePath);
}

public sealed class RecentLibraryService : IRecentLibraryService
{
    private const string PreferenceKey = "recent_libraries_v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    public IReadOnlyList<RecentLibrary> GetAll()
    {
        lock (_gate) return Read().OrderByDescending(item => item.LastOpenedAt).ToArray();
    }

    public void RecordOpened(LibraryDescriptor descriptor)
    {
        lock (_gate)
        {
            var path = Path.GetFullPath(descriptor.RootPath);
            var items = Read();
            items.RemoveAll(item => SamePath(item.Path, path) || string.Equals(item.LibraryId, descriptor.LibraryId, StringComparison.Ordinal));
            items.Add(new RecentLibrary(descriptor.LibraryId, descriptor.DisplayName, path, DateTimeOffset.UtcNow, LibraryVolumeIdentity.ForPath(path)));
            Write(items);
        }
    }

    public void Forget(string libraryId, string path)
    {
        lock (_gate)
        {
            var items = Read();
            items.RemoveAll(item => string.Equals(item.LibraryId, libraryId, StringComparison.Ordinal) && SamePath(item.Path, path));
            Write(items);
        }
    }

    public void RecordFailure(string path, string displayName, string? libraryId, RememberedLibraryState state, string failureStage, string diagnosticId)
    {
        if (state == RememberedLibraryState.Available) throw new ArgumentOutOfRangeException(nameof(state));
        lock (_gate)
        {
            var fullPath = Path.GetFullPath(path);
            var items = Read();
            var existing = items.FirstOrDefault(item => SamePath(item.Path, fullPath));
            items.RemoveAll(item => SamePath(item.Path, fullPath));
            items.Add(new RecentLibrary(
                libraryId ?? existing?.LibraryId ?? Guid.NewGuid().ToString("N"),
                string.IsNullOrWhiteSpace(displayName) ? existing?.DisplayName ?? "Unavailable library" : displayName,
                fullPath,
                existing?.LastOpenedAt ?? DateTimeOffset.UtcNow,
                existing?.VolumeIdentity ?? LibraryVolumeIdentity.ForPath(fullPath),
                state,
                failureStage,
                diagnosticId));
            Write(items);
        }
    }

    public void ValidateNoOverlap(string candidatePath)
    {
        var candidate = Path.GetFullPath(candidatePath);
        lock (_gate)
        {
            foreach (var item in Read())
            {
                var known = Path.GetFullPath(item.Path);
                if (SamePath(candidate, known)) continue;
                if (IsAncestor(candidate, known) || IsAncestor(known, candidate))
                {
                    throw new LibraryValidationException("A library cannot be created inside another known library or contain another known library.");
                }
            }
        }
        var parent = Directory.GetParent(candidate);
        while (parent is not null)
        {
            if (File.Exists(Path.Combine(parent.FullName, "slopfactory-library.json")))
            {
                throw new LibraryValidationException("A library cannot be nested inside another SlopFactory library.");
            }
            parent = parent.Parent;
        }
    }

    private static bool IsAncestor(string ancestor, string descendant)
    {
        var relative = Path.GetRelativePath(ancestor, descendant);
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static List<RecentLibrary> Read()
    {
        var json = Preferences.Default.Get(PreferenceKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<RecentLibrary>>(json, JsonOptions) ?? [])
                .Where(item => Guid.TryParseExact(item.LibraryId, "N", out _) && Path.IsPathFullyQualified(item.Path) && !string.IsNullOrWhiteSpace(item.DisplayName))
                .Take(100)
                .ToList();
        }
        catch (JsonException) { return []; }
    }

    private static void Write(List<RecentLibrary> items) => Preferences.Default.Set(PreferenceKey, JsonSerializer.Serialize(items, JsonOptions));
}
