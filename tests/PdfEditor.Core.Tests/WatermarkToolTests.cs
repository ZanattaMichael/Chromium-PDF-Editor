using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class WatermarkToolTests
{
    [Fact]
    public void AddTextWatermark_StampsTheTextOnEveryPage()
    {
        byte[] pdf = TestPdfs.MultiPage(3);

        var result = WatermarkTool.AddTextWatermark(pdf, "CONFIDENTIAL");

        for (int page = 1; page <= 3; page++)
            Assert.Contains("CONFIDENTIAL", TestPdfAssert.ExtractText(result.Pdf, page));
    }

    [Fact]
    public void AddTextWatermark_OnlyStampsTheNamedPages()
    {
        byte[] pdf = TestPdfs.MultiPage(3);

        var result = WatermarkTool.AddTextWatermark(pdf, "DRAFT", new WatermarkOptions(Pages: new[] { 2 }));

        Assert.DoesNotContain("DRAFT", TestPdfAssert.ExtractText(result.Pdf, 1));
        Assert.Contains("DRAFT", TestPdfAssert.ExtractText(result.Pdf, 2));
        Assert.DoesNotContain("DRAFT", TestPdfAssert.ExtractText(result.Pdf, 3));
    }

    [Fact]
    public void AddTextWatermark_ActuallyPaintsInkOntoThePage()
    {
        // Text sits low on the page; the watermark is centred, so the middle band is blank until stamped.
        // Use an opaque black mark so the pixels clear InkFraction's darkness threshold (it only
        // counts pixels below 128 — a faint grey at low opacity renders but stays light).
        byte[] pdf = TestPdfs.WithText(("body", 72, 60, 12));
        Assert.Equal(0d, TestPdfAssert.InkFraction(pdf, 1, 120, 350, 470, 490, 150), 3);

        var result = WatermarkTool.AddTextWatermark(pdf, "SECRET",
            new WatermarkOptions(ColorHex: "#000000", Opacity: 1f));

        Assert.True(TestPdfAssert.InkFraction(result.Pdf, 1, 120, 350, 470, 490, 150) > 0,
            "the watermark should add visible ink to the centre of the page");
    }

    [Fact]
    public void AddTextWatermark_EmptyText_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        Assert.Throws<System.ArgumentException>(() => WatermarkTool.AddTextWatermark(pdf, "  "));
    }

    [Fact]
    public void AddTextWatermark_OutOfRangePages_AreIgnored_LeavingTheDocumentUnstamped()
    {
        byte[] pdf = TestPdfs.MultiPage(2);

        var result = WatermarkTool.AddTextWatermark(pdf, "NOPE", new WatermarkOptions(Pages: new[] { 99 }));

        Assert.DoesNotContain("NOPE", TestPdfAssert.ExtractText(result.Pdf, 1));
        Assert.DoesNotContain("NOPE", TestPdfAssert.ExtractText(result.Pdf, 2));
    }
}
