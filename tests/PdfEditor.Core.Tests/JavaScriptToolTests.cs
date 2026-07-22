using PdfEditor.Core;

namespace PdfEditor.Tests;

public class JavaScriptToolTests
{
    [Fact]
    public void AddDocumentScript_IsDetectableAndReadableBack()
    {
        byte[] pdf = TestPdfs.WithText(("plain", 72, 700, 12));

        var result = JavaScriptTool.AddDocumentScript(pdf, "greet", "app.alert('hi');");

        // The safety scanner (used elsewhere to flag active content) now sees the script...
        Assert.True(PdfSafety.Scan(result.Pdf).HasJavaScript);
        // ...and it round-trips with its name and source intact.
        var script = Assert.Single(JavaScriptTool.ListScripts(result.Pdf));
        Assert.Equal("greet", script.Name);
        Assert.Equal("app.alert('hi');", script.Script);
    }

    [Fact]
    public void ListScripts_CleanDocument_IsEmpty()
    {
        Assert.Empty(JavaScriptTool.ListScripts(TestPdfs.WithText(("no scripts", 72, 700, 12))));
    }

    [Fact]
    public void AddDocumentScript_SameName_ReplacesRatherThanDuplicates()
    {
        byte[] pdf = TestPdfs.WithText(("plain", 72, 700, 12));

        byte[] once = JavaScriptTool.AddDocumentScript(pdf, "calc", "var a = 1;").Pdf;
        byte[] twice = JavaScriptTool.AddDocumentScript(once, "calc", "var a = 2;").Pdf;

        var script = Assert.Single(JavaScriptTool.ListScripts(twice));
        Assert.Equal("var a = 2;", script.Script);
    }

    [Fact]
    public void AddDocumentScript_DifferentNames_AreBothKept()
    {
        byte[] pdf = TestPdfs.WithText(("plain", 72, 700, 12));

        byte[] one = JavaScriptTool.AddDocumentScript(pdf, "first", "1;").Pdf;
        byte[] two = JavaScriptTool.AddDocumentScript(one, "second", "2;").Pdf;

        var names = JavaScriptTool.ListScripts(two).Select(s => s.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "first", "second" }, names);
        Assert.Equal(2, PdfSafety.Scan(two).JavaScriptCount);
    }

    [Fact]
    public void RemoveScript_RemovesTheNamedOne_KeepsOthers()
    {
        byte[] pdf = TestPdfs.WithText(("plain", 72, 700, 12));
        byte[] one = JavaScriptTool.AddDocumentScript(pdf, "keep", "1;").Pdf;
        byte[] two = JavaScriptTool.AddDocumentScript(one, "drop", "2;").Pdf;

        byte[] result = JavaScriptTool.RemoveScript(two, "drop").Pdf;

        var script = Assert.Single(JavaScriptTool.ListScripts(result));
        Assert.Equal("keep", script.Name);
    }

    [Fact]
    public void RemoveScript_LastOne_LeavesNoJavaScript()
    {
        byte[] pdf = JavaScriptTool.AddDocumentScript(
            TestPdfs.WithText(("plain", 72, 700, 12)), "only", "1;").Pdf;

        byte[] result = JavaScriptTool.RemoveScript(pdf, "only").Pdf;

        Assert.Empty(JavaScriptTool.ListScripts(result));
        Assert.False(PdfSafety.Scan(result).HasJavaScript);
    }

    [Fact]
    public void RemoveScript_UnknownName_IsANoOp()
    {
        byte[] pdf = JavaScriptTool.AddDocumentScript(
            TestPdfs.WithText(("plain", 72, 700, 12)), "keep", "1;").Pdf;

        byte[] result = JavaScriptTool.RemoveScript(pdf, "does-not-exist").Pdf;

        Assert.Equal("keep", Assert.Single(JavaScriptTool.ListScripts(result)).Name);
    }

    [Theory]
    [InlineData("", "app.alert('x');")]
    [InlineData("   ", "app.alert('x');")]
    [InlineData("name", "")]
    public void AddDocumentScript_MissingNameOrBody_Throws(string name, string script)
    {
        byte[] pdf = TestPdfs.WithText(("plain", 72, 700, 12));
        Assert.Throws<ArgumentException>(() => JavaScriptTool.AddDocumentScript(pdf, name, script));
    }

    [Fact]
    public void AddDocumentScript_PreservesMultiLineSource()
    {
        byte[] pdf = TestPdfs.WithText(("plain", 72, 700, 12));
        const string src = "var total = 0;\nfor (var i = 0; i < 3; i++) total += i;\napp.alert(total);";

        byte[] result = JavaScriptTool.AddDocumentScript(pdf, "loop", src).Pdf;

        Assert.Equal(src, Assert.Single(JavaScriptTool.ListScripts(result)).Script);
    }

    [Fact]
    public void AddDocumentScript_ThenStripActive_RemovesIt()
    {
        // The safety net still works: a document script added here is removed by the JS stripper,
        // so "strip active content on save" continues to protect a downstream reader.
        byte[] pdf = JavaScriptTool.AddDocumentScript(
            TestPdfs.WithText(("plain", 72, 700, 12)), "s", "app.alert('x');").Pdf;

        var stripped = PdfSafety.StripActive(pdf, javaScript: true, urls: false);

        Assert.Empty(JavaScriptTool.ListScripts(stripped.Pdf));
    }

    [Fact]
    public void AddDocumentScript_OnEncryptedDocument_WorksWithPassword()
    {
        byte[] locked = Encryptor.Encrypt(TestPdfs.WithText(("plain", 72, 700, 12)), "pw");

        byte[] result = JavaScriptTool.AddDocumentScript(locked, "s", "1;", "pw").Pdf;

        Assert.Single(JavaScriptTool.ListScripts(result, "pw"));
    }

    // ------------------------------------------------------ JavaScript push button

    [Fact]
    public void AddButton_WithScript_InsertsAButtonFieldThatCarriesJavaScript()
    {
        byte[] pdf = TestPdfs.WithText(("plain", 72, 700, 12));

        var result = FormTools.AddButton(pdf, 1, new RectRegion(1, 100, 500, 80, 24),
            "submit", "Submit", "this.submitForm('https://example.com');");

        var field = Assert.Single(FormTools.ListFields(result.Pdf));
        Assert.Equal("submit", field.Name);
        Assert.Equal("button", field.Type);
        // The activation JavaScript is present on the widget (an outward-reaching script).
        Assert.True(PdfSafety.Scan(result.Pdf).HasActiveContent);
    }

    [Fact]
    public void AddButton_WithoutScript_IsAPlainButton_NoActiveContent()
    {
        byte[] pdf = TestPdfs.WithText(("plain", 72, 700, 12));

        var result = FormTools.AddButton(pdf, 1, new RectRegion(1, 100, 500, 80, 24), "noop", "OK");

        Assert.Equal("button", Assert.Single(FormTools.ListFields(result.Pdf)).Type);
        Assert.False(PdfSafety.Scan(result.Pdf).HasActiveContent);
    }

    [Fact]
    public void AddButton_InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("plain", 72, 700, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FormTools.AddButton(pdf, 9, new RectRegion(9, 0, 0, 40, 12), "b", "B", "1;"));
    }
}
