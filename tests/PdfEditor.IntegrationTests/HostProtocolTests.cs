using System.Text.Json.Nodes;
using PdfEditor.Tests;
using Xunit;

namespace PdfEditor.IntegrationTests;

/// <summary>
/// Drives the real host process over Chrome's native messaging framing:
/// 4-byte little-endian length prefix + UTF-8 JSON, request/response with chunking.
/// </summary>
public class HostProtocolTests : IClassFixture<HostProcessFixture>
{
    private readonly HostProcessFixture _host;

    public HostProtocolTests(HostProcessFixture host) => _host = host;

    [Fact]
    public void Ping_RoundTrips()
    {
        var response = _host.Send(new { id = "t-ping", action = "ping" });

        Assert.Equal("t-ping", response["id"]!.GetValue<string>());
        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.True(response["result"]!["pong"]!.GetValue<bool>());
    }

    [Fact]
    public void UnknownAction_ReturnsError_AndHostSurvives()
    {
        var response = _host.Send(new { id = "t-bad", action = "explode" });

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Contains("explode", response["result"]!["error"]!.GetValue<string>());

        // The host must keep serving after a failed request.
        Assert.True(_host.Send(new { id = "t-after", action = "ping" })["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void MalformedPayload_ReturnsError_NotCrash()
    {
        var response = _host.Send(new { id = "t-malformed", action = "redact", payload = new { } });

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.True(_host.Send(new { id = "t-alive", action = "ping" })["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void Info_ReturnsPageGeometry()
    {
        string pdf = Convert.ToBase64String(TestPdfs.MultiPage(3));

        var response = _host.Send(new { id = "t-info", action = "info", payload = new { pdf } });

        Assert.True(response["ok"]!.GetValue<bool>());
        var result = response["result"]!;
        Assert.Equal(3, result["pageCount"]!.GetValue<int>());
        Assert.False(result["encrypted"]!.GetValue<bool>());
        Assert.Equal(TestPdfs.PageWidth, result["pages"]![0]!["width"]!.GetValue<float>());
    }

    [Fact]
    public void Render_ReturnsPngImage()
    {
        string pdf = Convert.ToBase64String(TestPdfs.WithText(("visible", 72, 700, 12)));

        var response = _host.Send(new
        {
            id = "t-render",
            action = "render",
            payload = new { pdf, page = 1, dpi = 72 }
        });

        Assert.True(response["ok"]!.GetValue<bool>());
        byte[] png = Convert.FromBase64String(response["result"]!["png"]!.GetValue<string>());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray()); // PNG magic
    }

    [Fact]
    public void LargeResponse_IsChunked_AndReassembles()
    {
        // A 2000-page document makes the base64 PDF comfortably exceed the ~900 KB
        // single-frame limit, forcing the host down the chunked-response path
        // (HostProcessFixture.ReadResponse reassembles the chunk frames).
        string pdf = Convert.ToBase64String(TestPdfs.MultiPage(2000,
            "Padding content to inflate the document size, page"));

        var response = _host.Send(new
        {
            id = "t-chunky",
            action = "merge",
            payload = new { pdfs = new[] { pdf, pdf } }
        }, TimeSpan.FromMinutes(3));

        Assert.True(response["ok"]!.GetValue<bool>());
        byte[] merged = Convert.FromBase64String(response["result"]!["pdf"]!.GetValue<string>());
        Assert.Equal(4000, PdfEditor.Core.PdfInspector.GetInfo(merged).PageCount);
    }

    [Fact]
    public void ChunkedRequest_IsReassembledByHost()
    {
        string pdf = Convert.ToBase64String(TestPdfs.WithText(("chunked request", 72, 700, 12)));

        var response = _host.SendChunked(new { id = "t-chunk-req", action = "info", payload = new { pdf } });

        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.Equal(1, response["result"]!["pageCount"]!.GetValue<int>());
    }

    [Fact]
    public void CreateCert_ReturnsUsablePkcs12()
    {
        var response = _host.Send(new
        {
            id = "t-cert",
            action = "create-cert",
            payload = new { name = "Integration Tester", password = "pw" }
        });

        Assert.True(response["ok"]!.GetValue<bool>());
        byte[] pfx = Convert.FromBase64String(response["result"]!["pfx"]!.GetValue<string>());
        byte[] signed = PdfEditor.Core.Signer.SignDigitally(
            TestPdfs.WithText(("doc", 72, 700, 12)), pfx, "pw");
        Assert.Single(PdfEditor.Core.Signer.GetSignatures(signed));
    }
}
