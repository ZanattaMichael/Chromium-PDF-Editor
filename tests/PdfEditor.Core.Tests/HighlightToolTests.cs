using PdfEditor.Core;
using SkiaSharp;

namespace PdfEditor.Tests;

public class HighlightToolTests
{
    [Fact]
    public void AddHighlight_TurnsThePaperTheHighlightColour_OverAClearArea()
    {
        byte[] pdf = TestPdfs.WithText(("word", 72, 100, 12)); // text far from the highlight

        var result = HighlightTool.AddHighlight(pdf, 1,
            new[] { new RectRegion(1, 100, 500, 200, 30) }, "#ffeb3b");

        // Blank paper under the highlight becomes yellow (high R+G, low B).
        var px = TestPdfAssert.PixelAt(result.Pdf, 1, 200, 515, 150);
        Assert.True(px.Red > 200 && px.Green > 180 && px.Blue < 120,
            $"expected a yellow highlight but got {px}");
    }

    [Fact]
    public void AddHighlight_KeepsTextLegible_UnderneathTheHighlight()
    {
        byte[] pdf = TestPdfs.WithText(("HIGHLIGHT ME", 72, 500, 24));
        var match = Assert.Single(TextTools.FindText(pdf, "HIGHLIGHT ME"));

        var result = HighlightTool.AddHighlight(pdf, 1,
            new[] { new RectRegion(1, match.X, match.Y, match.Width, match.Height) });

        // Some glyph pixel under the highlight is still dark (multiply blend keeps the text).
        bool anyDark = false;
        for (float x = match.X; x < match.X + match.Width && !anyDark; x += 2)
        {
            var px = TestPdfAssert.PixelAt(result.Pdf, 1, x, match.Y + match.Height * 0.4f, 150);
            if (px.Red < 120 && px.Green < 120) anyDark = true;
        }
        Assert.True(anyDark, "the highlighted text should still be legible (dark glyphs present)");
    }

    [Fact]
    public void AddHighlight_NoRects_ReturnsUnchanged()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        Assert.Equal(pdf, HighlightTool.AddHighlight(pdf, 1, System.Array.Empty<RectRegion>()).Pdf);
    }

    [Fact]
    public void AddHighlight_InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            HighlightTool.AddHighlight(pdf, 9, new[] { new RectRegion(9, 0, 0, 10, 10) }));
    }
}
