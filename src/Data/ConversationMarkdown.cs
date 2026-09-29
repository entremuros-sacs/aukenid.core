namespace Aukenid.Core.Data;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Aukenid.Core.Contracts;

/// <summary>Reads and writes the markdown format used to persist a single conversation.</summary>
internal static class ConversationMarkdown
{
    public static string BuildHeader(ThreadDto thread) =>
        $"<!--thread id={thread.Id} created={thread.Created:O}-->{Environment.NewLine}# {thread.Title}{Environment.NewLine}{Environment.NewLine}";

    public static string FormatMessageBlock(MessageDto message)
    {
        var parent = message.ParentId ?? "none";
        var roleHeading = message.Role.Length > 0
            ? char.ToUpperInvariant(message.Role[0]) + message.Role[1..]
            : message.Role;
        var attachments = message.Attachments is { Count: > 0 }
            ? " attachments=" + string.Join('|', message.Attachments.Select(Uri.EscapeDataString))
            : string.Empty;

        return $"<!--msg id={message.Id} parent={parent} role={message.Role} created={message.Created:O}{attachments}-->{Environment.NewLine}## {roleHeading}{Environment.NewLine}{Environment.NewLine}{message.Content}{Environment.NewLine}{Environment.NewLine}";
    }

    public static string? ReadThreadId(string filePath)
    {
        using var reader = new StreamReader(filePath);
        var firstLine = reader.ReadLine();
        return firstLine is null ? null : ParseAttributes(firstLine, "<!--thread ")?.GetValueOrDefault("id");
    }

    public static ThreadDto? ReadThread(string filePath, string folderPath)
    {
        var lines = File.ReadAllLines(filePath);
        if (lines.Length == 0)
        {
            return null;
        }

        var attrs = ParseAttributes(lines[0], "<!--thread ");
        if (attrs is null || !attrs.TryGetValue("id", out var id) || !attrs.TryGetValue("created", out var createdRaw))
        {
            return null;
        }

        var title = lines.Length > 1 && lines[1].StartsWith("# ", StringComparison.Ordinal)
            ? lines[1][2..]
            : Path.GetFileNameWithoutExtension(filePath);

        var created = DateTimeOffset.Parse(createdRaw, CultureInfo.InvariantCulture);
        return new ThreadDto(id, title, folderPath, created);
    }

    public static IReadOnlyList<MessageDto> ReadMessages(string filePath, string threadId)
    {
        var lines = File.ReadAllLines(filePath);
        var messages = new List<MessageDto>();

        var i = 0;
        while (i < lines.Length && !lines[i].StartsWith("<!--msg ", StringComparison.Ordinal))
        {
            i++;
        }

        while (i < lines.Length)
        {
            var attrs = ParseAttributes(lines[i], "<!--msg ");
            i++;

            if (i < lines.Length && lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                i++;
            }

            var contentLines = new List<string>();
            while (i < lines.Length && !lines[i].StartsWith("<!--msg ", StringComparison.Ordinal))
            {
                contentLines.Add(lines[i]);
                i++;
            }

            if (attrs is null
                || !attrs.TryGetValue("id", out var id)
                || !attrs.TryGetValue("role", out var role)
                || !attrs.TryGetValue("created", out var createdRaw))
            {
                continue;
            }

            attrs.TryGetValue("parent", out var parent);
            var content = string.Join(Environment.NewLine, contentLines).Trim();
            var created = DateTimeOffset.Parse(createdRaw, CultureInfo.InvariantCulture);
            IReadOnlyList<string>? attachments = null;
            if (attrs.TryGetValue("attachments", out var attachmentsRaw) && !string.IsNullOrWhiteSpace(attachmentsRaw))
            {
                attachments = attachmentsRaw
                    .Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.UnescapeDataString)
                    .ToArray();
            }

            messages.Add(new MessageDto(id, threadId, parent == "none" ? null : parent, role, content, created, attachments));
        }

        return messages;
    }

    private static Dictionary<string, string>? ParseAttributes(string line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith("-->", StringComparison.Ordinal))
        {
            return null;
        }

        var body = line[prefix.Length..^"-->".Length];
        var attrs = new Dictionary<string, string>();
        foreach (var token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = token.IndexOf('=');
            if (separatorIndex > 0)
            {
                attrs[token[..separatorIndex]] = token[(separatorIndex + 1)..];
            }
        }

        return attrs;
    }
}
