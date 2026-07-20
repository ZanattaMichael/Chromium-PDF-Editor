using PdfEditor.Core;

namespace PdfEditor.Tests;

public class UrlExtractionTests
{
    [Fact]
    public void ExtractLinks_FindsTheUrl_AndItsPage()
    {
        byte[] pdf = TestPdfs.WithLinkTo("https://example.com/report");

        var links = UrlTools.ExtractLinks(pdf);

        var link = Assert.Single(links);
        Assert.Equal(1, link.Page);
        Assert.Equal("https://example.com/report", link.Url);
    }

    [Fact]
    public void ExtractLinks_NoLinks_ReturnsEmpty()
    {
        byte[] pdf = TestPdfs.WithText(("no links", 72, 700, 12));
        Assert.Empty(UrlTools.ExtractLinks(pdf));
    }
}

public class UrlClassifierTests
{
    [Theory]
    [InlineData("https://www.google.com/search?q=x", "green")]
    [InlineData("https://mail.google.com", "green")]
    [InlineData("https://en.wikipedia.org/wiki/PDF", "green")]
    [InlineData("https://irs.gov/forms", "green")]
    public void KnownSites_AreGreen(string url, string expected)
        => Assert.Equal(expected, UrlClassifier.Rate(url).Level);

    [Theory]
    [InlineData("https://github.com/user/repo", "code-hosting")]
    [InlineData("https://gitlab.com/x", "code-hosting")]
    [InlineData("https://www.dropbox.com/s/abc/file.zip", "file-hosting")]
    [InlineData("https://bit.ly/3xyz", "url-shortener")]
    public void CodeAndFileHosting_AreYellow(string url, string category)
    {
        var (level, cat, _) = UrlClassifier.Rate(url);
        Assert.Equal("yellow", level);
        Assert.Equal(category, cat);
    }

    [Theory]
    [InlineData("https://free-prizes.xyz/claim")]
    [InlineData("https://download.tk/thing")]
    [InlineData("javascript:alert(1)")]
    public void RiskyShapes_AreRed(string url)
        => Assert.Equal("red", UrlClassifier.Rate(url).Level);

    [Fact]
    public void UnrecognisedSite_DefaultsToYellowCaution_NeverSilentlyGreen()
    {
        var (level, category, _) = UrlClassifier.Rate("https://some-random-blog.example/post");
        Assert.Equal("yellow", level);
        Assert.Equal("unknown", category);
    }

    [Fact]
    public void Classify_CarriesPageAndSource()
    {
        var verdict = UrlClassifier.Classify(new PdfLink(3, "https://github.com/x"));
        Assert.Equal(3, verdict.Page);
        Assert.Equal("heuristic", verdict.Source);
        Assert.Equal("yellow", verdict.Level);
    }
}

public class CloudflareMergeTests
{
    private static UrlVerdict Heuristic(string level = "yellow") =>
        new(1, "https://x.example", level, "unknown", "heuristic");

    [Fact]
    public void Merge_MaliciousFlag_ForcesRed_FromCloudflare()
    {
        var merged = CloudflareUrlScanner.Merge(Heuristic("green"), cloudflareMalicious: true);
        Assert.Equal("red", merged.Level);
        Assert.Equal("malicious", merged.Category);
        Assert.Equal("cloudflare", merged.Source);
    }

    [Fact]
    public void Merge_CleanFlag_KeepsHeuristicLevel_ButMarksSource()
    {
        var merged = CloudflareUrlScanner.Merge(Heuristic("yellow"), cloudflareMalicious: false);
        Assert.Equal("yellow", merged.Level);
        Assert.Equal("cloudflare", merged.Source);
    }

    [Fact]
    public void Merge_NoCloudflareResult_LeavesHeuristicUntouched()
    {
        var h = Heuristic("green");
        var merged = CloudflareUrlScanner.Merge(h, cloudflareMalicious: null);
        Assert.Equal("green", merged.Level);
        Assert.Equal("heuristic", merged.Source);
    }

    [Fact]
    public async Task ScanAsync_WithoutCredentials_FallsBackToHeuristic()
    {
        var links = new[] { new PdfLink(1, "https://github.com/x"), new PdfLink(1, "https://google.com") };
        var verdicts = await CloudflareUrlScanner.ScanAsync(links, creds: null);

        Assert.Collection(verdicts,
            v => { Assert.Equal("yellow", v.Level); Assert.Equal("heuristic", v.Source); },
            v => { Assert.Equal("green", v.Level); Assert.Equal("heuristic", v.Source); });
    }
}
