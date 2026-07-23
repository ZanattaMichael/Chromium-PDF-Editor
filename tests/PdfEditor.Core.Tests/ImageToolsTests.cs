using PdfEditor.Core;
using SkiaSharp;

namespace PdfEditor.Tests;

public class ImageToolsTests
{
    // The colour of the rendered page at a PDF user-space point.
    private static SKColor PixelAt(byte[] pdf, float x, float y) =>
        TestPdfAssert.PixelAt(pdf, 1, x, y, dpi: 150);

    [Fact]
    public void MoveImage_RelocatesTheImage_ClearingTheOriginalSpot()
    {
        // WithImage draws a solid red image at (100,700) sized 60x40 on white paper.
        byte[] pdf = TestPdfs.WithImage(100, 700, 60, 40);
        Assert.Equal(SKColors.Red, PixelAt(pdf, 130, 720));

        // Move it right 50 and down 300 (dy negative in PDF space).
        var result = ImageTools.MoveImage(pdf, 1, new RectRegion(1, 100, 700, 60, 40), dx: 50, dy: -300);

        // Gone from the original spot (now white)...
        var oldSpot = PixelAt(result.Pdf, 130, 720);
        Assert.True(oldSpot.Red > 240 && oldSpot.Green > 240 && oldSpot.Blue > 240);
        // ...and present at the shifted spot (red again).
        Assert.Equal(SKColors.Red, PixelAt(result.Pdf, 180, 420));
    }

    [Fact]
    public void MoveImage_NoImageInRegion_IsANoOp()
    {
        byte[] pdf = TestPdfs.WithImage(100, 700, 60, 40);

        // A region far from the image finds nothing to move.
        var result = ImageTools.MoveImage(pdf, 1, new RectRegion(1, 300, 200, 40, 40), 10, 10);

        // The image is still where it started.
        Assert.Equal(SKColors.Red, PixelAt(result.Pdf, 130, 720));
    }

    [Fact]
    public void MoveImage_InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.WithImage(100, 700, 60, 40);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImageTools.MoveImage(pdf, 9, new RectRegion(9, 0, 0, 10, 10), 5, 5));
    }
}
