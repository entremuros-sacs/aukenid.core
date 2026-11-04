# Aukenid.Core

Source code of the core component of Aukenid AI:  local GGUF chat, conversation storage, attachments, and web/wiki/scholar tools.

License: PolyForm Strict License 1.0.0 (see `LICENSE`).

Not for commercial or any other unauthorized usage.

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

## Optional GPU backends

On Windows, `Aukenid.Core` can auto-enable a CUDA backend at runtime when an NVIDIA driver is present. It downloads the matching `LLamaSharp.Backend.Cuda*.Windows` NuGet package, extracts `runtimes/win-x64/native` into the user's local app-data cache, and prepends that folder to LLamaSharp's native search path.

This is meant to keep the base installer small while still enabling GPU acceleration for non-developers.
