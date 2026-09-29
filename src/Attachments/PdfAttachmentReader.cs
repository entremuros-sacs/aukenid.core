namespace Aukenid.Core.Attachments;

using System;
using System.Collections.Generic;
using System.IO;
using Aukenid.Core.Engine;
using Aukenid.Core.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Util;

internal sealed class PdfAttachmentReader : IAttachmentReader
{
    public const int MaxFileBytes = 20_000_000;
    public const int MaxOcrPages = 12;

    public bool CanRead(string fileName) =>
        Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

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

        try
        {
            using var document = PdfDocument.Open(path);
            var placed = new List<PlacedWord>();
            var ocrPages = 0;
            // A short prompt read stops after a few scanned pages. A full read (the document
            // pass) asks for far more characters and recognizes every page.
            var ocrLimit = maxChars > AttachmentContext.MaxCharsPerFile ? int.MaxValue : MaxOcrPages;
            foreach (var page in document.GetPages())
            {
                var fromLayer = FromTextLayer(page);
                if (fromLayer.Count > 0)
                {
                    placed.AddRange(fromLayer);
                    continue;
                }

                if (ocrPages >= ocrLimit)
                {
                    continue;
                }

                ocrPages++;
                placed.AddRange(TesseractOcr.RecognizePdfPage(path, page.Number));
            }

            if (placed.Count == 0)
            {
                return AttachmentReadResult.Fail("attach.unreadable");
            }

            var text = LayoutText.WithNote(LayoutText.Format(placed));
            return string.IsNullOrWhiteSpace(text)
                ? AttachmentReadResult.Fail("attach.unreadable")
                : AttachmentReadResult.FromText(LayoutText.Limit(text, maxChars));
        }
        catch (Exception ex)
        {
            HostLog.Line("ocr", HostLog.Describe(ex));
            return AttachmentReadResult.Fail("attach.unreadable");
        }
    }

    // Digital pages already know each word's box. That is a better position than
    // rasterizing the same page and recognizing it again.
    private static List<PlacedWord> FromTextLayer(Page page)
    {
        var placed = new List<PlacedWord>();
        try
        {
            var width = (float)page.Width;
            var height = (float)page.Height;
            if (width <= 0 || height <= 0)
            {
                return placed;
            }

            foreach (var word in DefaultWordExtractor.Instance.GetWords(page.Letters))
            {
                var text = word.Text?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var box = word.BoundingBox;
                placed.Add(new PlacedWord(
                    page.Number,
                    text,
                    Unit((float)box.Left, width),
                    Unit((float)(height - box.Top), height),
                    Unit((float)box.Width, width),
                    Unit((float)box.Height, height)));
            }
        }
        catch (Exception ex)
        {
            HostLog.Line("ocr", HostLog.Describe(ex));
        }

        if (placed.Count > 0)
        {
            return placed;
        }

        var fallback = page.Text;
        if (string.IsNullOrWhiteSpace(fallback))
        {
            return placed;
        }

        var collapsed = string.Join(' ', fallback.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(collapsed)
            ? placed
            : [new PlacedWord(page.Number, collapsed, 0.05f, 0.05f, 0.9f, 0.05f)];
    }

    private static float Unit(float value, float size)
    {
        if (size <= 0)
        {
            return 0;
        }

        return Math.Clamp(value / size, 0f, 1f);
    }
}
