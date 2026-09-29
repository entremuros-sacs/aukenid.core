namespace Aukenid.Core.Attachments;

using System.Globalization;
using System;
using System.Collections.Generic;
using System.Text;
using Aukenid.Core.Engine;
using System.Text.RegularExpressions;

/// <summary>
/// A document that does not fit beside the conversation is read in slices. Each slice
/// becomes a short digest. Those digests are stored and reused for later questions.
/// </summary>
internal static partial class DocumentPass
{
    public const int CharsPerToken = 3;
    public const int ContextTokens = 8192;
    public const int ContextChars = ContextTokens * CharsPerToken;
    public const int AnswerReserveChars = 3000;
    public const int SliceChars = 10000;
    public const int FullReadChars = 1_500_000;
    public const int DigestChars = 1500;

    internal readonly record struct PageBlock(int Number, string Text);

    internal readonly record struct Slice(
        string FileName,
        int FromPage,
        int ToPage,
        string Text,
        bool Paged = true,
        int Part = 1,
        int PartCount = 1);

    public static int RoomForDocument(int otherChars) =>
        Math.Max(AttachmentContext.MaxCharsPerFile, ContextChars - Math.Max(0, otherChars) - AnswerReserveChars);

    public static bool NeedsPass(int otherChars, int documentChars) =>
        documentChars > RoomForDocument(otherChars);

    public static IReadOnlyList<Slice> Pack(IReadOnlyList<AttachmentExcerpt> excerpts)
    {
        var slices = new List<Slice>();
        foreach (var excerpt in excerpts)
        {
            if (string.IsNullOrWhiteSpace(excerpt.Text))
            {
                continue;
            }

            slices.AddRange(PackFile(excerpt.FileName, excerpt.Text));
        }

        return slices;
    }

    public static IReadOnlyList<ChatTurn> DigestTurns(Slice slice)
    {
        var body = new StringBuilder();
        body.Append("You are reading part of a long document. This part is ");
        AppendWhere(body, slice);
        body.Append(". A wide gap is another column or cell. A blank line is a new region.\n");
        body.Append("Write a standalone digest of this part. A later question will be answered from the digest alone, without the original text.\n");
        body.Append("Keep names, numbers, dates, defined terms, and the sentences that carry them. Quote the decisive sentences.\n");
        body.Append("Write the digest in the language of the document. Stay under ");
        body.Append(DigestChars.ToString(CultureInfo.InvariantCulture));
        body.Append(" characters.\n");
        body.Append("Do not answer a question.\n\nDocument:\n");
        body.Append(slice.Text);
        return [new ChatTurn("system", body.ToString()), new ChatTurn("user", "Write the digest of this part.")];
    }

    public static string NoteFor(Slice slice, string note)
    {
        var body = new StringBuilder();
        body.Append("File: ");
        body.Append(slice.FileName);
        if (slice.Paged)
        {
            body.Append(" pages ");
            body.Append(slice.FromPage.ToString(CultureInfo.InvariantCulture));
            body.Append('-');
            body.Append(slice.ToPage.ToString(CultureInfo.InvariantCulture));
        }
        else if (slice.PartCount > 1)
        {
            body.Append(" part ");
            body.Append(slice.Part.ToString(CultureInfo.InvariantCulture));
            body.Append('/');
            body.Append(slice.PartCount.ToString(CultureInfo.InvariantCulture));
        }

        body.Append('\n');
        var clipped = note.Trim();
        if (clipped.Length > DigestChars)
        {
            clipped = clipped[..DigestChars] + "…";
        }

        body.Append(clipped);
        return body.ToString();
    }

    /// <summary>
    /// Joins the notes so every part keeps a share of <paramref name="room"/>.
    /// A prefix clip would drop the later parts of the file.
    /// </summary>
    public static string Fit(IReadOnlyList<string> notes, int room)
    {
        var usable = new List<string>();
        foreach (var note in notes)
        {
            if (!string.IsNullOrWhiteSpace(note))
            {
                usable.Add(note.Trim());
            }
        }

        if (usable.Count == 0 || room <= 0)
        {
            return "";
        }

        const int separatorLength = 2;
        var budget = room - separatorLength * (usable.Count - 1);
        if (budget <= 0)
        {
            return Clip(usable[0], room);
        }

        var caps = new int[usable.Count];
        var total = 0;
        for (var i = 0; i < usable.Count; i++)
        {
            caps[i] = usable[i].Length;
            total += caps[i];
        }

        if (total > budget)
        {
            var share = budget / usable.Count;
            var extra = budget % usable.Count;
            for (var i = 0; i < caps.Length; i++)
            {
                var cap = share + (i < extra ? 1 : 0);
                if (caps[i] > cap)
                {
                    caps[i] = cap;
                }
            }

            var leftover = budget;
            foreach (var cap in caps)
            {
                leftover -= cap;
            }

            while (leftover > 0)
            {
                var progressed = false;
                for (var i = 0; i < caps.Length && leftover > 0; i++)
                {
                    if (caps[i] < usable[i].Length)
                    {
                        caps[i]++;
                        leftover--;
                        progressed = true;
                    }
                }

                if (!progressed)
                {
                    break;
                }
            }
        }

        var fitted = new StringBuilder();
        for (var i = 0; i < usable.Count; i++)
        {
            if (i > 0)
            {
                fitted.Append("\n\n");
            }

            fitted.Append(Clip(usable[i], caps[i]));
        }

        return fitted.Length <= room ? fitted.ToString() : Clip(fitted.ToString(), room);
    }

    public static string ReducePrompt(string notes)
    {
        var body = new StringBuilder();
        body.Append("The notes below are digests of every part of the attached documents. ");
        body.Append("Use them to answer the user's latest question. Cite the page or part named on each note. ");
        body.Append("Do not invent passages that are not in the notes. ");
        body.Append("Answer in the same language as the user.\n\nNotes:\n");
        body.Append(notes);
        return body.ToString();
    }

    private static void AppendWhere(StringBuilder body, Slice slice)
    {
        body.Append(slice.FileName);
        if (slice.Paged)
        {
            body.Append(", pages ");
            body.Append(slice.FromPage.ToString(CultureInfo.InvariantCulture));
            body.Append('–');
            body.Append(slice.ToPage.ToString(CultureInfo.InvariantCulture));
        }
        else if (slice.PartCount > 1)
        {
            body.Append(", part ");
            body.Append(slice.Part.ToString(CultureInfo.InvariantCulture));
            body.Append(" of ");
            body.Append(slice.PartCount.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string Clip(string text, int room)
    {
        if (room <= 0 || text.Length == 0)
        {
            return "";
        }

        if (text.Length <= room)
        {
            return text;
        }

        if (room == 1)
        {
            return "…";
        }

        var line = text.IndexOf('\n');
        if (line > 0 && line + 2 < room)
        {
            var header = text[..(line + 1)];
            var bodyCap = room - header.Length;
            if (bodyCap > 1)
            {
                var body = text[(line + 1)..];
                var kept = Math.Min(body.Length, bodyCap - 1);
                return header + body[..kept] + "…";
            }
        }

        return text[..(room - 1)] + "…";
    }

    private static IReadOnlyList<Slice> PackFile(string fileName, string text)
    {
        var pages = PagesOf(text, out var paged);
        var slices = new List<Slice>();
        var buffer = new StringBuilder();
        var from = 0;
        var to = 0;

        void Flush()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            slices.Add(new Slice(fileName, from, to, buffer.ToString().Trim(), paged));
            buffer.Clear();
        }

        foreach (var page in pages)
        {
            var blocks = WindowsOf(page);
            foreach (var block in blocks)
            {
                var piece = paged
                    ? "--- page " + block.Number.ToString(CultureInfo.InvariantCulture) + " ---\n" + block.Text
                    : block.Text;
                if (buffer.Length > 0 && buffer.Length + piece.Length + 2 > SliceChars)
                {
                    Flush();
                }

                if (buffer.Length > 0)
                {
                    buffer.Append("\n\n");
                }
                else
                {
                    from = block.Number;
                }

                buffer.Append(piece);
                to = block.Number;
            }
        }

        Flush();
        if (!paged)
        {
            for (var i = 0; i < slices.Count; i++)
            {
                slices[i] = slices[i] with { Part = i + 1, PartCount = slices.Count };
            }
        }

        return slices;
    }

    private static IReadOnlyList<PageBlock> WindowsOf(PageBlock page)
    {
        if (page.Text.Length <= SliceChars)
        {
            return [page];
        }

        var windows = new List<PageBlock>();
        for (var offset = 0; offset < page.Text.Length; offset += SliceChars)
        {
            var length = Math.Min(SliceChars, page.Text.Length - offset);
            windows.Add(new PageBlock(page.Number, page.Text.Substring(offset, length)));
        }

        return windows;
    }

    private static IReadOnlyList<PageBlock> PagesOf(string text, out bool paged)
    {
        var matches = PageMarker().Matches(text);
        if (matches.Count == 0)
        {
            paged = false;
            var body = StripNote(text).Trim();
            return body.Length == 0 ? [] : [new PageBlock(1, body)];
        }

        paged = true;

        var pages = new List<PageBlock>();
        for (var i = 0; i < matches.Count; i++)
        {
            var number = int.Parse(matches[i].Groups[1].Value, CultureInfo.InvariantCulture);
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var body = text[start..end].Trim();
            if (body.Length > 0)
            {
                pages.Add(new PageBlock(number, body));
            }
        }

        return pages;
    }

    private static string StripNote(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith(LayoutText.Note, StringComparison.Ordinal))
        {
            return trimmed[LayoutText.Note.Length..];
        }

        return text;
    }

    [GeneratedRegex(@"^--- page (\d+) ---$", RegexOptions.Multiline)]
    private static partial Regex PageMarker();
}
