using PdfEditor.Core;

namespace PdfEditor.Tests;

/// <summary>
/// OCR depends on an external Tesseract install (like Word import depends on LibreOffice). These
/// tests adapt to the environment: where Tesseract is present the happy path is exercised; where
/// it is absent the caller must get a clear, actionable error — never a crash.
/// </summary>
public class OcrToolTests
{
    [Fact]
    public void CanOcr_ReturnsABoolean_WithoutThrowing()
    {
        bool available = OcrTool.CanOcr;
        Assert.IsType<bool>(available);
    }

    [Fact]
    public void MakeSearchable_BehavesPerTesseractAvailability()
    {
        byte[] pdf = TestPdfs.WithText(("Scanned looking text", 72, 700, 14));

        if (!OcrTool.CanOcr)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => OcrTool.MakeSearchable(pdf));
            Assert.Contains("Tesseract", ex.Message);
        }
        else
        {
            byte[] result = OcrTool.MakeSearchable(pdf);
            Assert.Equal(1, PdfInspector.GetInfo(result).PageCount);
        }
    }

    [Fact]
    public void ExtractText_BehavesPerTesseractAvailability()
    {
        byte[] pdf = TestPdfs.WithText(("HELLO OCR WORLD", 72, 700, 24));

        if (!OcrTool.CanOcr)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => OcrTool.ExtractText(pdf, 1));
            Assert.Contains("Tesseract", ex.Message);
        }
        else
        {
            string text = OcrTool.ExtractText(pdf, 1);
            Assert.Contains("OCR", text.ToUpperInvariant());
        }
    }

    [Fact]
    public void ExtractText_InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("one page", 72, 700, 12));
        if (!OcrTool.CanOcr) return; // page validation runs after the Tesseract check

        Assert.Throws<ArgumentOutOfRangeException>(() => OcrTool.ExtractText(pdf, 9));
    }
}
