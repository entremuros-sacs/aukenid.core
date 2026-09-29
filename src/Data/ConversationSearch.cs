namespace Aukenid.Core.Data;

using Aukenid.Core.Contracts;

public sealed record SearchOccurrence(string ThreadId, string MessageId, int Offset, int Length);

public sealed record SearchThreadHit(
    string Id,
    string Title,
    string FolderPath,
    DateTimeOffset Created,
    int Count,
    IReadOnlyList<SearchOccurrence> Occurrences);

public sealed record SearchResult(
    string Query,
    IReadOnlyList<FolderDto> Folders,
    IReadOnlyList<SearchThreadHit> Threads);

/// <summary>
/// Full-text search over persisted conversation files and in-memory Temporal threads.
/// </summary>
public sealed class ConversationSearch
{
    private readonly ConversationStore _store;

    public ConversationSearch(ConversationStore store)
    {
        _store = store;
    }

    public SearchResult Search(string? query, bool wholeWord = false)
    {
        var needle = query?.Trim() ?? string.Empty;
        if (needle.Length == 0)
        {
            return new SearchResult(needle, [], []);
        }

        var foldersByPath = _store.ListFolders().ToDictionary(folder => folder.Path, StringComparer.Ordinal);
        var threads = new List<SearchThreadHit>();
        var folders = new Dictionary<string, FolderDto>(StringComparer.Ordinal);

        foreach (var (thread, messages) in _store.ListConversations())
        {
            var occurrences = FindOccurrences(thread.Id, messages, needle, wholeWord);
            if (occurrences.Count == 0)
            {
                continue;
            }

            threads.Add(new SearchThreadHit(
                thread.Id,
                thread.Title,
                thread.FolderPath,
                thread.Created,
                occurrences.Count,
                occurrences));

            if (foldersByPath.TryGetValue(thread.FolderPath, out var folder))
            {
                folders.TryAdd(folder.Path, folder);
            }
            else
            {
                folders.TryAdd(thread.FolderPath, new FolderDto(thread.FolderPath, thread.FolderPath, false));
            }
        }

        var orderedFolders = FolderOrder.Sort(folders.Values);

        var orderedThreads = threads
            .OrderByDescending(thread => thread.Created)
            .ToList();

        return new SearchResult(needle, orderedFolders, orderedThreads);
    }

    private static List<SearchOccurrence> FindOccurrences(
        string threadId,
        IReadOnlyList<MessageDto> messages,
        string needle,
        bool wholeWord)
    {
        var occurrences = new List<SearchOccurrence>();
        foreach (var message in messages)
        {
            if (string.IsNullOrEmpty(message.Content))
            {
                continue;
            }

            var start = 0;
            while (start < message.Content.Length)
            {
                var index = message.Content.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                if (!wholeWord || IsWholeWord(message.Content, index, needle.Length))
                {
                    occurrences.Add(new SearchOccurrence(threadId, message.Id, index, needle.Length));
                }

                start = index + needle.Length;
            }
        }

        return occurrences;
    }

    private static bool IsWholeWord(string content, int index, int length)
    {
        if (index > 0 && IsWordChar(content[index - 1]))
        {
            return false;
        }

        var end = index + length;
        return end >= content.Length || !IsWordChar(content[end]);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
