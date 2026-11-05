using Aukenid.Core.Attachments;
using Aukenid.Core.Contracts;
using Aukenid.Core.Data;
using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class DocumentToolTests
{
    [TestMethod]
    public void GenerationTurns_InsertsContextBeforeLastUserTurn()
    {
        var recentTurns = new[]
        {
            new ChatTurn("user", "Escribe una receta"),
            new ChatTurn("assistant", "Aquí está"),
            new ChatTurn("user", "Tradúcela al francés"),
        };

        var turns = DocumentTool.GenerationTurns(recentTurns, "# Recipe\nIngredients...", []);

        Assert.AreEqual(5, turns.Count);
        Assert.AreEqual("system", turns[0].Role);
        Assert.AreEqual("user", turns[1].Role);
        Assert.AreEqual("assistant", turns[2].Role);
        Assert.AreEqual("system", turns[3].Role);
        Assert.IsTrue(turns[3].Content.Contains("Current document:", StringComparison.Ordinal));
        Assert.AreEqual("user", turns[4].Role);
        Assert.AreEqual("Tradúcela al francés", turns[4].Content);
    }

    [TestMethod]
    public void GenerationTurns_IncludesAttachmentText()
    {
        var recentTurns = new[] { new ChatTurn("user", "Traduce el archivo adjunto") };
        var excerpts = new[] { new AttachmentExcerpt("overview.md", "Hello world", null) };

        var turns = DocumentTool.GenerationTurns(recentTurns, null, excerpts);

        var contextTurn = turns.Single(t => t.Content.Contains("overview.md", StringComparison.Ordinal));
        Assert.IsTrue(contextTurn.Content.Contains("Hello world", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GenerationTurns_WithNoDocumentOrAttachments_AddsNoExtraSystemTurn()
    {
        var recentTurns = new[] { new ChatTurn("user", "Escribe algo") };

        var turns = DocumentTool.GenerationTurns(recentTurns, null, []);

        Assert.AreEqual(2, turns.Count);
    }

    [TestMethod]
    public void GenerationTurns_TruncatesVeryLongContext()
    {
        var recentTurns = new[] { new ChatTurn("user", "resume esto") };
        var longDoc = new string('a', 20000);

        var turns = DocumentTool.GenerationTurns(recentTurns, longDoc, []);

        var contextTurn = turns.Single(t => t.Content.StartsWith("Current document:", StringComparison.Ordinal));
        Assert.IsTrue(contextTurn.Content.Length < 20000);
        Assert.IsTrue(contextTurn.Content.EndsWith("\u2026", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResolveContent_PrefersCurrentReplyWhenSubstantial()
    {
        var longReply = new string('a', 200);

        var resolved = DocumentTool.ResolveContent(longReply, []);

        Assert.AreEqual(longReply, resolved);
    }

    [TestMethod]
    public void StripWholeDocumentCodeFence_UnwrapsEntireContentFence()
    {
        var wrapped = "```markdown\n# Title\n\nSome text.\n```";

        var stripped = DocumentTool.StripWholeDocumentCodeFence(wrapped);

        Assert.AreEqual("# Title\n\nSome text.", stripped);
    }

    [TestMethod]
    public void StripWholeDocumentCodeFence_UnwrapsWithoutLanguageTag()
    {
        var wrapped = "```\n# Title\n\nSome text.\n```";

        var stripped = DocumentTool.StripWholeDocumentCodeFence(wrapped);

        Assert.AreEqual("# Title\n\nSome text.", stripped);
    }

    [TestMethod]
    public void TryVerbatimMarkdownContent_SingleMarkdownAttachment_ReturnsRawTextUnchanged()
    {
        var excerpts = new[] { new AttachmentExcerpt("notes.md", "# Title\n\nSome text.", null) };

        var content = DocumentTool.TryVerbatimMarkdownContent(excerpts);

        Assert.AreEqual("# Title\n\nSome text.", content);
    }

    [TestMethod]
    public void TryVerbatimMarkdownContent_NoAttachments_ReturnsNull()
    {
        Assert.IsNull(DocumentTool.TryVerbatimMarkdownContent([]));
    }

    [TestMethod]
    public void TryVerbatimMarkdownContent_NonMarkdownAttachment_ReturnsNull()
    {
        var excerpts = new[] { new AttachmentExcerpt("notes.txt", "Plain text.", null) };

        Assert.IsNull(DocumentTool.TryVerbatimMarkdownContent(excerpts));
    }

    [TestMethod]
    public void TryVerbatimMarkdownContent_MixedMarkdownAndOtherAttachment_ReturnsNull()
    {
        var excerpts = new[]
        {
            new AttachmentExcerpt("notes.md", "# Title", null),
            new AttachmentExcerpt("data.csv", "a,b,c", null),
        };

        Assert.IsNull(DocumentTool.TryVerbatimMarkdownContent(excerpts));
    }

    [TestMethod]
    public void StripWholeDocumentCodeFence_LeavesDocumentWithItsOwnEmbeddedCodeBlockUntouched()
    {
        // A document that legitimately starts and ends with its own separate code examples must not
        // be mistaken for one big accidental outer wrap.
        var content = "```csharp\nvar x = 1;\n```\n\nSome prose.\n\n```csharp\nvar y = 2;\n```";

        var stripped = DocumentTool.StripWholeDocumentCodeFence(content);

        Assert.AreEqual(content, stripped);
    }

    [TestMethod]
    public void StripWholeDocumentCodeFence_PlainContent_IsUnchanged()
    {
        var content = "# Title\n\nSome text.";

        var stripped = DocumentTool.StripWholeDocumentCodeFence(content);

        Assert.AreEqual(content, stripped);
    }

    [TestMethod]
    public void ResolveContent_WithAttachment_PrefersSubstantialReplyOverRawAttachment()
    {
        // The reply is what synthesizes/organizes the attachment's data (e.g. a template filled in
        // from it); the raw file must not override that synthesized result.
        var chatReply = new string('a', 200);
        var excerpts = new[] { new AttachmentExcerpt("notes.txt", "Attached file content.", null) };

        var resolved = DocumentTool.ResolveContent(chatReply, [], excerpts);

        Assert.AreEqual(chatReply, resolved);
    }

    [TestMethod]
    public void ResolveContent_WithAttachment_FallsBackToAttachmentWhenReplyIsJustAConfirmation()
    {
        var shortReply = "I've added it to the document panel.";
        var excerpts = new[] { new AttachmentExcerpt("notes.txt", "Attached file content.", null) };

        var resolved = DocumentTool.ResolveContent(shortReply, [], excerpts);

        Assert.AreEqual("Attached file content.", resolved);
    }

    [TestMethod]
    public void ResolveContent_WithMultipleAttachments_CombinesWithFileHeadingsWhenReplyIsNotSubstantial()
    {
        var excerpts = new[]
        {
            new AttachmentExcerpt("a.txt", "First file.", null),
            new AttachmentExcerpt("b.txt", "Second file.", null),
        };

        var resolved = DocumentTool.ResolveContent("short reply", [], excerpts);

        Assert.AreEqual("## a.txt\nFirst file.\n\n## b.txt\nSecond file.", resolved);
    }

    [TestMethod]
    public void ResolveContent_WithUnreadableAttachmentAndNoSubstantialReply_ReturnsNull()
    {
        var excerpts = new[] { new AttachmentExcerpt("scan.png", null, "attach.unreadable") };

        var resolved = DocumentTool.ResolveContent("short reply", [], excerpts);

        Assert.IsNull(resolved);
    }

    [TestMethod]
    public void ResolveContent_FallsBackToMostRecentSubstantialAssistantMessage()
    {
        var shortReply = "Please check the panel now.";
        var priorPath = new[]
        {
            Message("user", "the specific content is the template you already suggested"),
            Message("Aukenid", new string('b', 300)),
        };

        var resolved = DocumentTool.ResolveContent(shortReply, priorPath);

        Assert.AreEqual(new string('b', 300), resolved);
    }

    [TestMethod]
    public void ResolveContent_ReturnsNullWhenNothingSubstantialExists()
    {
        var shortReply = "Done.";
        var priorPath = new[]
        {
            Message("user", "short question"),
            Message("Aukenid", "short reply"),
        };

        Assert.IsNull(DocumentTool.ResolveContent(shortReply, priorPath));
    }

    [TestMethod]
    public void ResolveContent_IgnoresUserMessagesWhenSearchingBackward()
    {
        var shortReply = "Done.";
        var priorPath = new[]
        {
            Message("Aukenid", new string('c', 150)),
            Message("user", new string('d', 150)),
        };

        var resolved = DocumentTool.ResolveContent(shortReply, priorPath);

        Assert.AreEqual(new string('c', 150), resolved);
    }

    [TestMethod]
    public void Apply_WritesDocumentAndSnapshotsBeforeAndAfter()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        workspace.Store.SaveDocument(thread.Id, "old content");
        var tool = new DocumentTool(workspace.Store);

        var result = tool.Apply(thread.Id, new string('x', 150), [], []);

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(new string('x', 150), workspace.Store.ReadDocument(thread.Id));
        var versions = workspace.Store.ListDocumentVersions(thread.Id);
        Assert.AreEqual(2, versions.Count);
        Assert.IsTrue(versions.Any(v => v.Label == "before-edit"));
        Assert.IsTrue(versions.Any(v => v.Label == "after-edit"));
    }

    [TestMethod]
    public void Apply_ReturnsNotAppliedWhenNothingSubstantialToWrite()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        var tool = new DocumentTool(workspace.Store);

        var result = tool.Apply(thread.Id, "Done.", [], []);

        Assert.IsFalse(result.Applied);
        Assert.IsNull(workspace.Store.ReadDocument(thread.Id));
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
