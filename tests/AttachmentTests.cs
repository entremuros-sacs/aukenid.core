using Aukenid.Core.Attachments;
using Aukenid.Core.Data;
using Aukenid.Core.Contracts;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests;

public sealed class AttachmentTests
{
    [Theory]
    [InlineData("notes.txt")]
    [InlineData("README.md")]
    [InlineData("data.csv")]
    [InlineData("config.json")]
    [InlineData("doc.xml")]
    [InlineData("Program.cs")]
    [InlineData("app.js")]
    [InlineData("main.py")]
    [InlineData("lib.rs")]
    [InlineData("Dockerfile")]
    [InlineData("Makefile")]
    public void PlainTextReader_ClaimsCommonTextAndSourceFiles(string fileName)
    {
        Assert.True(new PlainTextAttachmentReader().CanRead(fileName));
    }

    [Theory]
    [InlineData("photo.png")]
    [InlineData("scan.jpg")]
    [InlineData("archive.zip")]
    [InlineData("sheet.xlsx")]
    [InlineData("report.docx")]
    public void PlainTextReader_RejectsBinaryOfficeAndImageFiles(string fileName)
    {
        Assert.False(new PlainTextAttachmentReader().CanRead(fileName));
    }

    [Fact]
    public void PlainTextReader_ExtractsUtf8Contents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aukenid-txt-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "# Title\n\nHello from markdown.");

        var result = new PlainTextAttachmentReader().Read(path, 4000);

        Assert.True(result.Ok);
        Assert.Contains("Hello from markdown.", result.Text);

        File.Delete(path);
    }

    [Fact]
    public void PlainTextReader_TruncatesToMaxChars()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aukenid-txt-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, new string('a', 80));

        var result = new PlainTextAttachmentReader().Read(path, 20);

        Assert.True(result.Ok);
        Assert.Equal(21, result.Text!.Length);
        Assert.EndsWith("\u2026", result.Text);

        File.Delete(path);
    }

    [Fact]
    public void PdfReader_ExtractsTextLayer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aukenid-pdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, MinimalPdf("Hello PDF"));

        var result = new PdfAttachmentReader().Read(path, 4000);

        Assert.True(result.Ok);
        Assert.Contains("Hello PDF", result.Text);

        File.Delete(path);
    }

    [Fact]
    public void Registry_RoutesPdfAndTextAndRejectsUnknown()
    {
        var registry = new AttachmentReaderRegistry();

        Assert.IsType<PdfAttachmentReader>(registry.ForFileName("paper.pdf"));
        Assert.IsType<PlainTextAttachmentReader>(registry.ForFileName("main.ts"));
        Assert.IsType<ImageAttachmentReader>(registry.ForFileName("photo.png"));
        Assert.True(registry.IsSupported("scan.jpg"));
        Assert.Null(registry.ForFileName("archive.zip"));
        Assert.False(registry.IsSupported("sheet.xlsx"));
    }

    [Fact]
    public void AttachmentContext_InjectsExtractsAndNotesUnsupported()
    {
        var turn = AttachmentContext.TryTurn(
        [
            new AttachmentExcerpt("notes.md", "buy milk", null),
            new AttachmentExcerpt("photo.png", null, "attach.unsupported"),
        ]);

        Assert.NotNull(turn);
        Assert.Equal("system", turn.Value.Role);
        Assert.Contains("notes.md", turn.Value.Content);
        Assert.Contains("buy milk", turn.Value.Content);
        Assert.Contains("photo.png", turn.Value.Content);
        Assert.Contains("format not supported", turn.Value.Content);
    }

    [Fact]
    public void AttachmentContext_CapsTotalCharacters()
    {
        var turn = AttachmentContext.TryTurn(
        [
            new AttachmentExcerpt("big.txt", new string('x', 400), null),
        ], maxTotalChars: 200);

        Assert.NotNull(turn);
        Assert.True(turn.Value.Content.Length <= 201);
        Assert.Contains('\u2026', turn.Value.Content);
        Assert.DoesNotContain(new string('x', 400), turn.Value.Content);
    }

    [Fact]
    public void ConversationMarkdown_RoundTripsAttachmentNames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-md-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "conversation.md");
        var thread = new ThreadDto("t1", "Test", ConversationStore.GeneralFolder, DateTimeOffset.UnixEpoch);
        var message = new MessageDto(
            "m1",
            "t1",
            null,
            "user",
            "Please summarize",
            DateTimeOffset.UnixEpoch,
            ["report one.pdf", "notes.md"]);

        File.WriteAllText(file, ConversationMarkdown.BuildHeader(thread) + ConversationMarkdown.FormatMessageBlock(message));
        var read = ConversationMarkdown.ReadMessages(file, "t1");

        Assert.Single(read);
        Assert.Equal(["report one.pdf", "notes.md"], read[0].Attachments);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_CopiesAttachmentIntoThreadFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-att-{Guid.NewGuid():N}");
        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);
        var source = Path.Combine(Path.GetTempPath(), $"aukenid-src-{Guid.NewGuid():N}.txt");
        File.WriteAllText(source, "hello");

        var stored = store.SaveAttachment(thread.Id, source);

        Assert.Equal(Path.GetFileName(source), stored);
        var dest = store.TryGetAttachmentPath(thread.Id, stored!);
        Assert.NotNull(dest);
        Assert.StartsWith(store.GetAttachmentsDirectory(thread.Id)!, dest);
        Assert.Equal("hello", File.ReadAllText(dest));

        File.Delete(source);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_SavesAttachmentsForTemporalThreads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-att-{Guid.NewGuid():N}");
        var store = new ConversationStore(root);
        var thread = store.CreateThread("Scratch", ConversationStore.TemporalFolder);
        var source = Path.Combine(Path.GetTempPath(), $"aukenid-src-{Guid.NewGuid():N}.md");
        File.WriteAllText(source, "scratch");

        var stored = store.SaveAttachment(thread.Id, source);
        var dest = store.TryGetAttachmentPath(thread.Id, stored!);

        Assert.NotNull(stored);
        Assert.NotNull(dest);
        Assert.Equal("scratch", File.ReadAllText(dest!));

        store.DeleteThread(thread.Id);
        Assert.Null(store.TryGetAttachmentPath(thread.Id, stored!));

        File.Delete(source);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_MovingTemporalThreadKeepsAttachments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-att-{Guid.NewGuid():N}");
        var store = new ConversationStore(root);
        var thread = store.CreateThread("Scratch", ConversationStore.TemporalFolder);
        var source = Path.Combine(Path.GetTempPath(), $"aukenid-src-{Guid.NewGuid():N}.txt");
        File.WriteAllText(source, "keep me");
        var stored = store.SaveAttachment(thread.Id, source);

        store.MoveThread(thread.Id, ConversationStore.GeneralFolder);

        var dest = store.TryGetAttachmentPath(thread.Id, stored!);
        Assert.NotNull(dest);
        Assert.Equal("keep me", File.ReadAllText(dest!));
        Assert.Contains(Path.Combine(root, ConversationStore.GeneralFolder), dest);

        File.Delete(source);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ConversationStore_RejectsPathTraversalInStoredName()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aukenid-att-{Guid.NewGuid():N}");
        var store = new ConversationStore(root);
        var thread = store.CreateThread("Test", ConversationStore.GeneralFolder);

        Assert.Null(store.TryGetAttachmentPath(thread.Id, "../secret.txt"));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void LayoutText_ReadsColumnsBeforeTheNeighbor()
    {
        var text = LayoutText.Format(
        [
            new PlacedWord(1, "The left column explains the first idea in a full sentence.", 0.05f, 0.10f, 0.38f, 0.04f),
            new PlacedWord(1, "The right column starts beside it with its own full sentence.", 0.62f, 0.10f, 0.33f, 0.04f),
            new PlacedWord(1, "The left column continues with another full sentence here.", 0.05f, 0.16f, 0.38f, 0.04f),
            new PlacedWord(1, "The right column continues underneath that opening sentence.", 0.62f, 0.16f, 0.33f, 0.04f),
        ]);

        var leftFollows = text.IndexOf("continues with another", StringComparison.Ordinal);
        var rightStarts = text.IndexOf("right column starts", StringComparison.Ordinal);
        Assert.True(leftFollows >= 0 && rightStarts > leftFollows);
    }

    [Fact]
    public void LayoutText_KeepsATableOnTheSameRow()
    {
        var text = LayoutText.Format(
        [
            new PlacedWord(1, "Name", 0.05f, 0.10f, 0.12f, 0.03f),
            new PlacedWord(1, "Age", 0.50f, 0.10f, 0.08f, 0.03f),
            new PlacedWord(1, "Ada", 0.05f, 0.16f, 0.10f, 0.03f),
            new PlacedWord(1, "36", 0.50f, 0.16f, 0.06f, 0.03f),
        ]);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.Contains("Name", StringComparison.Ordinal) && line.Contains("Age", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Ada", StringComparison.Ordinal) && line.Contains("36", StringComparison.Ordinal));
    }

    [Fact]
    public void LayoutText_SeparatesRegionsAndPages()
    {
        var text = LayoutText.Format(
        [
            new PlacedWord(1, "First region of the note.", 0.08f, 0.10f, 0.40f, 0.03f),
            new PlacedWord(1, "Second region after a gap.", 0.08f, 0.30f, 0.40f, 0.03f),
            new PlacedWord(2, "Next page.", 0.08f, 0.10f, 0.30f, 0.03f),
        ]);

        Assert.Contains("First region of the note.\n\nSecond region", text, StringComparison.Ordinal);
        Assert.Contains("--- page 2 ---", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tesseract_ReadsTextDrawnInAPng()
    {
        var trained = Path.Combine(AppContext.BaseDirectory, "tessdata", "eng.traineddata");
        if (!File.Exists(trained))
        {
            return;
        }

        var png = Path.Combine(Path.GetTempPath(), $"aukenid-ocr-{Guid.NewGuid():N}.png");
        var swift = Path.Combine(Path.GetTempPath(), $"aukenid-ocr-{Guid.NewGuid():N}.swift");
        File.WriteAllText(swift, """
            import CoreGraphics
            import CoreText
            import Foundation
            import ImageIO

            let width = 900
            let height = 220
            let colorSpace = CGColorSpaceCreateDeviceRGB()
            guard let context = CGContext(
                data: nil,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: 0,
                space: colorSpace,
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            ) else {
                fputs("context failed\n", stderr)
                exit(1)
            }

            context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: width, height: height))
            let text = "Hola OCR" as CFString
            let attributed = CFAttributedStringCreateMutable(kCFAllocatorDefault, 0)
            CFAttributedStringReplaceString(attributed, CFRange(location: 0, length: 0), text)
            let font = CTFontCreateWithName("Helvetica-Bold" as CFString, 72, nil)
            CFAttributedStringSetAttribute(attributed, CFRange(location: 0, length: 8), kCTFontAttributeName, font)
            let line = CTLineCreateWithAttributedString(attributed!)
            context.textPosition = CGPoint(x: 40, y: 70)
            CTLineDraw(line, context)
            guard let image = context.makeImage() else {
                fputs("image failed\n", stderr)
                exit(1)
            }

            let url = URL(fileURLWithPath: CommandLine.arguments[1]) as CFURL
            guard let dest = CGImageDestinationCreateWithURL(url, "public.png" as CFString, 1, nil) else {
                fputs("dest failed\n", stderr)
                exit(1)
            }
            CGImageDestinationAddImage(dest, image, nil)
            if !CGImageDestinationFinalize(dest) {
                fputs("write failed\n", stderr)
                exit(1)
            }
            """);

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "swift",
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            process.StartInfo.ArgumentList.Add(swift);
            process.StartInfo.ArgumentList.Add(png);
            process.Start();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0 && File.Exists(png), error);

            var result = new ImageAttachmentReader().Read(png, 4000);

            Assert.True(result.Ok, result.ErrorKey);
            Assert.Contains("Hola", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("OCR", result.Text, StringComparison.OrdinalIgnoreCase);

            var pdf = Path.Combine(Path.GetTempPath(), $"aukenid-ocr-{Guid.NewGuid():N}.pdf");
            var wrap = Path.Combine(Path.GetTempPath(), $"aukenid-ocr-{Guid.NewGuid():N}.swift");
            File.WriteAllText(wrap, """
                import AppKit
                import PDFKit

                let image = NSImage(contentsOfFile: CommandLine.arguments[1])!
                let page = PDFPage(image: image)!
                let document = PDFDocument()
                document.insert(page, at: 0)
                if !document.write(to: URL(fileURLWithPath: CommandLine.arguments[2])) {
                    fputs("pdf failed\n", stderr)
                    exit(1)
                }
                """);
            try
            {
                using var wrapped = new System.Diagnostics.Process();
                wrapped.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "swift",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                wrapped.StartInfo.ArgumentList.Add(wrap);
                wrapped.StartInfo.ArgumentList.Add(png);
                wrapped.StartInfo.ArgumentList.Add(pdf);
                wrapped.Start();
                var wrapError = wrapped.StandardError.ReadToEnd();
                wrapped.WaitForExit();
                Assert.True(wrapped.ExitCode == 0 && File.Exists(pdf), wrapError);

                var scanned = new PdfAttachmentReader().Read(pdf, 4000);
                Assert.True(scanned.Ok, scanned.ErrorKey);
                Assert.Contains("Hola", scanned.Text, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (File.Exists(pdf)) File.Delete(pdf);
                if (File.Exists(wrap)) File.Delete(wrap);
            }
        }
        finally
        {
            TesseractOcr.ReleaseEngines();
            if (File.Exists(png)) File.Delete(png);
            if (File.Exists(swift)) File.Delete(swift);
        }
    }

    private static byte[] MinimalPdf(string text)
    {
        // A tiny valid PDF with a Helvetica text showing. Offsets are computed after the body is built.
        var objects = new[]
        {
            "1 0 obj<< /Type /Catalog /Pages 2 0 R >>endobj\n",
            "2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj\n",
            "3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources<< /Font<< /F1 5 0 R >> >> >>endobj\n",
            "",
            "5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj\n",
        };

        var stream = $"BT /F1 12 Tf 20 100 Td ({text}) Tj ET";
        objects[3] = $"4 0 obj<< /Length {stream.Length} >>stream\n{stream}\nendstream\nendobj\n";

        var header = "%PDF-1.4\n";
        var body = string.Concat(objects);
        var offsets = new int[6];
        var cursor = header.Length;
        var parts = objects;
        for (var i = 0; i < parts.Length; i++)
        {
            offsets[i + 1] = cursor;
            cursor += parts[i].Length;
        }

        var xrefPos = header.Length + body.Length;
        var xref = "xref\n0 6\n0000000000 65535 f \n"
            + string.Concat(Enumerable.Range(1, 5).Select(i => $"{offsets[i]:D10} 00000 n \n"));
        var trailer = $"trailer<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n";
        return System.Text.Encoding.ASCII.GetBytes(header + body + xref + trailer);
    }
}
