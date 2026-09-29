namespace Aukenid.Core.Data;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Aukenid.Core.Contracts;

public sealed record BookmarkItem(string MessageId, string Text);

public sealed record BookmarkThread(
    string Id,
    string Title,
    string FolderPath,
    DateTimeOffset Created,
    IReadOnlyList<BookmarkItem> Bookmarks);

public sealed record BookmarkList(
    IReadOnlyList<FolderDto> Folders,
    IReadOnlyList<BookmarkThread> Threads);

/// <summary>
/// Persists pointers to user prompts and lists them as a folder → thread → prompt tree.
/// </summary>
public sealed class BookmarkStore
{
    public const int SnippetLength = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly ConversationStore _conversations;
    private readonly Lock _sync = new();
    private List<BookmarkPointer> _items = [];
    private bool _loaded;

    public BookmarkStore(string path, ConversationStore conversations)
    {
        _path = path;
        _conversations = conversations;
    }

    public bool Toggle(string threadId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        lock (_sync)
        {
            Load();
            var existing = _items.FindIndex(item =>
                item.ThreadId == threadId && item.MessageId == messageId);
            if (existing >= 0)
            {
                _items.RemoveAt(existing);
                Save();
                return false;
            }

            if (!HasUserPrompt(threadId, messageId))
            {
                return false;
            }

            _items.Add(new BookmarkPointer(threadId, messageId));
            Save();
            return true;
        }
    }

    public bool IsBookmarked(string threadId, string messageId)
    {
        lock (_sync)
        {
            Load();
            return _items.Any(item => item.ThreadId == threadId && item.MessageId == messageId);
        }
    }

    public BookmarkList List()
    {
        lock (_sync)
        {
            Load();

            var conversations = _conversations.ListConversations()
                .ToDictionary(entry => entry.Thread.Id, StringComparer.Ordinal);
            var foldersByPath = _conversations.ListFolders()
                .ToDictionary(folder => folder.Path, StringComparer.Ordinal);

            var kept = new List<BookmarkPointer>();
            var grouped = new Dictionary<string, List<(BookmarkItem Item, DateTimeOffset Created)>>(StringComparer.Ordinal);

            foreach (var pointer in _items)
            {
                if (!conversations.TryGetValue(pointer.ThreadId, out var conversation))
                {
                    continue;
                }

                var message = conversation.Messages.FirstOrDefault(item => item.Id == pointer.MessageId);
                if (message is null || !IsUserPrompt(message.Role))
                {
                    continue;
                }

                kept.Add(pointer);
                if (!grouped.TryGetValue(pointer.ThreadId, out var items))
                {
                    items = [];
                    grouped[pointer.ThreadId] = items;
                }

                items.Add((new BookmarkItem(message.Id, Snippet(message.Content)), message.Created));
            }

            if (kept.Count != _items.Count)
            {
                _items = kept;
                Save();
            }

            var threads = new List<BookmarkThread>();
            var folders = new Dictionary<string, FolderDto>(StringComparer.Ordinal);

            foreach (var (threadId, items) in grouped)
            {
                var thread = conversations[threadId].Thread;
                var bookmarks = items
                    .OrderBy(item => item.Created)
                    .Select(item => item.Item)
                    .ToList();
                threads.Add(new BookmarkThread(thread.Id, thread.Title, thread.FolderPath, thread.Created, bookmarks));

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

            return new BookmarkList(orderedFolders, orderedThreads);
        }
    }

    private bool HasUserPrompt(string threadId, string messageId)
    {
        var message = _conversations.ListMessages(threadId).FirstOrDefault(item => item.Id == messageId);
        return message is not null && IsUserPrompt(message.Role);
    }

    private static bool IsUserPrompt(string role) =>
        string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);

    internal static string Snippet(string content)
    {
        var text = string.Join(' ', (content ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return "\u2026";
        }

        return text.Length <= SnippetLength ? text : text[..SnippetLength];
    }

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), JsonOptions);
            if (parsed?.Items is { Count: > 0 })
            {
                _items = parsed.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.ThreadId) && !string.IsNullOrWhiteSpace(item.MessageId))
                    .Select(item => new BookmarkPointer(item.ThreadId, item.MessageId))
                    .ToList();
            }
        }
        catch (JsonException)
        {
            _items = [];
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var model = new FileModel(_items
            .Select(item => new FileItem(item.ThreadId, item.MessageId))
            .ToList());
        File.WriteAllText(_path, JsonSerializer.Serialize(model, JsonOptions));
    }

    private sealed record BookmarkPointer(string ThreadId, string MessageId);

    private sealed record FileItem(string ThreadId, string MessageId);

    private sealed record FileModel(List<FileItem> Items);
}
