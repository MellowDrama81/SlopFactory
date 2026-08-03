using System.Security.Cryptography;

namespace Mellow.SlopFactory.Infrastructure.Storage;

internal static class Hashing
{
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken, Action<long>? reportBytes = null)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1_048_576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1_048_576];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            incrementalHash.AppendData(buffer, 0, read);
            total += read;
            reportBytes?.Invoke(total);
        }
        return Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
    }

    public static async Task<(string Hash, long Bytes)> CopyAndHashAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken, Action<long>? reportBytes = null)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1_048_576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1_048_576, FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1_048_576];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            incrementalHash.AppendData(buffer, 0, read);
            total += read;
            reportBytes?.Invoke(total);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (Convert.ToHexStringLower(incrementalHash.GetHashAndReset()), total);
    }
}
