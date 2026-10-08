"""延迟校准:测量本机按键输出的调度滞后,计算补偿值。

测量原理:按固定节奏(默认 250ms 间隔 × 16 次)发送交替测试按键,
记录"计划发送时刻"与"实际发送完成时刻"(含 SendInput 调用耗时)之差;
其平均值即为系统性滞后 = Event.wait 超时过冲 + 线程唤醒延迟 + 发送调用耗时。

补偿值 = 平均滞后取整,范围 0~100ms。
演奏引擎采用绝对时钟调度:每个音符的目标发送时刻提前补偿量,
从而抵消每次发送的系统性滞后;配合绝对时钟,长曲演奏不再累积节奏漂移。

校准不要求游戏窗口焦点——测的是本机输出链路,与游戏无关。
时钟与等待函数可注入,单元测试借此做到完全确定性。
"""

import threading
import time
from dataclasses import dataclass

MAX_COMPENSATION_MS = 100
HOLD_MS = 50        # 测试音按住时长(不参与滞后测量)
DEFAULT_INTERVAL_MS = 250
DEFAULT_SAMPLES = 16


def _perf_counter() -> float:
    return time.perf_counter()


def _default_wait(seconds: float):
    threading.Event().wait(seconds)


@dataclass
class CalibrationResult:
    samples: int = 0
    avg_lag_ms: float = 0.0
    max_lag_ms: float = 0.0
    std_lag_ms: float = 0.0
    compensation_ms: int = 0
    cancelled: bool = False

    @property
    def stable(self) -> bool:
        return self.std_lag_ms <= 5.0

    def format(self) -> str:
        if self.samples == 0:
            return "未采集到数据"
        lines = [
            f"采样 {self.samples} 次 · 平均滞后 {self.avg_lag_ms:.1f}ms",
            f"最大滞后 {self.max_lag_ms:.1f}ms · 波动(标准差) {self.std_lag_ms:.1f}ms",
            f"建议补偿值 {self.compensation_ms}ms · "
            + ("系统输出稳定" if self.stable else "系统波动较大,建议关闭后台程序后重测"),
        ]
        if self.cancelled:
            lines.append("(已取消,结果不完整)")
        return "\n".join(lines)


class LatencyCalibrator:
    """测试序列执行器。clock/wait_fn 可注入:测试中用虚拟时钟得到确定性结果。"""

    def __init__(self, driver, keymap, interval_ms=DEFAULT_INTERVAL_MS,
                 samples=DEFAULT_SAMPLES, clock=None, wait_fn=None):
        self._driver = driver
        self._keymap = keymap
        self.interval_ms = interval_ms
        self.samples = samples
        self._clock = clock or _perf_counter
        self._wait = wait_fn or _default_wait

    def run(self, stop_event=None, on_sample=None) -> CalibrationResult:
        """执行采样;stop_event 触发后尽快返回(cancelled=True)。"""
        ev = stop_event or threading.Event()
        keys = [k for k in (self._keymap.key_for("mid_1"), self._keymap.key_for("mid_2")) if k] or ["A"]
        interval = self.interval_ms / 1000.0
        lags = []
        cancelled = False
        t0 = self._clock()
        for k in range(self.samples):
            if ev.is_set():
                cancelled = True
                break
            target = t0 + k * interval
            wait_s = target - self._clock()
            if wait_s > 0:
                self._wait(wait_s)
            if ev.is_set():
                cancelled = True
                break
            key = keys[k % len(keys)]
            self._driver.press_key(key)
            lags.append((self._clock() - target) * 1000.0)
            self._wait(HOLD_MS / 1000.0)
            self._driver.release_key(key)
            if on_sample:
                on_sample(k + 1)

        result = CalibrationResult(cancelled=cancelled)
        result.samples = len(lags)
        if lags:
            result.avg_lag_ms = sum(lags) / len(lags)
            result.max_lag_ms = max(lags)
            mean = result.avg_lag_ms
            result.std_lag_ms = (sum((x - mean) ** 2 for x in lags) / len(lags)) ** 0.5
            result.compensation_ms = max(0, min(MAX_COMPENSATION_MS, int(round(result.avg_lag_ms))))
        return result
