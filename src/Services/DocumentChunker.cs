namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

/// <summary>
/// Splits a document's markdown into addressable <see cref="DocumentChunk"/>s: one chunk per heading
/// section (H1-H3) when it fits <see cref="ChunkBudgetChars"/>, otherwise split further by
/// accumulating whole blocks up to the budget - a block is never split mid-way. A single block alone
/// larger than the budget (a huge table or fenced code block) is kept whole: splitting it would break
/// its own markdown validity and risks the model reproducing content it never fully saw, which is the
/// exact drift this chunking exists to avoid. That one case is left to the existing context-overflow
/// handling (DesktopBridge.IsContextOverflow) rather than solved here.
/// </summary>
internal static class DocumentChunker
{
    public const int ChunkBudgetChars = 1200;
    private const int SectionHeadingMaxLevel = 3;

    // Short, random, and sparse on purpose (see ADR-26): long enough that a garbled id from the model
    // overwhelmingly matches no real chunk (a safe, catchable failure) rather than accidentally
    // colliding with a different real chunk (a silent, wrong-chunk edit) - unlike short sequential
    // integers, where an off-by-one typo is just as "valid-looking" as the intended id.
    private const string IdAlphabet = "23456789abcdefghjkmnpqrstuvwxyz";
    private const int IdLength = 5;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    public static IReadOnlyList<DocumentChunk> Chunk(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return [];
        }

        var blocks = Markdown.Parse(document, Pipeline).ToList();
        if (blocks.Count == 0)
        {
            return [];
        }

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var chunks = new List<DocumentChunk>();
        foreach (var section in GroupIntoSections(blocks, document))
        {
            chunks.AddRange(ChunkSection(section, document, usedIds));
        }

        return chunks;
    }

    private sealed record Section(string? HeadingText, List<Block> Blocks);

    private static List<Section> GroupIntoSections(IReadOnlyList<Block> blocks, string document)
    {
        var sections = new List<Section>();
        string? currentHeading = null;
        var current = new List<Block>();

        void Flush()
        {
            if (current.Count > 0)
            {
                sections.Add(new Section(currentHeading, current));
            }
        }

        foreach (var block in blocks)
        {
            if (block is HeadingBlock heading && heading.Level <= SectionHeadingMaxLevel)
            {
                Flush();
                current = [];
                var headingLine = document.Substring(heading.Span.Start, heading.Span.Length).Trim();
                currentHeading = headingLine.TrimStart('#').Trim();
            }

            current.Add(block);
        }

        Flush();
        return sections;
    }

    private static IEnumerable<DocumentChunk> ChunkSection(Section section, string document, HashSet<string> usedIds)
    {
        var total = SpanLength(section.Blocks);
        if (total <= ChunkBudgetChars)
        {
            yield return BuildChunk(section.Blocks, document, section.HeadingText, usedIds);
            yield break;
        }

        var run = new List<Block>();
        var part = 0;
        foreach (var block in section.Blocks)
        {
            // Measured as the true substring span (first-block start to this block's end), not a sum
            // of individual block lengths, so the inter-block gap bytes count toward the budget too.
            if (run.Count > 0 && block.Span.End - run[0].Span.Start + 1 > ChunkBudgetChars)
            {
                part++;
                yield return BuildChunk(run, document, LabelFor(section.HeadingText, part), usedIds);
                run = [];
            }

            run.Add(block);
        }

        if (run.Count > 0)
        {
            part++;
            yield return BuildChunk(run, document, LabelFor(section.HeadingText, part), usedIds);
        }
    }

    private static string? LabelFor(string? headingText, int part) =>
        headingText is null ? null : part == 1 ? headingText : $"{headingText} (continued)";

    private static DocumentChunk BuildChunk(List<Block> blocks, string document, string? headingPath, HashSet<string> usedIds)
    {
        var start = blocks[0].Span.Start;
        var length = blocks[^1].Span.End - start + 1;
        var text = length > 0 ? document.Substring(start, length).Trim() : string.Empty;
        return new DocumentChunk(NextId(usedIds), ClassifyKind(blocks), headingPath, text);
    }

    private static DocumentChunkKind ClassifyKind(List<Block> blocks)
    {
        var first = ClassifySingle(blocks[0]);
        return blocks.Count == 1 || blocks.All(b => ClassifySingle(b) == first) ? first : DocumentChunkKind.Other;
    }

    private static DocumentChunkKind ClassifySingle(Block block) => block switch
    {
        HeadingBlock => DocumentChunkKind.Heading,
        Table => DocumentChunkKind.Table,
        ListBlock => DocumentChunkKind.List,
        FencedCodeBlock or CodeBlock => DocumentChunkKind.Code,
        QuoteBlock => DocumentChunkKind.Quote,
        ParagraphBlock => DocumentChunkKind.Paragraph,
        _ => DocumentChunkKind.Other,
    };

    private static int SpanLength(List<Block> blocks) => Math.Max(0, blocks[^1].Span.End - blocks[0].Span.Start + 1);

    private static string NextId(HashSet<string> usedIds)
    {
        Span<char> buffer = stackalloc char[IdLength];
        while (true)
        {
            for (var i = 0; i < IdLength; i++)
            {
                buffer[i] = IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)];
            }

            var id = new string(buffer);
            if (usedIds.Add(id))
            {
                return id;
            }
        }
    }
}
