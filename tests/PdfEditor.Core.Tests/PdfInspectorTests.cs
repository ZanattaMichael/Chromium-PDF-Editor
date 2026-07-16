using iText.Kernel.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using PdfEditor.Core;

namespace PdfEditor.Tests;

public class PdfInspectorTests
{
    [Fact]
    public void OriginZeroPage_ReportsZeroOffset()
    {
        byte[] pdf = TestPdfs.WithText(("hello", 72, 700, 14));

        var info = PdfInspector.GetInfo(pdf);

        Assert.Equal(0, info.Pages[0].X);
        Assert.Equal(0, info.Pages[0].Y);
        Assert.Equal(TestPdfs.PageWidth, info.Pages[0].Width);
        Assert.Equal(TestPdfs.PageHeight, info.Pages[0].Height);
    }

    [Fact]
    public void NonZeroOriginPage_ExposesTheBoxOrigin()
    {
        // A page whose MediaBox is [100 200 500 700] — origin (100,200), size 400x500.
        // The viewer needs this origin: the rendered image's bottom-left corresponds to
        // (100,200) in user space, not (0,0), so without it screen->document mapping (and
        // therefore redaction placement) is offset by the origin.
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(new PageSize(new Rectangle(100, 200, 400, 500)));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            new PdfCanvas(page).BeginText().SetFontAndSize(font, 14)
                .MoveText(150, 650).ShowText("offset").EndText();
        }

        var info = PdfInspector.GetInfo(ms.ToArray());

        Assert.Equal(100, info.Pages[0].X);
        Assert.Equal(200, info.Pages[0].Y);
        Assert.Equal(400, info.Pages[0].Width);
        Assert.Equal(500, info.Pages[0].Height);
        Assert.Equal(0, info.Pages[0].Rotation);
    }

    [Fact]
    public void ReportsTheCropBox_NotTheMediaBox()
    {
        // PDFium renders the crop box; the geometry the viewer gets must match it, otherwise
        // every mapped coordinate is wrong for any document that sets a crop box.
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(new PageSize(new Rectangle(0, 0, 595, 842)));
            page.SetCropBox(new Rectangle(50, 60, 400, 500));
        }

        var info = PdfInspector.GetInfo(ms.ToArray());

        Assert.Equal(50, info.Pages[0].X);
        Assert.Equal(60, info.Pages[0].Y);
        Assert.Equal(400, info.Pages[0].Width);
        Assert.Equal(500, info.Pages[0].Height);
    }

    [Fact]
    public void ClampsACropBoxThatExceedsTheMediaBox_ToWhatIsRendered()
    {
        // A crop box larger than / offset beyond the media box: the renderer only shows the
        // intersection, so that is what must be reported (not iText's unclamped crop box).
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(new PageSize(new Rectangle(0, 0, 612, 792)));
            page.SetCropBox(new Rectangle(-50, -50, 712, 892)); // extends past the media box every side
        }

        var info = PdfInspector.GetInfo(ms.ToArray());

        Assert.Equal(0, info.Pages[0].X);
        Assert.Equal(0, info.Pages[0].Y);
        Assert.Equal(612, info.Pages[0].Width);
        Assert.Equal(792, info.Pages[0].Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ExposesThePageRotation(int rotation)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(new PageSize(new Rectangle(0, 0, 595, 842)));
            if (rotation != 0) page.SetRotation(rotation);
        }

        var info = PdfInspector.GetInfo(ms.ToArray());

        Assert.Equal(rotation, info.Pages[0].Rotation);
    }
}
