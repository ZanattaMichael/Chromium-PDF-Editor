using iText.Forms;
using iText.Forms.Fields;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;

namespace PdfEditor.NativeHost.Tests;

/// <summary>Tiny deterministic PDF fixtures for exercising the message dispatcher in-process.</summary>
internal static class TestPdf
{
    public static byte[] OnePage(string text = "hello")
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(595, 842));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            new PdfCanvas(page).BeginText().SetFontAndSize(font, 14)
                .MoveText(72, 700).ShowText(text).EndText();
        }
        return output.ToArray();
    }

    public static byte[] ManyPages(int count)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            for (int i = 0; i < count; i++)
            {
                var page = doc.AddNewPage(new PageSize(595, 842));
                new PdfCanvas(page).BeginText().SetFontAndSize(font, 14)
                    .MoveText(72, 700).ShowText($"Page {i}").EndText();
            }
        }
        return output.ToArray();
    }

    public static byte[] WithField(string name = "field1", string value = "")
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            doc.AddNewPage(new PageSize(595, 842));
            var form = PdfFormCreator.GetAcroForm(doc, true);
            var field = new TextFormFieldBuilder(doc, name)
                .SetWidgetRectangle(new Rectangle(100, 600, 200, 24)).CreateText();
            field.SetValue(value);
            form.AddField(field);
        }
        return output.ToArray();
    }

    public static byte[] WithJavaScript()
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            doc.AddNewPage(new PageSize(595, 842));
            doc.GetCatalog().SetOpenAction(PdfAction.CreateJavaScript("app.alert('x');"));
        }
        return output.ToArray();
    }

    public static byte[] WithLink(string url = "https://github.com/example/repo")
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(595, 842));
            var link = new PdfLinkAnnotation(new Rectangle(72, 700, 200, 20));
            link.SetAction(PdfAction.CreateURI(url));
            page.AddAnnotation(link);
        }
        return output.ToArray();
    }

    public static string Base64(byte[] pdf) => Convert.ToBase64String(pdf);
}
