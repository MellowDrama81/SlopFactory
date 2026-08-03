using System.Text.Json;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Storage;

internal static class LibraryManifestStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<LibraryManifest> ReadAsync(LibraryLayout layout, CancellationToken cancellationToken)
    {
        if (!File.Exists(layout.ManifestPath))
        {
            throw new LibraryValidationException("The selected location does not contain a SlopFactory library manifest.");
        }

        try
        {
            await using var stream = new FileStream(layout.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync<LibraryManifest>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new LibraryValidationException("The library manifest is empty.");
            Validate(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new LibraryValidationException($"The library manifest is invalid JSON: {exception.Message}");
        }
    }

    public static async Task WriteAsync(LibraryLayout layout, LibraryManifest manifest, CancellationToken cancellationToken)
    {
        Validate(manifest);
        var temporaryPath = layout.ManifestPath + ".new";
        layout.EnsureManagedPath(temporaryPath);
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, layout.ManifestPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(LibraryManifest manifest)
    {
        if (!string.Equals(manifest.FormatIdentity, LibraryRules.FormatIdentity, StringComparison.Ordinal))
        {
            throw new LibraryValidationException("The manifest does not identify a SlopFactory library.");
        }

        if (manifest.ManifestVersion != LibraryRules.ManifestVersion)
        {
            throw new LibraryValidationException($"Manifest version {manifest.ManifestVersion} is unsupported.");
        }

        if (manifest.SchemaVersion > LibraryRules.SchemaVersion)
        {
            throw new LibraryValidationException("The library was created by a newer SlopFactory version.");
        }

        if (!Guid.TryParseExact(manifest.LibraryId, "N", out _))
        {
            throw new LibraryValidationException("The library manifest contains an invalid library ID.");
        }

        _ = LibraryRules.NormalizeDisplayName(manifest.DisplayName, "Library name");
    }
}

