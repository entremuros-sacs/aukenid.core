namespace Aukenid.Core.Engine;

using System.Text;

public readonly record struct Citation(string Title, string Url);

public static class SourceCitations
{
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
