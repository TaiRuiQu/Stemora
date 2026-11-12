"""Stemora's local adapter for the pinned official muscriptor 0.4.0 API.

Audio stays local. Credentials are inherited from HF_TOKEN / the HF login cache.
The wire protocol is NDJSON progress on stdout; diagnostics go to stderr.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys

ANALYSIS_REVISION = "bffead9be61857fd44b2e0d3a2510d9d94c8f2d2"
ANALYSIS_FAMILIES = ("downbeats_blstm", "key_cnn")


def report(stage, fraction=None, message=""):
    print(json.dumps(dict(stage=stage, fraction=fraction, message=message)), flush=True)


def download(args):
    from huggingface_hub import hf_hub_download
    destination = Path(args.model_dir)
    destination.mkdir(parents=True, exist_ok=True)
    repository = f"MuScriptor/muscriptor-{args.variant}"
    report("download", message=repository)
    for name in ("config.json", "model.safetensors"):
        hf_hub_download(repository, name, revision=args.revision, local_dir=destination)
    weights = destination / "model.safetensors"
    report("verify", message="SHA-256")
    digest = hashlib.sha256()
    with weights.open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(block)
    if weights.stat().st_size != args.size or digest.hexdigest() != args.sha256:
        weights.unlink()
        raise ValueError("Downloaded weights failed size / SHA-256 verification; retry the download.")
    (destination / f"verified-{args.revision}").write_text(args.sha256, encoding="utf-8")
    report("download", 1, repository)


def transcribe(args):
    # Inference uses only local files; it must not initiate a model download.
    os.environ["HF_HUB_OFFLINE"] = "1"
    import torch
    from muscriptor import TranscriptionModel, NoteEndEvent
    from muscriptor.events import ProgressEvent
    device = ("cuda" if torch.cuda.is_available() else "cpu") if args.device == "auto" else args.device
    if device == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA is unavailable. Select CPU and prepare its runtime, or check the NVIDIA driver.")
    report("loading", message=device)
    model = TranscriptionModel.load_model(Path(args.model_dir) / "model.safetensors", device=device)
    notes = []
    # The official model represents singing using the clarinet instrument group.
    # Constrain the separated vocal input rather than guessing a melody from a mix.
    with torch.inference_mode():
        for event in model.transcribe(args.input, instruments=["clarinet"], batch_size=1):
            if isinstance(event, ProgressEvent):
                report("transcribe", event.completed / event.total if event.total else None)
            elif isinstance(event, NoteEndEvent) and event.start_event.instrument != "drums":
                notes.append(dict(start=event.start_event.start_time, end=event.end_time, pitch=event.start_event.pitch))
    destination = Path(args.output)
    temporary = destination.with_suffix(".tmp")
    temporary.write_text(json.dumps(notes, allow_nan=False), encoding="utf-8")
    temporary.replace(destination)
    report("transcribe", 1)


def analysis_files(directory, download=False):
    from madmom_infer.models import cache_info, download_checkpoint
    os.environ["XDG_CACHE_HOME"] = str(Path(directory).resolve())
    root = Path(directory) / "madmom_infer" / "models"
    if download:
        for family in ANALYSIS_FAMILIES:
            report("analysis-download", message=family)
            download_checkpoint(family, cache_root=root)
    # Verify before constructing processors, which reuse this process-local cache.
    files = {}
    for family in ANALYSIS_FAMILIES:
        state = cache_info(family, cache_root=root)[family]
        if not state["complete"]:
            raise RuntimeError("Music analysis weights are missing or corrupt. Prepare the model again.")
        files[family] = [str(item["path"]) for item in state["files"]]
    return files


def prepare_analysis(args):
    analysis_files(args.analysis_dir, download=True)
    report("analysis-download", 1)


def beat_grid(beats):
    import numpy as np
    beats = np.asarray(beats, dtype=float)
    if beats.ndim != 2 or beats.shape[1] != 2 or len(beats) < 4 or not np.isfinite(beats).all():
        return None, None
    times = beats[:, 0]
    if np.any(np.diff(times) <= 0):
        return None, None
    # A single constant-tempo grid is sufficient for the first version. Fit the whole
    # beat sequence rather than letting 10 ms frame rounding accumulate over a song.
    period, phase = np.polyfit(np.arange(len(times)), times, 1)
    if period <= 0 or not 20 <= 60 / period <= 400:
        return None, None
    # Anchor bars on a neural downbeat, including when the crop starts mid-bar.
    downbeat_index = (1 - int(beats[0, 1])) % 4
    origin = float((phase + downbeat_index * period) % (4 * period))
    return float(60 / period), origin


def analyze(args):
    import numpy as np
    import soundfile as sf
    import torch
    from madmom_infer.audio.signal import Signal
    from madmom_infer.features.downbeats import RNNDownBeatProcessor, DBNDownBeatTrackingProcessor
    from madmom_infer.features.key import CNNKeyRecognitionProcessor

    device = ("cuda" if torch.cuda.is_available() else "cpu") if args.device == "auto" else args.device
    if device == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA is unavailable. Select CPU and prepare its runtime, or check the NVIDIA driver.")
    torch.set_num_threads(min(4, os.cpu_count() or 1))
    analysis_files(args.analysis_dir)
    audio, sr = sf.read(args.input, dtype="float32", always_2d=True)
    audio = audio.mean(axis=1)
    if sr != 44100 or not len(audio) or not np.isfinite(audio).all():
        raise ValueError("Music analysis requires finite 44100 Hz audio.")
    result = dict(bpm=None, firstBeat=None, key=None, isMinor=False, revision=ANALYSIS_REVISION)
    if np.max(np.abs(audio)) > 1e-7:
        report("analyze-beats")
        beat_model = RNNDownBeatProcessor(backend="torch", device=device)
        signal = Signal(audio, sample_rate=sr, num_channels=1)
        with torch.inference_mode():
            activations = beat_model(signal)
        beats = DBNDownBeatTrackingProcessor(beats_per_bar=[4], fps=100)(activations)
        result["bpm"], result["firstBeat"] = beat_grid(beats)
        del beat_model, activations
        report("analyze-key")
        key_model = CNNKeyRecognitionProcessor(backend="torch", device=device)
        # Bounded CNN windows avoid allocating a whole song's feature maps on the GPU.
        probabilities = np.zeros(24, dtype=float)
        window = 30 * sr
        with torch.inference_mode():
            for start in range(0, len(audio), window):
                chunk = audio[start:start + window]
                if len(chunk) < sr and start > 0:
                    continue
                prediction = np.asarray(key_model(Signal(chunk, sample_rate=sr, num_channels=1))).reshape(-1)
                if prediction.shape != (24,) or not np.isfinite(prediction).all():
                    raise ValueError("The neural key model returned invalid probabilities.")
                probabilities += prediction * len(chunk)
        key_class = int(probabilities.argmax())
        # madmom's 24 classes start at A major, then A minor (not at C).
        result["key"] = (key_class % 12 + 9) % 12
        result["isMinor"] = key_class >= 12
    destination = Path(args.output)
    temporary = destination.with_suffix(".tmp")
    temporary.write_text(json.dumps(result, allow_nan=False), encoding="utf-8")
    temporary.replace(destination)
    report("analyze", 1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["download", "transcribe", "prepare-analysis", "analyze"])
    parser.add_argument("--model-dir")
    parser.add_argument("--analysis-dir")
    parser.add_argument("--variant", choices=["small", "medium", "large"])
    parser.add_argument("--revision")
    parser.add_argument("--sha256")
    parser.add_argument("--size", type=int)
    parser.add_argument("--input")
    parser.add_argument("--output")
    parser.add_argument("--device", choices=["auto", "cpu", "cuda"], default="auto")
    args = parser.parse_args()
    try:
        {"download": download, "transcribe": transcribe, "prepare-analysis": prepare_analysis, "analyze": analyze}[args.command](args)
    except Exception as error:
        # Never print request headers, environment variables or credentials.
        message = str(error)
        token = os.environ.get("HF_TOKEN")
        if token:
            message = message.replace(token, "[redacted]")
        print(f"{type(error).__name__}: {message}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
