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
    public static EditResult Redact(byte[] pdf, IEnumerable<RectRegion> regions, string? password = null)
        => Apply(pdf, regions, drawBoxes: true, password);

    /// <summary>Removes the content in the regions without painting black boxes (used by text editing).</summary>
    internal static EditResult RemoveContent(byte[] pdf, IEnumerable<RectRegion> regions, string? password = null)
        => Apply(pdf, regions, drawBoxes: false, password);

    private static EditResult Apply(byte[] pdf, IEnumerable<RectRegion> regions, bool drawBoxes, string? password)
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

                var editor = ContentStreamEditor.Create(rects, doc, warnings);
                editor.EditPage(page);

                RemoveAnnotationsIn(page, rects);

                if (drawBoxes)
                {
                    var canvas = new PdfCanvas(page);
                    canvas.SaveState().SetFillColor(ColorConstants.BLACK);
                    foreach (var r in rects)
                        canvas.Rectangle(r.GetLeft(), r.GetBottom(), r.GetWidth(), r.GetHeight()).Fill();
                    canvas.RestoreState();
                }
            }
        }
        return new EditResult(output.ToArray(), warnings);
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
