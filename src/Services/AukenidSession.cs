namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Attachments;
using Aukenid.Core.Engine;

/// <summary>
/// This API is intentionally small: it exposes the orchestration a host needs (send messages,
/// attach files, export transcript, export document, reset thread) while keeping implementation
/// details (attachment readers, excerpt packing, routing notes) encapsulated.
/// </summary>
public sealed class AukenidSession : IDisposable
{
    private readonly IChatEngine _engine;
    private readonly GroundedChat _grounded;
    private readonly AttachmentReaderRegistry _attachments = new();

    private readonly List<AttachmentExcerpt> _stickyAttachments = [];
    private readonly List<ChatTurn> _conversation = [];

    private string _document = string.Empty;
    private bool _documentDirty;

    private AukenidSession(IChatEngine engine, GroundedChat grounded)
    {
        _engine = engine;
        _grounded = grounded;
    }

    /// <summary>
    /// Creates a session with the default grounding providers (Startpage / Wikipedia / Semantic Scholar).
    /// </summary>
    public static AukenidSession CreateDefault(IChatEngine engine) => new(
        engine,
        GroundedChat.CreateDefault());

    /// <summary>
    /// Loads the model into the underlying engine.
    /// </summary>
    public Task LoadModelAsync(string profileIdOrPath, CancellationToken cancellationToken) =>
        _engine.LoadModelAsync(profileIdOrPath, cancellationToken);

    public string? ActiveModelName => _engine.ActiveModelName;

    public bool HasAttachments => _stickyAttachments.Count > 0;

    public bool HasDocument => !string.IsNullOrWhiteSpace(_document);

    public bool IsDocumentDirty => _documentDirty;

    public void NewThread()
    {
        _stickyAttachments.Clear();
        _conversation.Clear();
        _document = string.Empty;
        _documentDirty = false;
    }

    public AttachmentAddResult Attach(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AttachmentAddResult(false, string.Empty, null, "attach.unreadable");
        }

        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileName(fullPath);
        if (!File.Exists(fullPath))
        {
            return new AttachmentAddResult(false, fileName, null, "attach.unreadable");
        }

        var reader = _attachments.ForFileName(fileName);
        if (reader is null)
        {
            _stickyAttachments.Add(new AttachmentExcerpt(fileName, null, "attach.unsupported"));
            return new AttachmentAddResult(false, fileName, null, "attach.unsupported");
        }

        var result = reader.Read(fullPath, AttachmentContext.MaxCharsPerFile);
        if (result.Ok)
        {
            var text = result.Text ?? string.Empty;
            _stickyAttachments.Add(new AttachmentExcerpt(fileName, text, null));
            return new AttachmentAddResult(true, fileName, text.Length, null);
        }

        _stickyAttachments.Add(new AttachmentExcerpt(fileName, null, result.ErrorKey));
        return new AttachmentAddResult(false, fileName, null, result.ErrorKey ?? "attach.unreadable");
    }

    public async Task<GroundedReply> SendAsync(string userText, CancellationToken cancellationToken)
    {
        userText ??= string.Empty;
        userText = userText.Trim();
        if (userText.Length == 0)
        {
            return default;
        }

        var history = _conversation.ToList();
        IEnumerable<ChatTurn?> Inject(ToolRouter.Plan plan)
        {
            if (HasAttachments)
            {
                yield return AttachmentContext.TryTurn(_stickyAttachments);
            }

            if (plan.Document)
            {
                yield return DocumentTool.GuidanceTurn();
            }
        }

        var reply = await _grounded.ReplyAsync(
            _engine,
            _conversation,
            userText,
            hasAttachments: HasAttachments,
            injectBeforeLastUser: Inject,
            cancellationToken);

        var userTurn = _conversation.Count >= 2 ? _conversation[^2] : new ChatTurn("user", userText);
        var assistantTurn = _conversation.Count >= 1 ? _conversation[^1] : new ChatTurn("assistant", reply.Text);

        if (reply.Plan.Document)
        {
            var historyWithUser = history.Concat(new[] { userTurn }).ToList();
            await TryApplyDocumentAsync(historyWithUser, cancellationToken);
        }

        return reply;
    }

    public DocumentSaveResult SaveDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DocumentSaveResult(false, 0, "doc.invalid-path");
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var content = _document;
        File.WriteAllText(fullPath, content);
        _documentDirty = false;
        return new DocumentSaveResult(true, content.Length, null);
    }

    public ThreadSaveResult SaveThread(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ThreadSaveResult(false, 0, "thread.invalid-path");
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var markdown = BuildThreadMarkdown();
        File.WriteAllText(fullPath, markdown);
        return new ThreadSaveResult(true, markdown.Length, null);
    }

    private string BuildThreadMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# Transcript\n\n");

        foreach (var turn in _conversation)
        {
            var heading = turn.Role switch
            {
                "user" => "You",
                "assistant" => "Aukenid",
                _ => turn.Role,
            };

            sb.Append("## ");
            sb.Append(heading);
            sb.Append("\n\n");

            sb.Append(turn.Content.Trim());

            sb.Append("\n\n");

        }

        return sb.ToString();
    }

    private async Task<bool> TryApplyDocumentAsync(IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken)
    {
        // "Verbatim" means: markdown attachments can be pasted as-is.
        var verbatim = DocumentTool.TryVerbatimMarkdownContent(_stickyAttachments);
        var newContent = verbatim;
        if (newContent is null)
        {
            var turns = DocumentTool.GenerationTurns(history, _document, _stickyAttachments);
            newContent = await _engine.CompleteAsync(turns, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(newContent))
        {
            return false;
        }

        newContent = DocumentTool.StripWholeDocumentCodeFence(newContent);
        if (string.IsNullOrWhiteSpace(newContent))
        {
            return false;
        }

        _document = newContent.TrimEnd() + "\n";
        _documentDirty = true;
        return true;
    }

    private static void InsertBeforeLastUser(List<ChatTurn> turns, ChatTurn? extra)
    {
        if (extra is null)
        {
            return;
        }

        var lastUser = turns.FindLastIndex(t => t.Role == "user");
        if (lastUser >= 0)
        {
            turns.Insert(lastUser, extra.Value);
        }
        else
        {
            turns.Add(extra.Value);
        }
    }

    public void Dispose()
    {
        if (_engine is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public readonly record struct AttachmentAddResult(
        bool Ok,
        string FileName,
        int? ExtractedChars,
        string? ErrorKey);

    public readonly record struct DocumentSaveResult(
        bool Ok,
        int WrittenChars,
        string? ErrorKey);

    public readonly record struct ThreadSaveResult(
        bool Ok,
        int WrittenChars,
        string? ErrorKey);
}

