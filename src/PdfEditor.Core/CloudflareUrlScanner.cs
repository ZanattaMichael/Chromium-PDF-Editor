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

    /// <summary>Rates every link, using Cloudflare when <paramref name="creds"/> is usable.</summary>
    public static async Task<IReadOnlyList<UrlVerdict>> ScanAsync(
        IReadOnlyList<PdfLink> links, CloudflareCredentials? creds,
        HttpClient? http = null, CancellationToken ct = default)
    {
        var results = new List<UrlVerdict>();
        bool useCloudflare = creds is { IsUsable: true };
        HttpClient? client = useCloudflare ? (http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }) : null;

        // De-duplicate identical URLs so we don't scan the same link many times.
        var scanned = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            var heuristic = UrlClassifier.Classify(link);
            bool? malicious = null;
            if (useCloudflare)
            {
                if (!scanned.TryGetValue(link.Url, out malicious))
                {
                    malicious = await TryVerdictAsync(client!, creds!, link.Url, ct);
                    scanned[link.Url] = malicious;
                }
            }
            results.Add(Merge(heuristic, malicious));
        }
        return results;
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
        string url, CancellationToken ct)
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
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
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
