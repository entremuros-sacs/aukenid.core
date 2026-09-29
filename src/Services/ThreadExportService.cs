namespace Aukenid.Core.Services;

using Aukenid.Core.Contracts;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

/// <summary>PDF transcript of a thread: markdown messages with real GFM tables, not pipe text.</summary>
internal static class ThreadExportService
{
    static ThreadExportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        QuestPDF.Settings.UseSystemFonts = true;
    }

    public static byte[] BuildPdf(ThreadDto thread, IReadOnlyList<MessageDto> messages)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(40);
                page.Size(PageSizes.A4);
                page.DefaultTextStyle(x => x.FontSize(10.5f).FontColor(Colors.Grey.Darken4).LineHeight(1.35f));
                page.PageColor(Colors.White);

                page.Header().Column(header =>
                {
                    header.Item().Text(thread.Title).FontSize(18).Bold().FontColor(Colors.Grey.Darken4);
                    header.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().PaddingTop(16).Column(column =>
                {
                    column.Spacing(16);
                    foreach (var message in messages)
                    {
                        column.Item().Column(item =>
                        {
                            item.Item().Text(message.Role.ToUpperInvariant()).FontSize(8).Bold()
                                .FontColor(Colors.Grey.Medium).LetterSpacing(0.4f);
                            item.Item().PaddingTop(6).Column(body =>
                            {
                                body.Spacing(8);
                                foreach (var block in MarkdownPdfBlocks.Parse(message.Content))
                                {
                                    body.Item().Element(container => RenderBlock(container, block));
                                }
                            });
                        });
                    }
                });

                page.Footer().AlignCenter().DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Medium)).Text(x =>
                {
                    x.CurrentPageNumber();
                    x.Span(" / ");
                    x.TotalPages();
                });
            });
        });

        return document.GeneratePdf();
    }

    private static void RenderBlock(IContainer container, MarkdownPdfBlock block)
    {
        switch (block)
        {
            case MarkdownPdfHeading heading:
                container.Text(heading.Text).Bold().FontSize(heading.Level <= 2 ? 13 : 11.5f);
                break;
            case MarkdownPdfParagraph paragraph:
                container.Text(paragraph.Text);
                break;
            case MarkdownPdfQuote quote:
                container.BorderLeft(2).BorderColor(Colors.Grey.Lighten1).PaddingLeft(10)
                    .Text(quote.Text).Italic().FontColor(Colors.Grey.Darken2);
                break;
            case MarkdownPdfCode code:
                container.Background(Colors.Grey.Lighten4).Padding(8).Text(code.Text).FontFamily("Courier New").FontSize(9);
                break;
            case MarkdownPdfList list:
                container.Column(column =>
                {
                    column.Spacing(3);
                    for (var i = 0; i < list.Items.Count; i++)
                    {
                        var marker = list.Ordered ? $"{i + 1}." : "•";
                        var text = list.Items[i];
                        column.Item().Row(row =>
                        {
                            row.ConstantItem(16).Text(marker);
                            row.RelativeItem().Text(text);
                        });
                    }
                });
                break;
            case MarkdownPdfTable table:
                RenderTable(container, table);
                break;
            default:
                container.Text(string.Empty);
                break;
        }
    }

    private static void RenderTable(IContainer container, MarkdownPdfTable table)
    {
        var columnCount = table.Headers.Count;
        foreach (var row in table.Rows)
        {
            columnCount = Math.Max(columnCount, row.Count);
        }

        if (columnCount == 0)
        {
            return;
        }

        container.Table(pdfTable =>
        {
            pdfTable.ColumnsDefinition(columns =>
            {
                for (var i = 0; i < columnCount; i++)
                {
                    columns.RelativeColumn();
                }
            });

            if (table.Headers.Count > 0)
            {
                pdfTable.Header(header =>
                {
                    foreach (var cell in Pad(table.Headers, columnCount))
                    {
                        header.Cell().Element(c => HeaderCell(c)).Text(cell).Bold().FontSize(9);
                    }
                });
            }

            var stripe = false;
            foreach (var row in table.Rows)
            {
                var background = stripe ? Colors.Grey.Lighten4 : Colors.White;
                stripe = !stripe;
                foreach (var cell in Pad(row, columnCount))
                {
                    pdfTable.Cell().Element(c => BodyCell(c, background)).Text(cell).FontSize(9);
                }
            }
        });
    }

    private static IContainer HeaderCell(IContainer container) =>
        container
            .Border(0.6f)
            .BorderColor(Colors.Grey.Lighten1)
            .Background(Colors.Grey.Lighten3)
            .PaddingVertical(6)
            .PaddingHorizontal(8);

    private static IContainer BodyCell(IContainer container, string background) =>
        container
            .Border(0.6f)
            .BorderColor(Colors.Grey.Lighten1)
            .Background(background)
            .PaddingVertical(5)
            .PaddingHorizontal(8);

    private static IEnumerable<string> Pad(IReadOnlyList<string> cells, int columns)
    {
        for (var i = 0; i < columns; i++)
        {
            yield return i < cells.Count ? cells[i] : string.Empty;
        }
    }
}
