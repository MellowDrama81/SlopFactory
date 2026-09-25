using System.Globalization;
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

    public IReadOnlyList<PanelSummary> GetAll(string projectFolder, string? relativeFolder = null)
    {
        var folder = ResolveFolder(projectFolder, relativeFolder);
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
                {
                    Validate(projectFolder, panel, requireUsableSources: false);
                    var relativePath = Path.GetRelativePath(PanelFolder(projectFolder), file).Replace('\\', '/');
                    var thumbnailPath = Path.Combine(Path.GetDirectoryName(file)!, id + ".thumb.png");
                    string? thumbnail = null;
                    try
                    {
                        if (File.Exists(thumbnailPath))
                            thumbnail = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(thumbnailPath));
                    }
                    catch (IOException) { /* Fall back to rendering the panel preview. */ }
                    panels.Add(new PanelSummary(id, panel.Name, panel.Width, panel.Height, relativePath, panel, thumbnail));
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            { /* An invalid file should not hide the other panels. */ }
        }
        return panels.OrderBy(panel => panel.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> GetFolders(string projectFolder, string? relativeFolder)
    {
        var folder = ResolveFolder(projectFolder, relativeFolder);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateDirectories(folder).Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string NormalizeFolder(string projectFolder, string? relativeFolder)
    {
        var folder = ResolveFolder(projectFolder, relativeFolder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The Panel folder no longer exists.");
        var relative = Path.GetRelativePath(PanelFolder(projectFolder), folder).Replace('\\', '/');
        return relative == "." ? string.Empty : relative;
    }

    public string CreateFolder(string projectFolder, string? parentFolder, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100 || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new InvalidOperationException("Enter a valid folder name of 1–100 characters.");
        var parent = ResolveFolder(projectFolder, parentFolder);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The parent Panel folder no longer exists.");
        var path = Path.Combine(parent, name);
        if (Directory.Exists(path) || File.Exists(path)) throw new InvalidOperationException("A folder with that name already exists.");
        Directory.CreateDirectory(path);
        return NormalizeFolder(projectFolder, Path.GetRelativePath(PanelFolder(projectFolder), path));
    }

    public PanelDocument Load(string projectFolder, string panelId)
    {
        var path = PanelPath(projectFolder, panelId);
        if (!File.Exists(path)) throw new FileNotFoundException("The selected Panel no longer exists.");
        var panel = JsonSerializer.Deserialize<PanelDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("The Panel JSON is invalid.");
        if (panel.Id != Path.GetFileNameWithoutExtension(path)) throw new InvalidOperationException("The Panel ID does not match its file name.");
        Validate(projectFolder, panel, requireUsableSources: false);
        return panel;
    }

    public string PreviewDataUrl(string projectFolder, string panelReference)
    {
        var path = PanelPath(projectFolder, panelReference);
        var thumbnailPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".thumb.png");
        if (File.Exists(thumbnailPath))
            return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(thumbnailPath));

        var panel = Load(projectFolder, panelReference);
        var clipId = "panel-bounds-" + panel.Id;
        var innerWidth = Math.Max(0, panel.Width - 2);
        var innerHeight = Math.Max(0, panel.Height - 2);
        var svg = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {panel.Width} {panel.Height}\"><defs><clipPath id=\"{clipId}\"><rect x=\"1\" y=\"1\" width=\"{innerWidth}\" height=\"{innerHeight}\" /></clipPath></defs><rect x=\"1\" y=\"1\" width=\"{innerWidth}\" height=\"{innerHeight}\" fill=\"white\"/><g clip-path=\"url(#{clipId})\">");
        foreach (var layer in panel.Layers.Where(item => item.Visible))
        {
            string? source;
            try
            {
                source = layer.Kind == "raster" ? RasterDataUrl(projectFolder, layer.Asset ?? string.Empty) :
                    layer.Kind == "vector" && !string.IsNullOrWhiteSpace(layer.Svg) ? VectorDataUrl(layer.Svg) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                source = null;
            }
            if (source is null) continue;
            var matrix = string.Join(" ", layer.Matrix.Select(value => value.ToString("G17", CultureInfo.InvariantCulture)));
            var centeredMatrix = $"translate({panel.Width / 2} {panel.Height / 2}) matrix({matrix}) translate({-panel.Width / 2} {-panel.Height / 2})";
            var aspect = layer.Kind == "raster" ? "xMidYMid meet" : "none";
            svg.Append($"<g transform=\"{centeredMatrix}\"><image href=\"{source}\" width=\"{panel.Width}\" height=\"{panel.Height}\" preserveAspectRatio=\"{aspect}\"/></g>");
        }
        svg.Append($"</g><rect x=\"1\" y=\"1\" width=\"{innerWidth}\" height=\"{innerHeight}\" fill=\"none\" stroke=\"#202027\" stroke-width=\"2\"/></svg>");
        return VectorDataUrl(svg.ToString());
    }

    public async Task SaveAsync(string projectFolder, PanelDocument panel, string? relativeFolder = null)
    {
        Validate(projectFolder, panel, requireUsableSources: true);
        panel.Name = panel.Name.Trim();
        foreach (var layer in panel.Layers) layer.Name = layer.Name.Trim();
        var folder = ResolveFolder(projectFolder, relativeFolder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The Panel folder no longer exists.");
        var path = Path.Combine(folder, panel.Id + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(panel, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task SaveThumbnailAsync(string projectFolder, PanelDocument panel, string? relativeFolder, string dataUrl)
    {
        const string marker = ";base64,";
        var index = dataUrl.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException("The panel thumbnail could not be encoded.");
        var folder = ResolveFolder(projectFolder, relativeFolder);
        var path = Path.Combine(folder, panel.Id + ".thumb.png");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, Convert.FromBase64String(dataUrl[(index + marker.Length)..]));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string Move(string projectFolder, string panelReference, string? destinationFolder)
    {
        var source = PanelPath(projectFolder, panelReference);
        if (!File.Exists(source)) throw new FileNotFoundException("The selected Panel no longer exists.");
        var destinationDirectory = ResolveFolder(projectFolder, destinationFolder);
        if (!Directory.Exists(destinationDirectory)) throw new DirectoryNotFoundException("The destination Panel folder no longer exists.");
        var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
        var sourceThumbnail = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + ".thumb.png");
        var destinationThumbnail = Path.Combine(destinationDirectory, Path.GetFileNameWithoutExtension(destination) + ".thumb.png");
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(destination)) throw new IOException("A Panel with this ID already exists in the destination folder.");
            if (File.Exists(sourceThumbnail) && File.Exists(destinationThumbnail))
                throw new IOException("A thumbnail for this Panel already exists in the destination folder.");
            File.Move(source, destination);
            if (File.Exists(sourceThumbnail)) File.Move(sourceThumbnail, destinationThumbnail);
        }
        return Path.GetRelativePath(PanelFolder(projectFolder), destination).Replace('\\', '/');
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
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string PanelPath(string projectFolder, string panelId)
    {
        if (Guid.TryParseExact(panelId, "N", out _)) panelId += ".json";
        var file = Path.GetFileNameWithoutExtension(panelId);
        if (!Guid.TryParseExact(file, "N", out _) || !panelId.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Panel reference is invalid.");
        var path = Path.GetFullPath(Path.Combine(PanelFolder(projectFolder), panelId.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(PanelFolder(projectFolder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Panel reference is outside the panels folder.");
        return path;
    }

    private static string ResolveFolder(string projectFolder, string? relativeFolder)
    {
        var root = Path.GetFullPath(PanelFolder(projectFolder));
        if (string.IsNullOrWhiteSpace(relativeFolder)) return root;
        if (Path.IsPathRooted(relativeFolder)) throw new InvalidOperationException("The Panel folder must be within this project.");
        var path = Path.GetFullPath(Path.Combine(root, relativeFolder.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Panel folder must be within this project.");
        return path;
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
