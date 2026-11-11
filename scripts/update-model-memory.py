"""Regenerate catalog memory profiles and the readable report from local measurements.

python scripts/update-model-memory.py --reports .local/model-benchmarks/defaults/report.json \
    .local/model-benchmarks/precision/report.json .local/model-benchmarks/longer/report.json
"""

import argparse
import json
import math
from pathlib import Path
import struct


ROOT = Path(__file__).resolve().parents[1]
MIB = 1024 * 1024
BENCHMARK = "roformer-2026-11-11-rtx5060ti"
DOCUMENT = "docs/MODEL-MEMORY.md"
BASELINES = {"anvuew-q8_0", "bs-roformer-sw-q8_0", "becruily-q4_0", "gaboxr67-q8_0"}


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def gguf_layout(path):
    """Read GGUF layout only. Tensor payload is neither loaded nor modified."""
    with path.open("rb") as stream:
        def number(code):
            return struct.unpack("<" + code, stream.read(struct.calcsize("<" + code)))[0]

        def string():
            return stream.read(number("Q")).decode("utf-8")

        def skip(kind):
            if kind == 8:
                stream.seek(number("Q"), 1)
            elif kind == 9:
                inner, count = number("I"), number("Q")
                for _ in range(count):
                    skip(inner)
            else:
                stream.seek({0: 1, 1: 1, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 7: 1, 10: 8, 11: 8, 12: 8}[kind], 1)

        if stream.read(4) != b"GGUF" or number("I") not in (2, 3):
            raise ValueError(f"Unsupported model: {path}")
        tensors, metadata = number("Q"), number("Q")
        alignment = 32
        for _ in range(metadata):
            key, kind = string(), number("I")
            if key == "general.alignment" and kind == 4:
                alignment = number("I")
            else:
                skip(kind)
        offsets, tensor_bytes, largest_tensor, types = [], 0, 0, {}
        for _ in range(tensors):
            string()
            dimensions = number("I")
            shape = [number("Q") for _ in range(dimensions)]
            kind = number("I")
            block, size = {0: (1, 4), 1: (1, 2), 2: (32, 18), 3: (32, 20),
                           6: (32, 22), 7: (32, 24), 8: (32, 34), 26: (1, 4)}[kind]
            length = math.prod(shape) // block * size
            tensor_bytes += length
            largest_tensor = max(largest_tensor, length)
            types[str(kind)] = types.get(str(kind), 0) + 1
            offsets.append(number("Q"))
        data_start = math.ceil(stream.tell() / alignment) * alignment
        offsets = sorted(offsets) + [path.stat().st_size - data_start]
        return {"payloadBytes": offsets[-1], "tensorBytes": tensor_bytes, "largestTensorBytes": largest_tensor,
                "tensorTypes": types, "largestTensorSpanBytes": max(b - a for a, b in zip(offsets, offsets[1:]))}


def group(model):
    return model["author"], model["name"], model["architecture"]


def rounded(value):
    return math.ceil(max(16, value) / 16) * 16


def predicted(model, reference, measured, layouts, round_result=True):
    base = measured[reference["id"]]
    weight_delta = (model["size"] - reference["size"]) / MIB
    layout = layouts[reference["id"]]
    largest_delta = layout["largestTensorSpanBytes"] * (model["size"] / reference["size"] - 1) / MIB
    values = {"cpuRamMiB": base["cpuRamMiB"] + weight_delta,
              "cudaRamMiB": base["cudaRamMiB"] + largest_delta,
              "vramMiB": base["vramMiB"] + weight_delta}
    if round_result:
        # Keep estimated peaks distinct from the separate operating headroom in capacity tags.
        values = {key: rounded(value) for key, value in values.items()}
    return values


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reports", type=Path, nargs="+", required=True)
    parser.add_argument("--models-directory", type=Path, default=ROOT / ".local/home/models")
    args = parser.parse_args()
    reports = [load(path) for path in args.reports]
    manifests = [report["engineManifest"] for report in reports if report.get("engineManifest")]
    if not manifests:
        raise ValueError("Benchmark reports must retain the runtime manifest used during measurement.")
    def manifest_signature(manifest):
        return sorted((entry["file"], entry["size"], entry["sha256"]) for entry in manifest)
    if any(manifest_signature(manifest) != manifest_signature(manifests[0]) for manifest in manifests[1:]):
        raise ValueError("Cannot combine measurements from different runtime builds.")
    catalog_path = ROOT / "src/Stemora.Core/Data/models.json"
    catalog = load(catalog_path)
    by_id = {model["id"]: model for model in catalog}
    runs = [run for report in reports for run in report["runs"]]
    if any(run["exitCode"] != 0 or run["timedOut"] for run in runs):
        raise ValueError("Failed or incomplete measurements cannot populate the model catalog.")
    short = [run for run in runs if run["audioSeconds"] == 6]
    short_hashes = {report["inputSha256"] for report in reports if any(run["audioSeconds"] == 6 for run in report["runs"])}
    if len(short_hashes) != 1 or any(run["threads"] != 6 for run in short):
        raise ValueError("Reference runs must share the same six-second input and thread count.")
    measured = {}
    for id_ in sorted({run["modelId"] for run in short}):
        cpu = [run for run in short if run["modelId"] == id_ and run["device"] == "cpu"]
        cuda = [run for run in short if run["modelId"] == id_ and run["device"] == "cuda"]
        if not cpu or not cuda or any(run["descriptor"]["backend"] != "CPU" for run in cpu):
            raise ValueError(f"Missing CPU/CUDA pair: {id_}")
        if any("CUDA" not in run["descriptor"]["backend"].upper() or not run["peakDedicatedVramMiB"] for run in cuda):
            raise ValueError(f"Missing process VRAM measurement: {id_}")
        descriptor = cpu[0]["descriptor"]
        measured[id_] = {
            "cpuRamMiB": max(run["peakWorkingSetMiB"] for run in cpu),
            "cudaRamMiB": max(run["peakWorkingSetMiB"] for run in cuda),
            "vramMiB": max(run["peakDedicatedVramMiB"] for run in cuda),
            "cpuSeconds": sum(run["seconds"] for run in cpu) / len(cpu),
            "cpuPercent": sum(run["meanCpuPercent"] for run in cpu) / len(cpu),
            "cudaSeconds": sum(run["seconds"] for run in cuda) / len(cuda),
            "chunkFrames": descriptor["chunkFrames"], "overlap": descriptor["overlap"],
        }
    if not BASELINES.issubset(measured):
        raise ValueError("Each catalog model group needs a measured representative.")
    layouts = {id_: gguf_layout(args.models_directory / Path(by_id[id_]["repositoryPath"]).name) for id_ in measured}
    validation = []
    for id_ in sorted(measured.keys() - BASELINES):
        # Floating point kernels were measured directly after revealing different workspaces.
        # Only quantized holdouts validate the extrapolation used by the remaining catalog.
        if not by_id[id_]["quantization"].startswith("Q"):
            continue
        reference = next(by_id[id0] for id0 in BASELINES if group(by_id[id0]) == group(by_id[id_]))
        prediction = predicted(by_id[id_], reference, measured, layouts, round_result=False)
        error = {key: round(prediction[key] - measured[id_][key], 3) for key in prediction}
        validation.append({"modelId": id_, "referenceModelId": reference["id"], "errorMiB": error})
        if any(abs(value) > 128 for value in error.values()):
            raise ValueError(f"Estimation needs recalibration: {id_} {error}")
    for model in catalog:
        hardware = model["hardware"]
        if model["id"] in measured:
            reference = model
            profile = {key: round(measured[model["id"]][key], 3) for key in ("cpuRamMiB", "cudaRamMiB", "vramMiB")}
            basis = "measured"
        else:
            if not model["quantization"].startswith("Q"):
                raise ValueError(f"Floating point model needs a direct measurement: {model['id']}")
            reference = min((by_id[id_] for id_ in measured if group(by_id[id_]) == group(model)
                             and by_id[id_]["quantization"].startswith("Q")),
                            key=lambda ref: abs(ref["size"] - model["size"]))
            profile = predicted(model, reference, measured, layouts)
            basis = "estimated"
        reference_run = measured[reference["id"]]
        profile = {"basis": basis, "referenceModelId": reference["id"], **profile,
                   "chunkFrames": reference_run["chunkFrames"], "overlap": reference_run["overlap"],
                   "audioSeconds": 6, "benchmark": BENCHMARK}
        # Allow a ten-minute stereo input, native output buffers, desktop/GUI RAM, and GPU desktop use.
        pcm_growth = (20 + 8 * len(model["stems"])) * 44100 * (600 - 6) / MIB
        ram_gib = (max(profile["cpuRamMiB"], profile["cudaRamMiB"]) * 1.25 + pcm_growth + 3072) / 1024
        vram_gib = (profile["vramMiB"] * 1.2 + 1024) / 1024
        hardware.update({"recommendedRamGb": next(value for value in (8, 16, 32, 64) if value >= ram_gib),
                         "recommendedVramGb": next(value for value in (4, 6, 8, 12, 16, 24, 32) if value >= vram_gib),
                         "source": DOCUMENT, "memory": profile})
    catalog_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    public_runs = []
    for run in runs:
        public = {key: value for key, value in run.items() if key != "command"}
        public["chunkFrames"] = run.get("chunkFrames") or run["descriptor"]["chunkFrames"]
        public["overlap"] = run.get("overlap") or run["descriptor"]["overlap"]
        public_runs.append(public)
    artifact = {"id": BENCHMARK, "recordedAt": reports[0]["recordedAt"], "hardware": reports[0]["hardware"],
                "input": {"sampleRate": 44100, "channels": 2, "encoding": "PCM16", "signal": "deterministic synthetic tones",
                          "sha256BySeconds": {str(report["runs"][0]["audioSeconds"]): report["inputSha256"] for report in reports}},
                "threads": 6, "processRamCounter": "PSAPI PeakWorkingSetSize",
                "processVramCounter": "PDH GPU Process Memory / Dedicated Usage", "sampleIntervalSeconds": 0.1,
                "engineManifest": manifests[0], "layouts": layouts,
                "validation": validation, "runs": public_runs}
    artifact_folder = ROOT / "docs/benchmarks"
    artifact_folder.mkdir(exist_ok=True)
    (artifact_folder / (BENCHMARK + ".json")).write_text(json.dumps(artifact, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    write_document(catalog, measured, validation, runs, reports[0]["hardware"])
    print(f"Updated {len(measured)} measured / {len(catalog) - len(measured)} estimated model profiles.")


def write_document(catalog, measured, validation, runs, hardware):
    by_id = {model["id"]: model for model in catalog}
    cpu = hardware["cpu"][0]
    lines = ["# Roformer 本机内存与显存测试", "",
             f"测试日期：2026-11-11。CPU：{cpu['Name']}（6 核 / 12 线程），物理内存约 32 GB。",
             "GPU：NVIDIA GeForce RTX 5060 Ti 16GB，驱动 621.14。Windows 11，内部版本 26300。", "",
             "## 默认参数实测", "",
             "每次使用新的原生引擎进程，44.1 kHz 双声道、6 秒合成 PCM 音频，CPU 固定 6 线程。分块大小与重叠次数采用各模型默认值。基础四组分别重复两次，其余实测样本各测一次；内存取最大值，耗时取平均值。目录中全部 FP16 / FP32 版本均直接测量，其余量化版本按同组模型估算。", "",
             "| 模型 / 精度 | 分块 / 重叠 | CPU 内存峰值 | CUDA 主机内存峰值 | CUDA 显存峰值 | CPU 耗时 / 占用 | CUDA 耗时 |",
             "| --- | --- | ---: | ---: | ---: | ---: | ---: |"]
    for id_ in sorted(measured):
        model, result = by_id[id_], measured[id_]
        lines.append(f"| {model['name']} / {model['quantization']} | {result['chunkFrames']} / {result['overlap']} | "
                     f"{result['cpuRamMiB']:.0f} MiB | {result['cudaRamMiB']:.0f} MiB | {result['vramMiB']:.0f} MiB | "
                     f"{result['cpuSeconds']:.2f} s / {result['cpuPercent']:.1f}% | {result['cudaSeconds']:.2f} s |")
    longer = [run for run in runs if run["audioSeconds"] > 6]
    if longer:
        lines += ["", "## 更长输入复核", "", "| 模型 | 后端 | 音频长度 | 主机内存峰值 | 显存峰值 | 耗时 |",
                  "| --- | --- | ---: | ---: | ---: | ---: |"]
        for run in longer:
            lines.append(f"| {by_id[run['modelId']]['name']} / {by_id[run['modelId']]['quantization']} | {run['device']} | "
                         f"{run['audioSeconds']:g} s | {run['peakWorkingSetMiB']:.0f} MiB | {run['peakDedicatedVramMiB']:.0f} MiB | {run['seconds']:.2f} s |")
    lines += ["", "## 模型库采用的数据", "",
              "表中的占用值对应上面的 6 秒测试条件。未直接运行的量化版本按同组模型估算；估算结果向上取整到 16 MiB。界面只显示 CPU / CUDA 与建议内存、显存容量。", "",
              "| 模型 / 精度 | 数据来源 | CPU 内存 | CUDA 主机内存 | CUDA 显存 | 建议内存 / 显存 |",
              "| --- | --- | ---: | ---: | ---: | --- |"]
    for model in catalog:
        memory, hardware0 = model["hardware"]["memory"], model["hardware"]
        basis = "实测" if memory["basis"] == "measured" else "估算"
        lines.append(f"| {model['name']} / {model['quantization']} | {basis} | {memory['cpuRamMiB']:.0f} MiB | "
                     f"{memory['cudaRamMiB']:.0f} MiB | {memory['vramMiB']:.0f} MiB | "
                     f"{hardware0['recommendedRamGb']} GB / {hardware0['recommendedVramGb']} GB |")
    lines += ["", "## 测量口径与估算方法", "",
              "- 主机内存采用 Windows `GetProcessMemoryInfo` 的 `PeakWorkingSetSize`，由操作系统记录峰值；另外在原始记录中保留提交内存峰值。工作集与提交内存不是同一个指标。",
              "- 显存采用 WDDM `GPU Process Memory / Dedicated Usage`，只累计被测进程的实例，每 100 ms 采样；另存共享 GPU 内存。没有把整张显卡的总占用当作模型显存。NVML 的设备级空闲基线、峰值仅作为交叉检查，后台桌面程序仍可占用显卡。",
              "- CPU 与 CUDA 分开运行。每次检查引擎实际报告的后端，CUDA 测试必须使用 CUDA0，CPU 测试必须使用 CPU。CPU 模式的少量显存记账来自已加载的驱动，并非 GPU 推理要求。",
              "- 占用是原生推理进程数据，包括模型加载、计算图、音频分块和输出缓冲；不包含 WinUI 窗口进程、操作系统与其他应用。耗时包含启动、加载和导出，不是只计核心矩阵计算。",
              "- CPU 占用由进程的用户态与内核态时间除以墙钟时间、再除以 12 个逻辑处理器计算。6 线程满载约为 50%。短音频仍会处理完整默认分块，不能直接按这张 6 秒耗时表的倍率推算整首歌曲用时。",
              "- 估算仅在相同作者、名称和架构的 Q4 / Q5 / Q8 版本之间进行，沿用该组默认分块。CPU 内存和 CUDA 显存按实测锚点加减模型权重字节差。CUDA 主机内存则按最大的张量读取缓冲区变化调整，模型权重主要驻留显存。GGUF 头与张量偏移用于读取权重布局，不修改模型。",
              "- 浮点内核和量化内核的临时工作区不同。例如 anvuew 的 FP32 文件比 Q8_0 大约 142 MiB，但本机显存峰值只增加约 16 MiB；Deux FP16 的增量又不同。因此目录中的 FP16 / FP32 全部实测，没有跨内核按文件体积倍率推算。",
              "- 估算占用向上取整到 16 MiB，实测条目保留实际峰值。容量建议另行加入运行余量，不把建议容量混成实际占用。", "",
              "额外的 Q4_0 校准样本用于检验量化估算，以下误差为未加余量的估算减去实测：", "",
              "| 校准模型 | CPU 内存误差 | CUDA 主机内存误差 | 显存误差 |",
              "| --- | ---: | ---: | ---: |"]
    for value in validation:
        error = value["errorMiB"]
        lines.append(f"| {value['modelId']} | {error['cpuRamMiB']:+.1f} MiB | {error['cudaRamMiB']:+.1f} MiB | {error['vramMiB']:+.1f} MiB |")
    lines += ["", "## 建议容量的计算", "",
              "建议容量针对默认分块和最长约 10 分钟的普通双声道音频。系统内存按模型进程峰值的 1.25 倍，加音频增长所需的 `(20 + 8 × 音轨数) × 44100 × 秒数` 字节，以及 3 GiB 的系统与界面空间，再上取 8 / 16 / 32 / 64 GB。显卡按进程显存峰值的 1.2 倍，加 1 GiB 桌面空间，再上取 4 / 6 / 8 / 12 / 16 / 24 / 32 GB。", "",
              "这些是建议容量，不是硬性启动门槛。更长音频、手动增大分块、其他后台程序、不同驱动或推理后端会改变占用。量化能减少权重占用，不能按相同倍率缩小计算图。每个条目的实测/估算来源、参考模型与分块参数保存在 models.json；不在界面重复解释。", "",
              "## 复现", "", "需要 Windows、Python 3.11 或更高版本、NVIDIA 驱动，以及已经构建好的 CUDA / CPU 后端。测量脚本只使用 Python 标准库，会重新读取本机硬件信息。先通过 CLI 下载命令取得表中的实测模型；脚本启动时再次校验目录中的固定 SHA-256。", "", "```powershell",
              "python scripts/benchmark-models.py --devices cuda cpu --repeat 2 --seconds 6 --output .local/model-benchmarks/defaults",
              "python scripts/benchmark-models.py --models anvuew-fp32 gaboxr67-q4_0 --devices cuda cpu --repeat 1 --seconds 6 --output .local/model-benchmarks/precision",
              "python scripts/benchmark-models.py --models becruily-fp16 --devices cuda cpu --repeat 1 --seconds 6 --output .local/model-benchmarks/fp16",
              "python scripts/benchmark-models.py --models anvuew-fp16 gaboxr67-fp16 gaboxr67-fp32 becruily-fp32 --devices cuda cpu --repeat 1 --seconds 6 --output .local/model-benchmarks/floating",
              "python scripts/benchmark-models.py --devices cuda --repeat 1 --seconds 30 --output .local/model-benchmarks/longer",
              "python scripts/update-model-memory.py --reports .local/model-benchmarks/defaults/report.json .local/model-benchmarks/precision/report.json .local/model-benchmarks/fp16/report.json .local/model-benchmarks/floating/report.json .local/model-benchmarks/longer/report.json",
              "```", "",
              f"原始摘要：[JSON](benchmarks/{BENCHMARK}.json)。逐次日志和采样序列保存在 `.local/model-benchmarks`，不会进入便携包。", "",
              "测量接口：[Windows 进程内存](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex)、[PDH 计数器](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcounterarrayw)、[NVIDIA NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceQueries.html)。", ""]
    (ROOT / DOCUMENT).write_text("\n".join(lines), encoding="utf-8")


if __name__ == "__main__":
    main()
