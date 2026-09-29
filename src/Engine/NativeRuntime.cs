namespace Aukenid.Core.Engine;

using System.Runtime.InteropServices;
using LLama.Native;

/// <summary>
/// Finds llama.cpp natives next to this assembly so hosts that are not a .NET exe
/// (PowerShell, tests, custom ALCs) can load GGUF weights.
/// </summary>
internal static class NativeRuntime
{
    private static readonly object Gate = new();
    private static bool _prepared;

    public static void Prepare()
    {
        lock (Gate)
        {
            if (_prepared)
            {
                return;
            }

            var directories = SearchDirectories().Where(Directory.Exists).Distinct(StringComparer.Ordinal).ToArray();
            if (directories.Length > 0)
            {
                NativeLibraryConfig.All.WithSearchDirectories(directories);
            }

            LlamaCppLogging.Quiet();
            _prepared = true;
        }
    }

    internal static IReadOnlyList<string> SearchDirectories()
    {
        var root = Path.GetDirectoryName(typeof(NativeRuntime).Assembly.Location);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(root))
        {
            directories.Add(root);
            foreach (var rid in RidFallbacks())
            {
                directories.Add(Path.Combine(root, "runtimes", rid, "native"));
            }
        }

        return directories;
    }

    private static IEnumerable<string> RidFallbacks()
    {
        yield return RuntimeInformation.RuntimeIdentifier;
        if (OperatingSystem.IsMacOS())
        {
            yield return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            yield return "osx";
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            yield return "win";
        }
    }
}
