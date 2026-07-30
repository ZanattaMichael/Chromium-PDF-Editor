using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using SkiaSharp;

namespace PdfEditor.Tests;

/// <summary>Inspection helpers used by assertions.</summary>
public static class TestPdfAssert
{
    /// <summary>Extracts all text from a page using standard extraction.</summary>
    public static string ExtractText(byte[] pdf, int page = 1, string? password = null)
    {
        var props = new ReaderProperties();
        if (password != null) props.SetPassword(System.Text.Encoding.UTF8.GetBytes(password));
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf), props));
        return PdfTextExtractor.GetTextFromPage(doc.GetPage(page), new LocationTextExtractionStrategy());
    }

    /// <summary>
    /// The (base font name, type size) of every text-showing run on a page, read from the rendered
    /// graphics state.
    /// <para>
    /// This is deliberately independent of <c>TextTools</c>'s own measurement: the font-fidelity
    /// tests (#29) must not verify the detector against itself, or a detector that is wrong in a
    /// self-consistent way passes. The size reported here is the <c>Tf</c> operand scaled by the
    /// text/graphics matrices, i.e. the size the glyphs are actually drawn at.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Font, float Size)> DrawnRuns(byte[] pdf, int page = 1)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var listener = new RunListener();
        new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(page));
        return listener.Runs;
    }

    private sealed class RunListener : IEventListener
    {
        public List<(string Font, float Size)> Runs { get; } = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo t || string.IsNullOrWhiteSpace(t.GetText())) return;
            // GetFontSize() is the Tf operand; the vertical scale of the text matrix carries any
            // Tm/cm scaling on top of it.
            var matrix = t.GetTextMatrix();
            float scale = (float)Math.Sqrt(Math.Abs(
                matrix.Get(iText.Kernel.Geom.Matrix.I11) * matrix.Get(iText.Kernel.Geom.Matrix.I22)
                - matrix.Get(iText.Kernel.Geom.Matrix.I12) * matrix.Get(iText.Kernel.Geom.Matrix.I21)));
            string name = t.GetFont()?.GetFontProgram()?.GetFontNames()?.GetFontName() ?? "";
            Runs.Add((name, t.GetFontSize() * (scale == 0 ? 1 : scale)));
        }

        public ICollection<EventType>? GetSupportedEvents() => null;
    }

    /// <summary>Counts image draw events on a page (XObject and inline images).</summary>
    public static int CountImages(byte[] pdf, int page = 1)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var listener = new ImageCounter();
        new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(page));
        return listener.Count;
    }

    /// <summary>Renders the page and returns the colour of the pixel at a user-space point.</summary>
    public static SKColor PixelAt(byte[] pdf, int page, float userX, float userY, int dpi = 72)
    {
        byte[] png = PdfEditor.Core.PageRenderer.RenderPagePng(pdf, page, dpi);
        using var bitmap = SKBitmap.Decode(png);
        float scale = dpi / 72f;
        using var docReader = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        float pageHeight = docReader.GetPage(page).GetPageSize().GetHeight();
        int px = (int)(userX * scale);
        int py = (int)((pageHeight - userY) * scale);
        return bitmap.GetPixel(Math.Clamp(px, 0, bitmap.Width - 1), Math.Clamp(py, 0, bitmap.Height - 1));
    }

    private sealed class ImageCounter : IEventListener
    {
        public int Count { get; private set; }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is ImageRenderInfo) Count++;
        }

        public ICollection<EventType>? GetSupportedEvents() => null;
    }
}
