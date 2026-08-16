using System.Text.Json;

namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// File-based <see cref="IDiagnosticsLogger"/> — a single JSON-lines file under a device-wide
/// directory (not per-library, matching plan.md:167's "remain local" without being tied to any one
/// library). Takes a plain directory path rather than a MAUI path-provider interface (unlike
/// <see cref="IRecoveryStagingPathProvider"/>) so it's directly constructible and testable with a
/// real temporary directory, the same way <c>LibraryWorkspaceFactory</c> takes a root path.
/// </summary>
public sealed class DiagnosticsLogger : IDiagnosticsLogger
{
    private const long MaxTotalBytes = 50 * 1024 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan VerboseDuration = TimeSpan.FromHours(1);
    private const string VerboseExpiresAtPreferenceKey = "slopfactory.diagnostics.verboseexpiresat";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _logDirectory;
    private readonly IAppPreferenceStore _preferences;
    private readonly long _maxTotalBytes;
    private readonly object _gate = new();

    public DiagnosticsLogger(string logDirectory, IAppPreferenceStore preferences, long? maxTotalBytesOverride = null)
    {
        _logDirectory = logDirectory;
        _preferences = preferences;
        _maxTotalBytes = maxTotalBytesOverride ?? MaxTotalBytes;
    }

    private string LogFilePath => Path.Combine(_logDirectory, "diagnostics.log");

    public void Log(DiagnosticLogEntry entry)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_logDirectory);
            File.AppendAllText(LogFilePath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            EnforceRetention();
        }
    }

    public IReadOnlyList<DiagnosticLogEntry> ReadAll()
    {
        lock (_gate) return ReadAllUnlocked();
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { File.Delete(LogFilePath); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    public bool VerboseEnabled => VerboseExpiresAt is { } expiresAt && expiresAt > DateTimeOffset.UtcNow;

    public DateTimeOffset? VerboseExpiresAt
    {
        get
        {
            var stored = _preferences.ReadString(VerboseExpiresAtPreferenceKey, string.Empty);
            return DateTimeOffset.TryParse(stored, out var value) ? value : null;
        }
    }

    public void EnableVerbose()
    {
        // Re-activating an already-running period never extends it — plan.md:181's "without
        // extending the deadline through activity."
        if (VerboseEnabled) return;
        _preferences.WriteString(VerboseExpiresAtPreferenceKey, (DateTimeOffset.UtcNow + VerboseDuration).ToString("o"));
    }

    public void DisableVerbose() => _preferences.WriteString(VerboseExpiresAtPreferenceKey, string.Empty);

    private List<DiagnosticLogEntry> ReadAllUnlocked()
    {
        if (!File.Exists(LogFilePath)) return [];
        var entries = new List<DiagnosticLogEntry>();
        foreach (var line in File.ReadAllLines(LogFilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<DiagnosticLogEntry>(line, JsonOptions) is { } entry) entries.Add(entry);
            }
            catch (JsonException) { /* A malformed line (e.g. a partial write after a crash) is skipped rather than aborting the whole read. */ }
        }
        return entries;
    }

    private void WriteAllUnlocked(List<DiagnosticLogEntry> entries) =>
        File.WriteAllLines(LogFilePath, entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)));

    /// <summary>
    /// Applies the 30-day age limit and the 50 MB rolling cap together on every write, oldest
    /// entries removed first, per plan.md:167-170. Re-reads and (if anything changed) rewrites the
    /// whole file — a deliberately simple approach rather than a true multi-file rolling log, which
    /// is acceptable for occasional diagnostic events rather than a high-frequency logging hot path.
    /// </summary>
    private void EnforceRetention()
    {
        var entries = ReadAllUnlocked();
        var cutoff = DateTimeOffset.UtcNow - MaxAge;
        var filtered = entries.Where(entry => entry.Timestamp >= cutoff).ToList();
        var changed = filtered.Count != entries.Count;
        while (filtered.Count > 0 && EstimateSize(filtered) > _maxTotalBytes)
        {
            filtered.RemoveAt(0);
            changed = true;
        }
        if (changed) WriteAllUnlocked(filtered);
    }

    private static long EstimateSize(IReadOnlyList<DiagnosticLogEntry> entries) =>
        entries.Sum(entry => JsonSerializer.Serialize(entry, JsonOptions).Length + Environment.NewLine.Length);
}
