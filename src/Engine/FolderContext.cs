namespace Aukenid.Core.Engine;

using System.Text;
using Aukenid.Core.Data;
using Aukenid.Core.Contracts;

/// <summary>
/// Shared background from sibling threads in a custom folder (ChatGPT Projects-style).
/// General and Temporal stay isolated: each thread there only sees its own active path.
/// </summary>
public static class FolderContext
{
    public const int MaxTotalChars = 4000;
    public const int MaxCharsPerThread = 1200;

    public static ChatTurn? TryTurn(
        string folderPath,
        string folderName,
        string currentThreadId,
        IReadOnlyList<ThreadDto> threads,
        Func<string, IReadOnlyList<MessageDto>> loadMessages,
        int maxTotalChars = MaxTotalChars,
        int maxCharsPerThread = MaxCharsPerThread)
    {
        var text = TryBuild(folderPath, folderName, currentThreadId, threads, loadMessages, maxTotalChars, maxCharsPerThread);
        return text is null ? null : new ChatTurn("system", text);
    }

    public static string? TryBuild(
        string folderPath,
        string folderName,
        string currentThreadId,
        IReadOnlyList<ThreadDto> threads,
        Func<string, IReadOnlyList<MessageDto>> loadMessages,
        int maxTotalChars = MaxTotalChars,
        int maxCharsPerThread = MaxCharsPerThread)
    {
        if (!ConversationStore.IsCustomFolder(folderPath))
        {
            return null;
        }

        var siblings = threads
            .Where(t => t.Id != currentThreadId && t.FolderPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Created)
            .ToList();

        if (siblings.Count == 0)
        {
            return null;
        }

        var bodies = new List<(string Title, IReadOnlyList<MessageDto> Path)>(siblings.Count);
        foreach (var sibling in siblings)
        {
            var messages = loadMessages(sibling.Id);
            if (messages.Count == 0)
            {
                continue;
            }

            bodies.Add((sibling.Title, ActivePath.Resolve(messages, messages[^1].Id)));
        }

        if (bodies.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("Shared context from other conversations in the folder \"");
        sb.Append(folderName);
        sb.Append("\". Use it as project background for this thread. Do not mention this note unless asked.");
        sb.Append("\n\nConversations:\n");
        foreach (var (title, _) in bodies)
        {
            sb.Append("- ");
            sb.Append(title);
            sb.Append('\n');
        }

        foreach (var (title, path) in bodies)
        {
            if (sb.Length >= maxTotalChars - 8)
            {
                break;
            }

            sb.Append("\n### ");
            sb.Append(title);
            sb.Append('\n');

            var threadBudget = Math.Min(maxCharsPerThread, maxTotalChars - sb.Length);
            AppendTranscript(sb, path, threadBudget);
        }

        return sb.ToString();
    }

    private static void AppendTranscript(StringBuilder sb, IReadOnlyList<MessageDto> path, int budget)
    {
        var start = sb.Length;
        foreach (var message in path)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            var room = budget - (sb.Length - start);
            if (room <= 8)
            {
                break;
            }

            var role = ActivePath.NormalizeRole(message.Role) == "user" ? "User" : "Assistant";
            var line = $"{role}: {CollapseWhitespace(message.Content)}\n";
            if (line.Length <= room)
            {
                sb.Append(line);
                continue;
            }

            sb.Append(line.AsSpan(0, room - 1));
            sb.Append('\u2026');
            break;
        }
    }

    private static string CollapseWhitespace(string content)
    {
        var normalized = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Trim();
    }
}
