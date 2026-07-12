using PDFtoImage;
using SkiaSharp;

namespace PdfEditor.Core;

/// <summary>Renders pages to PNG for the extension's preview canvas (PDFium via PDFtoImage).</summary>
public static class PageRenderer
{
    /// <summary>Renders a single page (1-based) to PNG at the given DPI.</summary>
    public static byte[] RenderPagePng(byte[] pdf, int page, int dpi = 144, string? password = null)
    {
        using var stream = new MemoryStream(pdf);
        // PDFtoImage annotates ToImage per-platform; every platform this host ships to
        // (Windows, Linux, macOS — see the install scripts) is on its supported list.
#pragma warning disable CA1416
        using SKBitmap bitmap = Conversion.ToImage(stream, page: (Index)(page - 1),
            password: password, options: new RenderOptions(Dpi: dpi));
#pragma warning restore CA1416
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
