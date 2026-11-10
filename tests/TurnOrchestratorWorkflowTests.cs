using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Contracts;
using Aukenid.Core.Engine;
using Aukenid.Core.Services;
using Aukenid.Core.Tests.TestDoubles;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class TurnOrchestratorWorkflowTests
{
    [TestMethod]
    public async Task ExecuteAsync_WhenChatReplyIsAQuestion_SkipsDocumentGenerationButKeepsPanelOpen()
    {
        using var workspace = new WorkflowWorkspace();
        var thread = workspace.Store.CreateThread("t", Aukenid.Core.Data.ConversationStore.GeneralFolder);

        var parentId = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid().ToString("N");
        var assistantId = Guid.NewGuid().ToString("N");

        workspace.Store.SaveMessage(new MessageDto(parentId, thread.Id, null, "Aukenid", "¿Sobre qué tema quieres escribir el documento?", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(userId, thread.Id, parentId, "user", "Un memorándum", DateTimeOffset.UtcNow));

        var engine = new FakeChatEngine();
        engine.EnqueueDetection(new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Document | ToolRouter.IntentKind.Chat,
            Query: null,
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: false,
            Explicit: true,
            ClarificationQuestion: null));

        engine.EnqueueChatReply("¿Cuál es el horario exacto y desde cuándo aplica?");
        engine.EnqueueDocumentReply("# SHOULD NOT BE GENERATED");

        var orchestrator = BuildOrchestrator(workspace, engine);
        var events = new RecordingTurnEventSink();

        var input = new TurnOrchestrator.TurnRequest(
            ThreadId: thread.Id,
            UserMessageId: userId,
            AssistantMessageId: assistantId,
            ParentId: parentId,
            Content: "Un memorándum",
            IsFirstMessage: false,
            AlreadyHaveUserMessage: true,
            DocumentPanelOpen: true,
            ExistingMessages: workspace.Store.ListMessages(thread.Id),
            StoredAttachmentNames: Array.Empty<string>());

        var result = await orchestrator.ExecuteAsync(input, events, tryBuildKnowledgeTurnAsync: null, CancellationToken.None);

        Assert.IsTrue(result.Plan.Document);
        Assert.AreEqual("¿Cuál es el horario exacto y desde cuándo aplica?", result.Content);
        Assert.AreEqual(string.Empty, workspace.Store.ReadDocument(thread.Id));
        Assert.AreEqual(0, engine.DocumentCalls);
        Assert.IsTrue(events.Events.OfType<DocumentOpenEvent>().Any());
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenModelDumpsDocumentIntoChat_ReplacesChatWithShortConfirmation()
    {
        using var workspace = new WorkflowWorkspace();
        var thread = workspace.Store.CreateThread("t", Aukenid.Core.Data.ConversationStore.GeneralFolder);

        var parentId = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid().ToString("N");
        var assistantId = Guid.NewGuid().ToString("N");

        workspace.Store.SaveMessage(new MessageDto(parentId, thread.Id, null, "Aukenid", "Ok.", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(userId, thread.Id, parentId, "user", "Redacta el memorándum.", DateTimeOffset.UtcNow));

        var engine = new FakeChatEngine();
        engine.EnqueueDetection(new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Document | ToolRouter.IntentKind.Chat,
            Query: null,
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: false,
            Explicit: true,
            ClarificationQuestion: null));

        var badChat = string.Join("\n", new[]
        {
            "# Internal memo",
            "",
            "## Purpose",
            "Reinforce working hours and punctuality.",
            "",
            "## Key points",
            "- Working hours: 8:00 a.m. to 5:00 p.m.",
            "- Coordinate with your team lead for exceptions.",
            "- Contact HR for questions.",
            "",
            "## Details",
            "This memo applies to all staff and contractors.",
            "Please acknowledge receipt of this memo by end of day.",
            "",
            "Regards,",
            "Jaime",
        });

        engine.EnqueueChatReply(badChat);
        engine.EnqueueDocumentReply("# Memorando\n\nEste es el contenido real en el panel.\n\n- Punto 1: Horario 8:00 a.m. a 5:00 p.m.\n- Punto 2: Consultas con RR.HH.\n\nGracias por su comprensión y compromiso.\n");

        var orchestrator = BuildOrchestrator(workspace, engine);
        var events = new RecordingTurnEventSink();

        var input = new TurnOrchestrator.TurnRequest(
            ThreadId: thread.Id,
            UserMessageId: userId,
            AssistantMessageId: assistantId,
            ParentId: parentId,
            Content: "Draft the memo.",
            IsFirstMessage: false,
            AlreadyHaveUserMessage: true,
            DocumentPanelOpen: true,
            ExistingMessages: workspace.Store.ListMessages(thread.Id),
            StoredAttachmentNames: Array.Empty<string>());

        var result = await orchestrator.ExecuteAsync(input, events, tryBuildKnowledgeTurnAsync: null, CancellationToken.None);

        Assert.IsTrue(result.Plan.Document);
        Assert.IsFalse(result.Content.Contains("# Internal memo", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(result.Content.Length < 200);
        Assert.IsTrue((workspace.Store.ReadDocument(thread.Id) ?? string.Empty).Contains("# Memorando", StringComparison.Ordinal));
        Assert.IsTrue(events.Events.OfType<DocumentOpenEvent>().Any());

        var tokenEvents = events.Events.OfType<TokenEvent>().ToList();
        Assert.AreEqual(1, tokenEvents.Count);
        Assert.IsFalse(tokenEvents[0].Text.Contains("# Internal memo", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenModelKeepsAskingClarifications_ProceedsToDraftAfterTwoQuestions()
    {
        using var workspace = new WorkflowWorkspace();
        var thread = workspace.Store.CreateThread("t", Aukenid.Core.Data.ConversationStore.GeneralFolder);

        var q1 = Guid.NewGuid().ToString("N");
        var a1 = Guid.NewGuid().ToString("N");
        var q2 = Guid.NewGuid().ToString("N");
        var a2 = Guid.NewGuid().ToString("N");
        var parentId = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid().ToString("N");
        var assistantId = Guid.NewGuid().ToString("N");

        workspace.Store.SaveMessage(new MessageDto(q1, thread.Id, null, "Aukenid", "What is the document topic?", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(a1, thread.Id, q1, "user", "A memo for staff.", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(q2, thread.Id, a1, "Aukenid", "What is the purpose?", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(a2, thread.Id, q2, "user", "Working hours compliance.", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(parentId, thread.Id, a2, "Aukenid", "What kind of deductions?", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(userId, thread.Id, parentId, "user", "Please write the document now.", DateTimeOffset.UtcNow));

        var engine = new FakeChatEngine();
        engine.EnqueueDetection(new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Document | ToolRouter.IntentKind.NeedClarification,
            Query: null,
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: false,
            Explicit: true,
            ClarificationQuestion: "What is the document topic?"));

        engine.EnqueueChatReply("IGNORED");
        engine.EnqueueDocumentReply("# Memo\n\nThis is the draft with enough body text to pass the minimum length threshold in DocumentTool.Apply.\n\n- Hours: 8am to 5pm\n- Policy: Deductions may apply\n\nPlease comply.\n");

        var orchestrator = BuildOrchestrator(workspace, engine);
        var events = new RecordingTurnEventSink();

        var input = new TurnOrchestrator.TurnRequest(
            ThreadId: thread.Id,
            UserMessageId: userId,
            AssistantMessageId: assistantId,
            ParentId: parentId,
            Content: "Please write the document now.",
            IsFirstMessage: false,
            AlreadyHaveUserMessage: true,
            DocumentPanelOpen: true,
            ExistingMessages: workspace.Store.ListMessages(thread.Id),
            StoredAttachmentNames: Array.Empty<string>());

        var result = await orchestrator.ExecuteAsync(input, events, tryBuildKnowledgeTurnAsync: null, CancellationToken.None);

        Assert.IsTrue(result.Plan.Document);
        Assert.IsFalse(result.Plan.ClarifyInChat);
        Assert.AreEqual(1, engine.DocumentCalls);
        Assert.IsTrue((workspace.Store.ReadDocument(thread.Id) ?? string.Empty).Contains("# Memo", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenModelKeepsClarifying_AfterOneQuestion_ProceedsToDraft()
    {
        using var workspace = new WorkflowWorkspace();
        var thread = workspace.Store.CreateThread("t", Aukenid.Core.Data.ConversationStore.GeneralFolder);

        var q1 = Guid.NewGuid().ToString("N");
        var a1 = Guid.NewGuid().ToString("N");
        var parentId = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid().ToString("N");
        var assistantId = Guid.NewGuid().ToString("N");

        // One clarification already happened in this workflow.
        workspace.Store.SaveMessage(new MessageDto(q1, thread.Id, null, "Aukenid", "¿Sobre qué tema quieres redactar el documento?", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(a1, thread.Id, q1, "user", "Un memorándum para mis trabajadores.", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(parentId, thread.Id, a1, "Aukenid", "Gracias.", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(userId, thread.Id, parentId, "user", "Sí, ese memorándum.", DateTimeOffset.UtcNow));

        var engine = new FakeChatEngine();
        // Model incorrectly keeps asking a second clarification.
        engine.EnqueueDetection(new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Document | ToolRouter.IntentKind.NeedClarification,
            Query: null,
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: false,
            Explicit: true,
            ClarificationQuestion: "¿Para quién es el memorándum?"));

        engine.EnqueueChatReply("IGNORED");
        engine.EnqueueDocumentReply("# Memorandum\n\n[TODO: Asunto]\n\n[TODO: Puntos clave]\n");

        var orchestrator = BuildOrchestrator(workspace, engine);
        var events = new RecordingTurnEventSink();

        var input = new TurnOrchestrator.TurnRequest(
            ThreadId: thread.Id,
            UserMessageId: userId,
            AssistantMessageId: assistantId,
            ParentId: parentId,
            Content: "Sí, ese memorándum.",
            IsFirstMessage: false,
            AlreadyHaveUserMessage: true,
            DocumentPanelOpen: true,
            ExistingMessages: workspace.Store.ListMessages(thread.Id),
            StoredAttachmentNames: Array.Empty<string>());

        var result = await orchestrator.ExecuteAsync(input, events, tryBuildKnowledgeTurnAsync: null, CancellationToken.None);

        Assert.IsTrue(result.Plan.Document);
        Assert.IsFalse(result.Plan.ClarifyInChat);
        Assert.AreEqual(1, engine.DocumentCalls);
    }

    private static TurnOrchestrator BuildOrchestrator(WorkflowWorkspace workspace, FakeChatEngine engine)
    {
        var web = new WebEnricher(new ThrowingSearchProvider(), new WebFetchService());
        var scholar = new ScholarEnricher(new ThrowingScholarProvider());
        var wiki = new WikiEnricher(new ThrowingWikiProvider());

        return new TurnOrchestrator(
            engine,
            workspace.Store,
            workspace.Attachments,
            web,
            scholar,
            wiki,
            workspace.DocumentTool);
    }


    [TestMethod]
    public async Task ExecuteAsync_WhenExistingDocument_EditProtocolAppliesChunkPatch()
    {
        using var workspace = new WorkflowWorkspace();
        var thread = workspace.Store.CreateThread("t", Aukenid.Core.Data.ConversationStore.GeneralFolder);

        var existing = "# Memo\n\nDate: [Current date]\n\nCompany: [TODO]\nName: [TODO]\n";
        workspace.Store.SaveDocument(thread.Id, existing);

        var parentId = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid().ToString("N");
        var assistantId = Guid.NewGuid().ToString("N");

        workspace.Store.SaveMessage(new MessageDto(parentId, thread.Id, null, "Aukenid", "Ok.", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(userId, thread.Id, parentId, "user", "My company is Aukenid and my name is Jaime. Replace the date with today's date.", DateTimeOffset.UtcNow));

        var chunkId = DocumentChunker.Chunk(existing).First().Id;
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");

        var engine = new FakeChatEngine();
        engine.EnqueueDetection(new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Document,
            Query: null,
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: false,
            Explicit: true,
            ClarificationQuestion: null));

        engine.EnqueueChatReply("Updated.");
        engine.EnqueueDocumentReply($">>> EDIT action=replace target={chunkId}\n# Memo\n\nDate: {today}\n\nCompany: Aukenid\nName: Jaime\n<<< END");

        var orchestrator = BuildOrchestrator(workspace, engine);
        var events = new RecordingTurnEventSink();

        var input = new TurnOrchestrator.TurnRequest(
            ThreadId: thread.Id,
            UserMessageId: userId,
            AssistantMessageId: assistantId,
            ParentId: parentId,
            Content: "My company is Aukenid and my name is Jaime. Replace the date with today's date.",
            IsFirstMessage: false,
            AlreadyHaveUserMessage: true,
            DocumentPanelOpen: true,
            ExistingMessages: workspace.Store.ListMessages(thread.Id),
            StoredAttachmentNames: Array.Empty<string>());

        var result = await orchestrator.ExecuteAsync(input, events, tryBuildKnowledgeTurnAsync: null, CancellationToken.None);

        Assert.IsTrue(result.Plan.Document);
        Assert.AreEqual(1, engine.DocumentCalls);

        var updated = workspace.Store.ReadDocument(thread.Id) ?? string.Empty;
        Assert.IsTrue(updated.Contains("Company: Aukenid", StringComparison.Ordinal));
        Assert.IsTrue(updated.Contains("Name: Jaime", StringComparison.Ordinal));
        Assert.IsTrue(updated.Contains(today, StringComparison.Ordinal));
        Assert.IsTrue(events.Events.OfType<DocumentOpenEvent>().Any());
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenEditProtocolResponseIsInvalid_RetriesOnce()
    {
        using var workspace = new WorkflowWorkspace();
        var thread = workspace.Store.CreateThread("t", Aukenid.Core.Data.ConversationStore.GeneralFolder);

        var existing = "# Memo\n\nDate: [Current date]\n\nCompany: [TODO]\nName: [TODO]\n";
        workspace.Store.SaveDocument(thread.Id, existing);

        var parentId = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid().ToString("N");
        var assistantId = Guid.NewGuid().ToString("N");

        workspace.Store.SaveMessage(new MessageDto(parentId, thread.Id, null, "Aukenid", "Ok.", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(userId, thread.Id, parentId, "user", "Set Company to Aukenid.", DateTimeOffset.UtcNow));

        var chunkId = DocumentChunker.Chunk(existing).First().Id;

        var engine = new FakeChatEngine();
        engine.EnqueueDetection(new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Document,
            Query: null,
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: false,
            Explicit: true,
            ClarificationQuestion: null));

        engine.EnqueueChatReply("Updated.");
        engine.EnqueueDocumentReply("Sure — I'll update it now.");
        engine.EnqueueDocumentReply($">>> EDIT action=replace target={chunkId}\n# Memo\n\nDate: [Current date]\n\nCompany: Aukenid\nName: [TODO]\n<<< END");

        var orchestrator = BuildOrchestrator(workspace, engine);
        var events = new RecordingTurnEventSink();

        var input = new TurnOrchestrator.TurnRequest(
            ThreadId: thread.Id,
            UserMessageId: userId,
            AssistantMessageId: assistantId,
            ParentId: parentId,
            Content: "Set Company to Aukenid.",
            IsFirstMessage: false,
            AlreadyHaveUserMessage: true,
            DocumentPanelOpen: true,
            ExistingMessages: workspace.Store.ListMessages(thread.Id),
            StoredAttachmentNames: Array.Empty<string>());

        var result = await orchestrator.ExecuteAsync(input, events, tryBuildKnowledgeTurnAsync: null, CancellationToken.None);

        Assert.IsTrue(result.Plan.Document);
        Assert.AreEqual(2, engine.DocumentCalls);
        Assert.IsTrue((workspace.Store.ReadDocument(thread.Id) ?? string.Empty).Contains("Company: Aukenid", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenEditProtocolFailsTwice_DoesNotRegenerateOrOverwriteDocument()
    {
        using var workspace = new WorkflowWorkspace();
        var thread = workspace.Store.CreateThread("t", Aukenid.Core.Data.ConversationStore.GeneralFolder);

        var existing = "# Memo\n\nDate: [Current date]\n\nCompany: [TODO]\nName: [TODO]\n";
        workspace.Store.SaveDocument(thread.Id, existing);

        var parentId = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid().ToString("N");
        var assistantId = Guid.NewGuid().ToString("N");

        workspace.Store.SaveMessage(new MessageDto(parentId, thread.Id, null, "Aukenid", "Ok.", DateTimeOffset.UtcNow));
        workspace.Store.SaveMessage(new MessageDto(userId, thread.Id, parentId, "user", "Please update the company name.", DateTimeOffset.UtcNow));

        var engine = new FakeChatEngine();
        engine.EnqueueDetection(new ToolRouter.IntentDetection(
            ToolRouter.IntentKind.Document,
            Query: null,
            Folder: true,
            NewTopic: false,
            DocumentVerbatim: false,
            Explicit: true,
            ClarificationQuestion: null));

        engine.EnqueueChatReply("Updated.");
        engine.EnqueueDocumentReply("Sure — I'll update it now.");
        engine.EnqueueDocumentReply("I will replace the company with Aukenid.");

        var orchestrator = BuildOrchestrator(workspace, engine);
        var events = new RecordingTurnEventSink();

        var input = new TurnOrchestrator.TurnRequest(
            ThreadId: thread.Id,
            UserMessageId: userId,
            AssistantMessageId: assistantId,
            ParentId: parentId,
            Content: "Please update the company name.",
            IsFirstMessage: false,
            AlreadyHaveUserMessage: true,
            DocumentPanelOpen: true,
            ExistingMessages: workspace.Store.ListMessages(thread.Id),
            StoredAttachmentNames: Array.Empty<string>());

        var result = await orchestrator.ExecuteAsync(input, events, tryBuildKnowledgeTurnAsync: null, CancellationToken.None);

        Assert.IsTrue(result.Plan.Document);
        Assert.AreEqual(2, engine.DocumentCalls);
        Assert.AreEqual(existing, workspace.Store.ReadDocument(thread.Id));
        Assert.AreEqual(Aukenid.Core.Contracts.NoticeKeys.DocumentUpdateFailed, result.ToolWarning);
    }
}
