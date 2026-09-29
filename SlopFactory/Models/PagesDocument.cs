namespace SlopFactory.Models;

public sealed class PagesDocument
{
    public List<BookDocument> Books { get; set; } = [];
}

public sealed class BookDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled book";
    public string Description { get; set; } = string.Empty;
    // Stored in centimetres so changing the editor unit never changes the physical page size.
    public double PageWidthCm { get; set; } = 21;
    public double PageHeightCm { get; set; } = 29.7;
    public string SizePreset { get; set; } = "a4";
    public string SizeUnit { get; set; } = "cm";
    public List<PageDocument> Pages { get; set; } = [];
}

public sealed class PageDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled page";
    public string Layout { get; set; } = "single";
    public double MarginCm { get; set; } = 0.5;
    public List<PagePanelPlacement> Panels { get; set; } = [];
    public string Notes { get; set; } = string.Empty;
}

public sealed class PagePanelPlacement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "panel";
    public string PanelPath { get; set; } = string.Empty;
    public string PanelName { get; set; } = "Panel";
    public double WidthCm { get; set; }
    public double HeightCm { get; set; }
    public double Xcm { get; set; }
    public double Ycm { get; set; }
    public double Scale { get; set; } = 1;
    // Object-position percentages keep a cover-fitted panel visible while it is panned within its frame.
    public double ContentPositionX { get; set; } = 50;
    public double ContentPositionY { get; set; } = 50;
}
