using Aukenid.Core.Data;
using Aukenid.Core.Services;
using Aukenid.Core.Contracts;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class DesktopFoundationTests
{
    [TestMethod]
    [DataRow("localhost", true)]
    [DataRow("127.0.0.1", true)]
    [DataRow("192.168.1.100", true)]
    [DataRow("8.8.8.8", false)]
    [DataRow("example.com", false)]
    public void IsBlockedHost_RecognizesPrivateAndPublicHosts(string host, bool expected)
    {
        Assert.AreEqual(expected, WebFetchService.IsBlockedHost(host));
    }

    [TestMethod]
    public void ConversationStore_PersistsTreeMessagesAsMarkdownFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);

        var user = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Hola", DateTimeOffset.UtcNow);
        var assistant = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, user.Id, "Aukenid", "Hola!", DateTimeOffset.UtcNow.AddSeconds(1));

        store.SaveMessage(user);
        store.SaveMessage(assistant);

        var messages = store.ListMessages(thread.Id);

        Assert.AreEqual(2, messages.Count);
        Assert.AreEqual(user.Id, messages[0].Id);
        Assert.AreEqual(user.Id, messages[1].ParentId);

        var files = Directory.GetFiles(Path.Combine(root, ConversationStore.GeneralFolder), "conversation.md", SearchOption.AllDirectories);
        Assert.AreEqual(1, files.Length);

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_GivesEachThreadItsOwnFolderWithAttachmentsSubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);

        var attachmentsDir = store.GetAttachmentsDirectory(thread.Id);

        Assert.IsNotNull(attachmentsDir);
        Assert.IsTrue(Directory.Exists(attachmentsDir));
        Assert.AreEqual(Path.Combine(root, ConversationStore.GeneralFolder, "Test", "attachments"), attachmentsDir);

        // The thread's own folder must not surface as a user-facing folder in the tree.
        Assert.IsFalse(store.ListFolders().Any(f => f.Path == $"{ConversationStore.GeneralFolder}/Test"));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_GetThreadFolderNameReflectsDirectoryNotRenamedTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("New conversation", ConversationStore.GeneralFolder);

        // Renaming only updates the title inside conversation.md, not the on-disk folder name.
        store.RenameThread(thread.Id, "Derived from first prompt");

        Assert.AreEqual("New conversation", store.GetThreadFolderName(thread.Id));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_GetThreadFolderNameReturnsNullForTemporalThreads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Scratch", ConversationStore.TemporalFolder);

        Assert.IsNull(store.GetThreadFolderName(thread.Id));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_DeletingThreadDirectoryRemovesConversationAndAttachments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);
        var attachmentsDir = store.GetAttachmentsDirectory(thread.Id)!;
        File.WriteAllText(Path.Combine(attachmentsDir, "image.png"), "fake-bytes");

        var threadDir = Path.Combine(root, ConversationStore.GeneralFolder, "Test");
        Directory.Delete(threadDir, recursive: true);

        Assert.IsFalse(Directory.Exists(threadDir));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_TemporalThreadsAreNotPersistedToDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Scratch", ConversationStore.TemporalFolder);
        store.SaveMessage(new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Hi", DateTimeOffset.UtcNow));

        Assert.AreEqual(1, store.ListMessages(thread.Id).Count);

        var temporalDir = Path.Combine(root, ConversationStore.TemporalFolder);
        Assert.IsFalse(Directory.Exists(temporalDir));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_MoveExchangeToNewThreadRelocatesTheTriggeringTurnOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Work", ConversationStore.GeneralFolder);
        var firstUser = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Tell me about the Jev AI model", DateTimeOffset.UtcNow);
        store.SaveMessage(firstUser);
        var firstReply = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, firstUser.Id, "Aukenid", "Jev is a GGUF model.", DateTimeOffset.UtcNow.AddSeconds(1));
        store.SaveMessage(firstReply);
        var secondUser = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, firstReply.Id, "user", "What does Azure offer for aggregation?", DateTimeOffset.UtcNow.AddSeconds(2));
        store.SaveMessage(secondUser);
        var secondReply = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, secondUser.Id, "Aukenid", "Azure offers Event Hubs.", DateTimeOffset.UtcNow.AddSeconds(3));
        store.SaveMessage(secondReply);

        var result = store.MoveExchangeToNewThread(thread.Id, secondUser.Id, "New conversation");

        Assert.IsNotNull(result);
        var (newThread, movedMessages) = result!.Value;
        Assert.AreEqual(ConversationStore.GeneralFolder, newThread.FolderPath);
        Assert.AreEqual(2, movedMessages.Count);

        var remaining = store.ListMessages(thread.Id);
        Assert.AreEqual(2, remaining.Count);
        Assert.IsFalse(remaining.Any(m => m.Id == secondUser.Id || m.Id == secondReply.Id));

        var movedInNewThread = store.ListMessages(newThread.Id);
        Assert.AreEqual(2, movedInNewThread.Count);
        Assert.IsTrue(movedInNewThread.Any(m => m.Id == secondUser.Id && m.ParentId == null));
        Assert.IsTrue(movedInNewThread.Any(m => m.Id == secondReply.Id && m.ParentId == secondUser.Id));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_MoveExchangeToNewThreadRelocatesAttachmentFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Work", ConversationStore.GeneralFolder);
        var sourceFile = Path.Combine(Path.GetTempPath(), $"aukenid-attach-{Guid.NewGuid():N}.txt");
        File.WriteAllText(sourceFile, "contents");
        var storedName = store.SaveAttachment(thread.Id, sourceFile)!;

        var user = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "See attached", DateTimeOffset.UtcNow, [storedName]);
        store.SaveMessage(user);

        var result = store.MoveExchangeToNewThread(thread.Id, user.Id, "New conversation");

        Assert.IsNotNull(result);
        var newThread = result!.Value.Thread;
        Assert.IsNull(store.TryGetAttachmentPath(thread.Id, storedName));
        Assert.IsNotNull(store.TryGetAttachmentPath(newThread.Id, storedName));

        File.Delete(sourceFile);
        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_MoveExchangeToNewThreadWorksForTemporalThreads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Scratch", ConversationStore.TemporalFolder);
        var user = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Unrelated question", DateTimeOffset.UtcNow);
        store.SaveMessage(user);

        var result = store.MoveExchangeToNewThread(thread.Id, user.Id, "New conversation");

        Assert.IsNotNull(result);
        Assert.AreEqual(ConversationStore.TemporalFolder, result!.Value.Thread.FolderPath);
        Assert.AreEqual(0, store.ListMessages(thread.Id).Count);
        Assert.AreEqual(1, store.ListMessages(result.Value.Thread.Id).Count);

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_MoveExchangeToNewThreadReturnsNullWhenMessageIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Work", ConversationStore.GeneralFolder);

        Assert.IsNull(store.MoveExchangeToNewThread(thread.Id, "missing-id", "New conversation"));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_SplitThreadWithHistoryCopiesThePrefixAndLeavesTheOriginalUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Work", ConversationStore.GeneralFolder);
        var root1 = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "First question", DateTimeOffset.UtcNow);
        store.SaveMessage(root1);
        var reply1 = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, root1.Id, "Aukenid", "First answer", DateTimeOffset.UtcNow.AddSeconds(1));
        store.SaveMessage(reply1);
        var root2 = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, reply1.Id, "user", "Second question", DateTimeOffset.UtcNow.AddSeconds(2));
        store.SaveMessage(root2);

        var result = store.SplitThread(thread.Id, reply1.Id, "Split", includeHistory: true, removeFromSource: false);

        Assert.IsNotNull(result);
        var (newThread, movedMessages) = result!.Value;
        Assert.AreEqual(ConversationStore.GeneralFolder, newThread.FolderPath);
        Assert.AreEqual(2, movedMessages.Count);
        Assert.AreEqual(root1.Content, movedMessages[0].Content);
        Assert.IsNull(movedMessages[0].ParentId);
        Assert.AreEqual(reply1.Content, movedMessages[1].Content);
        Assert.AreEqual(movedMessages[0].Id, movedMessages[1].ParentId);

        // The original thread keeps everything, including the message split from (M).
        Assert.AreEqual(3, store.ListMessages(thread.Id).Count);

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_SplitThreadWithoutHistoryMatchesMoveExchange()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Work", ConversationStore.GeneralFolder);
        var user = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Unrelated question", DateTimeOffset.UtcNow);
        store.SaveMessage(user);
        var reply = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, user.Id, "Aukenid", "Unrelated answer", DateTimeOffset.UtcNow.AddSeconds(1));
        store.SaveMessage(reply);

        var result = store.SplitThread(thread.Id, user.Id, "New conversation", includeHistory: false, removeFromSource: true);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result!.Value.Messages.Count);
        Assert.AreEqual(0, store.ListMessages(thread.Id).Count);

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_SplitThreadReturnsNullWhenMessageIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Work", ConversationStore.GeneralFolder);

        Assert.IsNull(store.SplitThread(thread.Id, "missing-id", "Split", includeHistory: true, removeFromSource: false));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_PruneThreadKeepsOnlyTheActivePathUpToTheMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Work", ConversationStore.GeneralFolder);
        var root1 = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "First question", DateTimeOffset.UtcNow);
        store.SaveMessage(root1);
        var reply1 = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, root1.Id, "Aukenid", "First answer", DateTimeOffset.UtcNow.AddSeconds(1));
        store.SaveMessage(reply1);
        var root2 = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, reply1.Id, "user", "Second question", DateTimeOffset.UtcNow.AddSeconds(2));
        store.SaveMessage(root2);
        var reply2 = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, root2.Id, "Aukenid", "Second answer", DateTimeOffset.UtcNow.AddSeconds(3));
        store.SaveMessage(reply2);

        var remaining = store.PruneThread(thread.Id, reply1.Id);

        Assert.IsNotNull(remaining);
        Assert.AreEqual(2, remaining!.Count);
        CollectionAssert.AreEqual(new[] { root1.Id, reply1.Id }, remaining.Select(m => m.Id).ToArray());

        var persisted = store.ListMessages(thread.Id);
        Assert.AreEqual(2, persisted.Count);
        Assert.IsFalse(persisted.Any(m => m.Id == root2.Id || m.Id == reply2.Id));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_PruneThreadDeletesAttachmentsThatOnlyBelongedToDiscardedMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Work", ConversationStore.GeneralFolder);
        var sourceFile = Path.Combine(Path.GetTempPath(), $"aukenid-attach-{Guid.NewGuid():N}.txt");
        File.WriteAllText(sourceFile, "contents");
        var storedName = store.SaveAttachment(thread.Id, sourceFile)!;

        var user = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Keep me", DateTimeOffset.UtcNow);
        store.SaveMessage(user);
        var discarded = new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, user.Id, "user", "Drop me", DateTimeOffset.UtcNow.AddSeconds(1), [storedName]);
        store.SaveMessage(discarded);

        store.PruneThread(thread.Id, user.Id);

        Assert.IsNull(store.TryGetAttachmentPath(thread.Id, storedName));

        File.Delete(sourceFile);
        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_PruneThreadReturnsNullWhenMessageIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Work", ConversationStore.GeneralFolder);

        Assert.IsNull(store.PruneThread(thread.Id, "missing-id"));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_RenameThreadUpdatesPersistedTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("New conversation", ConversationStore.GeneralFolder);

        store.RenameThread(thread.Id, "Derived from first prompt");

        var renamed = store.ListThreads().Single(t => t.Id == thread.Id);
        Assert.AreEqual("Derived from first prompt", renamed.Title);

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_RenameThreadWorksForTemporalThreads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("New conversation", ConversationStore.TemporalFolder);

        store.RenameThread(thread.Id, "Derived from first prompt");

        var renamed = store.ListThreads().Single(t => t.Id == thread.Id);
        Assert.AreEqual("Derived from first prompt", renamed.Title);

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_DeleteThreadRemovesPersistedThread()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);
        var threadDir = Path.Combine(root, ConversationStore.GeneralFolder, "Test");

        store.DeleteThread(thread.Id);

        Assert.IsFalse(Directory.Exists(threadDir));
        Assert.IsFalse(store.ListThreads().Any(t => t.Id == thread.Id));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_DeleteThreadRemovesTemporalThread()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Scratch", ConversationStore.TemporalFolder);

        store.DeleteThread(thread.Id);

        Assert.IsFalse(store.ListThreads().Any(t => t.Id == thread.Id));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_MoveThreadBetweenPersistedFoldersPreservesMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var folder = store.CreateFolder(string.Empty, "Work");
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);
        store.SaveMessage(new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Hola", DateTimeOffset.UtcNow));

        store.MoveThread(thread.Id, folder.Path);

        var moved = store.ListThreads().Single(t => t.Id == thread.Id);
        Assert.AreEqual(folder.Path, moved.FolderPath);
        Assert.AreEqual(1, store.ListMessages(thread.Id).Count);

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_MoveThreadIntoAndOutOfTemporalPreservesMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);
        store.SaveMessage(new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Hola", DateTimeOffset.UtcNow));

        store.MoveThread(thread.Id, ConversationStore.TemporalFolder);
        var temporal = store.ListThreads().Single(t => t.Id == thread.Id);
        Assert.AreEqual(ConversationStore.TemporalFolder, temporal.FolderPath);
        Assert.AreEqual(1, store.ListMessages(thread.Id).Count);

        store.MoveThread(thread.Id, ConversationStore.GeneralFolder);
        var persisted = store.ListThreads().Single(t => t.Id == thread.Id);
        Assert.AreEqual(ConversationStore.GeneralFolder, persisted.FolderPath);
        Assert.AreEqual(1, store.ListMessages(thread.Id).Count);

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_RenameFolderMovesDirectoryAndUpdatesThreadPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var folder = store.CreateFolder(string.Empty, "Work");
        var thread = store.CreateThread("Test", folder.Path);

        var renamed = store.RenameFolder(folder.Path, "Projects");

        Assert.IsNotNull(renamed);
        Assert.AreEqual("Projects", renamed!.Name);
        Assert.AreEqual("Projects", store.ListThreads().Single(t => t.Id == thread.Id).FolderPath);

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_RenameFolderRejectsStandardFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);

        Assert.IsNull(store.RenameFolder(ConversationStore.GeneralFolder, "Whatever"));
        Assert.IsNull(store.RenameFolder(ConversationStore.TemporalFolder, "Whatever"));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_DeleteFolderRemovesFolderAndItsThreads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var folder = store.CreateFolder(string.Empty, "Work");
        var thread = store.CreateThread("Test", folder.Path);

        store.DeleteFolder(folder.Path);

        Assert.IsFalse(store.ListFolders().Any(f => f.Path == folder.Path));
        Assert.IsFalse(store.ListThreads().Any(t => t.Id == thread.Id));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "Work")));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_EmptyFolderDeletesThreadsButKeepsFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var folder = store.CreateFolder(string.Empty, "Work");
        var thread = store.CreateThread("Test", folder.Path);

        store.EmptyFolder(folder.Path);

        Assert.IsTrue(store.ListFolders().Any(f => f.Path == folder.Path));
        Assert.IsFalse(store.ListThreads().Any(t => t.Id == thread.Id));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_MergeFolderMovesThreadsAndDeletesEmptySourceFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var source = store.CreateFolder(string.Empty, "Old");
        var target = store.CreateFolder(string.Empty, "New");
        var thread = store.CreateThread("Test", source.Path);

        store.MergeFolder(source.Path, target.Path);

        Assert.AreEqual(target.Path, store.ListThreads().Single(t => t.Id == thread.Id).FolderPath);
        Assert.IsFalse(store.ListFolders().Any(f => f.Path == source.Path));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ConversationStore_MergeFolderKeepsGeneralAndTemporalAfterMerging()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var target = store.CreateFolder(string.Empty, "Archive");
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);

        store.MergeFolder(ConversationStore.GeneralFolder, target.Path);

        Assert.AreEqual(target.Path, store.ListThreads().Single(t => t.Id == thread.Id).FolderPath);
        Assert.IsTrue(store.ListFolders().Any(f => f.Path == ConversationStore.GeneralFolder));

        Directory.Delete(root, recursive: true);
    }
}
