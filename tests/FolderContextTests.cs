using Aukenid.Core.Data;
using Aukenid.Core.Engine;
using Aukenid.Core.Contracts;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class FolderContextTests
{
    [TestMethod]
    public void TryBuild_ReturnsNullForGeneralAndTemporal()
    {
        var sibling = Thread("s1", ConversationStore.GeneralFolder, "Other chat");
        var messages = Messages(("s1", "u1", "user", "secret from general"));

        Assert.IsNull(FolderContext.TryBuild(
            ConversationStore.GeneralFolder,
            "General",
            "current",
            [sibling],
            id => messages[id]));

        Assert.IsNull(FolderContext.TryBuild(
            ConversationStore.TemporalFolder,
            "Temporal",
            "current",
            [sibling with { FolderPath = ConversationStore.TemporalFolder }],
            id => messages[id]));
    }

    [TestMethod]
    public void TryBuild_ReturnsNullWhenFolderHasNoOtherThreads()
    {
        var current = Thread("c1", "Work", "Only chat");
        Assert.IsNull(FolderContext.TryBuild("Work", "Work", "c1", [current], _ => []));
    }

    [TestMethod]
    public void TryBuild_InjectsSiblingTranscriptAndOmitsCurrentThread()
    {
        var current = Thread("c1", "Work", "Current");
        var sibling = Thread("s1", "Work", "Trip to Cusco");
        var outsider = Thread("o1", "Other", "Unrelated");
        var messages = Messages(
            ("c1", "cu1", "user", "current-only-secret"),
            ("s1", "su1", "user", "we fly to cusco on friday"),
            ("s1", "sa1", "Aukenid", "noted, friday flight"),
            ("o1", "ou1", "user", "outsider-secret"));

        var text = FolderContext.TryBuild("Work", "Work", "c1", [current, sibling, outsider], id => messages[id]);

        Assert.IsNotNull(text);
        Assert.IsTrue(text.Contains("Work", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Trip to Cusco", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("we fly to cusco on friday", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("noted, friday flight", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("current-only-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("outsider-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Current", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryTurn_IsASystemTurn()
    {
        var sibling = Thread("s1", "Work", "Alpha");
        var messages = Messages(("s1", "su1", "user", "hello project"));

        var turn = FolderContext.TryTurn("Work", "Work", "c1", [sibling], id => messages[id]);

        Assert.IsNotNull(turn);
        Assert.AreEqual("system", turn.Value.Role);
        Assert.IsTrue(turn.Value.Content.Contains("hello project", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryBuild_SkipsEmptySiblings()
    {
        var empty = Thread("s1", "Work", "Empty");
        var filled = Thread("s2", "Work", "Filled");
        var messages = Messages(("s2", "su1", "user", "payload"));

        var text = FolderContext.TryBuild(
            "Work",
            "Work",
            "c1",
            [empty, filled],
            id => messages.GetValueOrDefault(id) ?? []);

        Assert.IsNotNull(text);
        Assert.IsTrue(text.Contains("Filled", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("payload", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Empty", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryBuild_TruncatesToBudget()
    {
        var sibling = Thread("s1", "Work", "Long");
        var messages = Messages(("s1", "su1", "user", new string('x', 400)));

        var text = FolderContext.TryBuild(
            "Work",
            "Work",
            "c1",
            [sibling],
            id => messages[id],
            maxTotalChars: 2000,
            maxCharsPerThread: 80);

        Assert.IsNotNull(text);
        Assert.IsTrue(text.Contains('\u2026'));
        Assert.IsFalse(text.Contains(new string('x', 400), StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryBuild_UsesActivePathNotSiblingBranches()
    {
        var sibling = Thread("s1", "Work", "Branched");
        var root = new MessageDto("u1", "s1", null, "user", "root question", DateTimeOffset.UnixEpoch);
        var a1 = new MessageDto("a1", "s1", "u1", "Aukenid", "root answer", DateTimeOffset.UnixEpoch.AddSeconds(1));
        var discarded = new MessageDto("u2", "s1", "a1", "user", "discarded-branch", DateTimeOffset.UnixEpoch.AddSeconds(2));
        var kept = new MessageDto("u2b", "s1", "a1", "user", "kept-branch", DateTimeOffset.UnixEpoch.AddSeconds(3));

        var text = FolderContext.TryBuild("Work", "Work", "c1", [sibling], _ => [root, a1, discarded, kept]);

        Assert.IsNotNull(text);
        Assert.IsTrue(text.Contains("kept-branch", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("discarded-branch", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IsHeavy_FalseForGeneralAndTemporalRegardlessOfSiblingCount()
    {
        var siblings = Enumerable.Range(0, 10)
            .Select(i => Thread($"s{i}", ConversationStore.GeneralFolder, $"Chat {i}"))
            .ToList();

        Assert.IsFalse(FolderContext.IsHeavy(ConversationStore.GeneralFolder, "current", siblings));
    }

    [TestMethod]
    public void IsHeavy_FalseAtOrBelowTheCheapThreshold()
    {
        var siblings = Enumerable.Range(0, FolderContext.CheapSiblingThreshold)
            .Select(i => Thread($"s{i}", "Work", $"Chat {i}"))
            .ToList();

        Assert.IsFalse(FolderContext.IsHeavy("Work", "current", siblings));
    }

    [TestMethod]
    public void IsHeavy_TrueOncePastTheCheapThreshold()
    {
        var siblings = Enumerable.Range(0, FolderContext.CheapSiblingThreshold + 1)
            .Select(i => Thread($"s{i}", "Work", $"Chat {i}"))
            .ToList();

        Assert.IsTrue(FolderContext.IsHeavy("Work", "current", siblings));
    }

    [TestMethod]
    public void IsHeavy_IgnoresEmptySiblingsAndTheCurrentThread()
    {
        var siblings = Enumerable.Range(0, FolderContext.CheapSiblingThreshold + 5)
            .Select(i => Thread($"s{i}", "Work", $"Chat {i}") with { HasMessages = false })
            .Append(Thread("current", "Work", "Current"))
            .ToList();

        Assert.IsFalse(FolderContext.IsHeavy("Work", "current", siblings));
    }

    private static ThreadDto Thread(string id, string folder, string title) =>
        new(id, title, folder, DateTimeOffset.UnixEpoch, HasMessages: true);

    private static Dictionary<string, List<MessageDto>> Messages(params (string ThreadId, string Id, string Role, string Content)[] items)
    {
        var map = new Dictionary<string, List<MessageDto>>();
        foreach (var (threadId, id, role, content) in items)
        {
            if (!map.TryGetValue(threadId, out var list))
            {
                list = [];
                map[threadId] = list;
            }

            var parent = list.Count == 0 ? null : list[^1].Id;
            list.Add(new MessageDto(id, threadId, parent, role, content, DateTimeOffset.UnixEpoch.AddSeconds(list.Count)));
        }

        return map;
    }
}
