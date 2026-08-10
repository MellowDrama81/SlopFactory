namespace Mellow.SlopFactory.Gui.Services;

public interface ILibraryAvailabilityProbe
{
    bool IsAvailable(string path, string? expectedVolumeIdentity, out string failureStage);
}

public sealed class LibraryAvailabilityProbe : ILibraryAvailabilityProbe
{
    public bool IsAvailable(string path, string? expectedVolumeIdentity, out string failureStage)
    {
        failureStage = "availability";
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath)) { failureStage = "root-missing"; return false; }
            if (expectedVolumeIdentity is not null && !string.Equals(expectedVolumeIdentity, LibraryVolumeIdentity.ForPath(fullPath), StringComparison.Ordinal)) { failureStage = "volume-mismatch"; return false; }
            if (!File.Exists(Path.Combine(fullPath, "slopfactory-library.json"))) { failureStage = "manifest-missing"; return false; }
            if (!File.Exists(Path.Combine(fullPath, "library.sqlite3"))) { failureStage = "database-missing"; return false; }
            var staging = Path.Combine(fullPath, ".staging");
            if (!Directory.Exists(staging)) { failureStage = "staging-missing"; return false; }
            var first = Path.Combine(staging, $"availability-{Guid.NewGuid():N}.tmp");
            var second = first + ".moved";
            try
            {
                using (var stream = new FileStream(first, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough)) { stream.WriteByte(1); stream.Flush(true); }
                File.Move(first, second, false);
                File.Delete(second);
            }
            finally
            {
                try { if (File.Exists(first)) File.Delete(first); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                try { if (File.Exists(second)) File.Delete(second); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            failureStage = exception is UnauthorizedAccessException ? "not-writable" : "filesystem-unavailable";
            return false;
        }
    }
}
