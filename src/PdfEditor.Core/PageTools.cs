using iText.Kernel.Pdf;
using iText.Kernel.Utils;

namespace PdfEditor.Core;

/// <summary>Page-level operations: rotation and arranging (reorder / delete).</summary>
public static class PageTools
{
    /// <summary>
    /// Rebuilds the document so its pages appear in exactly the given 1-based order. Pages omitted
    /// from <paramref name="order"/> are dropped, so this single operation covers both reordering
    /// and deleting pages (a page number may repeat to duplicate a page). The order must reference
    /// at least one existing page.
    /// </summary>
    public static EditResult Arrange(byte[] pdf, IReadOnlyList<int> order, string? password = null)
    {
        if (order.Count == 0)
            throw new ArgumentException("At least one page must remain.", nameof(order));

        using var source = PdfIo.OpenReadOnly(pdf, password);
        int count = source.GetNumberOfPages();
        foreach (int n in order)
            if (n < 1 || n > count)
                throw new ArgumentOutOfRangeException(nameof(order), $"Page {n} does not exist.");

        using var output = new MemoryStream();
        using (var target = new PdfDocument(new PdfWriter(output)))
        {
            new PdfMerger(target).Merge(source, order.ToList());
        }
        return EditResult.Of(output.ToArray());
    }

    /// <summary>
    /// Rotates the given pages (1-based) by <paramref name="deltaDegrees"/> clockwise, on top of
    /// each page's existing rotation. The delta is normalised to a multiple of 90; the resulting
    /// rotation is stored as 0/90/180/270. Pass an empty <paramref name="pages"/> to rotate every
    /// page.
    /// </summary>
    public static EditResult Rotate(byte[] pdf, IReadOnlyCollection<int> pages, int deltaDegrees,
        string? password = null)
    {
        int delta = ((deltaDegrees % 360) + 360) % 360;
        if (delta % 90 != 0)
            throw new ArgumentException("Rotation must be a multiple of 90 degrees.", nameof(deltaDegrees));

        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            int count = doc.GetNumberOfPages();
            var targets = pages.Count == 0
                ? Enumerable.Range(1, count).ToList()
                : pages.Distinct().ToList();

            foreach (int n in targets)
            {
                if (n < 1 || n > count)
                    throw new ArgumentOutOfRangeException(nameof(pages), $"Page {n} does not exist.");
                if (delta == 0) continue;
                var page = doc.GetPage(n);
                int current = ((page.GetRotation() % 360) + 360) % 360;
                page.SetRotation((current + delta) % 360);
            }
        }
        return EditResult.Of(output.ToArray());
    }
}
