"""演奏可观测性:按键发送日志与统计摘要。

每次演奏(含续播段)写一个 JSONL 日志文件:data/logs/play_YYYYMMDD_HHMMSS.jsonl
- 首行 {"type": "start", ...} 会话元信息(曲名/BPM/音符数)
- 每个完成的音符一行 {"type": "note", ...}:时间戳、音符、目标按键、发送结果、
  与上一音符的理论间隔(sched_ms)/实际间隔(actual_ms)及偏差(dev_ms,毫秒)
- 末行 {"type": "end", ...} 统计摘要

写盘失败自动降级为仅内存统计,绝不影响演奏本身。
日志目录位于 data/ 下,已被 .gitignore 覆盖,不入库不提交。
"""

import json
import os
from dataclasses import dataclass
from datetime import datetime

DEV_WARN_MS = 10.0  # 间隔偏差预警阈值(毫秒)


def _now() -> str:
    return datetime.now().isoformat(timespec="milliseconds")


def _round(v, nd=2):
    return None if v is None else round(float(v), nd)


@dataclass
class PlaySummary:
    score_name: str = ""
    total_notes: int = 0
    completed_notes: int = 0
    logged_notes: int = 0
    key_failures: int = 0
    avg_dev_ms: float | None = None
    max_dev_ms: float | None = None
    dev_over_ratio: float | None = None
    log_path: str = ""
    error: str | None = None

    @property
    def success_rate(self) -> float | None:
        if not self.logged_notes:
            return None
        return 1.0 - self.key_failures / self.logged_notes

    def format(self) -> str:
        parts = [f"完成 {self.completed_notes}/{self.total_notes} 音符"]
        if self.logged_notes:
            parts.append(f"按键成功率 {self.success_rate * 100:.0f}%")
        if self.avg_dev_ms is not None:
            parts.append(f"平均间隔偏差 {self.avg_dev_ms:.1f}ms")
        if self.max_dev_ms is not None:
            parts.append(f"最大 {self.max_dev_ms:.0f}ms")
        if self.dev_over_ratio is not None:
            parts.append(f"偏差>{DEV_WARN_MS:g}ms 占比 {self.dev_over_ratio * 100:.0f}%")
        if self.error:
            parts.append(f"出错: {self.error}")
        return " · ".join(parts)


class PlayLogger:
    """单次演奏会话的日志记录器;由演奏线程调用,GUI 只读取统计结果。"""

    def __init__(self, log_dir: str = "data/logs"):
        self.log_dir = log_dir
        self.path = ""
        self._meta = {}
        self._entries = []   # 内存副本:写盘失败时统计仍然可用
        self._fh = None

    # ---------- 会话 ----------

    def start(self, score_name: str, bpm: int, note_count: int):
        self._meta = {
            "score": score_name,
            "bpm": bpm,
            "note_count": note_count,
            "started_at": _now(),
        }
        self._entries = []
        self.path = ""
        self._fh = None
        try:
            self.path = self._new_path()
            self._fh = self._open()
        except Exception:
            self._fh = None  # 降级:仅内存统计,绝不影响演奏

    def log_note(self, index, notes, keys, ok, sched_ms=None, actual_ms=None, dev_ms=None, error=None):
        entry = {
            "type": "note",
            "ts": _now(),
            "index": index,
            "notes": list(notes),
            "keys": list(keys),
            "ok": bool(ok),
            "sched_ms": _round(sched_ms),
            "actual_ms": _round(actual_ms),
            "dev_ms": _round(dev_ms),
        }
        if error:
            entry["error"] = error
        self._entries.append(entry)
        self._write(entry)

    def finish(self, completed, total, stopped_early=False, error=None) -> PlaySummary:
        summary = self._build_summary(completed, total, error)
        self._write({
            "type": "end",
            "ts": _now(),
            "completed": completed,
            "total": total,
            "stopped_early": bool(stopped_early),
            "error": error,
            "logged_notes": summary.logged_notes,
            "key_failures": summary.key_failures,
            "success_rate": _round(summary.success_rate, 4),
            "avg_dev_ms": _round(summary.avg_dev_ms),
            "max_dev_ms": _round(summary.max_dev_ms),
            "dev_over_ratio": _round(summary.dev_over_ratio, 4),
        })
        if self._fh is not None:
            try:
                self._fh.close()
            except Exception:
                pass
            self._fh = None
        return summary

    # ---------- 统计 ----------

    def _build_summary(self, completed, total, error) -> PlaySummary:
        summary = PlaySummary(
            score_name=self._meta.get("score", ""),
            total_notes=total,
            completed_notes=completed,
            logged_notes=len(self._entries),
            key_failures=sum(1 for e in self._entries if not e["ok"]),
            log_path=self.path,
            error=error,
        )
        devs = [abs(e["dev_ms"]) for e in self._entries if e.get("dev_ms") is not None]
        if devs:
            summary.avg_dev_ms = sum(devs) / len(devs)
            summary.max_dev_ms = max(devs)
            summary.dev_over_ratio = sum(1 for d in devs if d > DEV_WARN_MS) / len(devs)
        return summary

    # ---------- 写盘(全部可失败降级) ----------

    def _new_path(self) -> str:
        base = datetime.now().strftime("play_%Y%m%d_%H%M%S")
        path = os.path.join(self.log_dir, base + ".jsonl")
        n = 1
        while os.path.exists(path):
            path = os.path.join(self.log_dir, f"{base}_{n}.jsonl")
            n += 1
        return path

    def _open(self):
        try:
            os.makedirs(self.log_dir, exist_ok=True)
            fh = open(self.path, "w", encoding="utf-8")
        except OSError:
            return None
        self._fh = fh
        try:
            self._dump({"type": "start", **self._meta})
        except Exception:
            try:
                fh.close()
            except Exception:
                pass
            self._fh = None
        return self._fh

    def _write(self, obj):
        if self._fh is None:
            return
        try:
            self._dump(obj)
        except Exception:
            try:
                self._fh.close()
            except Exception:
                pass
            self._fh = None

    def _dump(self, obj):
        self._fh.write(json.dumps(obj, ensure_ascii=False) + "\n")
        self._fh.flush()
