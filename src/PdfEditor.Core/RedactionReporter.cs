using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using SkiaSharp;

namespace PdfEditor.Core;

/// <summary>
/// Builds an auditable, compliance-grade record of what a redaction removes: per page and per
/// region, how many text runs, images, and annotations fell under the boxes — together with the
/// actual text removed and thumbnails of the images removed — plus a note on the content redaction
/// does <em>not</em> touch (embedded JavaScript and identifying metadata). It is computed from the
/// <em>original</em> document, because the content under the boxes is exactly what redaction takes
/// out, so it stands as a record of the operation. (#48)
/// </summary>
public static class RedactionReporter
{
    // Longest edge, in pixels, of an image thumbnail embedded in the report.
    private const int ThumbnailMaxDim = 220;

    /// <summary>Itemises, by region and page, the content inside the redaction regions.</summary>
    public static RedactionReport Analyze(byte[] pdf, IReadOnlyList<RectRegion> regions, string? password = null)
    {
        var byPage = regions
            .GroupBy(r => r.Page)
            .ToDictionary(g => g.Key, g => g.Select(r => new Rectangle(r.X, r.Y, r.Width, r.Height)).ToList());

        var pageReports = new List<RedactionPageReport>();
        var regionReports = new List<RedactionRegionReport>();
        bool metadata;
        using (var doc = PdfIo.OpenReadOnly(pdf, password))
        {
            int total = doc.GetNumberOfPages();
            foreach (int pageNum in byPage.Keys.Where(p => p >= 1 && p <= total).OrderBy(p => p))
            {
                var rects = byPage[pageNum];
                var page = doc.GetPage(pageNum);

                // Gather the page's content once, then attribute it both per-region (for the
                // itemised compliance detail) and per-page (distinct, so overlapping regions do
                // not inflate the totals).
                var spans = TextTools.GetTextSpans(pdf, pageNum, password)
                    .Select(s => new Rectangle(s.X, s.Y, s.Width, s.Height)).ToList();
                var pageImages = PageImages(page);
                var imageRects = pageImages.Select(i => i.Rect).ToList();
                var annots = page.GetAnnotations()
                    .Select(a => a.GetRectangle()?.ToRectangle()).Where(r => r != null).Select(r => r!).ToList();

                foreach (var r in rects)
                {
                    string text = TextTools.GetTextInRegion(pdf,
                        new RectRegion(pageNum, r.GetX(), r.GetY(), r.GetWidth(), r.GetHeight()), password).Text;
                    var thumbs = pageImages
                        .Where(im => Intersects(r, im.Rect) && im.Bytes is { Length: > 0 })
                        .Select(im => Thumbnail(im.Bytes!))
                        .Where(t => t != null).Select(t => t!).ToList();

                    var removed = new RemovedContent(
                        TextRuns: spans.Count(s => Intersects(r, s)),
                        Images: imageRects.Count(im => Intersects(r, im)),
                        Annotations: annots.Count(an => Intersects(r, an)),
                        Text: text,
                        ImageThumbnails: thumbs);
                    regionReports.Add(new RedactionRegionReport(pageNum,
                        r.GetX(), r.GetY(), r.GetWidth(), r.GetHeight(), removed));
                }

                pageReports.Add(new RedactionPageReport(pageNum, rects.Count,
                    spans.Count(s => Overlaps(rects, s)),
                    imageRects.Count(im => Overlaps(rects, im)),
                    annots.Count(an => Overlaps(rects, an))));
            }
            metadata = HasIdentifyingMetadata(doc);
        }

        // JavaScript and metadata survive redaction (they are stripped by "Remove hidden info"),
        // so the report flags them so a user auditing a redaction knows they are still there.
        int javaScript = PdfSafety.Scan(pdf, password).JavaScriptCount;

        var totals = new RedactionTotals(
            pageReports.Sum(p => p.TextRuns), pageReports.Sum(p => p.Images), pageReports.Sum(p => p.Annotations));
        return new RedactionReport(
            Regions: pageReports.Sum(p => p.Regions),
            PagesAffected: pageReports.Count,
            Totals: totals,
            Pages: pageReports,
            RegionDetails: regionReports,
            Residual: new ResidualRisk(javaScript, metadata));
    }

    private static bool Overlaps(IReadOnlyList<Rectangle> regions, Rectangle box) =>
        regions.Any(r => Intersects(r, box));

    private static bool Intersects(Rectangle a, Rectangle b) =>
        a.GetLeft() < b.GetRight() && b.GetLeft() < a.GetRight() &&
        a.GetBottom() < b.GetTop() && b.GetBottom() < a.GetTop();

    /// <summary>Image draws on a page, each with its on-page rectangle and (best-effort) bytes.</summary>
    private static List<FoundImage> PageImages(PdfPage page)
    {
        var finder = new ImageFinder();
        PdfIo.Guarded($"scanning images on page {page.GetDocument().GetPageNumber(page)}",
            () => new PdfCanvasProcessor(finder).ProcessPageContent(page));
        return finder.Found;
    }

    /// <summary>Re-encodes an image as a small PNG thumbnail (base64), or null if it can't be decoded.</summary>
    private static string? Thumbnail(byte[] imageBytes)
    {
        try
        {
            using var src = SKBitmap.Decode(imageBytes);
            if (src == null) return null;
            float scale = Math.Min(1f, (float)ThumbnailMaxDim / Math.Max(src.Width, src.Height));
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            SKBitmap? resized = scale < 1f
                ? src.Resize(new SKImageInfo(Math.Max(1, (int)(src.Width * scale)),
                    Math.Max(1, (int)(src.Height * scale))), sampling)
                : null;
            using var chosen = resized; // null when no resize was needed; src is used directly below
            using var image = SKImage.FromBitmap(resized ?? src);
            using var enc = image.Encode(SKEncodedImageFormat.Png, 80);
            return Convert.ToBase64String(enc.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // an undecodable/unsupported image is simply omitted from the report
        }
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

    private sealed record FoundImage(Rectangle Rect, byte[]? Bytes);

    private sealed class ImageFinder : IEventListener
    {
        public List<FoundImage> Found { get; } = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_IMAGE || data is not ImageRenderInfo info) return;
            var m = info.GetImageCtm();
            float w = m.Get(Matrix.I11), h = m.Get(Matrix.I22);
            float x = m.Get(Matrix.I31), y = m.Get(Matrix.I32);
            var rect = new Rectangle(Math.Min(x, x + w), Math.Min(y, y + h), Math.Abs(w), Math.Abs(h));
            byte[]? bytes = null;
            try { bytes = info.GetImage()?.GetImageBytes(); }
            catch { /* unsupported encoding — the rect is still counted, just no thumbnail */ }
            Found.Add(new FoundImage(rect, bytes));
        }

        public ICollection<EventType> GetSupportedEvents() =>
            new HashSet<EventType> { EventType.RENDER_IMAGE };
    }
}

/// <summary>Auditable summary of a redaction: totals, per-page and per-region detail, and residual risk.</summary>
public sealed record RedactionReport(
    int Regions, int PagesAffected, RedactionTotals Totals,
    IReadOnlyList<RedactionPageReport> Pages,
    IReadOnlyList<RedactionRegionReport> RegionDetails,
    ResidualRisk Residual);

/// <summary>Document-wide counts of what was removed.</summary>
public sealed record RedactionTotals(int TextRuns, int Images, int Annotations);

/// <summary>Content redaction does not remove, flagged so an auditor can act on it separately.</summary>
public sealed record ResidualRisk(int RemainingJavaScript, bool RemainingMetadata);

/// <summary>Per-page redaction counts.</summary>
public sealed record RedactionPageReport(int Page, int Regions, int TextRuns, int Images, int Annotations);

/// <summary>One redaction region: where it was and exactly what it removed.</summary>
public sealed record RedactionRegionReport(
    int Page, double X, double Y, double Width, double Height, RemovedContent Removed);

/// <summary>The content a single region removed: counts, the text, and image thumbnails (base64 PNG).</summary>
public sealed record RemovedContent(
    int TextRuns, int Images, int Annotations, string Text, IReadOnlyList<string> ImageThumbnails);
