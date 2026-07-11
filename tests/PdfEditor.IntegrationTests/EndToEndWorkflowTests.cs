using System.Text.Json.Nodes;
using PdfEditor.Tests;
using Xunit;

namespace PdfEditor.IntegrationTests;

/// <summary>
/// Full user workflows through the real host process: exactly the sequences the
/// extension performs, asserting on the returned document bytes.
/// </summary>
public class EndToEndWorkflowTests : IClassFixture<HostProcessFixture>
{
    private readonly HostProcessFixture _host;

    public EndToEndWorkflowTests(HostProcessFixture host) => _host = host;

    private JsonNode Ok(object request)
    {
        var response = _host.Send(request, TimeSpan.FromMinutes(2));
        Assert.True(response["ok"]!.GetValue<bool>(),
            response["result"]?["error"]?.GetValue<string>() ?? "unknown host error");
        return response["result"]!;
    }

    [Fact]
    public void RedactionWorkflow_PreviewThenApply_RemovesContentBehindBox()
    {
        byte[] original = TestPdfs.WithText(
            ("Account: 12-3456-789", 72, 700, 14),
            ("Balance: $10,000", 72, 660, 14),
            ("harmless footer", 72, 100, 10));
        string pdf = Convert.ToBase64String(original);
        var regions = new[] { new { page = 1, x = 60f, y = 690f, width = 250f, height = 30f } };

        // 1. Preview: extension applies redaction to a scratch copy and renders it.
        var preview = Ok(new { id = "w-preview", action = "redact", payload = new { pdf, regions } });
        string previewPdf = preview["pdf"]!.GetValue<string>();
        var previewRender = Ok(new
        {
            id = "w-preview-render",
            action = "render",
            payload = new { pdf = previewPdf, page = 1, dpi = 72 }
        });
        Assert.NotEmpty(previewRender["png"]!.GetValue<string>());
        // The original is untouched by previewing.
        Assert.Contains("12-3456-789", TestPdfAssert.ExtractText(original));

        // 2. Apply: same operation becomes the working document.
        var applied = Ok(new { id = "w-apply", action = "redact", payload = new { pdf, regions } });
        byte[] redacted = Convert.FromBase64String(applied["pdf"]!.GetValue<string>());

        string text = TestPdfAssert.ExtractText(redacted);
        Assert.DoesNotContain("12-3456-789", text);
        Assert.Contains("harmless footer", text);
        // The box is painted opaque black over the region.
        var pixel = TestPdfAssert.PixelAt(redacted, 1, 180, 705);
        Assert.Equal(new SkiaSharp.SKColor(0, 0, 0, 255), pixel);
    }

    [Fact]
    public void TextEditWorkflow_ReadRegion_EditText_SaveResult()
    {
        byte[] original = TestPdfs.WithText(("Amount Due: $500", 72, 700, 14));
        string pdf = Convert.ToBase64String(original);
        var region = new { page = 1, x = 60f, y = 690f, width = 250f, height = 30f };

        // 1. The extension reads what's under the selection to prefill the editor.
        var read = Ok(new { id = "w-read", action = "get-region-text", payload = new { pdf, region } });
        Assert.Equal("Amount Due: $500", read["text"]!.GetValue<string>());

        // 2. The user edits the text and applies.
        var replaced = Ok(new
        {
            id = "w-replace",
            action = "replace-region-text",
            payload = new { pdf, region, text = "Amount Due: $750 (revised)" }
        });
        byte[] edited = Convert.FromBase64String(replaced["pdf"]!.GetValue<string>());

        string text = TestPdfAssert.ExtractText(edited);
        Assert.DoesNotContain("$500", text);
        Assert.Contains("$750 (revised)", text);
    }

    [Fact]
    public void FindAndReplaceWorkflow_AcrossDocument()
    {
        byte[] original = TestPdfs.WithText(
            ("Contract with OldCorp", 72, 700, 12),
            ("OldCorp shall deliver", 72, 660, 12));
        string pdf = Convert.ToBase64String(original);

        var found = Ok(new { id = "w-find", action = "find-text", payload = new { pdf, phrase = "OldCorp" } });
        Assert.Equal(2, found["matches"]!.AsArray().Count);

        var replaced = Ok(new
        {
            id = "w-replace-all",
            action = "replace-all",
            payload = new { pdf, phrase = "OldCorp", replacement = "NewCorp" }
        });
        Assert.Equal(2, replaced["count"]!.GetValue<int>());
        string text = TestPdfAssert.ExtractText(Convert.FromBase64String(replaced["pdf"]!.GetValue<string>()));
        Assert.DoesNotContain("OldCorp", text);
        Assert.Contains("NewCorp", text);
    }

    [Fact]
    public void MergeWorkflow_CombinesDocumentsInOrder()
    {
        string a = Convert.ToBase64String(TestPdfs.MultiPage(2, "First"));
        string b = Convert.ToBase64String(TestPdfs.MultiPage(1, "Second"));

        var merged = Ok(new { id = "w-merge", action = "merge", payload = new { pdfs = new[] { a, b } } });
        byte[] result = Convert.FromBase64String(merged["pdf"]!.GetValue<string>());

        Assert.Equal(3, PdfEditor.Core.PdfInspector.GetInfo(result).PageCount);
        Assert.Contains("First 1", TestPdfAssert.ExtractText(result, 1));
        Assert.Contains("Second 1", TestPdfAssert.ExtractText(result, 3));
    }

    [Fact]
    public void ProtectWorkflow_EncryptThenReopenWithPassword()
    {
        string pdf = Convert.ToBase64String(TestPdfs.WithText(("classified", 72, 700, 12)));

        var encrypted = Ok(new
        {
            id = "w-encrypt",
            action = "encrypt",
            payload = new { pdf, userPassword = "s3cret" }
        });
        byte[] locked = Convert.FromBase64String(encrypted["pdf"]!.GetValue<string>());
        Assert.True(PdfEditor.Core.Encryptor.IsEncrypted(locked));

        // Reopening through the host requires the password...
        var info = Ok(new
        {
            id = "w-info-locked",
            action = "info",
            payload = new { pdf = encrypted["pdf"]!.GetValue<string>(), pdfPassword = "s3cret" }
        });
        Assert.True(info["encrypted"]!.GetValue<bool>());

        // ...and without it the host reports a clean error.
        var denied = _host.Send(new
        {
            id = "w-info-denied",
            action = "render",
            payload = new { pdf = encrypted["pdf"]!.GetValue<string>(), page = 1 }
        });
        Assert.False(denied["ok"]!.GetValue<bool>());

        var decrypted = Ok(new
        {
            id = "w-decrypt",
            action = "decrypt",
            payload = new { pdf = encrypted["pdf"]!.GetValue<string>(), password = "s3cret" }
        });
        Assert.False(PdfEditor.Core.Encryptor.IsEncrypted(
            Convert.FromBase64String(decrypted["pdf"]!.GetValue<string>())));
    }

    [Fact]
    public void SignatureWorkflow_DrawnImageThenDigitalCertificate()
    {
        string pdf = Convert.ToBase64String(TestPdfs.WithText(("Agreement", 72, 700, 14)));

        // 1. Stamp a hand-drawn signature image.
        string png = Convert.ToBase64String(MakeSignaturePng());
        var stamped = Ok(new
        {
            id = "w-stamp",
            action = "sign-image",
            payload = new { pdf, region = new { page = 1, x = 350f, y = 90f, width = 150f, height = 50f }, png }
        });
        byte[] withImage = Convert.FromBase64String(stamped["pdf"]!.GetValue<string>());
        Assert.Equal(1, TestPdfAssert.CountImages(withImage));

        // 2. Create a certificate and digitally sign the stamped document.
        var cert = Ok(new
        {
            id = "w-cert",
            action = "create-cert",
            payload = new { name = "Workflow Signer", password = "certpw" }
        });
        var signed = Ok(new
        {
            id = "w-sign",
            action = "sign-digital",
            payload = new
            {
                pdf = stamped["pdf"]!.GetValue<string>(),
                pfx = cert["pfx"]!.GetValue<string>(),
                pfxPassword = "certpw",
                reason = "Approved"
            }
        });

        // 3. Verify through the host.
        var signatures = Ok(new
        {
            id = "w-verify",
            action = "signatures",
            payload = new { pdf = signed["pdf"]!.GetValue<string>() }
        });
        var entry = Assert.Single(signatures["signatures"]!.AsArray());
        Assert.True(entry!["valid"]!.GetValue<bool>());
        Assert.Equal("Workflow Signer", entry["signer"]!.GetValue<string>());
    }

    [Fact]
    public void FullEditingSession_EditRedactMergeSignProtect()
    {
        // One session touching every capability, in the order a user might.
        string pdf = Convert.ToBase64String(TestPdfs.WithText(
            ("Draft Agreement with SecretParty", 72, 700, 14),
            ("Total: $1", 72, 660, 14)));

        // Edit the total.
        var edited = Ok(new
        {
            id = "s-edit",
            action = "replace-region-text",
            payload = new
            {
                pdf,
                region = new { page = 1, x = 60f, y = 650f, width = 200f, height = 30f },
                text = "Total: $1,000,000"
            }
        });

        // Redact the counterparty name.
        var match = Ok(new
        {
            id = "s-find",
            action = "find-text",
            payload = new { pdf = edited["pdf"]!.GetValue<string>(), phrase = "SecretParty" }
        })["matches"]!.AsArray()[0]!;
        var redacted = Ok(new
        {
            id = "s-redact",
            action = "redact",
            payload = new
            {
                pdf = edited["pdf"]!.GetValue<string>(),
                regions = new[]
                {
                    new
                    {
                        page = match["page"]!.GetValue<int>(),
                        x = match["x"]!.GetValue<float>(),
                        y = match["y"]!.GetValue<float>(),
                        width = match["width"]!.GetValue<float>(),
                        height = match["height"]!.GetValue<float>()
                    }
                }
            }
        });

        // Append a second document.
        var merged = Ok(new
        {
            id = "s-merge",
            action = "merge",
            payload = new
            {
                pdfs = new[]
                {
                    redacted["pdf"]!.GetValue<string>(),
                    Convert.ToBase64String(TestPdfs.MultiPage(1, "Appendix"))
                }
            }
        });

        // Encrypt first, then sign: signing appends to the encrypted file, so both
        // survive. (Encrypting after signing rewrites the file and breaks the signature.)
        var encrypted = Ok(new
        {
            id = "s-protect",
            action = "encrypt",
            payload = new { pdf = merged["pdf"]!.GetValue<string>(), userPassword = "openme" }
        });
        var cert = Ok(new
        {
            id = "s-cert",
            action = "create-cert",
            payload = new { name = "Final Approver", password = "pw" }
        });
        var signed = Ok(new
        {
            id = "s-sign",
            action = "sign-digital",
            payload = new
            {
                pdf = encrypted["pdf"]!.GetValue<string>(),
                pfx = cert["pfx"]!.GetValue<string>(),
                pfxPassword = "pw",
                pdfPassword = "openme"
            }
        });

        byte[] final = Convert.FromBase64String(signed["pdf"]!.GetValue<string>());
        Assert.True(PdfEditor.Core.Encryptor.IsEncrypted(final));

        string page1 = TestPdfAssert.ExtractText(final, 1, "openme");
        Assert.Contains("$1,000,000", page1);
        Assert.DoesNotContain("SecretParty", page1);
        Assert.DoesNotContain("Total: $1\n", page1 + "\n");
        Assert.Contains("Appendix 1", TestPdfAssert.ExtractText(final, 2, "openme"));

        var signatures = PdfEditor.Core.Signer.GetSignatures(final, "openme");
        var finalSignature = Assert.Single(signatures);
        Assert.Equal("Final Approver", finalSignature.SignerName);
        Assert.True(finalSignature.IntegrityValid);
    }

    private static byte[] MakeSignaturePng()
    {
        using var bitmap = new SkiaSharp.SKBitmap(120, 40);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            using var pen = new SkiaSharp.SKPaint
            {
                Color = SkiaSharp.SKColors.DarkBlue,
                StrokeWidth = 2,
                Style = SkiaSharp.SKPaintStyle.Stroke
            };
            canvas.DrawLine(10, 30, 50, 10, pen);
            canvas.DrawLine(50, 10, 110, 30, pen);
        }
        using var img = SkiaSharp.SKImage.FromBitmap(bitmap);
        return img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }
}
