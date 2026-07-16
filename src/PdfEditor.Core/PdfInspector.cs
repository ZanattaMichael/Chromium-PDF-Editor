namespace PdfEditor.Core;

/// <summary>Reads basic document facts (page geometry, encryption state).</summary>
public static class PdfInspector
{
    public static DocumentInfo GetInfo(byte[] pdf, string? password = null)
    {
        bool encrypted = Encryptor.IsEncrypted(pdf);
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        var pages = new List<PageInfo>();
        for (int p = 1; p <= doc.GetNumberOfPages(); p++)
        {
            var page = doc.GetPage(p);
            // PDFium renders the *crop* box (which defaults to the media box), not the media
            // box, and applies the page rotation — so both must be reported or the viewer's
            // coordinate mapping is wrong.
            var box = page.GetCropBox();
            int rotation = ((page.GetRotation() % 360) + 360) % 360;
            pages.Add(new PageInfo(p, box.GetX(), box.GetY(), box.GetWidth(), box.GetHeight(), rotation));
        }
        return new DocumentInfo(pages.Count, pages, encrypted);
    }
}
