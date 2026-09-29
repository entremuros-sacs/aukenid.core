namespace Aukenid.Core.Engine;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Tool choice is language-agnostic: identifiers (URL, DOI, arXiv) plus a JSON plan from the
/// local model. No per-language keyword lists.
/// </summary>
public static partial class ToolRouter
{
    public readonly record struct Plan(bool Wiki, bool Scholar, bool Web, string? Query = null, bool Explicit = false)
    {
        public bool Any => Wiki || Scholar || Web;

        public string? StatusKey =>
            Web ? "searching.web"
            : Scholar ? "searching.papers"
            : Wiki ? "searching.wiki"
            : null;
    }

    public static Plan HardSignals(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return default;
        }

        var doi = ScholarContext.FirstDoi(prompt) is not null;
        var arxiv = ArxivId().IsMatch(prompt);
        var pubmed = PubmedId().IsMatch(prompt);
        var url = WebContext.FirstPastedHttpUrl(prompt);
        var publicUrl = url is not null && (!doi || url.Contains("doi.org", StringComparison.OrdinalIgnoreCase) is false);

        return Cap(wiki: false, scholar: doi || arxiv || pubmed, web: publicUrl);
    }

    public static Plan ParseModelPlan(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        var json = JsonObject().Match(raw);
        if (!json.Success)
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(json.Value);
            var root = document.RootElement;
            var wiki = Truthy(root, "wiki");
            var scholar = Truthy(root, "scholar");
            var web = Truthy(root, "web");
            string? queryText = null;
            if (root.TryGetProperty("q", out var q) && q.ValueKind == JsonValueKind.String)
            {
                queryText = q.GetString();
            }

            var query = ShortQuery(queryText);
            return Cap(wiki, scholar, web) with { Query = query, Explicit = true };
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static Plan Merge(Plan hard, Plan model)
    {
        var capped = Cap(hard.Wiki || model.Wiki, hard.Scholar || model.Scholar, hard.Web || model.Web);
        return capped with
        {
            Query = model.Query ?? hard.Query,
            Explicit = model.Explicit || hard.Explicit,
        };
    }

    /// <summary>
    /// Attached files are the source material for this turn. Tools would spend the same
    /// GGUF window the extract and the reply need, so they stay off.
    /// </summary>
    public static Plan PlanForTurn(string prompt, Plan hard, Plan suggested, bool hasAttachments)
    {
        if (hasAttachments)
        {
            return default;
        }

        return BindQuery(WithDefaultWeb(Merge(hard, suggested), prompt), prompt);
    }

    /// <summary>
    /// Small local models often skip the JSON router. A named product, version, year, or
    /// proper name still gets a web lookup. The query is those names, not the sentence.
    /// An explicit all-false plan is left alone.
    /// </summary>
    public static Plan WithDefaultWeb(Plan plan, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || (plan.Explicit && !plan.Any))
        {
            return plan;
        }

        if (plan.Any)
        {
            return string.IsNullOrWhiteSpace(plan.Query)
                ? plan with { Query = LookupQuery(prompt) }
                : plan;
        }

        var lookup = LookupQuery(prompt);
        return lookup is null ? plan : new Plan(Wiki: false, Scholar: false, Web: true, Query: lookup);
    }

    /// <summary>
    /// Startpage treats the text as keywords. The user's sentence is not a query:
    /// "split" plus "relationship" retrieves stock splits and breakups.
    /// </summary>
    public static Plan BindQuery(Plan plan, string prompt)
    {
        var query = plan.Query;
        if (plan.Web && string.IsNullOrWhiteSpace(query))
        {
            var url = WebContext.FirstPastedHttpUrl(prompt);
            if (url is not null)
            {
                query = url;
            }
            else
            {
                plan = plan with { Web = false };
            }
        }

        if (plan.Wiki && string.IsNullOrWhiteSpace(query))
        {
            plan = plan with { Wiki = false };
        }

        if (plan.Scholar && string.IsNullOrWhiteSpace(query) && !HardSignals(prompt).Scholar)
        {
            plan = plan with { Scholar = false };
        }

        return plan with { Query = string.IsNullOrWhiteSpace(query) ? null : query };
    }

    public static string? LookupQuery(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var first = FirstWord().Match(prompt);
        var tokens = new List<string>();
        foreach (Match match in LookupToken().Matches(prompt))
        {
            if (first.Success && match.Index == first.Groups[1].Index)
            {
                continue;
            }

            if (tokens.Exists(token => token.Equals(match.Value, StringComparison.Ordinal)))
            {
                continue;
            }

            tokens.Add(match.Value);
            if (tokens.Count == 8)
            {
                break;
            }
        }

        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    private static string? ShortQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmed = query.Trim();
        var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 8 || trimmed.Length > 120)
        {
            return null;
        }

        return trimmed;
    }

    public static Plan Cap(bool wiki, bool scholar, bool web)
    {
        var count = 0;
        var s = scholar && count++ < 2;
        var w = wiki && count++ < 2;
        var v = web && count++ < 2;
        return new Plan(w, s, v);
    }

    private static bool Truthy(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && value.GetString() is "true" or "1");
    }

    [GeneratedRegex(@"\barxiv\.org\b|\b\d{4}\.\d{4,5}(v\d+)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArxivId();

    [GeneratedRegex(@"\b(pmid|pmc)\b|\bpubmed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PubmedId();

    [GeneratedRegex(@"\{[^{}]*\}", RegexOptions.Singleline)]
    private static partial Regex JsonObject();

    [GeneratedRegex(@"\b(?:\d+\.\d+(?:\.\d+)*|(?:19|20)\d{2}|\p{Lu}{2,}|\p{Lu}\p{Ll}{2,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex LookupToken();

    [GeneratedRegex(@"^[^\p{L}]*(\p{L}+)", RegexOptions.CultureInvariant)]
    private static partial Regex FirstWord();
}
