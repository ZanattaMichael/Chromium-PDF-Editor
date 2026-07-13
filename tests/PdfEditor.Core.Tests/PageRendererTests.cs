using PdfEditor.Core;
using SkiaSharp;
using Xunit;

namespace PdfEditor.Tests;

public class PageRendererTests
{
    [Fact]
    public void RendersPageToPng_WithExpectedDimensions()
    {
        byte[] pdf = TestPdfs.WithText(("render me", 72, 700, 12));

        byte[] png = PageRenderer.RenderPagePng(pdf, 1, dpi: 72);

        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        // A4 at 72 dpi is 595 x 842 points → pixels.
        Assert.InRange(bitmap.Width, 590, 600);
        Assert.InRange(bitmap.Height, 837, 847);
    }

    [Fact]
    public void HigherDpi_ProducesLargerImage()
    {
        byte[] pdf = TestPdfs.WithText(("render me", 72, 700, 12));

        using var low = SKBitmap.Decode(PageRenderer.RenderPagePng(pdf, 1, 72));
        using var high = SKBitmap.Decode(PageRenderer.RenderPagePng(pdf, 1, 144));

        Assert.InRange((float)high.Width / low.Width, 1.9f, 2.1f);
    }

    [Fact]
    public void RendersEncryptedPdf_WithPassword()
    {
        byte[] pdf = Encryptor.Encrypt(TestPdfs.WithText(("secret", 72, 700, 12)), "pw");

        byte[] png = PageRenderer.RenderPagePng(pdf, 1, 72, password: "pw");

        Assert.NotNull(SKBitmap.Decode(png));
    }

    [Fact]
    public void PdfInspector_ReportsPageGeometry()
    {
        byte[] pdf = TestPdfs.MultiPage(4);
        var info = PdfInspector.GetInfo(pdf);

        Assert.Equal(4, info.PageCount);
        Assert.False(info.IsEncrypted);
        Assert.All(info.Pages, p =>
        {
            Assert.Equal(TestPdfs.PageWidth, p.Width);
            Assert.Equal(TestPdfs.PageHeight, p.Height);
        });
    }
}
