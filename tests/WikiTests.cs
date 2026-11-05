using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class WikiTests
{
    [TestMethod]
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

        var hit = hits.Single();
        Assert.AreEqual("Lima", hit.Title);
        Assert.AreEqual("Lima is the capital of Peru.", hit.Snippet);
    }

    [TestMethod]
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

        var article = WikipediaProvider.ParseExtractsResponse(json).Single();
        Assert.AreEqual("Lima", article.Title);
        Assert.AreEqual("https://en.wikipedia.org/wiki/Lima", article.Url);
        Assert.IsTrue(article.Extract.Contains("capital", StringComparison.Ordinal));
        Assert.AreEqual("Q2868", article.WikidataId);
    }

    [TestMethod]
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

        Assert.AreEqual("capital of Peru", WikipediaProvider.ParseWikidataDescription(json, "Q2868"));
    }

    [TestMethod]
    public void TryTurn_IncludesTitleUrlAndExtract()
    {
        var articles = new[]
        {
            new WikiArticle("Lima", "https://en.wikipedia.org/wiki/Lima", "Lima is the capital of Peru.", "Q2868", "capital of Peru"),
        };

        var turn = WikiContext.TryTurn(articles);

        Assert.IsNotNull(turn);
        Assert.AreEqual("system", turn.Value.Role);
        Assert.IsTrue(turn.Value.Content.Contains("Lima", StringComparison.Ordinal));
        Assert.IsTrue(turn.Value.Content.Contains("https://en.wikipedia.org/wiki/Lima", StringComparison.Ordinal));
        Assert.IsTrue(turn.Value.Content.Contains("capital of Peru", StringComparison.Ordinal));
        Assert.IsTrue(turn.Value.Content.Contains("Q2868", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryTurn_ReturnsNullWhenEmpty()
    {
        Assert.IsNull(WikiContext.TryTurn([]));
    }
}
