namespace Aukenid.Core.Data;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Aukenid.Core.Contracts;

/// <summary>
/// Persists conversations as individual thread folders organized under a folder tree rooted
/// at <c>rootPath</c>. Each thread is its own directory containing a fixed-name markdown file
/// (<see cref="ThreadFileName"/>) plus an <see cref="AttachmentsDirectoryName"/> subfolder for
/// any images/attachments referenced by its messages, so deleting a thread's directory removes
/// everything that belongs to it in one shot. The "Temporal" folder is in-memory only and is
/// never written to disk, so its threads are lost when the app closes.
/// </summary>
public sealed class ConversationStore
{
    public const string GeneralFolder = "General";
    public const string TemporalFolder = "Temporal";
    private const string ThreadFileName = "conversation.md";
    private const string AttachmentsDirectoryName = "attachments";
    private const string DocumentFileName = "document.md";
    private const string DocumentVersionsDirectoryName = "document-versions";

    private readonly string _root;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, string> _threadFiles = new();
    private readonly Dictionary<string, (ThreadDto Thread, List<MessageDto> Messages)> _temporal = new();
    private readonly Dictionary<string, string> _temporalAttachmentDirs = new();
    private const long MaxAttachmentBytes = 50L * 1024 * 1024;

    public ConversationStore(string rootPath)
    {
        _root = rootPath;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, GeneralFolder));

        foreach (var file in Directory.EnumerateFiles(_root, ThreadFileName, SearchOption.AllDirectories))
        {
            var id = ConversationMarkdown.ReadThreadId(file);
            if (id is not null)
            {
                _threadFiles[id] = file;
            }
        }
    }

    public IReadOnlyList<FolderDto> ListFolders()
    {
        lock (_sync)
        {
            var folders = new List<FolderDto>
            {
                new(GeneralFolder, GeneralFolder, true),
                new(TemporalFolder, TemporalFolder, true),
            };

            foreach (var dir in EnumerateFolderDirectories(_root))
            {
                var relative = ToRelativePath(dir);
                if (relative == GeneralFolder)
                {
                    continue;
                }

                folders.Add(new FolderDto(relative, Path.GetFileName(dir), false));
            }

            return folders;
        }
    }

    public FolderDto CreateFolder(string parentPath, string name)
    {
        lock (_sync)
        {
            // Temporal has no backing directory; nest new folders under the root instead.
            if (IsTemporal(parentPath))
            {
                parentPath = string.Empty;
            }

            var parentDir = ResolveFolderDirectory(parentPath);
            Directory.CreateDirectory(parentDir);

            var baseName = FileNameCodec.Encode(name);
            var isRoot = string.IsNullOrEmpty(parentPath);
            var candidate = baseName;
            var counter = 2;
            while (Directory.Exists(Path.Combine(parentDir, candidate)) || (isRoot && IsReservedRootName(candidate)))
            {
                candidate = $"{baseName} ({counter++})";
            }

            var finalDir = Path.Combine(parentDir, candidate);
            Directory.CreateDirectory(finalDir);

            return new FolderDto(ToRelativePath(finalDir), candidate, false);
        }
    }

    public ThreadDto CreateThread(string title, string folderPath)
    {
        lock (_sync)
        {
            var thread = new ThreadDto(Guid.CreateVersion7().ToString(), title, folderPath, DateTimeOffset.UtcNow);

            if (IsTemporal(folderPath))
            {
                _temporal[thread.Id] = (thread, []);
                return thread;
            }

            var parentDir = ResolveFolderDirectory(folderPath);
            Directory.CreateDirectory(parentDir);

            var baseName = FileNameCodec.Encode(title);
            var candidate = baseName;
            var counter = 2;
            while (Directory.Exists(Path.Combine(parentDir, candidate)))
            {
                candidate = $"{baseName} ({counter++})";
            }

            var threadDir = Path.Combine(parentDir, candidate);
            Directory.CreateDirectory(threadDir);

            var filePath = Path.Combine(threadDir, ThreadFileName);
            File.WriteAllText(filePath, ConversationMarkdown.BuildHeader(thread));
            _threadFiles[thread.Id] = filePath;
            return thread;
        }
    }

    public IReadOnlyList<ThreadDto> ListThreads()
    {
        lock (_sync)
        {
            var threads = new List<ThreadDto>();

            foreach (var filePath in _threadFiles.Values)
            {
                var folderPath = ToRelativePath(Path.GetDirectoryName(Path.GetDirectoryName(filePath)!)!);
                var thread = ConversationMarkdown.ReadThread(filePath, folderPath);
                if (thread is not null)
                {
                    var hasMessages = File.ReadLines(filePath).Any(l => l.StartsWith("<!--msg ", StringComparison.Ordinal));
                    threads.Add(thread with { HasMessages = hasMessages });
                }
            }

            foreach (var (thread, messages) in _temporal.Values)
            {
                threads.Add(thread with { HasMessages = messages.Count > 0 });
            }

            return threads.OrderByDescending(t => t.Created).ToList();
        }
    }

    public void SaveMessage(MessageDto message)
    {
        lock (_sync)
        {
            if (_temporal.TryGetValue(message.ThreadId, out var entry))
            {
                entry.Messages.Add(message);
                return;
            }

            if (_threadFiles.TryGetValue(message.ThreadId, out var filePath))
            {
                File.AppendAllText(filePath, ConversationMarkdown.FormatMessageBlock(message));
            }
        }
    }

    public IReadOnlyList<MessageDto> ListMessages(string threadId)
    {
        lock (_sync)
        {
            if (_temporal.TryGetValue(threadId, out var entry))
            {
                return entry.Messages.ToList();
            }

            return _threadFiles.TryGetValue(threadId, out var filePath)
                ? ConversationMarkdown.ReadMessages(filePath, threadId)
                : [];
        }
    }

    /// <summary>
    /// Moves a user message and its direct reply (if any) out of <paramref name="threadId"/> into a
    /// brand-new thread in the same folder, instead of copying them (ADR-24): the topic-shift
    /// suggestion relocates the triggering exchange so the original thread stops carrying an
    /// unrelated turn. Returns null if the message is missing or is not a user message. A thin,
    /// pre-validated wrapper over <see cref="SplitThread"/> (shared with manual Split, ADR-11).
    /// </summary>
    public (ThreadDto Thread, IReadOnlyList<MessageDto> Messages)? MoveExchangeToNewThread(
        string threadId,
        string userMessageId,
        string provisionalTitle)
    {
        lock (_sync)
        {
            var userMessage = ListMessages(threadId).FirstOrDefault(m => m.Id == userMessageId);
            if (userMessage is null || !string.Equals(userMessage.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return SplitThread(threadId, userMessageId, provisionalTitle, includeHistory: false, removeFromSource: true);
        }
    }

    /// <summary>
    /// Shared primitive behind the topic-shift suggestion (ADR-24) and manual Split (ADR-11):
    /// copies or moves messages from <paramref name="threadId"/> into a brand-new thread in the
    /// same folder. With <paramref name="includeHistory"/> false, only <paramref name="leafMessageId"/>
    /// and its direct reply (if any) move - the topic-shift case. With it true, the whole active
    /// path from the root down to <paramref name="leafMessageId"/> is carried over - manual Split's
    /// "sibling thread whose prefix is root to M". <paramref name="removeFromSource"/> controls
    /// whether the source thread keeps those messages (Split leaves the original untouched) or not
    /// (topic-shift relocates them). Returns null if <paramref name="leafMessageId"/> is missing.
    /// </summary>
    public (ThreadDto Thread, IReadOnlyList<MessageDto> Messages)? SplitThread(
        string threadId,
        string leafMessageId,
        string title,
        bool includeHistory,
        bool removeFromSource)
    {
        lock (_sync)
        {
            var messages = ListMessages(threadId);
            var leaf = messages.FirstOrDefault(m => m.Id == leafMessageId);
            if (leaf is null)
            {
                return null;
            }

            var sourceThread = ListThreads().FirstOrDefault(t => t.Id == threadId);
            if (sourceThread is null)
            {
                return null;
            }

            var toMove = includeHistory ? ResolveActivePath(messages, leafMessageId) : BuildExchange(messages, leaf);
            if (toMove.Count == 0)
            {
                return null;
            }

            var newThread = CreateThread(title, sourceThread.FolderPath);

            // Relocate (move) or duplicate (copy) attachment files along with their message.
            var attachmentNames = toMove.SelectMany(m => m.Attachments ?? (IReadOnlyList<string>)[]).ToList();
            if (attachmentNames.Count > 0)
            {
                var sourceDir = PeekAttachmentsDirectory(threadId);
                var destDir = GetOrCreateAttachmentsDirectory(newThread.Id);
                if (sourceDir is not null && destDir is not null)
                {
                    foreach (var name in attachmentNames)
                    {
                        var sourcePath = Path.Combine(sourceDir, name);
                        if (!File.Exists(sourcePath))
                        {
                            continue;
                        }

                        var destPath = Path.Combine(destDir, name);
                        if (removeFromSource)
                        {
                            File.Move(sourcePath, destPath, overwrite: true);
                        }
                        else
                        {
                            File.Copy(sourcePath, destPath, overwrite: true);
                        }
                    }
                }
            }

            var movedMessages = new List<MessageDto>(toMove.Count);
            string? previousId = null;
            foreach (var message in toMove)
            {
                var moved = message with { ThreadId = newThread.Id, ParentId = previousId };
                SaveMessage(moved);
                movedMessages.Add(moved);
                previousId = moved.Id;
            }

            if (removeFromSource)
            {
                RemoveMessagesFromThread(threadId, toMove.Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
            }

            return (newThread with { HasMessages = true }, movedMessages);
        }
    }

    /// <summary>
    /// Cuts the tail after <paramref name="leafMessageId"/> (ADR-11 Prune): keeps only the active path
    /// from the thread's root down to and including that message, discarding every other message -
    /// discarded regenerations and anything that came after. Unlike <see cref="SplitThread"/>, this
    /// rewrites the thread in place instead of creating a new one. Attachment files that only belonged
    /// to discarded messages are deleted too, since there is no trash for them yet. Returns the
    /// surviving messages, or null if <paramref name="leafMessageId"/> is missing.
    /// </summary>
    public IReadOnlyList<MessageDto>? PruneThread(string threadId, string leafMessageId)
    {
        lock (_sync)
        {
            var messages = ListMessages(threadId);
            if (messages.All(m => m.Id != leafMessageId))
            {
                return null;
            }

            var keep = ResolveActivePath(messages, leafMessageId);
            var keepIds = keep.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            var discarded = messages.Where(m => !keepIds.Contains(m.Id)).ToList();
            if (discarded.Count == 0)
            {
                return keep;
            }

            var keptAttachments = keep.SelectMany(m => m.Attachments ?? (IReadOnlyList<string>)[]).ToHashSet(StringComparer.Ordinal);
            var attachmentsDir = PeekAttachmentsDirectory(threadId);
            if (attachmentsDir is not null)
            {
                var toDelete = discarded.SelectMany(m => m.Attachments ?? (IReadOnlyList<string>)[]).Where(name => !keptAttachments.Contains(name));
                foreach (var name in toDelete)
                {
                    var path = Path.Combine(attachmentsDir, name);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }

            RemoveMessagesFromThread(threadId, discarded.Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
            return keep;
        }
    }

    // The topic-shift exchange is just the trigger plus its direct reply, not the whole prefix.
    private static IReadOnlyList<MessageDto> BuildExchange(IReadOnlyList<MessageDto> messages, MessageDto trigger)
    {
        var reply = messages.FirstOrDefault(m => m.ParentId == trigger.Id);
        return reply is null ? [trigger] : [trigger, reply];
    }

    // Mirrors Engine.ActivePath.Resolve without taking a Data-layer dependency on the Engine project.
    private static List<MessageDto> ResolveActivePath(IReadOnlyList<MessageDto> messages, string leafId)
    {
        var byId = new Dictionary<string, MessageDto>(messages.Count);
        foreach (var message in messages)
        {
            byId[message.Id] = message;
        }

        if (!byId.TryGetValue(leafId, out var leaf))
        {
            return [];
        }

        var hasLinks = messages.Any(m => m.ParentId is not null && byId.ContainsKey(m.ParentId));
        if (!hasLinks)
        {
            var prefix = new List<MessageDto>(messages.Count);
            foreach (var message in messages)
            {
                prefix.Add(message);
                if (message.Id == leafId)
                {
                    break;
                }
            }

            return prefix;
        }

        var path = new List<MessageDto>();
        var seen = new HashSet<string>();
        MessageDto? current = leaf;
        while (current is not null && seen.Add(current.Id))
        {
            path.Add(current);
            current = current.ParentId is not null && byId.TryGetValue(current.ParentId, out var parent) ? parent : null;
        }

        path.Reverse();
        return path;
    }

    private void RemoveMessagesFromThread(string threadId, HashSet<string> idsToRemove)
    {
        lock (_sync)
        {
            if (_temporal.TryGetValue(threadId, out var entry))
            {
                entry.Messages.RemoveAll(m => idsToRemove.Contains(m.Id));
                return;
            }

            if (!_threadFiles.TryGetValue(threadId, out var filePath))
            {
                return;
            }

            var folderPath = ToRelativePath(Path.GetDirectoryName(Path.GetDirectoryName(filePath)!)!);
            var thread = ConversationMarkdown.ReadThread(filePath, folderPath);
            if (thread is null)
            {
                return;
            }

            var remaining = ConversationMarkdown.ReadMessages(filePath, threadId).Where(m => !idsToRemove.Contains(m.Id));
            var rewritten = ConversationMarkdown.BuildHeader(thread) + string.Concat(remaining.Select(ConversationMarkdown.FormatMessageBlock));
            File.WriteAllText(filePath, rewritten);
        }
    }

    /// <summary>
    /// One-lock snapshot of every persisted and in-memory conversation, for full-text search.
    /// </summary>
    internal IReadOnlyList<(ThreadDto Thread, IReadOnlyList<MessageDto> Messages)> ListConversations()
    {
        lock (_sync)
        {
            var conversations = new List<(ThreadDto Thread, IReadOnlyList<MessageDto> Messages)>();

            foreach (var filePath in _threadFiles.Values)
            {
                var folderPath = ToRelativePath(Path.GetDirectoryName(Path.GetDirectoryName(filePath)!)!);
                var thread = ConversationMarkdown.ReadThread(filePath, folderPath);
                if (thread is null)
                {
                    continue;
                }

                var messages = ConversationMarkdown.ReadMessages(filePath, thread.Id);
                conversations.Add((thread with { HasMessages = messages.Count > 0 }, messages));
            }

            foreach (var (thread, messages) in _temporal.Values)
            {
                conversations.Add((thread with { HasMessages = messages.Count > 0 }, messages.ToList()));
            }

            return conversations;
        }
    }

    /// <summary>Updates a thread's display title (e.g. derived from its first prompt). Does not rename its directory.</summary>
    public void RenameThread(string threadId, string title)
    {
        lock (_sync)
        {
            if (_temporal.TryGetValue(threadId, out var entry))
            {
                _temporal[threadId] = (entry.Thread with { Title = title }, entry.Messages);
                return;
            }

            if (!_threadFiles.TryGetValue(threadId, out var filePath))
            {
                return;
            }

            var lines = File.ReadAllLines(filePath);
            if (lines.Length > 1 && lines[1].StartsWith("# ", StringComparison.Ordinal))
            {
                lines[1] = $"# {title}";
                File.WriteAllLines(filePath, lines);
            }
        }
    }

    /// <summary>Deletes a thread and everything that belongs to it (conversation.md + attachments/).</summary>
    public void DeleteThread(string threadId)
    {
        lock (_sync)
        {
            DeleteThreadCore(threadId);
        }
    }

    /// <summary>Moves a thread to a different folder, including in/out of the in-memory Temporal folder.</summary>
    public void MoveThread(string threadId, string targetFolderPath)
    {
        lock (_sync)
        {
            MoveThreadCore(threadId, targetFolderPath);
        }
    }

    /// <summary>Renames a custom folder's directory. Not available for the standard General/Temporal folders.</summary>
    public FolderDto? RenameFolder(string folderPath, string newName)
    {
        lock (_sync)
        {
            if (!IsCustomFolder(folderPath))
            {
                return null;
            }

            var oldDir = ResolveFolderDirectory(folderPath);
            if (!Directory.Exists(oldDir))
            {
                return null;
            }

            var parentDir = Path.GetDirectoryName(oldDir)!;
            var newDirName = UniqueName(parentDir, FileNameCodec.Encode(newName));
            var newDir = Path.Combine(parentDir, newDirName);
            Directory.Move(oldDir, newDir);

            // Thread file paths are absolute, so they need to be patched to point under the new directory.
            foreach (var id in _threadFiles.Keys.ToList())
            {
                var path = _threadFiles[id];
                if (path.StartsWith(oldDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    _threadFiles[id] = newDir + path[oldDir.Length..];
                }
            }

            return new FolderDto(ToRelativePath(newDir), newDirName, false);
        }
    }

    /// <summary>Deletes a custom folder and every thread inside it. Not available for General/Temporal.</summary>
    public void DeleteFolder(string folderPath)
    {
        lock (_sync)
        {
            if (!IsCustomFolder(folderPath))
            {
                return;
            }

            foreach (var id in ThreadIdsInFolder(folderPath))
            {
                DeleteThreadCore(id);
            }

            var dir = ResolveFolderDirectory(folderPath);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>Deletes every thread inside a folder but keeps the folder itself.</summary>
    public void EmptyFolder(string folderPath)
    {
        lock (_sync)
        {
            foreach (var id in ThreadIdsInFolder(folderPath))
            {
                DeleteThreadCore(id);
            }
        }
    }

    /// <summary>Moves every thread from one folder into another. Deletes the (now empty) source folder unless it's standard.</summary>
    public void MergeFolder(string sourceFolderPath, string targetFolderPath)
    {
        lock (_sync)
        {
            foreach (var id in ThreadIdsInFolder(sourceFolderPath))
            {
                MoveThreadCore(id, targetFolderPath);
            }

            if (!IsCustomFolder(sourceFolderPath))
            {
                return;
            }

            var dir = ResolveFolderDirectory(sourceFolderPath);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
    }

    private void DeleteThreadCore(string threadId)
    {
        if (_temporal.Remove(threadId))
        {
            RemoveTemporalAttachments(threadId);
            return;
        }

        if (_threadFiles.TryGetValue(threadId, out var filePath))
        {
            Directory.Delete(Path.GetDirectoryName(filePath)!, recursive: true);
            _threadFiles.Remove(threadId);
        }
    }

    private void MoveThreadCore(string threadId, string targetFolderPath)
    {
        if (IsTemporal(targetFolderPath))
        {
            MoveToTemporal(threadId);
            return;
        }

        if (_temporal.TryGetValue(threadId, out var temporalEntry))
        {
            PersistTemporalThread(threadId, temporalEntry, targetFolderPath);
            return;
        }

        if (_threadFiles.TryGetValue(threadId, out var filePath))
        {
            MovePersistedThread(threadId, filePath, targetFolderPath);
        }
    }

    private List<string> ThreadIdsInFolder(string folderPath)
    {
        var ids = new List<string>();

        foreach (var (id, filePath) in _threadFiles)
        {
            var threadFolder = ToRelativePath(Path.GetDirectoryName(Path.GetDirectoryName(filePath)!)!);
            if (threadFolder == folderPath)
            {
                ids.Add(id);
            }
        }

        foreach (var (id, entry) in _temporal)
        {
            if (entry.Thread.FolderPath == folderPath)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private void MoveToTemporal(string threadId)
    {
        if (_temporal.ContainsKey(threadId) || !_threadFiles.TryGetValue(threadId, out var filePath))
        {
            return;
        }

        var thread = ConversationMarkdown.ReadThread(filePath, TemporalFolder);
        if (thread is null)
        {
            return;
        }

        var messages = ConversationMarkdown.ReadMessages(filePath, threadId).ToList();
        var currentThreadDir = Path.GetDirectoryName(filePath)!;
        var sourceAttachments = Path.Combine(currentThreadDir, AttachmentsDirectoryName);
        if (Directory.Exists(sourceAttachments) && Directory.EnumerateFileSystemEntries(sourceAttachments).Any())
        {
            var dest = CreateTemporalAttachmentsDirectory(threadId);
            CopyDirectoryContents(sourceAttachments, dest);
        }

        Directory.Delete(currentThreadDir, recursive: true);
        _threadFiles.Remove(threadId);
        _temporal[threadId] = (thread with { FolderPath = TemporalFolder }, messages);
    }

    private void PersistTemporalThread(string threadId, (ThreadDto Thread, List<MessageDto> Messages) entry, string targetFolderPath)
    {
        var parentDir = ResolveFolderDirectory(targetFolderPath);
        Directory.CreateDirectory(parentDir);

        var threadDir = Path.Combine(parentDir, UniqueName(parentDir, FileNameCodec.Encode(entry.Thread.Title)));
        Directory.CreateDirectory(threadDir);

        var filePath = Path.Combine(threadDir, ThreadFileName);
        File.WriteAllText(filePath, ConversationMarkdown.BuildHeader(entry.Thread with { FolderPath = targetFolderPath }));
        foreach (var message in entry.Messages)
        {
            File.AppendAllText(filePath, ConversationMarkdown.FormatMessageBlock(message));
        }

        if (_temporalAttachmentDirs.TryGetValue(threadId, out var sourceAttachments) && Directory.Exists(sourceAttachments))
        {
            var dest = Path.Combine(threadDir, AttachmentsDirectoryName);
            Directory.CreateDirectory(dest);
            CopyDirectoryContents(sourceAttachments, dest);
        }

        _threadFiles[threadId] = filePath;
        _temporal.Remove(threadId);
        RemoveTemporalAttachments(threadId);
    }

    private void MovePersistedThread(string threadId, string filePath, string targetFolderPath)
    {
        var currentThreadDir = Path.GetDirectoryName(filePath)!;
        var parentDir = ResolveFolderDirectory(targetFolderPath);
        Directory.CreateDirectory(parentDir);

        var destDir = Path.Combine(parentDir, UniqueName(parentDir, Path.GetFileName(currentThreadDir)));
        Directory.Move(currentThreadDir, destDir);
        _threadFiles[threadId] = Path.Combine(destDir, ThreadFileName);
    }

    private static string UniqueName(string parentDir, string baseName)
    {
        var candidate = baseName;
        var counter = 2;
        while (Directory.Exists(Path.Combine(parentDir, candidate)))
        {
            candidate = $"{baseName} ({counter++})";
        }

        return candidate;
    }

    private static string UniqueFile(string parentDir, string fileName)
    {
        var dest = Path.Combine(parentDir, fileName);
        if (!File.Exists(dest))
        {
            return dest;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var counter = 2;
        do
        {
            dest = Path.Combine(parentDir, $"{name} ({counter++}){ext}");
        }
        while (File.Exists(dest));

        return dest;
    }

    private string? GetOrCreateAttachmentsDirectory(string threadId)
    {
        if (_temporal.ContainsKey(threadId))
        {
            return CreateTemporalAttachmentsDirectory(threadId);
        }

        if (!_threadFiles.TryGetValue(threadId, out var filePath))
        {
            return null;
        }

        var dir = Path.Combine(Path.GetDirectoryName(filePath)!, AttachmentsDirectoryName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string? PeekAttachmentsDirectory(string threadId)
    {
        if (_temporalAttachmentDirs.TryGetValue(threadId, out var temporalDir))
        {
            return temporalDir;
        }

        if (!_threadFiles.TryGetValue(threadId, out var filePath))
        {
            return null;
        }

        var dir = Path.Combine(Path.GetDirectoryName(filePath)!, AttachmentsDirectoryName);
        return Directory.Exists(dir) ? dir : null;
    }

    private string CreateTemporalAttachmentsDirectory(string threadId)
    {
        if (_temporalAttachmentDirs.TryGetValue(threadId, out var existing))
        {
            Directory.CreateDirectory(existing);
            return existing;
        }

        var dir = Path.Combine(Path.GetTempPath(), "Aukenid", "temporal", threadId, AttachmentsDirectoryName);
        Directory.CreateDirectory(dir);
        _temporalAttachmentDirs[threadId] = dir;
        return dir;
    }

    private void RemoveTemporalAttachments(string threadId)
    {
        if (!_temporalAttachmentDirs.Remove(threadId, out var dir) || !Directory.Exists(dir))
        {
            return;
        }

        try
        {
            var threadDir = Path.GetDirectoryName(dir);
            if (threadDir is not null && Directory.Exists(threadDir))
            {
                Directory.Delete(threadDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Session temp; leftover files are discarded on next reboot of the machine anyway.
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, UniqueFile(destDir, Path.GetFileName(file)), overwrite: false);
        }
    }

    /// <summary>Directory for images/attachments referenced by this thread's messages, creating it on first use.</summary>
    public string? GetAttachmentsDirectory(string threadId)
    {
        lock (_sync)
        {
            return GetOrCreateAttachmentsDirectory(threadId);
        }
    }

    /// <summary>Copies a source file into the thread's attachments folder. Returns the stored file name, or null if the thread is unknown.</summary>
    public string? SaveAttachment(string threadId, string sourcePath)
    {
        lock (_sync)
        {
            if (!File.Exists(sourcePath))
            {
                return null;
            }

            var destDir = GetOrCreateAttachmentsDirectory(threadId);
            if (destDir is null)
            {
                return null;
            }

            var info = new FileInfo(sourcePath);
            if (info.Length > MaxAttachmentBytes)
            {
                return null;
            }

            var encoded = FileNameCodec.Encode(Path.GetFileName(sourcePath));
            var destPath = UniqueFile(destDir, encoded);
            File.Copy(sourcePath, destPath, overwrite: false);
            return Path.GetFileName(destPath);
        }
    }

    /// <summary>Absolute path of a stored attachment, or null if missing or the name is not a simple file name.</summary>
    public string? TryGetAttachmentPath(string threadId, string storedName)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(storedName) || storedName != Path.GetFileName(storedName))
            {
                return null;
            }

            var dir = PeekAttachmentsDirectory(threadId);
            if (dir is null)
            {
                return null;
            }

            var full = Path.GetFullPath(Path.Combine(dir, storedName));
            var root = Path.GetFullPath(dir);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !full.Equals(root, StringComparison.Ordinal))
            {
                return null;
            }

            return File.Exists(full) ? full : null;
        }
    }

    /// <summary>
    /// The thread's actual on-disk directory name, or null for a Temporal (in-memory) thread. Renaming a
    /// thread only rewrites its title inside conversation.md (see <see cref="RenameThread"/>), so this can
    /// differ from `FileNameCodec.Encode(title)` once a thread has been renamed since it was created.
    /// </summary>
    public string? GetThreadFolderName(string threadId)
    {
        lock (_sync)
        {
            return _threadFiles.TryGetValue(threadId, out var filePath)
                ? Path.GetFileName(Path.GetDirectoryName(filePath))
                : null;
        }
    }

    /// <summary>Markdown content of this thread's document, or null if none exists yet or the thread is unknown.</summary>
    public string? ReadDocument(string threadId)
    {
        lock (_sync)
        {
            var dir = GetThreadDirectory(threadId);
            if (dir is null)
            {
                return null;
            }

            var path = Path.Combine(dir, DocumentFileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    /// <summary>Writes the document's markdown content next to the thread's conversation file. Returns false if the thread is unknown.</summary>
    public bool SaveDocument(string threadId, string content)
    {
        lock (_sync)
        {
            var dir = GetThreadDirectory(threadId);
            if (dir is null)
            {
                return false;
            }

            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, DocumentFileName), content);
            return true;
        }
    }

    /// <summary>Snapshots the current document into its version history (e.g. before/after an assisted edit). Returns null if there is no document yet.</summary>
    public DocumentVersionDto? SnapshotDocumentVersion(string threadId, string label)
    {
        lock (_sync)
        {
            var dir = GetThreadDirectory(threadId);
            return dir is null ? null : SnapshotDocumentVersionCore(dir, label);
        }
    }

    /// <summary>Version history for this thread's document, newest first.</summary>
    public IReadOnlyList<DocumentVersionDto> ListDocumentVersions(string threadId)
    {
        lock (_sync)
        {
            var dir = GetThreadDirectory(threadId);
            var versionsDir = dir is null ? null : Path.Combine(dir, DocumentVersionsDirectoryName);
            if (versionsDir is null || !Directory.Exists(versionsDir))
            {
                return [];
            }

            var versions = new List<DocumentVersionDto>();
            foreach (var file in Directory.EnumerateFiles(versionsDir, "*.md"))
            {
                var parsed = DocumentMarkdown.ReadVersion(file);
                if (parsed is not null)
                {
                    versions.Add(new DocumentVersionDto(parsed.Value.Id, parsed.Value.Label, parsed.Value.Created));
                }
            }

            versions.Sort((a, b) => b.Created.CompareTo(a.Created));
            return versions;
        }
    }

    /// <summary>Reads a past version's content without restoring it, or null if the id is unknown.</summary>
    public string? ReadDocumentVersion(string threadId, string versionId)
    {
        lock (_sync)
        {
            var versionPath = ResolveVersionPath(threadId, versionId);
            return versionPath is null ? null : DocumentMarkdown.ReadVersion(versionPath)?.Content;
        }
    }

    /// <summary>Restores a prior version as the current document, after snapshotting the current content so the restore itself is undoable.</summary>
    public bool RestoreDocumentVersion(string threadId, string versionId)
    {
        lock (_sync)
        {
            var versionPath = ResolveVersionPath(threadId, versionId);
            var parsed = versionPath is null ? null : DocumentMarkdown.ReadVersion(versionPath);
            if (parsed is null)
            {
                return false;
            }

            var dir = GetThreadDirectory(threadId)!;
            SnapshotDocumentVersionCore(dir, "before-restore");
            File.WriteAllText(Path.Combine(dir, DocumentFileName), parsed.Value.Content);
            return true;
        }
    }

    private DocumentVersionDto? SnapshotDocumentVersionCore(string dir, string label)
    {
        var docPath = Path.Combine(dir, DocumentFileName);
        if (!File.Exists(docPath))
        {
            return null;
        }

        var versionsDir = Path.Combine(dir, DocumentVersionsDirectoryName);
        Directory.CreateDirectory(versionsDir);

        var id = Guid.CreateVersion7().ToString();
        var created = DateTimeOffset.UtcNow;
        var content = File.ReadAllText(docPath);
        File.WriteAllText(Path.Combine(versionsDir, id + ".md"), DocumentMarkdown.BuildVersionFile(id, label, created, content));
        return new DocumentVersionDto(id, label, created);
    }

    // Guards against a version id that escapes the versions directory (path traversal), same check as TryGetAttachmentPath.
    private string? ResolveVersionPath(string threadId, string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId != Path.GetFileName(versionId))
        {
            return null;
        }

        var dir = GetThreadDirectory(threadId);
        if (dir is null)
        {
            return null;
        }

        var versionPath = Path.Combine(dir, DocumentVersionsDirectoryName, versionId + ".md");
        return File.Exists(versionPath) ? versionPath : null;
    }

    /// <summary>This thread's own directory (conversation.md's folder, or a temp folder for a Temporal thread), or null if unknown.</summary>
    private string? GetThreadDirectory(string threadId)
    {
        if (_threadFiles.TryGetValue(threadId, out var filePath))
        {
            return Path.GetDirectoryName(filePath);
        }

        return _temporal.ContainsKey(threadId)
            ? Path.Combine(Path.GetTempPath(), "Aukenid", "temporal", threadId)
            : null;
    }

    public static bool IsCustomFolder(string folderPath) =>
        !IsGeneral(folderPath) && !IsTemporal(folderPath);

    private static bool IsTemporal(string folderPath) =>
        folderPath.Equals(TemporalFolder, StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneral(string folderPath) =>
        folderPath.Equals(GeneralFolder, StringComparison.OrdinalIgnoreCase);

    private static bool IsReservedRootName(string candidate) =>
        candidate.Equals(GeneralFolder, StringComparison.OrdinalIgnoreCase)
        || candidate.Equals(TemporalFolder, StringComparison.OrdinalIgnoreCase);

    private static bool IsThreadDirectory(string dir) => File.Exists(Path.Combine(dir, ThreadFileName));

    // Thread directories are leaves in the folder tree (their attachments subfolder is an
    // implementation detail, not a user-facing folder), so don't recurse into them here.
    private static IEnumerable<string> EnumerateFolderDirectories(string dir)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (IsThreadDirectory(sub))
            {
                continue;
            }

            yield return sub;
            foreach (var nested in EnumerateFolderDirectories(sub))
            {
                yield return nested;
            }
        }
    }

    private string ToRelativePath(string absolutePath) =>
        Path.GetRelativePath(_root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    private string ResolveFolderDirectory(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
        {
            return _root;
        }

        var segments = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([_root, .. segments]);
    }
}
