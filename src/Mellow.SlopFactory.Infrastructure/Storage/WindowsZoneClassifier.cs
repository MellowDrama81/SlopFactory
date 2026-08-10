using System.Text;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Storage;

internal static class WindowsZoneClassifier
{
    private const int MaximumBytes = 16 * 1024;

    public static SourceZoneClassification Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return SourceZoneClassification.Unknown;
        try
        {
            using var stream = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > MaximumBytes) return SourceZoneClassification.Unknown;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var text = Encoding.UTF8.GetString(bytes);
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Trim().StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(line.AsSpan(line.IndexOf('=') + 1).Trim(), out var zone)) return SourceZoneClassification.Unknown;
                return zone switch
                {
                    0 => SourceZoneClassification.LocalMachine,
                    1 => SourceZoneClassification.Intranet,
                    2 => SourceZoneClassification.Trusted,
                    3 => SourceZoneClassification.Internet,
                    4 => SourceZoneClassification.Restricted,
                    _ => SourceZoneClassification.Unknown
                };
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) { }
        return SourceZoneClassification.Unknown;
    }
}
