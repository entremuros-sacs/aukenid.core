namespace Aukenid.Core.Tests;

using System.Text;
using Aukenid.Core.Attachments;

[TestClass]
public sealed class DocumentPassTests
{
    [TestMethod]
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

        Assert.IsTrue(slices.Count > 1);
        Assert.AreEqual(1, slices[0].FromPage);
        Assert.AreEqual(30, slices[^1].ToPage);
        var covered = new HashSet<int>();
        foreach (var slice in slices)
        {
            Assert.IsTrue(slice.FromPage <= slice.ToPage);
            for (var page = slice.FromPage; page <= slice.ToPage; page++)
            {
                covered.Add(page);
            }

            Assert.IsTrue(slice.Text.Contains($"--- page {slice.FromPage} ---", StringComparison.Ordinal));
        }

        Assert.AreEqual(30, covered.Count);
    }

    [TestMethod]
    public void NeedsPass_IsFalseWhenTheDocumentFits()
    {
        Assert.IsFalse(DocumentPass.NeedsPass(otherChars: 1000, documentChars: 3000));
        Assert.IsTrue(DocumentPass.NeedsPass(otherChars: 1000, documentChars: DocumentPass.ContextChars));
    }

    [TestMethod]
    public void DigestTurns_NamesThePageRangeAndSkipsTheQuestion()
    {
        var turns = DocumentPass.DigestTurns(
            new DocumentPass.Slice("contract.pdf", 3, 8, "--- page 3 ---\nrent"));

        Assert.IsTrue(turns[0].Content.Contains("pages 3–8", StringComparison.Ordinal));
        Assert.IsTrue(turns[0].Content.Contains("standalone digest", StringComparison.Ordinal));
        Assert.IsTrue(turns[0].Content.Contains("--- page 3 ---", StringComparison.Ordinal));
        Assert.IsFalse(turns[0].Content.Contains("NONE", StringComparison.Ordinal));
        Assert.AreEqual("user", turns[1].Role);
    }

    [TestMethod]
    public void NoteFor_LabelsTheSliceAndClipsTheBody()
    {
        var paged = DocumentPass.NoteFor(
            new DocumentPass.Slice("contract.pdf", 3, 8, "rent"),
            "the tenant pays all repairs");
        Assert.IsTrue(paged.Contains("pages 3-8", StringComparison.Ordinal));
        Assert.IsTrue(paged.Contains("the tenant pays all repairs", StringComparison.Ordinal));

        var part = DocumentPass.NoteFor(
            new DocumentPass.Slice("notes.md", 1, 1, "x", Paged: false, Part: 2, PartCount: 3),
            new string('q', DocumentPass.DigestChars + 40));
        Assert.IsTrue(part.Contains("part 2/3", StringComparison.Ordinal));
        Assert.IsTrue(part.EndsWith("…", StringComparison.Ordinal));
        Assert.IsFalse(part.Contains("pages ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Fit_KeepsEveryPartInsideTheRoom()
    {
        var notes = new[]
        {
            "File: a.md part 1/2\n" + new string('a', 500),
            "File: b.md part 2/2\n" + new string('b', 500),
        };

        var fitted = DocumentPass.Fit(notes, 200);

        Assert.IsTrue(fitted.Length <= 200);
        Assert.IsTrue(fitted.Contains("File: a.md part 1/2", StringComparison.Ordinal));
        Assert.IsTrue(fitted.Contains("File: b.md part 2/2", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Pack_TreatsMarkdownAsUnpagedText()
    {
        var markdown = "# Notes\n\n" + new string('a', 2500);
        var slices = DocumentPass.Pack([new AttachmentExcerpt("notes.md", markdown, null)]);

        Assert.AreEqual(1, slices.Count);
        Assert.IsFalse(slices[0].Paged);
        Assert.AreEqual(1, slices[0].Part);
        Assert.AreEqual(1, slices[0].PartCount);
        Assert.IsFalse(slices[0].Text.Contains("--- page", StringComparison.Ordinal));
        Assert.IsTrue(slices[0].Text.Contains("# Notes", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Pack_SplitsALongMarkdownFileIntoParts()
    {
        var markdown = new string('b', DocumentPass.SliceChars + 50);
        var slices = DocumentPass.Pack([new AttachmentExcerpt("notes.md", markdown, null)]);

        Assert.AreEqual(2, slices.Count);
        foreach (var slice in slices)
        {
            Assert.IsFalse(slice.Paged);
            Assert.AreEqual(2, slice.PartCount);
            Assert.IsFalse(slice.Text.Contains("--- page", StringComparison.Ordinal));
        }
        Assert.AreEqual(1, slices[0].Part);
        Assert.AreEqual(2, slices[1].Part);
        Assert.AreEqual(DocumentPass.SliceChars + 50, slices[0].Text.Length + slices[1].Text.Length);
    }

    [TestMethod]
    public void DigestTurns_DoesNotInventPagesForMarkdown()
    {
        var turns = DocumentPass.DigestTurns(
            new DocumentPass.Slice("notes.md", 1, 1, "# Notes", Paged: false, Part: 2, PartCount: 3));

        Assert.IsTrue(turns[0].Content.Contains("notes.md, part 2 of 3", StringComparison.Ordinal));
        Assert.IsFalse(turns[0].Content.Contains("pages ", StringComparison.Ordinal));
        Assert.IsTrue(DocumentPass.ReducePrompt("notes").Contains("part named on each note", StringComparison.Ordinal));
    }
}
