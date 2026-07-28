using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PdfEditor.NativeHost.Tests;

/// <summary>
/// Covers the <c>validate</c> action — the thin dispatcher wrapper around
/// <c>PdfEditor.Core.ExportValidator</c>.
/// </summary>
public class ValidateActionTests
{
    private static JsonObject Handle(object payload)
    {
        string request = JsonSerializer.Serialize(new { id = "v1", action = "validate", payload });
        var frame = Assert.Single(MessageProcessor.Handle(request));
        return JsonNode.Parse(frame)!.AsObject();
    }

    [Fact]
    public void CleanDocumentReportsValidWithNoFindings()
    {
        var response = Handle(new { pdf = TestPdf.Base64(TestPdf.OnePage()) });
        Assert.True(response["ok"]!.GetValue<bool>());
        var result = response["result"]!;
        Assert.True(result["valid"]!.GetValue<bool>());
        Assert.Equal(0, result["errorCount"]!.GetValue<int>());
        Assert.Empty(result["findings"]!.AsArray());
    }

    [Fact]
    public void CorruptDocumentReportsActionableFindings()
    {
        // A PDF whose trailing %%EOF marker was cut off: still base64-clean input, broken output.
        byte[] good = TestPdf.OnePage();
        string text = Encoding.Latin1.GetString(good);
        byte[] truncated = Encoding.Latin1.GetBytes(text[..text.LastIndexOf("%%EOF", StringComparison.Ordinal)]);

        var result = Handle(new { pdf = Convert.ToBase64String(truncated) })["result"]!;
        Assert.False(result["valid"]!.GetValue<bool>());
        Assert.True(result["errorCount"]!.GetValue<int>() >= 1);

        var finding = result["findings"]!.AsArray()
            .First(f => f!["code"]!.GetValue<string>() == "PDF002");
        Assert.Equal("error", finding!["severity"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(finding["location"]!.GetValue<string>()));
        Assert.Contains("%%EOF", finding["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("PDF002", result["log"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableBytesAreReportedRatherThanThrown()
    {
        var response = Handle(new { pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a pdf")) });
        // The action must answer with a report, not an error frame — validation never fails a save.
        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.False(response["result"]!["valid"]!.GetValue<bool>());
    }
}
