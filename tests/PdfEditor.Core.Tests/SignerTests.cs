using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class SignerTests
{
    [Fact]
    public void CreateSelfSignedPkcs12_ProducesLoadablePkcs12()
    {
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Test Signer", "secret");
        Assert.NotEmpty(pfx);

        // Round-trips through the signing path without throwing.
        byte[] pdf = TestPdfs.WithText(("contract", 72, 700, 12));
        byte[] signed = Signer.SignDigitally(pdf, pfx, "secret");
        Assert.NotEmpty(signed);
    }

    [Fact]
    public void SignDigitally_Invisible_ProducesValidSignature()
    {
        byte[] pdf = TestPdfs.WithText(("contract body", 72, 700, 12));
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Alice Example", "pw");

        byte[] signed = Signer.SignDigitally(pdf, pfx, "pw", reason: "Approval", location: "Sydney");

        var signature = Assert.Single(Signer.GetSignatures(signed));
        Assert.True(signature.IntegrityValid);
        Assert.True(signature.CoversWholeDocument);
        Assert.Equal("Alice Example", signature.SignerName);
        Assert.Contains("contract body", TestPdfAssert.ExtractText(signed));
    }

    [Fact]
    public void SignDigitally_Visible_PlacesAppearanceOnPage()
    {
        byte[] pdf = TestPdfs.WithText(("contract body", 72, 700, 12));
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Bob Signer", "pw");

        byte[] signed = Signer.SignDigitally(pdf, pfx, "pw",
            placement: new RectRegion(1, 350, 80, 180, 60));

        var signature = Assert.Single(Signer.GetSignatures(signed));
        Assert.True(signature.IntegrityValid);

        // The visible appearance is a widget annotation on page 1 at the requested rect.
        using var doc = new iText.Kernel.Pdf.PdfDocument(
            new iText.Kernel.Pdf.PdfReader(new MemoryStream(signed)));
        var widget = Assert.Single(doc.GetPage(1).GetAnnotations());
        var rect = widget.GetRectangle().ToRectangle();
        Assert.Equal(350, rect.GetLeft(), 0.5);
        Assert.Equal(80, rect.GetBottom(), 0.5);
        Assert.Equal(180, rect.GetWidth(), 0.5);
        Assert.Equal(60, rect.GetHeight(), 0.5);
    }

    [Fact]
    public void TamperingAfterSigning_InvalidatesSignature()
    {
        byte[] pdf = TestPdfs.WithText(("contract body", 72, 700, 12));
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Alice", "pw");
        byte[] signed = Signer.SignDigitally(pdf, pfx, "pw");

        // Any post-signing edit must at minimum stop the signature covering the whole file.
        byte[] tampered = Signer.AddImageSignature(signed, new RectRegion(1, 10, 10, 50, 20),
            MakePng());

        var signature = Assert.Single(Signer.GetSignatures(tampered));
        Assert.False(signature.CoversWholeDocument);
    }

    [Fact]
    public void SignDigitally_WrongPkcs12Password_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Alice", "right");
        Assert.ThrowsAny<Exception>(() => Signer.SignDigitally(pdf, pfx, "wrong"));
    }

    [Fact]
    public void AddImageSignature_StampsImageAtPlacement()
    {
        byte[] pdf = TestPdfs.WithText(("sign here:", 72, 200, 12));
        Assert.Equal(0, TestPdfAssert.CountImages(pdf));

        byte[] signed = Signer.AddImageSignature(pdf,
            new RectRegion(1, 150, 180, 120, 40), MakePng());

        Assert.Equal(1, TestPdfAssert.CountImages(signed));
        Assert.Contains("sign here", TestPdfAssert.ExtractText(signed));
    }

    private static byte[] MakePng()
    {
        using var bitmap = new SkiaSharp.SKBitmap(40, 20);
        using (var c = new SkiaSharp.SKCanvas(bitmap)) c.Clear(SkiaSharp.SKColors.Blue);
        using var img = SkiaSharp.SKImage.FromBitmap(bitmap);
        return img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }
}
