using System.Net;
using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class GroundedChatTests
{
    [TestMethod]
    public async Task ReplyAsync_InjectsWebHitsAndAppendsSources()
    {
        var search = new StubSearch(
        [
            new WebHit("Example", "https://example.com/article", "A live snippet.", null),
        ]);
        var fetch = new WebFetchService(new NotFoundHandler(), (_, _) => Task.FromResult(Array.Empty<IPAddress>()));
        var host = new GroundedChat(
            new WebEnricher(search, fetch),
            new WikiEnricher(new StubWiki()),
            new ScholarEnricher(new StubScholar()));

        var engine = new NoChatEngine();
        await engine.LoadModelAsync("model", CancellationToken.None);
        var conversation = new List<ChatTurn>();

        var reply = await host.ReplyAsync(
            engine,
            conversation,
            "Summarize https://example.com/article",
            CancellationToken.None);

        Assert.IsTrue(reply.Plan.Web);
        Assert.IsTrue(reply.Text.Contains("**Sources**", StringComparison.Ordinal));
        Assert.IsTrue(reply.Text.Contains("https://example.com/article", StringComparison.Ordinal));
        Assert.AreEqual(2, conversation.Count);
        Assert.AreEqual("user", conversation[0].Role);
        Assert.AreEqual("assistant", conversation[1].Role);
        Assert.IsFalse(conversation.Any(t => t.Role == "system"));
    }

    private sealed class StubSearch(IReadOnlyList<WebHit> hits) : ISearchProvider
    {
        public Task<IReadOnlyList<WebHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(hits);
    }

    private sealed class StubWiki : IWikiProvider
    {
        public Task<IReadOnlyList<WikiArticle>> SearchAsync(WikiQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WikiArticle>>([]);
    }

    private sealed class StubScholar : IScholarProvider
    {
        public Task<IReadOnlyList<ScholarPaper>> SearchAsync(ScholarQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScholarPaper>>([]);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
