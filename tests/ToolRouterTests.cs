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
}
