namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Tools-block for Wikipedia/Wikidata (spec: extract + short description, 0–800 tok).
/// </summary>
public static class WikiContext
{
    public const int MaxTotalChars = 2400;

    public static ChatTurn? TryTurn(IReadOnlyList<WikiArticle> articles, int maxTotalChars = MaxTotalChars)
    {
        if (articles.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("Wikipedia extracts for this question. Cite with markdown links [title](url). Do not mention this note unless asked.");

        var n = 0;
        foreach (var article in articles)
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
            block.Append(article.Title);
            if (!string.IsNullOrWhiteSpace(article.Description))
            {
                block.Append(" — ");
                block.Append(article.Description);
            }

            if (!string.IsNullOrWhiteSpace(article.WikidataId))
            {
                block.Append(" (");
                block.Append(article.WikidataId);
                block.Append(')');
            }

            block.Append('\n');
            block.Append(article.Url);
            if (!string.IsNullOrWhiteSpace(article.Extract))
            {
                block.Append('\n');
                block.Append(CollapseWhitespace(article.Extract));
            }

            AppendCapped(sb, block.ToString(), maxTotalChars);
        }

        return new ChatTurn("system", sb.ToString());
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
