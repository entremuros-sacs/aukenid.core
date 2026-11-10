using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class DocumentChunkerTests
{
    [TestMethod]
    public void Chunk_EmptyDocument_ReturnsNoChunks()
    {
        Assert.AreEqual(0, DocumentChunker.Chunk(null).Count);
        Assert.AreEqual(0, DocumentChunker.Chunk("   ").Count);
    }

    [TestMethod]
    public void Chunk_OneChunkPerHeadingSection()
    {
        var doc = "# Title\n\nIntro paragraph.\n\n## Ingredients\n\n- flour\n- sugar\n\n## Steps\n\nMix it.\n";

        var chunks = DocumentChunker.Chunk(doc);

        Assert.AreEqual(3, chunks.Count);
        Assert.AreEqual("# Title\n\nIntro paragraph.", chunks[0].Text);
        Assert.IsTrue(chunks[1].Text.Contains("- flour", StringComparison.Ordinal));
        Assert.AreEqual("Steps", chunks[2].HeadingPath);
    }

    [TestMethod]
    public void Chunk_AssignsUniqueIds()
    {
        var doc = "# A\n\ntext\n\n## B\n\ntext2\n\n## C\n\ntext3\n";

        var chunks = DocumentChunker.Chunk(doc);

        Assert.AreEqual(chunks.Count, chunks.Select(c => c.Id).Distinct().Count());
        foreach (var chunk in chunks)
        {
            Assert.AreEqual(8, chunk.Id.Length);
        }
    }

    [TestMethod]
    public void Chunk_ContentWithoutLeadingHeading_IsItsOwnSection()
    {
        var doc = "Just a paragraph with no heading at all.\n";

        var chunks = DocumentChunker.Chunk(doc);

        var chunk = chunks.Single();
        Assert.IsNull(chunk.HeadingPath);
        Assert.AreEqual(DocumentChunkKind.Paragraph, chunk.Kind);
    }

    [TestMethod]
    public void Chunk_OversizedSection_SplitsAtBlockBoundaries_NeverMidBlock()
    {
        var paragraphs = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraph number {i} with some filler text to add length."));
        var doc = $"# Big Section\n\n{paragraphs}\n";

        var chunks = DocumentChunker.Chunk(doc);

        Assert.IsTrue(chunks.Count > 1, "An oversized section should be split into multiple chunks.");
        foreach (var chunk in chunks)
        {
            Assert.IsTrue(chunk.Text.Length <= DocumentChunker.ChunkBudgetChars + 50);
        }
        // Every paragraph's exact text must survive somewhere, untouched.
        for (var i = 0; i < 30; i++)
        {
            Assert.IsTrue(chunks.Any(c => c.Text.Contains($"Paragraph number {i} ", StringComparison.Ordinal)));
        }

        Assert.AreEqual("Big Section", chunks[0].HeadingPath);
        Assert.IsTrue(chunks.Any(c => c.HeadingPath == "Big Section (continued)"));
    }

    [TestMethod]
    public void Chunk_SingleBlockLargerThanBudget_IsKeptWholeNotSplit()
    {
        var hugeTableRows = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"| {i} | value-{i} |"));
        var doc = $"# Data\n\n| A | B |\n| - | - |\n{hugeTableRows}\n";

        var chunks = DocumentChunker.Chunk(doc);

        var tableChunk = chunks.Single(c => c.Kind == DocumentChunkKind.Table);
        Assert.IsTrue(tableChunk.Text.Length > DocumentChunker.ChunkBudgetChars);
        Assert.IsTrue(tableChunk.Text.Contains("value-199", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Chunk_OversizedSection_NeverSplitsACodeBlockMidWay()
    {
        var filler = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"Paragraph {i} with enough filler text to add real length to this section."));
        var code = "```csharp\nvar x = 1;\nvar y = 2;\n```";
        var doc = $"# Section\n\n{filler}\n\n{code}\n\n{filler}\n";

        var chunks = DocumentChunker.Chunk(doc);

        var codeChunk = chunks.Single(c => c.Text.Contains("var x = 1;", StringComparison.Ordinal));
        Assert.IsTrue(codeChunk.Text.Contains(code, StringComparison.Ordinal));
    }
}
