using System.Text.Json;
using System.Text.Json.Nodes;
using SlopFactory.Models;

namespace SlopFactory.Services;

public sealed class WorkspaceLayoutStore
{
    private const string FileName = "workspace-layout.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string FilePath => Path.Combine(FileSystem.AppDataDirectory, FileName);

    public async Task<DockNode?> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            await using var stream = File.OpenRead(FilePath);
            var saved = await JsonNode.ParseAsync(stream);
            if (saved is not JsonObject snapshot || !int.TryParse(snapshot["Version"]?.ToString(), out var version) || version is not (1 or 2))
                return null;
            if (version == 1) UpgradeLegacyPaneNames(snapshot["Root"]);
            return snapshot.Deserialize<WorkspaceSnapshot>(JsonOptions)?.Root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(DockNode root)
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new WorkspaceSnapshot(2, root), JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void UpgradeLegacyPaneNames(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            // Version 1 called dockable panes "Panels" in saved tab groups.
            if (obj.TryGetPropertyValue("Panels", out var panes))
            {
                obj.Remove("Panels");
                obj["Panes"] = panes;
            }
            foreach (var property in obj.ToList()) UpgradeLegacyPaneNames(property.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) UpgradeLegacyPaneNames(item);
        }
    }

    private sealed record WorkspaceSnapshot(int Version, DockNode Root);
}
