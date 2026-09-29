namespace Aukenid.Core.Engine;

public interface IChatEngine
{
    /// <summary>Short display name of the currently loaded weights, or null if none is loaded.</summary>
    string? ActiveModelName { get; }

    Task LoadModelAsync(string profileId, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken);

    /// <summary>Full assistant reply for this turn list (concatenated <see cref="StreamAsync"/> tokens).</summary>
    Task<string> CompleteAsync(IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken);

    /// <summary>i18n key set after <see cref="StreamAsync"/> when the KV window filled before EOS.</summary>
    string? StreamNoticeKey { get; }

    /// <summary>Short topic title for a first prompt, or null if unsupported/failed.</summary>
    Task<string?> TryGenerateTitleAsync(string prompt, CancellationToken cancellationToken);

    /// <summary>
    /// Language-agnostic tool plan for this user turn (web / wiki / scholar). Empty plan if the
    /// engine cannot classify.
    /// </summary>
    Task<ToolRouter.Plan> SuggestToolsAsync(string prompt, CancellationToken cancellationToken);
}
