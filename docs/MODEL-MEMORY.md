# Roformer 本机内存与显存测试

测试日期：2026-11-11。CPU：12th Gen Intel(R) Core(TM) i5-12400F（6 核 / 12 线程），物理内存约 32 GB。
GPU：NVIDIA GeForce RTX 5060 Ti 16GB，驱动 621.14。Windows 11，内部版本 26300。

## 默认参数实测

每次使用新的原生引擎进程，44.1 kHz 双声道、6 秒合成 PCM 音频，CPU 固定 6 线程。分块大小与重叠次数采用各模型默认值。基础四组分别重复两次，其余实测样本各测一次；内存取最大值，耗时取平均值。目录中全部 FP16 / FP32 版本均直接测量，其余量化版本按同组模型估算。

| 模型 / 精度 | 分块 / 重叠 | CPU 内存峰值 | CUDA 主机内存峰值 | CUDA 显存峰值 | CPU 耗时 / 占用 | CUDA 耗时 |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| BS-RoFormer / FP16 | 960000 / 4 | 1841 MiB | 522 MiB | 1972 MiB | 144.00 s / 49.4% | 3.63 s |
| BS-RoFormer / FP32 | 960000 / 4 | 1712 MiB | 606 MiB | 1786 MiB | 119.81 s / 49.3% | 3.33 s |
| BS-RoFormer / Q8_0 | 960000 / 4 | 1690 MiB | 607 MiB | 1770 MiB | 128.31 s / 49.1% | 3.33 s |
| Mel-Band Roformer Deux / FP16 | 573300 / 2 | 1756 MiB | 561 MiB | 1758 MiB | 47.34 s / 49.0% | 4.13 s |
| Mel-Band Roformer Deux / FP32 | 573300 / 2 | 2017 MiB | 659 MiB | 1982 MiB | 38.37 s / 48.7% | 1.92 s |
| Mel-Band Roformer Deux / Q4_0 | 573300 / 2 | 1393 MiB | 644 MiB | 1362 MiB | 42.96 s / 49.0% | 1.72 s |
| BS Roformer SW / Q8_0 | 588800 / 2 | 1406 MiB | 694 MiB | 1310 MiB | 35.71 s / 48.4% | 1.97 s |
| Mel-Band Roformer voc_fv6 / FP16 | 352800 / 1 | 1402 MiB | 457 MiB | 1444 MiB | 21.55 s / 48.7% | 1.01 s |
| Mel-Band Roformer voc_fv6 / FP32 | 352800 / 1 | 1697 MiB | 558 MiB | 1704 MiB | 17.43 s / 48.1% | 1.31 s |
| Mel-Band Roformer voc_fv6 / Q4_0 | 352800 / 1 | 1033 MiB | 542 MiB | 1044 MiB | 18.43 s / 48.8% | 1.01 s |
| Mel-Band Roformer voc_fv6 / Q8_0 | 352800 / 1 | 1140 MiB | 541 MiB | 1152 MiB | 17.12 s / 48.6% | 1.11 s |

## 更长输入复核

| 模型 | 后端 | 音频长度 | 主机内存峰值 | 显存峰值 | 耗时 |
| --- | --- | ---: | ---: | ---: | ---: |
| BS-RoFormer / Q8_0 | cuda | 30 s | 635 MiB | 1770 MiB | 8.77 s |
| BS Roformer SW / Q8_0 | cuda | 30 s | 763 MiB | 1310 MiB | 9.68 s |
| Mel-Band Roformer Deux / Q4_0 | cuda | 30 s | 680 MiB | 1362 MiB | 7.96 s |
| Mel-Band Roformer voc_fv6 / Q8_0 | cuda | 30 s | 570 MiB | 1152 MiB | 2.32 s |

## 模型库采用的数据

表中的占用值对应上面的 6 秒测试条件。未直接运行的量化版本按同组模型估算；估算结果向上取整到 16 MiB。界面只显示 CPU / CUDA 与建议内存、显存容量。

| 模型 / 精度 | 数据来源 | CPU 内存 | CUDA 主机内存 | CUDA 显存 | 建议内存 / 显存 |
| --- | --- | ---: | ---: | ---: | --- |
| BS Roformer SW / Q8_0 | 实测 | 1406 MiB | 694 MiB | 1310 MiB | 8 GB / 4 GB |
| Mel-Band Roformer voc_fv6 / FP16 | 实测 | 1402 MiB | 457 MiB | 1444 MiB | 8 GB / 4 GB |
| Mel-Band Roformer voc_fv6 / FP32 | 实测 | 1697 MiB | 558 MiB | 1704 MiB | 8 GB / 4 GB |
| Mel-Band Roformer voc_fv6 / Q4_0 | 实测 | 1033 MiB | 542 MiB | 1044 MiB | 8 GB / 4 GB |
| Mel-Band Roformer voc_fv6 / Q4_1 | 估算 | 1056 MiB | 544 MiB | 1072 MiB | 8 GB / 4 GB |
| Mel-Band Roformer voc_fv6 / Q5_0 | 估算 | 1072 MiB | 544 MiB | 1072 MiB | 8 GB / 4 GB |
| Mel-Band Roformer voc_fv6 / Q5_1 | 估算 | 1088 MiB | 544 MiB | 1088 MiB | 8 GB / 4 GB |
| Mel-Band Roformer voc_fv6 / Q8_0 | 实测 | 1140 MiB | 541 MiB | 1152 MiB | 8 GB / 4 GB |
| BS-RoFormer / FP16 | 实测 | 1841 MiB | 522 MiB | 1972 MiB | 8 GB / 4 GB |
| BS-RoFormer / FP32 | 实测 | 1712 MiB | 606 MiB | 1786 MiB | 8 GB / 4 GB |
| BS-RoFormer / Q4_0 | 估算 | 1680 MiB | 608 MiB | 1760 MiB | 8 GB / 4 GB |
| BS-RoFormer / Q4_1 | 估算 | 1680 MiB | 608 MiB | 1760 MiB | 8 GB / 4 GB |
| BS-RoFormer / Q5_0 | 估算 | 1680 MiB | 608 MiB | 1760 MiB | 8 GB / 4 GB |
| BS-RoFormer / Q5_1 | 估算 | 1680 MiB | 608 MiB | 1760 MiB | 8 GB / 4 GB |
| BS-RoFormer / Q8_0 | 实测 | 1690 MiB | 607 MiB | 1770 MiB | 8 GB / 4 GB |
| Mel-Band Roformer Deux / FP16 | 实测 | 1756 MiB | 561 MiB | 1758 MiB | 8 GB / 4 GB |
| Mel-Band Roformer Deux / FP32 | 实测 | 2017 MiB | 659 MiB | 1982 MiB | 8 GB / 4 GB |
| Mel-Band Roformer Deux / Q4_0 | 实测 | 1393 MiB | 644 MiB | 1362 MiB | 8 GB / 4 GB |
| Mel-Band Roformer Deux / Q4_1 | 估算 | 1408 MiB | 656 MiB | 1376 MiB | 8 GB / 4 GB |
| Mel-Band Roformer Deux / Q5_0 | 估算 | 1424 MiB | 656 MiB | 1392 MiB | 8 GB / 4 GB |
| Mel-Band Roformer Deux / Q5_1 | 估算 | 1440 MiB | 656 MiB | 1408 MiB | 8 GB / 4 GB |
| Mel-Band Roformer Deux / Q8_0 | 估算 | 1504 MiB | 656 MiB | 1472 MiB | 8 GB / 4 GB |

## 测量口径与估算方法

- 主机内存采用 Windows `GetProcessMemoryInfo` 的 `PeakWorkingSetSize`，由操作系统记录峰值；另外在原始记录中保留提交内存峰值。工作集与提交内存不是同一个指标。
- 显存采用 WDDM `GPU Process Memory / Dedicated Usage`，只累计被测进程的实例，每 100 ms 采样；另存共享 GPU 内存。没有把整张显卡的总占用当作模型显存。NVML 的设备级空闲基线、峰值仅作为交叉检查，后台桌面程序仍可占用显卡。
- CPU 与 CUDA 分开运行。每次检查引擎实际报告的后端，CUDA 测试必须使用 CUDA0，CPU 测试必须使用 CPU。CPU 模式的少量显存记账来自已加载的驱动，并非 GPU 推理要求。
- 占用是原生推理进程数据，包括模型加载、计算图、音频分块和输出缓冲；不包含 WinUI 窗口进程、操作系统与其他应用。耗时包含启动、加载和导出，不是只计核心矩阵计算。
- CPU 占用由进程的用户态与内核态时间除以墙钟时间、再除以 12 个逻辑处理器计算。6 线程满载约为 50%。短音频仍会处理完整默认分块，不能直接按这张 6 秒耗时表的倍率推算整首歌曲用时。
- 估算仅在相同作者、名称和架构的 Q4 / Q5 / Q8 版本之间进行，沿用该组默认分块。CPU 内存和 CUDA 显存按实测锚点加减模型权重字节差。CUDA 主机内存则按最大的张量读取缓冲区变化调整，模型权重主要驻留显存。GGUF 头与张量偏移用于读取权重布局，不修改模型。
- 浮点内核和量化内核的临时工作区不同。例如 anvuew 的 FP32 文件比 Q8_0 大约 142 MiB，但本机显存峰值只增加约 16 MiB；Deux FP16 的增量又不同。因此目录中的 FP16 / FP32 全部实测，没有跨内核按文件体积倍率推算。
- 估算占用向上取整到 16 MiB，实测条目保留实际峰值。容量建议另行加入运行余量，不把建议容量混成实际占用。

额外的 Q4_0 校准样本用于检验量化估算，以下误差为未加余量的估算减去实测：

| 校准模型 | CPU 内存误差 | CUDA 主机内存误差 | 显存误差 |
| --- | ---: | ---: | ---: |
| gaboxr67-q4_0 | -0.2 MiB | -1.9 MiB | +0.7 MiB |

## 建议容量的计算

建议容量针对默认分块和最长约 10 分钟的普通双声道音频。系统内存按模型进程峰值的 1.25 倍，加音频增长所需的 `(20 + 8 × 音轨数) × 44100 × 秒数` 字节，以及 3 GiB 的系统与界面空间，再上取 8 / 16 / 32 / 64 GB。显卡按进程显存峰值的 1.2 倍，加 1 GiB 桌面空间，再上取 4 / 6 / 8 / 12 / 16 / 24 / 32 GB。

这些是建议容量，不是硬性启动门槛。更长音频、手动增大分块、其他后台程序、不同驱动或推理后端会改变占用。量化能减少权重占用，不能按相同倍率缩小计算图。每个条目的实测/估算来源、参考模型与分块参数保存在 models.json；不在界面重复解释。

## 复现

需要 Windows、Python 3.11 或更高版本、NVIDIA 驱动，以及已经构建好的 CUDA / CPU 后端。测量脚本只使用 Python 标准库，会重新读取本机硬件信息。先通过 CLI 下载命令取得表中的实测模型；脚本启动时再次校验目录中的固定 SHA-256。

```powershell
python scripts/benchmark-models.py --devices cuda cpu --repeat 2 --seconds 6 --output .local/model-benchmarks/defaults
python scripts/benchmark-models.py --models anvuew-fp32 gaboxr67-q4_0 --devices cuda cpu --repeat 1 --seconds 6 --output .local/model-benchmarks/precision
python scripts/benchmark-models.py --models becruily-fp16 --devices cuda cpu --repeat 1 --seconds 6 --output .local/model-benchmarks/fp16
python scripts/benchmark-models.py --models anvuew-fp16 gaboxr67-fp16 gaboxr67-fp32 becruily-fp32 --devices cuda cpu --repeat 1 --seconds 6 --output .local/model-benchmarks/floating
python scripts/benchmark-models.py --devices cuda --repeat 1 --seconds 30 --output .local/model-benchmarks/longer
python scripts/update-model-memory.py --reports .local/model-benchmarks/defaults/report.json .local/model-benchmarks/precision/report.json .local/model-benchmarks/fp16/report.json .local/model-benchmarks/floating/report.json .local/model-benchmarks/longer/report.json
```

原始摘要：[JSON](benchmarks/roformer-2026-11-11-rtx5060ti.json)。逐次日志和采样序列保存在 `.local/model-benchmarks`，不会进入便携包。

测量接口：[Windows 进程内存](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex)、[PDH 计数器](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcounterarrayw)、[NVIDIA NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceQueries.html)。
