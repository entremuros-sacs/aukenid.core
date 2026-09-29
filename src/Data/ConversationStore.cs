namespace Aukenid.Core.Data;

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
