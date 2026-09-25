using System.Text.Json;
using SlopFactory.Models;

namespace SlopFactory.Services;

public sealed class PagesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public PagesDocument Load(string projectFolder)
    {
        var path = DocumentPath(projectFolder);
        if (!File.Exists(path)) return new PagesDocument();
        return JsonSerializer.Deserialize<PagesDocument>(File.ReadAllText(path), JsonOptions) ?? new PagesDocument();
    }

    public void Save(string projectFolder, PagesDocument document)
    {
        var path = DocumentPath(projectFolder);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string DocumentPath(string projectFolder)
    {
        var project = Path.GetFullPath(projectFolder);
        if (!Directory.Exists(project) || !File.Exists(Path.Combine(project, "slop.json")))
            throw new InvalidOperationException("Choose a valid SlopFactory project.");
        return Path.Combine(project, "pages.json");
    }
}
