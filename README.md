# Aukenid.Core

Source code of the core component of [Aukenid AI](https://aukenid.com):  local GGUF chat, conversation storage, attachments, and web/wiki/scholar tools.

License: PolyForm Strict License 1.0.0 (see `LICENSE`).

Not for commercial or any other unauthorized usage.

## Layout

- `src/` — packable library (`dotnet pack`)
- `cli/` — C# interactive console app (`Aukenid.CLI`) used to run the core
- `tests/` — unit tests

## Build and test

```bash
dotnet build Aukenid.Core.slnx
dotnet test tests/Aukenid.Core.Tests.csproj
```

## Chat CLI

Requires .NET 10 and a local `.gguf` file.

See the CLI user manual: [wiki/Aukenid.CLI.md](./wiki/Aukenid.CLI.md)

```bash
dotnet run --project cli/Aukenid.CLI.csproj -- --gguf /path/to/model.gguf
```

The app loads the model, prints a short help, then keeps the conversation until you type `exit`, `quit`, or `end`.

## Optional GPU backends

On Windows, `Aukenid.Core` can auto-enable a Vulkan backend at runtime when a Vulkan runtime is present **and** the host has bundled the Vulkan-capable llama.cpp natives (via `LLamaSharp.Backend.Vulkan.Windows`) next to the executable.

Concretely, the Desktop Windows MSI bundles `LLamaSharpRuntimes/win-x64/native/vulkan/*` under the install directory. `Aukenid.Core` detects that folder and prepends it to LLamaSharp's native search path so the Vulkan DLLs win over the CPU-only natives.
