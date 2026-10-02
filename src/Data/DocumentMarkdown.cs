namespace Aukenid.Core.Data;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>Reads and writes the markdown format used to persist one document version snapshot.</summary>
internal static class DocumentMarkdown
{
    public static string BuildVersionFile(string id, string label, DateTimeOffset created, string content) =>
        $"<!--version id={id} label={Uri.EscapeDataString(label)} created={created:O}-->{Environment.NewLine}{content}";

    public static (string Id, string Label, DateTimeOffset Created, string Content)? ReadVersion(string filePath)
    {
        using var reader = new StreamReader(filePath);
        var firstLine = reader.ReadLine();
        var attrs = firstLine is null ? null : ParseAttributes(firstLine, "<!--version ");
        if (attrs is null
            || !attrs.TryGetValue("id", out var id)
            || !attrs.TryGetValue("created", out var createdRaw))
        {
            return null;
        }

        var label = attrs.TryGetValue("label", out var labelRaw) ? Uri.UnescapeDataString(labelRaw) : string.Empty;
        var created = DateTimeOffset.Parse(createdRaw, CultureInfo.InvariantCulture);
        var content = reader.ReadToEnd();
        return (id, label, created, content);
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
