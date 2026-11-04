# Aukenid.Core

Source code of the core component of Aukenid AI:  local GGUF chat, conversation storage, attachments, and web/wiki/scholar tools.

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
dotnet run --project cli/Aukenid.CLI/Aukenid.CLI.csproj -- --gguf /path/to/model.gguf
```

The app loads the model, prints a short help, then keeps the conversation until you type `exit`, `quit`, or `end`.

## Optional GPU backends

On Windows, `Aukenid.Core` can auto-enable a CUDA backend at runtime when an NVIDIA driver is present. It downloads the matching `LLamaSharp.Backend.Cuda*.Windows` NuGet package, extracts `runtimes/win-x64/native` into the user's local app-data cache, and prepends that folder to LLamaSharp's native search path.

This is meant to keep the base installer small while still enabling GPU acceleration for non-developers.
