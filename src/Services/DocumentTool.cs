namespace Aukenid.Core.Services;

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Aukenid.Core.Attachments;
using Aukenid.Core.Contracts;
using Aukenid.Core.Data;
using Aukenid.Core.Engine;

/// <summary>
/// Executes a <c>ToolRouter.Plan.Document</c> instruction. Unlike the retrieval tools
/// (<see cref="WebEnricher"/>, <see cref="WikiEnricher"/>, <see cref="ScholarEnricher"/>, which inject a
/// <c>ChatTurn</c> BEFORE the GGUF answers), this one acts AFTER generation and writes to the document
/// panel directly: a small local GGUF has no real tool-calling, so the host must both decide
/// (ToolRouter) and perform (this class) the write - the chat reply can never be trusted to have done
/// it itself.
/// </summary>
public sealed class DocumentTool
{
    // Below this, a reply is almost certainly a short confirmation/meta sentence ("Please check the
    // panel now."), not actual document content worth transferring.
    private const int MinSubstantialLength = 120;

    // Keeps the dedicated generation prompt (current document + attachments) within a safe slice of
    // the 8K context window, leaving room for the system turn, recent history, and the answer itself.
    // Not a full chunking pipeline (see DocumentPass) - a very large document still gets truncated here.
    private const int MaxContextChars = 8000;

    private readonly ConversationStore _store;

    public DocumentTool(ConversationStore store)
    {
        _store = store;
    }

    public readonly record struct Result(bool Applied, string? Content, IReadOnlyList<DocumentVersionDto>? Versions);

    /// <summary>
    /// Injected into the MAIN chat completion only, when ToolRouter.Plan.Document is true: the actual
    /// document content now comes from a separate, isolated completion (<see cref="GenerationTurns"/>),
    /// so this one just needs the ordinary chat reply to summarize the change, not repeat it in full -
    /// the chat reply and the document panel update are complementary, not mutually exclusive.
    /// </summary>
    public static ChatTurn GuidanceTurn() => new("system", PromptLibrary.Get(PromptKey.DocumentGuidance));

    /// <summary>
    /// Builds an isolated prompt whose ONLY job is producing the document's new content: a small local
    /// model asked to both chat AND transform/translate an attachment in the same reply tends to just
    /// echo the source back, so this keeps the task and its material front and center, with nothing
    /// else to dilute it (the same reasoning the engine already applies to title generation and tool
    /// routing - a separate focused completion for one job, instead of overloading the main reply).
    /// </summary>
    internal static IReadOnlyList<ChatTurn> GenerationTurns(
        IReadOnlyList<ChatTurn> recentTurns,
        string? currentDocument,
        IReadOnlyList<AttachmentExcerpt> attachmentExcerpts)
    {
        var turns = new List<ChatTurn>
        {
            new("system", PromptLibrary.Get(PromptKey.DocumentGenerationSystem)),
        };

        turns.AddRange(recentTurns);

        var context = BuildContextBlock(currentDocument, attachmentExcerpts);
        if (context is not null)
        {
            var lastUserIndex = turns.FindLastIndex(t => t.Role == "user");
            if (lastUserIndex >= 0)
            {
                turns.Insert(lastUserIndex, new ChatTurn("system", context));
            }
            else
            {
                turns.Add(new ChatTurn("system", context));
            }
        }

        return turns;
    }

    private static string? BuildContextBlock(string? currentDocument, IReadOnlyList<AttachmentExcerpt> attachmentExcerpts)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(currentDocument))
        {
            builder.Append("Current document:\n").Append(currentDocument.Trim());
        }

        foreach (var excerpt in attachmentExcerpts)
        {
            if (string.IsNullOrWhiteSpace(excerpt.Text) || builder.Length >= MaxContextChars)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append("Attached file (").Append(excerpt.FileName).Append("):\n").Append(excerpt.Text.Trim());
        }

        if (builder.Length == 0)
        {
            return null;
        }

        if (builder.Length > MaxContextChars)
        {
            builder.Length = MaxContextChars;
            builder.Append('\u2026');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Resolves the content to write (an attachment sent with this turn wins over anything from the
    /// chat transcript - see <see cref="ResolveContent(string, IReadOnlyList{MessageDto}, IReadOnlyList{AttachmentExcerpt})"/>),
    /// then snapshots-before, saves, and snapshots-after so the write is itself undoable.
    /// <c>Result.Applied</c> is false when nothing substantial enough was found to write.
    /// </summary>
    internal Result Apply(
        string threadId,
        string currentReply,
        IReadOnlyList<MessageDto> priorActivePath,
        IReadOnlyList<AttachmentExcerpt> attachmentExcerpts)
    {
        var newContent = ResolveContent(currentReply, priorActivePath, attachmentExcerpts);
        return newContent is null ? default : ApplyResolvedContent(threadId, newContent);
    }

    /// <summary>
    /// Snapshots-before, saves, and snapshots-after an already-resolved document body - shared by the
    /// legacy whole-document <see cref="Apply"/> above and <see cref="DocumentEditOrchestrator"/>'s
    /// chunked patch result, which has no "resolve from chat reply" step of its own.
    /// </summary>
    internal Result ApplyResolvedContent(string threadId, string newContent)
    {
        _store.SnapshotDocumentVersion(threadId, "before-edit");
        if (!_store.SaveDocument(threadId, newContent))
        {
            return default;
        }

        _store.SnapshotDocumentVersion(threadId, "after-edit");
        return new Result(true, newContent, _store.ListDocumentVersions(threadId));
    }

    /// <summary>
    /// The reply just generated (or, failing that, the most recent substantial assistant message -
    /// see the 2-arg overload) wins: it is what actually synthesizes/organizes the requested content,
    /// including any attached file used as source data (e.g. "fill in this template using that file").
    /// A raw attachment dump is only used as a fallback, for a turn whose reply turned out to be a bare
    /// confirmation with nothing substantial of its own (e.g. "put this file in the document panel"
    /// with no other instruction).
    /// </summary>
    internal static string? ResolveContent(
        string currentReply,
        IReadOnlyList<MessageDto> priorActivePath,
        IReadOnlyList<AttachmentExcerpt> attachmentExcerpts)
    {
        return ResolveContent(currentReply, priorActivePath) ?? CombineAttachments(attachmentExcerpts);
    }

    /// <summary>
    /// Prefers the reply just generated for this turn (the model may have (re)drafted the content
    /// right now); otherwise falls back to the most recent substantial assistant message already on
    /// the active path, newest first. Returns null if nothing substantial is found either way.
    /// </summary>
    internal static string? ResolveContent(string currentReply, IReadOnlyList<MessageDto> priorActivePath)
    {
        if (IsSubstantial(currentReply))
        {
            return currentReply.Trim();
        }

        for (var i = priorActivePath.Count - 1; i >= 0; i--)
        {
            var message = priorActivePath[i];
            if (message.Role.Equals("Aukenid", System.StringComparison.OrdinalIgnoreCase) && IsSubstantial(message.Content))
            {
                return message.Content.Trim();
            }
        }

        return null;
    }

    private static string? CombineAttachments(IReadOnlyList<AttachmentExcerpt> excerpts)
    {
        var withText = excerpts.Where(excerpt => !string.IsNullOrWhiteSpace(excerpt.Text)).ToList();
        if (withText.Count == 0)
        {
            return null;
        }

        if (withText.Count == 1)
        {
            return withText[0].Text!.Trim();
        }

        var builder = new StringBuilder();
        foreach (var excerpt in withText)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append("## ").Append(excerpt.FileName).Append('\n').Append(excerpt.Text!.Trim());
        }

        return builder.ToString();
    }

    private static bool IsSubstantial(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length >= MinSubstantialLength;
}
