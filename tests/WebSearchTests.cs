using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

public sealed class WebSearchTests
{
    [Fact]
    public void ParseHits_ReadsEmbeddedTitleUrlDescription()
    {
        const string html = """
            <html><body>
            "title":"<b>LimaNews</b> (@lima.news.pe) - Facebook","clickUrl":"https://www.facebook.com/lima.news.pe/","description":"<b>LimaNews</b>. Agenda cultural en Lima."
            "title":"Lima - AP News","clickUrl":"https://apnews.com/hub/lima","description":"Latest news from Lima."
            </body></html>
            """;

        var hits = StartpageSearchProvider.ParseHits(html, limit: 8);

        Assert.Equal(2, hits.Count);
        Assert.Equal("LimaNews (@lima.news.pe) - Facebook", hits[0].Title);
        Assert.Equal("https://www.facebook.com/lima.news.pe/", hits[0].Url);
        Assert.Contains("Agenda cultural", hits[0].Snippet);
        Assert.Equal("Lima - AP News", hits[1].Title);
    }

    [Fact]
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

        Assert.Equal(2, hits.Count);
        Assert.Equal("Gemma (language model) - Wikipedia", hits[0].Title);
        Assert.Equal("https://en.wikipedia.org/wiki/Gemma_(language_model)", hits[0].Url);
        Assert.Equal("Gemma models overview", hits[1].Title);
        Assert.DoesNotContain(hits, h => h.Title.Contains("cuentame", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(hits, h => h.Title.Contains("thash", StringComparison.Ordinal));
    }

    [Fact]
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

        Assert.Equal(2, hits.Count);
        Assert.Equal(["One", "Two"], hits.Select(h => h.Title));
        Assert.Equal("https://one.example/", hits[0].Url);
        Assert.Equal("First snippet", hits[0].Snippet);
    }

    [Fact]
    public void SolveProofOfWork_MatchesSha256LeadingZeros()
    {
        var (nonce, hash) = StartpageSearchProvider.SolveProofOfWork("abc", 2);

        Assert.StartsWith("00", hash);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("abc" + nonce))).ToLowerInvariant();
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void TryTurn_FormatsHitsAndFetchedPage()
    {
        var hits = new[]
        {
            new WebHit("Football", "https://en.wikipedia.org/wiki/Football", "Team sports.", null),
        };

        var turn = WebContext.TryTurn(hits, "https://en.wikipedia.org/wiki/Football", "Football is a family of team sports.");

        Assert.NotNull(turn);
        Assert.Equal("system", turn.Value.Role);
        Assert.Contains("Football", turn.Value.Content);
        Assert.Contains("https://en.wikipedia.org/wiki/Football", turn.Value.Content);
        Assert.Contains("Fetched page", turn.Value.Content);
    }

    [Fact]
    public void TryTurn_ReturnsNullWhenEmpty()
    {
        Assert.Null(WebContext.TryTurn([], null, null));
    }

    [Fact]
    public void TryReadAnubisChallenge_ReadsNestedJson()
    {
        const string html = """
            <script id="anubis_challenge" type="application/json">
            {"rules":{"algorithm":"fast","difficulty":6},"challenge":{"id":"abc-id","method":"fast","randomData":"deadbeef","difficulty":6,"nested":{"x":1}}}
            </script>
            """;

        var challenge = StartpageSearchProvider.TryReadAnubisChallenge(html);
        Assert.NotNull(challenge);
        Assert.Equal("abc-id", challenge!.Id);
        Assert.Equal("deadbeef", challenge.RandomData);
        Assert.Equal(6, challenge.Difficulty);
    }

    [Fact]
    public void Diagnose_SummarizesEmptyPageWithoutDumpingHtml()
    {
        var text = StartpageSearchProvider.Diagnose(HttpStatusCode.OK, "<html></html>");
        Assert.Contains("HTTP 200", text);
        Assert.Contains("empty-page", text);
        Assert.DoesNotContain("<html>", text);
    }

    [Theory]
    [InlineData("see https://example.com/path?q=1 please", "https://example.com/path?q=1")]
    [InlineData("no url here", null)]
    [InlineData("(https://news.example/article).", "https://news.example/article")]
    public void FirstPastedHttpUrl_ExtractsPublicHttpUrl(string prompt, string? expected)
    {
        Assert.Equal(expected, WebContext.FirstPastedHttpUrl(prompt));
    }
}
