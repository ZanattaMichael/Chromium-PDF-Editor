using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Extgstate;

namespace PdfEditor.Core;

/// <summary>
/// Stamps a diagonal text watermark across pages. The mark is written straight into each page's
/// content stream — not added as an annotation or an optional-content (layer) group — so it is part
/// of the page in the same way its body text is. A reader cannot toggle it off from a layers panel
/// or delete it from an annotation list; removing it means editing the page content itself.
/// </summary>
public static class WatermarkTool
{
    // A neutral mid-grey when no colour is given: visible over white, unobtrusive over text.
    private static readonly DeviceRgb DefaultColour = new(128, 128, 128);

    /// <summary>
    /// Draws <paramref name="text"/> centred on and rotated across each target page, styled per
    /// <paramref name="options"/>.
    /// </summary>
    public static EditResult AddTextWatermark(byte[] pdf, string text,
        WatermarkOptions? options = null, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Watermark text is required.", nameof(text));

        var o = options ?? new WatermarkOptions();
        float opacity = Math.Clamp(o.Opacity, 0.05f, 1f);
        var colour = (TextTools.ParseColor(o.ColorHex) as DeviceRgb) ?? DefaultColour;
        var font = PdfFontFactory.CreateFont(TextTools.ResolveFont(o.FontFamily, o.Bold, o.Italic));
        double theta = o.RotationDegrees * Math.PI / 180.0;
        float cos = (float)Math.Cos(theta), sin = (float)Math.Sin(theta);

        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            var target = NormalizePages(o.Pages, doc.GetNumberOfPages());
            // One extended graphics state, shared across pages: it only carries the fill opacity.
            var gs = new PdfExtGState().SetFillOpacity(opacity);

            foreach (int pageNum in target)
            {
                var page = doc.GetPage(pageNum);
                var box = page.GetPageSize();
                float cx = (box.GetLeft() + box.GetRight()) / 2f;
                float cy = (box.GetBottom() + box.GetTop()) / 2f;

                float fs = o.FontSize ?? FitFontSize(font, text, box);
                float halfW = font.GetWidth(text, fs) / 2f;
                float halfH = fs * 0.35f; // roughly half a cap height, to centre the line vertically

                // Place the baseline start so the text's midpoint lands on the page centre once the
                // rotation is applied. Tm = [cos sin -sin cos startX startY]; solving for the start
                // that maps text-space (halfW, halfH) to (cx, cy):
                float startX = cx - (cos * halfW - sin * halfH);
                float startY = cy - (sin * halfW + cos * halfH);

                // Default user space (via PdfContentGuard) so a leftover page transform can't shift
                // or rescale the mark; save/restore so the opacity state does not leak into anything
                // drawn afterwards.
                var canvas = PdfContentGuard.InDefaultUserSpace(page, doc);
                canvas.SaveState();
                canvas.SetExtGState(gs).SetFillColor(colour);
                canvas.BeginText().SetFontAndSize(font, fs)
                    .SetTextMatrix(cos, sin, -sin, cos, startX, startY)
                    .ShowText(text)
                    .EndText();
                canvas.RestoreState();
            }
        }
        return EditResult.Of(output.ToArray());
    }

    /// <summary>Sizes the text so it spans ~80% of the page diagonal, clamped to a sane range.</summary>
    private static float FitFontSize(PdfFont font, string text, Rectangle box)
    {
        float widthAt1 = Math.Max(0.001f, font.GetWidth(text, 1f));
        float diagonal = (float)Math.Sqrt(box.GetWidth() * box.GetWidth() + box.GetHeight() * box.GetHeight());
        return Math.Clamp(diagonal * 0.8f / widthAt1, 8f, 200f);
    }

    /// <summary>Every page when none are named; otherwise the valid, de-duplicated, ordered subset.</summary>
    private static List<int> NormalizePages(IReadOnlyList<int>? pages, int total)
    {
        if (pages == null || pages.Count == 0)
            return Enumerable.Range(1, total).ToList();
        return pages.Where(p => p >= 1 && p <= total).Distinct().OrderBy(p => p).ToList();
    }
}

/// <summary>Styling options for <see cref="WatermarkTool.AddTextWatermark"/>.</summary>
/// <param name="Opacity">Fill opacity 0–1 (clamped to 0.05–1). Lower is fainter.</param>
/// <param name="RotationDegrees">Rotation of the text, anticlockwise. 45° is the usual diagonal.</param>
/// <param name="FontSize">Explicit point size, or null to size to ~80% of the page diagonal.</param>
/// <param name="Pages">1-based page numbers to stamp, or null/empty for every page.</param>
public sealed record WatermarkOptions(
    string? FontFamily = null, bool Bold = false, bool Italic = false,
    string? ColorHex = null, float Opacity = 0.3f, float RotationDegrees = 45f,
    float? FontSize = null, IReadOnlyList<int>? Pages = null);
