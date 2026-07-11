using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Canvas;
using SkiaSharp;

namespace PdfEditor.Tests;

/// <summary>Builds small, deterministic PDFs for tests.</summary>
public static class TestPdfs
{
    public const float PageWidth = 595;   // A4 in points
    public const float PageHeight = 842;

    /// <summary>A single page with absolutely positioned text lines.</summary>
    public static byte[] WithText(params (string Text, float X, float Y, float Size)[] lines)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var canvas = new PdfCanvas(page);
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            foreach (var (text, x, y, size) in lines)
            {
                canvas.BeginText().SetFontAndSize(font, size)
                    .MoveText(x, y).ShowText(text).EndText();
            }
        }
        return output.ToArray();
    }

    /// <summary>A document with the given number of pages, each labelled.</summary>
    public static byte[] MultiPage(int pages, string labelPrefix = "Page")
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            for (int i = 1; i <= pages; i++)
            {
                var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
                new PdfCanvas(page).BeginText().SetFontAndSize(font, 14)
                    .MoveText(72, 770).ShowText($"{labelPrefix} {i}").EndText();
            }
        }
        return output.ToArray();
    }

    /// <summary>A page with a solid-colour raster image drawn into the given rectangle.</summary>
    public static byte[] WithImage(float x, float y, float width, float height)
    {
        using var bitmap = new SKBitmap(60, 40);
        using (var c = new SKCanvas(bitmap)) c.Clear(SKColors.Red);
        using var img = SKImage.FromBitmap(bitmap);
        byte[] png = img.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var canvas = new PdfCanvas(page);
            canvas.AddImageFittedIntoRectangle(ImageDataFactory.Create(png),
                new Rectangle(x, y, width, height), false);
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            canvas.BeginText().SetFontAndSize(font, 12)
                .MoveText(72, 800).ShowText("Document with image").EndText();
        }
        return output.ToArray();
    }

    /// <summary>A page with a link annotation covering the given rectangle.</summary>
    public static byte[] WithLinkAnnotation(float x, float y, float width, float height)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            new PdfCanvas(page).BeginText().SetFontAndSize(font, 12)
                .MoveText(x, y + 4).ShowText("clickable link").EndText();
            var link = new PdfLinkAnnotation(new Rectangle(x, y, width, height));
            link.SetAction(PdfAction.CreateURI("https://example.com"));
            page.AddAnnotation(link);
        }
        return output.ToArray();
    }
}
