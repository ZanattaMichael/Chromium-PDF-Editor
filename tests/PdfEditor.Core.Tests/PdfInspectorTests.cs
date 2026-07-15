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
    }
}
