"""乐谱导入导出与来源格式适配(精简版: 仅 JSON)。

- JSON 支持本项目格式、带绝对时间的来源格式，以及时间轴文件的
  ``events``/``library`` 信封；所有结果统一经过乐谱 Schema 校验。
- ``import_external_events`` 供本地 JSON 文件复用时间轴适配链路。
- 解析、适配、SQLite 入库和播放输入互相隔离；导入不会触发键盘或鼠标。
"""

import json
import math
import os
import re
from dataclasses import dataclass, field

from core.score_model import require_valid
from core.source_score import AdaptOptions, SourceNote, SourceSong, adapt_source_song

JSON_FORMAT = "auto-music-player-score"
JSON_VERSION = 1
TPQ = 480          # 导出用的每四分音符 tick 数
MIN_BPM, MAX_BPM = 30, 300
MAX_IMPORT_BYTES = 10 * 1024 * 1024

_OCTAVE_OFFSET = {"low": -12, "mid": 0, "high": 12}
_NUM_SEMITONE = (0, 2, 4, 5, 7, 9, 11)   # 简谱 1-7 = C D E F G A B(自然音级,非半音递增)
_WHITE_NUM = {0: 1, 2: 2, 4: 3, 5: 4, 7: 5, 9: 6, 11: 7}
_BLACK_PCS = {1, 3, 6, 8, 10}
_MIDI_MIN, _MIDI_MAX = 48, 83   # low_1(C3) .. high_7(B5)


@dataclass
class ImportResult:
    name: str
    bpm: int
    notes: list
    warnings: list = field(default_factory=list)
    kind: str = ""   # "json"
    degradations: list = field(default_factory=list)
    tracks: dict = field(default_factory=dict)
    selected_track: int | None = None
    source_format: str = "native"
    # 来源层 sidecar；现有 notes 仍是兼容的游戏投影。
    source_metadata: dict = field(default_factory=dict)
    raw_text: str = ""


# ---------- 音符映射 ----------

def note_id_to_midi(note_id: str, semitone: int = 0) -> int:
    """note_id(如 mid_1)→ MIDI 音高编号;C4(中音 1)= 60。非法 id 抛 ValueError。"""
    octave, sep, num = note_id.partition("_")
    if octave not in _OCTAVE_OFFSET or not sep or not num.isdigit() or not 1 <= int(num) <= 7:
        raise ValueError(f"无效音符: {note_id}(应为 high/mid/low_1~7)")
    if semitone not in (0, 1):
        raise ValueError(f"semitone 必须是 0/1: {semitone}")
    return 60 + _OCTAVE_OFFSET[octave] + _NUM_SEMITONE[int(num) - 1] + semitone


def midi_to_note_id(midi: int) -> str | None:
    """MIDI 音高 → note_id;黑键或 C3~B5 之外返回 None。"""
    if not _MIDI_MIN <= midi <= _MIDI_MAX:
        return None
    pc = midi % 12
    if pc in _BLACK_PCS:
        return None
    octave = ("low", "mid", "high")[midi // 12 - 4]
    return f"{octave}_{_WHITE_NUM[pc]}"


# ---------- JSON ----------

def export_json(path: str, score: dict) -> None:
    data = {
        "format": JSON_FORMAT,
        "version": JSON_VERSION,
        "name": score.get("name", ""),
        "bpm": int(score.get("bpm_default", 100)),
        "notes": score.get("notes", []),
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def _load_json(path: str):
    if os.path.getsize(path) > MAX_IMPORT_BYTES:
        raise ValueError("JSON 乐谱不能超过 10 MB")
    try:
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)
    except json.JSONDecodeError as exc:
        raise ValueError(f"JSON 文件格式错误:第 {exc.lineno} 行第 {exc.colno} 列") from exc


def _native_json_result(data, stem: str) -> ImportResult | None:
    if isinstance(data, list):
        notes, name, bpm = data, stem, 100
    elif isinstance(data, dict) and isinstance(data.get("notes"), list):
        # Rich source JSON also has a notes list; its explicit format wins.
        if data.get("format") == "auto-music-player-source":
            return None
        if data.get("format") not in (None, JSON_FORMAT):
            return None
        if data.get("format") == JSON_FORMAT and int(data.get("version", 1)) != JSON_VERSION:
            raise ValueError(f"不支持的乐谱 JSON 版本: {data.get('version')}")
        notes = data["notes"]
        name = str(data.get("name") or stem)
        try:
            bpm = int(data.get("bpm", 100))
        except (TypeError, ValueError):
            raise ValueError("JSON 中的 bpm 不是有效数字") from None
    else:
        return None
    require_valid(notes, bpm=bpm)
    return ImportResult(name=name, bpm=bpm, notes=notes, kind="json")


_EXTERNAL_NOTE_RE = re.compile(r"^([+-]?)(#?)([1-7])$")
_SCALE = (0, 2, 4, 5, 7, 9, 11)
_KEY_DEGREE = {key: index for index, key in enumerate("zxcvbnm", start=1)}
_KEY_DEGREE[","] = 1


def _external_event_pitch(event, index: int) -> int:
    raw_note = str(event.get("note") or "").strip()
    match = _EXTERNAL_NOTE_RE.fullmatch(raw_note)
    if match:
        octave, accidental, degree = match.groups()
        pitch = 60 + _SCALE[int(degree) - 1]
        pitch += {"": 0, "+": 12, "-": -12}[octave]
        pitch += 1 if accidental else 0
    else:
        key = str(event.get("key") or "").lower()
        if key not in _KEY_DEGREE:
            raise ValueError(f"外部乐谱事件 {index + 1} 缺少可识别的 note/key")
        pitch = 60 + _SCALE[_KEY_DEGREE[key] - 1]
        if key == ",":
            pitch += 12

    raw_mouse = event.get("mouse") or []
    if isinstance(raw_mouse, str):
        raw_mouse = [raw_mouse]
    if not isinstance(raw_mouse, list):
        raise ValueError(f"外部乐谱事件 {index + 1} 的 mouse 必须是列表")
    mouse = {str(button).lower() for button in raw_mouse}
    unknown = mouse - {"left", "middle", "right"}
    if unknown:
        raise ValueError(f"外部乐谱事件 {index + 1} 含未知鼠标档位: {sorted(unknown)}")
    if "left" in mouse and "right" in mouse:
        raise ValueError(f"外部乐谱事件 {index + 1} 同时请求左右八度档位")
    if "left" in mouse:
        pitch -= 12
    if "right" in mouse:
        pitch += 12
    if "middle" in mouse:
        pitch += 1
    return pitch


def _external_song(data: dict, *, stem: str, inherited_bpm=120) -> tuple[SourceSong, list[str]]:
    events = data.get("events")
    if not isinstance(events, list):
        raise ValueError("外部乐谱缺少 events 列表")
    if len(events) > 30_000:
        raise ValueError("外部乐谱事件超过 30000 个")
    try:
        bpm = int(data.get("bpm", inherited_bpm))
    except (TypeError, ValueError):
        raise ValueError("外部乐谱的 bpm 不是有效数字") from None
    if not MIN_BPM <= bpm <= MAX_BPM:
        raise ValueError(f"外部乐谱 BPM 必须在 {MIN_BPM}-{MAX_BPM}")
    notes = []
    for index, event in enumerate(events):
        if not isinstance(event, dict):
            raise ValueError(f"外部乐谱事件 {index + 1} 必须是对象")
        try:
            start_s = float(event.get("t"))
            duration_s = float(event.get("d"))
        except (TypeError, ValueError):
            raise ValueError(f"外部乐谱事件 {index + 1} 的 t/d 不是有效数字") from None
        if not math.isfinite(start_s) or not math.isfinite(duration_s):
            raise ValueError(f"外部乐谱事件 {index + 1} 的 t/d 必须是有限数字")
        if start_s < 0 or duration_s <= 0:
            raise ValueError(f"外部乐谱事件 {index + 1} 必须满足 t≥0、d>0")
        notes.append(
            SourceNote(start_s, start_s + duration_s, _external_event_pitch(event, index), 0)
        )
    duration_s = max((note.end_s for note in notes), default=0.0)
    warnings = []
    if "bpm" not in data:
        warnings.append(f"外部事件格式未提供 BPM，按 {bpm} BPM 导入")
    return (
        SourceSong(
            title=str(data.get("title") or data.get("name") or stem),
            notes=notes,
            tracks={0: "录制轨"},
            duration_s=duration_s,
            bpm_hint=bpm,
        ),
        warnings,
    )


def _rich_source_song(data: dict, stem: str) -> SourceSong:
    if int(data.get("version", 1)) != 1:
        raise ValueError(f"不支持的来源乐谱版本: {data.get('version')}")
    raw_notes = data.get("notes")
    if not isinstance(raw_notes, list):
        raise ValueError("来源乐谱缺少 notes 列表")
    notes = []
    for index, item in enumerate(raw_notes):
        if not isinstance(item, dict):
            raise ValueError(f"来源音符 {index + 1} 必须是对象")
        try:
            notes.append(
                SourceNote(
                    float(item["start"]),
                    float(item["end"]),
                    int(item["pitch"]),
                    int(item.get("track", 0)),
                )
            )
        except (KeyError, TypeError, ValueError):
            raise ValueError(f"来源音符 {index + 1} 的 start/end/pitch/track 无效") from None
    raw_tracks = data.get("tracks") or {"0": "主旋律"}
    if not isinstance(raw_tracks, dict):
        raise ValueError("来源乐谱 tracks 必须是对象")
    tracks = {int(key): str(value) for key, value in raw_tracks.items()}
    duration_s = float(data.get("duration", max((note.end_s for note in notes), default=0.0)))
    return SourceSong(
        title=str(data.get("name") or data.get("title") or stem),
        notes=notes,
        tracks=tracks,
        duration_s=duration_s,
        bpm_hint=float(data.get("bpm", 100)),
        tempo_change_count=int(data.get("tempo_change_count", 1)),
    )


def _from_adapted(
    adapted,
    *,
    kind: str,
    source_format: str,
    extra_warnings=(),
    source_metadata=None,
):
    return ImportResult(
        name=adapted.name,
        bpm=adapted.bpm,
        notes=adapted.notes,
        warnings=[*extra_warnings, *adapted.warnings],
        kind=kind,
        degradations=list(adapted.degradations),
        tracks=dict(adapted.tracks),
        selected_track=adapted.selected_track,
        source_format=source_format,
        source_metadata=dict(source_metadata or {}),
    )


def import_external_events(data, *, stem: str = "external") -> ImportResult:
    """内存中的外部 events 谱({"title","bpm","events":[{t,d,note}]})→ ImportResult。

    本地时间轴转换: 与 JSON 导入的 events 分支走完全相同的适配链路。
    """
    if not isinstance(data, dict):
        raise ValueError("外部乐谱必须是对象")
    song, warnings = _external_song(data, stem=stem)
    adapted = adapt_source_song(song, AdaptOptions(style="preserve", track=None))
    return _from_adapted(
        adapted,
        kind="json",
        source_format="timeline-events",
        extra_warnings=warnings,
    )


def import_json_many(path: str) -> list[ImportResult]:
    """Recognize native, rich-source, and timeline JSON files."""
    data = _load_json(path)
    stem = os.path.splitext(os.path.basename(path))[0]
    native = _native_json_result(data, stem)
    if native is not None:
        return [native]
    if not isinstance(data, dict):
        raise ValueError("JSON 结构不是可识别的乐谱格式")

    if data.get("format") == "auto-music-player-source":
        song = _rich_source_song(data, stem)
        adapted = adapt_source_song(song, AdaptOptions(style="preserve", track=None))
        result = _from_adapted(
            adapted,
            kind="json",
            source_format="source-json",
            extra_warnings=data.get("warnings") if isinstance(data.get("warnings"), list) else (),
            source_metadata=(
                data.get("source_metadata")
                if isinstance(data.get("source_metadata"), dict)
                else {}
            ),
        )
        result.raw_text = str(data.get("raw_text") or "")
        return [result]

    if isinstance(data.get("events"), list):
        return [import_external_events(data, stem=stem)]

    library = data.get("library")
    if isinstance(library, list):
        if not library:
            raise ValueError("外部乐谱库为空")
        if len(library) > 200:
            raise ValueError("一次最多导入 200 首外部乐谱")
        results = []
        inherited_bpm = data.get("bpm", 120)
        for index, entry in enumerate(library):
            if not isinstance(entry, dict):
                raise ValueError(f"外部乐谱库第 {index + 1} 项必须是对象")
            song, warnings = _external_song(
                entry,
                stem=f"{stem}-{index + 1}",
                inherited_bpm=inherited_bpm,
            )
            adapted = adapt_source_song(song, AdaptOptions(style="preserve", track=None))
            results.append(
                _from_adapted(
                    adapted,
                    kind="json",
                    source_format="timeline-library",
                    extra_warnings=warnings,
                )
            )
        return results

    raise ValueError(
        "JSON 结构无法识别：支持本项目 notes、来源时间轴或时间轴文件 events/library"
    )


def import_json(path: str) -> ImportResult:
    results = import_json_many(path)
    if len(results) != 1:
        raise ValueError(f"JSON 中包含 {len(results)} 首乐谱，请从界面的批量导入入口处理")
    return results[0]


# ---------- 统一入口 ----------

def import_any(path: str) -> ImportResult:
    ext = os.path.splitext(path)[1].lower()
    if ext == ".json":
        return import_json(path)
    if ext in (".mid", ".midi"):
        from core.midi_io import read_midi
        return read_midi(path)
    raise ValueError(f"不支持的导入格式: {ext}（支持 JSON 和 MIDI）")


def import_many(path: str) -> list[ImportResult]:
    """Batch-aware import used by the GUI; JSON 可含 library 信封(单文件最多 200 首)。"""
    ext = os.path.splitext(path)[1].lower()
    if ext == ".json":
        return import_json_many(path)
    if ext == ".zip":
        from core.archive_io import read_score_zip
        return read_score_zip(path)
    return [import_any(path)]
