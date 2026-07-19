using iText.Kernel.Geom;

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
            // The renderer (PDFium) shows the *effective* crop box — the crop box intersected
            // with the media box — and applies the page rotation. iText's GetCropBox() returns
            // the crop box as authored, which can be larger than or offset beyond the media box;
            // reporting that unclamped would scale/shift every coordinate the viewer maps, so
            // redactions would land in the wrong place. Report exactly what gets rendered.
            var box = EffectiveBox(page.GetCropBox(), page.GetMediaBox());
            int rotation = ((page.GetRotation() % 360) + 360) % 360;
            pages.Add(new PageInfo(p, box.GetX(), box.GetY(), box.GetWidth(), box.GetHeight(), rotation));
        }
        return new DocumentInfo(pages.Count, pages, encrypted);
    }

    /// <summary>The crop box clamped to the media box (what actually gets rendered).</summary>
    private static Rectangle EffectiveBox(Rectangle crop, Rectangle media)
    {
        float llx = Math.Max(crop.GetLeft(), media.GetLeft());
        float lly = Math.Max(crop.GetBottom(), media.GetBottom());
        float urx = Math.Min(crop.GetRight(), media.GetRight());
        float ury = Math.Min(crop.GetTop(), media.GetTop());
        if (urx <= llx || ury <= lly) return media; // degenerate intersection — fall back
        return new Rectangle(llx, lly, urx - llx, ury - lly);
    }
}
