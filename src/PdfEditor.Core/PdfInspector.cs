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
            var size = doc.GetPage(p).GetPageSize();
            pages.Add(new PageInfo(p, size.GetWidth(), size.GetHeight()));
        }
        return new DocumentInfo(pages.Count, pages, encrypted);
    }
}
