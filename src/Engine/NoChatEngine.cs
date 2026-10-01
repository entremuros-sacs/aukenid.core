namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public sealed class NoChatEngine : IChatEngine
{
    private string _selected = "model";

    public string? ActiveModelName { get; private set; }

    public string? StreamNoticeKey => null;

    public Task LoadModelAsync(string profileId, CancellationToken cancellationToken)
    {
        _selected = profileId;
        ActiveModelName = profileId;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> turns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lastUser = turns.LastOrDefault(t => t.Role == "user").Content ?? string.Empty;
        var reply = $"[{_selected}] {lastUser}";
        foreach (var token in reply.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(30, cancellationToken);
            yield return token + " ";
        }
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken)
    {
        var reply = new System.Text.StringBuilder();
        await foreach (var token in StreamAsync(turns, cancellationToken))
        {
            reply.Append(token);
        }

        return reply.ToString();
    }

    // No real model to ask for a topic - caller falls back to a truncated-prompt title.
    public Task<string?> TryGenerateTitleAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<ToolRouter.Plan> SuggestToolsAsync(IReadOnlyList<ChatTurn> recentTurns, string prompt, CancellationToken cancellationToken) =>
        Task.FromResult(ToolRouter.HardSignals(prompt) with { Folder = true });
}
