"""乐谱数据库(SQLite)。"""

import json
import hashlib
import os
import shutil
import sqlite3
from datetime import datetime

from core.score_model import require_valid


class ScoreDB:
    """乐谱库:一份乐谱记录一次,重复演奏无需重新上传。"""

    SCHEMA_VERSION = 3
    MAX_SOURCE_COPY_BYTES = 500 * 1024 * 1024

    def __init__(self, db_path: str):
        self.db_path = os.path.abspath(os.fspath(db_path))
        self.data_dir = os.path.dirname(self.db_path)
        self.sources_dir = os.path.join(self.data_dir, "sources")
        os.makedirs(self.data_dir, exist_ok=True)
        self.conn = sqlite3.connect(self.db_path)
        self._closed = False
        self.conn.row_factory = sqlite3.Row
        self.conn.execute("PRAGMA foreign_keys = ON")
        self.conn.execute("PRAGMA busy_timeout = 5000")
        self.schema_version_before = int(self.conn.execute("PRAGMA user_version").fetchone()[0])
        self.pre_migration_backup_path = None
        if self.schema_version_before < self.SCHEMA_VERSION and os.path.getsize(self.db_path) > 0:
            self.pre_migration_backup_path = self._create_migration_backup()
        self._create_tables()

    def _create_migration_backup(self):
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        target = f"{self.db_path}.pre-v{self.SCHEMA_VERSION}.{timestamp}.bak"
        backup_conn = sqlite3.connect(target)
        try:
            self.conn.backup(backup_conn)
        finally:
            backup_conn.close()
        return target

    def _create_tables(self):
        self.conn.execute(
            """
            CREATE TABLE IF NOT EXISTS scores (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                source_file TEXT,
                source_type TEXT,
                raw_text TEXT,
                notes_json TEXT NOT NULL,
                bpm_default INTEGER NOT NULL DEFAULT 100,
                created_at TEXT NOT NULL
            )
            """
        )
        self.conn.execute(
            """
            CREATE TABLE IF NOT EXISTS score_preferences (
                score_id INTEGER NOT NULL,
                profile_id TEXT NOT NULL,
                settings_json TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (score_id, profile_id),
                FOREIGN KEY (score_id) REFERENCES scores(id) ON DELETE CASCADE
            )
            """
        )
        self.conn.execute(
            """
            CREATE TABLE IF NOT EXISTS score_sources (
                score_id INTEGER PRIMARY KEY,
                schema_version INTEGER NOT NULL DEFAULT 1,
                source_kind TEXT NOT NULL,
                original_path TEXT,
                stored_path TEXT,
                sha256 TEXT,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                FOREIGN KEY (score_id) REFERENCES scores(id) ON DELETE CASCADE
            )
            """
        )
        # user_version 是应用自己的 schema 版本，不依赖 SQLite 文件格式版本。
        current = int(self.conn.execute("PRAGMA user_version").fetchone()[0])
        if current < self.SCHEMA_VERSION:
            self.conn.execute(f"PRAGMA user_version = {self.SCHEMA_VERSION}")
        columns = {r[1] for r in self.conn.execute("PRAGMA table_info(scores)")}
        if "updated_at" not in columns:
            self.conn.execute("ALTER TABLE scores ADD COLUMN updated_at TEXT NOT NULL DEFAULT ''")
            self.conn.execute("UPDATE scores SET updated_at=created_at")
        if "favorite" not in columns:
            self.conn.execute("ALTER TABLE scores ADD COLUMN favorite INTEGER NOT NULL DEFAULT 0")
        has_members = self.conn.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='score_groups'").fetchone()
        self.conn.execute("CREATE TABLE IF NOT EXISTS library_groups(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL COLLATE NOCASE UNIQUE, created_at TEXT NOT NULL)")
        self.conn.execute("INSERT OR IGNORE INTO library_groups(id,name,created_at) VALUES (1,'默认分组',?)", (datetime.now().isoformat(),))
        self.conn.execute("CREATE TABLE IF NOT EXISTS score_groups(score_id INTEGER NOT NULL REFERENCES scores(id) ON DELETE CASCADE, group_id INTEGER NOT NULL REFERENCES library_groups(id) ON DELETE CASCADE, PRIMARY KEY(score_id,group_id))")
        self.conn.execute("CREATE INDEX IF NOT EXISTS score_groups_by_group ON score_groups(group_id,score_id)")
        if not has_members:
            self.conn.execute("INSERT OR IGNORE INTO score_groups SELECT id,1 FROM scores")
        self.conn.execute("CREATE TRIGGER IF NOT EXISTS score_created_time AFTER INSERT ON scores WHEN NEW.updated_at='' BEGIN UPDATE scores SET updated_at=NEW.created_at WHERE id=NEW.id; END")
        self.conn.execute("CREATE TRIGGER IF NOT EXISTS score_content_updated AFTER UPDATE OF name,notes_json,bpm_default ON scores WHEN NEW.name!=OLD.name OR NEW.notes_json!=OLD.notes_json OR NEW.bpm_default!=OLD.bpm_default BEGIN UPDATE scores SET updated_at=strftime('%Y-%m-%dT%H:%M:%f','now','localtime') WHERE id=NEW.id; END")
        self.conn.execute("CREATE TABLE IF NOT EXISTS library_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL)")
        self.conn.commit()

    def close(self):
        """显式关闭连接；主窗口退出和更新 helper 都应调用。"""

        conn = getattr(self, "conn", None)
        if conn is not None and not self._closed:
            conn.close()
            self._closed = True

    def __enter__(self):
        return self

    def __exit__(self, _exc_type, _exc, _tb):
        self.close()

    def backup_to(self, target_path: str) -> str:
        """使用 SQLite backup API 创建一致性备份，返回绝对路径。"""

        target = os.path.abspath(os.fspath(target_path))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        backup_conn = sqlite3.connect(target)
        try:
            self.conn.backup(backup_conn)
        finally:
            backup_conn.close()
        return target

    def archive_source_file(self, source_path: str, *, source_kind: str = "source") -> dict:
        """将用户确认过的来源文件保存到 data/sources，并返回 sidecar 信息。"""

        source = os.path.abspath(os.fspath(source_path))
        size = os.path.getsize(source)
        if size > self.MAX_SOURCE_COPY_BYTES:
            raise ValueError("来源文件不能超过 500 MB")
        digest = hashlib.sha256()
        with open(source, "rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        sha256 = digest.hexdigest()
        extension = os.path.splitext(source)[1].lower()
        filename = f"{sha256}{extension}"
        os.makedirs(self.sources_dir, exist_ok=True)
        stored = os.path.join(self.sources_dir, filename)
        if not os.path.exists(stored):
            temporary = stored + ".tmp"
            try:
                shutil.copy2(source, temporary)
                os.replace(temporary, stored)
            finally:
                if os.path.exists(temporary):
                    try:
                        os.remove(temporary)
                    except OSError:
                        pass
        return {
            "kind": str(source_kind),
            "original_path": source,
            "stored_path": os.path.relpath(stored, self.data_dir),
            "sha256": sha256,
            "size": size,
            "extension": extension,
        }

    def _save_source_row(self, score_id, source):
        if not source:
            return
        if not isinstance(source, dict):
            raise ValueError("来源 sidecar 必须是对象")
        metadata = source.get("metadata", {})
        if not isinstance(metadata, dict):
            raise ValueError("来源 metadata 必须是对象")
        self.conn.execute(
            """
            INSERT INTO score_sources(
                score_id, schema_version, source_kind, original_path,
                stored_path, sha256, metadata_json, created_at
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(score_id) DO UPDATE SET
                schema_version=excluded.schema_version,
                source_kind=excluded.source_kind,
                original_path=excluded.original_path,
                stored_path=excluded.stored_path,
                sha256=excluded.sha256,
                metadata_json=excluded.metadata_json,
                created_at=excluded.created_at
            """,
            (
                int(score_id),
                int(source.get("schema_version", 1)),
                str(source.get("kind") or source.get("source_kind") or "source"),
                str(source.get("original_path") or ""),
                str(source.get("stored_path") or ""),
                str(source.get("sha256") or ""),
                json.dumps(metadata, ensure_ascii=False, sort_keys=True),
                datetime.now().isoformat(timespec="seconds"),
            ),
        )

    def add_score(self, name, notes, raw_text="", source_file="", source_type="", bpm_default=100, source_meta=None, group_ids=None, favorite=False):
        return self.add_scores([{"name": name, "notes": notes, "raw_text": raw_text, "source_file": source_file,
                                 "source_type": source_type, "bpm_default": bpm_default, "source_meta": source_meta}],
                               group_ids=group_ids, favorite=favorite)[0]

    def add_scores(self, records, group_ids=None, favorite=False):
        """Atomically add multiple already-recognized scores.

        Every record is validated before the transaction starts.  A malformed
        song therefore cannot leave half of an imported JSON library behind.
        """
        prepared = []
        for index, record in enumerate(records):
            if not isinstance(record, dict):
                raise ValueError(f"批量导入第 {index + 1} 项必须是对象")
            notes = record.get("notes")
            bpm = record.get("bpm_default", 100)
            require_valid(notes, bpm=bpm)
            prepared.append(
                (
                    (
                    str(record.get("name") or f"导入乐谱 {index + 1}"),
                    str(record.get("source_file") or ""),
                    str(record.get("source_type") or ""),
                    str(record.get("raw_text") or ""),
                    json.dumps(notes, ensure_ascii=False),
                    int(bpm),
                    datetime.now().isoformat(timespec="seconds"),
                    ),
                    record.get("source_meta"),
                )
            )
        ids = []
        groups = self.validate_groups(group_ids or [])
        with self.conn:
            for values, source_meta in prepared:
                cur = self.conn.execute(
                    "INSERT INTO scores (name, source_file, source_type, raw_text, notes_json, bpm_default, created_at) "
                    "VALUES (?, ?, ?, ?, ?, ?, ?)",
                    values,
                )
                ids.append(cur.lastrowid)
                self._save_source_row(cur.lastrowid, source_meta)
                self.conn.executemany("INSERT OR IGNORE INTO score_groups VALUES (?,?)", [(cur.lastrowid, g) for g in groups])
                if favorite: self.conn.execute("UPDATE scores SET favorite=1 WHERE id=?", (cur.lastrowid,))
        return ids


    def count_scores(self) -> int:
        cur = self.conn.execute("SELECT COUNT(*) FROM scores")
        return int(cur.fetchone()[0])

    def count_by_source(self, source_type: str) -> int:
        cur = self.conn.execute(
            "SELECT COUNT(*) FROM scores WHERE source_type=?", (source_type,)
        )
        return int(cur.fetchone()[0])

    def update_score(self, score_id, name, notes, raw_text="", bpm_default=100):
        require_valid(notes, bpm=bpm_default)
        self.conn.execute(
            "UPDATE scores SET name=?, raw_text=?, notes_json=?, bpm_default=? WHERE id=?",
            (name, raw_text, json.dumps(notes, ensure_ascii=False), bpm_default, score_id),
        )
        self.conn.commit()

    def list_scores(self):
        rows = self.conn.execute(
            "SELECT id, name, source_file, source_type, bpm_default, created_at, updated_at, favorite "
            "FROM scores ORDER BY created_at DESC"
        ).fetchall()
        memberships = {}
        for r in self.conn.execute("SELECT score_id,group_id FROM score_groups"):
            memberships.setdefault(r[0], []).append(r[1])
        return [dict(r) | {"group_ids": memberships.get(r["id"], [])} for r in rows]

    def get_score(self, score_id):
        row = self.conn.execute("SELECT * FROM scores WHERE id=?", (score_id,)).fetchone()
        if row is None:
            return None
        d = dict(row)
        d["notes"] = json.loads(d.pop("notes_json"))
        d["source"] = self.get_score_source(score_id)
        return d

    def get_score_source(self, score_id):
        row = self.conn.execute(
            "SELECT * FROM score_sources WHERE score_id=?", (int(score_id),)
        ).fetchone()
        if row is None:
            return None
        result = dict(row)
        try:
            result["metadata"] = json.loads(result.pop("metadata_json"))
        except (TypeError, json.JSONDecodeError):
            result["metadata"] = {}
        return result

    def save_score_source(self, score_id, source):
        with self.conn:
            self._save_source_row(score_id, source)

    def delete_score(self, score_id):
        self.conn.execute("DELETE FROM scores WHERE id=?", (score_id,))
        self.conn.commit()

    def get_score_preferences(self, score_id, profile_id):
        """Return per-score/per-profile UI and transport preferences.

        The payload is deliberately a versioned JSON object.  It keeps the
        stable ``scores`` schema untouched while allowing transport and
        practice controls to evolve independently.
        """
        row = self.conn.execute(
            "SELECT settings_json FROM score_preferences WHERE score_id=? AND profile_id=?",
            (int(score_id), str(profile_id)),
        ).fetchone()
        if row is None:
            return {}
        try:
            value = json.loads(row["settings_json"])
        except (TypeError, json.JSONDecodeError):
            return {}
        return value if isinstance(value, dict) else {}

    def save_score_preferences(self, score_id, profile_id, settings):
        if not isinstance(settings, dict):
            raise ValueError("乐谱偏好必须是对象")
        # Ensure invalid/non-serializable values fail before opening a write
        # transaction, rather than leaving a partially updated preference.
        payload = json.dumps(settings, ensure_ascii=False, sort_keys=True)
        now = datetime.now().isoformat(timespec="seconds")
        self.conn.execute(
            """
            INSERT INTO score_preferences(score_id, profile_id, settings_json, updated_at)
            VALUES (?, ?, ?, ?)
            ON CONFLICT(score_id, profile_id) DO UPDATE SET
                settings_json=excluded.settings_json,
                updated_at=excluded.updated_at
            """,
            (int(score_id), str(profile_id), payload, now),
        )
        self.conn.commit()

    def validate_groups(self, group_ids):
        groups = sorted(set(int(g) for g in group_ids))
        if len(groups) > 100: raise ValueError("一次最多操作 100 个分组")
        known = {r[0] for r in self.conn.execute("SELECT id FROM library_groups")}
        if any(g not in known for g in groups): raise ValueError("分组已不存在，请重新选择")
        return groups

    def list_groups(self):
        return [dict(r) | {"builtin": r["id"] == 1} for r in self.conn.execute("SELECT g.id,g.name,COUNT(s.score_id) AS count FROM library_groups g LEFT JOIN score_groups s ON s.group_id=g.id GROUP BY g.id ORDER BY g.id")]

    def save_group(self, name, group_id=0):
        name = str(name).strip()
        if not name or len(name) > 40: raise ValueError("分组名须为 1–40 个字符")
        if name.casefold() in {"全部", "收藏", "默认分组"}: raise ValueError("此名称属于系统分组")
        if group_id == 1: raise ValueError("默认分组不能重命名")
        try:
            with self.conn:
                if group_id:
                    if not self.conn.execute("UPDATE library_groups SET name=? WHERE id=?", (name, group_id)).rowcount: raise ValueError("分组已不存在")
                    return group_id
                return self.conn.execute("INSERT INTO library_groups(name,created_at) VALUES (?,?)", (name,datetime.now().isoformat())).lastrowid
        except sqlite3.IntegrityError: raise ValueError("已有同名分组") from None

    def delete_group(self, group_id):
        if group_id == 1: raise ValueError("默认分组不能删除")
        with self.conn:
            if not self.conn.execute("DELETE FROM library_groups WHERE id=?", (group_id,)).rowcount: raise ValueError("分组已不存在")

    def library_batch(self, score_ids, *, group_ids=(), remove=False, favorite=None, replace_groups=False):
        ids = sorted(set(int(s) for s in score_ids))
        if not ids or len(ids) > 10000: raise ValueError("请选择曲目（最多 10000 首）")
        groups = self.validate_groups(group_ids)
        with self.conn:
            # Avoid a large SQL parameter list and validate every ID before modifying anything.
            known = {r[0] for r in self.conn.execute("SELECT id FROM scores")}
            if any(s not in known for s in ids): raise ValueError("部分曲目已不存在，请刷新后重试")
            for score_id in ids:
                if replace_groups: self.conn.execute("DELETE FROM score_groups WHERE score_id=?", (score_id,))
                for group_id in groups:
                    if remove: self.conn.execute("DELETE FROM score_groups WHERE score_id=? AND group_id=?", (score_id, group_id))
                    else: self.conn.execute("INSERT OR IGNORE INTO score_groups VALUES (?,?)", (score_id, group_id))
                if favorite is not None: self.conn.execute("UPDATE scores SET favorite=? WHERE id=?", (int(bool(favorite)),score_id))
        return len(ids)
