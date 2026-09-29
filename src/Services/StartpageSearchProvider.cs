namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Engine;

/// <summary>
/// Local web.search via Startpage. No API key. Startpage fronts Anubis proof-of-work; this
/// adapter solves it, keeps the auth cookie on the <see cref="HttpClient"/>, and parses hits.
/// </summary>
public sealed partial class StartpageSearchProvider : ISearchProvider
{
    public const string SearchUrl = "https://www.startpage.com/sp/search";
    private const string BrowserUa = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15";
    private const string PassChallengePath = "/.within.website/x/cmd/anubis/api/pass-challenge";

    private readonly HttpClient _httpClient;

    public StartpageSearchProvider(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            return;
        }

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUa);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
    }

    public async Task<IReadOnlyList<WebHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var requestUri = $"{SearchUrl}?query={Uri.EscapeDataString(query.Text)}&cat=web&language=english";
        var (status, html) = await GetHtmlAsync(requestUri, cancellationToken);
        html = await PassAnubisIfNeededAsync(html, requestUri, cancellationToken);
        var hits = ParseHits(html, query.Limit);
        if (hits.Count == 0)
        {
            throw new InvalidOperationException(Diagnose(status, html));
        }

        HostLog.Line("web.search", $"ok {hits.Count} hits, {html.Length}B");
        return hits;
    }

    public static IReadOnlyList<WebHit> ParseHits(string html, int limit)
    {
        var cap = Math.Clamp(limit, 1, 10);
        var organic = SliceOrganicJson(html);
        var hits = ParseEmbeddedResults(organic, cap);
        if (hits.Count == 0)
        {
            hits = ParseEmbeddedResults(html, cap);
        }

        return hits.Count > 0 ? hits : ParseMarkupResults(html, cap);
    }

    public static (int Nonce, string Hash) SolveProofOfWork(string randomData, int difficulty)
    {
        var prefix = new string('0', Math.Max(0, difficulty));
        var nonce = 0;
        while (true)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(randomData + nonce))).ToLowerInvariant();
            if (hash.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (nonce, hash);
            }

            nonce++;
        }
    }

    public static string Diagnose(HttpStatusCode status, string html)
    {
        var kind = html.Contains("captcha", StringComparison.OrdinalIgnoreCase) ? "captcha"
            : html.Contains("anubis_challenge", StringComparison.OrdinalIgnoreCase)
                ? (TryReadAnubisChallenge(html) is null ? "anubis-unparsed" : "anubis-challenge")
            : html.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ? "access-denied"
            : html.Length < 800 ? "empty-page"
            : "no-organic-hits";
        return $"HTTP {(int)status}, {html.Length}B, {kind}";
    }

    private async Task<(HttpStatusCode Status, string Html)> GetHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(Diagnose(response.StatusCode, html));
        }

        return (response.StatusCode, html);
    }

    private async Task<string> PassAnubisIfNeededAsync(string html, string originalUrl, CancellationToken cancellationToken)
    {
        var challenge = TryReadAnubisChallenge(html);
        if (challenge is null)
        {
            if (html.Contains("anubis_challenge", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("anubis JSON not parsed, " + Diagnose(HttpStatusCode.OK, html));
            }

            return html;
        }

        var started = DateTime.UtcNow;
        var (nonce, hash) = SolveProofOfWork(challenge.RandomData, challenge.Difficulty);
        HostLog.Line("web.search", $"anubis difficulty={challenge.Difficulty} nonce={nonce} { (int)(DateTime.UtcNow - started).TotalMilliseconds }ms");

        var original = new Uri(originalUrl);
        var passUri = new Uri(original, PassChallengePath).ToString()
            + "?id=" + Uri.EscapeDataString(challenge.Id)
            + "&response=" + Uri.EscapeDataString(hash)
            + "&nonce=" + nonce
            + "&redir=" + Uri.EscapeDataString(original.PathAndQuery)
            + "&elapsedTime=0";

        var (status, passed) = await GetHtmlAsync(passUri, cancellationToken);
        if (ParseHits(passed, 1).Count > 0)
        {
            return passed;
        }

        throw new InvalidOperationException("anubis pass: " + Diagnose(status, passed));
    }

    public static AnubisChallenge? TryReadAnubisChallenge(string html)
    {
        var json = ExtractAnubisJson(html);
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("challenge", out var challenge))
        {
            return null;
        }

        var id = challenge.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var randomData = challenge.TryGetProperty("randomData", out var dataEl) ? dataEl.GetString() : null;
        var difficulty = challenge.TryGetProperty("difficulty", out var diffEl) && diffEl.TryGetInt32(out var d)
            ? d
            : root.TryGetProperty("rules", out var rules) && rules.TryGetProperty("difficulty", out var rulesDiff) && rulesDiff.TryGetInt32(out var rd)
                ? rd
                : 0;

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(randomData) || difficulty <= 0)
        {
            return null;
        }

        return new AnubisChallenge(id, randomData, difficulty);
    }

    public static string? ExtractAnubisJson(string html)
    {
        const string marker = "id=\"anubis_challenge\"";
        var markerAt = html.IndexOf(marker, StringComparison.Ordinal);
        if (markerAt < 0)
        {
            return null;
        }

        var start = html.IndexOf('{', markerAt);
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return html[start..(i + 1)];
                }
            }
        }

        return null;
    }

    private static List<WebHit> ParseEmbeddedResults(string html, int limit)
    {
        var hits = new List<WebHit>();
        foreach (Match match in EmbeddedResult().Matches(html))
        {
            var title = Clean(match.Groups["title"].Value);
            var url = DecodeJsonString(match.Groups["url"].Value);
            var snippet = Clean(match.Groups["snippet"].Value);
            if (!IsOrganicHit(title, url))
            {
                continue;
            }

            hits.Add(new WebHit(title, url, snippet, Date: null));
            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }

    private static List<WebHit> ParseMarkupResults(string html, int limit)
    {
        var hits = new List<WebHit>();
        foreach (Match match in MarkupResult().Matches(html))
        {
            var title = Clean(match.Groups["title"].Value);
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
            var snippet = Clean(match.Groups["snippet"].Value);
            if (!IsOrganicHit(title, url))
            {
                continue;
            }

            hits.Add(new WebHit(title, url, snippet, Date: null));
            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }

    private static string SliceOrganicJson(string html)
    {
        const string marker = "\"display_type\":\"web-google\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return html;
        }

        var next = html.IndexOf("\"display_type\":\"", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? html[start..] : html[start..next];
    }

    internal static bool IsOrganicHit(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 180)
        {
            return false;
        }

        if (title.Contains('{', StringComparison.Ordinal)
            || title.Contains("thash", StringComparison.Ordinal)
            || title.Contains("sourceIndex", StringComparison.Ordinal)
            || title.Contains("\",\"", StringComparison.Ordinal)
            || title.Contains("impressionToken", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return !uri.Host.Contains("startpage.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string Clean(string raw)
    {
        var decoded = WebUtility.HtmlDecode(DecodeJsonString(raw));
        decoded = Regex.Replace(decoded, "<.*?>", string.Empty);
        return string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string DecodeJsonString(string raw) =>
        raw.Replace("\\/", "/").Replace("\\\"", "\"").Replace("\\n", " ");

    public sealed record AnubisChallenge(string Id, string RandomData, int Difficulty);

    [GeneratedRegex("\"title\":\"(?<title>[^\"]{1,200})\",\"clickUrl\":\"(?<url>https?:[^\"]{8,400})\"(?:,\"description\":\"(?<snippet>[^\"]{0,500})\")?")]
    private static partial Regex EmbeddedResult();

    [GeneratedRegex("""<a class="[^"]*result-title result-link[^"]*" href="(?<url>https?://[^"]+)"[^>]*>.*?<h2[^>]*>(?<title>.*?)</h2>.*?<p class="description[^"]*">(?<snippet>.*?)</p>""", RegexOptions.Singleline)]
    private static partial Regex MarkupResult();
}
