using iText.Bouncycastle.Cert;
using iText.Bouncycastle.X509;
using iText.Kernel.Crypto;
using iText.Bouncycastle.Crypto;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Form.Element;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Signatures;
using Org.BouncyCastle.Pkcs;

namespace PdfEditor.Core;

/// <summary>
/// Electronic signatures: a hand-drawn/uploaded signature image stamped onto the page,
/// and cryptographic digital signatures from a PKCS#12 (.pfx/.p12) certificate.
/// </summary>
public static class Signer
{
    /// <summary>Stamps a signature image (e.g. drawn on the extension's signature pad) onto a page.</summary>
    public static byte[] AddImageSignature(byte[] pdf, RectRegion placement, byte[] imageBytes,
        string? password = null)
    {
        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            var page = doc.GetPage(placement.Page);
            var canvas = new PdfCanvas(page);
            var image = ImageDataFactory.Create(imageBytes);
            canvas.AddImageFittedIntoRectangle(image,
                new Rectangle(placement.X, placement.Y, placement.Width, placement.Height), false);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Applies a cryptographic digital signature. When <paramref name="placement"/> is given the
    /// signature is visible on that page; otherwise it is invisible.
    /// </summary>
    public static byte[] SignDigitally(byte[] pdf, byte[] pkcs12, string pkcs12Password,
        string? reason = null, string? location = null, RectRegion? placement = null,
        byte[]? appearanceImage = null, string? pdfPassword = null)
    {
        var (privateKey, chain, subjectName) = LoadPkcs12(pkcs12, pkcs12Password);

        var readerProperties = new ReaderProperties();
        if (!string.IsNullOrEmpty(pdfPassword))
            readerProperties.SetPassword(System.Text.Encoding.UTF8.GetBytes(pdfPassword));
        using var output = new MemoryStream();
        var reader = new PdfReader(new MemoryStream(pdf), readerProperties);
        reader.SetUnethicalReading(true);

        var signer = new PdfSigner(reader, output, new StampingProperties().UseAppendMode());
        var signerProperties = new SignerProperties()
            .SetFieldName($"Signature_{DateTime.UtcNow:yyyyMMddHHmmssfff}");
        if (!string.IsNullOrEmpty(reason)) signerProperties.SetReason(reason);
        if (!string.IsNullOrEmpty(location)) signerProperties.SetLocation(location);
        if (placement != null)
        {
            var appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID);
            if (appearanceImage != null)
                appearance.SetContent(subjectName, ImageDataFactory.Create(appearanceImage));
            else
                appearance.SetContent($"Digitally signed by {subjectName}\n{DateTime.UtcNow:u}");
            signerProperties
                .SetPageNumber(placement.Page)
                .SetPageRect(new Rectangle(placement.X, placement.Y, placement.Width, placement.Height))
                .SetSignatureAppearance(appearance);
        }
        signer.SetSignerProperties(signerProperties);

        IExternalSignature signature = new PrivateKeySignature(privateKey, DigestAlgorithms.SHA256);
        signer.SignDetached(signature, chain, null, null, null, 0, PdfSigner.CryptoStandard.CMS);
        return output.ToArray();
    }

    /// <summary>Lists the digital signatures in a document and verifies their integrity.</summary>
    public static IReadOnlyList<SignatureInfo> GetSignatures(byte[] pdf, string? password = null)
    {
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        var util = new SignatureUtil(doc);
        var result = new List<SignatureInfo>();
        foreach (var name in util.GetSignatureNames())
        {
            var pkcs7 = util.ReadSignatureData(name);
            string? signer = null;
            try
            {
                signer = iText.Signatures.CertificateInfo.GetSubjectFields(pkcs7.GetSigningCertificate())
                    ?.GetField("CN");
            }
            catch
            {
                // certificate subject parsing is best-effort
            }
            result.Add(new SignatureInfo(name, signer,
                pkcs7.VerifySignatureIntegrityAndAuthenticity(),
                util.SignatureCoversWholeDocument(name)));
        }
        return result;
    }

    private static (iText.Commons.Bouncycastle.Crypto.IPrivateKey Key, IX509Certificate[] Chain, string Subject)
        LoadPkcs12(byte[] pkcs12, string password)
    {
        var store = new Pkcs12StoreBuilder().Build();
        store.Load(new MemoryStream(pkcs12), password.ToCharArray());
        string? alias = store.Aliases.FirstOrDefault(store.IsKeyEntry);
        if (alias == null)
            throw new ArgumentException("The PKCS#12 file contains no private key entry.");

        var key = new PrivateKeyBC(store.GetKey(alias).Key);
        var chain = store.GetCertificateChain(alias)
            .Select(e => (IX509Certificate)new X509CertificateBC(e.Certificate)).ToArray();
        string subject = "Unknown signer";
        try
        {
            var cn = store.GetCertificateChain(alias)[0].Certificate.SubjectDN
                .GetValueList(Org.BouncyCastle.Asn1.X509.X509Name.CN);
            if (cn.Count > 0) subject = cn[0].ToString() ?? subject;
        }
        catch
        {
            // fall back to the default label
        }
        return (key, chain, subject);
    }
}
