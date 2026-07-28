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
        // WithAnnotations + WithFormFill draw the annotation and AcroForm-widget layers, so
        // inserted form fields (text/checkbox/dropdown/button) and comment annotations are
        // actually visible in the preview instead of rendering as blank page content.
        using SKBitmap bitmap = Conversion.ToImage(stream, page: (Index)(page - 1),
            password: password,
            options: new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true));
#pragma warning restore CA1416
        // A malformed document (e.g. a page tree that loops back on itself) can make PDFium hand
        // back a bitmap with no pixels, and Skia then returns null rather than throwing. Without
        // these checks the caller sees a bare NullReferenceException instead of being told the
        // page could not be rendered.
        using var image = SKImage.FromBitmap(bitmap)
            ?? throw new InvalidDataException($"Page {page} could not be rendered: the document is malformed.");
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException($"Page {page} could not be encoded as PNG.");
        return encoded.ToArray();
    }
}
