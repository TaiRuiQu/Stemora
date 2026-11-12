# Development and build

[简体中文](DEVELOPMENT.md) | [English](DEVELOPMENT.en.md)

For developers who want to build, test, or modify Stemora from source. If you just want to use the app, go back to the [README](../README.en.md).

## Requirements

- Windows x64
- .NET 10 SDK
- Windows SDK 10.0.26100
- CMake / Ninja
- Visual Studio C++ toolchain
- CUDA toolchain details are in [NATIVE-INFRA](NATIVE-INFRA.md) and `scripts/Build-Native.ps1`

## Build

```powershell
# Native backend, app, and tests in one command
.\scripts\Build.ps1

# Or build in two steps
.\scripts\Build-Native.ps1
.\scripts\Build.ps1 -SkipNativeBuild
```

`Build.ps1` calls `Build-Native.ps1` by default, then builds the .NET / WinUI app and runs the core checks. Use `-CpuOnly` for CPU-only test binaries; use `-SkipNativeBuild` to reuse an existing backend.

App and CLI builds copy the engine and its DLLs from `artifacts/native` into a `native` subfolder in their output directories so development builds can run directly. A first-time `dotnet build` does not compile the C++ backend; run `Build-Native.ps1` first or use the full `Build.ps1`.

## Publish

```powershell
.\scripts\Publish.ps1 -IncludeRuntime -IncludeModel -CacheHome .\.local\home
.\scripts\Package.ps1
```

`Publish.ps1` produces a self-contained `artifacts/Stemora` with the .NET / Windows App SDK runtime and native Infra. `-SkipNativeBuild` reuses the local backend; publishing regenerates the native runtime files so stale DLLs are not included. `-IncludeModel` is for local testing only; official releases do not bundle models.

The top-level `Stemora.App.exe` is a statically linked native launcher. The WinUI app and its dependencies live in `app`, with English and Simplified Chinese resources only. `native`, `cli` and portable `data` remain at the top level. The launcher preserves arguments, working directory and exit status. Use `-NativeDirectory artifacts/native-cpu -SkipNativeBuild` for CPU packaging checks; official releases use the default CUDA / CPU backend.

`Package.ps1` creates `artifacts/Stemora-windows-x64.zip`, excluding settings, jobs, runtime caches, lock files, and PDBs.

## Tests

```powershell
dotnet run --project tests/Stemora.Tests -c Release
ctest --test-dir .local/native-prebuilt-cuda-build --output-on-failure
dotnet run --project tests/Stemora.Tests -c Release -- --verify-real 'full\path\result.json'
```

The C# checks cover the real subprocess protocol, non-ASCII paths, resampling, full tracks and residuals, multi-track output, failure / cancellation cleanup, download resume and hashing, portable settings, model metadata, waveform envelopes / zoom, synchronized mixing, and mute / solo / volume. Native checks cover the C ABI, input validation, track names, cancellation, and sample-accurate concatenation at different lengths and overlaps.

End-to-end checks have run with the actual SW six-stem model, a BS Roformer single-stem model, and a Mel-Band Roformer multi-track model. Music samples come from the official TorchAudio examples; tests use excerpts only and do not ship with releases.

## UI screenshots

The real UI can be exported through WinUI `RenderTargetBitmap`:

```powershell
.\artifacts\Stemora\Stemora.App.exe --capture-ui 'G:\Stemora\artifacts\screenshots' `
  --capture-input 'full\path\music-excerpt.wav' `
  --capture-model 'full\path\BS-RoFormer-SW-6stem-q8_0.gguf' --capture-run
```

`--capture-result result.json` displays an existing real result and avoids repeating inference. Diagnostic mode captures only the app's own visual tree and exits when done. Releases do not include test inputs or screenshot output.

## Project layout

```text
native/               C++ C ABI, model adapters, compute graphs, chunk scheduling, native host
src/Stemora.Core/     Audio decoding, models, downloads, mixing, waveforms, inference scheduling
src/Stemora.App/      Native WinUI 3 UI and timeline
src/Stemora.Cli/      Argument parsing, NDJSON, and exit codes
tests/Stemora.Tests/  Executable verification project
scripts/              Build, publish, and diagnostics
```

## Data locations

CLI `--home` → `STEMORA_HOME` → `data` next to `portable.flag` → `%LOCALAPPDATA%\Stemora`.

## Related documents

- [C++ Infra](NATIVE-INFRA.md) (Chinese)
- [UI design](UI-DESIGN.md) (Chinese)
- [CI and releases](RELEASING.md) (Chinese)
- [Third-party notices](../THIRD-PARTY-NOTICES.md)
