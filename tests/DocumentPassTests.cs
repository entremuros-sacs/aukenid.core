namespace Aukenid.Core.Tests;

using System.Text;
using Aukenid.Core.Attachments;

public sealed class DocumentPassTests
{
    [Fact]
    public void Pack_KeepsEveryPageAndSplitsWhenTheSliceFills()
    {
        var pages = new StringBuilder();
        for (var page = 1; page <= 30; page++)
        {
            pages.Append("--- page ");
            pages.Append(page);
            pages.AppendLine(" ---");
            pages.AppendLine(new string('a', 2000));
        }

        var slices = DocumentPass.Pack([new AttachmentExcerpt("contract.pdf", pages.ToString(), null)]);

        Assert.True(slices.Count > 1);
        Assert.Equal(1, slices[0].FromPage);
        Assert.Equal(30, slices[^1].ToPage);
        var covered = new HashSet<int>();
        foreach (var slice in slices)
        {
            Assert.True(slice.FromPage <= slice.ToPage);
            for (var page = slice.FromPage; page <= slice.ToPage; page++)
            {
                covered.Add(page);
            }

            Assert.Contains($"--- page {slice.FromPage} ---", slice.Text, StringComparison.Ordinal);
        }

        Assert.Equal(30, covered.Count);
    }

    [Fact]
    public void NeedsPass_IsFalseWhenTheDocumentFits()
    {
        Assert.False(DocumentPass.NeedsPass(otherChars: 1000, documentChars: 3000));
        Assert.True(DocumentPass.NeedsPass(otherChars: 1000, documentChars: DocumentPass.ContextChars));
    }

    [Fact]
    public void DigestTurns_NamesThePageRangeAndSkipsTheQuestion()
    {
        var turns = DocumentPass.DigestTurns(
            new DocumentPass.Slice("contract.pdf", 3, 8, "--- page 3 ---\nrent"));

        Assert.Contains("pages 3–8", turns[0].Content, StringComparison.Ordinal);
        Assert.Contains("standalone digest", turns[0].Content, StringComparison.Ordinal);
        Assert.Contains("--- page 3 ---", turns[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("NONE", turns[0].Content, StringComparison.Ordinal);
        Assert.Equal("user", turns[1].Role);
    }

    [Fact]
    public void NoteFor_LabelsTheSliceAndClipsTheBody()
    {
        var paged = DocumentPass.NoteFor(
            new DocumentPass.Slice("contract.pdf", 3, 8, "rent"),
            "the tenant pays all repairs");
        Assert.Contains("pages 3-8", paged, StringComparison.Ordinal);
        Assert.Contains("the tenant pays all repairs", paged, StringComparison.Ordinal);

        var part = DocumentPass.NoteFor(
            new DocumentPass.Slice("notes.md", 1, 1, "x", Paged: false, Part: 2, PartCount: 3),
            new string('q', DocumentPass.DigestChars + 40));
        Assert.Contains("part 2/3", part, StringComparison.Ordinal);
        Assert.EndsWith("…", part);
        Assert.DoesNotContain("pages ", part, StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_KeepsEveryPartInsideTheRoom()
    {
        var notes = new[]
        {
            "File: a.md part 1/2\n" + new string('a', 500),
            "File: b.md part 2/2\n" + new string('b', 500),
        };

        var fitted = DocumentPass.Fit(notes, 200);

        Assert.True(fitted.Length <= 200);
        Assert.Contains("File: a.md part 1/2", fitted, StringComparison.Ordinal);
        Assert.Contains("File: b.md part 2/2", fitted, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_TreatsMarkdownAsUnpagedText()
    {
        var markdown = "# Notes\n\n" + new string('a', 2500);
        var slices = DocumentPass.Pack([new AttachmentExcerpt("notes.md", markdown, null)]);

        Assert.Single(slices);
        Assert.False(slices[0].Paged);
        Assert.Equal(1, slices[0].Part);
        Assert.Equal(1, slices[0].PartCount);
        Assert.DoesNotContain("--- page", slices[0].Text, StringComparison.Ordinal);
        Assert.Contains("# Notes", slices[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_SplitsALongMarkdownFileIntoParts()
    {
        var markdown = new string('b', DocumentPass.SliceChars + 50);
        var slices = DocumentPass.Pack([new AttachmentExcerpt("notes.md", markdown, null)]);

        Assert.Equal(2, slices.Count);
        Assert.All(slices, slice =>
        {
            Assert.False(slice.Paged);
            Assert.Equal(2, slice.PartCount);
            Assert.DoesNotContain("--- page", slice.Text, StringComparison.Ordinal);
        });
        Assert.Equal(1, slices[0].Part);
        Assert.Equal(2, slices[1].Part);
        Assert.Equal(DocumentPass.SliceChars + 50, slices[0].Text.Length + slices[1].Text.Length);
    }

    [Fact]
    public void DigestTurns_DoesNotInventPagesForMarkdown()
    {
        var turns = DocumentPass.DigestTurns(
            new DocumentPass.Slice("notes.md", 1, 1, "# Notes", Paged: false, Part: 2, PartCount: 3));

        Assert.Contains("notes.md, part 2 of 3", turns[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("pages ", turns[0].Content, StringComparison.Ordinal);
        Assert.Contains("part named on each note", DocumentPass.ReducePrompt("notes"), StringComparison.Ordinal);
    }
}
