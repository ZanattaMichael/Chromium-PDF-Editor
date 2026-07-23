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
    public void ExtractLinks_CapturesTheHotspotRectangle()
    {
        // WithLinkTo puts the link annotation at Rect [72 700 272 720].
        byte[] pdf = TestPdfs.WithLinkTo("https://example.com");

        var link = Assert.Single(UrlTools.ExtractLinks(pdf));

        Assert.Equal(72, link.X, 0.5);
        Assert.Equal(700, link.Y, 0.5);
        Assert.Equal(200, link.Width, 0.5);
        Assert.Equal(20, link.Height, 0.5);
    }

    [Fact]
    public void ExtractLinks_NoLinks_ReturnsEmpty()
    {
        byte[] pdf = TestPdfs.WithText(("no links", 72, 700, 12));
        Assert.Empty(UrlTools.ExtractLinks(pdf));
    }

    [Fact]
    public void ExtractLinkAnnotations_IncludesUriAndNonUriLinks()
    {
        // A doc with a web link and a JavaScript link (like Salesforce "Close Window").
        byte[] pdf = WithLinks();

        var all = UrlTools.ExtractLinkAnnotations(pdf);

        // Both link annotations are returned, each with its hotspot rectangle...
        Assert.Equal(2, all.Count);
        Assert.Contains(all, l => l.Kind == "uri" && l.Url == "https://example.com" && l.Width > 0);
        Assert.Contains(all, l => l.Kind == "javascript" && l.Url == "" && l.Width > 0);

        // ...while URI-only extraction (used for scanning) still returns just the web link.
        var uris = UrlTools.ExtractLinks(pdf);
        Assert.Equal("https://example.com", Assert.Single(uris).Url);
    }

    /// <summary>A page with one URI link and one JavaScript-action link annotation.</summary>
    private static byte[] WithLinks()
    {
        using var output = new MemoryStream();
        using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfWriter(output)))
        {
            var page = doc.AddNewPage(new iText.Kernel.Geom.PageSize(595, 842));
            var uri = new iText.Kernel.Pdf.Annot.PdfLinkAnnotation(new iText.Kernel.Geom.Rectangle(72, 700, 200, 20));
            uri.SetAction(iText.Kernel.Pdf.Action.PdfAction.CreateURI("https://example.com"));
            page.AddAnnotation(uri);
            var js = new iText.Kernel.Pdf.Annot.PdfLinkAnnotation(new iText.Kernel.Geom.Rectangle(72, 660, 200, 20));
            js.SetAction(iText.Kernel.Pdf.Action.PdfAction.CreateJavaScript("window.close();"));
            page.AddAnnotation(js);
        }
        return output.ToArray();
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

    // A scripted Cloudflare endpoint: the scan POST returns a uuid, then the result GET returns
    // "not ready" a set number of times before yielding a verdict with the given malicious flag.
    private sealed class FakeCloudflare : HttpMessageHandler
    {
        private readonly bool _malicious;
        private readonly int _notReadyTimes;
        private int _polls;
        public int Submits { get; private set; }

        public FakeCloudflare(bool malicious, int notReadyTimes = 0)
        {
            _malicious = malicious;
            _notReadyTimes = notReadyTimes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                Submits++;
                return Task.FromResult(Json(System.Net.HttpStatusCode.OK, """{"uuid":"abc-123"}"""));
            }
            if (_polls++ < _notReadyTimes)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            string flag = _malicious ? "true" : "false";
            return Task.FromResult(Json(System.Net.HttpStatusCode.OK,
                "{\"verdicts\":{\"overall\":{\"malicious\":" + flag + "}}}"));
        }

        private static HttpResponseMessage Json(System.Net.HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    private static readonly CloudflareCredentials Creds = new("acct", "token");

    [Fact]
    public async Task ScanAsync_Cloudflare_MaliciousVerdict_ForcesRed()
    {
        var links = new[] { new PdfLink(1, "https://google.com") }; // heuristic green
        using var http = new HttpClient(new FakeCloudflare(malicious: true));

        var verdicts = await CloudflareUrlScanner.ScanAsync(links, Creds, http, TimeSpan.Zero);

        var v = Assert.Single(verdicts);
        Assert.Equal("red", v.Level);
        Assert.Equal("cloudflare", v.Source);
    }

    [Fact]
    public async Task ScanAsync_Cloudflare_PollsUntilReady_ThenReturnsClean()
    {
        var links = new[] { new PdfLink(1, "https://github.com/x") };
        using var http = new HttpClient(new FakeCloudflare(malicious: false, notReadyTimes: 2));

        var verdicts = await CloudflareUrlScanner.ScanAsync(links, Creds, http, TimeSpan.Zero);

        var v = Assert.Single(verdicts);
        Assert.Equal("yellow", v.Level); // heuristic level kept
        Assert.Equal("cloudflare", v.Source);
    }

    [Fact]
    public async Task ScanAsync_Cloudflare_DeduplicatesRepeatedUrls()
    {
        var links = new[]
        {
            new PdfLink(1, "https://dup.example"), new PdfLink(2, "https://dup.example"),
        };
        var handler = new FakeCloudflare(malicious: true);
        using var http = new HttpClient(handler);

        var verdicts = await CloudflareUrlScanner.ScanAsync(links, Creds, http, TimeSpan.Zero);

        Assert.Equal(2, verdicts.Count);
        Assert.Equal(1, handler.Submits); // the identical URL is only submitted once
    }
}
