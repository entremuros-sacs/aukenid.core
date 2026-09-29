namespace Aukenid.Core.Engine;

using LLama.Native;

/// <summary>
/// llama.cpp prints a long load dump (tensors, Metal, KV) to the console. Keep a short
/// warning or error once. Drop prompt text, continue-fragments, paths, and lines that
/// would otherwise print as only "Warning".
/// </summary>
internal static class LlamaCppLogging
{
    private static readonly HashSet<string> Printed = new(StringComparer.Ordinal);

    public static void Quiet()
    {
        NativeLogConfig.LLamaLogCallback callback = Write;
        NativeLibraryConfig.All.WithLogCallback(callback);
        NativeLogConfig.llama_log_set(callback);
    }

    /// <summary>
    /// Text safe to print, or null when the line must stay off the console.
    /// An error whose body cannot be shown becomes one shared "llama.cpp error" line.
    /// </summary>
    internal static string? Detail(bool error, string message)
    {
        if (!LooksLikePromptDump(message) && message.Length <= 120)
        {
            var safe = HostLog.Sanitize(message);
            if (safe.Length > 0
                && !safe.Contains("[path]", StringComparison.Ordinal)
                && !safe.Contains("[redacted]", StringComparison.Ordinal))
            {
                return safe;
            }
        }

        return error ? "llama.cpp error" : null;
    }

    private static void Write(LLamaLogLevel level, string message)
    {
        // Continue fragments are how llama.cpp streams a prompt. Never write them.
        if (level is not (LLamaLogLevel.Warning or LLamaLogLevel.Error))
        {
            return;
        }

        var detail = Detail(level == LLamaLogLevel.Error, message);
        if (detail is null)
        {
            return;
        }

        lock (Printed)
        {
            if (!Printed.Add(detail))
            {
                return;
            }
        }

        HostLog.Line("model", detail);
    }

    private static bool LooksLikePromptDump(string message) =>
        message.Contains("<|", StringComparison.Ordinal)
        || message.Contains("print_info:", StringComparison.Ordinal)
        || message.Contains("n_ctx_seq", StringComparison.Ordinal)
        || message.Contains("ggml_metal", StringComparison.Ordinal)
        || message.Contains("llama_context:", StringComparison.Ordinal)
        || message.IndexOf("n_keep", StringComparison.OrdinalIgnoreCase) >= 0
        || message.Trim().Trim('.').Length == 0;
}
