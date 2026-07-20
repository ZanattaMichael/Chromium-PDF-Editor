using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PdfEditor.NativeHost.Tests;

/// <summary>Covers the dispatcher handlers added for the editor-menu features.</summary>
public class NewActionsTests
{
    private readonly MessageProcessor _processor = new();

    private static JsonObject Request(string action, object? payload = null) =>
        JsonNode.Parse(JsonSerializer.Serialize(new { id = "t1", action, payload }))!.AsObject();

    private JsonObject Handle(string action, object payload)
    {
        var frame = Assert.Single(_processor.Handle(Request(action, payload).ToJsonString()));
        return JsonNode.Parse(frame)!.AsObject();
    }

    private static bool Ok(JsonObject r) => r["ok"]!.GetValue<bool>();
    private static JsonNode Result(JsonObject r) => r["result"]!;

    [Fact]
    public void Rotate_ReturnsRotatedPdf()
    {
        var r = Handle("rotate", new { pdf = TestPdf.Base64(TestPdf.OnePage()), pages = new[] { 1 }, degrees = 90 });
        Assert.True(Ok(r));
        Assert.NotNull(Result(r)["pdf"]);
    }

    [Fact]
    public void AddText_ReturnsPdf()
    {
        var r = Handle("add-text", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()),
            region = new { page = 1, x = 72, y = 400, width = 200, height = 30 },
            text = "added", fontSize = 14, fontFamily = "times", color = "#112233",
        });
        Assert.True(Ok(r));
        Assert.NotNull(Result(r)["pdf"]);
    }

    [Fact]
    public void AddDrawing_ReturnsPdf()
    {
        var r = Handle("add-drawing", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()),
            page = 1,
            strokes = new[] { new[] { new { x = 100, y = 500 }, new { x = 300, y = 500 } } },
            color = "#ff0000", width = 3,
        });
        Assert.True(Ok(r));
        Assert.NotNull(Result(r)["pdf"]);
    }

    [Fact]
    public void FormFields_ListsField_ThenFillPersistsIt()
    {
        string pdf = TestPdf.Base64(TestPdf.WithField("who", "before"));

        var list = Handle("form-fields", new { pdf });
        Assert.True(Ok(list));
        var field = Assert.Single(Result(list)["fields"]!.AsArray());
        Assert.Equal("who", field!["name"]!.GetValue<string>());

        var filled = Handle("fill-form", new { pdf, values = new { who = "after" }, flatten = false });
        Assert.True(Ok(filled));
        Assert.NotNull(Result(filled)["pdf"]);
    }

    [Fact]
    public void ScanSafety_DetectsJavaScript_AndStripRemovesIt()
    {
        string pdf = TestPdf.Base64(TestPdf.WithJavaScript());

        var scan = Handle("scan-safety", new { pdf });
        Assert.True(Ok(scan));
        Assert.True(Result(scan)["hasActiveContent"]!.GetValue<bool>());

        var stripped = Handle("strip-active", new { pdf, javaScript = true, urls = true });
        Assert.True(Ok(stripped));
        Assert.NotNull(Result(stripped)["pdf"]);
    }

    [Fact]
    public void ListUrls_ReturnsTheLink()
    {
        var r = Handle("list-urls", new { pdf = TestPdf.Base64(TestPdf.WithLink("https://example.com/a")) });
        Assert.True(Ok(r));
        var link = Assert.Single(Result(r)["links"]!.AsArray());
        Assert.Equal("https://example.com/a", link!["url"]!.GetValue<string>());
    }

    [Fact]
    public void ScanUrls_WithoutCredentials_RatesWithHeuristic()
    {
        var r = Handle("scan-urls", new { pdf = TestPdf.Base64(TestPdf.WithLink("https://github.com/x")) });
        Assert.True(Ok(r));
        Assert.False(Result(r)["usedCloudflare"]!.GetValue<bool>());
        var verdict = Assert.Single(Result(r)["verdicts"]!.AsArray());
        Assert.Equal("yellow", verdict!["level"]!.GetValue<string>());
        Assert.Equal("heuristic", verdict["source"]!.GetValue<string>());
    }
}
