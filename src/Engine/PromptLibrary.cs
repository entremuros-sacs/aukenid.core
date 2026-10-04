namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// One entry per human-editable prompt/grounding-note section in <c>Prompts/prompts.md</c>. The
/// member name must match a "## Name" header in that file exactly - <see cref="PromptLibrary.Initialize"/>
/// validates both directions (every key has a section, every section has a key) and crashes on a
/// mismatch instead of silently falling back to an empty or stale string.
/// </summary>
internal enum PromptKey
{
    ToolRouterSystem,
    DocumentGuidance,
    DocumentGenerationSystem,
    AttachmentContextNote,
    WebGroundingNote,
    ScholarGroundingNote,
    WikiGroundingNote,
    CodeExplanation,
    DocumentChunkEditSystem,
}

/// <summary>
/// Loads every <see cref="PromptKey"/> text once from the embedded <c>Prompts/prompts.md</c> resource
/// into an in-memory dictionary. Keeping these as one editable file (rather than string literals spread
/// across the engine/services) makes tuning the assistant's behavior a text edit, not a code change.
/// </summary>
public static class PromptLibrary
{
    private const string ResourceName = "Aukenid.Core.Prompts.prompts.md";

    private static readonly Lazy<IReadOnlyDictionary<PromptKey, string>> Prompts = new(Load);

    /// <summary>Forces the load/validation to run now, so a bad prompts.md crashes at startup
    /// instead of on first use deep in a conversation turn.</summary>
    public static void Initialize() => _ = Prompts.Value;

    internal static string Get(PromptKey key) => Prompts.Value[key];

    /// <summary>
    /// The one always-on style nudge applied to every turn (no per-topic heuristic). A factory, like
    /// <see cref="Services.DocumentTool.GuidanceTurn"/>, so callers outside Core never need to know
    /// which <see cref="PromptKey"/> backs it.
    /// </summary>
    public static ChatTurn CodeExplanationTurn() => new("system", Get(PromptKey.CodeExplanation));

    private static IReadOnlyDictionary<PromptKey, string> Load()
    {
        var assembly = typeof(PromptLibrary).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        var sections = ParseSections(reader.ReadToEnd());

        var errors = new List<string>();
        var result = new Dictionary<PromptKey, string>();
        foreach (PromptKey key in Enum.GetValues<PromptKey>())
        {
            if (sections.TryGetValue(key.ToString(), out var text))
            {
                result[key] = text;
            }
            else
            {
                errors.Add($"Missing section '## {key}' in prompts.md for PromptKey.{key}.");
            }
        }

        var knownNames = new HashSet<string>(Enum.GetNames<PromptKey>(), StringComparer.Ordinal);
        foreach (var header in sections.Keys)
        {
            if (!knownNames.Contains(header))
            {
                errors.Add($"Section '## {header}' in prompts.md has no matching PromptKey member.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("PromptLibrary validation failed:\n" + string.Join('\n', errors));
        }

        return result;
    }

    private static Dictionary<string, string> ParseSections(string markdown)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentKey = null;
        var currentText = new StringBuilder();

        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (currentKey is not null)
                {
                    sections[currentKey] = currentText.ToString().Trim();
                }

                currentKey = line[3..].Trim();
                currentText.Clear();
                continue;
            }

            if (currentKey is not null)
            {
                currentText.Append(line).Append('\n');
            }
        }

        if (currentKey is not null)
        {
            sections[currentKey] = currentText.ToString().Trim();
        }

        return sections;
    }
}
