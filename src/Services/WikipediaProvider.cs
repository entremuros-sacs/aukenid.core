namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Engine;

/// <summary>
/// Desktop wiki adapter: English Wikipedia + Wikidata description for the top hit.
/// Typed HttpClient, no vendor NuGet.
/// </summary>
public sealed partial class WikipediaProvider : IWikiProvider
{
    public const string EnApi = "https://en.wikipedia.org/w/api.php";
    public const string WikidataApi = "https://www.wikidata.org/w/api.php";

    private readonly HttpClient _httpClient;

    public WikipediaProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AukenidAI", "1.0"));
        }

        if (!_httpClient.DefaultRequestHeaders.Contains("Api-User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Api-User-Agent", "AukenidAI/1.0 (https://aukenid.com)");
        }
    }

    public async Task<IReadOnlyList<WikiArticle>> SearchAsync(WikiQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit, 1, 5);
        var (api, hits) = await SearchWikiAsync(EnApi, query.Text, limit, cancellationToken);
        if (hits.Count == 0)
        {
            // No matching article is a normal, non-exceptional search outcome, not a failure -
            // WikiContext.TryTurn already treats an empty list as "nothing to add", no notice shown.
            return [];
        }

        var articles = await LoadExtractsAsync(api, hits, cancellationToken);
        if (articles.Count > 0)
        {
            await EnrichWikidataAsync(articles, cancellationToken);
        }

        return articles;
    }

    public static IReadOnlyList<WikiSearchHit> ParseSearchResponse(string json, int limit)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("query", out var queryEl)
            || !queryEl.TryGetProperty("search", out var search)
            || search.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hits = new List<WikiSearchHit>();
        foreach (var item in search.EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var snippet = item.TryGetProperty("snippet", out var snippetEl) ? StripMarkup(snippetEl.GetString()) : string.Empty;
            hits.Add(new WikiSearchHit(title, snippet));
            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }

    public static IReadOnlyList<WikiArticle> ParseExtractsResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("query", out var queryEl)
            || !queryEl.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var articles = new List<WikiArticle>();
        foreach (var page in pages.EnumerateObject())
        {
            var item = page.Value;
            if (item.TryGetProperty("missing", out _))
            {
                continue;
            }

            var title = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            var url = item.TryGetProperty("fullurl", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var extract = item.TryGetProperty("extract", out var extractEl) ? extractEl.GetString() ?? string.Empty : string.Empty;
            string? qid = null;
            if (item.TryGetProperty("pageprops", out var props) && props.ValueKind == JsonValueKind.Object
                && props.TryGetProperty("wikibase_item", out var qEl))
            {
                qid = qEl.GetString();
            }

            articles.Add(new WikiArticle(title, url, extract, qid, Description: null));
        }

        return articles;
    }

    public static string? ParseWikidataDescription(string json, string entityId)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("entities", out var entities)
            || !entities.TryGetProperty(entityId, out var entity)
            || !entity.TryGetProperty("descriptions", out var descriptions)
            || descriptions.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (descriptions.TryGetProperty("en", out var desc)
            && desc.TryGetProperty("value", out var value))
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    public readonly record struct WikiSearchHit(string Title, string Snippet);

    private async Task<(string Api, IReadOnlyList<WikiSearchHit> Hits)> SearchWikiAsync(
        string api,
        string text,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = api
            + "?action=query&list=search&format=json&utf8=1"
            + "&srlimit=" + limit
            + "&srsearch=" + Uri.EscapeDataString(text);
        var json = await GetJsonAsync(url, cancellationToken);
        return (api, ParseSearchResponse(json, limit));
    }

    private async Task<List<WikiArticle>> LoadExtractsAsync(
        string api,
        IReadOnlyList<WikiSearchHit> hits,
        CancellationToken cancellationToken)
    {
        var titles = string.Join('|', hits.Select(h => h.Title));
        var url = api
            + "?action=query&prop=extracts|info|pageprops&exintro=1&explaintext=1&inprop=url&ppprop=wikibase_item"
            + "&redirects=1&format=json&utf8=1&titles=" + Uri.EscapeDataString(titles);
        var json = await GetJsonAsync(url, cancellationToken);
        var extracts = ParseExtractsResponse(json).ToList();
        if (extracts.Count > 0)
        {
            return extracts;
        }

        return hits.Select(h => new WikiArticle(
            h.Title,
            ArticleUrl(h.Title),
            h.Snippet,
            WikidataId: null,
            Description: null)).ToList();
    }

    private async Task EnrichWikidataAsync(List<WikiArticle> articles, CancellationToken cancellationToken)
    {
        if (articles.Count == 0 || string.IsNullOrWhiteSpace(articles[0].WikidataId))
        {
            return;
        }

        var qid = articles[0].WikidataId!;
        var url = WikidataApi
            + "?action=wbgetentities&format=json&props=descriptions&languages=en&ids="
            + Uri.EscapeDataString(qid);
        try
        {
            var json = await GetJsonAsync(url, cancellationToken);
            var description = ParseWikidataDescription(json, qid);
            if (!string.IsNullOrWhiteSpace(description))
            {
                articles[0] = articles[0] with { Description = description };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Extract-only is enough; Wikidata is optional.
        }
    }

    private async Task<string> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}, {json.Length}B");
        }

        return json;
    }

    private static string ArticleUrl(string title) =>
        "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_'));

    private static string StripMarkup(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = HtmlTag().Replace(html, string.Empty);
        return string.Join(' ', System.Net.WebUtility.HtmlDecode(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    [GeneratedRegex("<.*?>")]
    private static partial Regex HtmlTag();
}
