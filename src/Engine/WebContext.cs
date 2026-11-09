namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Tools-block injected before the current user question (spec: web RAG / tools, 0–800 tok).
/// Search hits plus at most one local fetch. HTML never enters this string.
/// </summary>
public static partial class WebContext
{
    public const int MaxTotalChars = 3200;

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrl();

    // Bare domains (optionally with a path) for prompts like "look at aukenid.com".
    [GeneratedRegex(@"\b([A-Za-z0-9][A-Za-z0-9-]{0,62}\.[A-Za-z]{2,3}(?:/[^\s<>""']*)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareDomain();

    public static ChatTurn? TryTurn(
        IReadOnlyList<WebHit> hits,
        string? fetchedUrl,
        string? fetchedText,
        int maxTotalChars = MaxTotalChars)
    {
        if (hits.Count == 0 && string.IsNullOrWhiteSpace(fetchedText))
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append(PromptLibrary.Get(PromptKey.WebGroundingNote));

        var n = 0;
        foreach (var hit in hits)
        {
            if (sb.Length >= maxTotalChars - 24)
            {
                break;
            }

            n++;
            var block = new StringBuilder();
            block.Append("\n\n");
            block.Append(n);
            block.Append(". ");
            block.Append(hit.Title);
            if (!string.IsNullOrWhiteSpace(hit.Date))
            {
                block.Append(" (");
                block.Append(hit.Date);
                block.Append(')');
            }

            block.Append('\n');
            block.Append(hit.Url);
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
            {
                block.Append('\n');
                block.Append(CollapseWhitespace(hit.Snippet));
            }

            AppendCapped(sb, block.ToString(), maxTotalChars);
        }

        if (!string.IsNullOrWhiteSpace(fetchedText) && !string.IsNullOrWhiteSpace(fetchedUrl) && sb.Length < maxTotalChars - 24)
        {
            AppendCapped(sb, "\n\nFetched page: " + fetchedUrl + "\n" + CollapseWhitespace(fetchedText), maxTotalChars);
        }

        return sb.Length == 0 ? null : new ChatTurn("system", sb.ToString());
    }

    public static string? FirstPastedHttpUrl(string prompt)
    {
        var match = HttpUrl().Match(prompt);
        return match.Success ? match.Value.TrimEnd(").,;".ToCharArray()) : null;
    }

    public static string? FirstPastedHttpsUrlFromBareDomain(string prompt)
    {
        // If the user already pasted a full URL, don't second-guess it.
        if (FirstPastedHttpUrl(prompt) is not null)
        {
            return null;
        }

        var match = BareDomain().Match(prompt);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value.TrimEnd(").,;:".ToCharArray());
        if (value.Length == 0)
        {
            return null;
        }

        // Avoid matching emails (the regex would match the domain part).
        var at = prompt.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            var domainStart = match.Index;
            if (at < domainStart && domainStart - at <= 64)
            {
                return null;
            }
        }

        return "https://" + value;
    }

    private static void AppendCapped(StringBuilder sb, string text, int maxTotalChars)
    {
        var room = maxTotalChars - sb.Length;
        if (room <= 0)
        {
            return;
        }

        if (text.Length <= room)
        {
            sb.Append(text);
            return;
        }

        if (room <= 1)
        {
            return;
        }

        sb.Append(text.AsSpan(0, room - 1));
        sb.Append('\u2026');
    }

    private static string CollapseWhitespace(string content) =>
        string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}
