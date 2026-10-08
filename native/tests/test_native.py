"""Meaningful migration, signature, scheduling, input release, and protocol checks."""
import os
os.environ["AMP_HEADLESS"] = "1"
import base64
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "native"))
import backend
from core.database import ScoreDB
from core.compiler import compile_score
from core.ir import from_storage
from core.profile import load_profiles
from core.practice import PracticeSession, build_practice_cues
from core.rhythm import normalize_durations
from core.rate_clock import RateClock

def eventually(condition, timeout=4):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if condition():
            return True
        time.sleep(.01)
    return condition()

class ServiceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.messages = []
        self.output_patch = patch.object(backend, "send", self.messages.append)
        self.output_patch.start()
        self.service = backend.Service(self.tmp.name, test_mode=True)
    def tearDown(self):
        self.service.close()
        self.output_patch.stop()
        self.tmp.cleanup()
    def test_fresh_install_starts_with_empty_local_library(self):
        self.assertEqual(self.service.request("list", {}), [])
        self.assertEqual(self.service.request("groups", {})[0]["name"], "默认分组")
    def create(self, text="1_ 2_ 3_ 0_"):
        return self.service.request("save", {"name": "测试乐谱", "bpm": 300, "text": text})["id"]
    def args(self, score_id, profile="delta_force_harmonica"):
        return {"score_id": score_id, "profile_id": profile, "bpm": 300, "humanize": False}
    def balanced(self):
        held = set()
        for device, key, action in self.service.driver.events:
            if action == "down": held.add((device, key))
            else: held.discard((device, key))
        self.assertEqual(held, set(), "keyboard/mouse left pressed")
    def test_crud_import_export_preserves_arbitrary_durations(self):
        notes = [{"notes": ["mid_1"], "dur": .375}, {"notes": ["high_2"], "dur": .6, "semitone": 1}]
        sid = self.service.request("save", {"name": "精确时值", "bpm": 120, "notes": notes})["id"]
        path = str(Path(self.tmp.name) / "export.json")
        self.service.request("export", {"id": sid, "path": path})
        result = self.service.request("import", {"paths": [path]})
        self.assertEqual(result["count"], 1)
        with ScoreDB(self.service.db_path) as db:
            for song in db.list_scores(): self.assertEqual(db.get_score(song["id"])["notes"], notes)
        self.service.request("delete", {"id": sid})
        self.assertEqual(len(self.service.request("list", {})), 1)
    def test_invalid_text_is_rejected_without_partial_save(self):
        with self.assertRaises(ValueError): self.service.request("save", {"name": "invalid", "text": "1 invalid-token 2", "bpm": 100})
        self.assertEqual(self.service.request("list", {}), [])
    def test_chinese_full_pinyin_and_initial_search_index(self):
        sid = self.service.request("save", {"name": "小星星", "bpm": 90, "text": "1 1 5 5"})["id"]
        song = next(s for s in self.service.request("list", {}) if s["id"] == sid)
        self.assertIn("小星星", song["search_key"])
        self.assertIn("xiaoxingxing", song["search_key"])
        self.assertIn("xxx", song["search_key"])
    def test_lite_restricts_profiles_and_rejects_other_instruments(self):
        hello = self.service.request("hello", {"consent": True})
        self.assertEqual([p["id"] for p in hello["profiles"]], ["delta_force_harmonica"])
        self.assertEqual(hello["active_profile"], "delta_force_harmonica")
        sid = self.create()
        with self.assertRaises(ValueError): self.service.prepare(self.args(sid, "default"))
        self.assertFalse((Path(self.tmp.name) / "profiles" / "default.yaml").exists())
    def test_delta_note_path_and_logs(self):
        sid = self.create()
        self.service.start(self.args(sid))
        self.assertTrue(eventually(lambda: self.service.state == "completed"))
        downs = [key for device, key, action in self.service.driver.events if device == "kb" and action == "down"]
        self.assertEqual(downs, ["Z", "X", "C"])
        self.balanced()
        logs = self.service.request("logs", {})
        self.assertTrue(logs)
        details = self.service.request("log_detail", {"file": logs[0]["file"]})
        self.assertIn("session_end", details["text"])
        path = str(Path(self.tmp.name) / "log.csv")
        self.service.request("log_export", {"file": logs[0]["file"], "path": path})
        self.assertTrue(Path(path).read_text(encoding="utf-8-sig").startswith("音符索引"))
    def test_delta_mouse_modifier_path_is_released(self):
        sid = self.create("1, 2' 3# 1'")
        self.service.start(self.args(sid, "delta_force_harmonica"))
        self.assertTrue(eventually(lambda: self.service.state == "completed"))
        downs = [(d, k) for d, k, a in self.service.driver.events if a == "down"]
        self.assertIn(("mouse", "left"), downs)
        self.assertIn(("mouse", "right"), downs)
        self.assertIn(("mouse", "middle"), downs)
        self.assertIn(("kb", ","), downs)
        self.balanced()
    def test_countdown_cancel_sends_nothing(self):
        sid = self.create()
        self.service.start(self.args(sid))
        self.service.stop()
        time.sleep(.08)
        self.assertFalse(self.service.driver.events)
        self.assertNotEqual(self.service.state, "playing")
    def test_pause_release_seek_resume(self):
        sid = self.create("1-- 2-- 3--")
        self.service.start(self.args(sid, "delta_force_harmonica"))
        self.assertTrue(eventually(lambda: any(e[2] == "down" for e in self.service.driver.events)))
        self.service.stop()
        self.assertTrue(eventually(lambda: not self.service.events.is_playing))
        self.assertEqual(self.service.state, "paused")
        self.balanced()
        self.service.request("seek", {"position": 2})
        self.service.start(self.args(sid, "delta_force_harmonica"))
        self.assertTrue(eventually(lambda: self.service.state == "completed"))
        self.assertEqual(self.service.position, 3)
        self.balanced()
    def test_npc_interrupt_clears_resume(self):
        sid = self.create("1-- 2--")
        args = self.args(sid, "delta_force_harmonica"); args["scenario"] = "npc_quest"
        self.service.start(args)
        self.assertTrue(eventually(lambda: self.service.state == "playing"))
        self.service.stop()
        self.assertTrue(eventually(lambda: not self.service.events.is_playing and self.service.state == "idle"))
        self.assertEqual(self.service.position, 0)
        self.assertEqual(self.service.state, "idle")
        self.balanced()
    def test_mini_seek_stops_active_input_and_updates_time(self):
        sid = self.create("1-- 2-- 3--")
        args = self.args(sid, "delta_force_harmonica")
        self.service.start(args)
        self.assertTrue(eventually(lambda: any(e[2] == "down" for e in self.service.driver.events)))
        result = self.service.request("seek_selected", {**args, "position": 2})
        self.assertEqual(result["position"], 2)
        self.assertEqual(result["state"], "paused")
        self.assertAlmostEqual(result["elapsed_seconds"], 1.6)
        self.assertAlmostEqual(result["total_seconds"], 2.4)
        self.assertFalse(self.service.events.is_playing)
        self.balanced()
    def test_import_precision_and_explicit_beat_text_roundtrip(self):
        notes = [{"notes": ["mid_3"], "dur": 1.25004}, {"notes": ["low_6"], "dur": .2499599999999999}, {"notes": ["high_1"], "dur": .5000399999999999}]
        cleaned = normalize_durations(notes, quantize=True)
        self.assertEqual([n["dur"] for n in cleaned], [1.25, .25, .5])
        text = " ".join(backend.Service._format(n) for n in cleaned)
        sid = self.service.request("save", {"name": "浮点归整", "bpm": 72, "text": text})["id"]
        self.assertEqual(self.service.request("get", {"id": sid})["notes"], cleaned)
    def test_external_import_quantization_does_not_accumulate_drift(self):
        notes = [{"notes": ["mid_1"], "dur": .24996} for _ in range(2000)]
        normalized = normalize_durations(notes, quantize=True)
        self.assertLessEqual(abs(sum(n["dur"] for n in normalized) - sum(n["dur"] for n in notes)), .0625)
        self.assertTrue(all(n["dur"] / .125 == int(n["dur"] / .125) for n in normalized))
    def test_global_speed_fractional_limit_and_persistence(self):
        sid = self.create("1-- 2--")
        self.service.request("set_speed", {"speed": 2.237})
        self.service.request("prepare", self.args(sid))
        self.assertAlmostEqual(self.service.status()["effective_bpm"], 671.1)
        with ScoreDB(self.service.db_path) as db: self.assertEqual(db.get_score(sid)["bpm_default"], 300)
        self.service._play()
        self.assertTrue(eventually(lambda: self.service.state == "completed"))
        self.balanced()
        for value in (0.49, 3.01, float("nan")):
            with self.assertRaises(ValueError): self.service.request("set_speed", {"speed": value})
        self.assertIn("playback_speed: 2.237", (Path(self.tmp.name) / "config.yaml").read_text(encoding="utf-8"))
    def test_rate_change_updates_active_preview_without_restarting(self):
        from unittest.mock import MagicMock
        sid = self.create("1--")
        self.service.request("set_speed", {"speed": .5})
        midi = MagicMock()
        with patch.object(self.service.preview, "_create_midi_output", return_value=midi):
            started = time.monotonic()
            self.service.request("preview", self.args(sid))
            self.assertTrue(eventually(lambda: midi.note_on.called))
            time.sleep(.06)
            self.service.request("set_speed", {"speed": 3})
            self.assertTrue(eventually(lambda: not self.service.preview.is_playing))
            self.assertLess(time.monotonic() - started, .9)
            midi.note_on.assert_called_once()
            self.assertFalse(self.service.driver.events)
    def test_continuous_clock_preserves_phase_when_rate_changes(self):
        values = {"wall": 10.0, "speed": 1.0}
        clock = RateClock(lambda: values["speed"], lambda: values["wall"])
        values["wall"] = 11; self.assertEqual(clock.now(), 11)
        values["speed"] = 3; self.assertEqual(clock.now(), 11)
        values["wall"] = 12; self.assertEqual(clock.now(), 14)
        values["speed"] = .5; self.assertEqual(clock.now(), 14)
        values["wall"] = 14; self.assertEqual(clock.now(), 15)
    def test_midi_roundtrip_chords_rest_accidental_and_base_tempo(self):
        import mido
        notes = [{"notes": ["mid_1", "mid_3", "mid_5"], "dur": .375}, {"notes": [], "dur": .25}, {"notes": ["low_2"], "semitone": 1, "dur": 1.25}, {"notes": [], "dur": .5}]
        sid = self.service.request("save", {"name": "MIDI 往返", "bpm": 72, "notes": notes})["id"]
        self.service.request("set_speed", {"speed": 3})
        path = str(Path(self.tmp.name) / "roundtrip.mid")
        self.service.request("export", {"id": sid, "path": path})
        midi = mido.MidiFile(path)
        self.assertEqual(midi.ticks_per_beat, 960)
        self.assertAlmostEqual(mido.tempo2bpm(next(m.tempo for m in midi.tracks[0] if m.type == "set_tempo")), 72, places=3)
        result = self.service.request("import", {"paths": [path]})
        self.assertEqual(result["count"], 1)
        with ScoreDB(self.service.db_path) as db:
            imported = next(s for s in db.list_scores() if s["id"] != sid)
            self.assertEqual(db.get_score(imported["id"])["notes"], notes)
    def test_midi_tempo_changes_and_sustain_are_timed_before_quantization(self):
        import mido
        from core.midi_io import read_midi
        file = mido.MidiFile(ticks_per_beat=480)
        track = mido.MidiTrack(); file.tracks.append(track)
        track.extend([mido.MetaMessage("set_tempo", tempo=500000), mido.Message("note_on", note=60, velocity=90),
                      mido.Message("control_change", control=64, value=127), mido.Message("note_off", note=60, time=240),
                      mido.MetaMessage("set_tempo", tempo=1000000, time=240), mido.Message("control_change", control=64, value=0, time=480),
                      mido.Message("note_on", channel=9, note=36, velocity=90), mido.Message("note_off", channel=9, note=36, time=240)])
        path = str(Path(self.tmp.name) / "tempo.midi"); file.save(path)
        result = read_midi(path)
        self.assertEqual(result.bpm, 120)
        self.assertEqual(result.notes[0]["dur"], 3)
        self.assertTrue(any("速度变化" in w for w in result.warnings))
        self.assertTrue(any("鼓轨" in w for w in result.warnings))
    def test_mixed_json_midi_batch_reports_bad_file_and_keeps_good_imports(self):
        sid = self.create()
        folder = Path(self.tmp.name) / "batch"; folder.mkdir()
        self.service.request("export", {"id": sid, "path": str(folder / "good.mid")})
        self.service.request("export", {"id": sid, "path": str(folder / "good.json")})
        (folder / "broken.midi").write_bytes(b"not-midi")
        result = self.service.request("import", {"folder": str(folder)})
        self.assertEqual(result["count"], 2)
        self.assertEqual(len(result["errors"]), 1)
    def test_guard_failure_releases_inputs(self):
        sid = self.create("2'-- 3'--")
        self.service.start(self.args(sid, "delta_force_harmonica"))
        self.assertTrue(eventually(lambda: any(e[2] == "down" for e in self.service.driver.events)))
        self.service.test_mode = False
        with patch.object(backend, "_get_fg_hwnd", return_value=None):
            self.assertTrue(eventually(lambda: not self.service.events.is_playing))
        self.balanced()
    def test_practice_never_dispatches(self):
        sid = self.create("1 2")
        args = self.args(sid); args["mode"] = "practice"
        self.service.start(args)
        self.service.practice_held = {"Z"}; self.service._practice_submit()
        self.assertEqual(self.service.position, 1)
        self.service.practice_held = {"X"}; self.service._practice_submit()
        self.assertEqual(self.service.state, "completed")
        self.assertEqual(self.service.driver.events, [])
    def test_ranges_preferences_and_validation(self):
        sid = self.create()
        args = self.args(sid); args.update(start=2, end=3, transpose=2)
        self.assertEqual(self.service.prepare(args)["count"], 2)
        self.assertEqual(self.service.notes[0]["notes"], ["mid_3"])
        prefs = self.service.request("get", {"id": sid, "profile_id": "delta_force_harmonica"})["preferences"]
        self.assertEqual(prefs["transpose"], 2)
        with self.assertRaises(ValueError): self.service.prepare({**args, "start": 100})
    def test_database_import_is_readonly_and_backup_exists(self):
        old = Path(self.tmp.name) / "old.db"
        with ScoreDB(str(old)) as db: db.add_score("旧曲库", [{"notes": ["mid_1"], "dur": 1}], bpm_default=100)
        original = old.read_bytes()
        result = self.service.request("import_database", {"path": str(old)})
        self.assertEqual(result["count"], 1)
        self.assertEqual(old.read_bytes(), original)
        self.assertTrue(list(Path(self.tmp.name).glob("scores.before-import-*.db")))
    def test_library_groups_multi_membership_favorites_and_delete_group(self):
        a = self.create()
        b = self.create()
        g = self.service.request("group_save", {"name": "练习曲"})["id"]
        h = self.service.request("group_save", {"name": "常用"})["id"]
        self.service.request("library_batch", {"ids": [a, b], "group_ids": [g, h], "favorite": True})
        songs = self.service.request("list", {})
        self.assertTrue(all(s["favorite"] == 1 and set(s["group_ids"]) == {1, g, h} for s in songs))
        self.service.request("library_batch", {"ids": [a], "group_ids": [g], "remove": True})
        self.service.request("group_save", {"id": g, "name": "练习"})
        self.service.request("group_delete", {"id": h})
        songs = self.service.request("list", {})
        self.assertEqual(len(songs), 2)
        self.assertEqual(next(s["group_ids"] for s in songs if s["id"] == a), [1])
        self.assertEqual(set(next(s["group_ids"] for s in songs if s["id"] == b)), {1, g})
        with self.assertRaises(ValueError): self.service.request("group_delete", {"id": 1})
        with self.assertRaises(ValueError): self.service.request("group_save", {"name": "练习"})
        self.service.request("library_batch", {"ids": [a], "group_ids": [g], "replace_groups": True, "favorite": False})
        song = next(s for s in self.service.request("list", {}) if s["id"] == a)
        self.assertEqual(song["group_ids"], [g]); self.assertEqual(song["favorite"], 0)
    def test_import_assigns_multiple_groups_atomically(self):
        sid = self.create()
        path = str(Path(self.tmp.name) / "group-import.json")
        self.service.request("export", {"id": sid, "path": path})
        g = self.service.request("group_save", {"name": "导入组"})["id"]
        self.service.request("import", {"paths": [path], "group_ids": [1, g], "favorite": True})
        songs = self.service.request("list", {})
        imported = next(s for s in songs if s["id"] != sid)
        self.assertEqual(set(imported["group_ids"]), {1, g}); self.assertEqual(imported["favorite"], 1)
        with self.assertRaises(ValueError): self.service.request("import", {"paths": [path], "group_ids": [999]})
        self.assertEqual(len(self.service.request("list", {})), 2)
    def test_updated_time_tracks_content_not_group_or_favorite(self):
        sid = self.create()
        original = self.service.request("get", {"id": sid})
        self.service.request("library_batch", {"ids": [sid], "favorite": True})
        self.assertEqual(self.service.request("get", {"id": sid})["updated_at"], original["updated_at"])
        time.sleep(.01)
        self.service.request("save", {"id": sid, "name": "修改曲名", "text": "5 6 7", "bpm": 120})
        changed = self.service.request("get", {"id": sid})
        self.assertEqual(changed["created_at"], original["created_at"])
        self.assertGreater(changed["updated_at"], original["updated_at"])
        self.assertEqual(changed["favorite"], 1)
    def test_chinese_pinyin_sort_and_letter_index(self):
        names = ["自检", "波浪", "《爱你》", "123", "Éclair", "阿尔法"]
        for name in names: self.service.request("save", {"name": name, "text": "1", "bpm": 100})
        songs = sorted(self.service.request("list", {}), key=lambda s: s["name_sort_key"])
        self.assertEqual([s["name_initial"] for s in songs], ["A", "A", "B", "E", "Z", "#"])
        self.assertEqual(songs[0]["name"], "阿尔法")
    def test_schema_v2_migration_preserves_preferences_and_scores(self):
        import sqlite3
        target = Path(self.tmp.name) / "migration-v2.db"
        old = sqlite3.connect(target)
        old.execute("CREATE TABLE scores(id INTEGER PRIMARY KEY AUTOINCREMENT,name TEXT NOT NULL,source_file TEXT,source_type TEXT,raw_text TEXT,notes_json TEXT NOT NULL,bpm_default INTEGER NOT NULL DEFAULT 100,created_at TEXT NOT NULL)")
        old.execute("INSERT INTO scores VALUES(42,'旧曲目','','import','',?,100,'2026-01-01T00:00:00')", (json.dumps([{"notes":["mid_1"],"dur":1}]),))
        old.execute("CREATE TABLE score_preferences(score_id INTEGER,profile_id TEXT,settings_json TEXT,updated_at TEXT,PRIMARY KEY(score_id,profile_id))")
        old.execute("INSERT INTO score_preferences VALUES(42,'delta_force_harmonica','{\"transpose\":2}','old')")
        old.execute("PRAGMA user_version=2"); old.commit(); old.close()
        with ScoreDB(str(target)) as db:
            self.assertEqual(db.get_score(42)["updated_at"], "2026-01-01T00:00:00")
            self.assertEqual(db.list_scores()[0]["group_ids"], [1])
            self.assertEqual(db.get_score_preferences(42, "delta_force_harmonica"), {"transpose": 2})
            self.assertTrue(Path(db.pre_migration_backup_path).exists())

if __name__ == "__main__":
    unittest.main(verbosity=2)
