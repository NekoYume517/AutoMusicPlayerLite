"""Local JSON-lines worker. No web server, Qt dependency, or UI in this process."""
from __future__ import annotations
import os
os.environ["AMP_HEADLESS"] = "1"
import sys
from pathlib import Path
ROOT = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(ROOT))
import argparse
import csv
import gzip
import uuid
from zipfile import BadZipFile
import unicodedata
import ctypes
import json
import math
import shutil
import sqlite3
import threading
import time
import traceback
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace, asdict
from contextlib import closing
import yaml
from pypinyin import lazy_pinyin, Style
from core.database import ScoreDB
from core.keyboard_driver import KeyboardDriver
from core.keymap import KeyMap
from core.player import Player
from core.event_player import EventPlayer
from core.event_logger import EventLogger
from core.preview_player import PreviewPlayer
from core.profile import load_profiles, resolve_profile
from core.parser import parse_jianpu
from core.prompt import JIANPU_PROMPT
from core.practice import PracticeSession, build_practice_cues, format_score_note
from core.humanize import HumanizeParams, make_seed, plan_timings
from core.transport import prepare_score
from core.compiler import compile_score
from core.ir import from_storage
from core.scenario import get_scenario
from core.score_io import export_json, import_many
from core.score_model import require_valid
from core.archive_io import export_zip, restore_backup
from core.rhythm import normalize_durations
from core.midi_io import write_midi
from core.window_monitor import _get_fg_hwnd, _get_window_title
from core.windows_reliability import inspect_target_elevation

VERSION = "2.2.6"
_write_lock = threading.RLock()

def send(value):
    with _write_lock:
        try:
            sys.stdout.write(json.dumps(value, ensure_ascii=False, allow_nan=False) + "\n")
            sys.stdout.flush()
        except (BrokenPipeError, OSError):
            pass

class RecordingDriver(KeyboardDriver):
    """Explicitly opt-in test driver; never generates OS input."""
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.events = []
    def _send(self, key, up):
        self.events.append(("kb", key, "up" if up else "down"))
    def _send_mouse(self, button, up):
        self.events.append(("mouse", button, "up" if up else "down"))

class Service:
    def __init__(self, data_dir, legacy_dir=None, test_mode=False):
        self.data = Path(data_dir).resolve()
        self.data.mkdir(parents=True, exist_ok=True)
        self.db_path = str(self.data / "scores.db")
        self.migration = ""
        if legacy_dir and not Path(self.db_path).exists():
            self._migrate(Path(legacy_dir).resolve())
        cfg_path = self.data / "config.yaml"
        if not cfg_path.exists():
            shutil.copyfile(ROOT / "config.yaml", cfg_path)
        profile_dir = self.data / "profiles"
        profile_dir.mkdir(exist_ok=True)
        if not (profile_dir / "delta_force_harmonica.yaml").exists():
            shutil.copyfile(ROOT / "profiles" / "delta_force_harmonica.yaml", profile_dir / "delta_force_harmonica.yaml")
        self.cfg = yaml.safe_load(cfg_path.read_text(encoding="utf-8"))
        self.profiles = [p for p in load_profiles(str(profile_dir), fallback_keymap=self.cfg["keymap"])
                         if p.id == "delta_force_harmonica"]
        if len(self.profiles) != 1:
            raise ValueError("Lite 版需要三角洲行动口琴档位")
        self.cfg.setdefault("app", {})["active_profile"] = "delta_force_harmonica"
        pc = self.cfg.get("player", {})
        self.speed = float(pc.get("playback_speed", 1))
        if not math.isfinite(self.speed) or not .5 <= self.speed <= 3:
            self.speed = 1.0
        driver_cls = RecordingDriver if test_mode else KeyboardDriver
        self.test_mode = test_mode
        self.driver = driver_cls(settle_ms=pc.get("modifier_settle_ms", 30), release_settle_ms=pc.get("modifier_release_ms", 20))
        self.logger = EventLogger(str(self.data / "play_logs"))
        self.player = Player(KeyMap(self.cfg["keymap"]), driver=self.driver, event_logger=self.logger)
        self.events = EventPlayer(self.driver, logger=self.logger)
        self.preview = PreviewPlayer()
        self.player.set_dispatch_guard(self.guard)
        self.events.set_dispatch_guard(self.guard)
        for engine in (self.player, self.events):
            engine.progress.connect(self.progress)
            engine.paused.connect(self.paused)
            engine.finished.connect(self.finished)
            engine.error_occurred.connect(self.error)
        self.events.aborted.connect(self.aborted)
        self.preview.finished.connect(self.preview_finished)
        self.preview.progress.connect(self.preview_progress)
        self.preview.error_occurred.connect(self.error)
        self.lock = threading.RLock()
        self.cancel = threading.Event()
        self.closing = threading.Event()
        self.state = "idle"
        self.message = "选择一首曲目开始"
        self.position = 0
        self.notes = []
        self.selected = None
        self.options = {}
        self.target = None
        self.own_hwnds = set()
        self.practice = None
        self.practice_held = set()
        self.mouse_held = set()
        self.practice_latched = False
        self.practice_timer = None
        self.key_listener = None
        self.mouse_listener = None
        self.seed = None
        self.worker = None
        self.consent = False
        self._clean_rhythm()
        if not test_mode:
            self._listeners()
        threading.Thread(target=self._watch_focus, daemon=True).start()



    def _migrate(self, legacy):
        # Only migrate an explicitly chosen source; the installer never embeds user data.
        source = legacy / "data" if (legacy / "data").is_dir() else legacy
        if not (source / "scores.db").exists():
            return
        uri = (source / "scores.db").as_uri() + "?mode=ro"
        with closing(sqlite3.connect(uri, uri=True)) as old, closing(sqlite3.connect(self.db_path)) as new:
            old.backup(new)
        for name in ("play_logs", "sources"):
            if (source / name).is_dir():
                shutil.copytree(source / name, self.data / name, dirs_exist_ok=True)
            elif (source / name).is_file():
                shutil.copy2(source / name, self.data / name)
        for name in ("config.yaml", "profiles"):
            if (legacy / name).is_dir():
                shutil.copytree(legacy / name, self.data / name, dirs_exist_ok=True)
            elif (legacy / name).is_file():
                shutil.copy2(legacy / name, self.data / name)
        self.migration = str(source)
    def _clean_rhythm(self):
        marker = self.data / "rhythm-normalization-v1.json"
        if marker.exists():
            return
        changed = []
        with ScoreDB(self.db_path) as db:
            for record in db.list_scores():
                song = db.get_score(record["id"])
                notes = normalize_durations(song["notes"], quantize=False)
                if notes != song["notes"]:
                    changed.append((song, notes))
            if changed:
                db.backup_to(str(self.data / ("scores.before-rhythm-" + time.strftime("%Y%m%d-%H%M%S") + ".db")))
                for song, notes in changed:
                    db.update_score(song["id"], song["name"], notes, song["raw_text"], song["bpm_default"])
        marker.write_text(json.dumps({"version": 1, "changed_scores": len(changed), "quantum_beats": .125}), encoding="utf-8")

    def status(self):
        cue = self.practice.current if self.practice else None
        seconds_per_beat = 60 / (self.options.get("bpm", 100) * self.speed)
        return {"state": self.state, "message": self.message, "position": self.position,
                "speed": self.speed, "effective_bpm": self.options.get("bpm", 100) * self.speed,
                "elapsed_seconds": sum(n["dur"] for n in self.notes[:self.position]) * seconds_per_beat,
                "total_seconds": sum(n["dur"] for n in self.notes) * seconds_per_beat,
                "total": len(self.notes), "score_name": self.selected["name"] if self.selected else "",
                "target": self.target["title"] if self.target else "",
                "score_hint": cue.score_text if cue else "",
                "input_hint": cue.input_text if cue else "",
                "hits": self.practice.hits if self.practice else 0,
                "misses": self.practice.misses if self.practice else 0}

    def notify(self):
        send({"event": "status", "data": self.status()})

    def progress(self, done, total):
        self.position = done
        self.notify()
    def paused(self, done, total):
        self.position = done
        self.state = "paused"
        self.message = "已暂停，可继续演奏"
        self.notify()
    def finished(self, normal):
        self.state = "completed" if normal else "paused"
        if normal:
            self.position = len(self.notes)
            self.message = "演奏完成"
        self.notify()
    def aborted(self, reason):
        self.position = 0
        self.state = "idle"
        self.message = "任务中止，请重新听 NPC 示范后再演奏"
        self.notify()
    def error(self, message):
        self.message = str(message)
        send({"event": "error", "data": str(message)})
    def preview_finished(self, normal):
        if self.state == "preview":
            self.state = "idle"
            self.message = "试听结束" if normal else "试听已停止"
            self.notify()
    def preview_progress(self, done, total):
        if self.state == "preview":
            self.position = done
            self.notify()

    def guard(self):
        if self.cancel.is_set() or self.closing.is_set():
            return False, "演奏已停止"
        if self.test_mode:
            return True, ""
        hwnd = _get_fg_hwnd()
        if not self.target or not hwnd or hwnd != self.target["hwnd"]:
            return False, "目标窗口失去焦点，已暂停并释放按键"
        return True, ""

    def _watch_focus(self):
        while not self.closing.wait(0.1):
            if self.state == "playing" and not self.guard()[0]:
                self.player.stop()
                self.events.stop()
                self.message = "目标窗口失去焦点，已暂停并释放按键"

    def _listeners(self):
        from pynput import keyboard, mouse
        down = set()
        def on_press(key):
            if key in (keyboard.Key.f6, keyboard.Key.f8):
                if key not in down:
                    down.add(key)
                    if key == keyboard.Key.f8:
                        self.stop()
                    elif self.consent:
                        send({"event": "hotkey_play", "data": None})
                return
            text = getattr(key, "char", None)
            if text:
                self.practice_held.add(text.upper())
                if self.state == "practice" and not self.practice_latched:
                    if self.practice_timer:
                        self.practice_timer.cancel()
                    self.practice_timer = threading.Timer(0.035, self._practice_submit)
                    self.practice_timer.daemon = True
                    self.practice_timer.start()
        def on_release(key):
            down.discard(key)
            text = getattr(key, "char", None)
            if text:
                self.practice_held.discard(text.upper())
            if not self.practice_held:
                self.practice_latched = False
        def on_click(x, y, button, pressed):
            name = button.name
            if pressed:
                self.mouse_held.add(name)
            else:
                self.mouse_held.discard(name)
        self.key_listener = keyboard.Listener(on_press=on_press, on_release=on_release)
        self.mouse_listener = mouse.Listener(on_click=on_click)
        self.key_listener.start()
        self.mouse_listener.start()

    def _practice_submit(self):
        with self.lock:
            if self.state != "practice" or not self.practice or not self.practice_held:
                return
            result = self.practice.submit(self.practice_held, self.mouse_held)
            self.practice_latched = True
            self.position = result.index
            self.message = result.message
            if result.completed:
                self.state = "completed"
            self.notify()

    def stop(self):
        self.cancel.set()
        if self.state == "countdown":
            self.state = "paused"
            self.message = "倒计时已取消"
        self.player.stop()
        self.events.stop()
        if self.state == "practice":
            self.state = "paused"
            self.message = "练习已暂停"
        if self.state == "preview":
            self.preview.stop()
            self.state = "idle"
            self.message = "试听已停止"
        self.notify()
        return self.status()

    def _quiesce(self):
        self.stop()
        if self.worker and self.worker is not threading.current_thread():
            self.worker.join(2)
            if self.worker.is_alive():
                raise ValueError("上一轮演奏正在停止，请稍后再试")
        self.player.shutdown(2)
        self.events.shutdown(2)
        self.preview.shutdown(2)
        if self.player.is_playing or self.events.is_playing or self.preview.is_playing:
            raise ValueError("后台仍在停止，请稍后再试")

    def prepare(self, args):
        if self.state in ("playing", "countdown", "practice", "preview"):
            raise ValueError("请先暂停，再修改演奏设置")
        with ScoreDB(self.db_path) as db:
            score = db.get_score(int(args["score_id"]))
            if not score:
                raise ValueError("曲目已不存在")
            options = {"profile_id": str(args.get("profile_id", "delta_force_harmonica")),
                       "bpm": int(args.get("bpm", score["bpm_default"])),
                       "transpose": int(args.get("transpose", 0)),
                       "start": int(args.get("start", 1)) - 1,
                       "end": int(args.get("end") or len(score["notes"])),
                       "scenario": str(args.get("scenario", "free_play")),
                       "humanize": bool(args.get("humanize", True)),
                       "latency": float(args.get("latency", 0)),
                       "mode": str(args.get("mode", "play"))}
            if options["mode"] not in ("play", "practice"):
                raise ValueError("未知演奏模式")
            if not 0 <= options["latency"] <= 200:
                raise ValueError("延迟补偿必须在 0–200ms")
            profile = resolve_profile(self.profiles, options["profile_id"])
            if profile.id != options["profile_id"]:
                raise ValueError("未知游戏档位")
            get_scenario(options["scenario"])
            prepared = prepare_score(score["notes"], start_index=options["start"], end_index=options["end"],
                                     transpose=options["transpose"], bpm=options["bpm"])
            if not prepared.notes:
                raise ValueError("所选片段没有音符")
            if score["id"] != (self.selected or {}).get("id") or options != self.options:
                self.position = 0
                self.practice = None
                self.seed = make_seed()
            self.selected = score
            self.notes = prepared.notes
            self.options = options
            self.profile = profile
            db.save_score_preferences(score["id"], profile.id, options)
        self.state = "paused" if self.position else "idle"
        return {"count": len(self.notes), "duration": sum(n["dur"] for n in self.notes) * 60 / options["bpm"],
                "warnings": [str(d) for d in prepared.degradations]}

    def start(self, args):
        if not self.consent and not self.test_mode:
            raise ValueError("请先阅读并确认首次使用提示")
        with self.lock:
            if self.state in ("playing", "countdown", "practice"):
                return self.status()
            self._quiesce()
            self.prepare(args)
            if self.position >= len(self.notes):
                self.position = 0
                self.practice = None
                self.seed = make_seed()
            self.own_hwnds = {int(h) for h in args.get("own_hwnds", [])}
            self.cancel.clear()
            if self.options["mode"] == "practice":
                keymap = KeyMap(self.profile.legacy_keymap or self.cfg["keymap"])
                if self.practice is None:
                    cues = build_practice_cues(self.notes, keymap=keymap, profile=self.profile,
                                              use_event_path=self.profile.legacy_keymap is None,
                                              bpm=self.options["bpm"])
                    self.practice = PracticeSession(cues, self.position)
                self.state = "practice"
                self.position = self.practice.index
                self.message = "按提示练习；不会自动发送按键"
                self.notify()
                return self.status()
            self.state = "countdown"
            self.worker = threading.Thread(target=self._countdown, daemon=True)
            self.worker.start()
            return self.status()

    def _countdown(self):
        try:
            for value in range(3, 0, -1):
                self.message = f"{value} 秒后演奏，请切换到游戏窗口"
                self.notify()
                if self.cancel.wait(1 if not self.test_mode else 0.01):
                    return
            if not self.test_mode:
                hwnd = _get_fg_hwnd()
                if not hwnd or hwnd in self.own_hwnds:
                    raise ValueError("倒计时结束时仍在播放器窗口，已取消输入；请切换到游戏后再开始")
                report = inspect_target_elevation(hwnd)
                if getattr(report, "definite_mismatch", False) or getattr(report, "status", "") == "blocked":
                    raise ValueError("游戏以管理员运行，请用设置页的管理员启动按钮重新打开")
                self.target = {"hwnd": hwnd, "title": _get_window_title(hwnd)}
            if self.cancel.is_set():
                return
            self._play()
        except Exception as exc:
            self.state = "paused"
            self.error(str(exc))
            self.notify()

    def _play(self):
        if self.cancel.is_set():
            return
        opt = self.options
        start_speed = self.speed
        effective_bpm = opt["bpm"] * start_speed
        for engine in (self.player, self.events):
            engine.rate_provider = lambda initial=start_speed: self.speed / initial
        human = HumanizeParams() if opt["humanize"] and opt["scenario"] != "npc_quest" else None
        self.player.latency_compensation_ms = opt["latency"]
        self.events.latency_compensation_ms = opt["latency"]
        self.state = "playing"
        self.message = "正在演奏；F8 暂停并释放按键"
        if self.profile.legacy_keymap is not None:
            self.player._keymap = KeyMap(self.profile.legacy_keymap)
            self.player.play(self.notes, effective_bpm, start_index=self.position, score_name=self.selected["name"],
                             humanize_override=human, humanize_seed=self.seed, trace_context={"speed": start_speed, "base_bpm": opt["bpm"]})
        else:
            scenario = get_scenario(opt["scenario"])
            subset = self.notes[self.position:]
            params = self.profile.build_compile_params(bpm=effective_bpm, settle_ms=self.driver.settle_ms,
                        release_settle_ms=self.driver.release_settle_ms, hold_ratio=.75, max_hold_ms=None, gap_ms=20)
            params = scenario.apply_intervals(params)
            timings = None
            if human:
                params = replace(params, min_gap_ms=human.min_gap_ms)
                # Reuse the same complete seed plan on pause/resume.
                timings = plan_timings(self.notes, human, seed=self.seed)[self.position:]
            result = compile_score(from_storage(subset, semitones=[n.get("semitone", 0) for n in subset]),
                                   params, timings=timings, source_index_offset=self.position)
            plan = scenario.plan(result.events)
            self.events.play(plan.events, interrupt_mode=plan.interrupt_mode, score_name=self.selected["name"],
                             source_total=len(self.notes), source_start_index=self.position,
                             source_notes=self.notes, bpm=effective_bpm,
                             trace_context={"profile": self.profile.id, "scenario": scenario.id, "humanize_seed": self.seed,
                                            "speed": start_speed, "base_bpm": opt["bpm"], "degradations": [str(d) for d in result.degradations]})
            if result.degradations:
                send({"event": "notice", "data": f"此档位有 {len(result.degradations)} 项音符适配，详情见演奏记录"})
        self.notify()
        if self.cancel.is_set():
            self.player.stop(); self.events.stop()

    def request(self, method, args):
        if method == "ai_prompt":
            return {"text": JIANPU_PROMPT}
        if method == "hello":
            self.consent = bool(args.get("consent", False))
            return {"version": VERSION, "speed": self.speed, "data_dir": str(self.data), "is_admin": bool(ctypes.windll.shell32.IsUserAnAdmin()),
                    "profiles": [{"id": p.id, "name": p.name} for p in self.profiles], "migration": self.migration,
                    "active_profile": self.cfg.get("app", {}).get("active_profile", "default"),
                    "driver": "Python SendInput / scancode + pynput", "status": self.status()}
        if method == "set_speed":
            value = float(args["speed"])
            if not math.isfinite(value) or not .5 <= value <= 3:
                raise ValueError("全局倍速必须在 0.5–3.0 之间")
            with self.lock:
                self.speed = value
                self.cfg.setdefault("player", {})["playback_speed"] = value
                file = self.data / "config.yaml"
                temporary = self.data / "config.yaml.new"
                temporary.write_text(yaml.safe_dump(self.cfg, allow_unicode=True, sort_keys=False), encoding="utf-8")
                os.replace(temporary, file)
                if self.logger.is_active:
                    self.logger.log_control("speed_change", speed=value, effective_bpm=self.options.get("bpm", 100) * value)
                self.notify()
                return {"speed": value}
        if method == "list":
            with ScoreDB(self.db_path) as db:
                scores = db.list_scores()
                for song in scores:
                    spelling = "".join(lazy_pinyin(song["name"])).casefold()
                    spelling = "".join(c for c in unicodedata.normalize("NFKD", spelling) if not unicodedata.combining(c))
                    spelling = spelling.lstrip(" \t《》〈〉（）()[]【】·-_.\"\'")
                    initial = spelling[:1].upper()
                    song["name_initial"] = initial if initial in "ABCDEFGHIJKLMNOPQRSTUVWXYZ" and initial else "#"
                    song["name_sort_key"] = ("\uffff" if song["name_initial"] == "#" else "") + spelling
                    song["search_key"] = " ".join((song["name"], "".join(lazy_pinyin(song["name"])),
                        "".join(lazy_pinyin(song["name"], style=Style.FIRST_LETTER)))).casefold()
                return scores
        if method == "groups":
            with ScoreDB(self.db_path) as db: return db.list_groups()
        if method in ("group_save", "group_delete", "library_batch"):
            with self.lock, ScoreDB(self.db_path) as db:
                if method == "group_save": return {"id": db.save_group(args["name"], int(args.get("id", 0)))}
                if method == "group_delete":
                    db.delete_group(int(args["id"]))
                    return {"deleted": True}
                return {"count": db.library_batch(args["ids"], group_ids=args.get("group_ids", []),
                            remove=bool(args.get("remove", False)), favorite=args.get("favorite"), replace_groups=bool(args.get("replace_groups", False)))}
        if method == "get":
            with ScoreDB(self.db_path) as db:
                score = db.get_score(int(args["id"]))
                if not score:
                    raise ValueError("曲目已不存在")
                score["editor_text"] = " ".join(self._format(n) for n in score["notes"])
                score["preferences"] = db.get_score_preferences(score["id"], args.get("profile_id", "delta_force_harmonica"))
                return score
        if method in ("save", "delete"):
            with self.lock:
                if self.state in ("playing", "countdown", "practice", "preview"):
                    raise ValueError("请先暂停演奏或试听，再修改曲库")
                with ScoreDB(self.db_path) as db:
                    score_id = int(args.get("id", 0))
                    if method == "delete":
                        db.delete_score(score_id)
                    else:
                        name = str(args["name"]).strip()
                        if not name:
                            raise ValueError("曲名不能为空")
                        text = str(args.get("text", ""))
                        if "notes" in args:
                            notes = args["notes"]
                        else:
                            notes, errors = parse_jianpu(text, collect=True, strict_ai=True)
                            if errors:
                                raise ValueError("\n".join(str(e) for e in errors[:10]))
                        if not notes:
                            raise ValueError("简谱不能为空")
                        bpm = int(args["bpm"])
                        require_valid(notes, bpm=bpm)
                        if score_id:
                            db.update_score(score_id, name, notes, text, bpm)
                        else:
                            score_id = db.add_score(name, notes, text, source_type="manual", bpm_default=bpm, group_ids=args.get("group_ids", [1]), favorite=args.get("favorite", False))
                    if self.selected and self.selected["id"] == score_id:
                        self.selected = None
                        self.position = 0
                        self.notes = []
                    return {"id": score_id}
        if method == "import":
            paths = args.get("paths", [])
            if args.get("folder"):
                paths = sorted(str(p) for p in Path(args["folder"]).iterdir() if p.is_file() and p.suffix.lower() in (".json", ".mid", ".midi", ".zip"))
            if not paths:
                raise ValueError("没有找到 JSON、MIDI 或乐谱 ZIP 文件")
            records, errors, warnings = [], [], []
            for index, path in enumerate(paths):
                send({"event": "notice", "data": f"导入 {index + 1}/{len(paths)}：{Path(path).name}"})
                try:
                    for res in import_many(path):
                        notes = normalize_durations(res.notes, quantize=res.source_format != "native")
                        records.append({"name": res.name, "notes": notes, "bpm_default": res.bpm,
                                        "raw_text": res.raw_text, "source_type": "import", "source_file": path})
                        warnings.extend(res.warnings)
                except (ValueError, OSError, EOFError, BadZipFile) as exc:
                    errors.append(f"{Path(path).name}: {exc}")
            with ScoreDB(self.db_path) as db:
                db.add_scores(records, group_ids=args.get("group_ids", [1]), favorite=args.get("favorite", False))
            return {"count": len(records), "errors": errors, "warnings": warnings[:50]}
        if method in ("export_zip", "backup_zip"):
            with self.lock:
                return export_zip(self.db_path, args["path"], args.get("ids") if method == "export_zip" else None,
                                  data_dir=self.data, backup=method == "backup_zip")
        if method == "restore_backup":
            if args.get("confirmed") is not True:
                raise ValueError("请先确认恢复备份")
            with self.lock:
                if self.state in ("playing", "countdown", "practice", "preview"):
                    raise ValueError("请先停止演奏或试听，再恢复备份")
                self.stop()
                result = restore_backup(self.db_path, self.data, args["path"])
                self.cfg = yaml.safe_load((self.data / "config.yaml").read_text(encoding="utf-8"))
                self.cfg.setdefault("app", {})["active_profile"] = "delta_force_harmonica"
                self.profiles = [p for p in load_profiles(str(self.data / "profiles"), fallback_keymap=self.cfg["keymap"])
                                 if p.id == "delta_force_harmonica"]
                pc = self.cfg.get("player", {})
                self.speed = float(pc.get("playback_speed", 1))
                self.driver.settle_ms = float(pc.get("modifier_settle_ms", 30))
                self.driver.release_settle_ms = float(pc.get("modifier_release_ms", 20))
                self.selected = None; self.notes = []; self.position = 0; self.options = {}
                self.state = "idle"; self.message = "备份已恢复"

                self.notify()
                return result
        if method == "export":
            with ScoreDB(self.db_path) as db:
                score = db.get_score(int(args["id"]))
                if not score:
                    raise ValueError("曲目已不存在")
                if Path(args["path"]).suffix.lower() in (".mid", ".midi"):
                    write_midi(args["path"], score)
                else:
                    export_json(args["path"], score)
            return {"path": args["path"]}
        if method == "prepare":
            with self.lock:
                return self.prepare(args)
        if method == "start":
            return self.start(args)
        if method == "stop":
            return self.stop()
        if method == "reset":
            with self.lock:
                self._quiesce()
                self.practice = None
                self.position = 0
                self.state = "idle"
                self.message = "进度已重置"
                self.notify()
                return self.status()
        if method == "seek":
            with self.lock:
                if self.state in ("playing", "countdown", "practice", "preview"):
                    raise ValueError("请先暂停，再调整进度")
                self.position = max(0, min(int(args["position"]), len(self.notes)))
                self.practice = None
                self.notify()
                return self.status()
        if method == "seek_selected":
            with self.lock:
                self._quiesce()
                self.prepare(args)
                self.position = max(0, min(int(args["position"]), len(self.notes)))
                self.practice = None
                self.state = "paused"
                self.message = "已跳转，按 F6 继续演奏"
                self.notify()
                return self.status()
        if method == "preview":
            with self.lock:
                if self.state == "preview":
                    return self.stop()
                self._quiesce()
                self.prepare(args)
                self.state = "preview"
                self.position = 0
                self.message = "本地钢琴试听，不会向游戏发送按键"
                start_speed = self.speed
                self.preview.rate_provider = lambda: self.speed / start_speed
                self.preview.play(self.notes, bpm=self.options["bpm"] * start_speed, score_name=self.selected["name"])
                self.notify()
                return self.status()
        if method == "logs":
            result = []
            for file in sorted((self.data / "play_logs").glob("*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)[:200]:
                with file.open(encoding="utf-8") as stream:
                    first = json.loads(next(stream, "{}"))
                result.append({"file": file.name, "name": first.get("score_name", file.stem),
                               "time": first.get("started_at", ""), "bpm": first.get("bpm", 0)})
            return result
        if method in ("log_detail", "log_export"):
            file = (self.data / "play_logs" / Path(args["file"]).name)
            events = [json.loads(line) for line in file.read_text(encoding="utf-8").splitlines() if line.strip()]
            if method == "log_detail":
                return {"text": "\n".join(json.dumps(e, ensure_ascii=False) for e in events[-2000:]), "count": len(events)}
            output = Path(args["path"])
            if output.suffix.lower() == ".csv":
                with output.open("w", encoding="utf-8-sig", newline="") as stream:
                    writer = csv.writer(stream)
                    writer.writerow(["音符索引", "音符", "按键", "预期时间", "实际时间", "偏差ms", "成功"])
                    for e in events:
                        if e.get("type") == "note":
                            writer.writerow([e.get("index"), ",".join(e.get("notes", [])), ",".join(e.get("keys", [])),
                                             e.get("expected_time"), e.get("actual_time"), e.get("deviation_ms"), e.get("success")])
            else:
                shutil.copyfile(file, output)
            return {"path": str(output)}
        if method == "status":
            return self.status()
        if method == "import_database":
            source = Path(args["path"]).resolve()
            if source == Path(self.db_path):
                raise ValueError("不能把当前数据库导入自身")
            with closing(sqlite3.connect(source.as_uri() + "?mode=ro", uri=True)) as old:
                old.row_factory = sqlite3.Row
                records = [{"name": r["name"], "notes": normalize_durations(json.loads(r["notes_json"]), quantize=False), "bpm_default": r["bpm_default"],
                            "raw_text": r["raw_text"] or "", "source_type": "import", "source_file": str(source)}
                           for r in old.execute("SELECT * FROM scores")]
            with ScoreDB(self.db_path) as db:
                db.backup_to(str(self.data / ("scores.before-import-" + time.strftime("%Y%m%d-%H%M%S") + ".db")))
                db.add_scores(records, group_ids=args.get("group_ids", [1]), favorite=args.get("favorite", False))
            return {"count": len(records)}
        raise ValueError(f"未知命令: {method}")

    @staticmethod
    def _format(note):
        ids = note.get("notes", [])
        def pitch(n):
            octave, number = n.split("_")
            return number + ("''" if octave == "top" else "'" if octave == "high" else "," if octave == "low" else "") + ("#" if note.get("semitone") else "")
        token = "0" if not ids else pitch(ids[0]) if len(ids) == 1 else "[" + " ".join(map(pitch, ids)) + "]"
        # The existing text grammar accepts conventional durations only; JSON editing preserves arbitrary durations.
        durations = {1: "", .5: "_", .25: "__", .125: "___", 2: "-", 4: "--", 1.5: "·", .75: "_·", 3: "-·", 6: "--·"}
        if note["dur"] not in durations:
            return token + "{" + format(note["dur"], ".9f").rstrip("0").rstrip(".") + "}"
        return token + durations[note["dur"]]

    def close(self):
        self.closing.set()
        self.stop()
        if self.worker:
            self.worker.join(2)
        self.player.shutdown(2)
        self.events.shutdown(2)
        self.preview.shutdown(2)
        if self.practice_timer:
            self.practice_timer.cancel()
        for listener in (self.key_listener, self.mouse_listener):
            if listener:
                listener.stop()

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-dir", default=str(Path(os.environ.get("LOCALAPPDATA", Path.home())) / "AutoMusicPlayerLite"))
    parser.add_argument("--legacy-dir")
    parser.add_argument("--test-mode", action="store_true")
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)
        sys.stdin.reconfigure(encoding="utf-8")
    service = Service(args.data_dir, args.legacy_dir, args.test_mode)
    pool = ThreadPoolExecutor(max_workers=4, thread_name_prefix="rpc")
    def handle(request):
        try:
            result = service.request(request["method"], request.get("args") or {})
            send({"id": request["id"], "ok": True, "result": result})
        except Exception as exc:
            traceback.print_exc(file=sys.stderr)
            send({"id": request.get("id"), "ok": False, "error": str(exc)})
    try:
        for line in sys.stdin:
            try:
                request = json.loads(line)
                if request.get("method") == "shutdown":
                    break
                if request.get("method") == "stop":
                    handle(request)  # Stop is never queued behind network/file IO.
                else:
                    pool.submit(handle, request)
            except (ValueError, TypeError) as exc:
                send({"id": None, "ok": False, "error": str(exc)})
    finally:
        service.closing.set()
        service.close()
        pool.shutdown(wait=True, cancel_futures=True)

if __name__ == "__main__":
    main()
