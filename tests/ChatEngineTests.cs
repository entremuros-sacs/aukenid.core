using System.Runtime.InteropServices;
using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class ChatEngineTests
{
    [TestMethod]
    public async Task CompleteAsync_ConcatenatesStreamTokens()
    {
        var engine = new NoChatEngine();
        await engine.LoadModelAsync("model", CancellationToken.None);

        var reply = await engine.CompleteAsync(
            [new ChatTurn("user", "Hello")],
            CancellationToken.None);

        Assert.AreEqual("[model] Hello ", reply);
    }

    [TestMethod]
    public void NativeRuntime_SearchDirectoriesIncludeAssemblyAndRidNative()
    {
        var directories = NativeRuntime.SearchDirectories();
        var root = Path.GetDirectoryName(typeof(NativeRuntime).Assembly.Location);
        Assert.IsTrue(directories.Contains(root));

        var ridNative = Path.Combine(root!, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        Assert.IsTrue(directories.Contains(ridNative));
    }
}
