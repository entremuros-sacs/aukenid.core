using Aukenid.Core.Attachments;
using Aukenid.Core.Engine;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class DocumentEditOrchestratorTests
{
    private const string Doc = "# Title\n\nIntro.\n\n## Ingredients\n\n- flour\n- sugar\n\n## Steps\n\nMix it.\n";

    [TestMethod]
    public void BuildEditTurns_IncludesSystemPromptAndChunkOutline()
    {
        var result = DocumentEditOrchestrator.BuildEditTurns([new ChatTurn("user", "add a step")], Doc, []);

        Assert.AreEqual("system", result.Turns[0].Role);
        Assert.IsTrue(result.Turns[^2].Content.Contains(">>> CHUNK", StringComparison.Ordinal));
        Assert.AreEqual(3, result.Chunks.Count);
        Assert.AreEqual(3, result.EditableIds.Count);
    }

    [TestMethod]
    public void BuildEditTurns_EmptyDocument_StillProducesAnInsertableContext()
    {
        var result = DocumentEditOrchestrator.BuildEditTurns([new ChatTurn("user", "write something")], null, []);

        Assert.AreEqual(0, result.Chunks.Count);
        Assert.IsTrue(result.Turns[^2].Content.Contains("target=START", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryApplyPatch_ReplaceKnownEditableChunk_Applies()
    {
        var prompt = DocumentEditOrchestrator.BuildEditTurns([new ChatTurn("user", "x")], Doc, []);
        var ingredients = prompt.Chunks.Single(c => c.HeadingPath == "Ingredients");
        var response = $">>> EDIT action=replace target={ingredients.Id}\n## Ingredients\n\n- flour\n- sugar\n- salt\n<<< END";

        var result = DocumentEditOrchestrator.TryApplyPatch(response, prompt.Chunks, prompt.EditableIds);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("- salt", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("Mix it.", StringComparison.Ordinal)); // untouched chunk survives
        Assert.IsTrue(result.Contains("Intro.", StringComparison.Ordinal)); // untouched chunk survives
    }

    [TestMethod]
    public void TryApplyPatch_UnknownTarget_ReturnsNullForFallback()
    {
        var prompt = DocumentEditOrchestrator.BuildEditTurns([new ChatTurn("user", "x")], Doc, []);
        var response = ">>> EDIT action=replace target=zzzzz\nnew content\n<<< END";

        var result = DocumentEditOrchestrator.TryApplyPatch(response, prompt.Chunks, prompt.EditableIds);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryApplyPatch_NoEditBlocks_ReturnsNull()
    {
        var prompt = DocumentEditOrchestrator.BuildEditTurns([new ChatTurn("user", "x")], Doc, []);

        var result = DocumentEditOrchestrator.TryApplyPatch("Sure, I can help with that!", prompt.Chunks, prompt.EditableIds);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryApplyPatch_Delete_RemovesChunkEvenIfNotShownInFull()
    {
        var prompt = DocumentEditOrchestrator.BuildEditTurns([new ChatTurn("user", "x")], Doc, []);
        var steps = prompt.Chunks.Single(c => c.HeadingPath == "Steps");
        // Only Ingredients/Title/Steps are small, so all are editable here, but delete should also
        // work through the broader "known ids" set, not just the editable one - use the known id set
        // directly to prove delete does not require editableIds membership.
        var notEditable = new HashSet<string>(StringComparer.Ordinal);
        var response = $">>> EDIT action=delete target={steps.Id}\n<<< END";

        var result = DocumentEditOrchestrator.TryApplyPatch(response, prompt.Chunks, notEditable);

        Assert.IsNotNull(result);
        Assert.IsFalse(result.Contains("Mix it.", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("Intro.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryApplyPatch_InsertAfterEnd_AppendsNewSection()
    {
        var prompt = DocumentEditOrchestrator.BuildEditTurns([new ChatTurn("user", "x")], Doc, []);
        var response = ">>> EDIT action=insert_after target=END\n## Notes\n\nSome new note.\n<<< END";

        var result = DocumentEditOrchestrator.TryApplyPatch(response, prompt.Chunks, prompt.EditableIds);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.EndsWith("Some new note.\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryApplyPatch_ReplaceWithEmptyContent_ActsAsDelete()
    {
        var prompt = DocumentEditOrchestrator.BuildEditTurns([new ChatTurn("user", "x")], Doc, []);
        var steps = prompt.Chunks.Single(c => c.HeadingPath == "Steps");
        var response = $">>> EDIT action=replace target={steps.Id}\n\n<<< END";

        var result = DocumentEditOrchestrator.TryApplyPatch(response, prompt.Chunks, prompt.EditableIds);

        Assert.IsNotNull(result);
        Assert.IsFalse(result.Contains("Mix it.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryApplyPatch_ReplacingOmittedChunk_ReturnsNull()
    {
        var prompt = DocumentEditOrchestrator.BuildEditTurns([new ChatTurn("user", "x")], Doc, []);
        var steps = prompt.Chunks.Single(c => c.HeadingPath == "Steps");
        var editableWithoutSteps = prompt.EditableIds.Where(id => id != steps.Id).ToHashSet();
        var response = $">>> EDIT action=replace target={steps.Id}\nnew content\n<<< END";

        var result = DocumentEditOrchestrator.TryApplyPatch(response, prompt.Chunks, editableWithoutSteps);

        Assert.IsNull(result);
    }
}
