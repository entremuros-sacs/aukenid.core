namespace Aukenid.Core.Engine;

using System.Collections.Generic;

/// <summary>
/// Family name (gallery <c>template</c> or GGUF architecture) to llama.cpp's named chat template.
/// </summary>
public sealed class ChatTemplate
{
    public ChatTemplate(string family, string apply, params string[] stopSequences)
    {
        Family = family;
        Apply = apply;
        StopSequences = stopSequences;
    }

    public string Family { get; }

    public string Apply { get; }

    // The text llama.cpp emits right where the model starts hallucinating a further turn (its own
    // template's next role marker) - without these, StatelessExecutor's MaxTokens=-1 keeps going
    // and can re-generate the whole reply again instead of stopping after the real answer.
    public IReadOnlyList<string> StopSequences { get; }

    public static IReadOnlyList<ChatTemplate> All { get; } =
    [
        new("gemma4", "gemma", "<end_of_turn>", "<start_of_turn>"),
        new("phi4", "phi3", "<|end|>", "<|user|>", "<|assistant|>"),
        new("qwen3", "chatml", "<|im_end|>", "<|im_start|>"),
        new("llama3", "llama3", "<|eot_id|>", "<|start_header_id|>"),
        new("granite", "granite", "<|end_of_text|>", "<|start_of_role|>"),
        new("mistral", "mistral-v1", "</s>", "[INST]"),
    ];
}
