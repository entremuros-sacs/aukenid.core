using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class SourceCitationsTests
{
    [TestMethod]
    public void FromPapers_PrefersOpenAccessPdfThenDoi()
    {
        var papers = new[]
        {
            new ScholarPaper("Attention Is All You Need", 2017, "Vaswani et al.", "10.5555/3295222.3295349",
                "https://www.semanticscholar.org/paper/abc", null, null, true, "https://arxiv.org/pdf/1706.03762", "NIPS", 1),
            new ScholarPaper("No links", 2020, "Anon", null, null, null, null, false, null, null, null),
        };

        var citations = SourceCitations.FromPapers(papers);
        var citation = citations.Single();
        Assert.AreEqual("Attention Is All You Need", citation.Title);
        Assert.AreEqual("https://arxiv.org/pdf/1706.03762", citation.Url);
    }

    [TestMethod]
    public void Append_AddsMarkdownLinksAndDedupesUrls()
    {
        var text = SourceCitations.Append(
            "Here is a summary.",
            [
                new Citation("Lima", "https://en.wikipedia.org/wiki/Lima"),
                new Citation("Lima again", "https://en.wikipedia.org/wiki/Lima"),
            ]);

        Assert.IsTrue(text.Contains("Here is a summary.", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("**Sources**", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("- [Lima](https://en.wikipedia.org/wiki/Lima)", StringComparison.Ordinal));
        Assert.AreEqual(1, text.Split("en.wikipedia.org/wiki/Lima", StringSplitOptions.None).Length - 1);
    }
}
