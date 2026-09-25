using System.Text;

namespace SlopFactory.Services;

public sealed record PageRaster(string DataUrl, int Width, int Height);
public sealed record PdfPageRaster(PageRaster Raster, double WidthCm, double HeightCm);

public sealed class PagePdfExporter
{
    public async Task<string?> ExportAsync(string name, PageRaster raster, double widthCm, double heightCm)
        => await ExportAsync(name, [new PdfPageRaster(raster, widthCm, heightCm)]);

    public async Task<string?> ExportAsync(string name, IReadOnlyList<PdfPageRaster> pages)
    {
        if (pages.Count == 0) throw new InvalidOperationException("This book has no pages to export.");
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = SafeName(name, "book")
        };
        picker.FileTypeChoices.Add("PDF document", [".pdf"]);
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (window is null) throw new InvalidOperationException("A window is required to choose an export location.");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var destination = await picker.PickSaveFileAsync();
        if (destination is null) return null;
        await File.WriteAllBytesAsync(destination.Path, CreatePdf(pages));
        return destination.Path;
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("PDF export is currently available on Windows.");
#endif
    }

    private static byte[] CreatePdf(IReadOnlyList<PdfPageRaster> pages)
    {
        var prepared = pages.Select(page =>
        {
            const string marker = ";base64,";
            var index = page.Raster.DataUrl.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) throw new InvalidOperationException("The page image could not be encoded for PDF export.");
            return (Bytes: Convert.FromBase64String(page.Raster.DataUrl[(index + marker.Length)..]), PixelWidth: page.Raster.Width, PixelHeight: page.Raster.Height, PageWidth: page.WidthCm / 2.54 * 72, PageHeight: page.HeightCm / 2.54 * 72);
        }).ToList();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var offsets = new List<long> { 0 };
        void Text(string value) => writer.Write(Encoding.ASCII.GetBytes(value));
        void Object(int id, string value) { offsets.Add(stream.Position); Text($"{id} 0 obj\n{value}\nendobj\n"); }
        Text("%PDF-1.4\n%âãÏÓ\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        var pageObjects = Enumerable.Range(0, prepared.Count).Select(index => 3 + index * 3).ToList();
        Object(2, $"<< /Type /Pages /Kids [{string.Join(' ', pageObjects.Select(id => id + " 0 R"))}] /Count {prepared.Count} >>");
        for (var index = 0; index < prepared.Count; index++)
        {
            var item = prepared[index]; var pageObject = pageObjects[index]; var contentObject = pageObject + 1; var imageObject = pageObject + 2;
            Object(pageObject, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {item.PageWidth:0.###} {item.PageHeight:0.###}] /Resources << /XObject << /Im0 {imageObject} 0 R >> >> /Contents {contentObject} 0 R >>");
            var content = $"q\n{item.PageWidth:0.###} 0 0 {item.PageHeight:0.###} 0 0 cm\n/Im0 Do\nQ\n";
            Object(contentObject, $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
            offsets.Add(stream.Position);
            Text($"{imageObject} 0 obj\n<< /Type /XObject /Subtype /Image /Width {item.PixelWidth} /Height {item.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {item.Bytes.Length} >>\nstream\n");
            writer.Write(item.Bytes); Text("\nendstream\nendobj\n");
        }
        var startXref = stream.Position;
        Text($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Text($"{offset:0000000000} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF");
        return stream.ToArray();
    }

    private static string SafeName(string name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
