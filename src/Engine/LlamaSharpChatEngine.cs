namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public LlamaSharpChatEngine(string weightsPath)
    {
        _weightsPath = weightsPath;
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
            _templateName ??= GalleryTemplate.FamilyFromMetadata(newWeights.Metadata);
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
            // Some GGUFs don't register their chat template's own turn-end token as an EOS the
            // executor recognizes, so without this it can keep going past the real answer and
            // hallucinate (and re-answer) further turns. See ChatTemplate.StopSequences.
            AntiPrompts = GalleryTemplate.StopSequences(_templateName),
            // Without this, a small model can degenerate into repeating/expanding the same phrase
            // (e.g. "...-Instruct-Instruct-Instruct...") forever instead of reaching EOS - a
            // different failure from the turn-boundary one above, since it never emits a stop
            // sequence at all. 1.1 is llama.cpp's own long-standing default for this reason.
            SamplingPipeline = new DefaultSamplingPipeline { RepeatPenalty = 1.1f },
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
    // Use llama.cpp's named template from ChatTemplate.All, not the GGUF jinja blob
    // (those often fail llama_chat_apply_template).
    private string FormatTurns(IReadOnlyList<ChatTurn> turns)
    {
        var cppName = GalleryTemplate.LlamaCppName(_templateName);
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
    public Task<ToolRouter.Plan> SuggestToolsAsync(IReadOnlyList<ChatTurn> recentTurns, string prompt, CancellationToken cancellationToken)
    {
        if (_executor is null)
        {
            return Task.FromResult(new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Folder: true));
        }

        return Task.Run(async () =>
        {
            try
            {
                // Recent history is folded into the single user turn as plain text, not as real
                // chat turns: a realistic user/assistant exchange right above the instruction made
                // the model continue the conversation instead of emitting strict JSON, which broke
                // parsing (and silently dropped folder/newTopic to their fallback defaults).
                var routePrompt = FormatTurns(
                [
                    new ChatTurn("system", """
                        Route retrieval tools. Reply with JSON only, no markdown, no explanation:
                        {"web":false,"wiki":false,"scholar":false,"document":false,"q":"","folder":true,"newTopic":false}
                        web=true for news, prices, products, versions, or when the user asks to look something up.
                        wiki=true for encyclopedia definitions of people, places, or established concepts.
                        scholar=true for scientific papers.
                        Questions about this assistant, its controls, or the current chat: all false.
                        At most two of web/wiki/scholar true. When one is true, q is a short search query of at most 8 words naming the outside subject. Do not copy the user's sentence into q.
                        document=true only when the user asks to put, add, write, fill, update, or show content in the side document panel for this thread. The panel can be named anywhere in the sentence, including first, before the verb (e.g. "add that to the document panel", "fill the panel with the draft", "pon eso en el panel", "en el panel de documento, escribe/agrega/pon ...", "in the document panel, list ..."). document=true can combine with any other field. A message that only discusses or drafts content in the chat, without asking for the document panel specifically, is document=false. When document=true and the user already states what to write, web/wiki/scholar stay false unless they also explicitly ask you to look something up first.
                        folder=false only if this question is simple and self-contained (a greeting, a direct edit, a one-off fact, a continuation of this same chat) and does not need background from other conversations in the current project. Otherwise folder=true.
                        The user turn below may start with "Recent conversation:" followed by "Newest message:". newTopic=true only when the newest message has no connection at all to that recent conversation (a clear subject change, not a follow-up, clarification, or continuation). If there is no recent conversation shown, newTopic is always false.
                        """),
                    new ChatTurn("user", BuildRouteUserTurn(recentTurns, prompt)),
                ]);
                var inferenceParams = new InferenceParams
                {
                    // The schema is 7 fields now (document, folder, newTopic added); too tight a budget
                    // lets generation get cut off before the closing brace, which fails the whole parse.
                    MaxTokens = 112,
                    SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.1f },
                };

                var raw = string.Empty;
                await foreach (var token in _executor.InferAsync(routePrompt, inferenceParams, cancellationToken))
                {
                    raw += token;
                }

                var plan = ToolRouter.ParseModelPlan(raw);
                HostLog.Line("tools", $"classifier web={plan.Web} wiki={plan.Wiki} scholar={plan.Scholar} document={plan.Document} folder={plan.Folder} newTopic={plan.NewTopic}");

                return plan;
            }
            catch (Exception ex)
            {
                HostLog.Line("tools", "classifier failed " + HostLog.Describe(ex));
                return new ToolRouter.Plan(Wiki: false, Scholar: false, Web: false, Folder: true);
            }
        }, cancellationToken);
    }

    private static string BuildRouteUserTurn(IReadOnlyList<ChatTurn> recentTurns, string prompt)
    {
        if (recentTurns.Count == 0)
        {
            return prompt;
        }

        var lines = recentTurns.TakeLast(4).Select(turn =>
        {
            var role = turn.Role == "user" ? "User" : "Assistant";
            return $"{role}: {CollapseAndCap(turn.Content, 300)}";
        });

        return "Recent conversation:\n" + string.Join('\n', lines) + "\n\nNewest message: " + prompt;
    }

    private static string CollapseAndCap(string text, int maxChars)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= maxChars ? collapsed : collapsed[..maxChars] + "\u2026";
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
