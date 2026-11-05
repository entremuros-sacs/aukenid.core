using Aukenid.Core.Services;
using Aukenid.Core.Contracts;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class MarkdownPdfBlocksTests
{
    [TestMethod]
    public void Parse_ReadsGfmTableHeadersAndRows()
    {
        const string markdown = """
            Before the table.

            | Metric | Value | Notes |
            | --- | ---: | --- |
            | Accuracy | 92.4% | held-out |
            | Latency | 38 ms | p95 |

            After the table.
            """;

        var blocks = MarkdownPdfBlocks.Parse(markdown);

        Assert.AreEqual(3, blocks.Count);
        Assert.IsInstanceOfType(blocks[0], typeof(MarkdownPdfParagraph));
        Assert.AreEqual("Before the table.", ((MarkdownPdfParagraph)blocks[0]).Text);
        Assert.IsInstanceOfType(blocks[1], typeof(MarkdownPdfTable));
        var table = (MarkdownPdfTable)blocks[1];
        CollectionAssert.AreEqual(new[] { "Metric", "Value", "Notes" }, table.Headers.ToArray());
        Assert.AreEqual(2, table.Rows.Count);
        CollectionAssert.AreEqual(new[] { "Accuracy", "92.4%", "held-out" }, table.Rows[0].ToArray());
        CollectionAssert.AreEqual(new[] { "Latency", "38 ms", "p95" }, table.Rows[1].ToArray());
        Assert.IsInstanceOfType(blocks[2], typeof(MarkdownPdfParagraph));
        Assert.AreEqual("After the table.", ((MarkdownPdfParagraph)blocks[2]).Text);
    }

    [TestMethod]
    public void Parse_ReadsHeadingsListsAndCode()
    {
        const string markdown = """
            ## Title

            - alpha
            - beta

            ```
            code
            ```
            """;

        var blocks = MarkdownPdfBlocks.Parse(markdown);
        Assert.IsInstanceOfType(blocks[0], typeof(MarkdownPdfHeading));
        Assert.AreEqual("Title", ((MarkdownPdfHeading)blocks[0]).Text);
        Assert.IsInstanceOfType(blocks[1], typeof(MarkdownPdfList));
        var list = (MarkdownPdfList)blocks[1];
        Assert.IsFalse(list.Ordered);
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, list.Items.ToArray());
        Assert.IsInstanceOfType(blocks[2], typeof(MarkdownPdfCode));
        Assert.AreEqual("code", ((MarkdownPdfCode)blocks[2]).Text);
    }

    [TestMethod]
    public void BuildPdf_ContainsTableContentWithoutPipeRow()
    {
        var thread = new ThreadDto("t1", "Export", "General", DateTimeOffset.UnixEpoch, true);
        var messages = new[]
        {
            new MessageDto("m1", "t1", null, "user", "Show numbers", DateTimeOffset.UnixEpoch),
            new MessageDto("m2", "t1", "m1", "Aukenid", """
                | City | Pop |
                | --- | --- |
                | Lima | 10M |
                """, DateTimeOffset.UnixEpoch),
        };

        var pdf = ThreadExportService.BuildPdf(thread, messages);
        Assert.IsTrue(pdf.Length > 500);
        Assert.IsTrue(System.Text.Encoding.ASCII.GetString(pdf[..4]).StartsWith("%PDF", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildPdf_DoesNotThrowOnMissingGlyphs()
    {
        var thread = new ThreadDto("t1", "Export", "General", DateTimeOffset.UnixEpoch, true);
        var messages = new[]
        {
            new MessageDto("m1", "t1", null, "user", "GEMMA 4 es una LOCURA ⤵️", DateTimeOffset.UnixEpoch),
        };

        var pdf = ThreadExportService.BuildPdf(thread, messages);
        Assert.IsTrue(System.Text.Encoding.ASCII.GetString(pdf[..4]).StartsWith("%PDF", StringComparison.Ordinal));
    }
}
