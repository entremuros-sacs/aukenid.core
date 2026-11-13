namespace Aukenid.Core.Engine;

using System;
using System.Text.RegularExpressions;

/// <summary>Short host diagnostics on stderr. Never dump prompts, attachment paths, or file contents.</summary>
public static class HostLog
{
    // Keep host diagnostic output disabled by default (privacy/perf).
    private static volatile bool _enabled;

    // Redaction is enabled by default. It can be disabled for local debugging
    // sessions (for example when running with --diag).
    private static volatile bool _redactionEnabled = true;

    private static readonly Regex SecretAssignment = new(
        @"\b(api[_-]?key|secret|token|password|authorization)\b\s*[=:]\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Bearer = new(@"\bBearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProviderKey = new(@"\bsk-[A-Za-z0-9]{8,}\b", RegexOptions.Compiled);
    private static readonly Regex Quoted = new("\"(?:\\\\.|[^\"\\\\]){40,}\"|'(?:\\\\.|[^'\\\\]){40,}'", RegexOptions.Compiled);
    private static readonly Regex AbsolutePath = new(
        @"(?<![A-Za-z0-9])(?:[A-Za-z]:\\|\\\\|/)[^\r\n""']+",
        RegexOptions.Compiled);

    /// <summary>
    /// Enables host diagnostic output (stderr). Intended to be toggled by the desktop host
    /// when launched with the <c>--diag</c> flag.
    /// </summary>
    public static void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>
    /// Enables or disables redaction of sensitive values/paths in host logs.
    /// WARNING: Disabling redaction may leak secrets to stderr/log files.
    /// </summary>
    public static void SetRedactionEnabled(bool enabled) => _redactionEnabled = enabled;

    public static bool Enabled => _enabled;

    public static void Line(string area, string detail)
    {
        if (!_enabled)
            return;

        var output = _redactionEnabled ? Sanitize(detail) : detail;
        Console.Error.WriteLine($"aukenid {area}: {output}");
    }

    public static string Describe(Exception ex)
    {
        var innermost = ex;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        if (!_redactionEnabled)
        {
            var raw = innermost.Message ?? string.Empty;
            raw = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return raw.Length == 0 ? ex.GetType().Name : $"{ex.GetType().Name}: {raw}";
        }

        var message = Sanitize(innermost.Message);
        if (message.Length == 0
            || message.Length > 120
            || message.Contains("[path]", StringComparison.Ordinal)
            || message.Contains("[redacted]", StringComparison.Ordinal))
        {
            return ex.GetType().Name;
        }

        return $"{ex.GetType().Name}: {message}";
    }

    public static string Sanitize(string text)
    {
        if (!_redactionEnabled)
        {
            return text ?? string.Empty;
        }

        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        oneLine = Bearer.Replace(oneLine, "Bearer [redacted]");
        oneLine = ProviderKey.Replace(oneLine, "[redacted]");
        oneLine = SecretAssignment.Replace(oneLine, "$1=[redacted]");
        oneLine = Quoted.Replace(oneLine, "[redacted]");
        oneLine = AbsolutePath.Replace(oneLine, "[path]");
        return oneLine.Length <= 220 ? oneLine : oneLine[..220] + "\u2026";
    }

    public static string Truncate(string text) => Sanitize(text);
}
