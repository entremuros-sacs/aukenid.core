# Aukenid.CLI — User Manual

`Aukenid.CLI` is a small interactive console app used to run **Aukenid.Core** the same way Aukenid AI Desktop does, but in a terminal.

Its purpose is functional evaluation by developers (accuracy, performance, privacy, reproducibility), not experimentation with unusual configurations.

## Requirements

- PowerShell is **not** required.
- .NET 10 runtime/SDK installed.
- A local `.gguf` model file.

## Start the application

Run the CLI and point it to your local model:

```bash
dotnet run --project cli/Aukenid.CLI/Aukenid.CLI.csproj -- --gguf /path/to/model.gguf
```

After loading the model, the app opens an interactive prompt.

## Normal chat

Type a message and press Enter.

- Grounding (web / wiki / scholar) runs automatically when needed.
- When grounding runs, the CLI prints a `Sources` section **at the end** of the assistant response.
- Press `Ctrl+C` to cancel the current generation (the session stays open).

## Interactive commands

Interactive commands begin with `/`.

### `/help`

Prints the short help (also printed automatically on startup).

### `/attach <path>`

Attach a local file as source material for subsequent turns.

- Attachments are **sticky** within the current thread.
- Attachments are cleared when you start a new thread.
- The CLI never prints the attachment contents to the terminal.

Supported file types depend on the core library build. Common examples include text/markdown files and PDFs.

Examples:

```text
/attach ./spec.md
/attach ./paper.pdf
```

### `/save-doc <path.md>`

Save the current in-memory document (the "document panel" artifact) to a Markdown file.

Notes:

- The document is maintained in-memory during the session.
- The document is not printed to the terminal.

Example:

```text
/save-doc ./notes.md
```

### `/save-thread <path.md>`

Export the current thread (chat transcript) to a Markdown file.

Example:

```text
/save-thread ./transcript.md
```

### `/new-thread` (alias: `/new`)

Start a new thread.

- The CLI prompts for confirmation: `Are you sure?`
- If a document exists and has not been saved since the last change, the CLI prompts to save it.
- The new thread starts with an empty chat transcript and no attachments.

Examples:

```text
/new
/new-thread
```

## Exit

Type any of the following to end the application:

- `exit`
- `quit`
- `end`

## Privacy notes

Grounding may send network requests depending on the prompt:

- web search + fetch
- Wikipedia queries
- Semantic Scholar queries

If you are evaluating privacy or running in an offline environment, use prompts that do not trigger grounding.

## Performance notes

For performance measurement, prefer:

- multiple runs with the same model
- the same prompts across runs
- disabling background system load

Use standard .NET tooling (`dotnet-trace`, `dotnet-counters`, etc.) when profiling.

