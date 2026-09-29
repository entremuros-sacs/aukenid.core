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
        conversation.Add(new ChatTurn("user", userText));

        var hard = ToolRouter.HardSignals(userText);
        var suggested = await engine.SuggestToolsAsync(userText, cancellationToken);
        var plan = ToolRouter.PlanForTurn(userText, hard, suggested, hasAttachments: false);
        if (plan.Any)
        {
            HostLog.Line("tools", $"web={plan.Web} wiki={plan.Wiki} scholar={plan.Scholar}");
        }

        var turns = conversation.ToList();
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
                var scholarText = ToolRouter.HardSignals(userText).Scholar ? null : plan.Query;
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
        if (citations.Count > 0 && !string.IsNullOrWhiteSpace(text) && warning is null)
        {
            text = SourceCitations.Append(text, citations);
        }

        conversation.Add(new ChatTurn("assistant", text));
        return new GroundedReply(text, plan, citations, warning);
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
