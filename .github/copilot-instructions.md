# Copilot instructions (aukenid.core)

This repository is `aukenid.core/` (shared engine/core). It lives inside a *workspace container* folder `aukenid/` that also contains other git repos.

## Repo boundaries

- Do **not** run `git` commands from `aukenid/`.
- Run git commands from inside the correct repo folder:
  - `aukenid.core/` (this repo)
  - `aukenid.*/` (other repos)

## Build

- Solution: `Aukenid.Core.slnx`
- Use `dotnet build Aukenid.Core.slnx -c Release`.
- On macOS, `dotnet` may be at `/usr/local/share/dotnet/dotnet`.

## Product rules

- CUDA is not used. GPU acceleration is via Vulkan (Windows) and Metal (macOS).
- Vulkan is bundled by the desktop packaging; core should only detect/use it when present.
