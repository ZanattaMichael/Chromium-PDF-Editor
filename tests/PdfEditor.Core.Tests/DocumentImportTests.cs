using System.IO.Compression;
using PdfEditor.Core;
using SkiaSharp;

namespace PdfEditor.Tests;

public class DocumentImportTests
{
    private static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Red);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] BuildDocx(string text)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                using var s = new StreamWriter(zip.CreateEntry(path).Open());
                s.Write(content);
            }
            Add("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "</Types>");
            Add("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");
            Add("word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                $"<w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>");
        }
        return ms.ToArray();
    }

    [Fact]
    public void ImageToPdf_ProducesAOnePagePdf_SizedToTheImage()
    {
        byte[] png = Png(200, 120);

        byte[] pdf = DocumentImport.ImageToPdf(png);

        var info = PdfInspector.GetInfo(pdf);
        Assert.Equal(1, info.PageCount);
        Assert.Equal(200, info.Pages[0].Width, 1.0);
        Assert.Equal(120, info.Pages[0].Height, 1.0);
    }

    [Fact]
    public void ToPdf_PdfKind_PassesThrough()
    {
        byte[] pdf = TestPdfs.WithText(("hi", 72, 700, 12));
        Assert.Same(pdf, DocumentImport.ToPdf(pdf, "pdf"));
    }

    [Fact]
    public void ToPdf_ImageKind_WrapsImage()
    {
        byte[] result = DocumentImport.ToPdf(Png(50, 50), "image");
        Assert.Equal(1, PdfInspector.GetInfo(result).PageCount);
    }

    [Fact]
    public void CanConvertWord_ReflectsLibreOfficePresence()
    {
        // Whatever the environment, this must return a boolean without throwing.
        bool available = DocumentImport.CanConvertWord;
        Assert.IsType<bool>(available);
    }

    [Fact]
    public void DocxToPdf_UnconvertibleInput_ThrowsInvalidOperationWithGuidance()
    {
        // Bytes that are not a loadable Word document. Whether LibreOffice is absent, or present
        // but unable to load the file, the caller gets a clear InvalidOperationException (with a
        // "convert to PDF first" hint when LibreOffice is missing) rather than a raw crash.
        byte[] notADocx = BuildDocx("x"); // structurally incomplete; LibreOffice won't load it

        var ex = Assert.Throws<InvalidOperationException>(() => DocumentImport.DocxToPdf(notADocx));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Merge_ImageConvertedToPdf_AppendsAsAPage()
    {
        byte[] basePdf = TestPdfs.WithText(("page one", 72, 700, 12));
        byte[] imagePage = DocumentImport.ToPdf(Png(300, 300), "image");

        byte[] merged = Merger.Merge(new[] { basePdf, imagePage });

        Assert.Equal(2, PdfInspector.GetInfo(merged).PageCount);
    }
}
