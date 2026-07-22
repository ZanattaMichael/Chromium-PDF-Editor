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
    public void AddHighlight_ReturnsPdf()
    {
        var r = Handle("add-highlight", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()),
            page = 1,
            rects = new[] { new { x = 60, y = 690, width = 200, height = 18 } },
            color = "#ffeb3b",
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
    public void AddFormField_InsertsField_ThenListShowsIt()
    {
        var added = Handle("add-form-field", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()),
            region = new { page = 1, x = 100, y = 500, width = 200, height = 24 },
            fieldType = "text", name = "email", value = "x@y.com",
        });
        Assert.True(Ok(added));
        string newPdf = Result(added)["pdf"]!.GetValue<string>();

        var list = Handle("form-fields", new { pdf = newPdf });
        var field = Assert.Single(Result(list)["fields"]!.AsArray());
        Assert.Equal("email", field!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ArrangePages_ReordersAndDrops()
    {
        var r = Handle("arrange-pages", new
        {
            pdf = TestPdf.Base64(TestPdf.ManyPages(3)),
            order = new[] { 3, 1 }, // keep pages 3 then 1, drop page 2
        });
        Assert.True(Ok(r));
        string arranged = Result(r)["pdf"]!.GetValue<string>();

        var info = Handle("info", new { pdf = arranged });
        Assert.Equal(2, Result(info)["pageCount"]!.GetValue<int>());
    }

    [Fact]
    public void AddFormField_Dropdown_InsertsChoiceField()
    {
        var added = Handle("add-form-field", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()),
            region = new { page = 1, x = 100, y = 500, width = 200, height = 24 },
            fieldType = "dropdown", name = "country",
            options = new[] { "Australia", "Canada" },
        });
        Assert.True(Ok(added));

        var list = Handle("form-fields", new { pdf = Result(added)["pdf"]!.GetValue<string>() });
        var field = Assert.Single(Result(list)["fields"]!.AsArray());
        Assert.Equal("country", field!["name"]!.GetValue<string>());
        Assert.Equal("choice", field["type"]!.GetValue<string>());
    }

    [Fact]
    public void AddFormField_Multiline_InsertsTextField()
    {
        var added = Handle("add-form-field", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()),
            region = new { page = 1, x = 100, y = 400, width = 200, height = 80 },
            fieldType = "multiline", name = "comments",
        });
        Assert.True(Ok(added));

        var list = Handle("form-fields", new { pdf = Result(added)["pdf"]!.GetValue<string>() });
        Assert.Equal("comments", Assert.Single(Result(list)["fields"]!.AsArray())!["name"]!.GetValue<string>());
    }

    [Fact]
    public void AddScript_ThenListScripts_RoundTripsTheSource()
    {
        var added = Handle("add-script", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()),
            name = "calc", script = "app.alert('hi');",
        });
        Assert.True(Ok(added));
        string withJs = Result(added)["pdf"]!.GetValue<string>();

        var list = Handle("list-scripts", new { pdf = withJs });
        var script = Assert.Single(Result(list)["scripts"]!.AsArray());
        Assert.Equal("calc", script!["name"]!.GetValue<string>());
        Assert.Equal("app.alert('hi');", script["script"]!.GetValue<string>());

        // And scan-safety flags the freshly-added script.
        var scan = Handle("scan-safety", new { pdf = withJs });
        Assert.True(Result(scan)["hasActiveContent"]!.GetValue<bool>());
    }

    [Fact]
    public void RemoveScript_DropsTheNamedScript()
    {
        string withJs = Result(Handle("add-script", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()), name = "s", script = "1;",
        }))["pdf"]!.GetValue<string>();

        var removed = Handle("remove-script", new { pdf = withJs, name = "s" });
        Assert.True(Ok(removed));

        var list = Handle("list-scripts", new { pdf = Result(removed)["pdf"]!.GetValue<string>() });
        Assert.Empty(Result(list)["scripts"]!.AsArray());
    }

    [Fact]
    public void AddFormField_Button_InsertsButtonFieldWithScript()
    {
        var added = Handle("add-form-field", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()),
            region = new { page = 1, x = 100, y = 500, width = 90, height = 24 },
            fieldType = "button", name = "go", caption = "Go",
            script = "app.alert('clicked');",
        });
        Assert.True(Ok(added));

        var list = Handle("form-fields", new { pdf = Result(added)["pdf"]!.GetValue<string>() });
        var field = Assert.Single(Result(list)["fields"]!.AsArray());
        Assert.Equal("go", field!["name"]!.GetValue<string>());
        Assert.Equal("button", field["type"]!.GetValue<string>());
    }

    [Fact]
    public void InspectHidden_ThenSanitize_RemovesTheHiddenData()
    {
        // A doc with a document-level script is the hidden data we can build with TestPdf here.
        string withJs = Result(Handle("add-script", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()), name = "s", script = "app.alert(1);",
        }))["pdf"]!.GetValue<string>();

        var inspect = Handle("inspect-hidden", new { pdf = withJs });
        Assert.True(Ok(inspect));
        Assert.True(Result(inspect)["hasAny"]!.GetValue<bool>());
        Assert.True(Result(inspect)["scriptsAndActions"]!.GetValue<int>() > 0);

        var sanitized = Handle("sanitize", new { pdf = withJs });
        Assert.True(Ok(sanitized));

        var after = Handle("inspect-hidden", new { pdf = Result(sanitized)["pdf"]!.GetValue<string>() });
        Assert.Equal(0, Result(after)["scriptsAndActions"]!.GetValue<int>());
    }

    [Fact]
    public void Sanitize_SelectiveOptions_KeepScriptsWhenNotAsked()
    {
        string withJs = Result(Handle("add-script", new
        {
            pdf = TestPdf.Base64(TestPdf.OnePage()), name = "s", script = "app.alert(1);",
        }))["pdf"]!.GetValue<string>();

        // Only strip metadata; scripts must survive.
        var sanitized = Handle("sanitize", new
        {
            pdf = withJs, metadata = true, attachments = false, scriptsAndActions = false,
            annotations = false, bookmarks = false, hiddenLayers = false,
        });
        var after = Handle("inspect-hidden", new { pdf = Result(sanitized)["pdf"]!.GetValue<string>() });
        Assert.True(Result(after)["scriptsAndActions"]!.GetValue<int>() > 0);
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

    // A minimal valid 1x1 PNG.
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public void MergeFiles_AppendsAConvertedImage_AsAPage()
    {
        var r = Handle("merge-files", new
        {
            files = new object[]
            {
                new { data = TestPdf.Base64(TestPdf.OnePage()), kind = "pdf" },
                new { data = OnePixelPng, kind = "image" },
            },
        });
        Assert.True(Ok(r));
        string merged = Result(r)["pdf"]!.GetValue<string>();

        var info = Handle("info", new { pdf = merged });
        Assert.Equal(2, Result(info)["pageCount"]!.GetValue<int>());
    }

    [Fact]
    public void PageText_ReturnsRunsWithPositions()
    {
        var r = Handle("page-text", new { pdf = TestPdf.Base64(TestPdf.OnePage("hello world")), page = 1 });
        Assert.True(Ok(r));
        var span = Assert.Single(Result(r)["spans"]!.AsArray());
        Assert.Contains("hello", span!["text"]!.GetValue<string>());
        Assert.True(span["width"]!.GetValue<double>() > 0);
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
