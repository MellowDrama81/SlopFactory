namespace Mellow.SlopFactory.Infrastructure.Storage;

internal static class MediaTypeDetector
{
    public static async Task<(string MediaType, string Extension)> DetectAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[32];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.AsSpan(0, read);

        if (read >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ("image/png", ".png");
        if (read >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF })) return ("image/jpeg", ".jpg");
        if (read >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return ("image/webp", ".webp");
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return ("image/gif", ".gif");
        if (bytes.StartsWith("%PDF-"u8)) return ("application/pdf", ".pdf");
        if (read >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WAVE"u8)) return ("audio/wav", ".wav");
        if (bytes.StartsWith("ID3"u8) || (read >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)) return ("audio/mpeg", ".mp3");
        if (read >= 12 && bytes[4..8].SequenceEqual("ftyp"u8))
        {
            return ("video/mp4", ".mp4");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".txt" => ("text/plain", ".txt"),
            ".md" or ".markdown" => ("text/markdown", ".md"),
            ".json" => ("application/json", ".json"),
            ".xml" => ("application/xml", ".xml"),
            ".csv" => ("text/csv", ".csv"),
            ".svg" => ("image/svg+xml", ".svg"),
            ".cs" => ("text/x-csharp", ".cs"),
            ".js" => ("text/javascript", ".js"),
            ".ts" => ("text/typescript", ".ts"),
            ".py" => ("text/x-python", ".py"),
            ".java" => ("text/x-java-source", ".java"),
            ".c" => ("text/x-c", ".c"),
            ".cc" or ".cpp" or ".cxx" => ("text/x-c++", ".cpp"),
            ".h" or ".hpp" => ("text/x-c-header", ".h"),
            ".css" => ("text/css", ".css"),
            ".html" or ".htm" => ("text/html", ".html"),
            ".yaml" or ".yml" => ("text/yaml", ".yaml"),
            ".toml" => ("text/toml", ".toml"),
            ".m4a" => ("audio/mp4", ".m4a"),
            ".aac" => ("audio/aac", ".aac"),
            _ => ("application/octet-stream", ".bin")
        };
    }
}
