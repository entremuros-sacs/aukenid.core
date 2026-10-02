using Aukenid.Core.Data;

namespace Aukenid.Core.Tests;

public sealed class DocumentStoreTests
{
    [Fact]
    public void ReadDocument_ReturnsNullBeforeFirstSave()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);

        Assert.Null(workspace.Store.ReadDocument(thread.Id));
    }

    [Fact]
    public void SaveDocument_ThenReadDocument_RoundTrips()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);

        Assert.True(workspace.Store.SaveDocument(thread.Id, "# Title\n\nBody text."));

        Assert.Equal("# Title\n\nBody text.", workspace.Store.ReadDocument(thread.Id));
    }

    [Fact]
    public void SaveDocument_UnknownThread_ReturnsFalse()
    {
        using var workspace = new Workspace();

        Assert.False(workspace.Store.SaveDocument(Guid.NewGuid().ToString(), "content"));
    }

    [Fact]
    public void SnapshotDocumentVersion_WithNoDocumentYet_ReturnsNull()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);

        Assert.Null(workspace.Store.SnapshotDocumentVersion(thread.Id, "before-edit"));
        Assert.Empty(workspace.Store.ListDocumentVersions(thread.Id));
    }

    [Fact]
    public void SnapshotDocumentVersion_ListsNewestFirstAndReadsBack()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        workspace.Store.SaveDocument(thread.Id, "v1");
        var first = workspace.Store.SnapshotDocumentVersion(thread.Id, "before-edit");
        workspace.Store.SaveDocument(thread.Id, "v2");
        var second = workspace.Store.SnapshotDocumentVersion(thread.Id, "after-edit");

        Assert.NotNull(first);
        Assert.NotNull(second);
        var versions = workspace.Store.ListDocumentVersions(thread.Id);
        Assert.Equal(2, versions.Count);
        Assert.Equal(second!.Id, versions[0].Id);
        Assert.Equal("after-edit", versions[0].Label);
        Assert.Equal(first!.Id, versions[1].Id);
        Assert.Equal("v1", workspace.Store.ReadDocumentVersion(thread.Id, first.Id));
    }

    [Fact]
    public void RestoreDocumentVersion_ReplacesCurrentAndSnapshotsItFirst()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        workspace.Store.SaveDocument(thread.Id, "v1");
        var first = workspace.Store.SnapshotDocumentVersion(thread.Id, "before-edit")!;
        workspace.Store.SaveDocument(thread.Id, "v2");

        Assert.True(workspace.Store.RestoreDocumentVersion(thread.Id, first.Id));

        Assert.Equal("v1", workspace.Store.ReadDocument(thread.Id));
        var versions = workspace.Store.ListDocumentVersions(thread.Id);
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.Label == "before-restore");
    }

    [Fact]
    public void RestoreDocumentVersion_RejectsPathTraversalId()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        workspace.Store.SaveDocument(thread.Id, "v1");

        Assert.False(workspace.Store.RestoreDocumentVersion(thread.Id, "../conversation"));
        Assert.Null(workspace.Store.ReadDocumentVersion(thread.Id, "../conversation"));
    }

    [Fact]
    public void Document_IsStoredNextToConversationFile()
    {
        using var workspace = new Workspace();
        var thread = workspace.Store.CreateThread("Notes", ConversationStore.GeneralFolder);
        workspace.Store.SaveDocument(thread.Id, "content");

        var folderName = workspace.Store.GetThreadFolderName(thread.Id);
        Assert.NotNull(folderName);
        var documentPath = Path.Combine(workspace.ConversationsRoot, ConversationStore.GeneralFolder, folderName!, "document.md");
        Assert.True(File.Exists(documentPath));
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
