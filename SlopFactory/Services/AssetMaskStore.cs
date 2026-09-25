using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopFactory.Services;

public sealed record AssetMask(string Id, string Name, string File);

public sealed class AssetMaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int MaxMaskBytes = 32 * 1024 * 1024;

    public event Action<string, string>? Changed;

    public IReadOnlyList<AssetMask> GetMasks(string projectFolder, string assetPath)
    {
        var metadata = ReadOrCreateMetadata(projectFolder, assetPath, out _);
        return ReadMasks(metadata);
    }

    public string GetMaskImage(string projectFolder, string assetPath, string maskId)
    {
        var mask = GetMasks(projectFolder, assetPath).FirstOrDefault(item => item.Id == maskId)
            ?? throw new FileNotFoundException("The selected mask is no longer linked to this asset.");
        var filePath = ResolveMaskPath(projectFolder, mask);
        if (!File.Exists(filePath)) throw new FileNotFoundException("The selected mask file is missing.");
        return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(filePath));
    }

    public string GetMaskPath(string projectFolder, string assetPath, string maskId)
    {
        var mask = GetMasks(projectFolder, assetPath).FirstOrDefault(item => item.Id == maskId)
            ?? throw new FileNotFoundException("The selected mask is no longer linked to this asset.");
        var path = ResolveMaskPath(projectFolder, mask);
        if (!File.Exists(path)) throw new FileNotFoundException("The selected mask file is missing.");
        return path;
    }

    public async Task<AssetMask> SaveAsync(string projectFolder, string assetPath, string? maskId, string name, string pngDataUrl)
    {
        name = name.Trim();
        if (name.Length is 0 or > 64) throw new InvalidOperationException("Enter a mask name of 1–64 characters.");
        var metadata = ReadOrCreateMetadata(projectFolder, assetPath, out var metadataPath);
        var masks = ReadMasks(metadata).ToList();
        var existing = string.IsNullOrWhiteSpace(maskId) ? null : masks.FirstOrDefault(item => item.Id == maskId);
        if (!string.IsNullOrWhiteSpace(maskId) && existing is null)
            throw new InvalidOperationException("The mask being edited is no longer linked to this asset.");
        if (masks.Any(item => item.Id != existing?.Id && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This asset already has a mask with that name.");

        const string prefix = "data:image/png;base64,";
        if (!pngDataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The mask must be a PNG image.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(pngDataUrl[prefix.Length..]); }
        catch (FormatException) { throw new InvalidOperationException("The mask image is invalid."); }
        if (bytes.Length < 24 || bytes.Length > MaxMaskBytes ||
            !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) is 0 or > 16384 ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) is 0 or > 16384)
            throw new InvalidOperationException("The mask PNG is invalid or too large.");

        var mask = existing is null
            ? new AssetMask(Guid.NewGuid().ToString("N"), name, string.Empty)
            : existing with { Name = name };
        mask = mask with { File = $"masks/{mask.Id}.png" };
        var path = ResolveMaskPath(projectFolder, mask);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes);
            File.Move(temporaryPath, path, overwrite: true);
            if (existing is null) masks.Add(mask);
            else masks[masks.FindIndex(item => item.Id == existing.Id)] = mask;
            metadata["masks"] = JsonSerializer.SerializeToNode(masks.Select(item => new { id = item.Id, name = item.Name, file = item.File }));
            await WriteMetadataAsync(metadataPath, metadata);
            Changed?.Invoke(projectFolder, assetPath);
            return mask;
        }
        catch
        {
            if (existing is null && File.Exists(path)) File.Delete(path);
            throw;
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static JsonObject ReadOrCreateMetadata(string projectFolder, string assetPath, out string metadataPath)
    {
        var root = Path.GetFullPath(Path.Combine(projectFolder, "assets")) + Path.DirectorySeparatorChar;
        var asset = Path.GetFullPath(assetPath);
        if (!asset.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(asset))
            throw new InvalidOperationException("The image must be an asset in this project.");
        var relative = Path.GetRelativePath(projectFolder, asset).Replace('\\', '/');
        metadataPath = AssetMetadataPaths.ForAsset(projectFolder, asset, createDirectory: true);
        if (File.Exists(metadataPath))
            return JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject
                ?? throw new InvalidOperationException("The asset metadata is invalid.");
        var metadata = new JsonObject
        {
            ["asset"] = relative,
            ["name"] = Path.GetFileName(asset),
            ["tags"] = new JsonArray()
        };
        File.WriteAllText(metadataPath, metadata.ToJsonString(JsonOptions));
        return metadata;
    }

    private static List<AssetMask> ReadMasks(JsonObject metadata)
    {
        if (metadata["masks"] is not JsonArray array) return [];
        var masks = new List<AssetMask>();
        foreach (var node in array)
        {
            if (node is not JsonObject entry) continue;
            var id = entry["id"]?.GetValue<string>();
            var name = entry["name"]?.GetValue<string>();
            var file = entry["file"]?.GetValue<string>();
            if (id is null || name is null || file is null || !Guid.TryParseExact(id, "N", out _) ||
                file != $"masks/{id}.png") continue;
            masks.Add(new AssetMask(id, name, file));
        }
        return masks;
    }

    private static string ResolveMaskPath(string projectFolder, AssetMask mask)
    {
        var root = Path.GetFullPath(Path.Combine(projectFolder, "masks"));
        var path = Path.GetFullPath(Path.Combine(projectFolder, mask.File.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The mask path is outside the project masks folder.");
        return path;
    }

    private static async Task WriteMetadataAsync(string path, JsonObject metadata)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, metadata.ToJsonString(JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
