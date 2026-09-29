using System.Net;
using System.Net.Sockets;
using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

public sealed class GroundedChatTests
{
    [Fact]
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

        Assert.True(reply.Plan.Web);
        Assert.Contains("**Sources**", reply.Text, StringComparison.Ordinal);
        Assert.Contains("https://example.com/article", reply.Text, StringComparison.Ordinal);
        Assert.Equal(2, conversation.Count);
        Assert.Equal("user", conversation[0].Role);
        Assert.Equal("assistant", conversation[1].Role);
        Assert.DoesNotContain(conversation, t => t.Role == "system");
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
