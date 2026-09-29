namespace Aukenid.Core.Engine;

/// <summary>One Wikipedia article. MediaWiki/Wikidata JSON is not passed through to the UI or the GGUF.</summary>
public sealed record WikiArticle(
    string Title,
    string Url,
    string Extract,
    string? WikidataId,
    string? Description);

public sealed record WikiQuery(string Text, int Limit = 3);

public interface IWikiProvider
{
    Task<IReadOnlyList<WikiArticle>> SearchAsync(WikiQuery query, CancellationToken cancellationToken);
}
