using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class BatesToolTests
{
    [Fact]
    public void AddBatesNumbers_StampsSequential_ZeroPadded_LabelsOnEveryPage()
    {
        byte[] pdf = TestPdfs.MultiPage(3);

        var result = BatesTool.AddBatesNumbers(pdf, new BatesOptions(Prefix: "ACME", Start: 1, Digits: 6));

        Assert.Equal("ACME000001", result.FirstLabel);
        Assert.Equal("ACME000003", result.LastLabel);
        Assert.Contains("ACME000001", TestPdfAssert.ExtractText(result.Pdf, 1));
        Assert.Contains("ACME000002", TestPdfAssert.ExtractText(result.Pdf, 2));
        Assert.Contains("ACME000003", TestPdfAssert.ExtractText(result.Pdf, 3));
    }

    [Fact]
    public void AddBatesNumbers_HonoursStartOffsetAndSuffix()
    {
        byte[] pdf = TestPdfs.MultiPage(2);

        var result = BatesTool.AddBatesNumbers(pdf,
            new BatesOptions(Prefix: "DOC", Suffix: "-X", Start: 100, Digits: 4));

        Assert.Equal("DOC0100-X", result.FirstLabel);
        Assert.Equal("DOC0101-X", result.LastLabel);
        Assert.Contains("DOC0100-X", TestPdfAssert.ExtractText(result.Pdf, 1));
        Assert.Contains("DOC0101-X", TestPdfAssert.ExtractText(result.Pdf, 2));
    }

    [Fact]
    public void AddBatesNumbers_OnlyStampsNamedPages_NumberedByStampOrder()
    {
        byte[] pdf = TestPdfs.MultiPage(3);

        // Only pages 1 and 3 are stamped; the counter increments per stamped page, not per document page.
        var result = BatesTool.AddBatesNumbers(pdf, new BatesOptions(Start: 1, Digits: 3, Pages: new[] { 1, 3 }));

        Assert.Contains("001", TestPdfAssert.ExtractText(result.Pdf, 1));
        Assert.DoesNotContain("002", TestPdfAssert.ExtractText(result.Pdf, 2));
        Assert.Contains("002", TestPdfAssert.ExtractText(result.Pdf, 3));
    }

    [Fact]
    public void AddBatesNumbers_PaintsInkInTheChosenCorner()
    {
        byte[] pdf = TestPdfs.WithText(("body", 72, 400, 12)); // A4, text mid-page

        var result = BatesTool.AddBatesNumbers(pdf,
            new BatesOptions(Prefix: "N", Start: 1, Digits: 6, Position: BatesTool.Corner.BottomRight));

        // The bottom-right corner band (inset ~24pt) gains ink; the bottom-left stays blank.
        Assert.True(TestPdfAssert.InkFraction(result.Pdf, 1, 470, 20, 585, 40, 200) > 0,
            "expected the Bates number in the bottom-right corner");
        Assert.Equal(0d, TestPdfAssert.InkFraction(result.Pdf, 1, 20, 20, 120, 40, 200), 3);
    }

    [Theory]
    [InlineData(BatesTool.Corner.TopLeft)]
    [InlineData(BatesTool.Corner.TopCenter)]
    [InlineData(BatesTool.Corner.BottomLeft)]
    [InlineData(BatesTool.Corner.BottomCenter)]
    public void AddBatesNumbers_PlacesTheLabelInEveryCorner(BatesTool.Corner corner)
    {
        byte[] pdf = TestPdfs.WithText(("body", 72, 400, 12));

        var result = BatesTool.AddBatesNumbers(pdf, new BatesOptions(Prefix: "N", Position: corner));

        // Whatever the corner, the label is stamped and extractable.
        Assert.Contains("N000001", TestPdfAssert.ExtractText(result.Pdf, 1));
    }

    [Fact]
    public void AddBatesNumbers_NegativeStart_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            BatesTool.AddBatesNumbers(pdf, new BatesOptions(Start: -1)));
    }

    [Theory]
    [InlineData("bottom-left", BatesTool.Corner.BottomLeft)]
    [InlineData("TOP_RIGHT", BatesTool.Corner.TopRight)]
    [InlineData("topcentre", BatesTool.Corner.TopCenter)]
    [InlineData("nonsense", BatesTool.Corner.BottomRight)]
    public void ParseCorner_IsForgivingAboutCasingAndSeparators(string input, BatesTool.Corner expected)
    {
        Assert.Equal(expected, BatesTool.ParseCorner(input));
    }
}
