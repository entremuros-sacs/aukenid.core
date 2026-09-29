using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

public sealed class ScholarTests
{
    [Fact]
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

        var paper = Assert.Single(papers);
        Assert.Equal("Attention Is All You Need", paper.Title);
        Assert.Equal(2017, paper.Year);
        Assert.Equal("Ashish Vaswani, Noam Shazeer, Niki Parmar, et al.", paper.Authors);
        Assert.Equal("10.5555/3295222.3295349", paper.Doi);
        Assert.Equal("We propose the Transformer.", paper.Tldr);
        Assert.True(paper.IsOpenAccess);
        Assert.Equal("https://arxiv.org/pdf/1706.03762", paper.PdfUrl);
        Assert.Equal(120000, paper.CitationCount);
    }

    [Fact]
    public void ParseOpenAlexPdfUrl_ReadsOaUrl()
    {
        const string json = """
            {
              "open_access": { "is_oa": true, "oa_url": "https://arxiv.org/pdf/1706.03762" },
              "best_oa_location": { "pdf_url": "https://example.com/other.pdf" }
            }
            """;

        Assert.Equal("https://arxiv.org/pdf/1706.03762", SemanticScholarProvider.ParseOpenAlexPdfUrl(json));
    }

    [Fact]
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

        Assert.NotNull(turn);
        Assert.Equal("system", turn.Value.Role);
        Assert.Contains("Attention Is All You Need", turn.Value.Content);
        Assert.Contains("We propose the Transformer.", turn.Value.Content);
        Assert.DoesNotContain("Long abstract", turn.Value.Content);
        Assert.Contains("10.5555/3295222.3295349", turn.Value.Content);
        Assert.Contains("Open access", turn.Value.Content);
    }

    [Theory]
    [InlineData("see 10.1038/nature12373 please", "10.1038/nature12373")]
    [InlineData("doi:10.1145/3290605.3300233", "10.1145/3290605.3300233")]
    [InlineData("no identifier here", null)]
    public void FirstDoi_ExtractsDoi(string text, string? expected)
    {
        Assert.Equal(expected, ScholarContext.FirstDoi(text));
    }

    [Fact]
    public void TryTurn_ReturnsNullWhenEmpty()
    {
        Assert.Null(ScholarContext.TryTurn([]));
    }
}
