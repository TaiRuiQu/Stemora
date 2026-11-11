"""Measure the real Windows native backend. Requires no Python packages.

Example: python scripts/benchmark-models.py --devices cuda cpu --repeat 2
Process RAM comes from Windows PSAPI; GPU memory comes from WDDM PDH counters.
NVML device memory is recorded separately as a cross-check, never as process VRAM.
"""

import argparse
import ctypes as C
from ctypes import wintypes as W
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import statistics
import subprocess
import time
import wave


ROOT = Path(__file__).resolve().parents[1]
MIB = 1024 * 1024


class ProcessMemory(C.Structure):
    _fields_ = [("cb", W.DWORD), ("page_faults", W.DWORD)] + [
        (name, C.c_size_t) for name in (
            "peak_working_set", "working_set", "peak_paged_pool", "paged_pool",
            "peak_nonpaged_pool", "nonpaged_pool", "commit", "peak_commit", "private_commit")]


class CounterUnion(C.Union):
    _fields_ = [("large", C.c_longlong), ("double", C.c_double)]


class CounterValue(C.Structure):
    _fields_ = [("status", W.DWORD), ("value", CounterUnion)]


class CounterItem(C.Structure):
    _fields_ = [("name", W.LPWSTR), ("formatted", CounterValue)]


class GpuMemory(C.Structure):
    _fields_ = [(name, C.c_ulonglong) for name in ("total", "free", "used")]


class GpuUtilization(C.Structure):
    _fields_ = [("gpu", C.c_uint), ("memory", C.c_uint)]


class WindowsMonitor:
    def __init__(self):
        self.kernel = C.WinDLL("kernel32", use_last_error=True)
        self.psapi = C.WinDLL("psapi", use_last_error=True)
        self.kernel.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]
        self.kernel.OpenProcess.restype = W.HANDLE
        self.kernel.CloseHandle.argtypes = [W.HANDLE]
        self.psapi.GetProcessMemoryInfo.argtypes = [W.HANDLE, C.POINTER(ProcessMemory), W.DWORD]
        self.kernel.GetProcessTimes.argtypes = [W.HANDLE] + [C.POINTER(W.FILETIME)] * 4
        self.pdh = C.WinDLL("pdh")
        self.pdh.PdhOpenQueryW.argtypes = [W.LPCWSTR, C.c_size_t, C.POINTER(W.HANDLE)]
        self.pdh.PdhAddEnglishCounterW.argtypes = [W.HANDLE, W.LPCWSTR, C.c_size_t, C.POINTER(W.HANDLE)]
        self.pdh.PdhCollectQueryData.argtypes = [W.HANDLE]
        self.pdh.PdhCloseQuery.argtypes = [W.HANDLE]
        self.pdh.PdhGetFormattedCounterArrayW.argtypes = [
            W.HANDLE, W.DWORD, C.POINTER(W.DWORD), C.POINTER(W.DWORD), C.c_void_p]
        self.query = W.HANDLE()
        self._check(self.pdh.PdhOpenQueryW(None, 0, C.byref(self.query)))
        self.counters = {}
        self.errors = {}
        for name, path in {
            "dedicated": r"\GPU Process Memory(*)\Dedicated Usage",
            "shared": r"\GPU Process Memory(*)\Shared Usage",
            "adapter_dedicated": r"\GPU Adapter Memory(*)\Dedicated Usage",
        }.items():
            counter = W.HANDLE()
            result = self.pdh.PdhAddEnglishCounterW(self.query, path, 0, C.byref(counter))
            if result == 0:
                self.counters[name] = counter
            else:
                self.errors[name] = hex(result & 0xffffffff)
        self.nvml = None
        driver = Path(os.environ["WINDIR"]) / "System32/nvml.dll"
        if driver.exists():
            self.nvml = C.WinDLL(str(driver))
            self.nvml.nvmlDeviceGetHandleByIndex_v2.argtypes = [C.c_uint, C.POINTER(C.c_void_p)]
            self.nvml.nvmlDeviceGetMemoryInfo.argtypes = [C.c_void_p, C.POINTER(GpuMemory)]
            self.nvml.nvmlDeviceGetUtilizationRates.argtypes = [C.c_void_p, C.POINTER(GpuUtilization)]
            self.device = C.c_void_p()
            self._check(self.nvml.nvmlInit_v2())
            self._check(self.nvml.nvmlDeviceGetHandleByIndex_v2(0, C.byref(self.device)))
        self.pdh.PdhCollectQueryData(self.query)

    @staticmethod
    def _check(result):
        if result != 0:
            raise RuntimeError(f"Windows/NVIDIA monitor failed: {result:#x}")

    def counter_values(self, counter):
        size, count = W.DWORD(), W.DWORD()
        result = self.pdh.PdhGetFormattedCounterArrayW(counter, 0x400, C.byref(size), C.byref(count), None)
        if (result & 0xffffffff) != 0x800007d2 or size.value == 0:
            return {}
        buffer = C.create_string_buffer(size.value)
        result = self.pdh.PdhGetFormattedCounterArrayW(counter, 0x400, C.byref(size), C.byref(count), buffer)
        if result != 0:
            return {}
        values = C.cast(buffer, C.POINTER(CounterItem))
        return {values[i].name: values[i].formatted.value.large for i in range(count.value)
                if values[i].formatted.status in (0, 1)}

    def gpu(self, pid):
        self.pdh.PdhCollectQueryData(self.query)
        result = {}
        for name, counter in self.counters.items():
            values = self.counter_values(counter)
            selected = {k: v for k, v in values.items() if name.startswith("adapter_") or k.startswith(f"pid_{pid}_")}
            result[name] = sum(selected.values()) if selected else None
        if self.nvml:
            memory, utilization = GpuMemory(), GpuUtilization()
            if self.nvml.nvmlDeviceGetMemoryInfo(self.device, C.byref(memory)) == 0:
                result["device_used"] = memory.used
            if self.nvml.nvmlDeviceGetUtilizationRates(self.device, C.byref(utilization)) == 0:
                result["device_gpu_percent"] = utilization.gpu
        return result

    def memory(self, handle):
        value = ProcessMemory()
        value.cb = C.sizeof(value)
        if not self.psapi.GetProcessMemoryInfo(handle, C.byref(value), value.cb):
            raise C.WinError(C.get_last_error())
        return {name: getattr(value, name) for name in ("working_set", "peak_working_set", "private_commit", "peak_commit")}

    def cpu_seconds(self, handle):
        values = [W.FILETIME() for _ in range(4)]
        if not self.kernel.GetProcessTimes(handle, *[C.byref(v) for v in values]):
            return None
        return sum((values[i].dwHighDateTime << 32) + values[i].dwLowDateTime for i in (2, 3)) / 10_000_000

    def close(self):
        self.pdh.PdhCloseQuery(self.query)
        if self.nvml:
            self.nvml.nvmlShutdown()


def prepare_input(destination, seconds):
    # Deterministic synthetic stereo PCM avoids including a user's audio in benchmark records.
    import math
    from array import array
    samples = array("h")
    for i in range(round(44100 * seconds)):
        t = i / 44100
        left = 0.18 * math.sin(2 * math.pi * 220 * t) + 0.1 * math.sin(2 * math.pi * 660 * t)
        right = 0.16 * math.sin(2 * math.pi * 330 * t) + 0.12 * math.sin(2 * math.pi * 880 * t)
        samples.extend((round(32767 * left), round(32767 * right)))
    with wave.open(str(destination), "wb") as output:
        output.setnchannels(2)
        output.setsampwidth(2)
        output.setframerate(44100)
        output.writeframes(samples.tobytes())


def peak(samples, name):
    values = [sample[name] for sample in samples if sample.get(name) is not None]
    return max(values) if values else None


def hardware_inventory():
    command = """
    $ErrorActionPreference = 'Stop'
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    [ordered]@{
        cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
        memoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber
        gpu = @(nvidia-smi --query-gpu=index,name,memory.total,driver_version,compute_cap --format=csv,noheader)
    } | ConvertTo-Json -Depth 4
    """
    result = subprocess.run(["powershell.exe", "-NoProfile", "-Command", command], check=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    return json.loads(result.stdout.decode("utf-8-sig"))


def run_model(args, model, device, repeat, folder, monitor):
    stem = f"{model['id']}-{device}-{repeat}"
    output = folder / stem
    output.mkdir(exist_ok=True)
    path = args.models_directory / Path(model["repositoryPath"]).name
    command = [str(args.engine), "separate", "--model", str(path), "--input", str(folder / "input.wav"),
               "--output-dir", str(output), "--device", device, "--threads", str(args.threads),
               "--stems", ",".join(model["stems"])]
    if model.get("profile"):
        command += ["--profile", model["profile"]]
    if args.chunk_size:
        command += ["--chunk-size", str(args.chunk_size)]
    if args.overlap:
        command += ["--overlap", str(args.overlap)]
    baseline = [monitor.gpu(-1) for _ in range(3)]
    flags = subprocess.CREATE_NO_WINDOW
    samples = []
    with (output / "stdout.ndjson").open("w", encoding="utf-8") as stdout, (output / "stderr.log").open("w", encoding="utf-8") as stderr:
        started = time.perf_counter()
        process = subprocess.Popen(command, stdout=stdout, stderr=stderr, creationflags=flags)
        handle = monitor.kernel.OpenProcess(0x410, False, process.pid)
        if not handle:
            process.kill()
            raise C.WinError(C.get_last_error())
        timed_out = False
        try:
            while process.poll() is None:
                sample = {"seconds": time.perf_counter() - started, **monitor.memory(handle), **monitor.gpu(process.pid)}
                samples.append(sample)
                if sample["seconds"] > args.timeout:
                    timed_out = True
                    process.kill()
                    break
                time.sleep(args.interval)
            process.wait()
            elapsed = time.perf_counter() - started
            # OS-maintained RAM peaks include allocations that occur between sample ticks.
            samples.append({"seconds": elapsed, **monitor.memory(handle)})
            cpu_seconds = monitor.cpu_seconds(handle)
        finally:
            monitor.kernel.CloseHandle(handle)
    log = (output / "stderr.log").read_text(encoding="utf-8", errors="replace")
    events = [json.loads(line) for line in (output / "stdout.ndjson").read_text(encoding="utf-8").splitlines() if line.startswith("{")]
    descriptor = next((event["data"] for event in events if event.get("type") == "model"), None)
    buffers = [float(value) for value in re.findall(r"compute_buffer=([\d.]+) MiB", log)]
    utilization = [sample["device_gpu_percent"] for sample in samples if sample.get("device_gpu_percent") is not None]
    record = {
        "modelId": model["id"], "device": device, "repeat": repeat, "seconds": round(elapsed, 4),
        "audioSeconds": args.seconds, "realTimeFactor": round(elapsed / args.seconds, 4), "threads": args.threads,
        "sampleIntervalSeconds": args.interval, "sampleCount": len(samples), "exitCode": process.returncode,
        "timedOut": timed_out, "descriptor": descriptor, "modelBytes": path.stat().st_size,
        "chunkFrames": args.chunk_size or (descriptor or {}).get("chunkFrames"),
        "overlap": args.overlap or (descriptor or {}).get("overlap"),
        "peakWorkingSetMiB": round(peak(samples, "peak_working_set") / MIB, 3),
        "peakCommitMiB": round(peak(samples, "peak_commit") / MIB, 3),
        "peakDedicatedVramMiB": round(peak(samples, "dedicated") / MIB, 3) if peak(samples, "dedicated") is not None else None,
        "peakSharedGpuMiB": round(peak(samples, "shared") / MIB, 3) if peak(samples, "shared") is not None else None,
        "deviceBaselineMiB": round(statistics.median(s["device_used"] for s in baseline) / MIB, 3) if monitor.nvml else None,
        "devicePeakMiB": round(peak(samples, "device_used") / MIB, 3) if monitor.nvml else None,
        "meanDeviceGpuPercent": round(statistics.mean(utilization), 2) if utilization else None,
        "cpuSeconds": round(cpu_seconds, 4) if cpu_seconds is not None else None,
        "meanCpuPercent": round(cpu_seconds / elapsed / os.cpu_count() * 100, 2) if cpu_seconds is not None else None,
        "computeBufferMiB": max(buffers) if buffers else None,
        "command": command,
    }
    (output / "samples.json").write_text(json.dumps(samples, indent=2), encoding="utf-8")
    (output / "measurement.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine", type=Path, default=ROOT / "artifacts/native/stemora-infra.exe")
    parser.add_argument("--models-directory", type=Path, default=ROOT / ".local/home/models")
    parser.add_argument("--models", nargs="+", default=["anvuew-q8_0", "bs-roformer-sw-q8_0", "becruily-q4_0", "gaboxr67-q8_0"])
    parser.add_argument("--devices", nargs="+", choices=["cpu", "cuda"], default=["cuda", "cpu"])
    parser.add_argument("--repeat", type=int, default=2)
    parser.add_argument("--threads", type=int, default=6)
    parser.add_argument("--seconds", type=float, default=6)
    parser.add_argument("--chunk-size", type=int)
    parser.add_argument("--overlap", type=int)
    parser.add_argument("--interval", type=float, default=0.1)
    parser.add_argument("--timeout", type=float, default=600)
    parser.add_argument("--output", type=Path, default=ROOT / ".local/model-benchmarks/defaults")
    parser.add_argument("--monitor-check", action="store_true")
    args = parser.parse_args()
    monitor = WindowsMonitor()
    try:
        if args.monitor_check:
            print(json.dumps({"monitorErrors": monitor.errors, "gpu": monitor.gpu(os.getpid()),
                              "hardware": hardware_inventory()}, indent=2), flush=True)
            return
        args.output.mkdir(parents=True, exist_ok=True)
        catalog = json.loads((ROOT / "src/Stemora.Core/Data/models.json").read_text(encoding="utf-8"))
        models = [next(model for model in catalog if model["id"] == id_) for id_ in args.models]
        for model in models:
            path = args.models_directory / Path(model["repositoryPath"]).name
            with path.open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            if digest != model["sha256"]:
                raise ValueError(f"Model hash mismatch: {model['id']}")
        prepare_input(args.output / "input.wav", args.seconds)
        results = []
        report = {"recordedAt": dt.datetime.now(dt.timezone.utc).isoformat(), "hardware": hardware_inventory(), "monitorErrors": monitor.errors,
            "inputSha256": hashlib.sha256((args.output / "input.wav").read_bytes()).hexdigest(),
            "engineManifest": json.loads((args.engine.parent / "runtime-manifest.json").read_text(encoding="utf-8-sig")),
            "scriptSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "runs": results}
        for device in args.devices:
            for model in models:
                for repeat in range(1, args.repeat + 1):
                    print(f"START {model['id']} / {device} / {repeat}", flush=True)
                    record = run_model(args, model, device, repeat, args.output, monitor)
                    results.append(record)
                    (args.output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                    print(json.dumps({key: record[key] for key in ("modelId", "device", "seconds", "peakWorkingSetMiB",
                        "peakCommitMiB", "peakDedicatedVramMiB", "computeBufferMiB", "exitCode")}), flush=True)
                    if record["exitCode"] != 0:
                        raise RuntimeError(f"Benchmark failed; see {args.output / (model['id'] + '-' + device + '-' + str(repeat))}")
                    if device == "cuda" and (not record["descriptor"] or "CUDA" not in record["descriptor"].get("backend", "").upper()):
                        raise RuntimeError("CUDA benchmark did not use CUDA.")
    finally:
        monitor.close()


if __name__ == "__main__":
    main()
