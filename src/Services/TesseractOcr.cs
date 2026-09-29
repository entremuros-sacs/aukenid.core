namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.IO;
using Aukenid.Core.Attachments;
using Aukenid.Core.Engine;
using PDFtoImage;
using Tesseract;

internal static class TesseractOcr
{
    private const string Latin = "eng+spa+fra+deu+por+ita+ron+tur+pol";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, TesseractEngine> Engines = new(StringComparer.Ordinal);

    public static IReadOnlyList<PlacedWord> RecognizeImage(string path)
    {
        lock (Gate)
        {
            return RecognizeFile(path, pageNumber: 1);
        }
    }

    public static IReadOnlyList<PlacedWord> RecognizePdfPage(string path, int pageNumber)
    {
        var png = Path.Combine(Path.GetTempPath(), $"aukenid-ocr-{Guid.NewGuid():N}.png");
        lock (Gate)
        {
            try
            {
                using (var pdf = File.OpenRead(path))
                {
#pragma warning disable CA1416 // PDFtoImage supports the desktop RIDs this project ships.
                    Conversion.SavePng(png, pdf, page: pageNumber - 1, options: new RenderOptions(Dpi: 200));
#pragma warning restore CA1416
                }

                return RecognizeFile(png, pageNumber);
            }
            catch (Exception ex)
            {
                HostLog.Line("ocr", HostLog.Describe(ex));
                return [];
            }
            finally
            {
                if (File.Exists(png))
                {
                    File.Delete(png);
                }
            }
        }
    }

    private static List<PlacedWord> RecognizeFile(string path, int pageNumber)
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!Directory.Exists(dataPath))
        {
            HostLog.Line("ocr", "tessdata is missing");
            return [];
        }

        try
        {
            using var pix = Pix.LoadFromFile(path);
            var language = LanguagesFor(pix, dataPath);
            using var page = Engine(dataPath, language).Process(pix);
            return ReadWords(page, pix.Width, pix.Height, pageNumber);
        }
        catch (Exception ex)
        {
            HostLog.Line("ocr", HostLog.Describe(ex));
            return [];
        }
    }

    // Greek and Cyrillic are their own pass. The Latin interface languages share one.
    private static string LanguagesFor(Pix pix, string dataPath)
    {
        try
        {
            using var page = Engine(dataPath, "osd").Process(pix, PageSegMode.OsdOnly);
            var report = page.GetText() ?? string.Empty;
            if (report.Contains("Greek", StringComparison.OrdinalIgnoreCase))
            {
                return "ell";
            }

            if (report.Contains("Cyrillic", StringComparison.OrdinalIgnoreCase))
            {
                return "rus";
            }
        }
        catch (Exception ex)
        {
            HostLog.Line("ocr", HostLog.Describe(ex));
        }

        return Latin;
    }

    private static TesseractEngine Engine(string dataPath, string language)
    {
        if (Engines.TryGetValue(language, out var existing))
        {
            return existing;
        }

        var engine = new TesseractEngine(dataPath, language, EngineMode.Default);
        Engines[language] = engine;
        return engine;
    }

    private static List<PlacedWord> ReadWords(Page page, int width, int height, int pageNumber)
    {
        var words = new List<PlacedWord>();
        using var iter = page.GetIterator();
        iter.Begin();
        var pageWidth = Math.Max(1, width);
        var pageHeight = Math.Max(1, height);
        do
        {
            var text = iter.GetText(PageIteratorLevel.Word);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out var box))
            {
                continue;
            }

            words.Add(new PlacedWord(
                pageNumber,
                text.Trim(),
                box.X1 / (float)pageWidth,
                box.Y1 / (float)pageHeight,
                Math.Max(0, box.X2 - box.X1) / (float)pageWidth,
                Math.Max(0, box.Y2 - box.Y1) / (float)pageHeight));
        }
        while (iter.Next(PageIteratorLevel.Word));

        return words;
    }
}
