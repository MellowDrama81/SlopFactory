using System.Text.Json;
using SlopFactory.Models;

namespace SlopFactory.Services;

public sealed record WorkflowValidationResult(string WorkflowId, bool Prepared, string? Error, IReadOnlyList<string> ReferenceInputs);

public sealed class WorkflowValidationHarness(WorkflowLibrary library, WorkflowTemplateService templates)
{
    public async Task<IReadOnlyList<WorkflowValidationResult>> PreflightAsync(string reportPath)
    {
        await library.LoadAsync();
        var results = new List<WorkflowValidationResult>();
        foreach (var workflow in library.GetAll())
        {
            try
            {
                var placeholders = await templates.GetPlaceholdersAsync(workflow);
                var references = placeholders.Where(item => item.Type == "image" && !item.IsMaskedImage).Select(item => item.Name).ToList();
                var values = placeholders.Where(item => !item.IsSeed).ToDictionary(item => item.Name, item => item.Type == "image" ? "validation-reference.png" : "validation", StringComparer.Ordinal);
                await templates.PrepareAsync(workflow, values);
                results.Add(new(workflow.Id, true, null, references));
            }
            catch (Exception ex) { results.Add(new(workflow.Id, false, ex.Message, [])); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return results;
    }
}
