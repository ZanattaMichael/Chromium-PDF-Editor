using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PdfEditor.NativeHost.Tests;

/// <summary>
/// Fast, in-process tests for the JSON dispatcher: no process spawning, no native
/// messaging framing — just <see cref="MessageProcessor.Handle"/> called directly.
/// Complements <c>PdfEditor.IntegrationTests</c>, which drives the same dispatcher
/// through the real host process and wire protocol end-to-end.
/// </summary>
public class MessageProcessorTests
{
    private readonly MessageProcessor _processor = new();

    private static JsonObject Request(string action, object? payload = null) =>
        JsonNode.Parse(JsonSerializer.Serialize(new { id = "t1", action, payload }))!.AsObject();

    private JsonObject HandleOne(JsonObject request)
    {
        var frames = _processor.Handle(request.ToJsonString());
        var frame = Assert.Single(frames);
        return JsonNode.Parse(frame)!.AsObject();
    }

    [Fact]
    public void Ping_ReturnsOkWithVersion()
    {
        var response = HandleOne(Request("ping"));

        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.True(response["result"]!["pong"]!.GetValue<bool>());
    }

    [Fact]
    public void Handle_PreservesTheRequestId()
    {
        var frames = _processor.Handle("""{"id":"my-id-42","action":"ping"}""");
        var response = JsonNode.Parse(Assert.Single(frames))!.AsObject();

        Assert.Equal("my-id-42", response["id"]!.GetValue<string>());
    }

    [Fact]
    public void MalformedJson_ReturnsErrorEnvelope_NotAnException()
    {
        var frames = _processor.Handle("not json at all {{{");
        var response = JsonNode.Parse(Assert.Single(frames))!.AsObject();

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.NotNull(response["result"]!["error"]);
        // No request id could be recovered from unparseable input.
        Assert.Equal("", response["id"]!.GetValue<string>());
    }

    [Fact]
    public void JsonArray_InsteadOfObject_ReturnsErrorEnvelope()
    {
        var response = JsonNode.Parse(Assert.Single(_processor.Handle("[1,2,3]")))!.AsObject();
        Assert.False(response["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void MissingAction_ReturnsError()
    {
        var response = JsonNode.Parse(Assert.Single(_processor.Handle("""{"id":"x"}""")))!.AsObject();

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Contains("action", response["result"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownAction_ReturnsError_NamingTheAction()
    {
        var response = HandleOne(Request("teleport"));

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Contains("teleport", response["result"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public void MissingRequiredPayloadField_ReturnsError_NotException()
    {
        // "info" requires "pdf"; omit it entirely.
        var response = HandleOne(Request("info", new { }));

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Contains("pdf", response["result"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public void Info_ReturnsPageGeometry()
    {
        string pdf = TestPdf.Base64(TestPdf.ManyPages(3));

        var response = HandleOne(Request("info", new { pdf }));

        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.Equal(3, response["result"]!["pageCount"]!.GetValue<int>());
        Assert.False(response["result"]!["encrypted"]!.GetValue<bool>());
    }

    [Fact]
    public void Render_ReturnsPngBytes()
    {
        string pdf = TestPdf.Base64(TestPdf.OnePage());

        var response = HandleOne(Request("render", new { pdf, page = 1, dpi = 72 }));

        Assert.True(response["ok"]!.GetValue<bool>());
        byte[] png = Convert.FromBase64String(response["result"]!["png"]!.GetValue<string>());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4));
    }

    [Fact]
    public void Render_DefaultsDpiTo144_WhenOmitted()
    {
        string pdf = TestPdf.Base64(TestPdf.OnePage());

        var response = HandleOne(Request("render", new { pdf, page = 1 }));

        Assert.True(response["ok"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    [InlineData(1_000_000)]
    public void Render_RejectsDpiOutsideSupportedRange(int dpi)
    {
        string pdf = TestPdf.Base64(TestPdf.OnePage());

        // An unbounded dpi would drive an unbounded bitmap allocation; the host must
        // reject it as a request error rather than try to render it.
        var response = HandleOne(Request("render", new { pdf, page = 1, dpi }));

        Assert.False(response["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void GetRegionText_ReturnsTheTextFoundInTheRegion()
    {
        string pdf = TestPdf.Base64(TestPdf.OnePage("Invoice 12345"));

        var response = HandleOne(Request("get-region-text", new
        {
            pdf,
            region = new { page = 1, x = 60, y = 690, width = 300, height = 30 }
        }));

        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.Equal("Invoice 12345", response["result"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ReplaceRegionText_ReplacesTheTextAndReturnsAValidPdf()
    {
        string pdf = TestPdf.Base64(TestPdf.OnePage("Old Value"));

        var response = HandleOne(Request("replace-region-text", new
        {
            pdf,
            region = new { page = 1, x = 60, y = 690, width = 300, height = 30 },
            text = "New Value"
        }));

        Assert.True(response["ok"]!.GetValue<bool>());
        var confirm = HandleOne(Request("get-region-text", new
        {
            pdf = response["result"]!["pdf"]!.GetValue<string>(),
            region = new { page = 1, x = 60, y = 690, width = 300, height = 30 }
        }));
        Assert.Equal("New Value", confirm["result"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SignImage_StampsTheSuppliedImageOntoThePage()
    {
        string pdf = TestPdf.Base64(TestPdf.OnePage("sign here"));

        using var bitmap = new SkiaSharp.SKBitmap(10, 10);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap)) canvas.Clear(SkiaSharp.SKColors.Black);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        string png = Convert.ToBase64String(image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray());

        var response = HandleOne(Request("sign-image", new
        {
            pdf,
            region = new { page = 1, x = 60, y = 600, width = 100, height = 40 },
            png
        }));

        Assert.True(response["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void Redact_RoundTripsThroughBase64_AndRemovesText()
    {
        string pdf = TestPdf.Base64(TestPdf.OnePage("secret payload"));

        var response = HandleOne(Request("redact", new
        {
            pdf,
            regions = new[] { new { page = 1, x = 60, y = 690, width = 300, height = 30 } }
        }));

        Assert.True(response["ok"]!.GetValue<bool>());
        string resultPdf = response["result"]!["pdf"]!.GetValue<string>();
        Assert.NotEmpty(resultPdf);
        // A valid PDF always starts with this signature once decoded.
        byte[] bytes = Convert.FromBase64String(resultPdf);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void CreateCert_ThenSignDigitally_RoundTrips()
    {
        var certResponse = HandleOne(Request("create-cert", new { name = "Unit Test", password = "pw" }));
        string pfx = certResponse["result"]!["pfx"]!.GetValue<string>();

        var signResponse = HandleOne(Request("sign-digital", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage("contract")),
            pfx,
            pfxPassword = "pw"
        }));

        Assert.True(signResponse["ok"]!.GetValue<bool>());

        var sigResponse = HandleOne(Request("signatures", new { pdf = signResponse["result"]!["pdf"]!.GetValue<string>() }));
        Assert.Single(sigResponse["result"]!["signatures"]!.AsArray());
    }

    [Fact]
    public void FindText_ThenReplaceAll_ReportsCount()
    {
        string pdf = TestPdf.Base64(TestPdf.OnePage("find me here"));

        var found = HandleOne(Request("find-text", new { pdf, phrase = "find" }));
        Assert.Single(found["result"]!["matches"]!.AsArray());

        var replaced = HandleOne(Request("replace-all", new { pdf, phrase = "find", replacement = "seek" }));
        Assert.Equal(1, replaced["result"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public void Merge_CombinesMultipleDocuments()
    {
        var pdfs = new[] { TestPdf.Base64(TestPdf.OnePage("a")), TestPdf.Base64(TestPdf.OnePage("b")) };

        var response = HandleOne(Request("merge", new { pdfs }));

        Assert.True(response["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void EncryptThenDecrypt_RoundTrips()
    {
        string pdf = TestPdf.Base64(TestPdf.OnePage("classified"));

        var encrypted = HandleOne(Request("encrypt", new { pdf, userPassword = "s3cret" }));
        Assert.True(encrypted["ok"]!.GetValue<bool>());
        string lockedPdf = encrypted["result"]!["pdf"]!.GetValue<string>();

        var decrypted = HandleOne(Request("decrypt", new { pdf = lockedPdf, password = "s3cret" }));
        Assert.True(decrypted["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void WrongDecryptPassword_ReturnsError_NotException()
    {
        string pdf = TestPdf.Base64(TestPdf.OnePage("classified"));
        var encrypted = HandleOne(Request("encrypt", new { pdf, userPassword = "right" }));

        var response = HandleOne(Request("decrypt", new
        {
            pdf = encrypted["result"]!["pdf"]!.GetValue<string>(),
            password = "wrong"
        }));

        Assert.False(response["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void LargeResponse_IsSplitIntoChunkFrames_ThatReassembleToTheSameEnvelope()
    {
        // A large enough document pushes the base64 PDF past the ~900 KB single-frame
        // limit, forcing MessageProcessor's own Frame() splitter into action.
        string pdf = TestPdf.Base64(TestPdf.ManyPages(1500));

        var frames = _processor.Handle(Request("merge", new { pdfs = new[] { pdf, pdf } }).ToJsonString());

        Assert.True(frames.Count > 1, "expected the large response to be split into multiple chunk frames");

        var first = JsonNode.Parse(frames[0])!.AsObject();
        Assert.NotNull(first["chunkIndex"]);
        int chunkCount = first["chunkCount"]!.GetValue<int>();
        Assert.Equal(chunkCount, frames.Count);

        string encoded = string.Concat(frames.Select(f => JsonNode.Parse(f)!["data"]!.GetValue<string>()));
        byte[] decoded = Convert.FromBase64String(encoded);
        var envelope = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(decoded))!.AsObject();
        Assert.True(envelope["ok"]!.GetValue<bool>());
    }
}
