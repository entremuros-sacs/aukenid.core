using System.Net;
using System.Security.Cryptography;
using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests;

public sealed class ModelStoreTests
{
    [Fact]
    public void FindPath_MatchesIdFileAndUrlFileName()
    {
        var root = Path.Combine(Path.GetTempPath(), "aukenid-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ModelStore(root);
            File.WriteAllBytes(Path.Combine(root, "phi4-mini.gguf"), [1, 2, 3]);
            Assert.Equal(Path.Combine(root, "phi4-mini.gguf"), store.FindPath("phi4-mini", "https://example.com/phi.gguf"));

            File.WriteAllBytes(Path.Combine(root, "Llama-3.2-3B-Instruct-Q4_K_M.gguf"), [4, 5]);
            Assert.Equal(
                Path.Combine(root, "Llama-3.2-3B-Instruct-Q4_K_M.gguf"),
                store.FindPath("llama32-3b", "https://huggingface.co/org/model/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadAsync_WritesIdPrefixedGguf()
    {
        var root = Path.Combine(Path.GetTempPath(), "aukenid-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var payload = "gguf-bytes"u8.ToArray();
            var store = new ModelStore(root, new StaticHandler(payload));

            var path = await store.DownloadAsync("qwen35-4b", "https://example.com/qwen.gguf", progress: null, CancellationToken.None);

            Assert.Equal(Path.Combine(root, "qwen35-4b.gguf"), path);
            Assert.Equal(payload, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".partial"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadAsync_SkipsWhenAlreadyPresent()
    {
        var root = Path.Combine(Path.GetTempPath(), "aukenid-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new StaticHandler("new"u8.ToArray());
            var store = new ModelStore(root, handler);
            var existing = Path.Combine(root, "gemma4-e2b-it.gguf");
            File.WriteAllBytes(existing, "old"u8.ToArray());

            var path = await store.DownloadAsync("gemma4-e2b-it", "https://example.com/gemma.gguf", progress: null, CancellationToken.None);

            Assert.Equal(existing, path);
            Assert.Equal("old"u8.ToArray(), File.ReadAllBytes(existing));
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadAsync_KeepsFileWhenHashMatches()
    {
        var root = Path.Combine(Path.GetTempPath(), "aukenid-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var payload = "gguf-bytes"u8.ToArray();
            var store = new ModelStore(root, new StaticHandler(payload));

            var path = await store.DownloadAsync(
                "qwen35-4b",
                "https://example.com/qwen.gguf",
                progress: null,
                CancellationToken.None,
                Sha256(payload));

            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadAsync_DeletesFileWhenHashDiffers()
    {
        var root = Path.Combine(Path.GetTempPath(), "aukenid-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ModelStore(root, new StaticHandler("gguf-bytes"u8.ToArray()));
            var dest = Path.Combine(root, "qwen35-4b.gguf");

            await Assert.ThrowsAsync<ModelHashMismatchException>(() => store.DownloadAsync(
                "qwen35-4b",
                "https://example.com/qwen.gguf",
                progress: null,
                CancellationToken.None,
                "sha256:" + new string('a', 64)));

            Assert.False(File.Exists(dest));
            Assert.False(File.Exists(dest + ".partial"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadAsync_ReplacesExistingFileWhenHashDiffers()
    {
        var root = Path.Combine(Path.GetTempPath(), "aukenid-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var payload = "fresh"u8.ToArray();
            var handler = new StaticHandler(payload);
            var store = new ModelStore(root, handler);
            var existing = Path.Combine(root, "gemma4-e2b-it.gguf");
            File.WriteAllBytes(existing, "stale"u8.ToArray());

            var path = await store.DownloadAsync(
                "gemma4-e2b-it",
                "https://example.com/gemma.gguf",
                progress: null,
                CancellationToken.None,
                Sha256(payload));

            Assert.Equal(1, handler.Calls);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsMalformedHashBeforeDownload()
    {
        var root = Path.Combine(Path.GetTempPath(), "aukenid-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new StaticHandler("gguf-bytes"u8.ToArray());
            var store = new ModelStore(root, handler);
            var existing = Path.Combine(root, "phi4-mini.gguf");
            File.WriteAllBytes(existing, "keep"u8.ToArray());

            await Assert.ThrowsAsync<ModelHashMismatchException>(() => store.DownloadAsync(
                "phi4-mini",
                "https://example.com/phi.gguf",
                progress: null,
                CancellationToken.None,
                "sha256:abcd"));

            Assert.Equal(0, handler.Calls);
            Assert.Equal("keep"u8.ToArray(), await File.ReadAllBytesAsync(existing));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string Sha256(byte[] payload) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;

        public int Calls { get; private set; }

        public StaticHandler(byte[] bytes) => _bytes = bytes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_bytes),
            };
            response.Content.Headers.ContentLength = _bytes.Length;
            return Task.FromResult(response);
        }
    }
}
