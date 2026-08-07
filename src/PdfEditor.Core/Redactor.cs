using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

namespace PdfEditor.Core;

/// <summary>
/// Applies true redaction: the content beneath each region (text, images, vector marks
/// inside form XObjects) is removed from the file, then an opaque black box is painted
/// over the region.
/// </summary>
public static class Redactor
{
    /// <summary>How the redaction box is painted (purely cosmetic; the content is removed either way).</summary>
    public enum Fill
    {
        /// <summary>A flat opaque black rectangle.</summary>
        Solid,
        /// <summary>Solid black overlaid with a diagonal hatch, for a heavier "redacted" look.</summary>
        Hatch,
    }

    public static EditResult Redact(byte[] pdf, IEnumerable<RectRegion> regions,
        string? password = null, Fill fill = Fill.Solid)
        => Apply(pdf, regions, drawBoxes: true, password, fill: fill);

    /// <summary>
    /// Removes the content in the regions without painting black boxes (used by text and image
    /// editing). <paramref name="kinds"/> selects what may be taken out: text editing passes
    /// <see cref="ContentKinds.TextOnly"/> so artwork behind the text is left alone, while moving an
    /// image needs the default and must take the image with it.
    /// </summary>
    internal static EditResult RemoveContent(byte[] pdf, IEnumerable<RectRegion> regions,
        string? password = null, ContentKinds kinds = ContentKinds.All)
        => Apply(pdf, regions, drawBoxes: false, password, kinds);

    private static EditResult Apply(byte[] pdf, IEnumerable<RectRegion> regions, bool drawBoxes,
        string? password, ContentKinds kinds = ContentKinds.All, Fill fill = Fill.Solid)
    {
        var byPage = regions.GroupBy(r => r.Page).ToDictionary(g => g.Key, g => g.ToList());
        if (byPage.Count == 0) return EditResult.Of(pdf);

        var warnings = new List<string>();
        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            foreach (var (pageNumber, pageRegions) in byPage)
            {
                if (pageNumber < 1 || pageNumber > doc.GetNumberOfPages())
                    throw new ArgumentOutOfRangeException(nameof(regions), $"Page {pageNumber} does not exist.");
                var page = doc.GetPage(pageNumber);
                var rects = pageRegions.Select(r => new Rectangle(r.X, r.Y, r.Width, r.Height)).ToList();

                var editor = ContentStreamEditor.Create(rects, doc, warnings, kinds: kinds);
                editor.EditPage(page);

                RemoveAnnotationsIn(page, rects);

                if (drawBoxes)
                    DrawBoxesInDefaultUserSpace(doc, page, rects, fill);
            }
        }
        return new EditResult(output.ToArray(), warnings);
    }

    /// <summary>
    /// Paints the opaque black boxes over the regions. Drawing in the page's default user space
    /// (see <see cref="PdfContentGuard.InDefaultUserSpace"/>) keeps the boxes aligned with the
    /// content even when the page leaves a scale/flip transform active — which is why the box used
    /// to land in the wrong place on Chrome / Google-Docs-exported PDFs while the removal was fine.
    /// </summary>
    private static void DrawBoxesInDefaultUserSpace(PdfDocument doc, PdfPage page, IList<Rectangle> rects, Fill fill)
    {
        var canvas = PdfContentGuard.InDefaultUserSpace(page, doc);
        canvas.SetFillColor(ColorConstants.BLACK);
        foreach (var r in rects)
            canvas.Rectangle(r.GetLeft(), r.GetBottom(), r.GetWidth(), r.GetHeight());
        canvas.Fill();

        // The hatch is purely visual — the content is already gone, and the solid black beneath keeps
        // the box fully opaque. It just gives a heavier, textured "redacted" look.
        if (fill == Fill.Hatch)
            foreach (var r in rects)
                DrawHatch(canvas, r);
    }

    /// <summary>Overlays a diagonal hatch, clipped to the box, in a slightly lighter grey.</summary>
    private static void DrawHatch(PdfCanvas canvas, Rectangle r)
    {
        canvas.SaveState();
        canvas.Rectangle(r.GetLeft(), r.GetBottom(), r.GetWidth(), r.GetHeight()).Clip().EndPath();
        canvas.SetStrokeColor(new DeviceGray(0.30f)).SetLineWidth(0.8f);
        const float step = 4f;
        float h = r.GetHeight();
        // 45° lines sweeping across the box; starting h to the left of the box so the whole face fills.
        for (float x = r.GetLeft() - h; x <= r.GetRight(); x += step)
            canvas.MoveTo(x, r.GetBottom()).LineTo(x + h, r.GetTop());
        canvas.Stroke();
        canvas.RestoreState();
    }

    private static void RemoveAnnotationsIn(PdfPage page, IList<Rectangle> regions)
    {
        foreach (var annotation in page.GetAnnotations().ToArray())
        {
            var rect = annotation.GetRectangle()?.ToRectangle();
            if (rect != null && regions.Any(r => Intersects(r, rect)))
                page.RemoveAnnotation(annotation);
        }
    }

    private static bool Intersects(Rectangle a, Rectangle b) =>
        a.GetLeft() < b.GetRight() && b.GetLeft() < a.GetRight() &&
        a.GetBottom() < b.GetTop() && b.GetBottom() < a.GetTop();
}
