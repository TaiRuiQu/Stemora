# Stemora

C# / .NET 10 原生 WinUI 3 音频分离软件，配有 C++ **Stemora Infra** 后端和独立 CLI。界面采用蓝色 Fluent 风格，支持多模型家族、具名乐器音轨、同步混音试听。

## 直接运行

- `artifacts/Stemora/Stemora.App.exe`：图形界面。
- `artifacts/Stemora/cli/stemora.exe`：供脚本或其他程序调用。
- `artifacts/Stemora/native/stemora-infra.exe` / `stemora_infra.dll`：本项目的 C++ 后端和 C ABI。
- `artifacts/Stemora-windows-x64.zip`：完整便携包。

发布包包含 .NET / Windows App SDK 运行时。便携数据在发布目录的 `data/` 中；模型可以在模型库下载或从本地导入。

## 模型与输出

当前适配器支持 **BS Roformer、Mel-Band Roformer、BS Roformer SW**。模型库包括原 GGUF 仓库的 21 个版本，以及 SW 六轨 Q8_0。不同网络架构通过 C++ `ModelAdapter` 扩展，当前不承诺支持任意 GGUF 模型。

SW 按原模型配置顺序输出：

| 文件 | 音轨 |
| --- | --- |
| `bass.wav` | 贝斯 |
| `drums.wav` | 鼓 |
| `other.wav` | 其他 |
| `vocals.wav` | 人声 |
| `guitar.wav` | 吉他 |
| `piano.wav` | 钢琴 |

多轨模型直接导出每个模型输出，**不会将第一轨当成人声，也不会另生成人声残差伴奏**。单轨模型可选导出 `residual.wav`（原始混音减去首个模型输出）。缺少音轨语义的自定义模型使用 `stem_1` 等中性名称，不猜测乐器顺序。

每个任务创建独立结果文件夹，内含 WAV 和 `result.json`。JSON 的 `tracks` 保存每轨的 `id`、显示名 `label`、绝对路径 `path`，同时保存模型家族、实际后端 `backend`、输入音频时长和耗时。`primaryPath` / `stemPaths` 为旧调用方保留；多轨调用方应读取 `tracks`。

输入 WAV、MP3、FLAC、M4A、AAC、AIFF、WMA、MP4；压缩格式依赖 Windows 系统解码器。单声道自动转为双声道并重采样为 44.1 kHz。输出支持 32 位浮点、24 / 16 位 PCM WAV；浮点保留原电平，整数格式会将超范围采样限制到 ±1。当前不接受环绕声或超过 RIFF WAV 4 GB 限制的音频。

模型仓库与原始配置：

- https://huggingface.co/chenmozhijin/BSRoformer-GGUF
- https://huggingface.co/FractalSurfer/BS-RoFormer-SW-6stem

模型下载固定提交、大小与 SHA-256，支持断点续传；实际音频全程保留在本地。

## 同步音轨工作台

分离完成后，点击音轨区右上角的 **导出音轨**，勾选一条或多条音轨（包含可用的残差伴奏），选择格式和保存位置：

- **WAV**：32 位浮点、24 位 PCM 或 16 位 PCM。
- **MP3**：320 kbps、44.1 kHz 双声道，使用 Windows Media Foundation 编码器；Windows N 版本需安装媒体功能包。

每次导出创建独立文件夹，按音轨 ID 保存文件；试听的静音、独听和音量不影响导出。支持取消，失败或取消会清理本次临时文件，保留已有分离结果。

所有分离音轨按行展开，使用同一个时间轴、播放线和进度条；原始混音默认静音，用于对照。播放器使用一个混音图和一个音频设备时钟，保持音轨采样对齐。

- **M**：静音。**S**：独听；可同时选择多条独听。静音优先于独听。
- 每轨音量独立调节。被静音或排除在独听之外的轨道仍然同步消耗采样，切换时不会错位。
- 点击或拖动任一波形定位全部音轨；底部进度条统一定位；播放、暂停和回到起点共用。
- 左右声道保留有符号的最小 / 最大包络，深色区域表示 RMS；显示时间刻度、采样峰值和播放线。
- 支持水平缩放、平移、完整音频视图。滚轮在波形上缩放。
- “波形归一化”仅调整显示比例，不改变试听音量或导出结果。电平条只在播放时出现。

音轨控制区与波形行加大；播放控制固定在音轨滚动区域之外。浅色、深色、紧凑布局均使用原生 WinUI 控件。设计依据见 [界面设计](docs/UI-DESIGN.md)。

## CUDA / CPU

最终后端仅提供 **CUDA / CPU**：

- `auto`：优先 CUDA，按后端可用性选择 CPU。
- `cuda`：显式使用 CUDA，不可用时返回错误。
- `cpu`：只使用 CPU。

无需 Python ML 环境。正常推理由本项目 `stemora-infra.exe` 运行，不调用外部 BSRoformer 命令行程序。GGML 张量库、Roformer 单块计算图的复用与许可证有明确说明，见 [C++ Infra](docs/NATIVE-INFRA.md)。

## CLI

```powershell
$cli = '.\artifacts\Stemora\cli\stemora.exe'
& $cli models list
& $cli models download --id bs-roformer-sw-q8_0
& $cli models inspect --model '.\artifacts\Stemora\data\models\BS-RoFormer-SW-6stem-q8_0.gguf' --json
& $cli separate --input 'D:\音乐\歌曲.mp3' `
  --model '.\artifacts\Stemora\data\models\BS-RoFormer-SW-6stem-q8_0.gguf' `
  --device cuda --output 'D:\分离结果' --json
```

```text
stemora models list
stemora models download --id bs-roformer-sw-q8_0
stemora models inspect --model model.gguf
stemora runtime install [--flavor cuda|cpu]   检查随包提供的 Infra
stemora doctor [--model model.gguf] [--engine stemora-infra.exe]
stemora separate --input song.mp3 --model model.gguf --output ./stems
  [--device auto|cuda|cpu] [--chunk-size 588800] [--overlap 2]
  [--format float32|pcm24|pcm16] [--no-residual] [--timeout 600]
  [--engine stemora-infra.exe] [--profile bs_roformer_sw]
  [--stems bass,drums,other,vocals,guitar,piano] [--json] [--home ./data]
```

SW 内置模型通过完整文件哈希识别并绑定正确的六轨顺序；无需手工填写 `--stems`。自定义模型的名称配置方式见 Infra 文档。未指定的分离参数读取 GUI 保存的设置。

`--json` 输出 UTF-8 **NDJSON**（一行一个 JSON 对象），原生日志封装为 progress 消息。结果示例：

```json
{"type":"progress","stage":"inference","fraction":0.5,"message":"推理中"}
{"type":"completed","data":{"family":"bs_roformer_sw","tracks":[{"id":"bass","label":"贝斯","path":"D:\\结果\\bass.wav"}]}}
{"type":"error","data":{"code":"operation_failed","message":"..."}}
```

退出码：`0` 成功、`1` 操作失败、`2` 参数错误或检查不完整、`130` 用户取消 / 超时。`fraction` 为 0–1；初始化期间可以为 `null`。调用方以 `completed` 和进程退出码判断成功。非 JSON 模式进度写入 stderr，结果写入 stdout。支持 Ctrl+C、超时和终止原生进程树，失败不会覆盖以前的结果。

外部 C# 程序应使用 `ProcessStartInfo.ArgumentList` 传参，并并行消费 stdout / stderr，避免参数转义与管道阻塞。也可直接引用 `Stemora.Core` 调用 `SeparationService.SeparateAsync`。

## 构建

Windows x64、.NET 10 SDK、Windows SDK 10.0.26100、CMake / Ninja。原生 CUDA 工具链细节见 Infra 文档和 `scripts/Build-Native.ps1`。

```powershell
.\scripts\Build-Native.ps1
.\scripts\Build.ps1
.\scripts\Publish.ps1 -IncludeRuntime -IncludeModel -CacheHome .\.local\home
.\scripts\Package.ps1
```

`-SkipNativeBuild` 复用已构建的本地后端。发布目录会重新生成原生运行时文件，避免混入以前的后端 DLL。

数据位置：CLI `--home` → `STEMORA_HOME` → `portable.flag` 所在目录的 `data` → `%LOCALAPPDATA%\Stemora`。便携目录中的模型、引擎路径使用相对设置保存，支持迁移目录。自定义后端使用 `--engine` 或 `STEMORA_ENGINE`。

## 验证

```powershell
dotnet run --project tests/Stemora.Tests -c Release
ctest --test-dir .local/native-prebuilt-cuda-build --output-on-failure
dotnet run --project tests/Stemora.Tests -c Release -- --verify-real '完整路径\result.json'
```

C# 检查覆盖真实子进程协议、中文路径、重采样、完整音轨与残差、多轨输出、失败 / 取消清理、下载续传与哈希、便携设置、模型元数据、波形包络 / 缩放、同步混音与 Mute / Solo / Volume。原生检查覆盖 C ABI、输入校验、音轨名称、取消和不同长度 / 重叠的样本级拼接。

已对实际 SW 六轨模型、BS Roformer 单轨模型和 Mel-Band Roformer 多轨模型运行端到端检查。音乐样本来自官方 TorchAudio 示例素材；测试只使用片段，不随发布包分发。

实际界面可通过 WinUI `RenderTargetBitmap` 导出：

```powershell
.\artifacts\Stemora\Stemora.App.exe --capture-ui 'G:\Stemora\artifacts\screenshots' `
  --capture-input '完整路径\音乐片段.wav' `
  --capture-model '完整路径\BS-RoFormer-SW-6stem-q8_0.gguf' --capture-run
```

`--capture-result result.json` 可展示已有真实结果，避免重复推理。诊断模式只捕获应用自身的界面，完成后退出。发布包不包含测试输入和截图输出。

## 源码

GitHub 自动检查和版本发布流程见 [发布说明](docs/RELEASING.md)。

```text
native/               C++ C ABI、家族适配、计算图、分块调度和原生宿主
src/Stemora.Core/     音频解码、模型、下载、混音、波形、推理调度
src/Stemora.App/      原生 WinUI 3 界面与时间轴
src/Stemora.Cli/      参数解析、NDJSON 与退出码
tests/Stemora.Tests/  可执行验证项目
scripts/             构建、发布和诊断
```

第三方软件及模型许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。模型许可证与推理引擎的 MIT 许可证相互独立。
