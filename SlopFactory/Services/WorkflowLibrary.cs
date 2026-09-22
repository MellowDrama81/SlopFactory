using System.Text.Json;
using SlopFactory.Models;

namespace SlopFactory.Services;

public sealed class WorkflowLibrary
{
    private const string BuiltInCatalogAsset = "Workflows/catalog.json";
    private const string CustomWorkflowsFileName = "custom-workflows.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private IReadOnlyList<WorkflowDefinition> builtIn = [];
    private List<WorkflowDefinition> custom = [];

    private string CustomWorkflowsPath => Path.Combine(FileSystem.AppDataDirectory, CustomWorkflowsFileName);

    public async Task LoadAsync()
    {
        builtIn = await LoadBuiltInAsync();
        custom = await LoadCustomAsync();
    }

    public IReadOnlyList<WorkflowDefinition> GetAll() =>
        builtIn.Concat(custom).OrderBy(workflow => workflow.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<WorkflowDefinition> SaveCustomAsync(string id, string displayName, string description, bool requiresMask, int minImages, int maxImages)
    {
        if (builtIn.Any(workflow => workflow.Id == id))
            throw new InvalidOperationException($"“{id}” is a built-in workflow and cannot be overwritten.");

        var definition = new WorkflowDefinition(id, displayName, description, string.Empty,
            new WorkflowCapabilities(requiresMask, minImages, maxImages), IsBuiltIn: false);

        custom = custom.Where(workflow => workflow.Id != id).Append(definition).ToList();
        await PersistCustomAsync();
        return definition;
    }

    public async Task DeleteCustomAsync(string id)
    {
        if (builtIn.Any(workflow => workflow.Id == id))
            throw new InvalidOperationException($"“{id}” is a built-in workflow and cannot be removed.");

        custom = custom.Where(workflow => workflow.Id != id).ToList();
        await PersistCustomAsync();
    }

    private static async Task<IReadOnlyList<WorkflowDefinition>> LoadBuiltInAsync()
    {
        try
        {
            await using var stream = await OpenBuiltInCatalogAsync();
            if (stream is null) return [];
            var entries = await JsonSerializer.DeserializeAsync<List<WorkflowDefinition>>(stream, JsonOptions) ?? [];
            return entries.Select(entry => entry with { IsBuiltIn = true }).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static async Task<Stream?> OpenBuiltInCatalogAsync()
    {
        try
        {
            // Works on Android/iOS/MacCatalyst and packaged (MSIX) Windows builds.
            return await FileSystem.OpenAppPackageFileAsync(BuiltInCatalogAsset);
        }
        catch (Exception)
        {
            // Unpackaged Windows builds (WindowsPackageType=None) have no package identity,
            // so fall back to the asset copied next to the executable.
            var fallbackPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Raw",
                BuiltInCatalogAsset.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fallbackPath) ? File.OpenRead(fallbackPath) : null;
        }
    }

    private async Task<List<WorkflowDefinition>> LoadCustomAsync()
    {
        try
        {
            if (!File.Exists(CustomWorkflowsPath)) return [];
            await using var stream = File.OpenRead(CustomWorkflowsPath);
            var entries = await JsonSerializer.DeserializeAsync<List<WorkflowDefinition>>(stream, JsonOptions) ?? [];
            return entries.Select(entry => entry with { IsBuiltIn = false }).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task PersistCustomAsync()
    {
        await using var stream = File.Create(CustomWorkflowsPath);
        await JsonSerializer.SerializeAsync(stream, custom, JsonOptions);
    }
}
