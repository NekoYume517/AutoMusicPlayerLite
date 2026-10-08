"""Audition stop must update transport before asynchronous MIDI cleanup finishes."""
import os
os.environ["AMP_HEADLESS"] = "1"
import sys
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import MagicMock, patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "native"))
import backend

class PreviewTransportTests(unittest.TestCase):
    def test_stop_is_immediate_and_restart_waits_for_old_audio_cleanup(self):
        closing, release = threading.Event(), threading.Event()
        midi = MagicMock()
        def close():
            closing.set()
            release.wait(2)
        midi.close.side_effect = close
        with tempfile.TemporaryDirectory() as directory, patch.object(backend, "send"), patch("sys.platform", "win32"):
            service = backend.Service(directory, test_mode=True)
            try:
                song = service.request("save", {"name": "试听停止", "bpm": 60, "text": "1--"})
                args = {"score_id": song["id"], "bpm": 60, "humanize": False}
                with patch.object(service.preview, "_create_midi_output", return_value=midi):
                    service.request("preview", args)
                    deadline = time.monotonic() + 2
                    while not midi.note_on.called and time.monotonic() < deadline:
                        time.sleep(.01)
                    self.assertTrue(midi.note_on.called)
                    stopped = service.request("preview", args)
                    self.assertTrue(closing.wait(1))
                    self.assertTrue(service.preview.is_playing, "old MIDI cleanup should still be pending")
                    self.assertEqual(stopped["state"], "idle")
                    self.assertEqual(stopped["message"], "试听已停止")
                    release.set()
                    service.request("preview", args)
                    self.assertEqual(service.status()["state"], "preview")
                    self.assertEqual(service.request("stop", {})["state"], "idle")
                    self.assertFalse(service.driver.events, "audition generated game input")
            finally:
                release.set()
                service.close()
