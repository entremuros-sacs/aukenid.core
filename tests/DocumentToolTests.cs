using Aukenid.Core.Attachments;
using Aukenid.Core.Contracts;
using Aukenid.Core.Data;
using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

public sealed class DocumentToolTests
{
    [Fact]
    public void GenerationTurns_InsertsContextBeforeLastUserTurn()
    {
        var recentTurns = new[]
        {
            new ChatTurn("user", "Escribe una receta"),
            new ChatTurn("assistant", "Aquí está"),
            new ChatTurn("user", "Tradúcela al francés"),
        };

        var turns = DocumentTool.GenerationTurns(recentTurns, "# Recipe\nIngredients...", []);

        Assert.Equal(5, turns.Count);
        Assert.Equal("system", turns[0].Role);
        Assert.Equal("user", turns[1].Role);
        Assert.Equal("assistant", turns[2].Role);
        Assert.Equal("system", turns[3].Role);
        Assert.Contains("Current document:", turns[3].Content);
        Assert.Equal("user", turns[4].Role);
        Assert.Equal("Tradúcela al francés", turns[4].Content);
    }

    [Fact]
    public void GenerationTurns_IncludesAttachmentText()
    {
        var recentTurns = new[] { new ChatTurn("user", "Traduce el archivo adjunto") };
        var excerpts = new[] { new AttachmentExcerpt("overview.md", "Hello world", null) };

        var turns = DocumentTool.GenerationTurns(recentTurns, null, excerpts);

        var contextTurn = Assert.Single(turns, t => t.Content.Contains("overview.md"));
        Assert.Contains("Hello world", contextTurn.Content);
    }

    [Fact]
    public void GenerationTurns_WithNoDocumentOrAttachments_AddsNoExtraSystemTurn()
    {
        var recentTurns = new[] { new ChatTurn("user", "Escribe algo") };

        var turns = DocumentTool.GenerationTurns(recentTurns, null, []);

        Assert.Equal(2, turns.Count);
    }

    [Fact]
    public void GenerationTurns_TruncatesVeryLongContext()
    {
        var recentTurns = new[] { new ChatTurn("user", "resume esto") };
        var longDoc = new string('a', 20000);

        var turns = DocumentTool.GenerationTurns(recentTurns, longDoc, []);

        var contextTurn = Assert.Single(turns, t => t.Content.StartsWith("Current document:"));
        Assert.True(contextTurn.Content.Length < 20000);
        Assert.EndsWith("\u2026", contextTurn.Content);
    }

    [Fact]
    public void ResolveContent_PrefersCurrentReplyWhenSubstantial()
    {
        var longReply = new string('a', 200);

        var resolved = DocumentTool.ResolveContent(longReply, []);

        Assert.Equal(longReply, resolved);
    }

    [Fact]
    public void ResolveContent_WithAttachment_PrefersSubstantialReplyOverRawAttachment()
    {
        // The reply is what synthesizes/organizes the attachment's data (e.g. a template filled in
        // from it); the raw file must not override that synthesized result.
        var chatReply = new string('a', 200);
        var excerpts = new[] { new AttachmentExcerpt("notes.txt", "Attached file content.", null) };

        var resolved = DocumentTool.ResolveContent(chatReply, [], excerpts);

        Assert.Equal(chatReply, resolved);
    }

    [Fact]
    public void ResolveContent_WithAttachment_FallsBackToAttachmentWhenReplyIsJustAConfirmation()
    {
        var shortReply = "I've added it to the document panel.";
        var excerpts = new[] { new AttachmentExcerpt("notes.txt", "Attached file content.", null) };

        var resolved = DocumentTool.ResolveContent(shortReply, [], excerpts);

        Assert.Equal("Attached file content.", resolved);
    }

    [Fact]
    public void ResolveContent_WithMultipleAttachments_CombinesWithFileHeadingsWhenReplyIsNotSubstantial()
    {
        var excerpts = new[]
        {
            new AttachmentExcerpt("a.txt", "First file.", null),
            new AttachmentExcerpt("b.txt", "Second file.", null),
        };

        var resolved = DocumentTool.ResolveContent("short reply", [], excerpts);

        Assert.Equal("## a.txt\nFirst file.\n\n## b.txt\nSecond file.", resolved);
    }

    [Fact]
    public void ResolveContent_WithUnreadableAttachmentAndNoSubstantialReply_ReturnsNull()
    {
        var excerpts = new[] { new AttachmentExcerpt("scan.png", null, "attach.unreadable") };

        var resolved = DocumentTool.ResolveContent("short reply", [], excerpts);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveContent_FallsBackToMostRecentSubstantialAssistantMessage()
    {
        var shortReply = "Please check the panel now.";
        var priorPath = new[]
        {
            Message("user", "the specific content is the template you already suggested"),
            Message("Aukenid", new string('b', 300)),
        };

        var resolved = DocumentTool.ResolveContent(shortReply, priorPath);

        Assert.Equal(new string('b', 300), resolved);
    }

    [Fact]
    public void ResolveContent_ReturnsNullWhenNothingSubstantialExists()
    {
        var shortReply = "Done.";
        var priorPath = new[]
        {
            Message("user", "short question"),
            Message("Aukenid", "short reply"),
        };

        Assert.Null(DocumentTool.ResolveContent(shortReply, priorPath));
    }

    [Fact]
    public void ResolveContent_IgnoresUserMessagesWhenSearchingBackward()
    {
        var shortReply = "Done.";
        var priorPath = new[]
        {
            Message("Aukenid", new string('c', 150)),
            Message("user", new string('d', 150)),
        };

        var resolved = DocumentTool.ResolveContent(shortReply, priorPath);

        Assert.Equal(new string('c', 150), resolved);
    }

    [Fact]
    public void Apply_WritesDocumentAndSnapshotsBeforeAndAfter()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        workspace.Store.SaveDocument(thread.Id, "old content");
        var tool = new DocumentTool(workspace.Store);

        var result = tool.Apply(thread.Id, new string('x', 150), [], []);

        Assert.True(result.Applied);
        Assert.Equal(new string('x', 150), workspace.Store.ReadDocument(thread.Id));
        var versions = workspace.Store.ListDocumentVersions(thread.Id);
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.Label == "before-edit");
        Assert.Contains(versions, v => v.Label == "after-edit");
    }

    [Fact]
    public void Apply_ReturnsNotAppliedWhenNothingSubstantialToWrite()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        var tool = new DocumentTool(workspace.Store);

        var result = tool.Apply(thread.Id, "Done.", [], []);

        Assert.False(result.Applied);
        Assert.Null(workspace.Store.ReadDocument(thread.Id));
    }

    private static MessageDto Message(string role, string content) =>
        new(Guid.NewGuid().ToString(), "thread-1", null, role, content, DateTimeOffset.UtcNow);

    private sealed class Workspace : IDisposable
    {
        private readonly string _root;

        public Workspace()
        {
            _root = Path.Combine(Path.GetTempPath(), "aukenid-document-tool-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Store = new ConversationStore(Path.Combine(_root, "conversations"));
        }

        public ConversationStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
