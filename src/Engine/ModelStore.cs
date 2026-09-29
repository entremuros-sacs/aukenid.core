namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Folder where GGUF model files live, created on first run.</summary>
public sealed class ModelStore
{
    private readonly HttpMessageHandler? _handler;

    public string RootPath { get; }

    public ModelStore(string rootPath, HttpMessageHandler? handler = null)
    {
        RootPath = rootPath;
        Directory.CreateDirectory(RootPath);
        _handler = handler;
    }

    /// <summary>Returns the first GGUF found (alphabetically), or null if the folder is empty.</summary>
    public string? FindFirstModel() =>
        Directory.EnumerateFiles(RootPath, "*.gguf", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    public IReadOnlyList<string> ListFileNames() =>
        Directory.EnumerateFiles(RootPath, "*.gguf", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();

    public string? FindPath(string id, string? url)
    {
        var byId = Path.Combine(RootPath, id + ".gguf");
        if (File.Exists(byId))
        {
            return byId;
        }

        var fromUrl = FileNameFromUrl(url);
        if (fromUrl is not null)
        {
            var byUrl = Path.Combine(RootPath, fromUrl);
            if (File.Exists(byUrl))
            {
                return byUrl;
            }
        }

        return null;
    }

    public async Task<string> DownloadAsync(
        string id,
        string url,
        IProgress<(long Received, long? Total)>? progress,
        CancellationToken cancellationToken,
        string? expectedHash = null)
    {
        byte[]? expected = null;
        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            if (!TryDecodeSha256(expectedHash, out var decoded))
                throw new ModelHashMismatchException();

            expected = decoded;
        }

        var existing = FindPath(id, url);
        if (existing is not null)
        {
            if (expected is null || HashEquals(existing, expected))
            {
                return existing;
            }

            File.Delete(existing);
        }

        var dest = Path.Combine(RootPath, id + ".gguf");
        var partial = dest + ".partial";
        if (File.Exists(partial))
        {
            File.Delete(partial);
        }

        using var http = CreateHttpClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 80 * 1024, useAsync: true))
        {
            var buffer = new byte[80 * 1024];
            long received = 0;
            progress?.Report((0, total));

            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Report((received, total));
            }
        }

        if (expected is not null && !HashEquals(partial, expected))
        {
            File.Delete(partial);
            throw new ModelHashMismatchException();
        }

        File.Move(partial, dest, overwrite: true);
        return dest;
    }

    public static bool TryDecodeSha256(string hash, out byte[] bytes)
    {
        var hex = hash.Trim();
        const string prefix = "sha256:";

        if (hex.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            hex = hex[prefix.Length..].Trim();

        try
        {
            bytes = Convert.FromHexString(hex);
            return bytes.Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    public static bool HashEquals(string filePath, byte[] expectedHash)
    {
        using var stream = File.OpenRead(filePath);
        var actualHash = SHA256.HashData(stream);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    public static string? FileNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private HttpClient CreateHttpClient()
    {
        var client = _handler is null
            ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aukenid/1.0");
        return client;
    }
}

public sealed class ModelHashMismatchException : Exception
{
    public ModelHashMismatchException()
        : base("Model file hash does not match the gallery.")
    {
    }
}
