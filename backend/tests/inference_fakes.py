"""Shared, event-controlled inference doubles; no model weights or Torch."""
import queue
import threading
import time
from types import SimpleNamespace

from posegraph.i18n import _lang


def wait_until(condition, timeout=5):
    deadline = time.monotonic() + timeout
    while not condition():
        if time.monotonic() > deadline:
            raise AssertionError("Timed out waiting for lifecycle transition")
        time.sleep(.005)


class FakeEstimator:
    name, device, precision = "fake", "cpu", "fp32"
    share_mhr, mhr_on_cpu, min_free_vram_mb = True, True, 3000

    def __init__(self):
        self.loaded = False
        self.active_device = self.active_precision = None
        self.device_reason = ("手动指定",)
        self.lock = threading.Lock()
        self.load_calls = self.unloads = self.active = self.peak = 0
        self.load_failures = self.unload_failures = 0
        self.load_gate = self.unload_gate = None
        self.load_started, self.unload_started = threading.Event(), threading.Event()
        self.started = queue.Queue()
        self.gates, self.fail, self.language_barrier = {}, set(), None
        self.loads_language = []

    @staticmethod
    def wait(gate):
        if gate is not None and not gate.wait(timeout=5):
            raise TimeoutError("Test gate was not released")

    def load(self):
        with self.lock:
            if self.active:
                raise AssertionError("load overlapped inference")
            self.load_calls += 1
            self.loads_language.append(_lang.get())
        self.load_started.set()
        self.wait(self.load_gate)
        with self.lock:
            if self.load_failures:
                self.load_failures -= 1
                raise ValueError("fake load failure")
            self.loaded = True
            self.active_device, self.active_precision = self.device, self.precision

    def unload(self):
        self.unload_started.set()
        self.wait(self.unload_gate)
        with self.lock:
            if self.active:
                raise AssertionError("unload overlapped inference")
            if self.unload_failures:
                self.unload_failures -= 1
                raise ValueError("fake unload failure")
            self.unloads += int(self.loaded)
            self.loaded = False
            self.active_device = self.active_precision = None

    def estimate(self, marker, bboxes=None, masks=None, focal=None):
        self.bboxes = bboxes
        self.masks = masks
        if hasattr(marker, "shape"):
            marker = int(marker.flat[0])
        with self.lock:
            if not self.loaded:
                raise AssertionError("estimate ran without weights")
            self.active += 1
            self.peak = max(self.peak, self.active)
        language = _lang.get()
        self.started.put((marker, threading.get_ident()))
        try:
            self.wait(self.gates.get(marker))
            if self.language_barrier is not None:
                self.language_barrier.wait(timeout=5)
            if marker in self.fail:
                raise ValueError("fake request failure")
            return SimpleNamespace(humans=[1], marker=marker, language=language, after_language=_lang.get())
        finally:
            with self.lock:
                self.active -= 1

    def diagnostics(self, *, include_pointers=False):
        return {"sharedModel": True, "includePointers": include_pointers}


class FakePool:
    def __init__(self, parallelism, config, on_loaded=None):
        self.parallelism, self.config = parallelism, dict(config)
        self.on_loaded = on_loaded
        self.loaded = self.closed = False
        self.estimates = 0

    @property
    def states(self):
        return [{"loaded": self.loaded, "loading": False, "error": None,
                 "device": "cpu", "precision": "fp32", "device_reason": ("",)}]

    def preload(self, language="zh"):
        self.loaded = True

    def estimate(self, rgb, language="zh"):
        self.estimates += 1
        self.loaded = True
        return SimpleNamespace(humans=[1])

    def close(self):
        self.closed, self.loaded = True, False
