namespace Aukenid.Core.Engine;

using Aukenid.Core.Contracts;

public readonly record struct ChatTurn(string Role, string Content);

/// <summary>
/// The branch that enters the prompt (spec: active path). Sibling regenerations stay on disk
/// but are not sent to the model. Threads written before parent links existed are treated as a
/// linear list in file order.
/// </summary>
public static class ActivePath
{
    public static IReadOnlyList<ChatTurn> ToTurns(IReadOnlyList<MessageDto> messages, string leafId)
    {
        var path = Resolve(messages, leafId);
        var turns = new ChatTurn[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            turns[i] = new ChatTurn(NormalizeRole(path[i].Role), path[i].Content);
        }

        return turns;
    }

    public static IReadOnlyList<MessageDto> Resolve(IReadOnlyList<MessageDto> messages, string leafId)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var byId = new Dictionary<string, MessageDto>(messages.Count);
        foreach (var message in messages)
        {
            byId[message.Id] = message;
        }

        var hasLinks = false;
        foreach (var message in messages)
        {
            if (message.ParentId is not null && byId.ContainsKey(message.ParentId))
            {
                hasLinks = true;
                break;
            }
        }

        return hasLinks ? WalkParents(byId, messages, leafId) : ChronologicalPrefix(messages, leafId);
    }

    private static IReadOnlyList<MessageDto> WalkParents(
        Dictionary<string, MessageDto> byId,
        IReadOnlyList<MessageDto> messages,
        string leafId)
    {
        if (!byId.TryGetValue(leafId, out var leaf))
        {
            return messages;
        }

        var path = new List<MessageDto>();
        var seen = new HashSet<string>();
        MessageDto? current = leaf;
        while (current is not null && seen.Add(current.Id))
        {
            path.Add(current);
            current = current.ParentId is not null && byId.TryGetValue(current.ParentId, out var parent)
                ? parent
                : null;
        }

        path.Reverse();
        return path;
    }

    private static IReadOnlyList<MessageDto> ChronologicalPrefix(IReadOnlyList<MessageDto> messages, string leafId)
    {
        var prefix = new List<MessageDto>(messages.Count);
        foreach (var message in messages)
        {
            prefix.Add(message);
            if (message.Id == leafId)
            {
                return prefix;
            }
        }

        return messages;
    }

    public static string NormalizeRole(string role) =>
        role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant";
}
