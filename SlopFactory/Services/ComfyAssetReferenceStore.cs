using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopFactory.Services;

public sealed record ComfyAssetReference(string Filename, string Subfolder, string Type, string Sha256, string ServerUrl)
{
    public string WorkflowFilename
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Subfolder) ? Filename : Subfolder.TrimEnd('/', '\\') + "/" + Filename;
            return Type.Equals("input", StringComparison.OrdinalIgnoreCase) ? name : $"{name} [{Type}]";
        }
    }
}

public sealed class ComfyAssetReferenceStore
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ComfyAssetReference?> GetValidAsync(string projectFolder, string assetPath,
        string connectionId, string serverUrl)
    {
        var metadataPath = GetMetadataPath(projectFolder, assetPath);
        var contentHash = await HashFileAsync(assetPath);
        var gate = locks.GetOrAdd(metadataPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var metadata = await ReadOrCreateAsync(metadataPath, projectFolder, assetPath);
            var references = metadata["comfyReferences"] as JsonObject;
            if (references?[connectionId] is not JsonObject entry) return null;
            var reference = new ComfyAssetReference(
                entry["filename"]?.GetValue<string>() ?? "",
                entry["subfolder"]?.GetValue<string>() ?? "",
                entry["type"]?.GetValue<string>() ?? "input",
                entry["sha256"]?.GetValue<string>() ?? "",
                entry["serverUrl"]?.GetValue<string>() ?? "");
            return reference.Filename.Length > 0 &&
                reference.Sha256.Equals(contentHash, StringComparison.OrdinalIgnoreCase) &&
                ReferenceUrl(reference.ServerUrl) == ReferenceUrl(serverUrl)
                ? reference : null;
        }
        finally { gate.Release(); }
    }

    public async Task SetAsync(string projectFolder, string assetPath, string connectionId,
        string serverUrl, string filename, string subfolder, string type, IReadOnlyList<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(filename))
            throw new ArgumentException("A Comfy connection ID and file reference are required.");
        var metadataPath = GetMetadataPath(projectFolder, assetPath);
        var contentHash = await HashFileAsync(assetPath);
        var gate = locks.GetOrAdd(metadataPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var metadata = await ReadOrCreateAsync(metadataPath, projectFolder, assetPath);
            if (tags is not null) metadata["tags"] = JsonSerializer.SerializeToNode(tags);
            var references = metadata["comfyReferences"] as JsonObject;
            if (references is null) metadata["comfyReferences"] = references = new JsonObject();
            references[connectionId] = new JsonObject
            {
                ["filename"] = filename,
                ["subfolder"] = subfolder,
                ["type"] = type,
                ["sha256"] = contentHash,
                ["serverUrl"] = ReferenceUrl(serverUrl)
            };
            var temporaryPath = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, metadataPath, true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        finally { gate.Release(); }
    }

    private static async Task<JsonObject> ReadOrCreateAsync(string metadataPath, string projectFolder, string assetPath)
    {
        if (File.Exists(metadataPath))
            return JsonNode.Parse(await File.ReadAllTextAsync(metadataPath)) as JsonObject
                ?? throw new InvalidOperationException($"Invalid metadata for {Path.GetFileName(assetPath)}.");
        var relative = Path.GetRelativePath(projectFolder, assetPath).Replace('\\', '/');
        return new JsonObject
        {
            ["asset"] = relative,
            ["name"] = Path.GetFileName(assetPath),
            ["tags"] = new JsonArray()
        };
    }

    private static string GetMetadataPath(string projectFolder, string assetPath)
    {
        var root = Path.GetFullPath(Path.Combine(projectFolder, "assets")) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(assetPath);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new InvalidOperationException("The asset must be a file inside the project's assets folder.");
        return AssetMetadataPaths.ForAsset(projectFolder, fullPath, createDirectory: true);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string ReferenceUrl(string url) => url.Trim().TrimEnd('/');
}
