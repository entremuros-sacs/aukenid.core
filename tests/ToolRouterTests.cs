using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class ToolRouterTests
{
    [TestMethod]
    public void ParseIntentDetection_DefaultsOnInvalidInput()
    {
        var parsed = ToolRouter.ParseIntentDetection("not json");
        Assert.IsTrue(parsed.Folder);
        Assert.IsFalse(parsed.NewTopic);
        Assert.AreEqual(ToolRouter.IntentKind.None, parsed.Intents);
    }

    [TestMethod]
    public void ParseIntentDetection_ReadsSchemaAndFields()
    {
        var raw = """
        {
          "schema":"aukenid.intent.v1",
          "intents":["document","search.web"],
          "q":"Aukenid",
          "documentVerbatim":false,
          "folder":false,
          "newTopic":true,
          "needClarification":false,
          "clarificationQuestion":""
        }
        """;

        var parsed = ToolRouter.ParseIntentDetection(raw);
        Assert.IsTrue(parsed.Explicit);
        Assert.IsTrue(parsed.HasIntent(ToolRouter.IntentKind.Document));
        Assert.IsTrue(parsed.HasIntent(ToolRouter.IntentKind.SearchWeb));
        Assert.AreEqual("Aukenid", parsed.Query);
        Assert.IsFalse(parsed.Folder);
        Assert.IsTrue(parsed.NewTopic);
        Assert.IsFalse(parsed.DocumentVerbatim);
    }

    [TestMethod]
    public void PlanForTurn_NeedClarification_BecomesClarifyInChatStep()
    {
        var raw = """
        {
          "schema":"aukenid.intent.v1",
          "intents":["document"],
          "q":"",
          "documentVerbatim":false,
          "folder":true,
          "newTopic":false,
          "needClarification":true,
          "clarificationQuestion":"What should I put in the document panel?"
        }
        """;

        var detection = ToolRouter.ParseIntentDetection(raw);
        var plan = ToolRouter.PlanForTurn("pon eso en el panel", detection, hasAttachments: false);
        Assert.IsTrue(plan.ClarifyInChat);
        Assert.AreEqual("What should I put in the document panel?", plan.ClarificationQuestion);
    }

    [TestMethod]
    public void PlanForTurn_UrlImpliesWebSearchEvenIfClassifierIsEmpty()
    {
        var detection = ToolRouter.IntentDetection.Default;
        var plan = ToolRouter.PlanForTurn("Summarize https://example.com/article", detection, hasAttachments: false);
        Assert.IsTrue(plan.Web);
        Assert.IsTrue(plan.HasIntent(ToolRouter.IntentKind.SearchWeb));
        Assert.AreEqual("https://example.com/article", plan.Query);
    }

    [TestMethod]
    public void PlanForTurn_DoiImpliesScholarEvenIfClassifierIsEmpty()
    {
        var detection = ToolRouter.IntentDetection.Default;
        var plan = ToolRouter.PlanForTurn("See 10.1038/nature12373 for the method.", detection, hasAttachments: false);
        Assert.IsTrue(plan.Scholar);
        Assert.IsTrue(plan.HasIntent(ToolRouter.IntentKind.SearchScholar));
    }

    [TestMethod]
    public void PlanForTurn_AttachmentsDisableRetrievalButKeepDocument()
    {
        var detection = new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Document | ToolRouter.IntentKind.SearchWeb,
            Query: "Aukenid",
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: true,
            Explicit: true,
            ClarificationQuestion: null);

        var plan = ToolRouter.PlanForTurn("put this in the document panel", detection, hasAttachments: true);
        Assert.IsTrue(plan.Document);
        Assert.IsFalse(plan.Web);
        Assert.IsFalse(plan.Wiki);
        Assert.IsFalse(plan.Scholar);
        Assert.IsFalse(plan.Folder);
        Assert.IsTrue(plan.DocumentVerbatim);
    }

    [TestMethod]
    public void PlanForTurn_DocumentOnly_DoesNotAddChatUnlessExplicitlyRequested()
    {
        var detection = new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Document,
            Query: null,
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: false,
            Explicit: true,
            ClarificationQuestion: null);

        var plan = ToolRouter.PlanForTurn("in the document panel, write a short intro", detection, hasAttachments: false);
        Assert.IsTrue(plan.HasIntent(ToolRouter.IntentKind.Document));
        Assert.IsFalse(plan.HasIntent(ToolRouter.IntentKind.Chat));
    }

    [TestMethod]
    public void PlanForTurn_DocumentPlusAnswerInChat_AddsChatIntent()
    {
        var detection = new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Document | ToolRouter.IntentKind.Chat,
            Query: null,
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: false,
            Explicit: true,
            ClarificationQuestion: null);

        var plan = ToolRouter.PlanForTurn("in the document panel, write a short intro. also answer in chat.", detection, hasAttachments: false);
        Assert.IsTrue(plan.HasIntent(ToolRouter.IntentKind.Document));
        Assert.IsTrue(plan.HasIntent(ToolRouter.IntentKind.Chat));
    }

    [TestMethod]
    public void PlanForTurn_ConsumerCuriosity_DefaultsToChatPlusWebSearch()
    {
        var detection = new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Chat | ToolRouter.IntentKind.SearchWeb,
            Query: "Aukenid",
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: false,
            Explicit: true,
            ClarificationQuestion: null);

        var plan = ToolRouter.PlanForTurn("qué sabes de Aukenid?", detection, hasAttachments: false);
        Assert.IsTrue(plan.Web);
        Assert.IsTrue(plan.HasIntent(ToolRouter.IntentKind.Chat));
        Assert.IsTrue(plan.HasIntent(ToolRouter.IntentKind.SearchWeb));
    }

    [TestMethod]
    public void PlanForTurn_ClarificationAnswer_DoesNotTriggerNewTopicAndKeepsDocumentWorkflow()
    {
        var detection = ToolRouter.IntentDetection.Default;
        var history = new[] { new ChatTurn("assistant", "What should I put in the document panel?") };

        var plan = ToolRouter.PlanForTurn("Un memorándum interno sobre el roadmap.", detection, hasAttachments: false, history);

        Assert.IsFalse(plan.NewTopic);
        Assert.IsTrue(plan.HasIntent(ToolRouter.IntentKind.Chat));
    }
}

