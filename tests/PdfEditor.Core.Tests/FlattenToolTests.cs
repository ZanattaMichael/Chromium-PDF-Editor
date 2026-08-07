using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class FlattenToolTests
{
    /// <summary>A page with a red square markup annotation that carries a normal appearance stream.</summary>
    private static byte[] WithSquareAnnotation(Rectangle rect)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(595, 842));
            var annot = new PdfSquareAnnotation(rect);
            var ap = new PdfFormXObject(new Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()));
            new PdfCanvas(ap, doc).SetFillColor(ColorConstants.RED)
                .Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()).Fill();
            annot.SetNormalAppearance(ap.GetPdfObject());
            annot.SetFlags(PdfAnnotation.PRINT);
            page.AddAnnotation(annot);
        }
        return output.ToArray();
    }

    private static int FormFieldCount(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var form = iText.Forms.Fields.PdfFormCreator.GetAcroForm(doc, false);
        return form?.GetAllFormFields().Count ?? 0;
    }

    private static int AnnotationCount(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return doc.GetPage(1).GetAnnotations().Count;
    }

    [Fact]
    public void Flatten_Forms_MakesFieldsStatic()
    {
        byte[] pdf = TestPdfs.WithTextField("name", "Jane Doe");
        Assert.Equal(1, FormFieldCount(pdf));

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.Forms);

        Assert.Equal(1, result.FormFieldsFlattened);
        Assert.Equal(0, FormFieldCount(result.Pdf));
    }

    [Fact]
    public void Flatten_AnnotationsOnly_BakesTheAppearance_AndRemovesTheAnnotation()
    {
        byte[] pdf = WithSquareAnnotation(new Rectangle(100, 600, 80, 50));
        Assert.Equal(1, AnnotationCount(pdf));

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.AnnotationsOnly);

        Assert.Equal(1, result.AnnotationsFlattened);
        Assert.Equal(0, AnnotationCount(result.Pdf));
        // The red appearance is now part of the page content.
        var px = TestPdfAssert.PixelAt(result.Pdf, 1, 140, 625, 150);
        Assert.True(px.Red > 180 && px.Green < 100 && px.Blue < 100, $"expected baked-in red, got {px}");
    }

    [Fact]
    public void Flatten_AnnotationsOnly_LeavesFormFieldsInteractive()
    {
        byte[] pdf = TestPdfs.WithTextField("name", "Jane");

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.AnnotationsOnly);

        Assert.Equal(0, result.AnnotationsFlattened); // a widget is a form field, not a markup annotation
        Assert.Equal(1, FormFieldCount(result.Pdf));  // still fillable
    }

    [Fact]
    public void Flatten_Everything_FlattensFormsAndAnnotations()
    {
        // A document with both a form field and a markup annotation.
        byte[] withField = TestPdfs.WithTextField("name", "Jane");
        byte[] pdf = AddSquareAnnotationTo(withField, new Rectangle(300, 600, 80, 50));

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.Everything);

        Assert.Equal(1, result.FormFieldsFlattened);
        Assert.Equal(1, result.AnnotationsFlattened);
        Assert.Equal(0, FormFieldCount(result.Pdf));
        Assert.Equal(0, AnnotationCount(result.Pdf));
    }

    [Fact]
    public void Flatten_LinkAnnotation_IsLeftAlone()
    {
        byte[] pdf = TestPdfs.WithLinkAnnotation(80, 500, 160, 20);

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.AnnotationsOnly);

        Assert.Equal(0, result.AnnotationsFlattened); // links carry no bakeable appearance
        Assert.Equal(1, AnnotationCount(result.Pdf));
    }

    private static byte[] AddSquareAnnotationTo(byte[] pdf, Rectangle rect)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)), new PdfWriter(output)))
        {
            var ap = new PdfFormXObject(new Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()));
            new PdfCanvas(ap, doc).SetFillColor(ColorConstants.RED)
                .Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()).Fill();
            var annot = new PdfSquareAnnotation(rect);
            annot.SetNormalAppearance(ap.GetPdfObject());
            annot.SetFlags(PdfAnnotation.PRINT);
            doc.GetPage(1).AddAnnotation(annot);
        }
        return output.ToArray();
    }
}
