namespace Aukenid.Core.Attachments;

using System;
using System.Collections.Generic;
using System.IO;
using Aukenid.Core.Services;

internal sealed class ImageAttachmentReader : IAttachmentReader
{
    public const int MaxFileBytes = 40_000_000;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".tif", ".tiff", ".bmp", ".webp", ".heic", ".heif",
    };

    public bool CanRead(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && Extensions.Contains(extension);
    }

    public AttachmentReadResult Read(string path, int maxChars)
    {
        if (!File.Exists(path))
        {
            return AttachmentReadResult.Fail("attach.unreadable");
        }

        if (new FileInfo(path).Length > MaxFileBytes)
        {
            return AttachmentReadResult.Fail("attach.too-large");
        }

        var words = TesseractOcr.RecognizeImage(path);
        if (words.Count == 0)
        {
            return AttachmentReadResult.Fail("attach.unreadable");
        }

        var text = LayoutText.WithNote(LayoutText.Format(words));
        return string.IsNullOrWhiteSpace(text)
            ? AttachmentReadResult.Fail("attach.unreadable")
            : AttachmentReadResult.FromText(LayoutText.Limit(text, maxChars));
    }
}
