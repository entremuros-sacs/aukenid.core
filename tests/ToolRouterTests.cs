using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class ToolRouterTests
{
    [TestMethod]
    public void HardSignals_DoiSelectsScholar()
    {
        var plan = ToolRouter.HardSignals("See 10.1038/nature12373 for the method.");
        Assert.IsTrue(plan.Scholar);
        Assert.IsFalse(plan.Web);
        Assert.IsFalse(plan.Wiki);
    }

    [TestMethod]
    public void HardSignals_UrlSelectsWeb()
    {
        var plan = ToolRouter.HardSignals("Summarize https://example.com/article");
        Assert.IsTrue(plan.Web);
        Assert.IsFalse(plan.Scholar);
        Assert.IsFalse(plan.Wiki);
    }

    [TestMethod]
    public void HardSignals_IgnoresNaturalLanguage()
    {
        var plan = ToolRouter.HardSignals("Dame una lista de variantes de modelos AI Gemma");
        Assert.IsFalse(plan.Any);
    }

    [TestMethod]
    public void ParseModelPlan_ReadsJsonEvenWithProse()
    {
        var plan = ToolRouter.ParseModelPlan("Sure.\n{\"web\":true,\"wiki\":false,\"scholar\":false}\n");
        Assert.IsTrue(plan.Web);
        Assert.IsFalse(plan.Wiki);
        Assert.IsFalse(plan.Scholar);
        Assert.AreEqual("searching.web", plan.StatusKey);
    }

    [TestMethod]
    public void Merge_UnionsHardAndModelThenCapsAtTwo()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: true, Web: false);
        var model = new ToolRouter.Plan(Wiki: true, Scholar: false, Web: true);
        var plan = ToolRouter.Merge(hard, model);
        Assert.IsTrue(plan.Scholar);
        Assert.IsTrue(plan.Wiki);
        Assert.IsFalse(plan.Web);
    }

    [TestMethod]
    public void PlanForTurn_SkipsToolsWhenTheTurnHasAttachments()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: true);
        var suggested = new ToolRouter.Plan(Wiki: true, Scholar: false, Web: true);
        var plan = ToolRouter.PlanForTurn("Add more phrases in other languages please", hard, suggested, hasAttachments: true);
        Assert.IsFalse(plan.Any);
    }

    [TestMethod]
    public void PlanForTurn_WithAttachments_StillRoutesDocumentButSkipsFolder()
    {
        var hard = default(ToolRouter.Plan);
        var suggested = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Document: true);
        var plan = ToolRouter.PlanForTurn("put this file in the document panel", hard, suggested, hasAttachments: true);
        Assert.IsTrue(plan.Document);
        Assert.IsFalse(plan.Any);
        Assert.IsFalse(plan.Folder);
    }

    [TestMethod]
    public void PlanForTurn_WithAttachments_CarriesDocumentVerbatimThrough()
    {
        var hard = default(ToolRouter.Plan);
        var suggested = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Document: true, DocumentVerbatim: true);
        var plan = ToolRouter.PlanForTurn("put this file in the document panel as-is", hard, suggested, hasAttachments: true);
        Assert.IsTrue(plan.DocumentVerbatim);
    }

    [TestMethod]
    public void PlanForTurn_StillRoutesWhenThereAreNoAttachments()
    {
        var empty = default(ToolRouter.Plan);
        var plan = ToolRouter.PlanForTurn("Dame una lista de variantes de modelos AI Gemma", empty, empty, hasAttachments: false);
        Assert.IsTrue(plan.Web);
    }

    [TestMethod]
    public void WithDefaultWeb_UsesNamedThingsAndSkipsAnOrdinarySentence()
    {
        var empty = default(ToolRouter.Plan);
        var named = ToolRouter.WithDefaultWeb(empty, "Dame una lista de variantes de modelos AI Gemma");
        Assert.IsTrue(named.Web);
        Assert.AreEqual("AI Gemma", named.Query);
        Assert.IsFalse(ToolRouter.WithDefaultWeb(empty, "Thanks").Any);
        Assert.IsFalse(ToolRouter.WithDefaultWeb(empty, "Explain the split functionality in relationship with the context meter").Any);
    }

    [TestMethod]
    public void PlanForTurn_DoesNotSearchTheAppQuestionAsWebKeywords()
    {
        var empty = default(ToolRouter.Plan);
        var asked = "Explain the split functionality in relationship with the context meter";
        var plan = ToolRouter.PlanForTurn(asked, empty, empty, hasAttachments: false);
        Assert.IsFalse(plan.Web);
        Assert.IsNull(plan.Query);
    }

    [TestMethod]
    public void ParseModelPlan_ReadsDocumentFlag()
    {
        var plan = ToolRouter.ParseModelPlan("{\"web\":false,\"wiki\":false,\"scholar\":false,\"document\":true}");
        Assert.IsTrue(plan.Document);
        Assert.IsFalse(plan.Any);
    }

    [TestMethod]
    public void ParseModelPlan_ReadsDocumentVerbatimFlag()
    {
        var plan = ToolRouter.ParseModelPlan("{\"web\":false,\"wiki\":false,\"scholar\":false,\"document\":true,\"documentVerbatim\":true}");
        Assert.IsTrue(plan.DocumentVerbatim);
    }

    [TestMethod]
    public void Merge_PreservesDocumentFromEitherSide()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var model = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Document: true);
        var plan = ToolRouter.Merge(hard, model);
        Assert.IsTrue(plan.Document);
    }

    [TestMethod]
    public void WithDefaultWeb_PreservesDocumentWhenFallingBackToWebLookup()
    {
        var withDocument = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Document: true, Explicit: true);
        var result = ToolRouter.WithDefaultWeb(withDocument, "Dame una lista de variantes de modelos AI Gemma");
        Assert.IsTrue(result.Document);
    }

    [TestMethod]
    public void PlanForTurn_DocumentSurvivesTheFullPipeline()
    {
        var hard = default(ToolRouter.Plan);
        var suggested = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Document: true, Explicit: true);
        var plan = ToolRouter.PlanForTurn("agrega ese contenido al panel de edición", hard, suggested, hasAttachments: false);
        Assert.IsTrue(plan.Document);
        Assert.IsFalse(plan.Any);
    }

    [TestMethod]
    public void PlanForTurn_KeepsAnExplicitRefusal()
    {
        var empty = default(ToolRouter.Plan);
        var declined = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false}""");
        var plan = ToolRouter.PlanForTurn(
            "Dame una lista de variantes de modelos AI Gemma",
            empty,
            declined,
            hasAttachments: false);
        Assert.IsFalse(plan.Any);
    }

    [TestMethod]
    public void PlanForTurn_DropsWebWhenTheModelCopiesTheSentence()
    {
        var empty = default(ToolRouter.Plan);
        var asked = "Explain the split functionality in relationship with the context meter";
        var copied = ToolRouter.ParseModelPlan($$"""{"web":true,"wiki":false,"scholar":false,"q":"{{asked}}"}""");
        var plan = ToolRouter.PlanForTurn(asked, empty, copied, hasAttachments: false);
        Assert.IsFalse(plan.Web);
    }

    [TestMethod]
    public void PlanForTurn_UsesAShortModelQuery()
    {
        var empty = default(ToolRouter.Plan);
        var suggested = ToolRouter.ParseModelPlan("""{"web":true,"wiki":false,"scholar":false,"q":"Gemma 3 release"}""");
        var plan = ToolRouter.PlanForTurn("When did that model come out?", empty, suggested, hasAttachments: false);
        Assert.IsTrue(plan.Web);
        Assert.AreEqual("Gemma 3 release", plan.Query);
    }

    [TestMethod]
    public void ParseModelPlan_EmptyOrInvalid_IsNone()
    {
        Assert.IsFalse(ToolRouter.ParseModelPlan(null).Any);
        Assert.IsFalse(ToolRouter.ParseModelPlan("no tools").Any);
    }

    [TestMethod]
    public void ParseModelPlan_DefaultsFolderToTrueWhenFieldIsMissing()
    {
        var plan = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false}""");
        Assert.IsTrue(plan.Folder);
    }

    [TestMethod]
    public void ParseModelPlan_ReadsAnExplicitFolderFalse()
    {
        var plan = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"folder":false}""");
        Assert.IsFalse(plan.Folder);
    }

    [TestMethod]
    public void ParseModelPlan_DefaultsFolderToTrueOnUnparseableJson()
    {
        Assert.IsTrue(ToolRouter.ParseModelPlan(null).Folder);
        Assert.IsTrue(ToolRouter.ParseModelPlan("no tools").Folder);
    }

    [TestMethod]
    public void Merge_CarriesTheModelsFolderDecisionNotTheHardSignalDefault()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var model = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"folder":false}""");
        var merged = ToolRouter.Merge(hard, model);
        Assert.IsFalse(merged.Folder);
    }

    [TestMethod]
    public void PlanForTurn_DropsFolderContextWhenTheTurnHasAttachments()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var suggested = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"folder":true}""");
        var plan = ToolRouter.PlanForTurn("Summarize the attached file", hard, suggested, hasAttachments: true);
        Assert.IsFalse(plan.Folder);
    }

    [TestMethod]
    public void PlanForTurn_KeepsTheModelsFolderFalseThroughDefaultWebAndBindQuery()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var suggested = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"folder":false}""");
        var plan = ToolRouter.PlanForTurn("Dame una lista de variantes de modelos AI Gemma", hard, suggested, hasAttachments: false);
        Assert.IsFalse(plan.Folder);
    }

    [TestMethod]
    public void WithDefaultWeb_PreservesFolderWhenFallingBackToTheNamedLookup()
    {
        // Explicit=false + Any=false is what hits WithDefaultWeb's named-lookup fallback branch.
        var plan = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Folder: false);
        var result = ToolRouter.WithDefaultWeb(plan, "Dame una lista de variantes de modelos AI Gemma");
        Assert.IsTrue(result.Web);
        Assert.IsFalse(result.Folder);
    }

    [TestMethod]
    public void ParseModelPlan_DefaultsNewTopicToFalseWhenFieldIsMissing()
    {
        var plan = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false}""");
        Assert.IsFalse(plan.NewTopic);
    }

    [TestMethod]
    public void ParseModelPlan_ReadsAnExplicitNewTopicTrue()
    {
        var plan = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"newTopic":true}""");
        Assert.IsTrue(plan.NewTopic);
    }

    [TestMethod]
    public void ParseModelPlan_DefaultsNewTopicToFalseOnUnparseableJson()
    {
        Assert.IsFalse(ToolRouter.ParseModelPlan(null).NewTopic);
        Assert.IsFalse(ToolRouter.ParseModelPlan("no tools").NewTopic);
    }

    [TestMethod]
    public void Merge_CarriesTheModelsNewTopicDecision()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var model = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"newTopic":true}""");
        var merged = ToolRouter.Merge(hard, model);
        Assert.IsTrue(merged.NewTopic);
    }

    [TestMethod]
    public void PlanForTurn_DropsNewTopicSuggestionWhenTheTurnHasAttachments()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var suggested = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"newTopic":true}""");
        var plan = ToolRouter.PlanForTurn("Summarize the attached file", hard, suggested, hasAttachments: true);
        Assert.IsFalse(plan.NewTopic);
    }

    [TestMethod]
    public void LooksLikeNewTopic_TrueWhenNoWordsAreShared()
    {
        var history = new[] { new ChatTurn("user", "Tell me something about the Jev AI model") };
        var result = ToolRouter.LooksLikeNewTopic(history, "What services does Azure offer for a cloud aggregation service");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void LooksLikeNewTopic_FalseWhenWordsOverlap()
    {
        var history = new[] { new ChatTurn("user", "Tell me something about the Jev AI model") };
        var result = ToolRouter.LooksLikeNewTopic(history, "What else can you tell me about that Jev model");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void LooksLikeNewTopic_FalseWithNoHistory()
    {
        Assert.IsFalse(ToolRouter.LooksLikeNewTopic([], "What services does Azure offer for a cloud aggregation service"));
    }

    [TestMethod]
    public void LooksLikeNewTopic_FalseWhenThePromptHasTooFewSignificantWords()
    {
        var history = new[] { new ChatTurn("user", "Tell me something about the Jev AI model") };
        Assert.IsFalse(ToolRouter.LooksLikeNewTopic(history, "Thanks a lot"));
    }

    [TestMethod]
    public void PlanForTurn_SetsNewTopicFromTheWordOverlapBackstopWhenTheModelMissesIt()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var suggested = ToolRouter.ParseModelPlan("""{"web":true,"wiki":false,"scholar":false,"newTopic":false}""");
        var history = new[] { new ChatTurn("user", "Tell me something about the Jev AI model") };
        var plan = ToolRouter.PlanForTurn(
            "What services does Azure offer for a cloud aggregation service",
            hard,
            suggested,
            hasAttachments: false,
            history);
        Assert.IsTrue(plan.NewTopic);
    }
}
