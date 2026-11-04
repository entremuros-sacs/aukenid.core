namespace Aukenid.CLI;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Engine;
using Aukenid.Core.Services;

internal sealed class Program
{
    public static async Task<int> Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = ParseOptions(args);
        }
        catch (Exception ex)
        {
            PrintUsage(ex.Message);
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.GgufPath))
        {
            PrintUsage("Missing required option: --gguf <path>");
            return 1;
        }

        var app = new CliApp();
        return await app.RunAsync(options);
    }

    private static void PrintUsage(string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
        }

        Console.Error.WriteLine(
            """
            Aukenid.CLI

            Usage:
              dotnet run --project cli/Aukenid.CLI.csproj -- --gguf <path.gguf> [--template <name>]

            Options:
              --gguf <path>          Required path to local .gguf model file
              --template <name>      Optional chat template name (passed to LlamaSharpChatEngine.UseTemplate)
              -h | --help            Show help

            See:
              wiki/Aukenid.CLI.md
            """);
    }

    private static CliOptions ParseOptions(string[] args)
    {
        string? gguf = null;
        string? template = null;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;

                case "--gguf":
                case "-g":
                    gguf = NextValue(args, ref index, "--gguf");
                    break;

                case "--template":
                case "-t":
                    template = NextValue(args, ref index, "--template");
                    break;

                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }
                    break;
            }
        }

        return new CliOptions(gguf, template, showHelp);
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}");
        }

        index++;
        return args[index];
    }
}

internal sealed class CliApp
{
    private readonly HashSet<string> _exitWords = new(StringComparer.OrdinalIgnoreCase) { "exit", "quit", "end" };

    private CancellationTokenSource? _activeTurnCts;

    public CliApp() { }

    public async Task<int> RunAsync(CliOptions options)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ConsoleStyling.Initialize();

        if (!TryValidateGguf(options.GgufPath!, out var ggufPath, out var error))
        {
            PrintUsage(error);
            return 1;
        }

        using var engine = new LlamaSharpChatEngine(ggufPath);
        if (!string.IsNullOrWhiteSpace(options.Template))
        {
            engine.UseTemplate(options.Template);
        }

        using var session = AukenidSession.CreateDefault(engine);

        Console.WriteLine($"Loading {Path.GetFileName(ggufPath)} ...");
        await session.LoadModelAsync(ggufPath, CancellationToken.None);

        var modelName = session.ActiveModelName ?? Path.GetFileNameWithoutExtension(ggufPath);
        Console.WriteLine($"Ready ({modelName}). Type /help for commands. Type exit/quit/end to stop.");
        Console.WriteLine();

        PrintHelp();

        using var shutdownCts = new CancellationTokenSource();
        using var cancel = new CtrlCCancellation(shutdownCts, () => _activeTurnCts);

        while (!shutdownCts.IsCancellationRequested)
        {
            Console.Write("you> ");
            var line = Console.ReadLine();
            if (line is null)
            {
                Console.WriteLine();
                break;
            }

            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (_exitWords.Contains(text))
            {
                break;
            }

            if (text.StartsWith("/", StringComparison.Ordinal))
            {
                HandleCommand(session, text);
                continue;
            }

            await HandleUserMessageAsync(session, text, shutdownCts.Token);
        }

        return 0;
    }

    private bool TryValidateGguf(string ggufPathInput, out string ggufPath, out string error)
    {
        ggufPath = Path.GetFullPath(ggufPathInput);
        if (!File.Exists(ggufPath))
        {
            error = $"GGUF file not found: {ggufPath}";
            return false;
        }

        if (!ggufPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Not a .gguf file: {ggufPath}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task HandleUserMessageAsync(AukenidSession session, string text, CancellationToken shutdownToken)
    {
        _activeTurnCts?.Dispose();
        _activeTurnCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);

        try
        {
            Console.Write("aukenid> ");
            var reply = await session.SendAsync(text, _activeTurnCts.Token);
            PrintPlan(reply.Plan);
            WriteAssistantReply(reply.Text);
            if (!string.IsNullOrWhiteSpace(reply.NoticeKey))
            {
                Console.WriteLine($"[{reply.NoticeKey}]");
            }

            Console.WriteLine();
        }
        catch (OperationCanceledException)
        {
            if (!shutdownToken.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("[canceled]");
                Console.WriteLine();
            }
        }
    }

    private void WriteAssistantReply(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine(text);
            return;
        }

        var marker = "\n\n**Sources**\n";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            ConsoleStyling.WriteAssistantBody(text);
            return;
        }

        var body = text[..index];
        var sources = text[index..];
        ConsoleStyling.WriteAssistantBody(body);
        Console.WriteLine(sources);
    }

    private void HandleCommand(AukenidSession session, string input)
    {
        var parsed = CommandLine.Parse(input);
        switch (parsed.Command)
        {
            case "/help":
                PrintHelp();
                return;

            case "/attach":
                HandleAttach(session, parsed.Rest);
                return;

            case "/save-doc":
                HandleSaveDoc(session, parsed.Rest);
                return;

            case "/save-thread":
                HandleSaveThread(session, parsed.Rest);
                return;

            case "/new":
            case "/new-thread":
                HandleNewThread(session);
                return;

            default:
                Console.WriteLine("Unknown command. Type /help.");
                Console.WriteLine();
                return;
        }
    }

    private void HandleAttach(AukenidSession session, string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            Console.WriteLine("Usage: /attach <path>");
            Console.WriteLine();
            return;
        }

        var result = session.Attach(rest);
        if (result.Ok)
        {
            Console.WriteLine($"Attached: {result.FileName} ({result.ExtractedChars ?? 0} chars)");
        }
        else
        {
            Console.WriteLine($"Attach failed: {result.FileName} [{result.ErrorKey}]");
        }

        Console.WriteLine();
    }

    private void HandleSaveDoc(AukenidSession session, string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            Console.WriteLine("Usage: /save-doc <path.md>");
            Console.WriteLine();
            return;
        }

        var result = session.SaveDocument(rest);
        if (result.Ok)
        {
            Console.WriteLine($"Saved document ({result.WrittenChars} chars).");
        }
        else
        {
            Console.WriteLine($"Save failed [{result.ErrorKey}].");
        }

        Console.WriteLine();
    }

    private void HandleSaveThread(AukenidSession session, string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            Console.WriteLine("Usage: /save-thread <path.md>");
            Console.WriteLine();
            return;
        }

        var result = session.SaveThread(rest);
        if (result.Ok)
        {
            Console.WriteLine($"Saved thread ({result.WrittenChars} chars).");
        }
        else
        {
            Console.WriteLine($"Save failed [{result.ErrorKey}].");
        }

        Console.WriteLine();
    }

    private void HandleNewThread(AukenidSession session)
    {
        if (!Confirm("Start a new thread? This clears chat transcript and attachments. Are you sure? (y/N) "))
        {
            Console.WriteLine();
            return;
        }

        if (session.HasDocument)
        {
            var prompt = session.IsDocumentDirty
                ? "Document has unsaved changes. Save it before starting a new thread? (y/N) "
                : "Document exists. Save a copy before starting a new thread? (y/N) ";

            if (Confirm(prompt))
            {
                Console.Write("Path for document (.md): ");
                var path = (Console.ReadLine() ?? string.Empty).Trim();
                if (path.Length > 0)
                {
                    var save = session.SaveDocument(path);
                    if (!save.Ok)
                    {
                        Console.WriteLine($"Save failed [{save.ErrorKey}]. Aborting /new-thread.");
                        Console.WriteLine();
                        return;
                    }
                }
            }
        }

        session.NewThread();
        Console.WriteLine("New thread started.");
        Console.WriteLine();
    }

    private bool Confirm(string prompt)
    {
        Console.Write(prompt);
        var answer = (Console.ReadLine() ?? string.Empty).Trim();
        return answer.Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private void PrintPlan(ToolRouter.Plan plan)
    {
        if (!plan.Any)
        {
            return;
        }

        var tools = new List<string>(3);
        if (plan.Web) tools.Add("web");
        if (plan.Wiki) tools.Add("wiki");
        if (plan.Scholar) tools.Add("scholar");
        Console.WriteLine($"[{string.Join(", ", tools)}]");
    }

    private void PrintHelp()
    {
        Console.WriteLine(
            """
            Commands:
              /help                 Show this help
              /attach <path>         Attach a local file (sticky)
              /save-doc <path.md>    Save the in-memory document
              /save-thread <path.md> Save the current thread transcript
              /new or /new-thread    Start a new thread (clears attachments + transcript)

            Ctrl+C cancels the current generation.

            Exit: exit | quit | end
            """);

        Console.WriteLine();
    }

    private void PrintUsage(string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
        }

        Console.Error.WriteLine(
            """
            Aukenid.CLI

            Usage:
              dotnet run --project cli/Aukenid.CLI.csproj -- --gguf <path.gguf> [--template <name>]

            Options:
              --gguf <path>          Required path to local .gguf model file
              --template <name>      Optional chat template name (passed to LlamaSharpChatEngine.UseTemplate)
              -h | --help            Show help

            See:
              wiki/Aukenid.CLI.md
            """);
    }
}

internal static class ConsoleStyling
{
    // SeaGreen-ish: readable on both light and dark terminals.
    private const string Green = "\x1b[38;2;46;139;87m";
    private const string Reset = "\x1b[0m";

    private const uint StdOutputHandle = 0xFFFFFFF5;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    private static bool _ansi;

    public static void Initialize()
    {
        if (Console.IsOutputRedirected)
        {
            _ansi = false;
            return;
        }

        _ansi = TryEnableVirtualTerminal();
    }

    public static void WriteAssistantBody(string text)
    {
        if (_ansi)
        {
            Console.WriteLine($"{Green}{text}{Reset}");
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    private static bool TryEnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var handle = NativeMethods.GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return false;
            }

            if (!NativeMethods.GetConsoleMode(handle, out var mode))
            {
                return false;
            }

            mode |= EnableVirtualTerminalProcessing;
            return NativeMethods.SetConsoleMode(handle, mode);
        }
        catch
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(uint nStdHandle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    }
}

internal readonly record struct CliOptions(string? GgufPath, string? Template, bool ShowHelp);

internal readonly record struct CommandLine(string Command, string Rest)
{
    public static CommandLine Parse(string input)
    {
        var trimmed = input.Trim();
        var space = trimmed.IndexOf(' ');
        if (space < 0)
        {
            return new CommandLine(trimmed.ToLowerInvariant(), string.Empty);
        }

        var command = trimmed[..space].ToLowerInvariant();
        var rest = trimmed[(space + 1)..].Trim();
        return new CommandLine(command, rest);
    }
}

internal sealed class CtrlCCancellation : IDisposable
{
    private readonly CancellationTokenSource _shutdown;
    private readonly Func<CancellationTokenSource?> _getActiveTurn;

    public CtrlCCancellation(CancellationTokenSource shutdown, Func<CancellationTokenSource?> getActiveTurn)
    {
        _shutdown = shutdown;
        _getActiveTurn = getActiveTurn;
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        var active = _getActiveTurn();
        if (active is { IsCancellationRequested: false })
        {
            e.Cancel = true;
            active.Cancel();
            return;
        }

        e.Cancel = true;
        _shutdown.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
    }
}

