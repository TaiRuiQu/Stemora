# BPM、调性与自动吸附

首版采用一条简单流程：**原曲 → 神经网络拍点 / 大小调分析 → 人声旋律识别 → 固定十六分音符网格吸附**。复用 MuScriptor 的本地 Python / PyTorch 环境，不增加后台服务。

## 分析

- 使用 `madmom-infer` 的 RNN 拍点 / 重拍模型与 CNN 调性模型。软件固定到提交 `bffead9be61857fd44b2e0d3a2510d9d94c8f2d2`，通过固定提交的 ZIP 安装，不要求用户安装 Git。
- 首次准备转谱模型时安装依赖，并下载约 5.7 MiB 的九个分析权重文件。权重来自官方 `CPJKU/madmom_models`，版本和 SHA-256 由固定的软件包目录记录；模型保存在 `models/music-analysis/madmom_infer/models`，每次准备和推理前校验。
- 应用优先分析通过「选择原曲」关联的混音，其次使用导入的源音频；源音频丢失但已提取人声仍可用时分析人声。因此直接导入人声且未关联原曲时，BPM / 调性分析也使用人声。CLI 默认分析传入的人声音频。音频先按所选范围解码为 44.1 kHz 单声道，推理完成后将拍点换回原曲绝对秒数。
- 固定为 4/4 拍，RNN 输出拍点及每拍在小节中的位置。对整段拍点拟合一条直线，得到单一 BPM 与小节首拍偏移，避免逐帧取整误差在长曲中累积。不另装速度模型。
- CNN 输出 12 大调 + 12 小调。使用不超过 30 秒的窗口、按窗口长度汇总概率，限制显存占用。保存真实主音和大小调模式；小调简谱采用 `6=主音`，例如 A 小调显示 `6=A`、`1=C`。

## 吸附

以四分音符为一拍，网格间隔为 `Δ = 60 / BPM / 4` 秒。音符开始位置吸附为 `首拍 + round((开始 - 首拍) / Δ) × Δ`，因此保留半拍、四分之一拍等正常弱拍位置。

音符长度选择最接近的十六分、八分、四分、二分或全音符，即 `1 / 2 / 4 / 8 / 16` 个网格；受下一音符和选段末尾约束时选择能放下的普通时值。休止保留，音高不变，不引入附点、三连音、装饰音、自由速度或自动转调。简谱跨拍延续使用已有延音表示，奇数网格会拆成普通时值。

多个音符吸附到同一位置时都保留，并标记待核对。原始识别结果保存在 `OriginalNotes` 与分析记录，首次吸附产生一个撤销步骤。识别完成后默认开启编辑器 `1/16` 吸附，用户仍能自由编辑。

未检测到拍点或输入为静音时，不编造 BPM，也不移动音符；不足一个网格的选段同样保留原时间。自动分析不代表人工确认，导出仍沿用「节奏待确认」状态。

## 接入与边界

- `MuScriptorService.PrepareAsync`：依赖与权重准备，可给旧版环境增量安装。
- `MuScriptorService.AnalyzeAsync` / `muscriptor_bridge.py analyze`：本地分析、进度、取消及临时文件清理。
- `MelodyTiming.Apply`：保存分析来源与大小调，量化音符并建立撤销快照。应用与 CLI 使用同一实现。
- `MelodyDocument.MusicAnalysis` / `KeyIsMinor`：可选新增字段，旧版 schema v1 草稿继续可读，打开旧稿不自动重写音符。

这是固定速度、固定拍号、单一调性的简化版。纯人声、弱节奏、变速和转调歌曲可能需要手动修正。分析软件为 BSD-2-Clause；下载的 madmom 权重采用 **CC BY-NC-SA 4.0**，与应用源代码的 MIT 许可证分开。

验证命令：

```powershell
dotnet build Stemora.sln -c Release -p:Platform=x64
dotnet run --project tests/Stemora.Tests -c Release
# 在已准备的转谱 Python 环境中运行
python -m unittest discover -s tests -p 'test_*bridge.py' -v
```

已执行核心检查与 Python 适配测试，并用实际下载的分析权重在 Windows CPU 上运行合成钢琴和 TorchAudio 官方音乐样例。此验证检查真实推理链路，不作为 BPM / 调性准确率评测。

上游：

- https://github.com/openmirlab/madmom-infer/tree/bffead9be61857fd44b2e0d3a2510d9d94c8f2d2
- https://github.com/CPJKU/madmom_models
- https://docs.pytorch.org/audio/2.8.0/tutorials/hybrid_demucs_tutorial.html
