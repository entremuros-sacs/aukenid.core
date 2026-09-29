namespace Aukenid.Core.Attachments;

using System.Text;

internal sealed class PlainTextAttachmentReader : IAttachmentReader
{
    public const int MaxFileBytes = 2_000_000;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonc", ".xml",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".env", ".properties",
        ".cs", ".csx", ".fs", ".fsx", ".vb", ".sln", ".slnx", ".csproj",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
        ".py", ".pyi", ".rb", ".go", ".rs", ".java", ".kt", ".kts", ".swift",
        ".c", ".h", ".cpp", ".cxx", ".cc", ".hpp", ".hh", ".hxx",
        ".m", ".mm",
        ".css", ".scss", ".less", ".sass", ".html", ".htm", ".svg",
        ".sh", ".bash", ".zsh", ".fish", ".ps1", ".bat", ".cmd",
        ".sql", ".r", ".php", ".lua", ".scala", ".dart", ".vue", ".svelte",
        ".gradle", ".cmake", ".proto", ".graphql", ".gql",
        ".diff", ".patch", ".log", ".rst", ".tex", ".bib",
        ".gitignore", ".gitattributes", ".editorconfig", ".plist",
    };

    private static readonly HashSet<string> FileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile", "GNUmakefile", "CMakeLists.txt",
        ".gitignore", ".gitattributes", ".editorconfig", ".env",
    };

    public bool CanRead(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = Path.GetFileName(fileName);
        if (FileNames.Contains(name))
        {
            return true;
        }

        var ext = Path.GetExtension(name);
        return ext.Length > 0 && Extensions.Contains(ext);
    }

    public AttachmentReadResult Read(string path, int maxChars)
    {
        if (!File.Exists(path))
        {
            return AttachmentReadResult.Fail("attach.unreadable");
        }

        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
        {
            return AttachmentReadResult.Fail("attach.too-large");
        }

        using var stream = File.OpenRead(path);
        var probeLength = (int)Math.Min(8192, stream.Length);
        var probe = new byte[probeLength];
        var read = stream.Read(probe, 0, probeLength);
        if (ContainsNul(probe, read))
        {
            return AttachmentReadResult.Fail("attach.unreadable");
        }

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var cap = Math.Max(1, maxChars);
        var buffer = new char[cap + 1];
        var n = reader.Read(buffer, 0, buffer.Length);
        if (n <= 0)
        {
            return AttachmentReadResult.Fail("attach.unreadable");
        }

        var text = new string(buffer, 0, Math.Min(n, cap));
        if (n > cap)
        {
            text += "\u2026";
        }

        return string.IsNullOrWhiteSpace(text)
            ? AttachmentReadResult.Fail("attach.unreadable")
            : AttachmentReadResult.FromText(text);
    }

    private static bool ContainsNul(byte[] buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] == 0)
            {
                return true;
            }
        }

        return false;
    }
}
