namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Engine;

/// <summary>
/// One user turn: route web/wiki/scholar in the host, inject those notes, then complete.
/// Conversation history stays user/assistant only; tool notes are per-turn.
/// </summary>
public sealed class GroundedChat
{
    public const string WebIssueKey = "issue.web";
    public const string ScholarIssueKey = "issue.papers";
    public const string WikiIssueKey = "issue.wiki";

    private readonly WebEnricher _web;
    private readonly WikiEnricher _wiki;
    private readonly ScholarEnricher _scholar;

    public GroundedChat(WebEnricher web, WikiEnricher wiki, ScholarEnricher scholar)
    {
        _web = web;
        _wiki = wiki;
        _scholar = scholar;
    }

    public static GroundedChat CreateDefault() => new(
        new WebEnricher(new StartpageSearchProvider(), new WebFetchService()),
        new WikiEnricher(new WikipediaProvider()),
        new ScholarEnricher(new SemanticScholarProvider()));

    public async Task<GroundedReply> ReplyAsync(
        IChatEngine engine,
        IList<ChatTurn> conversation,
        string userText,
        CancellationToken cancellationToken)
    {
        return await ReplyAsync(
            engine,
            conversation,
            userText,
            hasAttachments: false,
            injectBeforeLastUser: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GroundedReply> ReplyAsync(
        IChatEngine engine,
        IList<ChatTurn> conversation,
        string userText,
        bool hasAttachments,
        Func<ToolRouter.Plan, IEnumerable<ChatTurn?>>? injectBeforeLastUser,
        CancellationToken cancellationToken)
    {
        var history = conversation.ToList();
        conversation.Add(new ChatTurn("user", userText));

        var detection = await engine.DetectIntentsAsync(history, userText, cancellationToken);
        var plan = ToolRouter.PlanForTurn(userText, detection, hasAttachments, history);

        if (plan.ClarifyInChat)
        {
            var question = plan.ClarificationQuestion;
            if (string.IsNullOrWhiteSpace(question))
            {
                question = await engine.CompleteAsync(
                    [
                        PromptLibrary.ChatStyleTurn(),
                        new ChatTurn("user", "Ask ONE concise clarification question to proceed. Reply with ONLY the question text.\n\nUser message:\n" + userText),
                    ],
                    cancellationToken);
            }

            question = string.IsNullOrWhiteSpace(question) ? "?" : question.Trim();
            conversation.Add(new ChatTurn("assistant", question));
            return new GroundedReply(question, plan, [], null);
        }
        if (plan.Any)
        {
            HostLog.Line("tools", $"web={plan.Web} wiki={plan.Wiki} scholar={plan.Scholar}");
        }

        var turns = conversation.ToList();
        PromptLibrary.PrependStandardPrefix(turns, DateTimeOffset.Now);
        if (injectBeforeLastUser is not null)
        {
            foreach (var extra in injectBeforeLastUser(plan))
            {
                InsertBeforeLastUser(turns, extra);
            }
        }
        var citations = new List<Citation>();
        string? warning = null;

        if (plan.Wiki)
        {
            try
            {
                var wiki = await _wiki.EnrichAsync(userText, plan.Query, cancellationToken);
                InsertBeforeLastUser(turns, wiki.Turn);
                citations.AddRange(wiki.Citations);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                HostLog.Line("wiki", HostLog.Describe(ex));
                warning = WikiIssueKey;
            }
        }

        if (plan.Scholar)
        {
            try
            {
                var scholarText = ToolRouter.HasScholarIdentifier(userText) ? null : plan.Query;
                var scholar = await _scholar.EnrichAsync(userText, scholarText, cancellationToken);
                InsertBeforeLastUser(turns, scholar.Turn);
                citations.AddRange(scholar.Citations);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                HostLog.Line("scholar", HostLog.Describe(ex));
                warning = ScholarIssueKey;
            }
        }

        if (plan.Web)
        {
            try
            {
                var web = await _web.EnrichAsync(userText, plan.Query, cancellationToken);
                InsertBeforeLastUser(turns, web.Turn);
                citations.AddRange(web.Citations);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                HostLog.Line("web.search", HostLog.Describe(ex));
                warning = WebIssueKey;
            }
        }

        var text = await engine.CompleteAsync(turns, cancellationToken);
        warning ??= engine.StreamNoticeKey;

        var filteredCitations = SourceCitations.FilterByMentionedInAnswer(text, citations);
        if (filteredCitations.Count > 0 && !string.IsNullOrWhiteSpace(text))
        {
            text = SourceCitations.Append(text, filteredCitations);
        }

        conversation.Add(new ChatTurn("assistant", text));
        return new GroundedReply(text, plan, filteredCitations, warning);
    }

    private static void InsertBeforeLastUser(List<ChatTurn> turns, ChatTurn? extra)
    {
        if (extra is null)
        {
            return;
        }

        var lastUser = turns.FindLastIndex(t => t.Role == "user");
        if (lastUser >= 0)
        {
            turns.Insert(lastUser, extra.Value);
        }
        else
        {
            turns.Add(extra.Value);
        }
    }
}

public readonly record struct GroundedReply(
    string Text,
    ToolRouter.Plan Plan,
    IReadOnlyList<Citation> Citations,
    string? NoticeKey);
