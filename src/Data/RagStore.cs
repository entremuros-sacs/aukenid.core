namespace Aukenid.Core.Data;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Folder where downloaded .rag knowledge library files live.</summary>
public sealed class RagStore
{
    private readonly HttpMessageHandler? _handler;

    public string RootPath { get; }

    public RagStore(string rootPath, HttpMessageHandler? handler = null)
    {
        RootPath = rootPath;
        Directory.CreateDirectory(RootPath);
        _handler = handler;
    }

    public IReadOnlyList<string> ListFileNames() =>
        Directory.EnumerateFiles(RootPath, "*.rag", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();

    public string? FindPath(string id, string? url)
    {
        var byId = Path.Combine(RootPath, id + ".rag");
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
        byte[]? expectedMd5 = null;
        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            if (!TryDecodeMd5(expectedHash, out var decoded))
                throw new RagHashMismatchException();

            expectedMd5 = decoded;
        }

        var existing = FindPath(id, url);
        if (existing is not null)
        {
            if (expectedMd5 is null || Md5Equals(existing, expectedMd5))
            {
                return existing;
            }

            File.Delete(existing);
        }

        var dest = Path.Combine(RootPath, id + ".rag");
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

        if (expectedMd5 is not null && !Md5Equals(partial, expectedMd5))
        {
            File.Delete(partial);
            throw new RagHashMismatchException();
        }

        File.Move(partial, dest, overwrite: true);
        return dest;
    }

    public static bool TryDecodeMd5(string hash, out byte[] bytes)
    {
        var value = hash.Trim();
        const string prefix = "md5chksum:";

        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..].Trim();

        try
        {
            bytes = Convert.FromBase64String(value);
            return bytes.Length == 16;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    public static bool Md5Equals(string filePath, byte[] expectedMd5)
    {
        using var stream = File.OpenRead(filePath);
        var actual = MD5.HashData(stream);
        return CryptographicOperations.FixedTimeEquals(actual, expectedMd5);
    }

    private HttpClient CreateHttpClient()
    {
        var client = _handler is null
            ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aukenid/1.0");
        return client;
    }

    private static string? FileNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

public sealed class RagHashMismatchException : Exception
{
    public RagHashMismatchException()
        : base("RAG file hash does not match the knowledge catalog.")
    {
    }
}

