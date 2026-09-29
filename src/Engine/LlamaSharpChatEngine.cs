namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Sampling;
using LLama.Transformers;

public sealed class LlamaSharpChatEngine : IChatEngine, IDisposable
{
    private readonly string _dataDirectory;
    private string _weightsPath;
    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private string? _templateName;

    public string? ActiveModelName { get; private set; }

    public string? StreamNoticeKey { get; private set; }

    public const string ContextIssueKey = "error.context";

    /// <summary>
    /// Point llama.cpp at natives shipped with this assembly and hush its load dump.
    /// Safe to call more than once. <see cref="LoadModelAsync"/> calls it.
    /// </summary>
    public static void PrepareRuntime() => NativeRuntime.Prepare();

    public LlamaSharpChatEngine(string weightsPath, string? dataDirectory = null)
    {
        _weightsPath = weightsPath;
        _dataDirectory = dataDirectory ?? GalleryTemplate.DefaultDirectory();
    }

    public void UseTemplate(string? templateName) => _templateName = templateName;

    public Task LoadModelAsync(string profileId, CancellationToken cancellationToken)
    {
        PrepareRuntime();
        // Photino's web-message callback runs on the UI thread; LLamaWeights.LoadFromFile is a
        // long, synchronous, CPU-bound call, so it must run off-thread or the WebView freezes and
        // can't paint the loading overlay until this returns.
        return Task.Run(() =>
        {
            var path = File.Exists(profileId) ? profileId : _weightsPath;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("GGUF model file not found.", path);
            }

            var parameters = new ModelParams(path)
            {
                // Gallery usable ctx (ADR-14). Size of the KV cache, not a product quota.
                ContextSize = 8192,
            };

            // Load the new file before dropping the old weights so a failed switch keeps a working model.
            var newWeights = LLamaWeights.LoadFromFile(parameters);
            var newExecutor = new StatelessExecutor(newWeights, parameters);
            var previous = _weights;
            _weights = newWeights;
            _executor = newExecutor;
            _weightsPath = path;
            previous?.Dispose();
            _templateName ??= GalleryTemplate.GalleryName(_dataDirectory, profileId, path);
            ActiveModelName = BuildShortName(_weights.Metadata);
        }, cancellationToken);
    }

    // GGUF's general.basename ("Llama-3.2") + general.size_label ("3B") gives a short, human name
    // (e.g. "Llama 3.2 3B") without the finetune suffix that general.name usually carries ("... Instruct").
    private string BuildShortName(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("general.basename", out var basename) && metadata.TryGetValue("general.size_label", out var sizeLabel))
        {
            return $"{basename.Replace('-', ' ')} {sizeLabel}";
        }

        return metadata.TryGetValue("general.name", out var name) ? name : Path.GetFileNameWithoutExtension(_weightsPath);
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> turns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_executor is null || _weights is null)
        {
            throw new InvalidOperationException("Model not loaded.");
        }

        StreamNoticeKey = null;
        var prompt = FormatTurns(turns);
        var inferenceParams = new InferenceParams
        {
            // Local engine: no token cost. Run until EOS. The KV window is the only ceiling.
            MaxTokens = -1,
        };

        await foreach (var token in InferTokensAsync(prompt, inferenceParams, cancellationToken))
        {
            yield return token;
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

    // Yield each token as llama.cpp produces it. The local WebView paints that stream.
    // A context overflow keeps the tokens already produced and records StreamNoticeKey.
    private async IAsyncEnumerable<string> InferTokensAsync(
        string prompt,
        InferenceParams inferenceParams,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = _executor!.InferAsync(prompt, inferenceParams, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await enumerator.MoveNextAsync();
                }
                catch (ContextOverflowException ex)
                {
                    HostLog.Line("model", HostLog.Describe(ex));
                    StreamNoticeKey = ContextIssueKey;
                    more = false;
                }

                if (!more)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    // StatelessExecutor has no KV memory across calls, so every turn is a fresh completion.
    // Use llama.cpp's named template from templates.json `apply`, not the GGUF jinja blob
    // (those often fail llama_chat_apply_template).
    private string FormatTurns(IReadOnlyList<ChatTurn> turns)
    {
        var cppName = GalleryTemplate.LlamaCppName(_dataDirectory, _templateName);
        var template = cppName is not null
            ? new LLamaTemplate(cppName) { AddAssistant = true }
            : new LLamaTemplate(_weights!.NativeHandle) { AddAssistant = true };
        foreach (var turn in turns)
        {
            template.Add(turn.Role, turn.Content);
        }

        return PromptTemplateTransformer.ToModelPrompt(template);
    }

    public void Dispose() => _weights?.Dispose();

    // Asks the same local model for a short topic title instead of just truncating the raw prompt.
    // StatelessExecutor builds a fresh completion per call (no shared conversation state), but it's not
    // safe to run concurrently with the main response, so this must be awaited before that starts.
    public Task<ToolRouter.Plan> SuggestToolsAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_executor is null)
        {
            return Task.FromResult(default(ToolRouter.Plan));
        }

        return Task.Run(async () =>
        {
            try
            {
                var routePrompt = FormatTurns(
                [
                    new ChatTurn("system", """
                        Route retrieval tools. Reply with JSON only, no markdown, no explanation:
                        {"web":false,"wiki":false,"scholar":false,"q":""}
                        web=true for news, prices, products, versions, or when the user asks to look something up.
                        wiki=true for encyclopedia definitions of people, places, or established concepts.
                        scholar=true for scientific papers.
                        Questions about this assistant, its controls, or the current chat: all false.
                        At most two true. When one is true, q is a short search query of at most 8 words naming the outside subject. Do not copy the user's sentence into q.
                        """),
                    new ChatTurn("user", prompt),
                ]);
                var inferenceParams = new InferenceParams
                {
                    MaxTokens = 64,
                    SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.1f },
                };

                var raw = string.Empty;
                await foreach (var token in _executor.InferAsync(routePrompt, inferenceParams, cancellationToken))
                {
                    raw += token;
                }

                var plan = ToolRouter.ParseModelPlan(raw);
                if (plan.Any)
                {
                    HostLog.Line("tools", $"classifier web={plan.Web} wiki={plan.Wiki} scholar={plan.Scholar}");
                }

                return plan;
            }
            catch (Exception ex)
            {
                HostLog.Line("tools", "classifier failed " + HostLog.Describe(ex));
                return default;
            }
        }, cancellationToken);
    }

    public Task<string?> TryGenerateTitleAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_executor is null)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.Run(async () =>
        {
            try
            {
                var titlePrompt = FormatTurns(
                [
                    new ChatTurn("user", "Reply with only a short 3 to 6 word topic title (no quotes, no trailing "
                        + $"punctuation) describing what this message is about:\n\n{prompt}"),
                ]);
                var inferenceParams = new InferenceParams { MaxTokens = 16 };

                var raw = string.Empty;
                await foreach (var token in _executor.InferAsync(titlePrompt, inferenceParams, cancellationToken))
                {
                    raw += token;
                }

                var title = CleanTitle(raw);
                return title.Length == 0 ? null : title;
            }
            catch
            {
                // Best-effort: the caller falls back to a truncated-prompt title on null.
                return null;
            }
        }, cancellationToken);
    }

    private static string CleanTitle(string raw)
    {
        var normalized = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        normalized = normalized.Trim('"', '\'', ' ', '.', '\n', '\r');

        const int maxLength = 60;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd() + "\u2026";
    }
}
