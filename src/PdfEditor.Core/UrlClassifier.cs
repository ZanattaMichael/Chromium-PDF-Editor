namespace PdfEditor.Core;

/// <summary>
/// Local, offline traffic-light rating for a URL based on its host. This is the baseline used
/// out of the box (and the fallback when the Cloudflare URL Scanner isn't configured or is
/// unreachable). Well-known destinations are green; code- and file-hosting (where anyone can host
/// arbitrary content) are yellow; obvious high-risk shapes are red; everything else is yellow —
/// unrecognised is treated as "proceed with caution", never silently green.
/// </summary>
public static class UrlClassifier
{
    // Suffix-matched: a host matches if it equals the entry or ends with "." + entry.
    private static readonly string[] KnownSafe =
    {
        "google.com", "youtube.com", "microsoft.com", "apple.com", "amazon.com", "wikipedia.org",
        "cloudflare.com", "mozilla.org", "adobe.com", "linkedin.com", "office.com", "live.com",
        "bing.com", "duckduckgo.com", "stackoverflow.com", "who.int", "nih.gov",
    };

    private static readonly string[] CodeHosting =
    {
        "github.com", "gitlab.com", "bitbucket.org", "sourceforge.net", "npmjs.com", "pypi.org",
        "nuget.org", "hub.docker.com", "codeberg.org", "gist.github.com",
    };

    private static readonly string[] FileHosting =
    {
        "dropbox.com", "drive.google.com", "mega.nz", "mediafire.com", "wetransfer.com",
        "onedrive.live.com", "box.com", "anonfiles.com", "gofile.io", "sendspace.com",
    };

    // URL shorteners hide the real destination — always at least caution.
    private static readonly string[] Shorteners =
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "cutt.ly", "rb.gy",
    };

    // High-risk shapes: TLDs frequently abused for malware/phishing.
    private static readonly string[] DangerousTlds =
    {
        ".zip", ".mov", ".xyz", ".top", ".tk", ".gq", ".ml", ".cf", ".ga", ".work", ".click",
    };

    public static UrlVerdict Classify(PdfLink link)
    {
        var (level, category, detail) = Rate(link.Url);
        return new UrlVerdict(link.Page, link.Url, level, category, "heuristic", detail);
    }

    public static (string Level, string Category, string? Detail) Rate(string url)
    {
        // Non-web schemes (javascript:, file:, data:) are dangerous regardless of any host.
        if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return ("red", "unsafe-scheme", "Non-web link scheme.");

        string? host = HostOf(url);
        if (host == null)
            return ("yellow", "unparseable", "Could not parse the host from this URL.");

        if (DangerousTlds.Any(t => host.EndsWith(t, StringComparison.OrdinalIgnoreCase)))
            return ("red", "risky-tld", $"Uses a frequently-abused top-level domain.");

        if (Matches(host, Shorteners))
            return ("yellow", "url-shortener", "Shortened link hides its true destination.");
        if (Matches(host, CodeHosting))
            return ("yellow", "code-hosting", "Code-hosting site — anyone can publish here.");
        if (Matches(host, FileHosting))
            return ("yellow", "file-hosting", "File-hosting site — anyone can upload files here.");
        if (host.EndsWith(".gov", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".edu", StringComparison.OrdinalIgnoreCase) ||
            Matches(host, KnownSafe))
            return ("green", "known-site", null);

        return ("yellow", "unknown", "Not a recognised site — treat with caution.");
    }

    private static bool Matches(string host, string[] domains) =>
        domains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                         host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    private static string? HostOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host.ToLowerInvariant();
        return null;
    }
}
