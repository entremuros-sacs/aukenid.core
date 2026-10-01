namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Tool choice is language-agnostic: identifiers (URL, DOI, arXiv) plus a JSON plan from the
/// local model. No per-language keyword lists.
/// </summary>
public static partial class ToolRouter
{
    public readonly record struct Plan(bool Wiki, bool Scholar, bool Web, string? Query = null, bool Explicit = false, bool Folder = true, bool NewTopic = false)
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
        // No parseable JSON means no classification happened; keep folder context on rather
        // than silently dropping project background on a transient router miss.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Plan(Wiki: false, Scholar: false, Web: false, Folder: true);
        }

        var json = JsonObject().Match(raw);
        if (!json.Success)
        {
            return new Plan(Wiki: false, Scholar: false, Web: false, Folder: true);
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
            var folder = FolderFlag(root);
            var newTopic = Truthy(root, "newTopic");
            return Cap(wiki, scholar, web) with { Query = query, Explicit = true, Folder = folder, NewTopic = newTopic };
        }
        catch (JsonException)
        {
            return new Plan(Wiki: false, Scholar: false, Web: false, Folder: true);
        }
    }

    public static Plan Merge(Plan hard, Plan model)
    {
        var capped = Cap(hard.Wiki || model.Wiki, hard.Scholar || model.Scholar, hard.Web || model.Web);
        return capped with
        {
            Query = model.Query ?? hard.Query,
            Explicit = model.Explicit || hard.Explicit,
            // Hard signals (URL/DOI/arXiv regex) have no opinion on folder relevance or topic
            // drift; only the model's classification does.
            Folder = model.Folder,
            NewTopic = model.NewTopic,
        };
    }

    /// <summary>
    /// Attached files are the source material for this turn. Tools would spend the same
    /// GGUF window the extract and the reply need, so they stay off. Folder context is self-
    /// contained background too, so it is skipped the same way (Plan.Folder defaults to false here).
    /// </summary>
    public static Plan PlanForTurn(string prompt, Plan hard, Plan suggested, bool hasAttachments, IReadOnlyList<ChatTurn>? recentTurns = null)
    {
        if (hasAttachments)
        {
            return default;
        }

        var plan = BindQuery(WithDefaultWeb(Merge(hard, suggested), prompt), prompt);
        // Small local models reliably classify web/wiki/scholar but keep defaulting newTopic to
        // false even on an obvious subject change. Back the model's own call with a deterministic
        // one: no shared vocabulary at all with the recent conversation is a confident signal on
        // its own, regardless of what the model said.
        if (!plan.NewTopic && LooksLikeNewTopic(recentTurns ?? [], prompt))
        {
            plan = plan with { NewTopic = true };
        }

        return plan;
    }

    /// <summary>
    /// True when <paramref name="prompt"/> shares no word of more than 3 letters with
    /// <paramref name="recentTurns"/>. The length cutoff is a language-agnostic stand-in for a
    /// stopword list (short words tend to be function words across languages); too few
    /// significant words on either side means not enough signal to judge, so this returns false.
    /// </summary>
    public static bool LooksLikeNewTopic(IReadOnlyList<ChatTurn> recentTurns, string prompt)
    {
        if (recentTurns.Count == 0 || string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var promptWords = SignificantWords(prompt);
        if (promptWords.Count < 2)
        {
            return false;
        }

        var historyWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var turn in recentTurns)
        {
            foreach (var word in SignificantWords(turn.Content))
            {
                historyWords.Add(word);
            }
        }

        if (historyWords.Count == 0)
        {
            return false;
        }

        foreach (var word in promptWords)
        {
            if (historyWords.Contains(word))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> SignificantWords(string text)
    {
        var words = new List<string>();
        foreach (Match match in WordToken().Matches(text))
        {
            if (match.Value.Length > 3)
            {
                words.Add(match.Value);
            }
        }

        return words;
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
        return lookup is null ? plan : new Plan(Wiki: false, Scholar: false, Web: true, Query: lookup, Folder: plan.Folder, NewTopic: plan.NewTopic);
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

    // Unlike the tool flags, folder defaults to included: a missing or unreadable field should
    // not silently drop project background the turn might need.
    private static bool FolderFlag(JsonElement root)
    {
        if (!root.TryGetProperty("folder", out var value))
        {
            return true;
        }

        return value.ValueKind != JsonValueKind.False
            && (value.ValueKind != JsonValueKind.String || value.GetString() is not ("false" or "0"));
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

    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    private static partial Regex WordToken();
}
