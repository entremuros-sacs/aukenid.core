using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class ScholarTests
{
    [TestMethod]
    public void ParseSearchResponse_ReadsTitleTldrDoiAndPdf()
    {
        const string json = """
            {
              "data": [
                {
                  "title": "Attention Is All You Need",
                  "year": 2017,
                  "authors": [
                    { "name": "Ashish Vaswani" },
                    { "name": "Noam Shazeer" },
                    { "name": "Niki Parmar" },
                    { "name": "Jakob Uszkoreit" }
                  ],
                  "externalIds": { "DOI": "10.5555/3295222.3295349", "ArXiv": "1706.03762" },
                  "url": "https://www.semanticscholar.org/paper/abc",
                  "abstract": "The dominant sequence transduction models are based on complex recurrent or convolutional neural networks.",
                  "tldr": { "text": "We propose the Transformer." },
                  "isOpenAccess": true,
                  "openAccessPdf": { "url": "https://arxiv.org/pdf/1706.03762" },
                  "citationCount": 120000,
                  "venue": "NIPS"
                }
              ]
            }
            """;

        var papers = SemanticScholarProvider.ParseSearchResponse(json, limit: 8);

        var paper = papers.Single();
        Assert.AreEqual("Attention Is All You Need", paper.Title);
        Assert.AreEqual(2017, paper.Year);
        Assert.AreEqual("Ashish Vaswani, Noam Shazeer, Niki Parmar, et al.", paper.Authors);
        Assert.AreEqual("10.5555/3295222.3295349", paper.Doi);
        Assert.AreEqual("We propose the Transformer.", paper.Tldr);
        Assert.IsTrue(paper.IsOpenAccess);
        Assert.AreEqual("https://arxiv.org/pdf/1706.03762", paper.PdfUrl);
        Assert.AreEqual(120000, paper.CitationCount);
    }

    [TestMethod]
    public void ParseOpenAlexPdfUrl_ReadsOaUrl()
    {
        const string json = """
            {
              "open_access": { "is_oa": true, "oa_url": "https://arxiv.org/pdf/1706.03762" },
              "best_oa_location": { "pdf_url": "https://example.com/other.pdf" }
            }
            """;

        Assert.AreEqual("https://arxiv.org/pdf/1706.03762", SemanticScholarProvider.ParseOpenAlexPdfUrl(json));
    }

    [TestMethod]
    public void TryTurn_PrefersTldrAndIncludesDoi()
    {
        var papers = new[]
        {
            new ScholarPaper(
                "Attention Is All You Need",
                2017,
                "Vaswani et al.",
                "10.5555/3295222.3295349",
                "https://www.semanticscholar.org/paper/abc",
                "We propose the Transformer.",
                "Long abstract that should not be preferred.",
                true,
                "https://arxiv.org/pdf/1706.03762",
                "NIPS",
                120000),
        };

        var turn = ScholarContext.TryTurn(papers);

        Assert.IsNotNull(turn);
        Assert.AreEqual("system", turn.Value.Role);
        Assert.IsTrue(turn.Value.Content.Contains("Attention Is All You Need", StringComparison.Ordinal));
        Assert.IsTrue(turn.Value.Content.Contains("We propose the Transformer.", StringComparison.Ordinal));
        Assert.IsFalse(turn.Value.Content.Contains("Long abstract", StringComparison.Ordinal));
        Assert.IsTrue(turn.Value.Content.Contains("10.5555/3295222.3295349", StringComparison.Ordinal));
        Assert.IsTrue(turn.Value.Content.Contains("Open access", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("see 10.1038/nature12373 please", "10.1038/nature12373")]
    [DataRow("doi:10.1145/3290605.3300233", "10.1145/3290605.3300233")]
    [DataRow("no identifier here", null)]
    public void FirstDoi_ExtractsDoi(string text, string? expected)
    {
        Assert.AreEqual(expected, ScholarContext.FirstDoi(text));
    }

    [TestMethod]
    public void TryTurn_ReturnsNullWhenEmpty()
    {
        Assert.IsNull(ScholarContext.TryTurn([]));
    }
}
