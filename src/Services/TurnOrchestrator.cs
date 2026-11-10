namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Attachments;
using Aukenid.Core.Contracts;
using Aukenid.Core.Data;
using Aukenid.Core.Engine;

/// <summary>
/// Host-agnostic workflow orchestrator: intent detection, deterministic planning, retrieval tool runs,
/// token streaming, attachment digestion, and optional document writes.
/// </summary>
public sealed class TurnOrchestrator
{
    private const int DocumentProgressReportChars = 24;
    private const int SummaryTailMessages = 12;
    private static readonly ChatTurn ChatStyleTurn = PromptLibrary.ChatStyleTurn();

    private static readonly ChatTurn CodeExplanationTurn = PromptLibrary.CodeExplanationTurn();

    private readonly IChatEngine _chatEngine;
    private readonly ConversationStore _conversationStore;
    private readonly AttachmentReaderRegistry _attachmentReaders;
    private readonly WebEnricher _webEnricher;
    private readonly ScholarEnricher _scholarEnricher;
    private readonly WikiEnricher _wikiEnricher;
    private readonly DocumentTool _documentTool;

    public TurnOrchestrator(
        IChatEngine chatEngine,
        ConversationStore conversationStore,
        AttachmentReaderRegistry attachmentReaders,
        WebEnricher webEnricher,
        ScholarEnricher scholarEnricher,
        WikiEnricher wikiEnricher,
        DocumentTool documentTool)
    {
        _chatEngine = chatEngine;
        _conversationStore = conversationStore;
        _attachmentReaders = attachmentReaders;
        _webEnricher = webEnricher;
        _scholarEnricher = scholarEnricher;
        _wikiEnricher = wikiEnricher;
        _documentTool = documentTool;
    }

    public readonly record struct TurnRequest(
        string ThreadId,
        string UserMessageId,
        string AssistantMessageId,
        string? ParentId,
        string Content,
        bool IsFirstMessage,
        bool AlreadyHaveUserMessage,
        bool DocumentPanelOpen,
        IReadOnlyList<MessageDto> ExistingMessages,
        IReadOnlyList<string> StoredAttachmentNames);

    public readonly record struct TurnResult(
        string Content,
        IReadOnlyList<Citation> Citations,
        string? ToolWarning,
        ToolRouter.Plan Plan);

    public async Task<TurnResult> ExecuteAsync(
        TurnRequest input,
        ITurnEventSink events,
        Func<string, CancellationToken, Task<(ChatTurn Turn, IReadOnlyList<Citation> Citations)?>>? tryBuildKnowledgeTurnAsync,
        CancellationToken cancellationToken)
    {
        events ??= NullTurnEventSink.Instance;

        var content = string.Empty;
        var citations = new List<Citation>();
        string? toolWarning = null;

        if (input.IsFirstMessage && !input.AlreadyHaveUserMessage)
        {
            var titleSeed = string.IsNullOrWhiteSpace(input.Content) && input.StoredAttachmentNames.Count > 0
                ? input.StoredAttachmentNames[0]
                : input.Content;
            var title = await _chatEngine.TryGenerateTitleAsync(titleSeed, cancellationToken)
                ?? DeriveTitle(titleSeed);
            if (!string.IsNullOrWhiteSpace(title))
            {
                _conversationStore.RenameThread(input.ThreadId, title);
                events.Emit(new ThreadsListEvent(input.ThreadId, _conversationStore.ListThreads()));
            }
        }

	    IReadOnlyList<ChatTurn> recentTurns;
	    if (input.IsFirstMessage || input.ParentId is null)
	    {
	        recentTurns = Array.Empty<ChatTurn>();
	    }
	    else
	    {
	        recentTurns = ActivePath.ToTurns(input.ExistingMessages, input.ParentId).TakeLast(6).ToList();
	    }

        var detection = await _chatEngine.DetectIntentsAsync(recentTurns, input.Content, cancellationToken);
        var plan = ToolRouter.PlanForTurn(input.Content, detection, input.StoredAttachmentNames.Count > 0, recentTurns);
        if (plan.NewTopic && WasDocumentWrite(input.ExistingMessages, input.ParentId))
        {
            plan = plan with { Steps = plan.Steps & ~ToolRouter.PlanKind.NewTopic };
        }

        // UX preference: while the document panel is open, treat the turn as part of the document
        // workflow by default, even if the user's message doesn't explicitly mention the panel.
        // This keeps the system from "forgetting" document mode between clarification turns.
        if (input.DocumentPanelOpen)
        {
            plan = plan with
            {
                Steps = (plan.Steps | ToolRouter.PlanKind.Document) & ~ToolRouter.PlanKind.NewTopic,
                // Do NOT force Chat here: document mode should default to document-only unless the
                // user explicitly asked for a chat response.
                Intents = (plan.Intents | ToolRouter.IntentKind.Document) & ~ToolRouter.IntentKind.NewTopic,
            };
        }

        // Deterministic guardrail: local models can get stuck asking clarification after
        // clarification instead of drafting. If we're in document mode and we already asked enough
        // questions recently, proceed to draft with reasonable defaults.
        if (plan.ClarifyInChat && plan.Document && input.DocumentPanelOpen && TooManyRecentClarifications(input.ExistingMessages, input.ParentId))
        {
            plan = plan with
            {
                Steps = plan.Steps & ~ToolRouter.PlanKind.ClarifyInChat,
                Intents = plan.Intents & ~ToolRouter.IntentKind.NeedClarification,
                ClarificationQuestion = null,
            };
        }

        var documentOnly = plan.Document
            && plan.HasIntent(ToolRouter.IntentKind.Document)
            && !plan.HasIntent(ToolRouter.IntentKind.Chat);

        if (plan.ClarifyInChat)
        {
            content = string.IsNullOrWhiteSpace(plan.ClarificationQuestion)
                ? await GenerateFallbackClarificationAsync(input.Content, cancellationToken)
                : plan.ClarificationQuestion;
        }

        if (plan.NewTopic && !input.IsFirstMessage)
        {
            events.Emit(new ThreadTopicShiftEvent(input.ThreadId, input.UserMessageId));
        }

        if (plan.StatusKey is not null)
        {
            events.Emit(new PendingEvent(input.ThreadId, plan.StatusKey));
        }

        if (plan.ClarifyInChat)
        {
	        if (plan.Document)
	        {
	            EnsureDocumentOpened(events, input.ThreadId);
	        }

            return new TurnResult(content, citations, toolWarning, plan);
        }

        if (!documentOnly)
        {
            await Task.Run(async () =>
            {
                ChatTurn? webTurn = null;
                ChatTurn? scholarTurn = null;
                ChatTurn? wikiTurn = null;
                if (plan.Wiki)
                {
                    try
                    {
                        var wiki = await _wikiEnricher.EnrichAsync(input.Content, plan.Query, cancellationToken);
                        wikiTurn = wiki.Turn;
                        citations.AddRange(wiki.Citations);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        toolWarning = NoticeKeys.WikiSearchIssue;
                    }
                }

                if (plan.Scholar)
                {
                    try
                    {
                        var scholarText = string.IsNullOrWhiteSpace(plan.Query) ? input.Content : plan.Query;
                        var scholar = await _scholarEnricher.EnrichAsync(input.Content, scholarText, cancellationToken);
                        scholarTurn = scholar.Turn;
                        citations.AddRange(scholar.Citations);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        toolWarning = NoticeKeys.ScholarSearchIssue;
                    }
                }

                if (plan.Web)
                {
                    try
                    {
                        var web = await _webEnricher.EnrichAsync(input.Content, plan.Query, cancellationToken);
                        webTurn = web.Turn;
                        citations.AddRange(web.Citations);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        toolWarning = NoticeKeys.WebSearchIssue;
                    }
                }

                (ChatTurn Turn, IReadOnlyList<Citation> Citations)? knowledge = null;
                if (tryBuildKnowledgeTurnAsync is not null)
                {
                    knowledge = await tryBuildKnowledgeTurnAsync(input.Content, cancellationToken);
                }

                var knowledgeTurn = knowledge?.Turn;
                if (knowledge?.Citations is not null)
                {
                    citations.AddRange(knowledge.Value.Citations);
                }

                var turns = BuildTurns(
                    input.ThreadId,
                    input.UserMessageId,
                    webTurn,
                    scholarTurn,
                    wikiTurn,
                    knowledgeTurn,
                    plan.Folder);

                var slots = AttachmentSlots(input.ThreadId, input.UserMessageId);
                var unread = new List<string>();
                var storedChars = 0;
                foreach (var slot in slots)
                {
                    if (slot.Notes is null)
                    {
                        unread.Add(slot.Name);
                    }
                    else
                    {
                        foreach (var note in slot.Notes)
                        {
                            storedChars += note.Length;
                        }
                    }
                }

	                // Only pages; otherwise the notes are big enough that the prompt itself gets diluted.
	                var otherChars = turns.Sum(turn => turn.Content.Length);
	                var room = DocumentPass.RoomForDocument(otherChars);
                var pendingChars = unread.Sum(name =>
                {
                    var path = _conversationStore.TryGetAttachmentPath(input.ThreadId, name);
                    return path is null ? 0 : (int)Math.Min(new FileInfo(path).Length, int.MaxValue);
                });

                var pendingNeedsPass = pendingChars > 0
                    && (DocumentPass.NeedsPass(otherChars, pendingChars) || storedChars + pendingChars > room);
                var excerpts = unread.Count > 0 ? ReadExcerpts(input.ThreadId, unread, DocumentPass.FullReadChars) : [];
                var built = pendingNeedsPass
                    ? await BuildDigestsAsync(events, input.ThreadId, excerpts, cancellationToken)
                    : null;

                var digestNotes = new List<string>();
                foreach (var slot in slots)
                {
                    if (slot.Notes is not null)
                    {
                        digestNotes.AddRange(slot.Notes);
                    }
                    else if (built is not null && built.TryGetValue(slot.Name, out var fresh))
                    {
                        digestNotes.AddRange(fresh);
                    }
                }

                if (digestNotes.Count > 0 || pendingNeedsPass)
                {
                    var shortTurn = pendingNeedsPass
                        ? null
                        : AttachmentContext.TryTurn(excerpts, room);
                    var usedByShort = shortTurn?.Content.Length ?? 0;
                    var fitted = DocumentPass.Fit(digestNotes, Math.Max(0, room - usedByShort));
                    if (fitted.Length > 0)
                    {
                        InsertBeforeLastUser(turns, new ChatTurn("system", DocumentPass.ReducePrompt(fitted)));
                    }

                    InsertBeforeLastUser(turns, shortTurn);
                }
                else if (excerpts.Count > 0)
                {
                    InsertBeforeLastUser(turns, AttachmentContext.TryTurn(excerpts, room));
                }

                if (plan.Document)
                {
                    InsertBeforeLastUser(turns, DocumentTool.GuidanceTurn());
                }

                // Safety: when a document write is happening, some local models ignore
                // DocumentGuidance and dump the whole draft into the chat stream. If we streamed
                // token-by-token, we couldn't retract it after the fact.
                //
                // So for document workflows, buffer the full reply first, then coerce it into a
                // short confirmation if needed, and only then emit it to the UI.
                if (plan.Document)
                {
                    content = await _chatEngine.CompleteAsync(turns, cancellationToken);

                    var rawReply = content;
                    content = CoerceDocumentChatReply(rawReply);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        // Retry once to get a properly formatted chat reply (see DocumentGuidance).
                        var repairTurns = turns.ToList();
                        repairTurns.Add(new ChatTurn(
                            "user",
                            "Your previous response did not follow the required >>> CHAT / <<< END format. "
	                            + "Rewrite the same intended chat reply using ONLY that CHAT block (no other text).\n\nPrevious response:\n" + rawReply));

                        var repaired = await _chatEngine.CompleteAsync(repairTurns, cancellationToken);
                        content = CoerceDocumentChatReply(repaired);
                    }

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        content = await GenerateDocumentConfirmationAsync(input.Content, cancellationToken);
                    }

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        events.Emit(new TokenEvent(input.ThreadId, input.AssistantMessageId, content));
                    }
                }
                else
                {
                    await foreach (var token in _chatEngine.StreamAsync(turns, cancellationToken))
                    {
                        content += token;
                        if (token.Length > 0)
                        {
                            events.Emit(new TokenEvent(input.ThreadId, input.AssistantMessageId, token));
                        }
                    }
                }

                toolWarning ??= _chatEngine.StreamNoticeKey;

                if (!plan.Document && citations.Count > 0 && !string.IsNullOrWhiteSpace(content) && toolWarning is null)
                {
                    content = SourceCitations.Append(content, citations);
                }
            }, cancellationToken);
        }

        if (plan.Document)
        {
            // If the chat completion itself is asking a follow-up question, we are still in the
            // clarification stage. Do not generate/overwrite document content yet; keep (or create)
            // the blank document so the panel stays available.
            if (!plan.ClarifyInChat && LooksLikeQuestion(content))
            {
                EnsureDocumentOpened(events, input.ThreadId);
                return new TurnResult(content, citations, toolWarning, plan);
            }

            var documentTurns = ActivePath.ToTurns(_conversationStore.ListMessages(input.ThreadId), input.UserMessageId)
                .TakeLast(7)
                .ToList();

            documentTurns = FilterDocumentTurns(documentTurns);
            var documentNotice = await ApplyDocumentCommandAsync(
                events,
                input.ThreadId,
                documentTurns,
                input.StoredAttachmentNames,
                plan.DocumentVerbatim,
                cancellationToken);

            toolWarning = documentNotice ?? toolWarning;
        }

        return new TurnResult(content, citations, toolWarning, plan);
    }

	private void EnsureDocumentOpened(ITurnEventSink events, string threadId)
	{
	    var existing = _conversationStore.ReadDocument(threadId);
	    if (existing is null)
	    {
	        // A blank file is enough to mark HasDocument=true for UI state.
	        if (!_conversationStore.SaveDocument(threadId, string.Empty))
	        {
	            return;
	        }

	        existing = string.Empty;
	    }

	    // Send updated thread metadata first so the UI can treat the document as "existing".
	    events.Emit(new ThreadsListEvent(threadId, _conversationStore.ListThreads()));
	    events.Emit(new DocumentOpenEvent(threadId, existing, Exists: true));
	    events.Emit(new DocumentVersionsListEvent(threadId, _conversationStore.ListDocumentVersions(threadId)));
	}

    private static bool TooManyRecentClarifications(IReadOnlyList<MessageDto> priorMessages, string? parentId)
    {
        if (string.IsNullOrWhiteSpace(parentId) || priorMessages.Count == 0)
        {
            return false;
        }

        // Walk the active path and count recent assistant questions.
        var turns = ActivePath.ToTurns(priorMessages, parentId).TakeLast(12);
        var questions = 0;
        foreach (var turn in turns)
        {
            if (turn.Role == "assistant" && LooksLikeQuestion(turn.Content))
            {
                questions++;
            }
        }

        // After one clarification, proceed to draft instead of looping.
        return questions >= 1;
    }

    private async Task<string> GenerateFallbackClarificationAsync(string userPrompt, CancellationToken cancellationToken)
    {
        var turns = new List<ChatTurn>
        {
            ChatStyleTurn,
            PromptLibrary.RuntimeContextTurn(DateTimeOffset.Now),
            new("user", "Ask ONE concise clarification question to proceed. Reply with ONLY the question text.\n\nUser message:\n" + userPrompt),
        };

        var question = await _chatEngine.CompleteAsync(turns, cancellationToken);
        return string.IsNullOrWhiteSpace(question) ? "?" : question.Trim();
    }

    private async Task<string> GenerateDocumentConfirmationAsync(string userPrompt, CancellationToken cancellationToken)
    {
        var turns = new List<ChatTurn>
        {
            ChatStyleTurn,
            PromptLibrary.RuntimeContextTurn(DateTimeOffset.Now),
            new("user", "Reply with ONE short sentence confirming you updated the side document panel. Do not include the document body.\n\nUser message:\n" + userPrompt),
        };

        var reply = await _chatEngine.CompleteAsync(turns, cancellationToken);
        return string.IsNullOrWhiteSpace(reply) ? "OK." : reply.Trim();
    }

    private readonly record struct AttachmentSlot(string Name, IReadOnlyList<string>? Notes);

    private List<AttachmentSlot> AttachmentSlots(string threadId, string leafMessageId)
    {
        var slots = new List<AttachmentSlot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var path = ActivePath.Resolve(_conversationStore.ListMessages(threadId), leafMessageId);
        foreach (var message in path)
        {
            if (message.Attachments is not { Count: > 0 })
            {
                continue;
            }

            foreach (var attachment in message.Attachments)
            {
                if (!seen.Add(attachment))
                {
                    continue;
                }

	                var attachmentPath = _conversationStore.TryGetAttachmentPath(threadId, attachment);
	                slots.Add(new AttachmentSlot(
	                    attachment,
	                    attachmentPath is null ? null : DocumentDigest.TryRead(attachmentPath)));
            }
        }

        return slots;
    }

    private IReadOnlyList<AttachmentExcerpt> ReadExcerpts(string threadId, IReadOnlyList<string> names, int maxChars)
    {
        if (names.Count == 0)
        {
            return [];
        }

        var excerpts = new List<AttachmentExcerpt>();
        foreach (var name in names)
        {
            var path = _conversationStore.TryGetAttachmentPath(threadId, name);
            if (path is null)
            {
                excerpts.Add(new AttachmentExcerpt(name, null, "attach.unreadable"));
                continue;
            }

            var reader = _attachmentReaders.ForFileName(name);
            if (reader is null)
            {
                excerpts.Add(new AttachmentExcerpt(name, null, "attach.unsupported"));
                continue;
            }

            var result = reader.Read(path, maxChars);
            if (result.Ok)
            {
                excerpts.Add(new AttachmentExcerpt(name, result.Text ?? string.Empty, null));
            }
            else
            {
                excerpts.Add(new AttachmentExcerpt(name, null, result.ErrorKey ?? "attach.unreadable"));
            }
        }

        return excerpts;
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> BuildDigestsAsync(
        ITurnEventSink events,
        string threadId,
        IReadOnlyList<AttachmentExcerpt> excerpts,
        CancellationToken cancellationToken)
    {
        var built = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var excerpt in excerpts)
        {
            if (string.IsNullOrWhiteSpace(excerpt.Text))
            {
                continue;
            }

            var slices = DocumentPass.Pack([excerpt]);
            if (slices.Count == 0)
            {
                continue;
            }

            var notes = new List<string>();
            var complete = true;
            foreach (var slice in slices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (slice.Paged)
                {
                    events.Emit(new PendingEvent(threadId, "reading.pages", slice.FromPage, slice.ToPage));
                }
                else
                {
                    events.Emit(new PendingEvent(threadId, "reading.document", slice.Part, slice.PartCount));
                }

                string note;
                try
                {
                    note = await CompleteAsync(DocumentPass.DigestTurns(slice), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsContextOverflow(ex))
                {
                    complete = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(note))
                {
                    complete = false;
                    continue;
                }

                notes.Add(DocumentPass.NoteFor(slice, note));
            }

            if (notes.Count > 0)
            {
                built[excerpt.FileName] = notes;
            }

            if (!complete)
            {
                continue;
            }

            var path = _conversationStore.TryGetAttachmentPath(threadId, excerpt.FileName);
            if (path is null)
            {
                continue;
            }

            try
            {
                DocumentDigest.Write(path, notes);
            }
            catch
            {
                // Best effort; if notes can't be persisted we can still use them for this turn.
            }
        }

        return built;
    }

    private async Task<string> CompleteAsync(IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await foreach (var token in _chatEngine.StreamAsync(turns, cancellationToken))
        {
            text.Append(token);
        }

        return text.ToString();
    }

    private List<ChatTurn> BuildTurns(
        string threadId,
        string leafMessageId,
        ChatTurn? webTurn,
        ChatTurn? scholarTurn,
        ChatTurn? wikiTurn,
        ChatTurn? knowledgeTurn,
        bool folderNeeded)
    {
        var summary = _conversationStore.ReadSummary(threadId);
        var turns = ActivePath.ToTurns(_conversationStore.ListMessages(threadId), leafMessageId).ToList();
        ChatTurn? summaryTurn = null;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            if (turns.Count > SummaryTailMessages)
            {
                turns = turns.TakeLast(SummaryTailMessages).ToList();
            }

            summaryTurn = PromptLibrary.ThreadSummaryNoteTurn(summary);
        }

        var threads = _conversationStore.ListThreads();
        var thread = threads.FirstOrDefault(t => t.Id == threadId);

        if (thread is not null && (folderNeeded || !FolderContext.IsHeavy(thread.FolderPath, thread.Id, threads)))
        {
            var folderName = thread.FolderPath.Split('/').LastOrDefault() ?? thread.FolderPath;
            var folderTurn = FolderContext.TryTurn(
                thread.FolderPath,
                folderName,
                thread.Id,
                threads,
                _conversationStore.ListMessages);
            if (folderTurn is not null)
            {
                turns.Insert(0, folderTurn.Value);
            }
        }

        if (summaryTurn is not null)
        {
            var insertAt = turns.Count > 0 && turns[0].Role == "system" ? 1 : 0;
            turns.Insert(insertAt, summaryTurn.Value);
        }

        if (knowledgeTurn is not null)
        {
            InsertBeforeLastUser(turns, knowledgeTurn);
        }

        if (wikiTurn is not null)
        {
            InsertBeforeLastUser(turns, wikiTurn);
        }

        if (scholarTurn is not null)
        {
            InsertBeforeLastUser(turns, scholarTurn);
        }

        if (webTurn is not null)
        {
            InsertBeforeLastUser(turns, webTurn);
        }

        if (ShouldExplainCode(turns))
        {
            turns.Add(CodeExplanationTurn);
        }

        // Keep behavior consistent and deterministic across all model calls.
        turns.Insert(0, PromptLibrary.RuntimeContextTurn(DateTimeOffset.Now));
        turns.Insert(0, ChatStyleTurn);

        return turns;
    }

    private static bool ShouldExplainCode(IReadOnlyList<ChatTurn> turns)
    {
        if (turns.Count == 0)
        {
            return false;
        }

        var lastUser = turns.LastOrDefault(t => t.Role == "user").Content ?? string.Empty;
        if (lastUser.Length == 0)
        {
            return false;
        }

        return lastUser.Contains("```", StringComparison.Ordinal)
            || lastUser.Contains("`", StringComparison.Ordinal)
            || lastUser.Contains(".cs", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains(".json", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("stack trace", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("exception", StringComparison.OrdinalIgnoreCase);
    }
    private async Task<string?> ApplyDocumentCommandAsync(
        ITurnEventSink events,
        string threadId,
        IReadOnlyList<ChatTurn> documentTurns,
        IReadOnlyList<string> attachmentNames,
        bool verbatim,
        CancellationToken cancellationToken)
    {
        try
        {
            var attachmentExcerpts = attachmentNames.Count > 0
                ? ReadExcerpts(threadId, attachmentNames, DocumentPass.FullReadChars)
                : [];

            if (verbatim && DocumentTool.TryVerbatimMarkdownContent(attachmentExcerpts) is { } verbatimContent)
            {
                return FinishDocumentWrite(events, threadId, _documentTool.ApplyResolvedContent(threadId, verbatimContent));
            }

            var currentDocument = _conversationStore.ReadDocument(threadId);
            events.Emit(new PendingEvent(threadId, NoticeKeys.DocumentGenerating));

            string? patched = null;
            var hasExistingDocument = !string.IsNullOrWhiteSpace(currentDocument);
            if (hasExistingDocument)
            {
                var editPrompt = DocumentEditOrchestrator.BuildEditTurns(documentTurns, currentDocument, attachmentExcerpts);
                var editResponse = await StreamDocumentAsync(events, threadId, editPrompt.Turns, cancellationToken);
                patched = DocumentEditOrchestrator.TryApplyPatch(editResponse, editPrompt.Chunks, editPrompt.EditableIds);

                if (patched is null)
                {
                    // Common failure mode on small local models: the intent is correct, but the
                    // response contains extra prose instead of the strict >>> EDIT blocks.
                    // Retry once by asking the model to re-express the same edits in the protocol.
                    var repairTurns = editPrompt.Turns.ToList();
                    repairTurns.Add(new ChatTurn(
                        "user",
                        "Your previous response could not be applied because it did not follow the required EDIT block format. "
                        + "Rewrite the same intended changes as EDIT blocks ONLY (no other text).\n\nPrevious response:\n" + editResponse));

                    var repaired = await StreamDocumentAsync(events, threadId, repairTurns, cancellationToken);
                    patched = DocumentEditOrchestrator.TryApplyPatch(repaired, editPrompt.Chunks, editPrompt.EditableIds);
                }
            }

            DocumentTool.Result result;
            if (patched is not null)
            {
                result = _documentTool.ApplyResolvedContent(threadId, patched);
            }
            else if (hasExistingDocument)
            {
                // No safe fallback: if the strict edit protocol fails twice (original + repair),
                // do not overwrite the user's document with a best-effort regeneration.
                // This avoids corrupting the draft (e.g. duplicated sections) when the model emits
                // full-document prose instead of protocol edits.
                return NoticeKeys.DocumentUpdateFailed;
            }
            else
            {
                var generationTurns = DocumentTool.GenerationTurns(documentTurns, currentDocument, attachmentExcerpts);
                var generated = await StreamDocumentAsync(events, threadId, generationTurns, cancellationToken);
                result = _documentTool.Apply(threadId, generated, [], attachmentExcerpts);
            }

            return FinishDocumentWrite(events, threadId, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return NoticeKeys.DocumentUpdateFailed;
        }
    }
    private string? FinishDocumentWrite(ITurnEventSink events, string threadId, DocumentTool.Result result)
    {
        if (!result.Applied || string.IsNullOrWhiteSpace(result.Content) || result.Versions is null)
        {
            return NoticeKeys.DocumentUpdateFailed;
        }

        events.Emit(new DocumentOpenEvent(threadId, result.Content, Exists: true));
        events.Emit(new DocumentVersionsListEvent(threadId, result.Versions));
        events.Emit(new ThreadsListEvent(threadId, _conversationStore.ListThreads()));
        return NoticeKeys.DocumentUpdated;
    }

    private async Task<string> StreamDocumentAsync(ITurnEventSink events, string threadId, IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken)
    {
        var generated = new StringBuilder();
        var lastReportedLength = 0;
        await foreach (var token in _chatEngine.StreamAsync(turns, cancellationToken))
        {
            generated.Append(token);
            if (generated.Length - lastReportedLength >= DocumentProgressReportChars)
            {
                lastReportedLength = generated.Length;
                events.Emit(new DocumentProgressEvent(threadId, generated.Length));
            }
        }

        return generated.ToString();
    }

    private static List<ChatTurn> FilterDocumentTurns(IReadOnlyList<ChatTurn> turns)
    {
        if (turns.Count == 0)
        {
            return [];
        }

        // Prevent accidental "conversation" text from showing up in the generated document by
        // excluding assistant clarification questions from the document-generation context.
        var filtered = new List<ChatTurn>(turns.Count);
        foreach (var turn in turns)
        {
            if (turn.Role == "assistant" && LooksLikeQuestion(turn.Content))
            {
                continue;
            }

            filtered.Add(turn);
        }

        return filtered;
    }

    private static bool LooksLikeQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains('?', StringComparison.Ordinal) || text.Contains('¿', StringComparison.Ordinal);
    }

    public static bool IsContextOverflow(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name == "ContextOverflowException")
            {
                return true;
            }
        }

        return false;
    }

    private static string? CoerceDocumentChatReply(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Deterministic: prefer extracting the explicit CHAT envelope.
        const string start = ">>> CHAT";
        const string end = "<<< END";
        var startIndex = raw.IndexOf(start, StringComparison.Ordinal);
        if (startIndex >= 0)
        {
            var endIndex = raw.IndexOf(end, startIndex, StringComparison.Ordinal);
            if (endIndex > startIndex)
            {
                var innerStart = startIndex + start.Length;
                var inner = raw[innerStart..endIndex].Trim();
                return inner.Length == 0 ? null : inner;
            }
        }

        // Back-compat safety: accept a short single-paragraph reply as-is.
        // This is not content-specific (poem/memo/report/etc), only structural.
        var trimmed = raw.Trim();
        if (!trimmed.Contains('\n', StringComparison.Ordinal) && trimmed.Length <= 240)
        {
            return trimmed;
        }

        return null;
    }

    public static string? DeriveTitle(string prompt)
    {
        var normalized = string.Join(' ', prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        const int maxLength = 60;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd() + "\u2026";
    }

    public static bool WasDocumentWrite(IReadOnlyList<MessageDto> priorMessages, string? parentId)
    {
        var last = parentId is null ? null : priorMessages.FirstOrDefault(m => m.Id == parentId);
        return last is not null
            && last.Role.Equals("Aukenid", StringComparison.OrdinalIgnoreCase)
            && (last.Content.StartsWith($"%%auk:{NoticeKeys.DocumentUpdated}%%", StringComparison.Ordinal)
                || last.Content.StartsWith($"%%auk:{NoticeKeys.DocumentUpdateFailed}%%", StringComparison.Ordinal));
    }

    private static void InsertBeforeLastUser(List<ChatTurn> turns, ChatTurn? extra)
    {
        if (extra is null)
        {
            return;
        }

        var lastUserIndex = turns.FindLastIndex(t => t.Role == "user");
        if (lastUserIndex < 0)
        {
            turns.Add(extra.Value);
            return;
        }

        turns.Insert(lastUserIndex, extra.Value);
    }
}

