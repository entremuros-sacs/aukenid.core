namespace Aukenid.Core.Data;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>Reversibly encodes titles into filesystem-safe, human-readable file/folder names.</summary>
public static class FileNameCodec
{
    private static readonly HashSet<char> InvalidChars = new("<>:\"/\\|?*".ToCharArray());

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            if (ch == '%' || InvalidChars.Contains(ch) || char.IsControl(ch))
            {
                foreach (var b in Encoding.UTF8.GetBytes([ch]))
                {
                    sb.Append('%').Append(b.ToString("X2"));
                }
            }
            else
            {
                sb.Append(ch);
            }
        }

        var encoded = sb.ToString().Trim().TrimEnd('.', ' ');
        if (encoded.Length == 0)
        {
            encoded = "Untitled";
        }

        if (ReservedDeviceNames.Contains(encoded))
        {
            encoded = "_" + encoded;
        }

        return encoded;
    }

    public static string Decode(string segment) => Uri.UnescapeDataString(segment);
}
