"""Archive roundtrip, replacement rollback, import and octave regression tests."""
import os
os.environ["AMP_HEADLESS"] = "1"
from pathlib import Path
import json
import shutil
import sqlite3
import sys
import tempfile
import unittest
import zipfile
from unittest.mock import patch
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT)); sys.path.insert(0, str(ROOT / "native"))
import backend
from core.archive_io import export_zip, restore_backup, read_score_zip
from core.database import ScoreDB
from core.score_io import import_external_events, note_id_to_midi, midi_to_note_id
from core.midi_io import write_midi, read_midi
from core.transport import prepare_score
from core.ir import from_storage
from core.compiler import compile_score
from core.profile import load_profiles

class Archives(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.data = Path(self.temp.name)
        self.output = patch.object(backend, "send"); self.output.start()
        self.service = backend.Service(str(self.data), test_mode=True)
        self.db = self.service.db_path
        with ScoreDB(self.db) as db:
            self.group = db.save_group("练习")
            self.id = db.add_score("曲目 / 重名", [{"notes": ["low_6"], "dur": .25}, {"notes": ["mid_3"], "dur": 1.25}], "6, 3", bpm_default=72, group_ids=[1, self.group], favorite=True)
            db.save_score_preferences(self.id, "delta_force_harmonica", {"transpose": -12, "bpm": 100})
        (self.data / "ui-settings.json").write_text(json.dumps({"theme": 2, "acceptedRisk": True, "preferAdministrator": True, "suppressAdminReminder": True, "librarySort": 5}), encoding="utf-8")
    def tearDown(self):
        self.service.close(); self.output.stop(); self.temp.cleanup()
    def backup(self):
        path = self.data / "backup.zip"
        export_zip(self.db, path, data_dir=self.data, backup=True)
        return path
    def test_batch_zip_only_scores_and_duplicate_safe_names(self):
        with ScoreDB(self.db) as db:
            other = db.add_score("曲目 / 重名", [{"notes": ["mid_1"], "dur": 1}], bpm_default=100)
        path = self.data / "scores.zip"
        export_zip(self.db, path, [self.id, other])
        with zipfile.ZipFile(path) as archive:
            self.assertEqual(len(archive.namelist()), 2)
            self.assertTrue(all(n.startswith("scores/") and n.endswith(".json") for n in archive.namelist()))
        self.assertEqual(len(read_score_zip(path)), 2)
        result = self.service.request("import", {"paths": [str(path)], "group_ids": [self.group]})
        self.assertEqual(result["count"], 2); self.assertEqual(result["errors"], [])
    def test_export_failure_preserves_existing_file(self):
        path = self.data / "scores.zip"; path.write_bytes(b"existing")
        with patch("core.archive_io._score_document", side_effect=ValueError("bad score")):
            with self.assertRaises(ValueError): export_zip(self.db, path, [self.id])
        self.assertEqual(path.read_bytes(), b"existing")
        self.assertEqual(list(self.data.glob(".amp-export-*")), [])
    def test_missing_selection_does_not_write(self):
        path = self.data / "missing.zip"
        with self.assertRaises(ValueError): export_zip(self.db, path, [self.id + 100])
        self.assertFalse(path.exists())
    def test_backup_restores_groups_favorites_settings_and_preferences(self):
        self.service.request("set_speed", {"speed": 2.237})
        backup = self.backup()
        with ScoreDB(self.db) as db:
            original = db.get_score(self.id)
            db.delete_score(self.id); db.delete_group(self.group)
        self.service.request("set_speed", {"speed": .5})
        (self.data / "ui-settings.json").write_text('{"theme":0}', encoding="utf-8")
        result = self.service.request("restore_backup", {"path": str(backup), "confirmed": True})
        with ScoreDB(self.db) as db:
            self.assertEqual(db.get_score(self.id)["notes"], original["notes"])
            self.assertEqual(db.get_score(self.id)["created_at"], original["created_at"])
            self.assertEqual(db.list_scores()[0]["group_ids"], [1, self.group])
            self.assertTrue(db.get_score(self.id)["favorite"])
            self.assertEqual(db.get_score_preferences(self.id, "delta_force_harmonica")["transpose"], -12)
        self.assertEqual(json.loads((self.data / "ui-settings.json").read_text())["theme"], 2)
        self.assertAlmostEqual(self.service.speed, 2.237)
        self.assertTrue(Path(result["safety_backup"]).exists())
        self.service.request("set_speed", {"speed": 1.25})
        self.assertEqual(self.service.cfg["player"]["playback_speed"], 1.25)
    def test_restore_requires_confirmation_and_idle(self):
        path = self.backup()
        with self.assertRaises(ValueError): self.service.request("restore_backup", {"path": str(path)})
        self.service.state = "playing"
        with self.assertRaises(ValueError): self.service.request("restore_backup", {"path": str(path), "confirmed": True})
        self.service.state = "idle"
    def test_backup_cannot_be_imported_as_score_zip(self):
        with self.assertRaises(ValueError): read_score_zip(self.backup())
    def test_empty_library_backup_can_be_restored(self):
        with ScoreDB(self.db) as db: db.delete_score(self.id)
        path = self.backup()
        result = restore_backup(self.db, self.data, path)
        self.assertEqual(result["count"], 0)
        with ScoreDB(self.db) as db: self.assertEqual(db.list_scores(), [])
    def test_corrupt_backup_rejected_before_any_change(self):
        good = self.backup(); bad = self.data / "corrupt.zip"
        with zipfile.ZipFile(good) as source, zipfile.ZipFile(bad, "w") as target:
            for entry in source.infolist():
                contents = source.read(entry)
                if entry.filename.startswith("scores/"):
                    score = json.loads(contents); score["notes"][0]["notes"] = ["mid_9"]; contents = json.dumps(score)
                target.writestr(entry.filename, contents)
        before = (self.data / "ui-settings.json").read_bytes()
        with self.assertRaises(ValueError): restore_backup(self.db, self.data, bad)
        with ScoreDB(self.db) as db: self.assertIsNotNone(db.get_score(self.id))
        self.assertEqual((self.data / "ui-settings.json").read_bytes(), before)
        self.assertFalse((self.data / "backups").exists())
    def test_traversal_and_duplicate_members_rejected(self):
        path = self.data / "bad.zip"
        with zipfile.ZipFile(path, "w") as archive: archive.writestr("../config.yaml", "bad")
        with self.assertRaises(ValueError): restore_backup(self.db, self.data, path)
        with ScoreDB(self.db) as db: self.assertIsNotNone(db.get_score(self.id))
    def test_sql_failure_rolls_back_original_scores_and_settings(self):
        path = self.backup()
        before = (self.data / "ui-settings.json").read_bytes()
        with ScoreDB(self.db) as db:
            db.conn.execute("CREATE TRIGGER reject_restore BEFORE INSERT ON scores BEGIN SELECT RAISE(ABORT,'test rejection'); END")
            db.conn.commit()
        with self.assertRaises(sqlite3.IntegrityError): restore_backup(self.db, self.data, path)
        with ScoreDB(self.db) as db: self.assertIsNotNone(db.get_score(self.id))
        self.assertEqual((self.data / "ui-settings.json").read_bytes(), before)
    def test_settings_write_failure_rolls_back_database_and_files(self):
        path = self.backup()
        (self.data / "ui-settings.json").write_bytes(b'{"theme":0}')
        with ScoreDB(self.db) as db: db.update_score(self.id, "changed", [{"notes": ["high_1"], "dur": 1}], "1'", 100)
        import core.archive_io as io
        replace = io.os.replace; calls = []
        def fail_second(source, target):
            if str(target).endswith("ui-settings.json") and not calls:
                calls.append(target); raise OSError("injected write failure")
            return replace(source, target)
        with patch.object(io.os, "replace", side_effect=fail_second):
            with self.assertRaises(OSError): restore_backup(self.db, self.data, path)
        with ScoreDB(self.db) as db: self.assertEqual(db.get_score(self.id)["name"], "changed")
        self.assertEqual((self.data / "ui-settings.json").read_bytes(), b'{"theme":0}')
    def test_octaves_preserved_through_events_midi_transport_and_input(self):
        expected = [f"{octave}_{degree}" for octave in ("low", "mid", "high") for degree in range(1,8)]
        events = [{"t": i*.5, "d": .5, "note": sign+str(degree)} for i,(sign,degree) in enumerate((s,n) for s in ("-", "", "+") for n in range(1,8))]
        imported = import_external_events({"title": "octaves", "bpm": 120, "events": events})
        self.assertEqual([n["notes"][0] for n in imported.notes], expected)
        path = self.data / "octaves.mid"
        write_midi(str(path), {"name": "octaves", "bpm_default": 120, "notes": imported.notes})
        self.assertEqual([n["notes"][0] for n in read_midi(str(path)).notes], expected)
        prepared = prepare_score(imported.notes, transpose=0, bpm=120)
        self.assertEqual([n["notes"][0] for n in prepared.notes], expected)
        for note in expected: self.assertEqual(midi_to_note_id(note_id_to_midi(note)), note)
        profile = self.service.profiles[0]
        params = profile.build_compile_params(bpm=120, settle_ms=30, release_settle_ms=20, hold_ratio=.75, max_hold_ms=None, gap_ms=0)
        compiled = compile_score(from_storage(prepared.notes), params)
        self.assertTrue(compiled.events)
        held = set(); sounded = []
        for event in compiled.events:
            if event.device == "mouse":
                if event.action == "down": held.add(event.key)
                else: held.discard(event.key)
            elif event.action == "down":
                index = event.source_index; sounded.append(index)
                if index < 7: self.assertEqual(held, {"left"})
                elif index < 14: self.assertEqual(held, set())
                elif index == 14: self.assertEqual(event.key, ","); self.assertEqual(held, set())
                else: self.assertEqual(held, {"right"})
        self.assertEqual(sounded, list(range(21)))
        self.assertEqual(held, set())

if __name__ == "__main__": unittest.main()
