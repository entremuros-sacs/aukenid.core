namespace Aukenid.Core.Attachments;

using System.Globalization;
using System.Text;
using Aukenid.Core.Engine;

/// <summary>
/// A digest sits beside the attachment as <c>{stored-name}.digest</c>.
/// It is reused while the file's length and write time are unchanged.
/// </summary>
internal static class DocumentDigest
{
    public const string Suffix = ".digest";
    public const int MaxSidecarBytes = 2_000_000;
    private const string Magic = "aukenid-digest 1";

    public static string SidecarPath(string attachmentPath) => attachmentPath + Suffix;

    public static IReadOnlyList<string>? TryRead(string attachmentPath)
    {
        try
        {
            if (!File.Exists(attachmentPath))
            {
                return null;
            }

            var sidecar = SidecarPath(attachmentPath);
            if (!File.Exists(sidecar))
            {
                return null;
            }

            var sidecarInfo = new FileInfo(sidecar);
            if (sidecarInfo.Length is 0 or > MaxSidecarBytes)
            {
                return null;
            }

            var source = new FileInfo(attachmentPath);
            return Parse(File.ReadAllText(sidecar), source.Length, source.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HostLog.Line("digest", HostLog.Describe(ex));
            return null;
        }
    }

    public static void Write(string attachmentPath, IReadOnlyList<string> parts)
    {
        var source = new FileInfo(attachmentPath);
        var body = Format(source.Length, source.LastWriteTimeUtc.Ticks, parts);
        var sidecar = SidecarPath(attachmentPath);
        var temp = sidecar + ".tmp";
        File.WriteAllText(temp, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, sidecar, overwrite: true);
    }

    public static string Format(long bytes, long writeTimeTicks, IReadOnlyList<string> parts)
    {
        var body = new StringBuilder();
        body.Append(Magic);
        body.Append('\n');
        body.Append("bytes ");
        body.Append(bytes.ToString(CultureInfo.InvariantCulture));
        body.Append('\n');
        body.Append("write-time ");
        body.Append(writeTimeTicks.ToString(CultureInfo.InvariantCulture));
        body.Append('\n');
        body.Append("parts ");
        body.Append(parts.Count.ToString(CultureInfo.InvariantCulture));
        body.Append('\n');
        foreach (var part in parts)
        {
            var note = part ?? "";
            body.Append(note.Length.ToString(CultureInfo.InvariantCulture));
            body.Append('\n');
            body.Append(note);
            body.Append('\n');
        }

        return body.ToString();
    }

    public static IReadOnlyList<string>? Parse(string text, long bytes, long writeTimeTicks)
    {
        var index = 0;
        if (!TakeLine(text, ref index, out var magic) || magic != Magic)
        {
            return null;
        }

        if (!TakeLine(text, ref index, out var bytesLine)
            || bytesLine != "bytes " + bytes.ToString(CultureInfo.InvariantCulture))
        {
            return null;
        }

        if (!TakeLine(text, ref index, out var timeLine)
            || timeLine != "write-time " + writeTimeTicks.ToString(CultureInfo.InvariantCulture))
        {
            return null;
        }

        if (!TakeLine(text, ref index, out var partsLine)
            || !partsLine.StartsWith("parts ", StringComparison.Ordinal)
            || !int.TryParse(partsLine.AsSpan("parts ".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count < 0)
        {
            return null;
        }

        var parts = new List<string>(count);
        for (var n = 0; n < count; n++)
        {
            if (!TakeLine(text, ref index, out var lengthLine)
                || !int.TryParse(lengthLine, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length < 0
                || index + length > text.Length)
            {
                return null;
            }

            parts.Add(text.Substring(index, length));
            index += length;
            if (index >= text.Length || text[index] != '\n')
            {
                return null;
            }

            index++;
        }

        return index == text.Length ? parts : null;
    }

    private static bool TakeLine(string text, ref int index, out string line)
    {
        if (index >= text.Length)
        {
            line = "";
            return false;
        }

        var end = text.IndexOf('\n', index);
        if (end < 0)
        {
            line = "";
            return false;
        }

        line = text[index..end];
        index = end + 1;
        return true;
    }
}
