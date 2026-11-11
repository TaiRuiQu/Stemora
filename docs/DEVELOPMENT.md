# 开发与构建

[简体中文](DEVELOPMENT.md) | [English](DEVELOPMENT.en.md)

面向从源码构建、测试或修改 Stemora 的开发者。普通用户请回到 [README](../README.md)。

## 环境

- Windows x64
- .NET 10 SDK
- Windows SDK 10.0.26100
- CMake / Ninja
- Visual Studio C++ 工具链
- CUDA 工具链细节见 [NATIVE-INFRA](NATIVE-INFRA.md) 和 `scripts/Build-Native.ps1`

## 构建

```powershell
# 一次完成原生后端、应用构建和测试
.\scripts\Build.ps1

# 或分步执行
.\scripts\Build-Native.ps1
.\scripts\Build.ps1 -SkipNativeBuild
```

`Build.ps1` 默认调用 `Build-Native.ps1`，然后构建 .NET / WinUI 并运行核心检查。`-CpuOnly` 可生成 CPU-only 测试产物；已单独构建后端时，用 `-SkipNativeBuild` 复用。

## 发布

```powershell
.\scripts\Publish.ps1 -IncludeRuntime -IncludeModel -CacheHome .\.local\home
.\scripts\Package.ps1
```

`Publish.ps1` 生成自包含的 `artifacts/Stemora`，包含 .NET / Windows App SDK 运行时和原生 Infra。`-SkipNativeBuild` 复用已构建的本地后端；发布时会重新生成原生运行时文件，避免混入旧 DLL。`-IncludeModel` 仅用于本地测试，正式发布包不附带模型。

顶层的 `Stemora.App.exe` 是静态链接的原生启动器，实际 WinUI 应用及其运行依赖位于 `app`。仅保留英文和简体中文语言资源；`native`、`cli` 和便携数据 `data` 仍位于顶层。启动器保留参数、工作目录和退出码。可用 `-NativeDirectory artifacts/native-cpu -SkipNativeBuild` 验证 CPU 测试包，正式发布使用默认的 CUDA / CPU 后端。

`Package.ps1` 生成 `artifacts/Stemora-windows-x64.zip`，会排除设置、任务、运行时缓存、锁文件和 PDB。

## 测试

```powershell
dotnet run --project tests/Stemora.Tests -c Release
ctest --test-dir .local/native-prebuilt-cuda-build --output-on-failure
dotnet run --project tests/Stemora.Tests -c Release -- --verify-real '完整路径\result.json'
```

C# 检查覆盖真实子进程协议、中文路径、重采样、完整音轨与残差、多轨输出、失败 / 取消清理、下载续传与哈希、便携设置、模型元数据、波形包络 / 缩放、同步混音与 Mute / Solo / Volume。原生检查覆盖 C ABI、输入校验、音轨名称、取消和不同长度 / 重叠的样本级拼接。

已对实际 SW 六轨模型、BS Roformer 单轨模型和 Mel-Band Roformer 多轨模型运行端到端检查。音乐样本来自官方 TorchAudio 示例素材；测试只使用片段，不随发布包分发。

## 界面截图

实际界面可通过 WinUI `RenderTargetBitmap` 导出：

```powershell
.\artifacts\Stemora\Stemora.App.exe --capture-ui 'G:\Stemora\artifacts\screenshots' `
  --capture-input '完整路径\音乐片段.wav' `
  --capture-model '完整路径\BS-RoFormer-SW-6stem-q8_0.gguf' --capture-run
```

`--capture-result result.json` 可展示已有真实结果，避免重复推理。诊断模式只捕获应用自身的界面，完成后退出。发布包不包含测试输入和截图输出。

## 项目结构

```text
native/               C++ C ABI、家族适配、计算图、分块调度和原生宿主
src/Stemora.Core/     音频解码、模型、下载、混音、波形、推理调度
src/Stemora.App/      原生 WinUI 3 界面与时间轴
src/Stemora.Cli/      参数解析、NDJSON 与退出码
tests/Stemora.Tests/  可执行验证项目
scripts/              构建、发布和诊断
```

## 数据位置

CLI `--home` → `STEMORA_HOME` → `portable.flag` 所在目录的 `data` → `%LOCALAPPDATA%\Stemora`。

## 相关文档

- [C++ Infra](NATIVE-INFRA.md)
- [界面设计](UI-DESIGN.md)
- [自动检查与发布](RELEASING.md)
- [第三方说明](../THIRD-PARTY-NOTICES.md)
