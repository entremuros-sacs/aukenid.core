using Aukenid.Core.Data;
using Aukenid.Core.Contracts;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class BookmarkStoreTests
{
    [TestMethod]
    public void Toggle_AddsAndRemovesUserPrompt()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Chat", ConversationStore.GeneralFolder);
        var prompt = SaveUser(workspace.Store, thread.Id, "Remember this later");

        Assert.IsTrue(workspace.Bookmarks.Toggle(thread.Id, prompt.Id));
        Assert.IsTrue(workspace.Bookmarks.IsBookmarked(thread.Id, prompt.Id));

        Assert.IsFalse(workspace.Bookmarks.Toggle(thread.Id, prompt.Id));
        Assert.IsFalse(workspace.Bookmarks.IsBookmarked(thread.Id, prompt.Id));
    }

    [TestMethod]
    public void Toggle_IgnoresAssistantMessagesAndMissingIds()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Chat", ConversationStore.GeneralFolder);
        var prompt = SaveUser(workspace.Store, thread.Id, "Hello");
        var reply = new MessageDto(
            Guid.CreateVersion7().ToString(), thread.Id, prompt.Id, "Aukenid", "Hi", DateTimeOffset.UtcNow);
        workspace.Store.SaveMessage(reply);

        Assert.IsFalse(workspace.Bookmarks.Toggle(thread.Id, reply.Id));
        Assert.IsFalse(workspace.Bookmarks.Toggle(thread.Id, Guid.CreateVersion7().ToString()));
        Assert.IsFalse(workspace.Bookmarks.Toggle("", prompt.Id));
        Assert.AreEqual(0, workspace.Bookmarks.List().Threads.Count);
    }

    [TestMethod]
    public void List_GroupsByFolderAndThread_TruncatesSnippetTo50()
    {
        using var workspace = new Workspace();
        var general = workspace.Store.CreateThread("General chat", ConversationStore.GeneralFolder);
        var workFolder = workspace.Store.CreateFolder(string.Empty, "Work");
        var work = workspace.Store.CreateThread("Work chat", workFolder.Path);
        var temporal = workspace.Store.CreateThread("Scratch", ConversationStore.TemporalFolder);

        var first = SaveUser(workspace.Store, general.Id, "First prompt in general", DateTimeOffset.UtcNow);
        var second = SaveUser(workspace.Store, general.Id, "Second prompt in general", DateTimeOffset.UtcNow.AddSeconds(1));
        var workPrompt = SaveUser(workspace.Store, work.Id, "Work prompt");
        var temporalPrompt = SaveUser(workspace.Store, temporal.Id, "Temp prompt");
        var longPrompt = SaveUser(
            workspace.Store,
            work.Id,
            "This prompt is deliberately longer than fifty characters so the tree can truncate it");

        workspace.Bookmarks.Toggle(general.Id, second.Id);
        workspace.Bookmarks.Toggle(general.Id, first.Id);
        workspace.Bookmarks.Toggle(work.Id, workPrompt.Id);
        workspace.Bookmarks.Toggle(work.Id, longPrompt.Id);
        workspace.Bookmarks.Toggle(temporal.Id, temporalPrompt.Id);

        var result = workspace.Bookmarks.List();
        Assert.AreEqual(3, result.Folders.Count);
        Assert.AreEqual(ConversationStore.TemporalFolder, result.Folders[0].Path);
        Assert.AreEqual(ConversationStore.GeneralFolder, result.Folders[1].Path);
        Assert.AreEqual("Work", result.Folders[2].Path);

        var generalThread = result.Threads.Single(item => item.Id == general.Id);
        CollectionAssert.AreEqual(
            new[] { "First prompt in general", "Second prompt in general" },
            generalThread.Bookmarks.Select(item => item.Text).ToArray());

        var workThread = result.Threads.Single(item => item.Id == work.Id);
        var truncated = workThread.Bookmarks.Single(item => item.MessageId == longPrompt.Id).Text;
        Assert.AreEqual(50, truncated.Length);
        Assert.IsTrue(truncated.StartsWith("This prompt is deliberately longer", StringComparison.Ordinal));

        Assert.IsTrue(result.Threads.Any(item => item.Id == temporal.Id));
    }

    [TestMethod]
    public void List_CollapsesWhitespaceAndPersistsAcrossReload()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Chat", ConversationStore.GeneralFolder);
        var prompt = SaveUser(workspace.Store, thread.Id, "Hello\n\n  world");
        Assert.IsTrue(workspace.Bookmarks.Toggle(thread.Id, prompt.Id));

        var reloaded = new BookmarkStore(workspace.BookmarkPath, workspace.Store);
        var listed = reloaded.List().Threads.Single().Bookmarks.Single();
        Assert.AreEqual("Hello world", listed.Text);
        Assert.AreEqual(prompt.Id, listed.MessageId);
    }

    [TestMethod]
    public void List_PrunesDeletedThreadsAndMessages()
    {
        using var workspace = new Workspace();
        var keptThread = workspace.Store.CreateThread("Keep", ConversationStore.GeneralFolder);
        var goneThread = workspace.Store.CreateThread("Gone", ConversationStore.GeneralFolder);
        var kept = SaveUser(workspace.Store, keptThread.Id, "Keep me");
        var gone = SaveUser(workspace.Store, goneThread.Id, "Delete me");

        workspace.Bookmarks.Toggle(keptThread.Id, kept.Id);
        workspace.Bookmarks.Toggle(goneThread.Id, gone.Id);
        workspace.Store.DeleteThread(goneThread.Id);

        var result = workspace.Bookmarks.List();
        var remaining = result.Threads.Single();
        Assert.AreEqual(keptThread.Id, remaining.Id);
        Assert.AreEqual(kept.Id, remaining.Bookmarks.Single().MessageId);

        var reloaded = new BookmarkStore(workspace.BookmarkPath, workspace.Store);
        Assert.AreEqual(1, reloaded.List().Threads.Count);
    }

    [TestMethod]
    public void Snippet_UsesEllipsisWhenPromptIsEmpty()
    {
        Assert.AreEqual("\u2026", BookmarkStore.Snippet("   \n"));
        Assert.AreEqual("short", BookmarkStore.Snippet("short"));
    }

    private static MessageDto SaveUser(
        ConversationStore store, string threadId, string content, DateTimeOffset? created = null)
    {
        var message = new MessageDto(
            Guid.CreateVersion7().ToString(),
            threadId,
            null,
            "user",
            content,
            created ?? DateTimeOffset.UtcNow);
        store.SaveMessage(message);
        return message;
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _root;

        public Workspace()
        {
            _root = Path.Combine(Path.GetTempPath(), "aukenid-bookmarks-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Store = new ConversationStore(Path.Combine(_root, "conversations"));
            BookmarkPath = Path.Combine(_root, "bookmarks.json");
            Bookmarks = new BookmarkStore(BookmarkPath, Store);
        }

        public ConversationStore Store { get; }
        public string BookmarkPath { get; }
        public BookmarkStore Bookmarks { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
