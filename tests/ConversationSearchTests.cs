using Aukenid.Core.Data;
using Aukenid.Core.Contracts;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class ConversationSearchTests
{
    [TestMethod]
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

            Assert.AreEqual("hello", result.Query);
            Assert.AreEqual(2, result.Threads.Count);
            Assert.IsTrue(result.Folders.Any(folder => folder.Path == ConversationStore.GeneralFolder));
            Assert.IsTrue(result.Folders.Any(folder => folder.Path == ConversationStore.TemporalFolder));

            var persistedHit = result.Threads.Single(thread => thread.Id == persisted.Id);
            Assert.AreEqual(1, persistedHit.Count);
            Assert.AreEqual(0, persistedHit.Occurrences[0].Offset);
            Assert.AreEqual(5, persistedHit.Occurrences[0].Length);
            Assert.AreEqual(persisted.Id, persistedHit.Occurrences[0].ThreadId);

            var temporalHit = result.Threads.Single(thread => thread.Id == temporal.Id);
            Assert.AreEqual(1, temporalHit.Count);
            Assert.AreEqual(4, temporalHit.Occurrences[0].Offset);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
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
            var hit = result.Threads.Single();

            Assert.AreEqual(2, hit.Count);
            Assert.AreEqual(0, hit.Occurrences[0].Offset);
            Assert.AreEqual(13, hit.Occurrences[1].Offset);
            foreach (var occurrence in hit.Occurrences)
            {
                Assert.AreEqual(5, occurrence.Length);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
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
            Assert.AreEqual(0, empty.Threads.Count);
            Assert.AreEqual(0, empty.Folders.Count);

            var result = search.Search("folder");
            Assert.AreEqual(1, result.Threads.Count);
            Assert.AreEqual(1, result.Folders.Count);
            Assert.AreEqual(ConversationStore.GeneralFolder, result.Folders[0].Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
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

            Assert.AreEqual(3, all.Threads.Single().Count);
            var hit = whole.Threads.Single();
            Assert.AreEqual(1, hit.Count);
            Assert.AreEqual(2, hit.Occurrences[0].Offset);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
