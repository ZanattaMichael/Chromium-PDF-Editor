using PdfEditor.Core;

namespace PdfEditor.Tests;

public class PageToolsTests
{
    [Fact]
    public void Rotate_AddsToExistingRotation_ForTheNamedPages()
    {
        byte[] pdf = TestPdfs.MultiPage(3);

        var result = PageTools.Rotate(pdf, new[] { 2 }, 90);

        var info = PdfInspector.GetInfo(result.Pdf);
        Assert.Equal(0, info.Pages[0].Rotation);
        Assert.Equal(90, info.Pages[1].Rotation);
        Assert.Equal(0, info.Pages[2].Rotation);
    }

    [Fact]
    public void Rotate_EmptyPageList_RotatesEveryPage()
    {
        byte[] pdf = TestPdfs.MultiPage(2);

        var result = PageTools.Rotate(pdf, Array.Empty<int>(), 180);

        var info = PdfInspector.GetInfo(result.Pdf);
        Assert.All(info.Pages, pg => Assert.Equal(180, pg.Rotation));
    }

    [Fact]
    public void Rotate_WrapsAround_ModuloThreeSixty()
    {
        byte[] pdf = TestPdfs.MultiPage(1);

        // 270 + 180 = 450 -> 90
        var once = PageTools.Rotate(pdf, new[] { 1 }, 270);
        var twice = PageTools.Rotate(once.Pdf, new[] { 1 }, 180);

        Assert.Equal(90, PdfInspector.GetInfo(twice.Pdf).Pages[0].Rotation);
    }

    [Fact]
    public void Rotate_NegativeDelta_TurnsCounterClockwise()
    {
        byte[] pdf = TestPdfs.MultiPage(1);

        var result = PageTools.Rotate(pdf, new[] { 1 }, -90);

        Assert.Equal(270, PdfInspector.GetInfo(result.Pdf).Pages[0].Rotation);
    }

    [Fact]
    public void Rotate_NonMultipleOf90_Throws()
    {
        byte[] pdf = TestPdfs.MultiPage(1);
        Assert.Throws<ArgumentException>(() => PageTools.Rotate(pdf, new[] { 1 }, 45));
    }

    [Fact]
    public void Rotate_InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.MultiPage(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageTools.Rotate(pdf, new[] { 5 }, 90));
    }

    [Fact]
    public void Rotate_PreservesPageContent()
    {
        byte[] pdf = TestPdfs.MultiPage(2, "Confidential");

        var result = PageTools.Rotate(pdf, new[] { 1 }, 90);

        Assert.Contains("Confidential 1", TestPdfAssert.ExtractText(result.Pdf, 1));
    }
}
