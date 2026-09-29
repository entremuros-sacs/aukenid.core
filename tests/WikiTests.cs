using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

public sealed class WikiTests
{
    [Fact]
    public void ParseSearchResponse_ReadsTitleAndStripsSnippetHtml()
    {
        const string json = """
            {
              "query": {
                "search": [
                  { "title": "Lima", "snippet": "Lima is the <span class=\"searchmatch\">capital</span> of Peru." },
                  { "title": "Lima (disambiguation)", "snippet": "Other uses." }
                ]
              }
            }
            """;

        var hits = WikipediaProvider.ParseSearchResponse(json, limit: 1);

        var hit = Assert.Single(hits);
        Assert.Equal("Lima", hit.Title);
        Assert.Equal("Lima is the capital of Peru.", hit.Snippet);
    }

    [Fact]
    public void ParseExtractsResponse_ReadsExtractUrlAndWikidataId()
    {
        const string json = """
            {
              "query": {
                "pages": {
                  "123": {
                    "title": "Lima",
                    "extract": "Lima is the capital and largest city of Peru.",
                    "fullurl": "https://en.wikipedia.org/wiki/Lima",
                    "pageprops": { "wikibase_item": "Q2868" }
                  }
                }
              }
            }
            """;

        var article = Assert.Single(WikipediaProvider.ParseExtractsResponse(json));
        Assert.Equal("Lima", article.Title);
        Assert.Equal("https://en.wikipedia.org/wiki/Lima", article.Url);
        Assert.Contains("capital", article.Extract);
        Assert.Equal("Q2868", article.WikidataId);
    }

    [Fact]
    public void ParseWikidataDescription_ReadsEnglish()
    {
        const string json = """
            {
              "entities": {
                "Q2868": {
                  "descriptions": {
                    "en": { "value": "capital of Peru" }
                  }
                }
              }
            }
            """;

        Assert.Equal("capital of Peru", WikipediaProvider.ParseWikidataDescription(json, "Q2868"));
    }

    [Fact]
    public void TryTurn_IncludesTitleUrlAndExtract()
    {
        var articles = new[]
        {
            new WikiArticle("Lima", "https://en.wikipedia.org/wiki/Lima", "Lima is the capital of Peru.", "Q2868", "capital of Peru"),
        };

        var turn = WikiContext.TryTurn(articles);

        Assert.NotNull(turn);
        Assert.Equal("system", turn.Value.Role);
        Assert.Contains("Lima", turn.Value.Content);
        Assert.Contains("https://en.wikipedia.org/wiki/Lima", turn.Value.Content);
        Assert.Contains("capital of Peru", turn.Value.Content);
        Assert.Contains("Q2868", turn.Value.Content);
    }

    [Fact]
    public void TryTurn_ReturnsNullWhenEmpty()
    {
        Assert.Null(WikiContext.TryTurn([]));
    }
}
