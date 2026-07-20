using iText.Kernel.Pdf;

namespace PdfEditor.Core;

/// <summary>Page-level operations: rotation (and room to grow — reorder, delete, insert).</summary>
public static class PageTools
{
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
