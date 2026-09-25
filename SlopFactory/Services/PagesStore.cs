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
        var document = JsonSerializer.Deserialize<PagesDocument>(File.ReadAllText(path), JsonOptions) ?? new PagesDocument();
        NormalizePlacements(document);
        return document;
    }

    public void Save(string projectFolder, PagesDocument document)
    {
        NormalizePlacements(document);
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

    private static void NormalizePlacements(PagesDocument document)
    {
        foreach (var book in document.Books)
        {
            book.PageWidthCm = ValidDimension(book.PageWidthCm, 21);
            book.PageHeightCm = ValidDimension(book.PageHeightCm, 29.7);
            foreach (var page in book.Pages)
            {
                var pageWidth = book.PageWidthCm * (page.Layout == "double" ? 2 : 1);
                foreach (var placement in page.Panels)
                {
                    placement.WidthCm = Math.Clamp(ValidDimension(placement.WidthCm, .1), .1, pageWidth);
                    placement.HeightCm = Math.Clamp(ValidDimension(placement.HeightCm, .1), .1, book.PageHeightCm);
                    placement.Xcm = Math.Clamp(ValidCoordinate(placement.Xcm), 0, pageWidth - placement.WidthCm);
                    placement.Ycm = Math.Clamp(ValidCoordinate(placement.Ycm), 0, book.PageHeightCm - placement.HeightCm);
                }
            }
        }
    }

    private static double ValidDimension(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
    private static double ValidCoordinate(double value) => double.IsFinite(value) ? value : 0;
}
