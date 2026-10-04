using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

public sealed class DocumentChunkerTests
{
    [Fact]
    public void Chunk_EmptyDocument_ReturnsNoChunks()
    {
        Assert.Empty(DocumentChunker.Chunk(null));
        Assert.Empty(DocumentChunker.Chunk("   "));
    }

    [Fact]
    public void Chunk_OneChunkPerHeadingSection()
    {
        var doc = "# Title\n\nIntro paragraph.\n\n## Ingredients\n\n- flour\n- sugar\n\n## Steps\n\nMix it.\n";

        var chunks = DocumentChunker.Chunk(doc);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("# Title\n\nIntro paragraph.", chunks[0].Text);
        Assert.Contains("- flour", chunks[1].Text);
        Assert.Equal("Steps", chunks[2].HeadingPath);
    }

    [Fact]
    public void Chunk_AssignsUniqueIds()
    {
        var doc = "# A\n\ntext\n\n## B\n\ntext2\n\n## C\n\ntext3\n";

        var chunks = DocumentChunker.Chunk(doc);

        Assert.Equal(chunks.Count, chunks.Select(c => c.Id).Distinct().Count());
        Assert.All(chunks, c => Assert.Equal(5, c.Id.Length));
    }

    [Fact]
    public void Chunk_ContentWithoutLeadingHeading_IsItsOwnSection()
    {
        var doc = "Just a paragraph with no heading at all.\n";

        var chunks = DocumentChunker.Chunk(doc);

        var chunk = Assert.Single(chunks);
        Assert.Null(chunk.HeadingPath);
        Assert.Equal(DocumentChunkKind.Paragraph, chunk.Kind);
    }

    [Fact]
    public void Chunk_OversizedSection_SplitsAtBlockBoundaries_NeverMidBlock()
    {
        var paragraphs = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraph number {i} with some filler text to add length."));
        var doc = $"# Big Section\n\n{paragraphs}\n";

        var chunks = DocumentChunker.Chunk(doc);

        Assert.True(chunks.Count > 1, "An oversized section should be split into multiple chunks.");
        Assert.All(chunks, c => Assert.True(c.Text.Length <= DocumentChunker.ChunkBudgetChars + 50));
        // Every paragraph's exact text must survive somewhere, untouched.
        for (var i = 0; i < 30; i++)
        {
            Assert.Contains(chunks, c => c.Text.Contains($"Paragraph number {i} "));
        }

        Assert.Equal("Big Section", chunks[0].HeadingPath);
        Assert.Contains(chunks, c => c.HeadingPath == "Big Section (continued)");
    }

    [Fact]
    public void Chunk_SingleBlockLargerThanBudget_IsKeptWholeNotSplit()
    {
        var hugeTableRows = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"| {i} | value-{i} |"));
        var doc = $"# Data\n\n| A | B |\n| - | - |\n{hugeTableRows}\n";

        var chunks = DocumentChunker.Chunk(doc);

        var tableChunk = Assert.Single(chunks, c => c.Kind == DocumentChunkKind.Table);
        Assert.True(tableChunk.Text.Length > DocumentChunker.ChunkBudgetChars);
        Assert.Contains("value-199", tableChunk.Text);
    }

    [Fact]
    public void Chunk_OversizedSection_NeverSplitsACodeBlockMidWay()
    {
        var filler = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"Paragraph {i} with enough filler text to add real length to this section."));
        var code = "```csharp\nvar x = 1;\nvar y = 2;\n```";
        var doc = $"# Section\n\n{filler}\n\n{code}\n\n{filler}\n";

        var chunks = DocumentChunker.Chunk(doc);

        var codeChunk = Assert.Single(chunks, c => c.Text.Contains("var x = 1;"));
        Assert.Contains(code, codeChunk.Text);
    }
}
