using System.Text;
using PdfEditor.Core;

namespace PdfEditor.Tests;

/// <summary>
/// Feeds malformed, truncated, and empty byte streams to every public PDF entry point.
/// A hostile or corrupt document must fail with an ordinary exception the host can catch —
/// never hang (these tests would then time out), never silently "succeed" on garbage, and
/// never corrupt process state. This is the engine-level counterpart to the dispatcher's
/// error-envelope tests.
/// </summary>
public class MalformedPdfSecurityTests
{
    /// <summary>
    /// Runs every public engine operation against <paramref name="input"/> and asserts each
    /// one throws rather than hanging or returning a bogus success.
    /// </summary>
    private static void AssertEveryOperationRejects(byte[] input)
    {
        Assert.ThrowsAny<Exception>(() => PdfInspector.GetInfo(input));
        Assert.ThrowsAny<Exception>(() => PageRenderer.RenderPagePng(input, 1, 72));
        Assert.ThrowsAny<Exception>(() => Redactor.Redact(input, new[] { new RectRegion(1, 0, 0, 10, 10) }));
        Assert.ThrowsAny<Exception>(() => TextTools.FindText(input, "secret"));
        Assert.ThrowsAny<Exception>(() => TextTools.GetTextInRegion(input, new RectRegion(1, 0, 0, 10, 10)));
        Assert.ThrowsAny<Exception>(() => Signer.GetSignatures(input));
        Assert.ThrowsAny<Exception>(() => Encryptor.Encrypt(input, "pw"));
        // A single-element merge short-circuits without parsing; two copies force the read path.
        Assert.ThrowsAny<Exception>(() => Merger.Merge(new[] { input, input }));
        // Newer editor operations must be just as hostile-input-safe as the originals.
        Assert.ThrowsAny<Exception>(() => PageTools.Arrange(input, new[] { 1 }));
        Assert.ThrowsAny<Exception>(() => PageTools.Rotate(input, new[] { 1 }, 90));
        Assert.ThrowsAny<Exception>(() => FormTools.ListFields(input));
        Assert.ThrowsAny<Exception>(() => FormTools.AddTextField(input, 1, new RectRegion(1, 0, 0, 10, 10)));
        Assert.ThrowsAny<Exception>(() =>
            FormTools.AddDropdown(input, 1, new RectRegion(1, 0, 0, 40, 12), "f", new[] { "a" }));
        Assert.ThrowsAny<Exception>(() =>
            HighlightTool.AddHighlight(input, 1, new[] { new RectRegion(1, 0, 0, 10, 10) }, null));
        Assert.ThrowsAny<Exception>(() =>
            InkTools.AddInk(input, 1, new[] { (IReadOnlyList<(float, float)>)new[] { (0f, 0f), (5f, 5f) } }, null, 2f));
    }

    [Fact]
    public void GarbageBytes_EveryOperationRejects()
    {
        byte[] garbage = Encoding.ASCII.GetBytes("%PDF-1.7\nthis looks like a header but is not a real document\n%%EOF");
        AssertEveryOperationRejects(garbage);
    }

    [Fact]
    public void EmptyInput_EveryOperationRejects()
    {
        AssertEveryOperationRejects(Array.Empty<byte>());
    }

    [Fact]
    public void TruncatedPdf_EveryOperationRejects()
    {
        byte[] valid = TestPdfs.MultiPage(1);
        byte[] truncated = valid.Take(valid.Length / 3).ToArray();
        AssertEveryOperationRejects(truncated);
    }

    [Fact]
    public void RandomBinaryNoise_EveryOperationRejects()
    {
        // Deterministic pseudo-random bytes with a PDF magic prefix — structurally hostile.
        var bytes = new byte[512];
        var rng = new Random(1234);
        rng.NextBytes(bytes);
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(bytes, 0);
        AssertEveryOperationRejects(bytes);
    }

    [Fact]
    public void Redaction_LeavesNoRecoverableTextBehind_EvenViaTheEnginesOwnExtraction()
    {
        // The core security guarantee of a redaction tool: after redacting a region, the
        // covered text must be gone from the document, not merely hidden under a black box.
        // We probe with the engine's *own* extraction API — the most capable recovery tool a
        // motivated reader would reach for.
        byte[] pdf = TestPdfs.WithText(("CONFIDENTIAL-SSN-123-45-6789", 72, 700, 14));
        var match = Assert.Single(TextTools.FindText(pdf, "CONFIDENTIAL-SSN-123-45-6789"));

        var result = Redactor.Redact(pdf, new[]
        {
            new RectRegion(match.Page, match.X, match.Y, match.Width, match.Height)
        });

        Assert.Empty(TextTools.FindText(result.Pdf, "CONFIDENTIAL-SSN-123-45-6789"));
        Assert.Empty(TextTools.FindText(result.Pdf, "123-45-6789"));
        Assert.DoesNotContain("6789", TextTools.GetTextInRegion(result.Pdf,
            new RectRegion(match.Page, match.X, match.Y, match.Width, match.Height)).Text);
    }
}
