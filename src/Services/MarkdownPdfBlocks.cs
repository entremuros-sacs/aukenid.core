namespace Aukenid.Core.Services;

using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

internal abstract record MarkdownPdfBlock;

internal sealed record MarkdownPdfParagraph(string Text) : MarkdownPdfBlock;

internal sealed record MarkdownPdfHeading(int Level, string Text) : MarkdownPdfBlock;

internal sealed record MarkdownPdfList(bool Ordered, IReadOnlyList<string> Items) : MarkdownPdfBlock;

internal sealed record MarkdownPdfCode(string Text) : MarkdownPdfBlock;

internal sealed record MarkdownPdfQuote(string Text) : MarkdownPdfBlock;

internal sealed record MarkdownPdfTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownPdfBlock;

internal static class MarkdownPdfBlocks
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    public static IReadOnlyList<MarkdownPdfBlock> Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var document = Markdown.Parse(markdown, Pipeline);
        var blocks = new List<MarkdownPdfBlock>();
        foreach (var block in document)
        {
            var parsed = ParseBlock(block);
            if (parsed is not null)
            {
                blocks.Add(parsed);
            }
        }

        return blocks.Count > 0 ? blocks : [new MarkdownPdfParagraph(markdown.Trim())];
    }

    private static MarkdownPdfBlock? ParseBlock(Block block) =>
        block switch
        {
            Table table => ParseTable(table),
            HeadingBlock heading => new MarkdownPdfHeading(heading.Level, InlineText(heading.Inline)),
            ListBlock list => ParseList(list),
            FencedCodeBlock code => new MarkdownPdfCode(code.Lines.ToString().TrimEnd()),
            CodeBlock indented => new MarkdownPdfCode(indented.Lines.ToString().TrimEnd()),
            QuoteBlock quote => new MarkdownPdfQuote(BlockText(quote)),
            ParagraphBlock paragraph => ParagraphOrNull(InlineText(paragraph.Inline)),
            ThematicBreakBlock => null,
            _ => ParagraphOrNull(BlockText(block)),
        };

    private static MarkdownPdfTable ParseTable(Table table)
    {
        var headers = new List<string>();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row)
            {
                continue;
            }

            var cells = row.OfType<TableCell>().Select(BlockText).ToList();
            if (row.IsHeader && headers.Count == 0)
            {
                headers.AddRange(cells);
            }
            else
            {
                rows.Add(cells);
            }
        }

        return new MarkdownPdfTable(headers, rows);
    }

    private static MarkdownPdfList ParseList(ListBlock list)
    {
        var items = new List<string>();
        foreach (var item in list)
        {
            if (item is ListItemBlock listItem)
            {
                items.Add(BlockText(listItem));
            }
        }

        return new MarkdownPdfList(list.IsOrdered, items);
    }

    private static MarkdownPdfParagraph? ParagraphOrNull(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : new MarkdownPdfParagraph(text);

    private static string BlockText(Block block)
    {
        var sb = new StringBuilder();
        AppendBlock(block, sb);
        return Collapse(sb.ToString());
    }

    private static void AppendBlock(Block block, StringBuilder sb)
    {
        switch (block)
        {
            case LeafBlock leaf when leaf.Inline is not null:
                AppendInlines(leaf.Inline, sb);
                break;
            case ContainerBlock container:
                foreach (var child in container)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(' ');
                    }

                    AppendBlock(child, sb);
                }

                break;
        }
    }

    private static string InlineText(ContainerInline? inline)
    {
        var sb = new StringBuilder();
        AppendInlines(inline, sb);
        return Collapse(sb.ToString());
    }

    private static void AppendInlines(ContainerInline? inline, StringBuilder sb)
    {
        for (var current = inline?.FirstChild; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case LinkInline link:
                    var start = sb.Length;
                    AppendInlines(link, sb);
                    var label = sb.ToString(start, sb.Length - start);
                    if (!string.IsNullOrWhiteSpace(link.Url) && !string.Equals(label, link.Url, StringComparison.Ordinal))
                    {
                        sb.Append(" (");
                        sb.Append(link.Url);
                        sb.Append(')');
                    }

                    break;
                case ContainerInline nested:
                    AppendInlines(nested, sb);
                    break;
            }
        }
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}
