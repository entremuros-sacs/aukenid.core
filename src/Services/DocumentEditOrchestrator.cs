namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Aukenid.Core.Attachments;
using Aukenid.Core.Engine;

/// <summary>
/// Builds the chunk-aware document-edit prompt (see <see cref="DocumentChunker"/>, ADR-26) and applies
/// the model's response back onto the document - a scoped alternative to
/// <see cref="DocumentTool.GenerationTurns"/>'s whole-document rewrite. Every step here is deterministic
/// host code except the content the model supplies for each edit; a malformed or invalid response is
/// rejected outright (<see cref="TryApplyPatch"/> returns null) so the caller can fall back to the
/// legacy full-regeneration path for that turn, rather than risk a half-applied or corrupt document.
/// </summary>
internal static partial class DocumentEditOrchestrator
{
    // Chunks are included in full, in document order, until this budget is spent; the rest are shown
    // as an outline only (id + kind + heading, no body) - same oldest-first-wins shape as
    // FolderContext's sibling budget. Independent of DocumentChunker.ChunkBudgetChars, which bounds a
    // single chunk's size, not the whole prompt.
    private const int PromptBudgetChars = 8000;

    public sealed record EditPrompt(IReadOnlyList<ChatTurn> Turns, IReadOnlyList<DocumentChunk> Chunks, IReadOnlySet<string> EditableIds);

    public static EditPrompt BuildEditTurns(
        IReadOnlyList<ChatTurn> recentTurns,
        string? currentDocument,
        IReadOnlyList<AttachmentExcerpt> attachmentExcerpts)
    {
        var chunks = DocumentChunker.Chunk(currentDocument);
        var turns = new List<ChatTurn> { new("system", PromptLibrary.Get(PromptKey.DocumentChunkEditSystem)) };
        turns.AddRange(recentTurns);

        var (outline, editableIds) = BuildOutline(chunks);
        var context = new StringBuilder(outline);
        AppendAttachments(context, attachmentExcerpts);

        var lastUserIndex = turns.FindLastIndex(t => t.Role == "user");
        var contextTurn = new ChatTurn("system", context.ToString());
        if (lastUserIndex >= 0)
        {
            turns.Insert(lastUserIndex, contextTurn);
        }
        else
        {
            turns.Add(contextTurn);
        }

        return new EditPrompt(turns, chunks, editableIds);
    }

    private static (string Outline, HashSet<string> EditableIds) BuildOutline(IReadOnlyList<DocumentChunk> chunks)
    {
        var editable = new HashSet<string>(StringComparer.Ordinal);
        if (chunks.Count == 0)
        {
            return ("The document is currently empty. Use action=insert_after target=START to add content.", editable);
        }

        var sb = new StringBuilder("Document chunks (edit only ids whose content is shown in full below):");
        var used = 0;
        foreach (var chunk in chunks)
        {
            var heading = chunk.HeadingPath is null ? string.Empty : $" heading=\"{chunk.HeadingPath}\"";
            var kind = chunk.Kind.ToString().ToLowerInvariant();
            if (used + chunk.Text.Length <= PromptBudgetChars)
            {
                sb.Append("\n\n>>> CHUNK id=").Append(chunk.Id).Append(" kind=").Append(kind).Append(heading)
                  .Append('\n').Append(chunk.Text).Append("\n<<< END");
                used += chunk.Text.Length;
                editable.Add(chunk.Id);
            }
            else
            {
                sb.Append("\n\n[omitted] id=").Append(chunk.Id).Append(" kind=").Append(kind).Append(heading)
                  .Append(" chars=").Append(chunk.Text.Length);
            }
        }

        return (sb.ToString(), editable);
    }

    private static void AppendAttachments(StringBuilder sb, IReadOnlyList<AttachmentExcerpt> excerpts)
    {
        foreach (var excerpt in excerpts)
        {
            if (!string.IsNullOrWhiteSpace(excerpt.Text))
            {
                sb.Append("\n\nAttached file (").Append(excerpt.FileName).Append("):\n").Append(excerpt.Text.Trim());
            }
        }
    }

    private enum EditAction { Replace, Delete, InsertAfter }

    // Not Enum.TryParse: the wire protocol's snake_case action keyword ('insert_after') has no
    // relation to the C# enum member's own naming convention and should not need to match it.
    private static EditAction? ParseAction(string raw) => raw.ToLowerInvariant() switch
    {
        "replace" => EditAction.Replace,
        "delete" => EditAction.Delete,
        "insert_after" => EditAction.InsertAfter,
        _ => null,
    };

    private sealed record Edit(EditAction Action, string Target, string Content);

    [GeneratedRegex(@">>> EDIT\s+action=(?<action>\S+)\s+target=(?<target>\S+)\s*\r?\n(?<body>.*?)\s*<<< END", RegexOptions.Singleline)]
    private static partial Regex EditPattern();

    /// <summary>
    /// Parses and validates the model's response, then applies it to the chunk list. Returns null on
    /// any malformed or invalid reference (unknown id, editing a chunk that was never shown in full, an
    /// unrecognized action, no parseable edits at all) - the whole patch is all-or-nothing, never
    /// partially applied, so the caller can safely fall back to a full regeneration on null.
    /// </summary>
    public static string? TryApplyPatch(string rawResponse, IReadOnlyList<DocumentChunk> chunks, IReadOnlySet<string> editableIds)
    {
        var edits = new List<Edit>();
        foreach (Match match in EditPattern().Matches(rawResponse))
        {
            var action = ParseAction(match.Groups["action"].Value);
            if (action is null)
            {
                return null;
            }

            edits.Add(new Edit(action.Value, match.Groups["target"].Value, match.Groups["body"].Value.Trim()));
        }

        if (edits.Count == 0)
        {
            return null;
        }

        var knownIds = new HashSet<string>(chunks.Select(c => c.Id), StringComparer.Ordinal);
        var result = chunks.ToList();
        foreach (var edit in edits)
        {
            if (!ApplyEdit(edit, result, knownIds, editableIds))
            {
                return null;
            }
        }

        return result.Count == 0 ? string.Empty : string.Join("\n\n", result.Select(c => c.Text.Trim())) + "\n";
    }

    private static bool ApplyEdit(Edit edit, List<DocumentChunk> result, HashSet<string> knownIds, IReadOnlySet<string> editableIds)
    {
        switch (edit.Action)
        {
            case EditAction.Replace:
            {
                if (!editableIds.Contains(edit.Target))
                {
                    return false;
                }

                var index = result.FindIndex(c => c.Id == edit.Target);
                if (index < 0)
                {
                    return false;
                }

                if (edit.Content.Length == 0)
                {
                    result.RemoveAt(index);
                }
                else
                {
                    result[index] = result[index] with { Text = edit.Content };
                }

                return true;
            }

            case EditAction.Delete:
            {
                if (!knownIds.Contains(edit.Target))
                {
                    return false;
                }

                var index = result.FindIndex(c => c.Id == edit.Target);
                if (index < 0)
                {
                    return false;
                }

                result.RemoveAt(index);
                return true;
            }

            case EditAction.InsertAfter:
            {
                if (edit.Content.Length == 0)
                {
                    return false;
                }

                var newChunk = new DocumentChunk(Guid.NewGuid().ToString("N")[..8], DocumentChunkKind.Other, null, edit.Content);
                if (edit.Target.Equals("START", StringComparison.OrdinalIgnoreCase))
                {
                    result.Insert(0, newChunk);
                    return true;
                }

                if (edit.Target.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(newChunk);
                    return true;
                }

                if (!knownIds.Contains(edit.Target))
                {
                    return false;
                }

                var anchorIndex = result.FindIndex(c => c.Id == edit.Target);
                if (anchorIndex < 0)
                {
                    return false;
                }

                result.Insert(anchorIndex + 1, newChunk);
                return true;
            }

            default:
                return false;
        }
    }
}
