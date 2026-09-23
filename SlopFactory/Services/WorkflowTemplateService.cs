using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SlopFactory.Models;

namespace SlopFactory.Services;

public sealed partial class WorkflowTemplateService
{
    [GeneratedRegex(@"\{\{(?<name>[A-Z][A-Z0-9_]*):(?<type>[a-z]+)\}\}(?<annotation> \[(?:input|output|temp)\])?")]
    private static partial Regex PlaceholderPattern();

    public async Task<IReadOnlyList<WorkflowPlaceholder>> GetPlaceholdersAsync(WorkflowDefinition workflow)
    {
        var template = await ReadGraphAsync(workflow);
        return PlaceholderPattern().Matches(template)
            .Select(match => new WorkflowPlaceholder(match.Groups["name"].Value, match.Groups["type"].Value))
            .DistinctBy(placeholder => placeholder.Name)
            .ToList();
    }

    public async Task<PreparedWorkflow> PrepareAsync(WorkflowDefinition workflow, IReadOnlyDictionary<string, string> values)
    {
        var template = await ReadGraphAsync(workflow);
        var seeds = new Dictionary<string, long>(StringComparer.Ordinal);
        var populated = PlaceholderPattern().Replace(template, match =>
        {
            var placeholder = new WorkflowPlaceholder(match.Groups["name"].Value, match.Groups["type"].Value);
            if (placeholder.IsSeed)
            {
                if (!seeds.TryGetValue(placeholder.Name, out var seed))
                    seeds[placeholder.Name] = seed = RandomNumberGenerator.GetInt32(int.MaxValue);
                return seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (!values.TryGetValue(placeholder.Name, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Choose or enter {placeholder.Label.ToLowerInvariant()}.");
            // Non-seed placeholders appear inside JSON strings. Escape the value without adding quotes.
            var annotation = match.Groups["annotation"].Value;
            var finalValue = placeholder.Type == "image" &&
                (value.EndsWith(" [input]", StringComparison.Ordinal) ||
                 value.EndsWith(" [output]", StringComparison.Ordinal) ||
                 value.EndsWith(" [temp]", StringComparison.Ordinal))
                ? value : value + annotation;
            var escaped = JsonSerializer.Serialize(finalValue);
            return escaped[1..^1];
        });
        using var document = JsonDocument.Parse(populated);
        return new PreparedWorkflow(populated, seeds);
    }

    private static async Task<string> ReadGraphAsync(WorkflowDefinition workflow)
    {
        var fileName = workflow.GraphFile;
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
            !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This workflow does not have a usable graph file.");

        var assetPath = $"Workflows/{fileName}";
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync(assetPath);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch (Exception)
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(WorkflowTemplateService).Assembly.Location) ?? AppContext.BaseDirectory;
            var fallback = Path.Combine(assemblyDirectory, "Workflows", fileName);
            if (!File.Exists(fallback))
                fallback = Path.Combine(assemblyDirectory, "Resources", "Raw", "Workflows", fileName);
            if (!File.Exists(fallback)) throw new FileNotFoundException($"Workflow graph {fileName} is not bundled with the app.");
            return await File.ReadAllTextAsync(fallback);
        }
    }
}

public sealed record PreparedWorkflow(string GraphJson, IReadOnlyDictionary<string, long> Seeds);
