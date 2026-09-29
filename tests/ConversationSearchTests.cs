using Aukenid.Core.Data;
using Aukenid.Core.Contracts;

namespace Aukenid.Core.Tests;

public sealed class ConversationSearchTests
{
    [Fact]
    public void Search_FindsPersistedAndTemporalThreadsWithOffsets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-search-{Guid.NewGuid():N}");
        try
        {
            var store = new ConversationStore(root);
            var persisted = store.CreateThread("Persisted", ConversationStore.GeneralFolder);
            var temporal = store.CreateThread("Scratch", ConversationStore.TemporalFolder);

            store.SaveMessage(new MessageDto(
                Guid.CreateVersion7().ToString(), persisted.Id, null, "user", "Hello world", DateTimeOffset.UtcNow));
            store.SaveMessage(new MessageDto(
                Guid.CreateVersion7().ToString(), temporal.Id, null, "user", "Say hello again", DateTimeOffset.UtcNow));

            var result = new ConversationSearch(store).Search("hello");

            Assert.Equal("hello", result.Query);
            Assert.Equal(2, result.Threads.Count);
            Assert.Contains(result.Folders, folder => folder.Path == ConversationStore.GeneralFolder);
            Assert.Contains(result.Folders, folder => folder.Path == ConversationStore.TemporalFolder);

            var persistedHit = result.Threads.Single(thread => thread.Id == persisted.Id);
            Assert.Equal(1, persistedHit.Count);
            Assert.Equal(0, persistedHit.Occurrences[0].Offset);
            Assert.Equal(5, persistedHit.Occurrences[0].Length);
            Assert.Equal(persisted.Id, persistedHit.Occurrences[0].ThreadId);

            var temporalHit = result.Threads.Single(thread => thread.Id == temporal.Id);
            Assert.Equal(1, temporalHit.Count);
            Assert.Equal(4, temporalHit.Occurrences[0].Offset);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Search_IsCaseInsensitiveAndCollectsMultipleHitsInOneMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-search-{Guid.NewGuid():N}");
        try
        {
            var store = new ConversationStore(root);
            var thread = store.CreateThread("Chat", ConversationStore.GeneralFolder);
            store.SaveMessage(new MessageDto(
                Guid.CreateVersion7().ToString(), thread.Id, null, "user", "Hello world, hello", DateTimeOffset.UtcNow));

            var result = new ConversationSearch(store).Search("HELLO");
            var hit = Assert.Single(result.Threads);

            Assert.Equal(2, hit.Count);
            Assert.Equal(0, hit.Occurrences[0].Offset);
            Assert.Equal(13, hit.Occurrences[1].Offset);
            Assert.All(hit.Occurrences, occurrence => Assert.Equal(5, occurrence.Length));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Search_OmitsFoldersWithoutMatchesAndIgnoresEmptyQuery()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-search-{Guid.NewGuid():N}");
        try
        {
            var store = new ConversationStore(root);
            var general = store.CreateThread("General chat", ConversationStore.GeneralFolder);
            store.CreateFolder(string.Empty, "Work");
            store.SaveMessage(new MessageDto(
                Guid.CreateVersion7().ToString(), general.Id, null, "user", "Only this folder matches", DateTimeOffset.UtcNow));

            var search = new ConversationSearch(store);
            var empty = search.Search("   ");
            Assert.Empty(empty.Threads);
            Assert.Empty(empty.Folders);

            var result = search.Search("folder");
            Assert.Single(result.Threads);
            Assert.Single(result.Folders);
            Assert.Equal(ConversationStore.GeneralFolder, result.Folders[0].Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Search_WholeWordSkipsEmbeddedMatches()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-search-{Guid.NewGuid():N}");
        try
        {
            var store = new ConversationStore(root);
            var thread = store.CreateThread("Chat", ConversationStore.GeneralFolder);
            store.SaveMessage(new MessageDto(
                Guid.CreateVersion7().ToString(),
                thread.Id,
                null,
                "user",
                "A cat sat. Category and bobcat.",
                DateTimeOffset.UtcNow));

            var search = new ConversationSearch(store);
            var all = search.Search("cat");
            var whole = search.Search("cat", wholeWord: true);

            Assert.Equal(3, Assert.Single(all.Threads).Count);
            var hit = Assert.Single(whole.Threads);
            Assert.Equal(1, hit.Count);
            Assert.Equal(2, hit.Occurrences[0].Offset);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
