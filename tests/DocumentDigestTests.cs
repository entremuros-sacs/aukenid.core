namespace Aukenid.Core.Tests;

using Aukenid.Core.Attachments;

[TestClass]
public sealed class DocumentDigestTests
{
    [TestMethod]
    public void Parse_RoundTripsNotesAndRejectsADifferentLength()
    {
        var notes = new[] { "File: notes.md part 1/2\nalpha\nline", "File: notes.md part 2/2\nbeta" };
        var body = DocumentDigest.Format(12, 99, notes);

        var read = DocumentDigest.Parse(body, 12, 99);
        Assert.IsNotNull(read);
        CollectionAssert.AreEqual(notes, read.ToArray());
        Assert.IsNull(DocumentDigest.Parse(body, 13, 99));
        Assert.IsNull(DocumentDigest.Parse(body, 12, 100));
    }

    [TestMethod]
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
            Assert.IsNotNull(read);
            CollectionAssert.AreEqual(notes, read.ToArray());
            Assert.AreEqual(path + DocumentDigest.Suffix, DocumentDigest.SidecarPath(path));

            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));
            Assert.IsNull(DocumentDigest.TryRead(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
