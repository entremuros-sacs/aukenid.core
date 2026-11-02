namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using LLama;
using LLama.Native;

internal static class BackendPackageVersion
{
    private static readonly Regex VersionPrefix = new(
        @"^(?<v>\d+(?:\.\d+){1,3})",
        RegexOptions.Compiled);

    private static readonly Regex DepsLlmaSharp = new(
        "\"LLamaSharp/(?<v>\\d+(?:\\.\\d+){1,3})\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DepsLlmaSharpCpu = new(
        "\"LLamaSharp\\.Backend\\.Cpu/(?<v>\\d+(?:\\.\\d+){1,3})\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? Resolve()
    {
        // Published apps always carry a .deps.json next to the host; it's the most reliable
        // way to recover the LLamaSharp version when assembly metadata is trimmed or ends up
        // as 0.0.0.0.
        var depsVersion = ResolveFromDepsJson();
        if (depsVersion is not null)
        {
            return depsVersion;
        }

        // Prefer versions coming from the LLamaSharp assemblies we depend on.
        // Some distributions may set AssemblyVersion to 0.0.0.0, so we also
        // try informational and file versions.
        return ResolveFromAssemblies(
        [
            typeof(LLamaWeights).Assembly,
            typeof(NativeLibraryConfig).Assembly,
        ]);
    }

    private static string? ResolveFromDepsJson()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
            {
                return null;
            }

            foreach (var deps in Directory.EnumerateFiles(baseDir, "*.deps.json"))
            {
                string text;
                try
                {
                    text = File.ReadAllText(deps);
                }
                catch
                {
                    continue;
                }

                var match = DepsLlmaSharp.Match(text);
                if (match.Success)
                {
                    var version = match.Groups["v"].Value;
                    if (!IsPlaceholderVersion(version))
                    {
                        return version;
                    }
                }

                match = DepsLlmaSharpCpu.Match(text);
                if (match.Success)
                {
                    var version = match.Groups["v"].Value;
                    if (!IsPlaceholderVersion(version))
                    {
                        return version;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? ResolveFromAssemblies(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies.Distinct())
        {
            if (TryExtractVersion(assembly, out var version))
            {
                return version;
            }
        }

        return null;
    }

    private static bool TryExtractVersion(Assembly assembly, out string version)
    {
        version = string.Empty;

        string? fileVersion = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(assembly.Location))
            {
                var info = FileVersionInfo.GetVersionInfo(assembly.Location);
                fileVersion = info.FileVersion;
                if (string.IsNullOrWhiteSpace(fileVersion))
                {
                    fileVersion = info.ProductVersion;
                }
            }
        }
        catch
        {
            // ignore
        }

        var candidates = new string?[]
        {
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version,
            assembly.GetName().Version?.ToString(),
            fileVersion,
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var text = candidate.Trim();
            var match = VersionPrefix.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var parsed = match.Groups["v"].Value;
            if (IsPlaceholderVersion(parsed))
            {
                continue;
            }

            version = parsed;
            return true;
        }

        return false;
    }

    private static bool IsPlaceholderVersion(string version) =>
        version == "0.0.0" || version == "0.0.0.0" || version.StartsWith("0.0.0.", StringComparison.Ordinal);
}

