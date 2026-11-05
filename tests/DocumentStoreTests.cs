using Aukenid.Core.Data;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class DocumentStoreTests
{
    [TestMethod]
    public void ReadDocument_ReturnsNullBeforeFirstSave()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);

        Assert.IsNull(workspace.Store.ReadDocument(thread.Id));
    }

    [TestMethod]
    public void SaveDocument_ThenReadDocument_RoundTrips()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);

        Assert.IsTrue(workspace.Store.SaveDocument(thread.Id, "# Title\n\nBody text."));

        Assert.AreEqual("# Title\n\nBody text.", workspace.Store.ReadDocument(thread.Id));
    }

    [TestMethod]
    public void SaveDocument_UnknownThread_ReturnsFalse()
    {
        using var workspace = new Workspace();

        Assert.IsFalse(workspace.Store.SaveDocument(Guid.NewGuid().ToString(), "content"));
    }

    [TestMethod]
    public void SnapshotDocumentVersion_WithNoDocumentYet_ReturnsNull()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);

        Assert.IsNull(workspace.Store.SnapshotDocumentVersion(thread.Id, "before-edit"));
        Assert.AreEqual(0, workspace.Store.ListDocumentVersions(thread.Id).Count);
    }

    [TestMethod]
    public void SnapshotDocumentVersion_ListsNewestFirstAndReadsBack()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        workspace.Store.SaveDocument(thread.Id, "v1");
        var first = workspace.Store.SnapshotDocumentVersion(thread.Id, "before-edit");
        workspace.Store.SaveDocument(thread.Id, "v2");
        var second = workspace.Store.SnapshotDocumentVersion(thread.Id, "after-edit");

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        var versions = workspace.Store.ListDocumentVersions(thread.Id);
        Assert.AreEqual(2, versions.Count);
        Assert.AreEqual(second!.Id, versions[0].Id);
        Assert.AreEqual("after-edit", versions[0].Label);
        Assert.AreEqual(first!.Id, versions[1].Id);
        Assert.AreEqual("v1", workspace.Store.ReadDocumentVersion(thread.Id, first.Id));
    }

    [TestMethod]
    public void RestoreDocumentVersion_ReplacesCurrentAndSnapshotsItFirst()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        workspace.Store.SaveDocument(thread.Id, "v1");
        var first = workspace.Store.SnapshotDocumentVersion(thread.Id, "before-edit")!;
        workspace.Store.SaveDocument(thread.Id, "v2");

        Assert.IsTrue(workspace.Store.RestoreDocumentVersion(thread.Id, first.Id));

        Assert.AreEqual("v1", workspace.Store.ReadDocument(thread.Id));
        var versions = workspace.Store.ListDocumentVersions(thread.Id);
        Assert.AreEqual(2, versions.Count);
        Assert.IsTrue(versions.Any(v => v.Label == "before-restore"));
    }

    [TestMethod]
    public void RestoreDocumentVersion_RejectsPathTraversalId()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        workspace.Store.SaveDocument(thread.Id, "v1");

        Assert.IsFalse(workspace.Store.RestoreDocumentVersion(thread.Id, "../conversation"));
        Assert.IsNull(workspace.Store.ReadDocumentVersion(thread.Id, "../conversation"));
    }

    [TestMethod]
    public void Document_IsStoredNextToConversationFile()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        workspace.Store.SaveDocument(thread.Id, "content");

        var folderName = workspace.Store.GetThreadFolderName(thread.Id);
        Assert.IsNotNull(folderName);
        var documentPath = Path.Combine(workspace.ConversationsRoot, ConversationStore.GeneralFolder, folderName!, "document.md");
        Assert.IsTrue(File.Exists(documentPath));
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _root;

        public Workspace()
        {
            _root = Path.Combine(Path.GetTempPath(), "aukenid-document-tests", Guid.NewGuid().ToString("N"));
            ConversationsRoot = Path.Combine(_root, "conversations");
            Directory.CreateDirectory(_root);
            Store = new ConversationStore(ConversationsRoot);
        }

        public ConversationStore Store { get; }
        public string ConversationsRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
