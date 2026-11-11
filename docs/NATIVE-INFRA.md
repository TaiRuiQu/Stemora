# Stemora Infra 0.2

`native/` 是本项目的 C++ 推理基础层，交付 `stemora_infra.dll` 和 `stemora-infra.exe`。最终仅支持 CUDA / CPU。正常推理不调用外部 BSRoformer 可执行文件。

## 结构

- `stemora_infra.h`：版本化 C ABI、会话生命周期、模型探测、进度回调、取消、按轨读取输出。
- `ModelRegistry` / `ModelAdapter`：根据 GGUF 架构选择家族。实现 BS Roformer、Mel-Band Roformer；SW 为六输出 BS Roformer 配置。
- `overlap_add.cpp`：本项目编写的反射边界、加权交叠、样本计数校验、有限数值检查与取消调度器。
- `main.cpp`：本项目的 UTF-8 NDJSON 协议和具名输出 manifest；日志进入 stderr。
- `vendor/roformer`：固定提交、MIT 授权的单块计算图、STFT、mask 与 ISTFT 源码，包含本地适配修改。来源保留在该目录说明与许可证中。
- GGML：底层 CPU / CUDA 张量运算库。新基础层复用计算库，并不将这些网络算子描述为本项目原创。

新增网络架构需实现 `ModelAdapter::infer_chunk`，注册工厂，并正确实现该网络的输入表示、权重与计算图。仅修改一个架构字符串不能支持新模型。宿主、时间轴、导出、C ABI 不需要按家族复制业务流程。

## 设备

公开 C ABI 的 `stemora_options.device`：

| 值 | 含义 |
| --- | --- |
| 0 | 自动，优先 CUDA，必要时选择 CPU |
| 1 | CPU |
| 2 | CUDA，不可用时明确失败 |

C# `ComputeDevice` 保持相同值。原生 CLI 为 `--device auto|cpu|cuda`。GPU 不可用的显式 CUDA 请求不伪装成 CUDA 成功。

## 音轨元数据

SW 六轨顺序为 `bass, drums, other, vocals, guitar, piano`，来自原模型 YAML。内置仓库描述与完整 SHA-256 绑定。GGUF 文件本身只声明六个输出，没有乐器名称，宿主据已验证的模型配置传入顺序。

未知自定义多轨模型没有语义元数据时使用 `stem_1` 等名称。可在模型旁提供 `.gguf.stemora.json`：

```json
{"family":"bs_roformer_sw","stems":["bass","drums","other","vocals","guitar","piano"]}
```

或 CLI 显式指定 `--profile` / `--stems`。名字必须唯一、数量一致且不能成为文件路径。原生层再次校验；SW profile 只接受六输出 BS Roformer。

## 构建与验证

```powershell
.\scripts\Build-Native.ps1
ctest --test-dir .local/native-prebuilt-cuda-build --output-on-failure
```

脚本固定依赖版本与校验值，从源码编译本仓库基础层和 CPU 后端，默认复用已构建的 GGML CUDA 计算库。从源码构建 CUDA 算子使用 `-BuildTensorBackend`。默认路线已经过完整构建和 CUDA / CPU 实测。可选的全算子源码路线仅完成配置与部分编译，本次未验证完整构建。构建产物在 `artifacts/native/`。工具链、CUDA 版本和下载依赖以脚本中的固定值为准。CUDA 运行时随发布包提供，使用 GPU 还需要兼容的 NVIDIA 驱动。

如需代理，可使用 `-Proxy http://localhost:7897`，或设置 `STEMORA_DOWNLOAD_PROXY`。`-CpuOnly` 生成独立的 CPU-only 测试 / CI 产物。

```powershell
.\artifacts\native\stemora-infra.exe probe --model model.gguf --profile bs_roformer_sw
.\artifacts\native\stemora-infra.exe separate --model model.gguf --profile bs_roformer_sw `
  --input stereo-44100.wav --output-dir .\stems --device cuda
```

原生宿主接受 44.1 kHz 双声道 WAV；C# 核心负责压缩格式解码与重采样。NDJSON 输出 `model`、`progress`、`completed` 或 `error`。成功后 `native-result.json` 将输出文件映射到音轨 ID；C# 验证后导出用户选择的 WAV 格式。

每个会话只允许一个处理调用；可以创建独立会话。取消标志在块之间检查，C# 取消同时终止宿主进程树。调用方必须等处理结束再销毁会话。

## 实际检查

- 模型：固定 SHA-256 的 SW Q8_0、BS Roformer、Mel-Band Roformer。
- 六轨：数量、名称、长度、有限数值、非零能量及合并与原始混音的一致性。
- 宿主：中文路径、显式设备参数、取消、错误码、manifest。
- C ABI：版本与参数校验、名称安全、取消、不同音频长度与重叠配置的采样级拼接。

原模型镜像：https://huggingface.co/FractalSurfer/BS-RoFormer-SW-6stem

SW 原始权重未提供明确许可证；镜像转换不赋予额外许可。推理引擎许可不能代替权重许可。

实测记录：RTX 5060 Ti (驱动 621.14) 上，12 秒音乐 SW 六轨 CUDA 输出合并后的余弦相似度为 0.999828，相对重建误差为 -34.56 dB。此项用于端到端一致性检查，不是分离质量评分或性能基准。
