using System.Text.Json;
using System.Text.Json.Nodes;
using SlopFactory.Models;

namespace SlopFactory.Services;

public sealed class ProjectLibrary
{
    private const string FileName = "projects.json";
    private const string ProjectSettingsFileName = "slop.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ProjectJsonOptions = new() { WriteIndented = true };
    private List<ProjectDefinition> projects = [];
    private string FilePath => Path.Combine(FileSystem.AppDataDirectory, FileName);

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            await using var stream = File.OpenRead(FilePath);
            projects = await JsonSerializer.DeserializeAsync<List<ProjectDefinition>>(stream, JsonOptions) ?? [];
        }
        catch { projects = []; }
    }

    public IReadOnlyList<ProjectDefinition> GetAll() => projects.OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<string> GetProjectTags(string folderPath)
    {
        var settingsPath = ProjectSettingsPath(folderPath);
        if (!File.Exists(settingsPath)) return [];

        var settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject
            ?? throw new InvalidOperationException("The project's slop.json file must contain a JSON object.");
        var tags = ReadTags(settings);

        // Include tags saved before the project-level catalog was introduced.
        foreach (var path in Directory.EnumerateFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (fileName.Length != 64 || !fileName.All(Uri.IsHexDigit)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject metadata)
                    tags.AddRange(ReadTags(metadata));
            }
            catch (JsonException) { /* An invalid sidecar should not hide other tags. */ }
        }

        var distinctTags = tags.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList();
        if (settings["tags"] is not JsonArray ||
            !ReadTags(settings).SequenceEqual(distinctTags, StringComparer.Ordinal))
        {
            settings["tags"] = JsonSerializer.SerializeToNode(distinctTags);
            File.WriteAllText(settingsPath, settings.ToJsonString(ProjectJsonOptions));
        }
        return distinctTags;
    }

    public void RegisterProjectTag(string folderPath, string tag)
    {
        var settingsPath = ProjectSettingsPath(folderPath);
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject
            ?? throw new InvalidOperationException("The project's slop.json file must contain a JSON object.");
        var tags = ReadTags(settings);
        if (tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;
        tags.Add(tag);
        settings["tags"] = JsonSerializer.SerializeToNode(tags.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        File.WriteAllText(settingsPath, settings.ToJsonString(ProjectJsonOptions));
    }

    private static List<string> ReadTags(JsonObject settings)
    {
        if (settings["tags"] is not JsonArray tags) return [];
        return tags.Select(tag => tag is JsonValue value && value.TryGetValue<string>(out var text) ? text?.Trim() : null)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .ToList();
    }

    public async Task<ProjectDefinition> SaveAsync(string folderPath, string name, bool createFolder)
    {
        var normalizedPath = Path.GetFullPath(folderPath.Trim());
        if (createFolder) Directory.CreateDirectory(normalizedPath);
        if (!Directory.Exists(normalizedPath)) throw new DirectoryNotFoundException("The selected project folder does not exist.");
        Directory.CreateDirectory(Path.Combine(normalizedPath, "assets"));

        var settings = await LoadProjectSettingsAsync(normalizedPath);
        if (settings is null)
        {
            settings = new ProjectSettings(name.Trim());
            await PersistProjectSettingsAsync(normalizedPath, settings);
        }
        else if (!string.IsNullOrWhiteSpace(name) && !string.Equals(settings.Name, name.Trim(), StringComparison.Ordinal))
        {
            settings = settings with { Name = name.Trim() };
            await UpdateProjectNameAsync(normalizedPath, settings.Name);
        }

        var project = new ProjectDefinition(normalizedPath, settings.Name);
        projects = projects.Where(item => !string.Equals(item.FolderPath, normalizedPath, StringComparison.OrdinalIgnoreCase)).Append(project).ToList();
        await PersistAsync();
        return project;
    }

    public async Task RemoveAsync(string folderPath)
    {
        var normalizedPath = Path.GetFullPath(folderPath.Trim());
        projects = projects.Where(item => !string.Equals(item.FolderPath, normalizedPath, StringComparison.OrdinalIgnoreCase)).ToList();
        await PersistAsync();
    }

    private static string ProjectSettingsPath(string folderPath) => Path.Combine(folderPath, ProjectSettingsFileName);

    private static async Task<ProjectSettings?> LoadProjectSettingsAsync(string folderPath)
    {
        try
        {
            var path = ProjectSettingsPath(folderPath);
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ProjectSettings>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The project's slop.json file is invalid.", ex);
        }
    }

    private static async Task PersistProjectSettingsAsync(string folderPath, ProjectSettings settings)
    {
        await using var stream = File.Create(ProjectSettingsPath(folderPath));
        await JsonSerializer.SerializeAsync(stream, settings, ProjectJsonOptions);
    }

    private static async Task UpdateProjectNameAsync(string folderPath, string name)
    {
        var path = ProjectSettingsPath(folderPath);
        JsonObject settings;
        await using (var input = File.OpenRead(path))
            settings = await JsonNode.ParseAsync(input) as JsonObject
                ?? throw new InvalidOperationException("The project's slop.json file must contain a JSON object.");
        settings["name"] = name;
        await using var output = File.Create(path);
        await JsonSerializer.SerializeAsync(output, settings, ProjectJsonOptions);
    }

    private async Task PersistAsync()
    {
        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, projects, JsonOptions);
    }

    private sealed record ProjectSettings(string Name);
}
