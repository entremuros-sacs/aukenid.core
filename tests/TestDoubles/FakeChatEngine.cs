using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests.TestDoubles;

internal sealed class FakeChatEngine : IChatEngine
{
    private readonly Queue<ToolRouter.IntentDetection> _detections = new();
    private readonly Queue<string> _chatReplies = new();
    private readonly Queue<string> _documentReplies = new();

    public string? ActiveModelName => "fake";

    public bool IsGpuEnabled => false;

    public string? StreamNoticeKey { get; private set; }

    public int ChatCalls { get; private set; }

    public int DocumentCalls { get; private set; }

    public void EnqueueDetection(ToolRouter.IntentDetection detection) => _detections.Enqueue(detection);

    public void EnqueueChatReply(string reply) => _chatReplies.Enqueue(reply);

    public void EnqueueDocumentReply(string reply) => _documentReplies.Enqueue(reply);

    public Task LoadModelAsync(string profileId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> TryGenerateTitleAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<ToolRouter.IntentDetection> DetectIntentsAsync(IReadOnlyList<ChatTurn> recentTurns, string prompt, CancellationToken cancellationToken)
    {
        return Task.FromResult(_detections.Count > 0 ? _detections.Dequeue() : ToolRouter.IntentDetection.Default);
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> turns, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StreamNoticeKey = null;

        var reply = IsDocumentPrompt(turns)
            ? DequeueDocumentReply()
            : DequeueChatReply();

        // Stream as a single chunk; tests only care about final content.
        yield return reply;
        await Task.CompletedTask;
    }

    public Task<string> CompleteAsync(IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StreamNoticeKey = null;

        return Task.FromResult(IsDocumentPrompt(turns)
            ? DequeueDocumentReply()
            : DequeueChatReply());
    }

    private string DequeueChatReply()
    {
        ChatCalls++;
        return _chatReplies.Count > 0 ? _chatReplies.Dequeue() : string.Empty;
    }

    private string DequeueDocumentReply()
    {
        DocumentCalls++;
        return _documentReplies.Count > 0 ? _documentReplies.Dequeue() : string.Empty;
    }

    private static bool IsDocumentPrompt(IReadOnlyList<ChatTurn> turns)
    {
        if (turns.Count == 0)
        {
            return false;
        }

        // Match the prompt contract in prompts.md without requiring PromptLibrary internals.
        // The orchestrator may prepend other system turns (e.g. ChatStyleSystem), so scan.
        foreach (var turn in turns)
        {
            if (turn.Role != "system" || string.IsNullOrWhiteSpace(turn.Content))
            {
                continue;
            }

            if (turn.Content.Contains("side markdown document panel", StringComparison.OrdinalIgnoreCase)
                || turn.Content.Contains("You write the content for a side markdown document panel", StringComparison.OrdinalIgnoreCase)
                || turn.Content.Contains("You edit a markdown document", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

