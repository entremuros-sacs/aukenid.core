namespace Aukenid.Core.Engine;

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

internal static class VulkanBackendBootstrapper
{
    private static readonly object Gate = new();
    private static bool _prepared;
    private static string? _nativeDirectory;

    public static string? TryPrepareNativeDirectory()
    {
        if (!OperatingSystem.IsWindows())
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

        if (!HasVulkanRuntime())
        {
            return null;
        }

        var nativeDirectory = TryFindBundledNativeDirectory();
        if (nativeDirectory is not null)
        {
            _nativeDirectory = nativeDirectory;
            HostLog.Line("vulkan", $"Enabled Vulkan backend from {nativeDirectory}");
            return nativeDirectory;
        }

        HostLog.Line("vulkan", "Vulkan runtime detected, but Vulkan backend natives were not bundled; falling back to CPU.");
        return null;
    }

    private static bool HasVulkanRuntime()
    {
        try
        {
            if (NativeLibrary.TryLoad("vulkan-1.dll", out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch
        {
            // Ignore and treat as no Vulkan.
        }

        return false;
    }

    private static string? TryFindBundledNativeDirectory()
    {
        // When the Windows build includes LLamaSharp.Backend.Vulkan.Windows, its .props file
        // copies Vulkan-capable llama.cpp binaries under LLamaSharpRuntimes/win-x64/native/vulkan.
        // We prepend this folder so those libs win over the CPU-only natives.
        var root = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        var rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var candidate = Path.Combine(root, "LLamaSharpRuntimes", rid, "native", "vulkan");
        if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate).Any())
        {
            // Vulkan backend packages ship ggml-vulkan.dll; use it as a sanity check.
            var marker = Path.Combine(candidate, "ggml-vulkan.dll");
            if (File.Exists(marker))
            {
                return candidate;
            }

            return candidate;
        }

        return null;
    }
}

