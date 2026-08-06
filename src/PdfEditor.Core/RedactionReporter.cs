using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace PdfEditor.Core;

/// <summary>
/// Builds an auditable summary of what a redaction removes: per page, how many text runs, images,
/// and annotations fell under the redaction regions, plus a document-level note on the content
/// redaction does <em>not</em> touch (embedded JavaScript and identifying metadata). It is computed
/// from the <em>original</em> document — the content that was under the boxes is exactly what the
/// redaction takes out — so it stands as a record of the operation. (#48)
/// </summary>
public static class RedactionReporter
{
    /// <summary>Counts, by category and page, the content inside the redaction regions.</summary>
    public static RedactionReport Analyze(byte[] pdf, IReadOnlyList<RectRegion> regions, string? password = null)
    {
        var byPage = regions
            .GroupBy(r => r.Page)
            .ToDictionary(g => g.Key, g => g.Select(r => new Rectangle(r.X, r.Y, r.Width, r.Height)).ToList());

        var pageReports = new List<RedactionPageReport>();
        bool metadata;
        using (var doc = PdfIo.OpenReadOnly(pdf, password))
        {
            int total = doc.GetNumberOfPages();
            foreach (int pageNum in byPage.Keys.Where(p => p >= 1 && p <= total).OrderBy(p => p))
            {
                var rects = byPage[pageNum];
                var page = doc.GetPage(pageNum);

                int textRuns = TextTools.GetTextSpans(pdf, pageNum, password)
                    .Count(s => Overlaps(rects, new Rectangle(s.X, s.Y, s.Width, s.Height)));

                int images = ImageRects(page).Count(ir => Overlaps(rects, ir));

                int annotations = page.GetAnnotations().Count(a =>
                {
                    var ar = a.GetRectangle()?.ToRectangle();
                    return ar != null && Overlaps(rects, ar);
                });

                pageReports.Add(new RedactionPageReport(pageNum, rects.Count, textRuns, images, annotations));
            }
            metadata = HasIdentifyingMetadata(doc);
        }

        // JavaScript and metadata survive redaction (they are stripped by "Remove hidden info"),
        // so the report flags them so a user auditing a redaction knows they are still there.
        int javaScript = PdfSafety.Scan(pdf, password).JavaScriptCount;

        return new RedactionReport(
            Regions: pageReports.Sum(p => p.Regions),
            PagesAffected: pageReports.Count,
            TextRuns: pageReports.Sum(p => p.TextRuns),
            Images: pageReports.Sum(p => p.Images),
            Annotations: pageReports.Sum(p => p.Annotations),
            Pages: pageReports,
            RemainingJavaScript: javaScript,
            RemainingMetadata: metadata);
    }

    private static bool Overlaps(IReadOnlyList<Rectangle> regions, Rectangle box) =>
        regions.Any(r => Intersects(r, box));

    private static bool Intersects(Rectangle a, Rectangle b) =>
        a.GetLeft() < b.GetRight() && b.GetLeft() < a.GetRight() &&
        a.GetBottom() < b.GetTop() && b.GetBottom() < a.GetTop();

    /// <summary>On-page rectangles of every image drawn on a page.</summary>
    private static List<Rectangle> ImageRects(PdfPage page)
    {
        var finder = new ImageRectFinder();
        PdfIo.Guarded($"scanning images on page {page.GetDocument().GetPageNumber(page)}",
            () => new PdfCanvasProcessor(finder).ProcessPageContent(page));
        return finder.Rects;
    }

    /// <summary>
    /// True when the document carries metadata that could identify it: any of the human-authored
    /// Info fields, or an XMP metadata stream. Producer/CreationDate are ignored — they are set by
    /// the PDF engine on every save and are not what an auditor means by "metadata".
    /// </summary>
    private static bool HasIdentifyingMetadata(PdfDocument doc)
    {
        if (doc.GetCatalog().GetPdfObject().Get(PdfName.Metadata) != null) return true;
        var info = doc.GetDocumentInfo();
        return !string.IsNullOrWhiteSpace(info.GetTitle())
            || !string.IsNullOrWhiteSpace(info.GetAuthor())
            || !string.IsNullOrWhiteSpace(info.GetSubject())
            || !string.IsNullOrWhiteSpace(info.GetKeywords())
            || !string.IsNullOrWhiteSpace(info.GetCreator());
    }

    private sealed class ImageRectFinder : IEventListener
    {
        public List<Rectangle> Rects { get; } = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_IMAGE || data is not ImageRenderInfo info) return;
            var m = info.GetImageCtm();
            float w = m.Get(Matrix.I11), h = m.Get(Matrix.I22);
            float x = m.Get(Matrix.I31), y = m.Get(Matrix.I32);
            Rects.Add(new Rectangle(Math.Min(x, x + w), Math.Min(y, y + h), Math.Abs(w), Math.Abs(h)));
        }

        public ICollection<EventType> GetSupportedEvents() =>
            new HashSet<EventType> { EventType.RENDER_IMAGE };
    }
}

/// <summary>Auditable summary of a redaction: totals, per-page counts, and what survives it.</summary>
public sealed record RedactionReport(
    int Regions, int PagesAffected, int TextRuns, int Images, int Annotations,
    IReadOnlyList<RedactionPageReport> Pages, int RemainingJavaScript, bool RemainingMetadata);

/// <summary>Per-page redaction counts.</summary>
public sealed record RedactionPageReport(int Page, int Regions, int TextRuns, int Images, int Annotations);
