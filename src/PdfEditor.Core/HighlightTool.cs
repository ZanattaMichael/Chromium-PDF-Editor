using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Extgstate;

namespace PdfEditor.Core;

/// <summary>Applies a highlighter mark over text.</summary>
public static class HighlightTool
{
    // A pleasant highlighter yellow when no colour is given.
    private static readonly DeviceRgb DefaultColour = new(255, 235, 59);

    /// <summary>
    /// Paints highlight rectangles over the given regions on a page. The rectangles are drawn with
    /// a Multiply blend so the text underneath shows straight through — the paper turns the
    /// highlight colour while the (usually dark) glyphs stay legible. Drawn in the page's default
    /// user space (via <see cref="PdfContentGuard"/>) so a leftover page transform can't shift them.
    /// </summary>
    public static EditResult AddHighlight(byte[] pdf, int page, IReadOnlyList<RectRegion> rects,
        string? colorHex = null, string? password = null)
    {
        if (rects.Count == 0) return EditResult.Of(pdf);
        var colour = (TextTools.ParseColor(colorHex) as DeviceRgb) ?? DefaultColour;

        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            if (page < 1 || page > doc.GetNumberOfPages())
                throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");

            var canvas = PdfContentGuard.InDefaultUserSpace(doc.GetPage(page), doc);
            canvas.SetExtGState(new PdfExtGState().SetBlendMode(PdfName.Multiply)).SetFillColor(colour);
            foreach (var r in rects)
                canvas.Rectangle(r.X, r.Y, r.Width, r.Height);
            canvas.Fill();
        }
        return EditResult.Of(output.ToArray());
    }
}
