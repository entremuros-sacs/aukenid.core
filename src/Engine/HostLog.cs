namespace Aukenid.Core.Engine;

using System.Text.RegularExpressions;

/// <summary>Short host diagnostics on stderr. Never dump prompts, attachment paths, or file contents.</summary>
internal static class HostLog
{
    private static readonly Regex SecretAssignment = new(
        @"\b(api[_-]?key|secret|token|password|authorization)\b\s*[=:]\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Bearer = new(@"\bBearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProviderKey = new(@"\bsk-[A-Za-z0-9]{8,}\b", RegexOptions.Compiled);
    private static readonly Regex Quoted = new("\"(?:\\\\.|[^\"\\\\]){40,}\"|'(?:\\\\.|[^'\\\\]){40,}'", RegexOptions.Compiled);
    private static readonly Regex AbsolutePath = new(
        @"(?<![A-Za-z0-9])(?:[A-Za-z]:\\|\\\\|/)[^\r\n""']+",
        RegexOptions.Compiled);

    public static void Line(string area, string detail)
    {
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
