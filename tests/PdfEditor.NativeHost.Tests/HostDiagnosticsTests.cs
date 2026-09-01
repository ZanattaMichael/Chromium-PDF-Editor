using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PdfEditor.NativeHost.Tests;

/// <summary>Covers the host self-diagnostics (the 'diagnostics' action and the CLI mode).</summary>
public class HostDiagnosticsTests
{
    private static JsonObject Handle(string action)
    {
        var request = JsonNode.Parse(JsonSerializer.Serialize(new { id = "d1", action }))!.AsObject();
        var frame = Assert.Single(MessageProcessor.Handle(request.ToJsonString()));
        return JsonNode.Parse(frame)!.AsObject();
    }

    [Fact]
    public void DiagnosticsAction_ReportsHostAndRuntime()
    {
        var r = Handle("diagnostics");
        Assert.True(r["ok"]!.GetValue<bool>());
        var result = r["result"]!.AsObject();
        Assert.Equal("com.pdfeditor.host", result["host"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(result["version"]!.GetValue<string>()));
        Assert.Contains(".NET", result["runtime"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(result["os"]!.GetValue<string>()));
        // ocrAvailable is a bool either way; just assert the field is present and typed.
        Assert.NotNull(result["ocrAvailable"]);
        Assert.IsType<bool>(result["ocrAvailable"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("--diagnostics")]
    [InlineData("--selftest")]
    [InlineData("--version")]
    public void TryRunCli_WithFlag_WritesJsonAndReturnsZero(string flag)
    {
        var writer = new StringWriter();
        int? exit = HostDiagnostics.TryRunCli(new[] { flag }, writer);
        Assert.Equal(0, exit);
        var doc = JsonNode.Parse(writer.ToString())!.AsObject();
        Assert.Equal("com.pdfeditor.host", doc["host"]!.GetValue<string>());
    }

    [Fact]
    public void TryRunCli_WithNoArgs_ReturnsNull_SoTheHostLoopRuns()
    {
        var writer = new StringWriter();
        Assert.Null(HostDiagnostics.TryRunCli(Array.Empty<string>(), writer));
        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public void TryRunCli_WithBrowserOrigin_ReturnsNull_SoARealLaunchIsNotIntercepted()
    {
        // Chrome launches the host with the calling extension's origin as argv[0].
        var writer = new StringWriter();
        Assert.Null(HostDiagnostics.TryRunCli(
            new[] { "chrome-extension://cbmfodojjlfppljbdebmpbcppngkkibi/" }, writer));
        Assert.Equal("", writer.ToString());
    }
}
