"""Atomic local score ZIP export and portable library/settings backups."""
from contextlib import contextmanager
from datetime import datetime
from pathlib import Path, PurePosixPath
import json
import math
import os
import re
import tempfile
import zipfile
import yaml
from core.database import ScoreDB
from core.score_io import JSON_FORMAT, JSON_VERSION, ImportResult
from core.score_model import require_valid
from core.profile import load_profiles

MAX_ENTRIES = 10005
MAX_EXPANDED = 256 * 1024 * 1024
SETTING_FILES = {"settings/config.yaml": "config.yaml", "settings/ui-settings.json": "ui-settings.json", "settings/delta_force_harmonica.yaml": "profiles/delta_force_harmonica.yaml"}

def _json(value):
    return json.dumps(value, ensure_ascii=False, allow_nan=False, indent=2).encode("utf-8")

@contextmanager
def _atomic_zip(path):
    target = Path(path).resolve()
    with tempfile.NamedTemporaryFile(prefix=".amp-export-", suffix=".zip", dir=target.parent, delete=False) as file:
        temporary = Path(file.name)
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            yield archive
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)

def _score_document(score):
    require_valid(score["notes"], bpm=score["bpm_default"])
    return {"format": JSON_FORMAT, "version": JSON_VERSION, "name": score["name"], "bpm": score["bpm_default"], "notes": score["notes"]}

def export_zip(db_path, path, ids=None, *, data_dir=None, backup=False):
    manifest = {"format": "auto-music-player-backup", "version": 1, "created_at": datetime.now().isoformat(), "scores": [], "groups": [], "settings": []}
    with ScoreDB(db_path) as db:
        with db.conn:
            db.conn.execute("BEGIN")
            summaries = db.list_scores()
            wanted = sorted(set(int(i) for i in ids)) if ids is not None else [s["id"] for s in summaries]
            if not backup and not wanted: raise ValueError("请先选择要导出的曲目")
            known_ids = {s["id"] for s in summaries}
            if len(wanted) > 10000 or any(i not in known_ids for i in wanted): raise ValueError("选中的曲目已变化或超过 10000 首")
            scores = [db.get_score(i) for i in wanted]
            members = {s["id"]: s["group_ids"] for s in summaries}
            if backup: manifest["groups"] = db.list_groups()
            with _atomic_zip(path) as archive:
                for index, score in enumerate(scores, 1):
                    safe = re.sub(r'[\\/:*?"<>|\x00-\x1f]', "_", score["name"]).strip(" .")[:70] or "score"
                    name = f"scores/{index:05d}-{safe}.json"
                    archive.writestr(name, _json(_score_document(score)))
                    if backup:
                        preferences = [dict(r) for r in db.conn.execute("SELECT profile_id,settings_json FROM score_preferences WHERE score_id=?", (score["id"],))]
                        manifest["scores"].append({"file": name, "id": score["id"], "group_ids": members[score["id"]], "favorite": bool(score["favorite"]),
                            "created_at": score["created_at"], "updated_at": score["updated_at"], "source_type": score["source_type"], "raw_text": score["raw_text"] or "", "preferences": preferences})
                if backup:
                    for archived, relative in SETTING_FILES.items():
                        source = Path(data_dir) / relative
                        if source.is_file(): archive.writestr(archived, source.read_bytes()); manifest["settings"].append(archived)
                    archive.writestr("manifest.json", _json(manifest))
    return {"path": str(Path(path).resolve()), "count": len(scores), "settings": len(manifest["settings"]), "bytes": Path(path).stat().st_size}

def _validate_members(archive):
    entries = archive.infolist()
    if len(entries) > MAX_ENTRIES or sum(e.file_size for e in entries) > MAX_EXPANDED: raise ValueError("ZIP 内容超过支持的大小或数量")
    names = set()
    for entry in entries:
        name = entry.filename
        if name in names or name.startswith("/") or "\\" in name or ":" in name or ".." in PurePosixPath(name).parts or entry.flag_bits & 1:
            raise ValueError("ZIP 包含重复、加密或不安全的路径")
        names.add(name)
    return names

def _read_score(archive, name):
    if archive.getinfo(name).file_size > 10 * 1024 * 1024: raise ValueError("单首乐谱超过 10 MB")
    value = json.loads(archive.read(name).decode("utf-8-sig"))
    if not isinstance(value, dict) or value.get("format") != JSON_FORMAT or value.get("version") != JSON_VERSION: raise ValueError("ZIP 中有不支持的乐谱格式")
    if not isinstance(value.get("name"), str) or not value["name"].strip(): raise ValueError("乐谱名称为空")
    if "notes" not in value or "bpm" not in value: raise ValueError("乐谱缺少 notes/bpm")
    require_valid(value["notes"], bpm=value["bpm"])
    return value

def read_score_zip(path):
    with zipfile.ZipFile(path) as archive:
        names = _validate_members(archive)
        if "manifest.json" in names: raise ValueError("这是完整备份，请在设置中选择恢复备份")
        files = sorted(n for n in names if not n.endswith("/"))
        if not files or any(not n.endswith(".json") for n in files): raise ValueError("乐谱 ZIP 只支持 JSON 谱面")
        results = []
        for name in files:
            value = _read_score(archive, name)
            results.append(ImportResult(name=value["name"], bpm=value["bpm"], notes=value["notes"], kind="json"))
        return results

def _validate_settings(settings):
    if "settings/ui-settings.json" in settings:
        ui = json.loads(settings["settings/ui-settings.json"])
        if not isinstance(ui, dict): raise ValueError("界面设置格式错误")
        for name in ("acceptedRisk", "autoUpdate", "preferAdministrator", "suppressAdminReminder"):
            if name in ui and not isinstance(ui[name], bool): raise ValueError("界面设置中的开关格式错误")
    cfg = yaml.safe_load(settings.get("settings/config.yaml", b"{}"))
    if "settings/config.yaml" in settings:
        if not isinstance(cfg, dict) or not isinstance(cfg.get("keymap"), dict) or not isinstance(cfg.get("player", {}), dict): raise ValueError("播放器设置格式错误")
        for keys in (cfg["keymap"].get(k) for k in ("high", "mid", "low")):
            if not isinstance(keys, list) or len(keys) != 7 or any(not isinstance(k, str) for k in keys): raise ValueError("键位设置格式错误")
        rate = float(cfg.get("player", {}).get("playback_speed", 1))
        if not math.isfinite(rate) or not .5 <= rate <= 3: raise ValueError("备份倍速超出范围")
        for name in ("modifier_settle_ms", "modifier_release_ms"):
            value = float(cfg.get("player", {}).get(name, 30))
            if not math.isfinite(value) or value < 0: raise ValueError("修饰键延迟设置无效")
    if "settings/delta_force_harmonica.yaml" in settings:
        with tempfile.TemporaryDirectory() as temp:
            Path(temp, "delta_force_harmonica.yaml").write_bytes(settings["settings/delta_force_harmonica.yaml"])
            profiles = load_profiles(temp, fallback_keymap=cfg.get("keymap", {}))
            if [p.id for p in profiles if p.id != "default"] != ["delta_force_harmonica"]: raise ValueError("备份乐器档位不适用于本工具")

def restore_backup(db_path, data_dir, path):
    data = Path(data_dir)
    with zipfile.ZipFile(path) as archive:
        names = _validate_members(archive)
        if "manifest.json" not in names or archive.getinfo("manifest.json").file_size > 10 * 1024 * 1024: raise ValueError("这不是支持的完整备份")
        manifest = json.loads(archive.read("manifest.json"))
        if manifest.get("format") != "auto-music-player-backup" or manifest.get("version") != 1: raise ValueError("备份版本不支持")
        groups, records = manifest["groups"], manifest["scores"]
        group_ids = [g["id"] for g in groups]
        if len(groups) > 100 or len(set(group_ids)) != len(group_ids) or any(type(i) is not int or i < 1 for i in group_ids) or 1 not in group_ids: raise ValueError("备份分组格式错误")
        group_names = [g["name"].strip() for g in groups]
        if any(not n or len(n) > 40 for n in group_names) or len(set(n.casefold() for n in group_names)) != len(group_names): raise ValueError("备份分组名称无效")
        score_ids = [r["id"] for r in records]
        score_files = [r["file"] for r in records]
        if len(set(score_files)) != len(score_files): raise ValueError("备份重复引用同一个谱面文件")
        if len(records) > 10000 or len(set(score_ids)) != len(score_ids) or any(type(i) is not int or i < 1 for i in score_ids): raise ValueError("备份曲目编号无效")
        scores = []
        for record in records:
            if any(g not in group_ids for g in record["group_ids"]): raise ValueError("备份曲目引用了不存在的分组")
            for preference in record["preferences"]:
                if not isinstance(json.loads(preference["settings_json"]), dict): raise ValueError("曲目设置无效")
            scores.append(_read_score(archive, record["file"]))
        requested = manifest["settings"]
        if any(s not in SETTING_FILES for s in requested): raise ValueError("备份引用了不支持的设置文件")
        allowed = {"manifest.json", *(r["file"] for r in records), *requested}
        if names != allowed: raise ValueError("备份含有未声明的文件")
        settings = {s: archive.read(s) for s in requested}
        _validate_settings(settings)
    safety_dir = data / "backups"; safety_dir.mkdir(exist_ok=True)
    safety = safety_dir / ("backup-before-restore-" + datetime.now().strftime("%Y%m%d-%H%M%S-%f") + ".zip")
    export_zip(db_path, safety, data_dir=data, backup=True)
    old_files = {name: (data / SETTING_FILES[name]).read_bytes() if (data / SETTING_FILES[name]).exists() else None for name in settings}
    def write_setting(target, contents):
        target.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(prefix=".amp-restore-", dir=target.parent, delete=False) as file:
            temporary = Path(file.name)
        try:
            temporary.write_bytes(contents); os.replace(temporary, target)
        finally:
            temporary.unlink(missing_ok=True)
    with ScoreDB(db_path) as db:
        try:
            with db.conn:
                db.conn.execute("BEGIN IMMEDIATE")
                db.conn.execute("DELETE FROM scores")
                db.conn.execute("DELETE FROM library_groups WHERE id<>1")
                now = datetime.now().isoformat()
                for group in groups:
                    if group["id"] != 1: db.conn.execute("INSERT INTO library_groups(id,name,created_at) VALUES(?,?,?)", (group["id"], group["name"], now))
                for record, score in zip(records, scores):
                    db.conn.execute("INSERT INTO scores(id,name,source_type,raw_text,notes_json,bpm_default,created_at,updated_at,favorite) VALUES(?,?,?,?,?,?,?,?,?)", (record["id"], score["name"], record.get("source_type") or "import", record.get("raw_text", ""), json.dumps(score["notes"], ensure_ascii=False), score["bpm"], record["created_at"], record["updated_at"], int(bool(record["favorite"]))))
                    for preference in record["preferences"]: db.conn.execute("INSERT INTO score_preferences VALUES(?,?,?,?)", (record["id"], preference["profile_id"], preference["settings_json"], now))
                db.conn.execute("DELETE FROM score_groups")
                for record in records:
                    for group in set(record["group_ids"]): db.conn.execute("INSERT INTO score_groups VALUES(?,?)", (record["id"], group))
                for name, content in settings.items(): write_setting(data / SETTING_FILES[name], content)
        except Exception:
            for name, content in old_files.items():
                target = data / SETTING_FILES[name]
                if content is None: target.unlink(missing_ok=True)
                else: write_setting(target, content)
            raise
    return {"count": len(records), "groups": len(groups), "settings": len(settings), "safety_backup": str(safety)}
