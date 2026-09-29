namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Engine;

/// <summary>
/// Desktop scholar adapter: Semantic Scholar for search + TL;DR, OpenAlex for DOI / OA PDF.
/// No vendor NuGet. arXiv and PubMed ids come through S2 <c>externalIds</c>.
/// </summary>
public sealed class SemanticScholarProvider : IScholarProvider
{
    public const string SearchUrl = "https://api.semanticscholar.org/graph/v1/paper/search";
    public const string PaperUrl = "https://api.semanticscholar.org/graph/v1/paper/";
    public const string OpenAlexUrl = "https://api.openalex.org/works/";
    private const string Fields = "title,year,authors,externalIds,url,abstract,tldr,isOpenAccess,openAccessPdf,citationCount,venue,publicationDate";

    private readonly HttpClient _httpClient;

    public SemanticScholarProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AukenidAI", "1.0"));
        }
    }

    public async Task<IReadOnlyList<ScholarPaper>> SearchAsync(ScholarQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit, 1, 10);
        var papers = new List<ScholarPaper>();
        var doi = ScholarContext.FirstDoi(query.Text);

        if (doi is not null)
        {
            var byDoi = await GetByDoiAsync(doi, cancellationToken);
            if (byDoi is not null)
            {
                papers.Add(byDoi);
            }
        }

        if (doi is null || papers.Count == 0 || !LooksLikeBareDoi(query.Text, doi))
        {
            var searched = await SearchPapersAsync(query.Text, limit, cancellationToken);
            foreach (var paper in searched)
            {
                if (papers.Any(p => SamePaper(p, paper)))
                {
                    continue;
                }

                papers.Add(paper);
                if (papers.Count >= limit)
                {
                    break;
                }
            }
        }

        await EnrichOpenAccessAsync(papers, cancellationToken);

        if (papers.Count == 0)
        {
            throw new InvalidOperationException("Scholar search returned no papers.");
        }

        return papers;
    }

    public static IReadOnlyList<ScholarPaper> ParseSearchResponse(string json, int limit)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            var single = ParsePaper(document.RootElement);
            return single is null ? [] : [single];
        }

        var papers = new List<ScholarPaper>();
        foreach (var item in data.EnumerateArray())
        {
            var paper = ParsePaper(item);
            if (paper is null)
            {
                continue;
            }

            papers.Add(paper);
            if (papers.Count >= limit)
            {
                break;
            }
        }

        return papers;
    }

    public static ScholarPaper? ParsePaper(JsonElement item)
    {
        var title = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        int? year = item.TryGetProperty("year", out var yearEl) && yearEl.TryGetInt32(out var y) ? y : null;
        var authors = FormatAuthors(item);
        var doi = ReadExternalId(item, "DOI");
        var url = item.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
        var tldr = item.TryGetProperty("tldr", out var tldrEl) && tldrEl.ValueKind == JsonValueKind.Object && tldrEl.TryGetProperty("text", out var tldrText)
            ? tldrText.GetString()
            : null;
        var abs = item.TryGetProperty("abstract", out var absEl) ? absEl.GetString() : null;
        var isOa = item.TryGetProperty("isOpenAccess", out var oaEl) && oaEl.ValueKind == JsonValueKind.True;
        string? pdf = null;
        if (item.TryGetProperty("openAccessPdf", out var pdfEl) && pdfEl.ValueKind == JsonValueKind.Object
            && pdfEl.TryGetProperty("url", out var pdfUrl))
        {
            pdf = pdfUrl.GetString();
        }

        var venue = item.TryGetProperty("venue", out var venueEl) ? venueEl.GetString() : null;
        int? cites = item.TryGetProperty("citationCount", out var citesEl) && citesEl.TryGetInt32(out var c) ? c : null;

        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(doi))
        {
            url = "https://doi.org/" + doi;
        }

        return new ScholarPaper(title, year, authors, doi, url, tldr, abs, isOa || !string.IsNullOrWhiteSpace(pdf), pdf, venue, cites);
    }

    public static string? ParseOpenAlexPdfUrl(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("open_access", out var oa) && oa.ValueKind == JsonValueKind.Object)
        {
            if (oa.TryGetProperty("oa_url", out var urlEl))
            {
                var url = urlEl.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
        }

        if (root.TryGetProperty("best_oa_location", out var loc) && loc.ValueKind == JsonValueKind.Object
            && loc.TryGetProperty("pdf_url", out var pdfEl))
        {
            return pdfEl.GetString();
        }

        return null;
    }

    private async Task<IReadOnlyList<ScholarPaper>> SearchPapersAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var url = SearchUrl
            + "?query=" + Uri.EscapeDataString(text)
            + "&limit=" + limit
            + "&fields=" + Uri.EscapeDataString(Fields);
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}, {json.Length}B");
        }

        return ParseSearchResponse(json, limit);
    }

    private async Task<ScholarPaper?> GetByDoiAsync(string doi, CancellationToken cancellationToken)
    {
        var url = PaperUrl + "DOI:" + Uri.EscapeDataString(doi) + "?fields=" + Uri.EscapeDataString(Fields);
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        return ParsePaper(document.RootElement);
    }

    private async Task EnrichOpenAccessAsync(List<ScholarPaper> papers, CancellationToken cancellationToken)
    {
        var missing = papers
            .Select((paper, index) => (paper, index))
            .Where(p => string.IsNullOrWhiteSpace(p.paper.PdfUrl) && !string.IsNullOrWhiteSpace(p.paper.Doi))
            .Take(3)
            .ToList();

        foreach (var (paper, index) in missing)
        {
            var pdf = await LookupOpenAlexPdfAsync(paper.Doi!, cancellationToken);
            if (string.IsNullOrWhiteSpace(pdf))
            {
                continue;
            }

            papers[index] = paper with { IsOpenAccess = true, PdfUrl = pdf };
        }
    }

    private async Task<string?> LookupOpenAlexPdfAsync(string doi, CancellationToken cancellationToken)
    {
        var url = OpenAlexUrl + "https://doi.org/" + Uri.EscapeDataString(doi) + "?mailto=" + Uri.EscapeDataString("contact@aukenid.com");
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseOpenAlexPdfUrl(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatAuthors(JsonElement item)
    {
        if (!item.TryGetProperty("authors", out var authors) || authors.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var names = new List<string>();
        foreach (var author in authors.EnumerateArray())
        {
            var name = author.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            names.Add(name);
            if (names.Count == 3)
            {
                break;
            }
        }

        if (authors.GetArrayLength() > 3)
        {
            names.Add("et al.");
        }

        return string.Join(", ", names);
    }

    private static string? ReadExternalId(JsonElement item, string key)
    {
        if (!item.TryGetProperty("externalIds", out var ids) || ids.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ids.TryGetProperty(key, out var value) ? value.GetString() : null;
    }

    private static bool SamePaper(ScholarPaper a, ScholarPaper b) =>
        (!string.IsNullOrWhiteSpace(a.Doi) && string.Equals(a.Doi, b.Doi, StringComparison.OrdinalIgnoreCase))
        || string.Equals(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeBareDoi(string text, string doi)
    {
        var trimmed = text.Trim();
        return trimmed.Equals(doi, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("https://doi.org/" + doi, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("doi:" + doi, StringComparison.OrdinalIgnoreCase);
    }
}
