using iText.Kernel.Pdf;
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
    public void SignDigitally_PasswordProtectedDocument_UsesThePdfPassword()
    {
        byte[] pdf = Encryptor.Encrypt(TestPdfs.WithText(("contract body", 72, 700, 12)), "docpw");
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Alice", "pw");

        byte[] signed = Signer.SignDigitally(pdf, pfx, "pw", pdfPassword: "docpw");

        var signature = Assert.Single(Signer.GetSignatures(signed, "docpw"));
        Assert.True(signature.IntegrityValid);
    }

    [Fact]
    public void SignDigitally_Visible_WithAppearanceImage_UsesTheSuppliedImage()
    {
        byte[] pdf = TestPdfs.WithText(("contract body", 72, 700, 12));
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Carol Signer", "pw");

        byte[] signed = Signer.SignDigitally(pdf, pfx, "pw",
            placement: new RectRegion(1, 350, 80, 180, 60), appearanceImage: MakePng());

        var signature = Assert.Single(Signer.GetSignatures(signed));
        Assert.True(signature.IntegrityValid);

        // The appearance image lives in the widget's own appearance stream, not the page's
        // content stream, so it must be found by walking the annotation's /AP /N resources
        // (possibly through nested form XObjects) rather than via TestPdfAssert.CountImages.
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(signed)));
        var widget = Assert.Single(doc.GetPage(1).GetAnnotations());
        var appearance = widget.GetPdfObject().GetAsDictionary(PdfName.AP)?.GetAsStream(PdfName.N);
        Assert.NotNull(appearance);
        var resources = appearance!.GetAsDictionary(PdfName.Resources);
        Assert.NotNull(resources);
        Assert.True(AppearanceContainsImage(resources!), "expected the signature appearance to embed an image");
    }

    private static bool AppearanceContainsImage(PdfDictionary resources)
    {
        var xobjects = resources.GetAsDictionary(PdfName.XObject);
        if (xobjects == null) return false;
        foreach (var key in xobjects.KeySet())
        {
            var stream = xobjects.GetAsStream(key);
            if (stream == null) continue;
            if (PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype))) return true;
            if (PdfName.Form.Equals(stream.GetAsName(PdfName.Subtype)))
            {
                var nested = stream.GetAsDictionary(PdfName.Resources);
                if (nested != null && AppearanceContainsImage(nested)) return true;
            }
        }
        return false;
    }

    [Fact]
    public void SignDigitally_Pkcs12WithNoPrivateKey_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        byte[] certOnly = MakeCertOnlyPkcs12("pw");

        var ex = Assert.Throws<ArgumentException>(() => Signer.SignDigitally(pdf, certOnly, "pw"));
        Assert.Contains("no private key", ex.Message);
    }

    /// <summary>Builds a PKCS#12 file containing only a certificate (no key entry), by
    /// stripping the key out of a normally generated one.</summary>
    private static byte[] MakeCertOnlyPkcs12(string password)
    {
        byte[] full = CertificateFactory.CreateSelfSignedPkcs12("No Key", password);
        var source = new Org.BouncyCastle.Pkcs.Pkcs12StoreBuilder().Build();
        source.Load(new MemoryStream(full), password.ToCharArray());
        string alias = source.Aliases.First(source.IsKeyEntry);
        var chain = source.GetCertificateChain(alias);

        var certOnly = new Org.BouncyCastle.Pkcs.Pkcs12StoreBuilder().Build();
        certOnly.SetCertificateEntry("cert-only", chain[0]);
        using var output = new MemoryStream();
        certOnly.Save(output, password.ToCharArray(), new Org.BouncyCastle.Security.SecureRandom());
        return output.ToArray();
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
