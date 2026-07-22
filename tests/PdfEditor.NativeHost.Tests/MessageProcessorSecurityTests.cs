using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfEditor.NativeHost.Tests;

/// <summary>
/// Adversarial tests for the JSON dispatcher. Everything it receives is untrusted: a website
/// can hand the extension an arbitrary PDF, and the PDF's own contents (signature names,
/// stream data, declared geometry) are attacker-authored. Malformed, hostile, or oversized
/// input must always come back as a clean error envelope — never an unhandled crash, a hang,
/// a protocol-injection, or a leak of a supplied secret.
/// </summary>
public class MessageProcessorSecurityTests
{
    private readonly MessageProcessor _processor = new();

    private static JsonObject Request(string action, object? payload = null) =>
        JsonNode.Parse(JsonSerializer.Serialize(new { id = "sec", action, payload }))!.AsObject();

    private (JsonObject Response, string RawFrame) Handle(JsonObject request)
    {
        string raw = Assert.Single(_processor.Handle(request.ToJsonString()));
        return (JsonNode.Parse(raw)!.AsObject(), raw);
    }

    private JsonObject HandleOne(JsonObject request) => Handle(request).Response;

    private static bool Ok(JsonObject r) => r["ok"]!.GetValue<bool>();
    private static string ErrorOf(JsonObject r) => r["result"]!["error"]!.GetValue<string>();

    // ----------------------------------------------------------- malformed input

    [Fact]
    public void MalformedBase64Pdf_ReturnsError_NotCrash()
    {
        var r = HandleOne(Request("info", new { pdf = "@@@ not valid base64 @@@" }));
        Assert.False(Ok(r));
    }

    [Theory]
    [InlineData("info")]
    [InlineData("render")]
    [InlineData("redact")]
    [InlineData("find-text")]
    [InlineData("signatures")]
    [InlineData("get-region-text")]
    public void GarbageBytesInPlaceOfPdf_ReturnError_NotCrash(string action)
    {
        // Well-formed base64 that decodes to bytes that are in no way a PDF.
        string garbage = Convert.ToBase64String(
            System.Text.Encoding.ASCII.GetBytes("this is definitely not a PDF document"));

        // A superset payload so whichever extra fields the action needs are present; the
        // action under test ignores the ones it does not use.
        var r = HandleOne(Request(action, new
        {
            pdf = garbage,
            page = 1,
            dpi = 72,
            phrase = "x",
            region = new { page = 1, x = 0.0, y = 0.0, width = 10.0, height = 10.0 },
            regions = new[] { new { page = 1, x = 0.0, y = 0.0, width = 10.0, height = 10.0 } }
        }));

        Assert.False(Ok(r));
    }

    [Fact]
    public void TruncatedPdf_ReturnsError_NotCrash()
    {
        byte[] valid = TestPdf.OnePage("hello");
        string truncated = Convert.ToBase64String(valid.Take(valid.Length / 4).ToArray());

        Assert.False(Ok(HandleOne(Request("info", new { pdf = truncated }))));
    }

    [Theory]
    [InlineData("info")]
    [InlineData("render")]
    [InlineData("redact")]
    [InlineData("merge")]
    [InlineData("encrypt")]
    [InlineData("decrypt")]
    [InlineData("sign-digital")]
    [InlineData("create-cert")]
    [InlineData("signatures")]
    public void EmptyPayload_ReturnsError_NeverThrows(string action)
    {
        // No required fields supplied at all — must be a caught error, not an exception.
        Assert.False(Ok(HandleOne(Request(action, new { }))));
    }

    [Fact]
    public void DeeplyNestedJson_IsRejected_NotAStackOverflow()
    {
        // Far beyond System.Text.Json's default 64-level depth guard.
        string bomb = new string('[', 5000) + new string(']', 5000);
        var response = JsonNode.Parse(Assert.Single(_processor.Handle(bomb)))!.AsObject();
        Assert.False(Ok(response));
    }

    // ------------------------------------------------------ resource-exhaustion bounds

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(100_000)]
    public void RenderWithAbsurdDpi_ReturnsError_NotAGiganticAllocation(int dpi)
    {
        string pdf = Convert.ToBase64String(TestPdf.OnePage());
        Assert.False(Ok(HandleOne(Request("render", new { pdf, page = 1, dpi }))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void RenderNonExistentPage_ReturnsError_NotCrash(int page)
    {
        string pdf = Convert.ToBase64String(TestPdf.OnePage());
        Assert.False(Ok(HandleOne(Request("render", new { pdf, page, dpi = 72 }))));
    }

    [Fact]
    public void RedactPageBeyondDocument_ReturnsError_NotCrash()
    {
        string pdf = Convert.ToBase64String(TestPdf.OnePage());
        var r = HandleOne(Request("redact", new
        {
            pdf,
            regions = new[] { new { page = 999_999, x = 0.0, y = 0.0, width = 10.0, height = 10.0 } }
        }));
        Assert.False(Ok(r));
    }

    // ---------------------------------------------- page/form actions reject bad input

    [Fact]
    public void ArrangePages_EmptyOrder_ReturnsError_NotCrash()
    {
        string pdf = Convert.ToBase64String(TestPdf.ManyPages(3));
        Assert.False(Ok(HandleOne(Request("arrange-pages", new { pdf, order = Array.Empty<int>() }))));
    }

    [Fact]
    public void ArrangePages_PageBeyondDocument_ReturnsError_NotCrash()
    {
        string pdf = Convert.ToBase64String(TestPdf.ManyPages(2));
        Assert.False(Ok(HandleOne(Request("arrange-pages", new { pdf, order = new[] { 1, 999 } }))));
    }

    [Fact]
    public void ArrangePages_MissingOrder_ReturnsError_NotCrash()
    {
        string pdf = Convert.ToBase64String(TestPdf.ManyPages(2));
        Assert.False(Ok(HandleOne(Request("arrange-pages", new { pdf }))));
    }

    [Fact]
    public void AddFormField_DropdownWithNoOptions_ReturnsError_NotCrash()
    {
        var r = HandleOne(Request("add-form-field", new
        {
            pdf = Convert.ToBase64String(TestPdf.OnePage()),
            region = new { page = 1, x = 100, y = 500, width = 200, height = 24 },
            fieldType = "dropdown", name = "empty", options = Array.Empty<string>(),
        }));
        Assert.False(Ok(r));
    }

    [Fact]
    public void AddFormField_PageBeyondDocument_ReturnsError_NotCrash()
    {
        var r = HandleOne(Request("add-form-field", new
        {
            pdf = Convert.ToBase64String(TestPdf.OnePage()),
            region = new { page = 42, x = 100, y = 500, width = 200, height = 24 },
            fieldType = "text", name = "n",
        }));
        Assert.False(Ok(r));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddScript_EmptyName_ReturnsError_NotCrash(string name)
    {
        var r = HandleOne(Request("add-script", new
        {
            pdf = Convert.ToBase64String(TestPdf.OnePage()), name, script = "app.alert('x');",
        }));
        Assert.False(Ok(r));
    }

    [Fact]
    public void AddScript_EmptyBody_ReturnsError_NotCrash()
    {
        var r = HandleOne(Request("add-script", new
        {
            pdf = Convert.ToBase64String(TestPdf.OnePage()), name = "n", script = "",
        }));
        Assert.False(Ok(r));
    }

    [Fact]
    public void ListScripts_OnGarbageBytes_ReturnsError_NotCrash()
    {
        string garbage = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("not a pdf"));
        Assert.False(Ok(HandleOne(Request("list-scripts", new { pdf = garbage }))));
    }

    [Fact]
    public void AddScript_HostileScriptSource_IsStoredAsInertData_NeverEmittedRaw()
    {
        // The script body is attacker-controlled text. The host stores and returns it as a JSON
        // string value; it must never break out of the JSON envelope on the wire.
        const string hostile = "</script><svg onload=alert(1)>\"'\\";
        string withJs = HandleOne(Request("add-script", new
        {
            pdf = Convert.ToBase64String(TestPdf.OnePage()), name = "x", script = hostile,
        }))["result"]!["pdf"]!.GetValue<string>();

        var (response, rawFrame) = Handle(Request("list-scripts", new { pdf = withJs }));

        Assert.True(Ok(response));
        Assert.Equal(hostile, response["result"]!["scripts"]![0]!["script"]!.GetValue<string>());
        Assert.DoesNotContain("</script><svg", rawFrame); // escaped in the emitted JSON
    }

    // --------------------------------------------------------------- no secret leak

    [Fact]
    public void WrongPassword_ErrorMessageDoesNotEchoTheSuppliedPassword()
    {
        // Encrypt with a known password, then attempt to decrypt with a different, distinctive
        // one. The host must not reflect the caller-supplied secret back in its error text.
        const string wrongPassword = "leakme-9f83a2-DISTINCTIVE";
        var encrypted = HandleOne(Request("encrypt", new
        {
            pdf = Convert.ToBase64String(TestPdf.OnePage()),
            userPassword = "the-real-password"
        }));
        Assert.True(Ok(encrypted));
        string encryptedPdf = encrypted["result"]!["pdf"]!.GetValue<string>();

        var decrypted = HandleOne(Request("decrypt", new { pdf = encryptedPdf, password = wrongPassword }));

        Assert.False(Ok(decrypted));
        Assert.DoesNotContain(wrongPassword, ErrorOf(decrypted));
    }

    // ------------------------------------------ attacker-controlled PDF metadata is inert

    [Fact]
    public void MaliciousSignatureCommonName_RoundTripsAsInertJsonData()
    {
        // The signer name shown in the viewer comes from the signing certificate's Subject CN,
        // which anyone can set to anything by self-signing a PDF. The host must faithfully
        // return it as a JSON *string value* (so the viewer's escaping contract holds) and must
        // never emit it raw on the wire in a way that could break out of the JSON envelope.
        const string xss = "<script>alert(1)</script>";

        var cert = HandleOne(Request("create-cert", new { name = xss, password = "pw" }));
        Assert.True(Ok(cert));
        string pfx = cert["result"]!["pfx"]!.GetValue<string>();

        var signed = HandleOne(Request("sign-digital", new
        {
            pdf = Convert.ToBase64String(TestPdf.OnePage()),
            pfx,
            pfxPassword = "pw"
        }));
        Assert.True(Ok(signed));
        string signedPdf = signed["result"]!["pdf"]!.GetValue<string>();

        var (response, rawFrame) = Handle(Request("signatures", new { pdf = signedPdf }));

        Assert.True(Ok(response));
        string signer = response["result"]!["signatures"]![0]!["signer"]!.GetValue<string>();
        // Preserved exactly as data...
        Assert.Equal(xss, signer);
        // ...but never emitted raw on the wire — System.Text.Json escapes the angle brackets,
        // so the literal "<script>" never appears in the bytes sent to the browser.
        Assert.DoesNotContain("<script>", rawFrame);
    }

    // ----------------------------------------------------------- dispatcher resilience

    [Fact]
    public void ProcessorStaysUsable_AfterAStreamOfHostileRequests()
    {
        _processor.Handle("not json {{{");
        _processor.Handle("[1,2,3]");
        _processor.Handle(Request("info", new { pdf = "@@@" }).ToJsonString());
        _processor.Handle(Request("teleport").ToJsonString());

        Assert.True(Ok(HandleOne(Request("ping"))));
    }
}
