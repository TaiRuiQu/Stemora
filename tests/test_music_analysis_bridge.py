"""Offline checks for beat phase, class order, and local checkpoint contracts."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

import numpy as np

SPEC = importlib.util.spec_from_file_location(
    "music_bridge", Path(__file__).parents[1] / "src/Stemora.Core/Data/muscriptor_bridge.py"
)
bridge = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bridge)


class MusicAnalysisTests(unittest.TestCase):
    def test_grid_fits_whole_song_without_frame_rounding_drift(self):
        period = 60 / 123.4
        times = np.round((0.17 + np.arange(800) * period) * 100) / 100
        beats = np.column_stack((times, np.arange(800) % 4 + 1))
        bpm, origin = bridge.beat_grid(beats)
        self.assertAlmostEqual(bpm, 123.4, places=3)
        self.assertAlmostEqual(origin, 0.17, places=3)

    def test_crop_starting_mid_bar_keeps_downbeat_phase(self):
        beats = np.column_stack((0.1 + np.arange(20) * 0.5, (np.arange(20) + 2) % 4 + 1))
        bpm, origin = bridge.beat_grid(beats)
        self.assertAlmostEqual(bpm, 120)
        self.assertAlmostEqual(origin, 1.1)

    def test_missing_or_invalid_beats_do_not_fabricate_a_tempo(self):
        for beats in ([], [[0, 1]], [[0, 1]] * 4, [[float("nan"), 1]] * 4):
            self.assertEqual(bridge.beat_grid(beats), (None, None))

    def test_preparation_downloads_only_the_two_verified_families(self):
        with tempfile.TemporaryDirectory() as directory:
            state = lambda family, **kwargs: {family: {"complete": True, "files": [{"path": Path(directory) / family}]}}
            with patch.dict("os.environ"), patch("madmom_infer.models.download_checkpoint") as download, patch("madmom_infer.models.cache_info", side_effect=state), contextlib.redirect_stdout(io.StringIO()):
                bridge.prepare_analysis(SimpleNamespace(analysis_dir=directory))
                self.assertEqual([call.args[0] for call in download.call_args_list], list(bridge.ANALYSIS_FAMILIES))
                self.assertTrue(all(call.kwargs["cache_root"] == Path(directory) / "madmom_infer/models" for call in download.call_args_list))

    def test_inference_rejects_missing_cache_without_downloading(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict("os.environ"), patch("madmom_infer.models.download_checkpoint") as download:
            with self.assertRaisesRegex(RuntimeError, "missing or corrupt"):
                bridge.analysis_files(directory)
            download.assert_not_called()

    def test_neural_class_order_and_silence_use_the_real_adapter_contract(self):
        from madmom_infer.features.key import KEY_LABELS
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(analysis_dir=directory, input="local.wav", output=str(Path(directory) / "result.json"), device="cpu")
            audio = np.ones((4 * 44100, 1), dtype=np.float32) * 0.1
            beats = np.column_stack((0.1 + np.arange(8) * 0.5, np.arange(8) % 4 + 1))
            with patch.object(bridge, "analysis_files"), patch("soundfile.read", return_value=(audio, 44100)) as read, patch("madmom_infer.features.downbeats.RNNDownBeatProcessor") as beat_model, patch("madmom_infer.features.downbeats.DBNDownBeatTrackingProcessor") as decoder, patch("madmom_infer.features.key.CNNKeyRecognitionProcessor") as key_model, contextlib.redirect_stdout(io.StringIO()):
                decoder.return_value.return_value = beats
                names = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"]
                aliases = {"Db": "C#", "D#": "Eb", "G#": "Ab"}
                for key_class, expected in enumerate(KEY_LABELS):
                    prediction = np.zeros((1, 24)); prediction[0, key_class] = 1
                    key_model.return_value.return_value = prediction
                    bridge.analyze(args)
                    result = json.loads(Path(args.output).read_text())
                    name, mode = expected.split()
                    self.assertEqual(names[result["key"]], aliases.get(name, name))
                    self.assertEqual(result["isMinor"], mode == "minor")
                beat_model.assert_called_with(backend="torch", device="cpu")
                key_model.assert_called_with(backend="torch", device="cpu")
                beat_model.reset_mock(); key_model.reset_mock()
                read.return_value = (np.zeros_like(audio), 44100)
                bridge.analyze(args)
                result = json.loads(Path(args.output).read_text())
                self.assertIsNone(result["bpm"]); self.assertIsNone(result["key"])
                beat_model.assert_not_called(); key_model.assert_not_called()


if __name__ == "__main__":
    unittest.main()
