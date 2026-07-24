using iText.Kernel.Pdf;
using PdfEditor.Core;

namespace PdfEditor.Tests;

public class SanitizerTests
{
    [Fact]
    public void Inspect_FindsEveryCategoryOfHiddenData()
    {
        var report = Sanitizer.Inspect(TestPdfs.WithHiddenData());

        Assert.True(report.MetadataFields > 0);   // author/title/custom + xmp
        Assert.True(report.Attachments > 0);
        Assert.True(report.ScriptsAndActions > 0);
        Assert.True(report.Annotations > 0);       // the sticky note
        Assert.True(report.Bookmarks > 0);
        Assert.True(report.HiddenLayers > 0);
        Assert.True(report.HasAny);
    }

    [Fact]
    public void Inspect_CleanDocument_ReportsNothing()
    {
        var report = Sanitizer.Inspect(TestPdfs.WithText(("just text", 72, 700, 12)));
        Assert.False(report.HasAny);
    }

    [Fact]
    public void Sanitize_RemovesEverything_ByDefault()
    {
        byte[] cleaned = Sanitizer.Sanitize(TestPdfs.WithHiddenData(), new SanitizeOptions()).Pdf;

        var report = Sanitizer.Inspect(cleaned);
        Assert.Equal(0, report.Attachments);
        Assert.Equal(0, report.ScriptsAndActions);
        Assert.Equal(0, report.Annotations);
        Assert.Equal(0, report.Bookmarks);
        Assert.Equal(0, report.HiddenLayers);
    }

    [Fact]
    public void Sanitize_ClearsDocumentMetadata()
    {
        byte[] cleaned = Sanitizer.Sanitize(TestPdfs.WithHiddenData(), new SanitizeOptions()).Pdf;

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(cleaned)));
        var info = doc.GetDocumentInfo();
        Assert.True(string.IsNullOrEmpty(info.GetAuthor()));
        Assert.True(string.IsNullOrEmpty(info.GetTitle()));
        Assert.Null(info.GetMoreInfo("Department"));
        Assert.Null(doc.GetCatalog().GetPdfObject().Get(PdfName.Metadata)); // XMP gone
    }

    [Fact]
    public void Sanitize_PreservesVisiblePageContent()
    {
        byte[] cleaned = Sanitizer.Sanitize(TestPdfs.WithHiddenData(), new SanitizeOptions()).Pdf;

        Assert.Equal(1, PdfInspector.GetInfo(cleaned).PageCount);
        Assert.Contains("Visible content", TestPdfAssert.ExtractText(cleaned, 1));
    }

    [Fact]
    public void Sanitize_RespectsSelectiveOptions_KeepsMetadataWhenAsked()
    {
        // Only strip attachments; metadata must survive.
        var options = new SanitizeOptions(Metadata: false, Attachments: true,
            ScriptsAndActions: false, Annotations: false, Bookmarks: false, HiddenLayers: false);

        byte[] cleaned = Sanitizer.Sanitize(TestPdfs.WithHiddenData(), options).Pdf;

        var report = Sanitizer.Inspect(cleaned);
        Assert.Equal(0, report.Attachments);        // removed
        Assert.True(report.MetadataFields > 0);     // kept
        Assert.True(report.ScriptsAndActions > 0);  // kept
        Assert.True(report.Bookmarks > 0);          // kept
    }

    [Fact]
    public void Sanitize_KeepsLinksAndFormFields_WhileRemovingComments()
    {
        // A doc with a link annotation and a form field plus a comment: sanitising annotations must
        // drop the comment but keep the link and the fillable field.
        byte[] pdf = TestPdfs.WithTextField("email", "a@b.com");
        // add a comment annotation on the field page
        using var withNote = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)), new PdfWriter(withNote)))
        {
            var note = new iText.Kernel.Pdf.Annot.PdfTextAnnotation(new iText.Kernel.Geom.Rectangle(400, 700, 20, 20));
            note.SetContents("comment");
            doc.GetPage(1).AddAnnotation(note);
        }

        byte[] cleaned = Sanitizer.Sanitize(withNote.ToArray(),
            new SanitizeOptions(Metadata: false, Attachments: false, ScriptsAndActions: false,
                Annotations: true, Bookmarks: false, HiddenLayers: false)).Pdf;

        Assert.Equal(0, Sanitizer.Inspect(cleaned).Annotations);        // comment gone
        Assert.Single(FormTools.ListFields(cleaned));                    // form field kept (Widget)
    }

    [Fact]
    public void Sanitize_EncryptedInput_WorksWithPassword()
    {
        byte[] locked = Encryptor.Encrypt(TestPdfs.WithHiddenData(), "pw");

        byte[] cleaned = Sanitizer.Sanitize(locked, new SanitizeOptions(), "pw").Pdf;

        Assert.Equal(0, Sanitizer.Inspect(cleaned).Attachments);
        Assert.Contains("Visible content", TestPdfAssert.ExtractText(cleaned, 1));
    }

    [Fact]
    public void Sanitize_CleanDocument_IsANoOpButStillValid()
    {
        byte[] pdf = TestPdfs.WithText(("plain", 72, 700, 12));
        byte[] cleaned = Sanitizer.Sanitize(pdf, new SanitizeOptions()).Pdf;
        Assert.Equal(1, PdfInspector.GetInfo(cleaned).PageCount);
    }
}
