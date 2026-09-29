# Aukenid.Core

C# library for local GGUF chat, conversation storage, attachments, and web/wiki/scholar tools.

## Layout

- `src/` — packable library (`dotnet pack`)
- `cli/` — PowerShell chat host (`Chat.ps1`) that calls `LlamaSharpChatEngine` directly
- `tests/` — unit tests

## Build and test

```bash
dotnet build Aukenid.Core.slnx
dotnet test tests/Aukenid.Core.Tests.csproj
```

## Chat CLI

Requires PowerShell 7 (`pwsh`) and a local `.gguf` file. The script builds `Aukenid.Core` and calls `LlamaSharpChatEngine` from the same process (`PrepareRuntime`, `LoadModelAsync`, `CompleteAsync`).

```bash
./cli/Chat.ps1 -Gguf /path/to/model.gguf
./cli/Chat.ps1 -Gguf /path/to/model.gguf -Prompt "Hello"
```

The script loads the model, prints a reply, then keeps the conversation until you type `exit`, `quit`, or `end`.

## Pack

```bash
dotnet pack src/Aukenid.Core.csproj -c Release
```
