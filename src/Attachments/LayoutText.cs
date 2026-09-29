namespace Aukenid.Core.Attachments;

using System.Globalization;
using System.Text;

internal readonly record struct PlacedWord(int Page, string Text, float X, float Y, float W, float H);

/// <summary>
/// Rebuilds reading order from word boxes. A vertical gutter that is not a table
/// is read column by column. Tables stay in rows, with a wide gap between cells.
/// </summary>
internal static class LayoutText
{
    public const string Note =
        "Layout follows the page from top to bottom. A wide gap is another column or cell. A blank line is a new region.";

    public static string WithNote(string body) =>
        string.IsNullOrWhiteSpace(body) ? body : Note + "\n" + body;

    public static string Limit(string text, int maxChars)
    {
        var cap = Math.Max(1, maxChars);
        if (text.Length <= cap)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, cap), "\u2026");
    }

    public static string Format(IReadOnlyList<PlacedWord> words)
    {
        var usable = words
            .Where(word => !string.IsNullOrWhiteSpace(word.Text) && float.IsFinite(word.X) && float.IsFinite(word.Y))
            .Select(word => word with { Text = word.Text.Trim() })
            .ToList();
        if (usable.Count == 0)
        {
            return string.Empty;
        }

        var pages = usable.GroupBy(word => word.Page).OrderBy(group => group.Key).ToList();
        var labelPages = pages.Count > 1;
        var sb = new StringBuilder();
        foreach (var page in pages)
        {
            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            if (labelPages)
            {
                sb.Append("--- page ");
                sb.Append(page.Key.ToString(CultureInfo.InvariantCulture));
                sb.Append(" ---\n");
            }

            sb.Append(FormatRegion(page.ToList()));
        }

        return sb.ToString().Trim();
    }

    private static string FormatRegion(List<PlacedWord> words)
    {
        if (words.Count == 0)
        {
            return string.Empty;
        }

        if (words.Count == 1)
        {
            return words[0].Text;
        }

        if (TrySplit(words, vertical: true, out var left, out var right, out var gap)
            && gap >= 0.04f
            && !LooksLikeTable(left, right))
        {
            return JoinBlocks(FormatRegion(left), FormatRegion(right));
        }

        return FormatLines(words);
    }

    private static string JoinBlocks(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return left;
        }

        return left.Trim() + "\n\n" + right.Trim();
    }

    private static bool LooksLikeTable(List<PlacedWord> left, List<PlacedWord> right)
    {
        var leftLines = GroupLines(left);
        var rightLines = GroupLines(right);
        if (leftLines.Count < 2 || rightLines.Count < 2)
        {
            return false;
        }

        var difference = Math.Abs(leftLines.Count - rightLines.Count);
        if (difference > Math.Max(leftLines.Count, rightLines.Count) / 2)
        {
            return false;
        }

        if (leftLines.Concat(rightLines).Any(line => line.Sum(word => word.Text.Length) > 40))
        {
            return false;
        }

        var aligned = 0;
        foreach (var line in leftLines)
        {
            var y = line.Average(word => word.Y);
            var height = Math.Max(0.01f, line.Average(word => word.H));
            if (rightLines.Any(other => Math.Abs(other.Average(word => word.Y) - y) <= height))
            {
                aligned++;
            }
        }

        return aligned >= leftLines.Count * 0.6;
    }

    private static bool TrySplit(
        List<PlacedWord> words,
        bool vertical,
        out List<PlacedWord> first,
        out List<PlacedWord> second,
        out float gap)
    {
        first = [];
        second = [];
        gap = 0;
        float bestGap = 0;
        float bestEdge = 0;
        var found = false;

        for (var i = 0; i < words.Count; i++)
        {
            var edge = vertical ? words[i].X + words[i].W : words[i].Y + words[i].H;
            var nextStart = float.MaxValue;
            var crosses = false;
            var before = 0;
            var after = 0;
            for (var j = 0; j < words.Count; j++)
            {
                var start = vertical ? words[j].X : words[j].Y;
                var end = vertical ? words[j].X + words[j].W : words[j].Y + words[j].H;
                if (end <= edge + 0.001f)
                {
                    before++;
                }
                else if (start >= edge - 0.001f)
                {
                    after++;
                    if (start < nextStart)
                    {
                        nextStart = start;
                    }
                }
                else
                {
                    crosses = true;
                    break;
                }
            }

            if (crosses || before == 0 || after == 0 || nextStart == float.MaxValue)
            {
                continue;
            }

            var candidate = nextStart - edge;
            if (candidate > bestGap)
            {
                bestGap = candidate;
                bestEdge = edge;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        gap = bestGap;
        foreach (var word in words)
        {
            var end = vertical ? word.X + word.W : word.Y + word.H;
            if (end <= bestEdge + 0.001f)
            {
                first.Add(word);
            }
            else
            {
                second.Add(word);
            }
        }

        return first.Count > 0 && second.Count > 0;
    }

    private static string FormatLines(List<PlacedWord> words)
    {
        var lines = GroupLines(words);
        var sb = new StringBuilder();
        var previousBottom = -1f;
        var previousHeight = 0f;
        foreach (var line in lines)
        {
            var top = line.Min(word => word.Y);
            var height = Math.Max(0.001f, line.Max(word => word.H));
            if (sb.Length > 0)
            {
                var space = top - previousBottom;
                sb.Append(space > previousHeight * 0.85f ? "\n\n" : "\n");
            }

            sb.Append(JoinLine(line));
            previousBottom = line.Max(word => word.Y + word.H);
            previousHeight = height;
        }

        return sb.ToString();
    }

    private static List<List<PlacedWord>> GroupLines(List<PlacedWord> words)
    {
        var lines = new List<List<PlacedWord>>();
        foreach (var word in words.OrderBy(word => word.Y).ThenBy(word => word.X))
        {
            var line = lines.LastOrDefault();
            if (line is null || !SameLine(line, word))
            {
                lines.Add([word]);
            }
            else
            {
                line.Add(word);
            }
        }

        return lines;
    }

    private static bool SameLine(List<PlacedWord> line, PlacedWord word)
    {
        var top = line.Min(item => item.Y);
        var bottom = line.Max(item => item.Y + item.H);
        var middle = word.Y + (word.H / 2f);
        return middle >= top && middle <= bottom;
    }

    private static string JoinLine(List<PlacedWord> line)
    {
        line.Sort((a, b) => a.X.CompareTo(b.X));
        var characters = Math.Max(1, line.Sum(word => word.Text.Length));
        var charWidth = Math.Max(0.005f, line.Sum(word => Math.Max(0.001f, word.W)) / characters);
        var sb = new StringBuilder();
        PlacedWord? previous = null;
        foreach (var word in line)
        {
            if (previous is { } prior)
            {
                var gap = word.X - (prior.X + prior.W);
                var spaces = gap <= charWidth * 0.6f
                    ? 1
                    : (int)Math.Clamp(Math.Round(gap / charWidth), 1, 16);
                sb.Append(' ', spaces);
            }

            sb.Append(word.Text);
            previous = word;
        }

        return sb.ToString();
    }
}
