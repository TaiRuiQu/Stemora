"""Adapter contract checks using muscriptor 0.4.0's actual public event types.

Run in a MuScriptor environment; weights and model access are not required.
These are adapter checks, not a measurement of transcription accuracy.
"""
import contextlib
import hashlib
import importlib.metadata
import importlib.util
import io
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

from muscriptor.events import NoteStartEvent, NoteEndEvent, ProgressEvent

SPEC = importlib.util.spec_from_file_location(
    "bridge", Path(__file__).parents[1] / "src/Stemora.Core/Data/muscriptor_bridge.py"
)
bridge = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bridge)


class BridgeTests(unittest.TestCase):
    def test_pinned_public_event_adapter(self):
        self.assertEqual(importlib.metadata.version("muscriptor"), "0.4.0")
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(model_dir=directory, input="input.wav", output=str(Path(directory) / "notes.json"), device="cpu")
            start = NoteStartEvent(pitch=69, start_time=0.25, index=3, instrument="clarinet")
            events = [ProgressEvent(0, 2), start, ProgressEvent(1, 2), NoteEndEvent(end_time=1.25, start_event=start), ProgressEvent(2, 2)]
            class Model:
                def transcribe(inner, audio, **kwargs):
                    self.assertEqual(audio, "input.wav")
                    self.assertEqual(kwargs, dict(instruments=["clarinet"], batch_size=1))
                    return iter(events)
            output = io.StringIO()
            with patch("muscriptor.TranscriptionModel.load_model", return_value=Model()) as load, contextlib.redirect_stdout(output):
                bridge.transcribe(args)
            load.assert_called_once_with(Path(directory) / "model.safetensors", device="cpu")
            self.assertEqual(json.loads(Path(args.output).read_text()), [dict(start=0.25, end=1.25, pitch=69)])
            self.assertEqual([item["fraction"] for item in map(json.loads, output.getvalue().splitlines()) if item["stage"] == "transcribe"], [0, 0.5, 1, 1])

    def test_download_pins_and_checks_hash(self):
        payload = b"test weights"
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(model_dir=directory, variant="small", revision="a" * 40, size=len(payload), sha256=hashlib.sha256(payload).hexdigest())
            def fetch(repository, name, **kwargs):
                self.assertEqual(repository, "MuScriptor/muscriptor-small")
                self.assertEqual(kwargs["revision"], args.revision)
                (Path(directory) / name).write_bytes(payload if name.endswith("safetensors") else b"{}")
            with patch("huggingface_hub.hf_hub_download", side_effect=fetch), contextlib.redirect_stdout(io.StringIO()):
                bridge.download(args)
            self.assertTrue((Path(directory) / ("verified-" + args.revision)).exists())

    def test_bad_weights_never_get_verified(self):
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(model_dir=directory, variant="small", revision="b" * 40, size=3, sha256="0" * 64)
            def fetch(repository, name, **kwargs):
                (Path(directory) / name).write_bytes(b"bad")
            with patch("huggingface_hub.hf_hub_download", side_effect=fetch), contextlib.redirect_stdout(io.StringIO()), self.assertRaises(ValueError):
                bridge.download(args)
            self.assertFalse((Path(directory) / "model.safetensors").exists())
            self.assertFalse((Path(directory) / ("verified-" + args.revision)).exists())

    def test_unavailable_explicit_cuda_is_an_error(self):
        args = SimpleNamespace(device="cuda")
        with patch("torch.cuda.is_available", return_value=False), self.assertRaisesRegex(RuntimeError, "CUDA is unavailable"):
            bridge.transcribe(args)


if __name__ == "__main__":
    unittest.main()
