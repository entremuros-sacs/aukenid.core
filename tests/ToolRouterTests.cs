using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests;

public sealed class ToolRouterTests
{
    [Fact]
    public void HardSignals_DoiSelectsScholar()
    {
        var plan = ToolRouter.HardSignals("See 10.1038/nature12373 for the method.");
        Assert.True(plan.Scholar);
        Assert.False(plan.Web);
        Assert.False(plan.Wiki);
    }

    [Fact]
    public void HardSignals_UrlSelectsWeb()
    {
        var plan = ToolRouter.HardSignals("Summarize https://example.com/article");
        Assert.True(plan.Web);
        Assert.False(plan.Scholar);
        Assert.False(plan.Wiki);
    }

    [Fact]
    public void HardSignals_IgnoresNaturalLanguage()
    {
        var plan = ToolRouter.HardSignals("Dame una lista de variantes de modelos AI Gemma");
        Assert.False(plan.Any);
    }

    [Fact]
    public void ParseModelPlan_ReadsJsonEvenWithProse()
    {
        var plan = ToolRouter.ParseModelPlan("Sure.\n{\"web\":true,\"wiki\":false,\"scholar\":false}\n");
        Assert.True(plan.Web);
        Assert.False(plan.Wiki);
        Assert.False(plan.Scholar);
        Assert.Equal("searching.web", plan.StatusKey);
    }

    [Fact]
    public void Merge_UnionsHardAndModelThenCapsAtTwo()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: true, Web: false);
        var model = new ToolRouter.Plan(Wiki: true, Scholar: false, Web: true);
        var plan = ToolRouter.Merge(hard, model);
        Assert.True(plan.Scholar);
        Assert.True(plan.Wiki);
        Assert.False(plan.Web);
    }

    [Fact]
    public void PlanForTurn_SkipsToolsWhenTheTurnHasAttachments()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: true);
        var suggested = new ToolRouter.Plan(Wiki: true, Scholar: false, Web: true);
        var plan = ToolRouter.PlanForTurn("Add more phrases in other languages please", hard, suggested, hasAttachments: true);
        Assert.False(plan.Any);
    }

    [Fact]
    public void PlanForTurn_WithAttachments_StillRoutesDocumentButSkipsFolder()
    {
        var hard = default(ToolRouter.Plan);
        var suggested = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Document: true);
        var plan = ToolRouter.PlanForTurn("put this file in the document panel", hard, suggested, hasAttachments: true);
        Assert.True(plan.Document);
        Assert.False(plan.Any);
        Assert.False(plan.Folder);
    }

    [Fact]
    public void PlanForTurn_WithAttachments_CarriesDocumentVerbatimThrough()
    {
        var hard = default(ToolRouter.Plan);
        var suggested = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Document: true, DocumentVerbatim: true);
        var plan = ToolRouter.PlanForTurn("put this file in the document panel as-is", hard, suggested, hasAttachments: true);
        Assert.True(plan.DocumentVerbatim);
    }

    [Fact]
    public void PlanForTurn_StillRoutesWhenThereAreNoAttachments()
    {
        var empty = default(ToolRouter.Plan);
        var plan = ToolRouter.PlanForTurn("Dame una lista de variantes de modelos AI Gemma", empty, empty, hasAttachments: false);
        Assert.True(plan.Web);
    }

    [Fact]
    public void WithDefaultWeb_UsesNamedThingsAndSkipsAnOrdinarySentence()
    {
        var empty = default(ToolRouter.Plan);
        var named = ToolRouter.WithDefaultWeb(empty, "Dame una lista de variantes de modelos AI Gemma");
        Assert.True(named.Web);
        Assert.Equal("AI Gemma", named.Query);
        Assert.False(ToolRouter.WithDefaultWeb(empty, "Thanks").Any);
        Assert.False(ToolRouter.WithDefaultWeb(empty, "Explain the split functionality in relationship with the context meter").Any);
    }

    [Fact]
    public void PlanForTurn_DoesNotSearchTheAppQuestionAsWebKeywords()
    {
        var empty = default(ToolRouter.Plan);
        var asked = "Explain the split functionality in relationship with the context meter";
        var plan = ToolRouter.PlanForTurn(asked, empty, empty, hasAttachments: false);
        Assert.False(plan.Web);
        Assert.Null(plan.Query);
    }

    [Fact]
    public void ParseModelPlan_ReadsDocumentFlag()
    {
        var plan = ToolRouter.ParseModelPlan("{\"web\":false,\"wiki\":false,\"scholar\":false,\"document\":true}");
        Assert.True(plan.Document);
        Assert.False(plan.Any);
    }

    [Fact]
    public void ParseModelPlan_ReadsDocumentVerbatimFlag()
    {
        var plan = ToolRouter.ParseModelPlan("{\"web\":false,\"wiki\":false,\"scholar\":false,\"document\":true,\"documentVerbatim\":true}");
        Assert.True(plan.DocumentVerbatim);
    }

    [Fact]
    public void Merge_PreservesDocumentFromEitherSide()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var model = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Document: true);
        var plan = ToolRouter.Merge(hard, model);
        Assert.True(plan.Document);
    }

    [Fact]
    public void WithDefaultWeb_PreservesDocumentWhenFallingBackToWebLookup()
    {
        var withDocument = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Document: true, Explicit: true);
        var result = ToolRouter.WithDefaultWeb(withDocument, "Dame una lista de variantes de modelos AI Gemma");
        Assert.True(result.Document);
    }

    [Fact]
    public void PlanForTurn_DocumentSurvivesTheFullPipeline()
    {
        var hard = default(ToolRouter.Plan);
        var suggested = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Document: true, Explicit: true);
        var plan = ToolRouter.PlanForTurn("agrega ese contenido al panel de edición", hard, suggested, hasAttachments: false);
        Assert.True(plan.Document);
        Assert.False(plan.Any);
    }

    [Fact]
    public void PlanForTurn_KeepsAnExplicitRefusal()
    {
        var empty = default(ToolRouter.Plan);
        var declined = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false}""");
        var plan = ToolRouter.PlanForTurn(
            "Dame una lista de variantes de modelos AI Gemma",
            empty,
            declined,
            hasAttachments: false);
        Assert.False(plan.Any);
    }

    [Fact]
    public void PlanForTurn_DropsWebWhenTheModelCopiesTheSentence()
    {
        var empty = default(ToolRouter.Plan);
        var asked = "Explain the split functionality in relationship with the context meter";
        var copied = ToolRouter.ParseModelPlan($$"""{"web":true,"wiki":false,"scholar":false,"q":"{{asked}}"}""");
        var plan = ToolRouter.PlanForTurn(asked, empty, copied, hasAttachments: false);
        Assert.False(plan.Web);
    }

    [Fact]
    public void PlanForTurn_UsesAShortModelQuery()
    {
        var empty = default(ToolRouter.Plan);
        var suggested = ToolRouter.ParseModelPlan("""{"web":true,"wiki":false,"scholar":false,"q":"Gemma 3 release"}""");
        var plan = ToolRouter.PlanForTurn("When did that model come out?", empty, suggested, hasAttachments: false);
        Assert.True(plan.Web);
        Assert.Equal("Gemma 3 release", plan.Query);
    }

    [Fact]
    public void ParseModelPlan_EmptyOrInvalid_IsNone()
    {
        Assert.False(ToolRouter.ParseModelPlan(null).Any);
        Assert.False(ToolRouter.ParseModelPlan("no tools").Any);
    }

    [Fact]
    public void ParseModelPlan_DefaultsFolderToTrueWhenFieldIsMissing()
    {
        var plan = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false}""");
        Assert.True(plan.Folder);
    }

    [Fact]
    public void ParseModelPlan_ReadsAnExplicitFolderFalse()
    {
        var plan = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"folder":false}""");
        Assert.False(plan.Folder);
    }

    [Fact]
    public void ParseModelPlan_DefaultsFolderToTrueOnUnparseableJson()
    {
        Assert.True(ToolRouter.ParseModelPlan(null).Folder);
        Assert.True(ToolRouter.ParseModelPlan("no tools").Folder);
    }

    [Fact]
    public void Merge_CarriesTheModelsFolderDecisionNotTheHardSignalDefault()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var model = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"folder":false}""");
        var merged = ToolRouter.Merge(hard, model);
        Assert.False(merged.Folder);
    }

    [Fact]
    public void PlanForTurn_DropsFolderContextWhenTheTurnHasAttachments()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var suggested = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"folder":true}""");
        var plan = ToolRouter.PlanForTurn("Summarize the attached file", hard, suggested, hasAttachments: true);
        Assert.False(plan.Folder);
    }

    [Fact]
    public void PlanForTurn_KeepsTheModelsFolderFalseThroughDefaultWebAndBindQuery()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var suggested = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"folder":false}""");
        var plan = ToolRouter.PlanForTurn("Dame una lista de variantes de modelos AI Gemma", hard, suggested, hasAttachments: false);
        Assert.False(plan.Folder);
    }

    [Fact]
    public void WithDefaultWeb_PreservesFolderWhenFallingBackToTheNamedLookup()
    {
        // Explicit=false + Any=false is what hits WithDefaultWeb's named-lookup fallback branch.
        var plan = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Folder: false);
        var result = ToolRouter.WithDefaultWeb(plan, "Dame una lista de variantes de modelos AI Gemma");
        Assert.True(result.Web);
        Assert.False(result.Folder);
    }

    [Fact]
    public void ParseModelPlan_DefaultsNewTopicToFalseWhenFieldIsMissing()
    {
        var plan = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false}""");
        Assert.False(plan.NewTopic);
    }

    [Fact]
    public void ParseModelPlan_ReadsAnExplicitNewTopicTrue()
    {
        var plan = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"newTopic":true}""");
        Assert.True(plan.NewTopic);
    }

    [Fact]
    public void ParseModelPlan_DefaultsNewTopicToFalseOnUnparseableJson()
    {
        Assert.False(ToolRouter.ParseModelPlan(null).NewTopic);
        Assert.False(ToolRouter.ParseModelPlan("no tools").NewTopic);
    }

    [Fact]
    public void Merge_CarriesTheModelsNewTopicDecision()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var model = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"newTopic":true}""");
        var merged = ToolRouter.Merge(hard, model);
        Assert.True(merged.NewTopic);
    }

    [Fact]
    public void PlanForTurn_DropsNewTopicSuggestionWhenTheTurnHasAttachments()
    {
        var hard = new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false);
        var suggested = ToolRouter.ParseModelPlan("""{"web":false,"wiki":false,"scholar":false,"newTopic":true}""");
        var plan = ToolRouter.PlanForTurn("Summarize the attached file", hard, suggested, hasAttachments: true);
        Assert.False(plan.NewTopic);
    }

    [Fact]
    public void LooksLikeNewTopic_TrueWhenNoWordsAreShared()
    {
        var history = new[] { new ChatTurn("user", "Tell me something about the Jev AI model") };
        var result = ToolRouter.LooksLikeNewTopic(history, "What services does Azure offer for a cloud aggregation service");
        Assert.True(result);
    }

    [Fact]
    public void LooksLikeNewTopic_FalseWhenWordsOverlap()
    {
        var history = new[] { new ChatTurn("user", "Tell me something about the Jev AI model") };
        var result = ToolRouter.LooksLikeNewTopic(history, "What else can you tell me about that Jev model");
        Assert.False(result);
    }

    [Fact]
    public void LooksLikeNewTopic_FalseWithNoHistory()
    {
        Assert.False(ToolRouter.LooksLikeNewTopic([], "What services does Azure offer for a cloud aggregation service"));
    }

    [Fact]
    public void LooksLikeNewTopic_FalseWhenThePromptHasTooFewSignificantWords()
    {
        var history = new[] { new ChatTurn("user", "Tell me something about the Jev AI model") };
        Assert.False(ToolRouter.LooksLikeNewTopic(history, "Thanks a lot"));
    }

    [Fact]
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
        Assert.True(plan.NewTopic);
    }
}
