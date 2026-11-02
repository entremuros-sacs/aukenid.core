namespace Aukenid.Core.Engine;

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using LLama;

internal static class MetalBackendBootstrapper
{
    private static readonly object Gate = new();
    private static bool _prepared;
    private static string? _nativeDirectory;

    private static readonly Regex VersionPrefix = new(
        @"^(?<v>\d+(?:\.\d+){1,3})",
        RegexOptions.Compiled);

    // https://learn.microsoft.com/nuget/api/package-base-address-resource
    private const string NuGetFlatContainer = "https://api.nuget.org/v3-flatcontainer/";

    private static readonly string[] CandidatePackages =
    [
        "LLamaSharp.Backend.Metal",
    ];

    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    public static string? TryPrepareNativeDirectory()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.OSArchitecture != Architecture.Arm64)
        {
            return null;
        }

        lock (Gate)
        {
            if (_prepared)
            {
                return _nativeDirectory;
            }

            _prepared = true;
        }

        var version = DetermineLlmaSharpVersion();
        if (version is null)
        {
            return null;
        }

        foreach (var packageId in CandidatePackages)
        {
            var nativeDirectory = EnsureBackendPresent(packageId, version);
            if (nativeDirectory is not null)
            {
                _nativeDirectory = nativeDirectory;
                HostLog.Line("metal", $"Enabled {packageId} {version}");
                return nativeDirectory;
            }
        }

        return null;
    }

    private static string? DetermineLlmaSharpVersion()
    {
        try
        {
            var assembly = typeof(LLamaWeights).Assembly;
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var text = info ?? assembly.GetName().Version?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            text = text.Trim();
            var match = VersionPrefix.Match(text);
            return match.Success ? match.Groups["v"].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? EnsureBackendPresent(string packageId, string version)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aukenid",
            "llama-backends",
            Sanitize(packageId),
            version);

        var nativeDir = Path.Combine(baseDir, "runtimes", "osx-arm64", "native");
        if (Directory.Exists(nativeDir) && Directory.EnumerateFiles(nativeDir).Any())
        {
            return nativeDir;
        }

        try
        {
            Directory.CreateDirectory(baseDir);
        }
        catch
        {
            return null;
        }

        var url = BuildNugetPackageUrl(packageId, version);
        if (url is null)
        {
            return null;
        }

        HostLog.Line("metal", $"Downloading {packageId} {version}…");
        var temp = Path.Combine(Path.GetTempPath(), $"{Sanitize(packageId)}.{version}.nupkg");
        try
        {
            if (!DownloadToFile(url, temp))
            {
                return null;
            }

            if (!ExtractNativeFromNupkg(temp, baseDir))
            {
                return null;
            }

            return Directory.Exists(nativeDir) && Directory.EnumerateFiles(nativeDir).Any()
                ? nativeDir
                : null;
        }
        catch (Exception ex)
        {
            HostLog.Line("metal", HostLog.Describe(ex));
            return null;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool DownloadToFile(string url, string destinationPath)
    {
        try
        {
            using var response = Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var source = response.Content.ReadAsStream();
            using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ExtractNativeFromNupkg(string nupkgPath, string baseDir)
    {
        using var stream = File.OpenRead(nupkgPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        const string prefix = "runtimes/osx-arm64/native/";

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = entry.FullName.Substring(prefix.Length);
            if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal) || relative.Contains('\\'))
            {
                continue;
            }

            var destinationPath = Path.Combine(baseDir, "runtimes", "osx-arm64", "native", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }

        return true;
    }

    private static string? BuildNugetPackageUrl(string packageId, string version)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var idLower = packageId.ToLowerInvariant();
        var versionLower = version.ToLowerInvariant();
        return $"{NuGetFlatContainer}{idLower}/{versionLower}/{idLower}.{versionLower}.nupkg";
    }

    private static string Sanitize(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(text.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}

