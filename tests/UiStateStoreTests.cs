using Aukenid.Core.Data;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class UiStateStoreTests
{
    [TestMethod]
    public void SetSelectedFolder_SurvivesReload()
    {
        var path = Path.Combine(Path.GetTempPath(), "aukenid-ui-state-tests", Guid.NewGuid().ToString("N"), "ui-state.json");
        try
        {
            var store = new UiStateStore(path);
            Assert.AreEqual(ConversationStore.GeneralFolder, store.SelectedFolder);

            store.SetSelectedFolder("Work");
            var reloaded = new UiStateStore(path);

            Assert.AreEqual("Work", reloaded.SelectedFolder);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Load_IgnoresCorruptFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "aukenid-ui-state-tests", Guid.NewGuid().ToString("N"), "ui-state.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{not json");

            var store = new UiStateStore(path);

            Assert.AreEqual(ConversationStore.GeneralFolder, store.SelectedFolder);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
