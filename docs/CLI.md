# 命令行参考

[简体中文](CLI.md) | [English](CLI.en.md)

`cli/stemora.exe` 随发布包提供，可用于批处理、自动化或从其他程序调用。GUI 与 CLI 共用模型、设置和任务目录。

## 常用命令

```powershell
$cli = '.\cli\stemora.exe'
& $cli models list
& $cli models download --id bs-roformer-sw-q8_0
& $cli models inspect --model '.\data\models\BS-RoFormer-SW-6stem-q8_0.gguf' --json
& $cli separate --input 'D:\音乐\歌曲.mp3' --model '.\data\models\BS-RoFormer-SW-6stem-q8_0.gguf' --device cuda --output 'D:\分离结果' --json
```

## 命令一览

```text
stemora models list
stemora models download --id <模型 ID>
stemora models inspect --model model.gguf [--json]
stemora runtime install [--flavor cuda|cpu]
stemora doctor [--model model.gguf] [--engine stemora-infra.exe]
stemora separate --input song.mp3 --model model.gguf --output ./stems
  [--device auto|cuda|cpu] [--chunk-size 588800] [--overlap 2]
  [--format float32|pcm24|pcm16] [--no-residual] [--timeout 600]
  [--engine stemora-infra.exe] [--profile bs_roformer_sw]
  [--stems bass,drums,other,vocals,guitar,piano] [--json] [--home ./data]
```

- `models list`：列出模型库，以及本地是否已下载。
- `models download`：下载固定版本的模型，支持断点续传和 SHA-256 校验。
- `models inspect`：读取 GGUF 元数据，显示架构、采样率、音轨数量和默认分块参数。
- `runtime install`：检查随包提供的 Stemora Infra；当前发布包已包含运行时，不需要单独下载。
- `doctor`：检查模型、引擎和运行环境。
- `separate`：执行分离。未指定的参数会读取 GUI 保存的设置。

SW 内置模型通过完整文件哈希识别，并自动绑定正确的六轨顺序，不需要手动填写 `--stems`。自定义模型的描述方式见 [Infra 文档](NATIVE-INFRA.md)。

## JSON 输出

加上 `--json` 后，stdout 输出 UTF-8 NDJSON（一行一个 JSON 对象），原生日志会封装为 progress 消息：

```json
{"type":"progress","stage":"inference","fraction":0.5,"message":"推理中"}
{"type":"completed","data":{"family":"bs_roformer_sw","tracks":[{"id":"bass","label":"贝斯","path":"D:\\结果\\bass.wav"}]}}
{"type":"error","data":{"code":"operation_failed","message":"..."}}
```

`fraction` 为 0–1，初始化期间可能为 `null`。调用方应以 `completed` 消息和进程退出码判断成功，不要只依赖 `fraction`。

非 JSON 模式下，进度写入 stderr，结果写入 stdout。

## 退出码

| 退出码 | 含义 |
| --- | --- |
| `0` | 成功 |
| `1` | 操作失败 |
| `2` | 参数错误或检查不完整 |
| `130` | 用户取消或超时 |

支持 Ctrl+C、`--timeout` 和终止原生进程树。失败或取消不会覆盖以前的结果。

## 数据位置

解析顺序：

1. `--home`
2. `STEMORA_HOME`
3. `portable.flag` 所在目录的 `data`
4. `%LOCALAPPDATA%\Stemora`

便携目录中的模型和引擎路径以相对路径保存，可以整体移动目录。自定义后端可使用 `--engine` 或 `STEMORA_ENGINE`。

## 从其他程序调用

外部 C# 程序应使用 `ProcessStartInfo.ArgumentList` 传参，并并行读取 stdout / stderr，避免参数转义与管道阻塞。也可以直接引用 `Stemora.Core`，调用 `SeparationService.SeparateAsync`。

`result.json` 中的 `tracks` 保存每轨的 `id`、显示名 `label` 和绝对路径 `path`。多轨调用方应读取 `tracks`，不要依赖旧版的 `primaryPath` / `stemPaths`。
