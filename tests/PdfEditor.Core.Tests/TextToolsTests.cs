using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class TextToolsTests
{
    [Fact]
    public void GetTextInRegion_ReturnsTextAndFontSize()
    {
        byte[] pdf = TestPdfs.WithText(
            ("Invoice Number 12345", 72, 700, 14),
            ("unrelated footer", 72, 100, 9));

        var region = TextTools.GetTextInRegion(pdf, new RectRegion(1, 60, 690, 300, 30));

        Assert.Equal("Invoice Number 12345", region.Text);
        Assert.InRange(region.FontSize, 8, 20);
    }

    [Fact]
    public void GetTextInRegion_InsertsASpace_BetweenSeparateTextRunsOnTheSameLine()
    {
        // Two independent ShowText calls on the same baseline, positioned with a gap but
        // with no space glyph of their own: AssembleText must recognise the visual gap
        // and stitch them back together with an inferred space.
        byte[] pdf = TestPdfs.WithText(("Hello", 72, 700, 14), ("World", 160, 700, 14));

        var region = TextTools.GetTextInRegion(pdf, new RectRegion(1, 60, 690, 200, 30));

        Assert.Equal("Hello World", region.Text);
    }

    [Fact]
    public void GetTextInRegion_EmptyRegion_ReturnsEmpty()
    {
        byte[] pdf = TestPdfs.WithText(("hello", 72, 700, 12));
        var region = TextTools.GetTextInRegion(pdf, new RectRegion(1, 400, 100, 50, 50));
        Assert.Equal(string.Empty, region.Text);
    }

    [Fact]
    public void ReplaceTextInRegion_RemovesOldText_AndStampsNewText()
    {
        byte[] pdf = TestPdfs.WithText(("Old Company Name", 72, 700, 14));

        var result = TextTools.ReplaceTextInRegion(pdf,
            new RectRegion(1, 60, 690, 300, 30), "New Corp Ltd");

        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.DoesNotContain("Old Company", text);
        Assert.Contains("New Corp Ltd", text);
    }

    [Fact]
    public void ReplaceTextInRegion_StampsNewText_WhenPageLeavesATransformActive()
    {
        // Regression (same root cause as the redaction box): on a Chrome / Google-Docs PDF whose
        // content leaves a scale+flip matrix active, stamped replacement text used to be scaled and
        // flipped away instead of landing at the region. The new glyphs must render at the region.
        byte[] pdf = TestPdfs.ChromeStyleLeftoverCtm("SECRET");
        var match = Assert.Single(TextTools.FindText(pdf, "SECRET"));
        var region = new RectRegion(match.Page, match.X - 2, match.Y - 2, match.Width + 80, match.Height + 4);

        var result = TextTools.ReplaceTextInRegion(pdf, region, "PUBLIC", fontSize: match.Height);

        // Some glyph of the stamped word renders dark somewhere along the region's baseline.
        bool anyDark = false;
        for (float dx = 0; dx < region.Width && !anyDark; dx += 2)
        {
            var px = TestPdfAssert.PixelAt(result.Pdf, 1, region.X + dx, match.Y + match.Height * 0.45f, 150);
            if (px.Red < 128 && px.Green < 128 && px.Blue < 128) anyDark = true;
        }
        Assert.True(anyDark, "stamped replacement text did not render at the region — a leftover transform likely displaced it");
    }

    [Fact]
    public void GetTextInRegion_ReportsHelveticaSansForPlainText()
    {
        byte[] pdf = TestPdfs.WithText(("basic helvetica", 72, 700, 14));

        var region = TextTools.GetTextInRegion(pdf, new RectRegion(1, 60, 690, 300, 30));

        Assert.Equal("helvetica", region.FontFamily);
        Assert.False(region.Bold);
        Assert.False(region.Italic);
    }

    [Fact]
    public void ReplaceTextInRegion_AppliesTheChosenFontFamilyAndStyle()
    {
        byte[] pdf = TestPdfs.WithText(("plain text here", 72, 700, 14));
        var region = new RectRegion(1, 60, 690, 300, 30);

        var result = TextTools.ReplaceTextInRegion(pdf, region, "styled words",
            fontSize: 14, fontFamily: "times", bold: true, italic: true);

        // Re-reading the region detects the family/style that was stamped.
        var reread = TextTools.GetTextInRegion(result.Pdf, region);
        Assert.Contains("styled words", reread.Text);
        Assert.Equal("times", reread.FontFamily);
        Assert.True(reread.Bold);
        Assert.True(reread.Italic);
    }

    [Fact]
    public void ReplaceTextInRegion_WithColour_ProducesReadableText()
    {
        byte[] pdf = TestPdfs.WithText(("colour me", 72, 700, 14));

        var result = TextTools.ReplaceTextInRegion(pdf, new RectRegion(1, 60, 690, 300, 30),
            "red text", fontSize: 14, colorHex: "#ff0000");

        Assert.Contains("red text", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Theory]
    [InlineData("times", true, true)]
    [InlineData("courier", false, false)]
    [InlineData("helvetica", true, false)]
    public void ReplaceTextInRegion_EveryFamilyStyleCombination_StaysReadable(string family, bool bold, bool italic)
    {
        byte[] pdf = TestPdfs.WithText(("before", 72, 700, 14));

        var result = TextTools.ReplaceTextInRegion(pdf, new RectRegion(1, 60, 690, 300, 30),
            "after words", fontSize: 14, fontFamily: family, bold: bold, italic: italic);

        Assert.Contains("after words", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Fact]
    public void FindText_LocatesPhraseOnCorrectPage()
    {
        byte[] pdf = TestPdfs.MultiPage(3, "Chapter");

        var matches = TextTools.FindText(pdf, "Chapter 2");

        var match = Assert.Single(matches);
        Assert.Equal(2, match.Page);
        Assert.True(match.Width > 0 && match.Height > 0);
    }

    [Fact]
    public void FindText_NoMatch_ReturnsEmpty()
    {
        byte[] pdf = TestPdfs.WithText(("hello world", 72, 700, 12));
        Assert.Empty(TextTools.FindText(pdf, "absent"));
    }

    [Fact]
    public void ReplaceAll_ReplacesEveryOccurrence()
    {
        byte[] pdf = TestPdfs.WithText(
            ("ACME did the work for ACME", 72, 700, 12),
            ("Signed by ACME", 72, 650, 12));

        var (result, count) = TextTools.ReplaceAll(pdf, "ACME", "Globex");

        Assert.Equal(3, count);
        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.DoesNotContain("ACME", text);
        Assert.Contains("Globex", text);
    }

    [Fact]
    public void ReplaceAll_KeepsSurroundingWordsIntact()
    {
        byte[] pdf = TestPdfs.WithText(("Payable to ACME within 30 days", 72, 700, 12));

        var (result, count) = TextTools.ReplaceAll(pdf, "ACME", "Globex");

        Assert.Equal(1, count);
        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.Contains("Payable to", text);
        Assert.Contains("within 30 days", text);
        Assert.Contains("Globex", text);
    }
}
