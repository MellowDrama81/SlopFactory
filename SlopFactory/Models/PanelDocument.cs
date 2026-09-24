namespace SlopFactory.Models;

public sealed class PanelDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled Panel";
    public int Width { get; set; } = 1200;
    public int Height { get; set; } = 800;
    public List<PanelLayer> Layers { get; set; } = [];
}

public sealed class PanelLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Layer";
    public string Kind { get; set; } = "raster";
    public bool Visible { get; set; } = true;
    // Raster layers reference a path relative to the project's assets folder.
    public string? Asset { get; set; }
    // Vector layers contain an SVG document, independent of the assets folder.
    public string? Svg { get; set; }
    // SVG/CSS affine matrix: a, b, c, d, translateX, translateY.
    public double[] Matrix { get; set; } = [1, 0, 0, 1, 0, 0];
}

public sealed record PanelSummary(string Id, string Name, int Width, int Height, string Path, PanelDocument Document);
public sealed record PanelPaneSelection(string PaneId, string? ProjectFolder, string? PanelId, string? PanelName, string? Folder = null, bool OpenEditor = false);
