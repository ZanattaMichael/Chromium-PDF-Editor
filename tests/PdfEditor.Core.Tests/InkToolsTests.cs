using PdfEditor.Core;
using SkiaSharp;

namespace PdfEditor.Tests;

public class InkToolsTests
{
    [Fact]
    public void AddInk_DrawsAStroke_ThatRendersInTheChosenColour()
    {
        byte[] pdf = TestPdfs.WithText(("keep", 72, 100, 12));

        // A horizontal red stroke across the middle of the page.
        var strokes = new IReadOnlyList<(float, float)>[]
        {
            new (float, float)[] { (100, 500), (300, 500), (400, 500) },
        };
        var result = InkTools.AddInk(pdf, 1, strokes, "#ff0000", width: 6f);

        var pixel = TestPdfAssert.PixelAt(result.Pdf, 1, 250, 500, 150);
        Assert.True(pixel.Red > 180 && pixel.Green < 80 && pixel.Blue < 80,
            $"expected a red stroke at (250,500) but got {pixel}");
    }

    [Fact]
    public void AddInk_SinglePoint_DrawsADot()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 100, 12));

        var strokes = new IReadOnlyList<(float, float)>[]
        {
            new (float, float)[] { (300, 400) },
        };
        var result = InkTools.AddInk(pdf, 1, strokes, "#0000ff", width: 12f);

        var pixel = TestPdfAssert.PixelAt(result.Pdf, 1, 300, 400, 150);
        Assert.True(pixel.Blue > 180 && pixel.Red < 80, $"expected a blue dot but got {pixel}");
    }

    [Fact]
    public void AddInk_PreservesExistingContent()
    {
        byte[] pdf = TestPdfs.WithText(("original text", 72, 700, 14));

        var strokes = new IReadOnlyList<(float, float)>[]
        {
            new (float, float)[] { (100, 300), (200, 350) },
        };
        var result = InkTools.AddInk(pdf, 1, strokes, "#000000", 2f);

        Assert.Contains("original text", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Fact]
    public void AddInk_NoStrokes_ReturnsUnchanged()
    {
        byte[] pdf = TestPdfs.WithText(("hi", 72, 700, 12));
        var result = InkTools.AddInk(pdf, 1, Array.Empty<IReadOnlyList<(float, float)>>());
        Assert.Equal(pdf, result.Pdf);
    }

    [Fact]
    public void AddInk_InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("hi", 72, 700, 12));
        var strokes = new IReadOnlyList<(float, float)>[] { new (float, float)[] { (1, 1), (2, 2) } };
        Assert.Throws<ArgumentOutOfRangeException>(() => InkTools.AddInk(pdf, 9, strokes));
    }

    [Fact]
    public void AddText_StampsNewText_WithoutRemovingExisting()
    {
        byte[] pdf = TestPdfs.WithText(("existing line", 72, 700, 14));

        var result = TextTools.AddText(pdf, new RectRegion(1, 72, 400, 300, 40),
            "added caption", fontSize: 16, fontFamily: "times");

        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.Contains("existing line", text);
        Assert.Contains("added caption", text);
    }

    [Fact]
    public void AddText_RendersDark_AtTheGivenRegion()
    {
        byte[] pdf = TestPdfs.WithText(("bg", 72, 100, 12));

        var result = TextTools.AddText(pdf, new RectRegion(1, 100, 500, 300, 30),
            "HELLO", fontSize: 24, colorHex: "#000000");

        // Some dark glyph pixel appears along the caption's band.
        bool anyDark = false;
        for (float x = 100; x < 380 && !anyDark; x += 2)
        {
            var px = TestPdfAssert.PixelAt(result.Pdf, 1, x, 512, 150);
            if (px.Red < 128 && px.Green < 128 && px.Blue < 128) anyDark = true;
        }
        Assert.True(anyDark, "added text did not render at the region");
    }
}
