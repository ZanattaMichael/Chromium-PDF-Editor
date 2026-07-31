using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfEditor.Core;

/// <summary>Cloudflare account credentials for the URL Scanner API.</summary>
public sealed record CloudflareCredentials(string AccountId, string ApiToken)
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(ApiToken);
}

/// <summary>
/// Rates the links in a document. Every URL always gets the local <see cref="UrlClassifier"/>
/// verdict; when Cloudflare credentials are supplied each URL is additionally submitted to the
/// Cloudflare URL Scanner and a malicious result upgrades the rating to red. Any Cloudflare
/// failure (no creds, network error, timeout) falls back cleanly to the heuristic rating, so the
/// report is always produced.
/// </summary>
public static class CloudflareUrlScanner
{
    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    /// <summary>How many Cloudflare scans may be in flight at once.</summary>
    public const int DefaultMaxConcurrency = 6;

    /// <summary>
    /// The most distinct URLs a single document will submit to Cloudflare. Beyond this, the
    /// remaining URLs keep their local heuristic verdict. Each scan is a submit plus up to a dozen
    /// polls, so without a ceiling a link-bomb document could enqueue thousands of round trips.
    /// </summary>
    public const int DefaultMaxUrls = 100;

    /// <summary>Rates every link, using Cloudflare when <paramref name="creds"/> is usable.</summary>
    public static async Task<IReadOnlyList<UrlVerdict>> ScanAsync(
        IReadOnlyList<PdfLink> links, CloudflareCredentials? creds,
        HttpClient? http = null, TimeSpan? pollDelay = null, CancellationToken ct = default,
        int maxConcurrency = DefaultMaxConcurrency, int maxUrls = DefaultMaxUrls)
    {
        var delay = pollDelay ?? TimeSpan.FromSeconds(2);
        bool useCloudflare = creds is { IsUsable: true };

        // No Cloudflare: every link gets the local heuristic and we make no network calls at all.
        if (!useCloudflare)
            return links.Select(l => Merge(UrlClassifier.Classify(l), null)).ToList();

        HttpClient? owned = http is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) } : null;
        HttpClient client = http ?? owned!;
        try
        {
            // Distinct URLs in first-seen order, capped: a document with thousands of links must not
            // turn into thousands of serialized scans. (Enumerable.Distinct preserves order.)
            var uniqueUrls = links.Select(l => l.Url)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, maxUrls))
                .ToList();

            // Scan those URLs concurrently rather than one-after-another. Each scan is a submit plus
            // up to ~24s of polling, so serial scanning made a link-heavy document take minutes and
            // hold the host busy the whole time (#82). A semaphore bounds how many run at once so we
            // neither starve the host nor hammer the Cloudflare API.
            var verdicts = new System.Collections.Concurrent.ConcurrentDictionary<string, bool?>(
                StringComparer.OrdinalIgnoreCase);
            using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            var scans = uniqueUrls.Select(async url =>
            {
                await gate.WaitAsync(ct);
                try { verdicts[url] = await TryVerdictAsync(client, creds!, url, delay, ct); }
                finally { gate.Release(); }
            });
            await Task.WhenAll(scans);

            // Map every link back to its URL's verdict; URLs past the cap have none and keep the
            // heuristic. Merge is applied per link so repeated URLs all reflect the one scan.
            return links.Select(l => Merge(UrlClassifier.Classify(l),
                verdicts.TryGetValue(l.Url, out var malicious) ? malicious : null)).ToList();
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// Combines the local heuristic with a Cloudflare malicious flag. A malicious flag forces red;
    /// a clean flag keeps the heuristic level but records that Cloudflare confirmed it.
    /// </summary>
    public static UrlVerdict Merge(UrlVerdict heuristic, bool? cloudflareMalicious)
    {
        if (cloudflareMalicious == true)
            return heuristic with { Level = "red", Category = "malicious", Source = "cloudflare",
                Detail = "Cloudflare URL Scanner flagged this as malicious." };
        if (cloudflareMalicious == false)
            return heuristic with { Source = "cloudflare",
                Detail = heuristic.Detail ?? "Cloudflare URL Scanner reported no threats." };
        return heuristic; // no Cloudflare result — heuristic stands
    }

    /// <summary>Submits a scan and polls for its verdict; returns null on any failure/timeout.</summary>
    private static async Task<bool?> TryVerdictAsync(HttpClient client, CloudflareCredentials creds,
        string url, TimeSpan pollDelay, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"{ApiBase}/accounts/{creds.AccountId}/urlscanner/v2/scan")
            {
                Content = JsonContent.Create(new { url }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiToken);
            var submit = await client.SendAsync(req, ct);
            if (!submit.IsSuccessStatusCode) return null;
            var uuid = JsonNode.Parse(await submit.Content.ReadAsStringAsync(ct))?["uuid"]?.GetValue<string>();
            if (string.IsNullOrEmpty(uuid)) return null;

            // The scan runs asynchronously; poll the result endpoint until it's ready.
            for (int attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(pollDelay, ct);
                var res = new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiBase}/accounts/{creds.AccountId}/urlscanner/v2/result/{uuid}");
                res.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiToken);
                var resp = await client.SendAsync(res, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) continue; // not ready yet
                if (!resp.IsSuccessStatusCode) return null;
                var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                var verdict = body?["verdicts"]?["overall"]?["malicious"];
                if (verdict != null) return verdict.GetValue<bool>();
                return false; // a result with no malicious flag = treated as clean
            }
            return null; // timed out waiting for the scan
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}
