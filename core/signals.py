"""Qt signals for the legacy UI; small thread-safe hooks for the WinUI worker."""
import os
import threading

if os.environ.get("AMP_HEADLESS") != "1":
    from PyQt6.QtCore import QObject, pyqtSignal
else:
    class QObject:
        def __init__(self, parent=None):
            pass

    class _Hook:
        def __init__(self):
            self._handlers = []
            self._lock = threading.RLock()

        def connect(self, handler):
            with self._lock:
                self._handlers.append(handler)

        def emit(self, *args):
            with self._lock:
                handlers = tuple(self._handlers)
            for handler in handlers:
                handler(*args)

    class pyqtSignal:
        def __init__(self, *types):
            pass

        def __set_name__(self, owner, name):
            self.name = "_hook_" + name

        def __get__(self, instance, owner=None):
            if instance is None:
                return self
            if self.name not in instance.__dict__:
                instance.__dict__[self.name] = _Hook()
            return instance.__dict__[self.name]
