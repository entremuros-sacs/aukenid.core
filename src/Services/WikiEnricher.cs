namespace Aukenid.Core.Services;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Engine;

/// <summary>
/// Runs Wikipedia (+ Wikidata) in the host before the GGUF answers.
/// </summary>
public sealed class WikiEnricher
{
    private readonly IWikiProvider _wiki;

    public WikiEnricher(IWikiProvider wiki)
    {
        _wiki = wiki;
    }

    public async Task<(ChatTurn? Turn, IReadOnlyList<Citation> Citations)> EnrichAsync(
        string userPrompt,
        string? searchText,
        CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(searchText) ? userPrompt : searchText.Trim();
        var articles = await _wiki.SearchAsync(new WikiQuery(query), cancellationToken);
        return (WikiContext.TryTurn(articles), SourceCitations.FromArticles(articles));
    }
}
