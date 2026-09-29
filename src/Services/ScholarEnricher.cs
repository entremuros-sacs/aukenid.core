namespace Aukenid.Core.Services;

using Aukenid.Core.Engine;

/// <summary>
/// Runs Semantic Scholar (+ OpenAlex OA) in the host before the GGUF answers.
/// </summary>
public sealed class ScholarEnricher
{
    private readonly IScholarProvider _scholar;

    public ScholarEnricher(IScholarProvider scholar)
    {
        _scholar = scholar;
    }

    public async Task<(ChatTurn? Turn, IReadOnlyList<Citation> Citations)> EnrichAsync(
        string userPrompt,
        string? searchText,
        CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(searchText) ? userPrompt : searchText.Trim();
        var papers = await _scholar.SearchAsync(new ScholarQuery(query), cancellationToken);
        return (ScholarContext.TryTurn(papers), SourceCitations.FromPapers(papers));
    }
}
