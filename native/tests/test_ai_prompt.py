"""The original transcription prompt is available offline and uses valid editor notation."""
import os
os.environ["AMP_HEADLESS"] = "1"
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT/'native'))
import backend
from core.prompt import JIANPU_PROMPT

class AiPromptTests(unittest.TestCase):
    def test_offline_prompt_rpc_and_examples_roundtrip_through_editor(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(backend, "send"):
            service = backend.Service(directory, test_mode=True)
            try:
                result = service.request("ai_prompt", {})
                self.assertEqual(result["text"], JIANPU_PROMPT)
                self.assertEqual(service.request("list", {}), [])
                for text in ["1 1 5 5 6 6 5- 4 4 3 3 2 2 1-", "5' 5, 1'' 5'# 5_· 3{1.25} [1' 3' 5']- 0"]:
                    item = service.request("save", {"name": "AI识谱示例", "bpm": 100, "text": text})
                    score = service.request("get", {"id": item["id"]})
                    self.assertTrue(score["notes"])
                    if "1''" in text:
                        self.assertEqual(score["notes"][2]["notes"], ["top_1"])
                        self.assertEqual(score["notes"][5]["dur"], 1.25)
                self.assertFalse(service.driver.events)
            finally:
                service.close()
