"""Rebuild the bundled piano bank. Requires ffmpeg; no runtime download is used."""
import concurrent.futures
import gzip
import hashlib
import json
import struct
import subprocess
import tempfile
import urllib.request
from pathlib import Path

REVISION = "869b6f8d9cddb47d966238c012041480b1ce517a"
BASE = f"https://raw.githubusercontent.com/Tonejs/audio/{REVISION}/salamander/"
DESTINATION = Path(__file__).resolve().parents[1] / "src/Stemora.Core/Data/Piano"

def fetch(pitch):
    names = {0: "C", 3: "Ds", 6: "Fs", 9: "A"}
    name = f"{names[pitch % 12]}{pitch // 12 - 1}.mp3"
    data = urllib.request.urlopen(BASE + name, timeout=60).read()
    with tempfile.TemporaryDirectory() as directory:
        source = Path(directory) / name
        source.write_bytes(data)
        pcm = subprocess.run(["ffmpeg", "-v", "error", "-i", str(source), "-ac", "1", "-ar", "44100", "-t", "10", "-f", "s16le", "pipe:1"], check=True, capture_output=True).stdout
    values = list(struct.unpack("<" + "h" * (len(pcm) // 2), pcm))
    # Remove encoder/onset silence, retaining 1 ms before the first audible hammer attack.
    onset = next((i for i, value in enumerate(values[:4410]) if abs(value) >= 80), 0)
    trimmed = max(0, onset - 44)
    values = values[trimmed:]
    for i in range(min(2205, len(values))):
        values[-1 - i] = round(values[-1 - i] * i / 2205)
    pcm = struct.pack("<" + "h" * len(values), *values)
    packed = gzip.compress(pcm, compresslevel=9, mtime=0)
    (DESTINATION / f"{pitch}.pcm.gz").write_bytes(packed)
    return {"midi": pitch, "source": name, "sourceSha256": hashlib.sha256(data).hexdigest(), "pcmSha256": hashlib.sha256(pcm).hexdigest(), "trimmedFrames": trimmed, "frames": len(values)}

if __name__ == "__main__":
    DESTINATION.mkdir(parents=True, exist_ok=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        samples = list(pool.map(fetch, range(21, 109, 3)))
    (DESTINATION / "manifest.json").write_text(json.dumps({"sourceRevision": REVISION, "sampleRate": 44100, "channels": 1, "encoding": "signed 16-bit little-endian PCM, gzip", "samples": samples}, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared {len(samples)} piano samples, {sum(p.stat().st_size for p in DESTINATION.glob('*.gz')):,} bytes")
