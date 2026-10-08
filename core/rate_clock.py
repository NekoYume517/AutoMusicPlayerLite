"""Continuous playback timeline: rate changes preserve elapsed phase."""
import time

class RateClock:
    def __init__(self, rate=None, now=time.perf_counter):
        self.rate = rate
        self.wall_now = now
        self.wall = now()
        self.virtual = self.wall
        self.last_rate = float(rate()) if rate else 1.0
    def now(self):
        wall = self.wall_now()
        self.virtual += max(0, wall - self.wall) * self.last_rate
        self.wall = wall
        self.last_rate = float(self.rate()) if self.rate else 1.0
        return self.virtual
    def wait_until(self, target, stop):
        while True:
            remaining = target - self.now()
            if remaining <= 0:
                return stop.is_set()
            delay = remaining / max(.01, self.last_rate)
            if stop.wait(min(.01, delay) if self.rate else delay):
                return True
    def wait_for(self, seconds, stop):
        return self.wait_until(self.now() + seconds, stop)
