using System.Text.Json;
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
            var snapshot = await JsonSerializer.DeserializeAsync<WorkspaceSnapshot>(stream, JsonOptions);
            return snapshot?.Version == 1 ? snapshot.Root : null;
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
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new WorkspaceSnapshot(1, root), JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed record WorkspaceSnapshot(int Version, DockNode Root);
}
