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
