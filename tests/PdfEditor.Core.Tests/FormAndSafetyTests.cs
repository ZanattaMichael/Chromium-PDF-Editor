using PdfEditor.Core;

namespace PdfEditor.Tests;

public class FormToolsTests
{
    [Fact]
    public void ListFields_ReturnsTheTextField_WithItsValue()
    {
        byte[] pdf = TestPdfs.WithTextField("fullName", "Jane");

        var fields = FormTools.ListFields(pdf);

        var field = Assert.Single(fields);
        Assert.Equal("fullName", field.Name);
        Assert.Equal("text", field.Type);
        Assert.Equal("Jane", field.Value);
    }

    [Fact]
    public void ListFields_NoForm_ReturnsEmpty()
    {
        byte[] pdf = TestPdfs.WithText(("no form here", 72, 700, 12));
        Assert.Empty(FormTools.ListFields(pdf));
    }

    [Fact]
    public void FillFields_SetsTheValue_ReadableAfterwards()
    {
        byte[] pdf = TestPdfs.WithTextField("fullName");

        var result = FormTools.FillFields(pdf,
            new Dictionary<string, string> { ["fullName"] = "Alan Turing" });

        var field = Assert.Single(FormTools.ListFields(result.Pdf));
        Assert.Equal("Alan Turing", field.Value);
    }

    [Fact]
    public void FillFields_Flatten_RemovesTheEditableField()
    {
        byte[] pdf = TestPdfs.WithTextField("fullName");

        var result = FormTools.FillFields(pdf,
            new Dictionary<string, string> { ["fullName"] = "Flat" }, flatten: true);

        Assert.Empty(FormTools.ListFields(result.Pdf)); // no fillable fields remain
    }

    [Fact]
    public void FillFields_UnknownName_IsIgnored()
    {
        byte[] pdf = TestPdfs.WithTextField("fullName", "keep");
        var result = FormTools.FillFields(pdf,
            new Dictionary<string, string> { ["doesNotExist"] = "x" });
        Assert.Equal("keep", Assert.Single(FormTools.ListFields(result.Pdf)).Value);
    }

    [Fact]
    public void AddTextField_InsertsAFillableTextField()
    {
        byte[] pdf = TestPdfs.WithText(("plain page", 72, 700, 12));

        var result = FormTools.AddTextField(pdf, 1, new RectRegion(1, 100, 500, 200, 24), "email", "a@b.com");

        var field = Assert.Single(FormTools.ListFields(result.Pdf));
        Assert.Equal("email", field.Name);
        Assert.Equal("text", field.Type);
        Assert.Equal("a@b.com", field.Value);
    }

    [Fact]
    public void AddCheckbox_InsertsACheckbox()
    {
        byte[] pdf = TestPdfs.WithText(("plain page", 72, 700, 12));

        var result = FormTools.AddCheckbox(pdf, 1, new RectRegion(1, 100, 500, 16, 16), "agree");

        var field = Assert.Single(FormTools.ListFields(result.Pdf));
        Assert.Equal("agree", field.Name);
        Assert.Equal("checkbox", field.Type);
    }

    [Fact]
    public void AddTextField_AvoidsNameCollision()
    {
        byte[] pdf = TestPdfs.WithTextField("dupe", "one");

        var result = FormTools.AddTextField(pdf, 1, new RectRegion(1, 100, 400, 200, 24), "dupe");

        var names = FormTools.ListFields(result.Pdf).Select(f => f.Name).ToList();
        Assert.Contains("dupe", names);
        Assert.Contains("dupe_2", names); // the inserted one was renamed to avoid the clash
    }

    [Fact]
    public void AddTextField_InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FormTools.AddTextField(pdf, 9, new RectRegion(9, 0, 0, 10, 10), "n"));
    }
}

public class PdfSafetyTests
{
    [Fact]
    public void Scan_DetectsDocumentLevelJavaScript()
    {
        byte[] pdf = TestPdfs.WithOpenActionJavaScript("app.alert('x');");

        var report = PdfSafety.Scan(pdf);

        Assert.True(report.HasJavaScript);
        Assert.True(report.HasActiveContent);
    }

    [Fact]
    public void Scan_DetectsUrlActions()
    {
        byte[] pdf = TestPdfs.WithLinkAnnotation(72, 650, 120, 20); // carries a /URI link action

        var report = PdfSafety.Scan(pdf);

        Assert.True(report.HasUrlActions);
        Assert.Contains(report.Samples, s => s.Contains("example.com"));
    }

    [Fact]
    public void Scan_CleanDocument_ReportsNothing()
    {
        byte[] pdf = TestPdfs.WithText(("just text", 72, 700, 12));

        var report = PdfSafety.Scan(pdf);

        Assert.False(report.HasActiveContent);
        Assert.Equal(0, report.JavaScriptCount);
        Assert.Equal(0, report.UrlCount);
    }

    [Fact]
    public void StripActive_RemovesJavaScript()
    {
        byte[] pdf = TestPdfs.WithOpenActionJavaScript("app.alert('x');");
        Assert.True(PdfSafety.Scan(pdf).HasJavaScript);

        var result = PdfSafety.StripActive(pdf);

        Assert.False(PdfSafety.Scan(result.Pdf).HasActiveContent);
    }

    [Fact]
    public void StripActive_RemovesUrlActions()
    {
        byte[] pdf = TestPdfs.WithLinkAnnotation(72, 650, 120, 20);
        Assert.True(PdfSafety.Scan(pdf).HasUrlActions);

        var result = PdfSafety.StripActive(pdf);

        Assert.False(PdfSafety.Scan(result.Pdf).HasUrlActions);
    }

    [Fact]
    public void StripActive_KeepsPageContent()
    {
        byte[] pdf = TestPdfs.WithOpenActionJavaScript();
        var result = PdfSafety.StripActive(pdf);
        // Document still opens and renders (no exception, valid PDF).
        Assert.Equal(1, PdfInspector.GetInfo(result.Pdf).PageCount);
    }

    [Fact]
    public void StripActive_UrlsOnly_KeepsJavaScript()
    {
        byte[] pdf = TestPdfs.WithOpenActionJavaScript();

        var result = PdfSafety.StripActive(pdf, javaScript: false, urls: true);

        Assert.True(PdfSafety.Scan(result.Pdf).HasJavaScript); // JS left intact
    }

    [Fact]
    public void StripActive_JavaScriptOnly_KeepsUrlLinks()
    {
        byte[] pdf = TestPdfs.WithLinkTo("https://example.com");

        var result = PdfSafety.StripActive(pdf, javaScript: true, urls: false);

        Assert.True(PdfSafety.Scan(result.Pdf).HasUrlActions); // link left intact
    }
}
