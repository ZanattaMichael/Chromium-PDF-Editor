using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class RedactionReporterTests
{
    [Fact]
    public void Analyze_CountsTextRuns_AndCapturesTheRemovedText()
    {
        byte[] pdf = TestPdfs.WithText(("SECRET", 72, 700, 12));
        var match = Assert.Single(TextTools.FindText(pdf, "SECRET"));
        var region = new RectRegion(1, match.X, match.Y, match.Width, match.Height);

        var report = RedactionReporter.Analyze(pdf, new[] { region });

        Assert.Equal(1, report.PagesAffected);
        Assert.True(report.Totals.TextRuns >= 1, "the redacted text run should be counted");
        Assert.Equal(1, report.Regions);
        Assert.Equal(1, report.Pages.Single().Page);
        // The compliance detail records the actual text that was removed.
        var detail = Assert.Single(report.RegionDetails);
        Assert.Contains("SECRET", detail.Removed.Text);
    }

    [Fact]
    public void Analyze_CountsImages_AndCapturesAThumbnail()
    {
        byte[] pdf = TestPdfs.WithImage(100, 600, 200, 100);

        var report = RedactionReporter.Analyze(pdf, new[] { new RectRegion(1, 100, 600, 200, 100) });

        Assert.Equal(1, report.Totals.Images);
        var detail = Assert.Single(report.RegionDetails);
        Assert.Equal(1, detail.Removed.Images);
        // A base64 PNG thumbnail of the removed image is embedded for the report.
        string thumb = Assert.Single(detail.Removed.ImageThumbnails);
        Assert.NotEmpty(System.Convert.FromBase64String(thumb));
    }

    [Fact]
    public void Analyze_CountsAnnotations_UnderARegion()
    {
        byte[] pdf = TestPdfs.WithLinkAnnotation(80, 500, 160, 20);

        var report = RedactionReporter.Analyze(pdf, new[] { new RectRegion(1, 80, 500, 160, 20) });

        Assert.Equal(1, report.Totals.Annotations);
    }

    [Fact]
    public void Analyze_ReportsPerPage_ForRegionsOnDifferentPages()
    {
        byte[] pdf = TestPdfs.MultiPage(3);

        var report = RedactionReporter.Analyze(pdf, new[]
        {
            new RectRegion(1, 50, 50, 100, 100),
            new RectRegion(3, 50, 50, 100, 100),
        });

        Assert.Equal(2, report.PagesAffected);
        Assert.Equal(new[] { 1, 3 }, report.Pages.Select(p => p.Page).ToArray());
        Assert.Equal(new[] { 1, 3 }, report.RegionDetails.Select(r => r.Page).ToArray());
    }

    [Fact]
    public void Analyze_FlagsRemainingJavaScript_ThatRedactionDoesNotRemove()
    {
        byte[] pdf = TestPdfs.WithOpenActionJavaScript("app.alert('hi');");

        var report = RedactionReporter.Analyze(pdf, new[] { new RectRegion(1, 50, 50, 100, 100) });

        Assert.True(report.Residual.RemainingJavaScript > 0,
            "embedded JavaScript survives redaction and should be flagged");
    }

    [Fact]
    public void Analyze_FlagsRemainingMetadata_ThatRedactionDoesNotRemove()
    {
        // WithHiddenData carries an author/title in the Info dictionary and document JavaScript.
        byte[] pdf = TestPdfs.WithHiddenData();

        var report = RedactionReporter.Analyze(pdf, new[] { new RectRegion(1, 60, 740, 200, 20) });

        Assert.True(report.Residual.RemainingMetadata, "identifying metadata should be flagged");
        Assert.True(report.Residual.RemainingJavaScript > 0, "document JavaScript should be flagged");
    }

    [Fact]
    public void Analyze_OutOfRangePages_AreIgnored()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));

        var report = RedactionReporter.Analyze(pdf, new[] { new RectRegion(9, 0, 0, 10, 10) });

        Assert.Equal(0, report.PagesAffected);
        Assert.Empty(report.Pages);
        Assert.Empty(report.RegionDetails);
    }
}
