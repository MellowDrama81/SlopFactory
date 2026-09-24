using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SlopFactory.Models;

namespace SlopFactory.Services;

public sealed class PanelStore
{
    private static readonly object FolderMigrationLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public PanelDocument CreateNew() => new();

    public IReadOnlyList<PanelSummary> GetAll(string projectFolder)
    {
        var folder = PanelFolder(projectFolder);
        if (!Directory.Exists(folder)) return [];
        var panels = new List<PanelSummary>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                var panel = JsonSerializer.Deserialize<PanelDocument>(File.ReadAllText(file), JsonOptions);
                if (panel is not null && panel.Id == id)
                    panels.Add(new PanelSummary(id, panel.Name, panel.Width, panel.Height));
            }
            catch (JsonException) { /* An invalid file should not hide the other panels. */ }
        }
        return panels.OrderBy(panel => panel.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public PanelDocument Load(string projectFolder, string panelId)
    {
        var path = PanelPath(projectFolder, panelId);
        if (!File.Exists(path)) throw new FileNotFoundException("The selected Panel no longer exists.");
        var panel = JsonSerializer.Deserialize<PanelDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("The Panel JSON is invalid.");
        if (panel.Id != panelId) throw new InvalidOperationException("The Panel ID does not match its file name.");
        Validate(projectFolder, panel, requireUsableSources: false);
        return panel;
    }

    public async Task SaveAsync(string projectFolder, PanelDocument panel)
    {
        Validate(projectFolder, panel, requireUsableSources: true);
        panel.Name = panel.Name.Trim();
        foreach (var layer in panel.Layers) layer.Name = layer.Name.Trim();
        var path = PanelPath(projectFolder, panel.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(panel, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IReadOnlyList<string> GetRasterAssets(string projectFolder)
    {
        var root = AssetsFolder(projectFolder);
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(IsImage)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string RasterDataUrl(string projectFolder, string asset)
    {
        var path = ResolveAsset(projectFolder, asset);
        if (!File.Exists(path)) throw new FileNotFoundException("The raster asset is missing.");
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => throw new InvalidOperationException("Unsupported raster image format.")
        };
        return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
    }

    public static string VectorDataUrl(string svg)
        => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

    private static string PanelFolder(string projectFolder)
    {
        var project = Path.GetFullPath(projectFolder);
        if (!Directory.Exists(project) || !File.Exists(Path.Combine(project, "slop.json")))
            throw new InvalidOperationException("Choose a valid SlopFactory project.");
        var folder = Path.Combine(project, "panels");
        lock (FolderMigrationLock)
        {
            var directories = Directory.EnumerateDirectories(project, "*", SearchOption.TopDirectoryOnly).ToList();
            var oldFolder = directories.FirstOrDefault(path => Path.GetFileName(path) == "Panels");
            var newFolder = directories.FirstOrDefault(path => Path.GetFileName(path) == "panels");
            if (oldFolder is not null && newFolder is null)
            {
                // A case-only rename needs an intermediate name on Windows.
                var temporary = Path.Combine(project, ".panels-rename-" + Guid.NewGuid().ToString("N"));
                Directory.Move(oldFolder, temporary);
                try { Directory.Move(temporary, folder); }
                catch
                {
                    Directory.Move(temporary, oldFolder);
                    throw;
                }
            }
        }
        return folder;
    }

    private static string PanelPath(string projectFolder, string panelId)
    {
        if (!Guid.TryParseExact(panelId, "N", out _)) throw new InvalidOperationException("The Panel ID is invalid.");
        return Path.Combine(PanelFolder(projectFolder), panelId + ".json");
    }

    private static string AssetsFolder(string projectFolder) => Path.Combine(Path.GetFullPath(projectFolder), "assets");

    private static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp";

    private static string ResolveAsset(string projectFolder, string asset)
    {
        if (string.IsNullOrWhiteSpace(asset) || Path.IsPathRooted(asset))
            throw new InvalidOperationException("Choose an image asset for the raster layer.");
        var root = Path.GetFullPath(AssetsFolder(projectFolder));
        var path = Path.GetFullPath(Path.Combine(root, asset.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !IsImage(path))
            throw new InvalidOperationException("The raster layer must reference an image in this project's assets folder.");
        return path;
    }

    private static void Validate(string projectFolder, PanelDocument panel, bool requireUsableSources)
    {
        _ = PanelPath(projectFolder, panel.Id);
        if (string.IsNullOrWhiteSpace(panel.Name) || panel.Name.Trim().Length > 100)
            throw new InvalidOperationException("Enter a Panel name of 1–100 characters.");
        if (panel.Width is < 1 or > 8192 || panel.Height is < 1 or > 8192)
            throw new InvalidOperationException("Panel width and height must be between 1 and 8192 pixels.");
        if (panel.Layers is null || panel.Layers.Count > 100)
            throw new InvalidOperationException("A Panel can contain up to 100 layers.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in panel.Layers)
        {
            if (!Guid.TryParseExact(layer.Id, "N", out _) || !ids.Add(layer.Id) ||
                string.IsNullOrWhiteSpace(layer.Name) || layer.Name.Trim().Length > 100)
                throw new InvalidOperationException("A layer has an invalid ID or name.");
            if (layer.Matrix is not { Length: 6 } || layer.Matrix.Any(value => !double.IsFinite(value) || Math.Abs(value) > 100_000))
                throw new InvalidOperationException($"The transform for {layer.Name} is invalid.");
            if (layer.Kind == "raster")
            {
                var path = ResolveAsset(projectFolder, layer.Asset ?? string.Empty);
                if (requireUsableSources && !File.Exists(path))
                    throw new FileNotFoundException($"The asset for {layer.Name} is missing.");
            }
            else if (layer.Kind == "vector")
            {
                if (requireUsableSources) ValidateSvg(layer.Svg ?? string.Empty);
            }
            else throw new InvalidOperationException("A layer must be raster or vector.");
        }
    }

    private static void ValidateSvg(string svg)
    {
        if (svg.Length is 0 or > 262_144) throw new InvalidOperationException("SVG content must be 1–262,144 characters.");
        using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "svg") throw new InvalidOperationException("A vector layer must contain an <svg> document.");
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "script", "foreignObject", "iframe", "object", "embed", "image", "use", "style" };
        foreach (var element in document.Root.DescendantsAndSelf())
        {
            if (blocked.Contains(element.Name.LocalName)) throw new InvalidOperationException("The SVG contains an unsupported element.");
            foreach (var attribute in element.Attributes())
            {
                if ((attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) && attribute.Name.LocalName.Length > 2) ||
                    (attribute.Name.LocalName == "href" && !attribute.Value.StartsWith('#')) ||
                    (attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) &&
                     !attribute.Value.Contains("url(#", StringComparison.OrdinalIgnoreCase)) ||
                    attribute.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The SVG contains an unsupported external reference or event handler.");
            }
        }
    }
}
