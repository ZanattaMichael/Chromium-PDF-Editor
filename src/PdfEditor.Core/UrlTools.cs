using iText.Kernel.Pdf;

namespace PdfEditor.Core;

/// <summary>Extracts the link URLs embedded in a document's annotations.</summary>
public static class UrlTools
{
    public static IReadOnlyList<PdfLink> ExtractLinks(byte[] pdf, string? password = null)
    {
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        var links = new List<PdfLink>();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            foreach (var annot in doc.GetPage(i).GetAnnotations())
            {
                var rect = annot.GetRectangle()?.ToRectangle();
                CollectUri(annot.GetPdfObject().Get(PdfName.A), i, rect, links);
            }
        return links;
    }

    private static void CollectUri(PdfObject? obj, int page, iText.Kernel.Geom.Rectangle? rect,
        List<PdfLink> links)
    {
        if (obj is PdfArray arr)
        {
            foreach (var e in arr) CollectUri(e, page, rect, links);
            return;
        }
        if (obj is not PdfDictionary a) return;
        if (a.GetAsName(PdfName.S)?.Equals(PdfName.URI) == true)
        {
            var uri = a.GetAsString(PdfName.URI)?.ToUnicodeString();
            if (!string.IsNullOrWhiteSpace(uri))
                links.Add(rect == null
                    ? new PdfLink(page, uri)
                    : new PdfLink(page, uri, rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight()));
        }
        CollectUri(a.Get(PdfName.Next), page, rect, links); // chained actions
    }
}
