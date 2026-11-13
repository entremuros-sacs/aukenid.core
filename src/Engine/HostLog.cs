namespace Aukenid.Core.Engine;

using System;

/// <summary>Short host diagnostics on stderr.</summary>
public static class HostLog
{
    // Keep host diagnostic output disabled by default (privacy/perf).
    private static volatile bool _enabled;

    /// <summary>
    /// Enables host diagnostic output (stderr). Intended to be toggled by the desktop host
    /// when launched with the <c>--diag</c> flag.
    /// </summary>
    public static void SetEnabled(bool enabled) => _enabled = enabled;

    public static bool Enabled => _enabled;

    public static void Line(string area, string detail)
    {
        if (!_enabled)
        {
            return;
        }

        Console.Error.WriteLine($"aukenid {area}: {Sanitize(detail)}");
    }

    public static string Describe(Exception ex)
    {
        var innermost = ex;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        var message = Sanitize(innermost.Message);
        if (message.Length == 0 || message.Length > 120)
        {
            return ex.GetType().Name;
        }

        return $"{ex.GetType().Name}: {message}";
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 220 ? oneLine : oneLine[..220] + "\u2026";
    }

    public static string Truncate(string text) => Sanitize(text);
}

