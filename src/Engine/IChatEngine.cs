using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Aukenid.Core.Engine;

public interface IChatEngine
{
    /// <summary>Short display name of the currently loaded weights, or null if none is loaded.</summary>
    string? ActiveModelName { get; }

    /// <summary>True when the engine is actively using a GPU backend (Vulkan/Metal).</summary>
    bool IsGpuEnabled { get; }

    Task LoadModelAsync(string profileId, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken);

    /// <summary>Full assistant reply for this turn list (concatenated <see cref="StreamAsync"/> tokens).</summary>
    Task<string> CompleteAsync(IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken);

    /// <summary>i18n key set after <see cref="StreamAsync"/> when the KV window filled before EOS.</summary>
    string? StreamNoticeKey { get; }

    /// <summary>Short topic title for a first prompt, or null if unsupported/failed.</summary>
    Task<string?> TryGenerateTitleAsync(string prompt, CancellationToken cancellationToken);

    /// <summary>
    /// First-pass intent detection for this user turn. The host will deterministically map the
    /// returned intents into an execution plan.
    /// </summary>
    Task<ToolRouter.IntentDetection> DetectIntentsAsync(IReadOnlyList<ChatTurn> recentTurns, string prompt, CancellationToken cancellationToken);
}
