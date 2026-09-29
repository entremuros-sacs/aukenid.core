using Aukenid.Core.Services;
using Aukenid.Core.Contracts;

namespace Aukenid.Core.Tests;

public sealed class MarkdownPdfBlocksTests
{
    [Fact]
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

        Assert.Equal(3, blocks.Count);
        Assert.Equal("Before the table.", Assert.IsType<MarkdownPdfParagraph>(blocks[0]).Text);
        var table = Assert.IsType<MarkdownPdfTable>(blocks[1]);
        Assert.Equal(["Metric", "Value", "Notes"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["Accuracy", "92.4%", "held-out"], table.Rows[0]);
        Assert.Equal(["Latency", "38 ms", "p95"], table.Rows[1]);
        Assert.Equal("After the table.", Assert.IsType<MarkdownPdfParagraph>(blocks[2]).Text);
    }

    [Fact]
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
        Assert.Equal("Title", Assert.IsType<MarkdownPdfHeading>(blocks[0]).Text);
        var list = Assert.IsType<MarkdownPdfList>(blocks[1]);
        Assert.False(list.Ordered);
        Assert.Equal(["alpha", "beta"], list.Items);
        Assert.Equal("code", Assert.IsType<MarkdownPdfCode>(blocks[2]).Text);
    }

    [Fact]
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
        Assert.True(pdf.Length > 500);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf[..4]));
    }

    [Fact]
    public void BuildPdf_DoesNotThrowOnMissingGlyphs()
    {
        var thread = new ThreadDto("t1", "Export", "General", DateTimeOffset.UnixEpoch, true);
        var messages = new[]
        {
            new MessageDto("m1", "t1", null, "user", "GEMMA 4 es una LOCURA ⤵️", DateTimeOffset.UnixEpoch),
        };

        var pdf = ThreadExportService.BuildPdf(thread, messages);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf[..4]));
    }
}
