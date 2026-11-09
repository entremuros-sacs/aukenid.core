namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Engine;

/// <summary>
/// Runs Startpage search and one local fetch in the host before the GGUF answers.
/// The UI only sees the finished reply (or the search-error line).
/// </summary>
public sealed class WebEnricher
{
    private readonly ISearchProvider _search;
    private readonly WebFetchService _fetch;

    public WebEnricher(ISearchProvider search, WebFetchService fetch)
    {
        _search = search;
        _fetch = fetch;
    }

    public async Task<(ChatTurn? Turn, IReadOnlyList<Citation> Citations)> EnrichAsync(
        string userPrompt,
        string? searchText,
        CancellationToken cancellationToken)
    {
        var pastedUrl = WebContext.FirstPastedHttpUrl(userPrompt)
            ?? WebContext.FirstPastedHttpsUrlFromBareDomain(userPrompt);

        var queryUrl = NormalizeQueryToUrl(searchText);

        IReadOnlyList<WebHit> hits = [];
        var directUrl = pastedUrl ?? queryUrl;
        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            // Even when fetch fails, keep a stable source entry so the assistant can cite it.
            hits =
            [
                new WebHit(TitleFromUrl(directUrl), directUrl, string.Empty, null),
            ];
        }

        if (!string.IsNullOrWhiteSpace(searchText) && pastedUrl is null && queryUrl is null)
        {
            hits = await _search.SearchAsync(new SearchQuery(searchText.Trim()), cancellationToken);
        }

        var fetchUrl = pastedUrl
            ?? queryUrl
            ?? hits.Select(h => h.Url).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

        string? fetchedText = null;
        if (!string.IsNullOrWhiteSpace(fetchUrl))
        {
            try
            {
                fetchedText = await _fetch.FetchAsync(fetchUrl, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Spec: failed fetch keeps the SERP snippet; no retry, no cloud fallback.
            }
        }

        return (WebContext.TryTurn(hits, fetchUrl, fetchedText), SourceCitations.FromHits(hits));
    }

    private static string TitleFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrWhiteSpace(uri.Host) ? url : uri.Host;
        }

        return url;
    }

    private static string? NormalizeQueryToUrl(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmed = query.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && (absolute.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || absolute.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            return trimmed;
        }

        // Treat a bare domain used as q as a fetch target.
        return WebContext.FirstPastedHttpsUrlFromBareDomain(trimmed);
    }
}
