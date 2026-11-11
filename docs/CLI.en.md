# CLI reference

[简体中文](CLI.md) | [English](CLI.en.md)

`cli/stemora.exe` ships with the release and is intended for batch processing, automation, and integration with other programs. The GUI and CLI share the same models, settings, and job folder.

## Common commands

```powershell
$cli = '.\cli\stemora.exe'
& $cli models list
& $cli models download --id bs-roformer-sw-q8_0
& $cli models inspect --model '.\data\models\BS-RoFormer-SW-6stem-q8_0.gguf' --json
& $cli separate --input 'D:\Music\song.mp3' --model '.\data\models\BS-RoFormer-SW-6stem-q8_0.gguf' --device cuda --output 'D:\Stems' --json
```

## Commands

```text
stemora models list
stemora models download --id <model ID>
stemora models inspect --model model.gguf [--json]
stemora runtime install [--flavor cuda|cpu]
stemora doctor [--model model.gguf] [--engine stemora-infra.exe]
stemora separate --input song.mp3 --model model.gguf --output ./stems
  [--device auto|cuda|cpu] [--chunk-size 588800] [--overlap 2]
  [--format float32|pcm24|pcm16] [--no-residual] [--timeout 600]
  [--engine stemora-infra.exe] [--profile bs_roformer_sw]
  [--stems bass,drums,other,vocals,guitar,piano] [--json] [--home ./data]
```

- `models list`: list catalog models and whether they are installed locally.
- `models download`: download a pinned model with resume and SHA-256 verification.
- `models inspect`: read GGUF metadata and show the architecture, sample rate, stem count, and default chunk settings.
- `runtime install`: check the bundled Stemora Infra. Current releases already include the runtime, so no separate download is needed.
- `doctor`: check the model, engine, and runtime environment.
- `separate`: run separation. Unspecified options fall back to the settings saved by the GUI.

The built-in SW model is recognized by its full file hash and automatically bound to the correct six-stem order; you do not need to pass `--stems`. See the [Infra documentation](NATIVE-INFRA.md) (Chinese) for custom model descriptions.

## JSON output

With `--json`, stdout is UTF-8 NDJSON (one JSON object per line), and native logs are wrapped as progress messages:

```json
{"type":"progress","stage":"inference","fraction":0.5,"message":"inference"}
{"type":"completed","data":{"family":"bs_roformer_sw","tracks":[{"id":"bass","label":"Bass","path":"D:\\Stems\\bass.wav"}]}}
{"type":"error","data":{"code":"operation_failed","message":"..."}}
```

`fraction` ranges from 0 to 1 and may be `null` during initialization. Callers should treat the `completed` message together with the process exit code as success, rather than relying on `fraction` alone.

Without `--json`, progress goes to stderr and results go to stdout.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Operation failed |
| `2` | Invalid arguments or incomplete checks |
| `130` | Cancelled by the user or timed out |

Ctrl+C, `--timeout`, and native process-tree termination are supported. Failures and cancellations do not overwrite earlier results.

## Data locations

Resolved in this order:

1. `--home`
2. `STEMORA_HOME`
3. `data` next to `portable.flag`
4. `%LOCALAPPDATA%\Stemora`

Model and engine paths inside a portable folder are stored as relative paths, so the directory can be moved. Use `--engine` or `STEMORA_ENGINE` for a custom backend.

## Calling from another program

External C# programs should pass arguments with `ProcessStartInfo.ArgumentList` and consume stdout / stderr concurrently to avoid escaping issues and pipe deadlocks. You can also reference `Stemora.Core` and call `SeparationService.SeparateAsync` directly.

In `result.json`, `tracks` contains each track's `id`, display `label`, and absolute `path`. Multi-track callers should read `tracks` rather than the legacy `primaryPath` / `stemPaths` fields.
