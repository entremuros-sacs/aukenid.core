namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Tools-block for papers (spec: abstract / TL;DR / metadata only, 0–800 tok).
/// OA PDFs are not fetched here; paywalls are not proxied.
/// </summary>
public static partial class ScholarContext
{
    public const int MaxTotalChars = 2400;

    [GeneratedRegex(@"10\.\d{4,9}/[-._;()/:A-Z0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoiPattern();

    public static ChatTurn? TryTurn(IReadOnlyList<ScholarPaper> papers, int maxTotalChars = MaxTotalChars)
    {
        if (papers.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("Academic papers for this question. Cite with markdown links [title](url). Prefer TL;DR over guessing. Do not mention this note unless asked.");

        var n = 0;
        foreach (var paper in papers)
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
            block.Append(paper.Title);
            if (paper.Year is not null)
            {
                block.Append(" (");
                block.Append(paper.Year);
                block.Append(')');
            }

            if (!string.IsNullOrWhiteSpace(paper.Authors))
            {
                block.Append('\n');
                block.Append(paper.Authors);
            }

            if (!string.IsNullOrWhiteSpace(paper.Venue))
            {
                block.Append(" — ");
                block.Append(paper.Venue);
            }

            if (paper.CitationCount is > 0)
            {
                block.Append(" · ");
                block.Append(paper.CitationCount);
                block.Append(" citations");
            }

            if (!string.IsNullOrWhiteSpace(paper.Doi))
            {
                block.Append("\nDOI: ");
                block.Append(paper.Doi);
            }

            if (!string.IsNullOrWhiteSpace(paper.Url))
            {
                block.Append('\n');
                block.Append(paper.Url);
            }

            if (paper.IsOpenAccess || !string.IsNullOrWhiteSpace(paper.PdfUrl))
            {
                block.Append("\nOpen access");
                if (!string.IsNullOrWhiteSpace(paper.PdfUrl))
                {
                    block.Append(": ");
                    block.Append(paper.PdfUrl);
                }
            }

            var summary = !string.IsNullOrWhiteSpace(paper.Tldr) ? paper.Tldr : paper.Abstract;
            if (!string.IsNullOrWhiteSpace(summary))
            {
                block.Append('\n');
                block.Append(CollapseWhitespace(summary));
            }

            AppendCapped(sb, block.ToString(), maxTotalChars);
        }

        return new ChatTurn("system", sb.ToString());
    }

    public static string? FirstDoi(string text)
    {
        var match = DoiPattern().Match(text);
        return match.Success ? match.Value.TrimEnd('.') : null;
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
