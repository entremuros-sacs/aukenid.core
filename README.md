# Aukenid.Core

Source code of the core component of Aukenid AI:  local GGUF chat, conversation storage, attachments, and web/wiki/scholar tools.

Not for commercial or any other unauthorized usage. Read the License.

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
