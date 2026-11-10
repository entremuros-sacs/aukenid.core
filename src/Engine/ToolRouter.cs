namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Intent routing: AI pass returns workflow intents as structured JSON; host turns them into a
/// deterministic execution plan.
/// </summary>
public static partial class ToolRouter
{
    [Flags]
    public enum IntentKind
    {
        None = 0,
        Chat = 1 << 0,
        Document = 1 << 1,
        ContinueConversation = 1 << 2,
        NewTopic = 1 << 3,
        SearchWeb = 1 << 4,
        SearchWiki = 1 << 5,
        SearchScholar = 1 << 6,
        NeedClarification = 1 << 7,
    }

    [Flags]
    public enum PlanKind
    {
        None = 0,
        Web = 1 << 0,
        Wiki = 1 << 1,
        Scholar = 1 << 2,
        Document = 1 << 3,
        DocumentVerbatim = 1 << 4,
        Folder = 1 << 5,
        NewTopic = 1 << 6,
        ClarifyInChat = 1 << 7,
    }

    public readonly record struct IntentDetection(IntentKind Intents, string? Query, bool Folder, bool NewTopic, bool DocumentVerbatim, bool Explicit, string? ClarificationQuestion)
    {
        public static IntentDetection Default => new(IntentKind.None, Query: null, Folder: true, NewTopic: false, DocumentVerbatim: false, Explicit: false, ClarificationQuestion: null);

        public bool HasIntent(IntentKind intent) => (Intents & intent) != IntentKind.None;
    }

    public readonly record struct Plan(PlanKind Steps, string? Query = null)
    {
        public IntentKind Intents { get; init; } = IntentKind.None;

        public string? ClarificationQuestion { get; init; }

        public bool Web => Steps.HasFlag(PlanKind.Web);

        public bool Wiki => Steps.HasFlag(PlanKind.Wiki);

        public bool Scholar => Steps.HasFlag(PlanKind.Scholar);

        public bool Document => Steps.HasFlag(PlanKind.Document);

        public bool DocumentVerbatim => Steps.HasFlag(PlanKind.DocumentVerbatim);

        public bool Folder => Steps.HasFlag(PlanKind.Folder);

        public bool NewTopic => Steps.HasFlag(PlanKind.NewTopic);

        public bool ClarifyInChat => Steps.HasFlag(PlanKind.ClarifyInChat);

        public bool Any => (Steps & (PlanKind.Web | PlanKind.Wiki | PlanKind.Scholar)) != PlanKind.None;

        public bool HasIntent(IntentKind intent) => (Intents & intent) != IntentKind.None;

        public string? StatusKey =>
            Web ? "searching.web"
            : Scholar ? "searching.papers"
            : Wiki ? "searching.wiki"
            : null;
    }

    public static IntentDetection ParseIntentDetection(string? raw)
    {
        // If routing fails, keep folder on (safer default), and let the planner treat it as chat.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return IntentDetection.Default;
        }

        var json = TryExtractJsonObject(raw);
        if (json is null)
        {
            return IntentDetection.Default;
        }

        try
        {
            using var jsonDoc = JsonDocument.Parse(json);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String)
            {
                return IntentDetection.Default;
            }

            if (!string.Equals(schema.GetString(), "aukenid.intent.v1", StringComparison.Ordinal))
            {
                return IntentDetection.Default;
            }

            var intents = ParseIntents(root);
            var query = ReadQuery(root);
            var folder = ReadFolder(root);
            var newTopic = ReadNewTopic(root);
            var documentVerbatim = ReadDocumentVerbatim(root);
            var needClarification = Truthy(root, "needClarification");
            var clarificationQuestion = ReadString(root, "clarificationQuestion");

            if (needClarification)
            {
                intents |= IntentKind.NeedClarification;
            }

            return new IntentDetection(intents, query, folder, newTopic, documentVerbatim, Explicit: true, clarificationQuestion);
        }
        catch (JsonException)
        {
            return IntentDetection.Default;
        }
    }

    public static Plan PlanForTurn(string prompt, IntentDetection detection, bool hasAttachments, IReadOnlyList<ChatTurn>? recentTurns = null)
    {
        var intents = detection.Intents;
        var query = detection.Query;

        // Deterministic continuation: if the assistant just asked a question (typical clarification
        // step) and the user is replying, do NOT treat it as a new topic. If that question was
        // specifically about the document panel, also default to producing a chat reply while
        // proceeding with the document workflow.
        var turns = recentTurns ?? [];
        if (LooksLikeAnswerToPriorAssistantQuestion(turns, prompt))
        {
            // Prefer continuity: never show the "new topic" banner for clarification answers.
            intents &= ~IntentKind.NewTopic;
            intents |= IntentKind.ContinueConversation;
        }

        // Deterministic safety net: URLs/DOIs/arXiv/etc imply retrieval even if the classifier misses.
        var hardIntents = HardSignalsAsIntents(prompt);
        intents |= hardIntents;

        // If the model gave no intents at all, treat it as a chat turn by default.
        if ((intents & (IntentKind.Chat | IntentKind.Document | IntentKind.SearchWeb | IntentKind.SearchWiki | IntentKind.SearchScholar | IntentKind.NeedClarification)) == IntentKind.None)
        {
            intents |= IntentKind.Chat;
        }

        var newTopic = detection.NewTopic;

        intents |= newTopic ? IntentKind.NewTopic : IntentKind.ContinueConversation;

        // Cap retrieval sources deterministically.
        intents = CapSearchIntents(intents);

        // Attachments are source material; keep retrieval tools off so they don't compete for context.
        if (hasAttachments)
        {
            intents &= ~(IntentKind.SearchWeb | IntentKind.SearchWiki | IntentKind.SearchScholar);
            query = null;
        }

        var steps = PlanKind.None;
        if (intents.HasFlag(IntentKind.NeedClarification))
        {
            steps |= PlanKind.ClarifyInChat;
        }

        if (intents.HasFlag(IntentKind.Document))
        {
            steps |= PlanKind.Document;
        }

        if (intents.HasFlag(IntentKind.SearchWeb))
        {
            steps |= PlanKind.Web;
        }

        if (intents.HasFlag(IntentKind.SearchWiki))
        {
            steps |= PlanKind.Wiki;
        }

        if (intents.HasFlag(IntentKind.SearchScholar))
        {
            steps |= PlanKind.Scholar;
        }

        if (newTopic)
        {
            steps |= PlanKind.NewTopic;
        }

        // Folder context is host-only: never when attachments are present.
        if (!hasAttachments && detection.Folder)
        {
            steps |= PlanKind.Folder;
        }

        // Verbatim document import is only meaningful when a file is attached.
        if (hasAttachments && steps.HasFlag(PlanKind.Document) && detection.DocumentVerbatim)
        {
            steps |= PlanKind.DocumentVerbatim;
        }

        query ??= InferMissingQuery(prompt, intents);

        return BindQuery(new Plan(steps, query) { Intents = intents, ClarificationQuestion = detection.ClarificationQuestion }, prompt);
    }

    private static bool LooksLikeAnswerToPriorAssistantQuestion(IReadOnlyList<ChatTurn> recentTurns, string prompt)
    {
        if (recentTurns.Count == 0 || string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var last = recentTurns[^1];
        if (last.Role != "assistant")
        {
            return false;
        }

        // Any assistant question implies the user reply is likely a continuation.
        if (!LooksLikeQuestion(last.Content))
        {
            return false;
        }

        // Extremely short replies like "ok" are more likely acknowledgements than answers; don't
        // override topic detection for those.
        var trimmed = prompt.Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Cross-language enough for our current UX: explicit question marks.
        return text.Contains('?', StringComparison.Ordinal) || text.Contains('¿', StringComparison.Ordinal);
    }

    public static bool HasScholarIdentifier(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        return ScholarContext.FirstDoi(prompt) is not null
            || ArxivId().IsMatch(prompt)
            || PubmedId().IsMatch(prompt);
    }


    private static IntentKind HardSignalsAsIntents(string prompt)
    {
        var intents = IntentKind.None;

        if (HasScholarIdentifier(prompt))
        {
            intents |= IntentKind.SearchScholar;
        }

        var url = WebContext.FirstPastedHttpUrl(prompt)
            ?? WebContext.FirstPastedHttpsUrlFromBareDomain(prompt);
        if (url is not null)
        {
            intents |= IntentKind.SearchWeb;
        }

        return intents;
    }

    private static string? InferMissingQuery(string prompt, IntentKind intents)
    {
        if (!intents.HasFlag(IntentKind.SearchWeb)
            && !intents.HasFlag(IntentKind.SearchWiki)
            && !intents.HasFlag(IntentKind.SearchScholar))
        {
            return null;
        }

        // If the user pasted a URL, that is the query.
        var url = WebContext.FirstPastedHttpUrl(prompt)
            ?? WebContext.FirstPastedHttpsUrlFromBareDomain(prompt);
        if (url is not null)
        {
            return url;
        }

        return LookupQuery(prompt);
    }

    private static Plan BindQuery(Plan plan, string prompt)
    {
        var query = plan.Query;

        if (plan.Web && string.IsNullOrWhiteSpace(query))
        {
            plan = plan with { Steps = plan.Steps & ~PlanKind.Web };
            plan = plan with { Intents = plan.Intents & ~IntentKind.SearchWeb };
        }

        if (plan.Wiki && string.IsNullOrWhiteSpace(query))
        {
            plan = plan with { Steps = plan.Steps & ~PlanKind.Wiki };
            plan = plan with { Intents = plan.Intents & ~IntentKind.SearchWiki };
        }

        // Scholar can run without an explicit query if there is a DOI/arXiv/PMID in the prompt.
        if (plan.Scholar && string.IsNullOrWhiteSpace(query) && !HasScholarIdentifier(prompt))
        {
            plan = plan with { Steps = plan.Steps & ~PlanKind.Scholar };
            plan = plan with { Intents = plan.Intents & ~IntentKind.SearchScholar };
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

    private static IntentKind CapSearchIntents(IntentKind intents)
    {
        var count = 0;
        var capped = intents & ~(IntentKind.SearchWeb | IntentKind.SearchWiki | IntentKind.SearchScholar);

        if (intents.HasFlag(IntentKind.SearchScholar) && count++ < 2)
        {
            capped |= IntentKind.SearchScholar;
        }

        if (intents.HasFlag(IntentKind.SearchWiki) && count++ < 2)
        {
            capped |= IntentKind.SearchWiki;
        }

        if (intents.HasFlag(IntentKind.SearchWeb) && count++ < 2)
        {
            capped |= IntentKind.SearchWeb;
        }

        return capped;
    }

    private static IntentKind ParseIntents(JsonElement root)
    {
        if (!root.TryGetProperty("intents", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return IntentKind.None;
        }

        var intents = IntentKind.None;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var normalized = raw.Trim();
            if (normalized.Equals("chat", StringComparison.OrdinalIgnoreCase))
            {
                intents |= IntentKind.Chat;
            }
            else if (normalized.Equals("document", StringComparison.OrdinalIgnoreCase))
            {
                intents |= IntentKind.Document;
            }
            else if (normalized.Equals("search.web", StringComparison.OrdinalIgnoreCase))
            {
                intents |= IntentKind.SearchWeb;
            }
            else if (normalized.Equals("search.wiki", StringComparison.OrdinalIgnoreCase))
            {
                intents |= IntentKind.SearchWiki;
            }
            else if (normalized.Equals("search.scholar", StringComparison.OrdinalIgnoreCase))
            {
                intents |= IntentKind.SearchScholar;
            }
        }

        return intents;
    }

    private static string? ReadQuery(JsonElement root)
    {
        if (!root.TryGetProperty("q", out var q) || q.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var trimmed = q.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 8 || trimmed.Length > 120)
        {
            return null;
        }

        return trimmed;
    }

    private static bool ReadFolder(JsonElement root)
    {
        if (!root.TryGetProperty("folder", out var value))
        {
            return true;
        }

        return value.ValueKind != JsonValueKind.False
            && (value.ValueKind != JsonValueKind.String || value.GetString() is not ("false" or "0"));
    }

    private static bool ReadNewTopic(JsonElement root) => Truthy(root, "newTopic");

    private static bool ReadDocumentVerbatim(JsonElement root) => Truthy(root, "documentVerbatim");

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
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

    private static string? TryExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                if (depth == 0)
                {
                    start = i;
                }

                depth++;
                continue;
            }

            if (ch == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    return raw.Substring(start, i - start + 1);
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"\barxiv\.org\b|\b\d{4}\.\d{4,5}(v\d+)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArxivId();

    [GeneratedRegex(@"\b(pmid|pmc)\b|\bpubmed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PubmedId();

    [GeneratedRegex(@"\b(?:\d+\.\d+(?:\.\d+)*|(?:19|20)\d{2}|\p{Lu}{2,}|\p{Lu}\p{Ll}{2,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex LookupToken();

    [GeneratedRegex(@"^[^\p{L}]*(\p{L}+)", RegexOptions.CultureInvariant)]
    private static partial Regex FirstWord();

    
}

