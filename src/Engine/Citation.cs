namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public readonly record struct Citation(string Title, string Url);

public static class SourceCitations
{
    private const int DefaultMaxSources = 5;

    public static IReadOnlyList<Citation> FromPapers(IEnumerable<ScholarPaper> papers)
    {
        var list = new List<Citation>();
        foreach (var paper in papers)
        {
            var url = FirstUrl(paper.PdfUrl, paper.Url, paper.Doi is null ? null : "https://doi.org/" + paper.Doi);
            if (url is not null)
            {
                list.Add(new Citation(paper.Title, url));
            }
        }

        return list;
    }

    public static IReadOnlyList<Citation> FromArticles(IEnumerable<WikiArticle> articles) =>
        articles
            .Where(a => !string.IsNullOrWhiteSpace(a.Url))
            .Select(a => new Citation(a.Title, a.Url))
            .ToList();

    public static IReadOnlyList<Citation> FromHits(IEnumerable<WebHit> hits) =>
        hits
            .Where(h => !string.IsNullOrWhiteSpace(h.Url))
            .Select(h => new Citation(h.Title, h.Url))
            .ToList();

    public static IReadOnlyList<Citation> FilterByMentionedInAnswer(string answerText, IReadOnlyList<Citation> citations, int maxSources = DefaultMaxSources)
    {
        if (citations.Count == 0)
        {
            return citations;
        }

        if (string.IsNullOrWhiteSpace(answerText))
        {
            return Array.Empty<Citation>();
        }

        var answer = answerText.ToLowerInvariant();
        var kept = new List<(Citation Citation, int Index)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < citations.Count; index++)
        {
            var citation = citations[index];
            if (string.IsNullOrWhiteSpace(citation.Url) || !seen.Add(citation.Url))
            {
                continue;
            }

            if (IsMentioned(answer, citation))
            {
                kept.Add((citation, index));
            }
        }

        return kept
            .OrderBy(k => k.Index)
            .Take(maxSources)
            .Select(k => k.Citation)
            .ToList();
    }

    public static string Append(string content, IReadOnlyList<Citation> citations)
    {
        if (citations.Count == 0)
        {
            return content;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(content))
        {
            sb.Append(content.TrimEnd());
            sb.Append("\n\n");
        }

        sb.Append("**Sources**\n");
        foreach (var citation in citations)
        {
            if (string.IsNullOrWhiteSpace(citation.Url) || !seen.Add(citation.Url))
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(citation.Title) ? citation.Url : citation.Title.Replace("]", string.Empty);
            sb.Append("- [");
            sb.Append(title);
            sb.Append("](");
            sb.Append(citation.Url);
            sb.Append(")\n");
        }

        return sb.ToString();
    }

    private static bool IsMentioned(string answerLower, Citation citation)
    {
        var title = citation.Title ?? string.Empty;
        if (TitleMentioned(answerLower, title))
        {
            return true;
        }

        if (!Uri.TryCreate(citation.Url ?? string.Empty, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = (uri.Host ?? string.Empty).ToLowerInvariant();
        if (host.Length == 0)
        {
            return false;
        }

        if (answerLower.Contains(host, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var alias in HostAliases(host))
        {
            if (answerLower.Contains(alias, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TitleMentioned(string answerLower, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        // Avoid pathological checks on very long titles.
        if (title.Length > 120)
        {
            title = title[..120];
        }

        // Don't match on generic titles like "Home".
        var lowered = title.Trim().ToLowerInvariant();
        if (lowered.Length < 8)
        {
            return false;
        }

        return answerLower.Contains(lowered, StringComparison.Ordinal);
    }

    private static IEnumerable<string> HostAliases(string host)
    {
        // Keep this list intentionally small and conservative.
        if (host.EndsWith("wikipedia.org", StringComparison.Ordinal))
        {
            yield return "wikipedia";
            yield return "wiki";
        }

        if (host.EndsWith("semanticscholar.org", StringComparison.Ordinal))
        {
            yield return "semantic scholar";
        }

        if (host.Contains("doi.org", StringComparison.Ordinal))
        {
            yield return "doi";
        }
    }

    private static string? FirstUrl(params string?[] urls)
    {
        foreach (var url in urls)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }
}
