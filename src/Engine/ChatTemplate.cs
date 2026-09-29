namespace Aukenid.Core.Engine;

using System.Collections.Generic;

/// <summary>
/// Family name (gallery <c>template</c> or GGUF architecture) to llama.cpp's named chat template.
/// </summary>
public sealed class ChatTemplate
{
    public ChatTemplate(string family, string apply)
    {
        Family = family;
        Apply = apply;
    }

    public string Family { get; }

    public string Apply { get; }

    public static IReadOnlyList<ChatTemplate> All { get; } =
    [
        new("gemma4", "gemma"),
        new("phi4", "phi3"),
        new("qwen3", "chatml"),
        new("llama3", "llama3"),
        new("granite", "granite"),
        new("mistral", "mistral-v1"),
    ];
}
