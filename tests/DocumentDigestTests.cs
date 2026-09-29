namespace Aukenid.Core.Tests;

using Aukenid.Core.Attachments;

public sealed class DocumentDigestTests
{
    [Fact]
    public void Parse_RoundTripsNotesAndRejectsADifferentLength()
    {
        var notes = new[] { "File: notes.md part 1/2\nalpha\nline", "File: notes.md part 2/2\nbeta" };
        var body = DocumentDigest.Format(12, 99, notes);

        var read = DocumentDigest.Parse(body, 12, 99);
        Assert.NotNull(read);
        Assert.Equal(notes, read);
        Assert.Null(DocumentDigest.Parse(body, 13, 99));
        Assert.Null(DocumentDigest.Parse(body, 12, 100));
    }

    [Fact]
    public void TryRead_ReusesTheFileUntilTheAttachmentChanges()
    {
        var dir = Directory.CreateTempSubdirectory("aukenid-digest");
        try
        {
            var path = Path.Combine(dir.FullName, "notes.md");
            File.WriteAllText(path, "hello");
            var notes = new[] { "File: notes.md part 1/2\nalpha", "File: notes.md part 2/2\nbeta" };
            DocumentDigest.Write(path, notes);

            var read = DocumentDigest.TryRead(path);
            Assert.NotNull(read);
            Assert.Equal(notes, read);
            Assert.Equal(path + DocumentDigest.Suffix, DocumentDigest.SidecarPath(path));

            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));
            Assert.Null(DocumentDigest.TryRead(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
