using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class WebSearchTests
{
    [TestMethod]
    public void ParseHits_ReadsEmbeddedTitleUrlDescription()
    {
        const string html = """
            <html><body>
            "title":"<b>LimaNews</b> (@lima.news.pe) - Facebook","clickUrl":"https://www.facebook.com/lima.news.pe/","description":"<b>LimaNews</b>. Agenda cultural en Lima."
            "title":"Lima - AP News","clickUrl":"https://apnews.com/hub/lima","description":"Latest news from Lima."
            </body></html>
            """;

        var hits = StartpageSearchProvider.ParseHits(html, limit: 8);

        Assert.AreEqual(2, hits.Count);
        Assert.AreEqual("LimaNews (@lima.news.pe) - Facebook", hits[0].Title);
        Assert.AreEqual("https://www.facebook.com/lima.news.pe/", hits[0].Url);
        Assert.IsTrue(hits[0].Snippet.Contains("Agenda cultural", StringComparison.Ordinal));
        Assert.AreEqual("Lima - AP News", hits[1].Title);
    }

    [TestMethod]
    public void ParseHits_IgnoresQueryAndAdJsonBlobs()
    {
        const string html = """
            {"title":"cuentame acerca del modelo de ia gemma","query":"cuentame acerca del modelo de ia gemma","sourceIndex":0,"thash":"VqDOQf1IzBvjSE-o5CgfGc7HJZWxLqyw","impressionToken":"abc","clickUrl":"https://www.startpage.com/sp/search"}
            {"display_type":"ads-google-top","results":[{"title":"Ad","clickUrl":"https://googleads.g.doubleclick.net/x"}]}
            {"display_type":"web-google","results":[
              {"title":"Gemma (language model) - Wikipedia","clickUrl":"https://en.wikipedia.org/wiki/Gemma_(language_model)","description":"Gemma is a family of open models."},
              {"title":"Gemma models overview","clickUrl":"https://ai.google.dev/gemma/docs","description":"Many Gemma variants."}
            ]}
            """;

        var hits = StartpageSearchProvider.ParseHits(html, limit: 8);

        Assert.AreEqual(2, hits.Count);
        Assert.AreEqual("Gemma (language model) - Wikipedia", hits[0].Title);
        Assert.AreEqual("https://en.wikipedia.org/wiki/Gemma_(language_model)", hits[0].Url);
        Assert.AreEqual("Gemma models overview", hits[1].Title);
        Assert.IsFalse(hits.Any(h => h.Title.Contains("cuentame", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(hits.Any(h => h.Title.Contains("thash", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ParseHits_FallsBackToMarkupAndHonorsLimit()
    {
        const string html = """
            <a class="result-title result-link css-x" href="https://one.example/">
              <h2 class="wgl-title">One</h2>
            </a>
            <p class="description css-y">First snippet</p>
            <a class="result-title result-link css-x" href="https://two.example/">
              <h2 class="wgl-title">Two</h2>
            </a>
            <p class="description css-y">Second snippet</p>
            <a class="result-title result-link css-x" href="https://three.example/">
              <h2 class="wgl-title">Three</h2>
            </a>
            <p class="description css-y">Third snippet</p>
            """;

        var hits = StartpageSearchProvider.ParseHits(html, limit: 2);

        Assert.AreEqual(2, hits.Count);
        CollectionAssert.AreEqual(new[] { "One", "Two" }, hits.Select(h => h.Title).ToArray());
        Assert.AreEqual("https://one.example/", hits[0].Url);
        Assert.AreEqual("First snippet", hits[0].Snippet);
    }

    [TestMethod]
    public void SolveProofOfWork_MatchesSha256LeadingZeros()
    {
        var (nonce, hash) = StartpageSearchProvider.SolveProofOfWork("abc", 2);

        Assert.IsTrue(hash.StartsWith("00", StringComparison.Ordinal));
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("abc" + nonce))).ToLowerInvariant();
        Assert.AreEqual(expected, hash);
    }

    [TestMethod]
    public void TryTurn_FormatsHitsAndFetchedPage()
    {
        var hits = new[]
        {
            new WebHit("Football", "https://en.wikipedia.org/wiki/Football", "Team sports.", null),
        };

        var turn = WebContext.TryTurn(hits, "https://en.wikipedia.org/wiki/Football", "Football is a family of team sports.");

        Assert.IsNotNull(turn);
        Assert.AreEqual("system", turn.Value.Role);
        Assert.IsTrue(turn.Value.Content.Contains("Football", StringComparison.Ordinal));
        Assert.IsTrue(turn.Value.Content.Contains("https://en.wikipedia.org/wiki/Football", StringComparison.Ordinal));
        Assert.IsTrue(turn.Value.Content.Contains("Fetched page", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryTurn_ReturnsNullWhenEmpty()
    {
        Assert.IsNull(WebContext.TryTurn([], null, null));
    }

    [TestMethod]
    public void TryReadAnubisChallenge_ReadsNestedJson()
    {
        const string html = """
            <script id="anubis_challenge" type="application/json">
            {"rules":{"algorithm":"fast","difficulty":6},"challenge":{"id":"abc-id","method":"fast","randomData":"deadbeef","difficulty":6,"nested":{"x":1}}}
            </script>
            """;

        var challenge = StartpageSearchProvider.TryReadAnubisChallenge(html);
        Assert.IsNotNull(challenge);
        Assert.AreEqual("abc-id", challenge!.Id);
        Assert.AreEqual("deadbeef", challenge.RandomData);
        Assert.AreEqual(6, challenge.Difficulty);
    }

    [TestMethod]
    public void Diagnose_SummarizesEmptyPageWithoutDumpingHtml()
    {
        var text = StartpageSearchProvider.Diagnose(HttpStatusCode.OK, "<html></html>");
        Assert.IsTrue(text.Contains("HTTP 200", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("empty-page", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("<html>", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("see https://example.com/path?q=1 please", "https://example.com/path?q=1")]
    [DataRow("no url here", null)]
    [DataRow("(https://news.example/article).", "https://news.example/article")]
    public void FirstPastedHttpUrl_ExtractsPublicHttpUrl(string prompt, string? expected)
    {
        Assert.AreEqual(expected, WebContext.FirstPastedHttpUrl(prompt));
    }

    [TestMethod]
    [DataRow("look at aukenid.com", "https://aukenid.com")]
    [DataRow("aukenid.com/index.html", "https://aukenid.com/index.html")]
    [DataRow("(aukenid.com).", "https://aukenid.com")]
    [DataRow("already https://aukenid.com", null)]
    [DataRow("email me at test@aukenid.com", null)]
    public void FirstPastedHttpsUrlFromBareDomain_ExtractsBareDomains(string prompt, string? expected)
    {
        Assert.AreEqual(expected, WebContext.FirstPastedHttpsUrlFromBareDomain(prompt));
    }
}
