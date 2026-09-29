namespace Aukenid.Core.Attachments;

using System.Text;
using Aukenid.Core.Engine;

internal sealed record AttachmentExcerpt(string FileName, string? Text, string? ErrorKey);

internal static class AttachmentContext
{
    public const int MaxTotalChars = 8000;
    public const int MaxCharsPerFile = 4000;

    public static ChatTurn? TryTurn(IReadOnlyList<AttachmentExcerpt> excerpts, int maxTotalChars = MaxTotalChars)
    {
        if (excerpts.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("The user attached files to this conversation. Use extracted text as context. Do not mention this note unless asked.");

        foreach (var excerpt in excerpts)
        {
            if (sb.Length >= maxTotalChars - 24)
            {
                break;
            }

            sb.Append("\n\nFile: ");
            sb.Append(excerpt.FileName);
            sb.Append('\n');

            if (!string.IsNullOrWhiteSpace(excerpt.Text))
            {
                var room = maxTotalChars - sb.Length;
                if (room <= 0)
                {
                    break;
                }

                if (excerpt.Text.Length <= room)
                {
                    sb.Append(excerpt.Text);
                }
                else
                {
                    sb.Append(excerpt.Text.AsSpan(0, room));
                    sb.Append('\u2026');
                    break;
                }
            }
            else if (excerpt.ErrorKey == "attach.unsupported")
            {
                sb.Append("(format not supported; no text extracted)");
            }
            else
            {
                sb.Append("(could not extract text)");
            }
        }

        if (sb.Length > maxTotalChars)
        {
            sb.Length = maxTotalChars;
            sb.Append('\u2026');
        }

        return new ChatTurn("system", sb.ToString());
    }
}
