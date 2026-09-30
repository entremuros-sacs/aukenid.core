namespace Aukenid.Core.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Loader;
using Aukenid.Core.Attachments;
using Aukenid.Core.Engine;
using PDFtoImage;
using Tesseract;

internal static class TesseractOcr
{
    private const string Latin = "eng+spa+fra+deu+por+ita+ron+tur+pol";
    private const int ImageDpi = 300;
    private const int PdfDpi = 200;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, TesseractEngine> Engines = new(StringComparer.Ordinal);

    static TesseractOcr()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseEngines();
        var loadContext = AssemblyLoadContext.GetLoadContext(typeof(TesseractOcr).Assembly);
        if (loadContext is not null)
        {
            loadContext.Unloading += _ => ReleaseEngines();
        }
    }

    public static IReadOnlyList<PlacedWord> RecognizeImage(string path)
    {
        lock (Gate)
        {
            return RecognizeFile(path, pageNumber: 1, ImageDpi);
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
                    Conversion.SavePng(png, pdf, page: pageNumber - 1, options: new RenderOptions(Dpi: PdfDpi));
#pragma warning restore CA1416
                }

                return RecognizeFile(png, pageNumber, PdfDpi);
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

    internal static void ReleaseEngines()
    {
        lock (Gate)
        {
            foreach (var engine in Engines.Values)
            {
                engine.Dispose();
            }

            Engines.Clear();
        }
    }

    private static List<PlacedWord> RecognizeFile(string path, int pageNumber, int dpi)
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
            if (pix.XRes == 0)
            {
                pix.XRes = dpi;
            }

            if (pix.YRes == 0)
            {
                pix.YRes = dpi;
            }

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
    // Sparse scans have too few glyphs for OSD; Latin is the default then.
    private static string LanguagesFor(Pix pix, string dataPath)
    {
        try
        {
            using var page = Engine(dataPath, "osd").Process(pix, PageSegMode.OsdOnly);
            page.DetectBestOrientationAndScript(out _, out _, out var script, out _);
            if (string.Equals(script, "Greek", StringComparison.OrdinalIgnoreCase))
            {
                return "ell";
            }

            if (string.Equals(script, "Cyrillic", StringComparison.OrdinalIgnoreCase))
            {
                return "rus";
            }
        }
        catch (TesseractException)
        {
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
        if (language == "osd")
        {
            engine.SetVariable("debug_file", OperatingSystem.IsWindows() ? "nul" : "/dev/null");
        }

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
