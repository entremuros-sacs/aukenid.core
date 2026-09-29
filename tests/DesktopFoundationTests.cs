using Aukenid.Core.Data;
using Aukenid.Core.Services;
using Aukenid.Core.Contracts;

namespace Aukenid.Core.Tests;

public sealed class DesktopFoundationTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.100", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("example.com", false)]
    public void IsBlockedHost_RecognizesPrivateAndPublicHosts(string host, bool expected)
    {
        Assert.Equal(expected, WebFetchService.IsBlockedHost(host));
    }

    [Fact]
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

        Assert.Equal(2, messages.Count);
        Assert.Equal(user.Id, messages[0].Id);
        Assert.Equal(user.Id, messages[1].ParentId);

        var files = Directory.GetFiles(Path.Combine(root, ConversationStore.GeneralFolder), "conversation.md", SearchOption.AllDirectories);
        Assert.Single(files);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_GivesEachThreadItsOwnFolderWithAttachmentsSubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);

        var attachmentsDir = store.GetAttachmentsDirectory(thread.Id);

        Assert.NotNull(attachmentsDir);
        Assert.True(Directory.Exists(attachmentsDir));
        Assert.Equal(Path.Combine(root, ConversationStore.GeneralFolder, "Test", "attachments"), attachmentsDir);

        // The thread's own folder must not surface as a user-facing folder in the tree.
        Assert.DoesNotContain(store.ListFolders(), f => f.Path == $"{ConversationStore.GeneralFolder}/Test");

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_GetThreadFolderNameReflectsDirectoryNotRenamedTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("New conversation", ConversationStore.GeneralFolder);

        // Renaming only updates the title inside conversation.md, not the on-disk folder name.
        store.RenameThread(thread.Id, "Derived from first prompt");

        Assert.Equal("New conversation", store.GetThreadFolderName(thread.Id));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_GetThreadFolderNameReturnsNullForTemporalThreads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Scratch", ConversationStore.TemporalFolder);

        Assert.Null(store.GetThreadFolderName(thread.Id));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_DeletingThreadDirectoryRemovesConversationAndAttachments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);
        var attachmentsDir = store.GetAttachmentsDirectory(thread.Id)!;
        File.WriteAllText(Path.Combine(attachmentsDir, "image.png"), "fake-bytes");

        var threadDir = Path.Combine(root, ConversationStore.GeneralFolder, "Test");
        Directory.Delete(threadDir, recursive: true);

        Assert.False(Directory.Exists(threadDir));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_TemporalThreadsAreNotPersistedToDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Scratch", ConversationStore.TemporalFolder);
        store.SaveMessage(new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Hi", DateTimeOffset.UtcNow));

        Assert.Single(store.ListMessages(thread.Id));

        var temporalDir = Path.Combine(root, ConversationStore.TemporalFolder);
        Assert.False(Directory.Exists(temporalDir));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_RenameThreadUpdatesPersistedTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("New conversation", ConversationStore.GeneralFolder);

        store.RenameThread(thread.Id, "Derived from first prompt");

        var renamed = store.ListThreads().Single(t => t.Id == thread.Id);
        Assert.Equal("Derived from first prompt", renamed.Title);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_RenameThreadWorksForTemporalThreads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("New conversation", ConversationStore.TemporalFolder);

        store.RenameThread(thread.Id, "Derived from first prompt");

        var renamed = store.ListThreads().Single(t => t.Id == thread.Id);
        Assert.Equal("Derived from first prompt", renamed.Title);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_DeleteThreadRemovesPersistedThread()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);
        var threadDir = Path.Combine(root, ConversationStore.GeneralFolder, "Test");

        store.DeleteThread(thread.Id);

        Assert.False(Directory.Exists(threadDir));
        Assert.DoesNotContain(store.ListThreads(), t => t.Id == thread.Id);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_DeleteThreadRemovesTemporalThread()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Scratch", ConversationStore.TemporalFolder);

        store.DeleteThread(thread.Id);

        Assert.DoesNotContain(store.ListThreads(), t => t.Id == thread.Id);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_MoveThreadBetweenPersistedFoldersPreservesMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var folder = store.CreateFolder(string.Empty, "Work");
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);
        store.SaveMessage(new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Hola", DateTimeOffset.UtcNow));

        store.MoveThread(thread.Id, folder.Path);

        var moved = store.ListThreads().Single(t => t.Id == thread.Id);
        Assert.Equal(folder.Path, moved.FolderPath);
        Assert.Single(store.ListMessages(thread.Id));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_MoveThreadIntoAndOutOfTemporalPreservesMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);
        store.SaveMessage(new MessageDto(Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Hola", DateTimeOffset.UtcNow));

        store.MoveThread(thread.Id, ConversationStore.TemporalFolder);
        var temporal = store.ListThreads().Single(t => t.Id == thread.Id);
        Assert.Equal(ConversationStore.TemporalFolder, temporal.FolderPath);
        Assert.Single(store.ListMessages(thread.Id));

        store.MoveThread(thread.Id, ConversationStore.GeneralFolder);
        var persisted = store.ListThreads().Single(t => t.Id == thread.Id);
        Assert.Equal(ConversationStore.GeneralFolder, persisted.FolderPath);
        Assert.Single(store.ListMessages(thread.Id));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_RenameFolderMovesDirectoryAndUpdatesThreadPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var folder = store.CreateFolder(string.Empty, "Work");
        var thread = store.CreateThread("Test", folder.Path);

        var renamed = store.RenameFolder(folder.Path, "Projects");

        Assert.NotNull(renamed);
        Assert.Equal("Projects", renamed!.Name);
        Assert.Equal("Projects", store.ListThreads().Single(t => t.Id == thread.Id).FolderPath);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_RenameFolderRejectsStandardFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);

        Assert.Null(store.RenameFolder(ConversationStore.GeneralFolder, "Whatever"));
        Assert.Null(store.RenameFolder(ConversationStore.TemporalFolder, "Whatever"));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_DeleteFolderRemovesFolderAndItsThreads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var folder = store.CreateFolder(string.Empty, "Work");
        var thread = store.CreateThread("Test", folder.Path);

        store.DeleteFolder(folder.Path);

        Assert.DoesNotContain(store.ListFolders(), f => f.Path == folder.Path);
        Assert.DoesNotContain(store.ListThreads(), t => t.Id == thread.Id);
        Assert.False(Directory.Exists(Path.Combine(root, "Work")));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_EmptyFolderDeletesThreadsButKeepsFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var folder = store.CreateFolder(string.Empty, "Work");
        var thread = store.CreateThread("Test", folder.Path);

        store.EmptyFolder(folder.Path);

        Assert.Contains(store.ListFolders(), f => f.Path == folder.Path);
        Assert.DoesNotContain(store.ListThreads(), t => t.Id == thread.Id);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_MergeFolderMovesThreadsAndDeletesEmptySourceFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var source = store.CreateFolder(string.Empty, "Old");
        var target = store.CreateFolder(string.Empty, "New");
        var thread = store.CreateThread("Test", source.Path);

        store.MergeFolder(source.Path, target.Path);

        Assert.Equal(target.Path, store.ListThreads().Single(t => t.Id == thread.Id).FolderPath);
        Assert.DoesNotContain(store.ListFolders(), f => f.Path == source.Path);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_MergeFolderKeepsGeneralAndTemporalAfterMerging()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-test-{Guid.NewGuid():N}");

        var store = new ConversationStore(root);
        var target = store.CreateFolder(string.Empty, "Archive");
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);

        store.MergeFolder(ConversationStore.GeneralFolder, target.Path);

        Assert.Equal(target.Path, store.ListThreads().Single(t => t.Id == thread.Id).FolderPath);
        Assert.Contains(store.ListFolders(), f => f.Path == ConversationStore.GeneralFolder);

        Directory.Delete(root, recursive: true);
    }
}
