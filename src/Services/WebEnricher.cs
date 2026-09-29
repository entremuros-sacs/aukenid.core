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
        IReadOnlyList<WebHit> hits = [];
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            hits = await _search.SearchAsync(new SearchQuery(searchText.Trim()), cancellationToken);
        }
        var fetchUrl = WebContext.FirstPastedHttpUrl(userPrompt)
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
}
