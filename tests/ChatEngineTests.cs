using System.Runtime.InteropServices;
using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests;

public sealed class ChatEngineTests
{
    [Fact]
    public async Task CompleteAsync_ConcatenatesStreamTokens()
    {
        var engine = new NoChatEngine();
        await engine.LoadModelAsync("model", CancellationToken.None);

        var reply = await engine.CompleteAsync(
            [new ChatTurn("user", "Hello")],
            CancellationToken.None);

        Assert.Equal("[model] Hello ", reply);
    }

    [Fact]
    public void NativeRuntime_SearchDirectoriesIncludeAssemblyAndRidNative()
    {
        var directories = NativeRuntime.SearchDirectories();
        var root = Path.GetDirectoryName(typeof(NativeRuntime).Assembly.Location);
        Assert.Contains(root, directories);

        var ridNative = Path.Combine(root!, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        Assert.Contains(ridNative, directories);
    }
}
