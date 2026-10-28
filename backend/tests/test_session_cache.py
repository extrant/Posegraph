"""Batch session cache regression tests; no model weights or GPU required."""

import os
import subprocess
import sys
import unittest
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import app as service
from posegraph.estimators.base import EstimateResult, HumanEstimate
from posegraph.session_cache import Session, SessionCache


def make_session(sid, marker=1):
    return Session(sid, "照片 / portrait", (12, 16), b"preview-png", [{
        "joint_coords": np.arange(12, dtype=np.float32).reshape(4, 3) + marker,
        "joint_global_rots": np.eye(3, dtype=np.float64),
        "focal_length": 320.5,
        "rest_joint_coords": None,
        "bbox": np.array([0, 0, 12, 16], dtype=np.int32),
        "numpy_scalar": np.float32(1.25),
        "annotations": {"labels": ["portrait", "cached"]},
        "scores": [0.5, 0.75],
    }], 123.5)


class SessionCacheTests(unittest.TestCase):
    def setUp(self):
        self.cache = SessionCache(2)
        self.addCleanup(self.cache.close)

    def test_spill_round_trip_preserves_arrays_preview_and_metadata(self):
        expected = make_session("../../outside", 7)
        self.cache[expected.id] = expected
        self.cache["second"] = make_session("second")
        self.cache["third"] = make_session("third")
        self.assertEqual(self.cache.in_memory_count, 2)
        self.assertEqual(len(self.cache), 3)
        self.assertEqual(len(list(Path(self.cache._directory.name).glob("*.npz"))), 1)
        restored = self.cache[expected.id]
        self.assertIsNot(restored, expected)
        self.assertEqual(restored.name, expected.name)
        self.assertEqual(restored.image_size, expected.image_size)
        self.assertEqual(restored.preview_png, expected.preview_png)
        self.assertEqual(restored.inference_ms, expected.inference_ms)
        for field, original in expected.humans[0].items():
            actual = restored.humans[0][field]
            if isinstance(original, np.ndarray):
                np.testing.assert_array_equal(actual, original)
                self.assertEqual(actual.dtype, original.dtype)
            else:
                self.assertEqual(actual, original)
        self.assertEqual(self.cache.in_memory_count, 2)

    def test_delete_archived_and_memory_entries_removes_files(self):
        for i in range(5):
            self.cache[str(i)] = make_session(str(i))
        self.cache.discard("0")
        self.cache.discard("4")
        self.cache.discard("0")
        self.assertEqual(len(self.cache), 3)
        with self.assertRaises(KeyError):
            self.cache["0"]
        self.cache.clear()
        self.assertFalse(list(Path(self.cache._directory.name).iterdir()))
        self.assertEqual(self.cache.in_memory_count, 0)

    def test_fused_views_survive_spill_and_late_updates_do_not_revive_deleted_sessions(self):
        original = make_session("main")
        original.fused = make_session("fused", 9).humans[0]
        original.views = [{"yaw": 20.0, "view": "right"}]
        self.cache["main"] = original
        for sid in ("second", "third"):
            self.cache[sid] = make_session(sid)
        restored = self.cache["main"]
        self.assertEqual(restored.views, original.views)
        np.testing.assert_array_equal(restored.fused["joint_coords"], original.fused["joint_coords"])
        # A fusion computation can still hold `restored` after eviction.
        for sid in ("fourth", "fifth"):
            self.cache[sid] = make_session(sid)
        self.cache.set_views("main", None, None)
        self.assertIsNone(self.cache["main"].fused)
        self.assertIsNone(self.cache["main"].views)
        self.cache.discard("main")
        with self.assertRaises(KeyError):
            self.cache.set_views("main", original.fused, original.views)
        self.assertNotIn("main", self.cache)

    def test_disk_failure_keeps_existing_session_and_memory_bound(self):
        for sid in ("first", "second"):
            self.cache[sid] = make_session(sid)
        with patch.object(self.cache, "_save", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                self.cache["third"] = make_session("third")
        self.assertEqual(set(self.cache), {"first", "second"})
        self.assertEqual(self.cache.in_memory_count, 2)

    def test_concurrent_requests_keep_all_sessions_with_bounded_ram(self):
        def work(index):
            sid = str(index)
            self.cache[sid] = make_session(sid, index)
            np.testing.assert_equal(self.cache[sid].humans[0]["joint_coords"][0, 0], index)
        with ThreadPoolExecutor(max_workers=8) as executor:
            list(executor.map(work, range(24)))
        self.assertEqual(len(self.cache), 24)
        self.assertLessEqual(self.cache.in_memory_count, 2)
        for index in range(24):
            self.assertEqual(self.cache[str(index)].humans[0]["joint_coords"][0, 0], index)

    @unittest.skipUnless(os.name == "nt", "Windows delete-on-close behavior")
    def test_forced_backend_exit_removes_archived_image_data(self):
        script = """
import time
from posegraph.session_cache import Session, SessionCache
cache = SessionCache(1)
for sid in ('first', 'second'):
    cache[sid] = Session(sid, sid, (1, 1), b'private-preview', [], 1)
print(cache._directory.name, flush=True)
time.sleep(60)
"""
        environment = os.environ.copy()
        environment["PYTHONPATH"] = str(Path(__file__).resolve().parents[1] / "src")
        process = subprocess.Popen([sys.executable, "-c", script], stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, text=True, env=environment,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            directory = Path(process.stdout.readline().strip())
            self.assertTrue(directory.name.startswith("posegraph-sessions-"))
            self.assertTrue(list(directory.glob("*.npz")))
        finally:
            process.kill()
            process.communicate(timeout=10)
        self.assertFalse(list(directory.glob("*.npz")))
        directory.rmdir()


class BatchSessionApiTests(unittest.TestCase):
    def setUp(self):
        self.cache = SessionCache(service.MAX_SESSIONS)
        self.addCleanup(self.cache.close)
        sessions_patch = patch.object(service, "_sessions", self.cache)
        sessions_patch.start()
        self.addCleanup(sessions_patch.stop)
        self.client = TestClient(service.app)
        self.addCleanup(self.client.close)

    def test_more_than_eight_images_remain_selectable_without_reinference(self):
        calls = []

        def estimate(data, hold=False, parallel=False, boxes=None, labels=None):
            calls.append(data)
            marker = len(calls)
            human = HumanEstimate(
                bbox=np.array([0, 0, 12, 16], dtype=np.float32),
                joint_coords=np.full((4, 3), marker, dtype=np.float32),
                joint_global_rots=np.repeat(np.eye(3)[None], 4, axis=0),
                keypoints_2d=np.zeros((4, 2), dtype=np.float32),
                keypoints_3d=np.zeros((4, 3), dtype=np.float32),
                focal_length=50.0,
                cam_t=np.zeros(3, dtype=np.float32),
                rest_joint_coords=None,
                rest_joint_rots=None,
                extra={"vertices": np.ones((8, 3), dtype=np.float32),
                       "rest_vertices": np.zeros((8, 3), dtype=np.float32)},
            )
            return np.zeros((16, 12, 3), dtype=np.uint8), EstimateResult((12, 16), [human], {"inference": 10}, "cpu")

        ids = []
        with patch.object(service, "_estimate", side_effect=estimate), \
                patch.object(service, "settings", replace(service.settings, save_sessions=False)):
            for index in range(12):
                response = self.client.post("/sessions", params={"name": f"image-{index}"}, content=b"fake-image")
                self.assertEqual(response.status_code, 200, response.text)
                ids.append(response.json()["sessionId"])
            self.assertEqual(len(calls), 12)
            self.assertEqual(self.cache.in_memory_count, 8)
            self.assertEqual(len(self.cache), 12)
            for index in (0, 11, 1, 10, 2, 9):
                response = self.client.get(f"/sessions/{ids[index]}/image")
                self.assertEqual(response.status_code, 200, response.text)
                self.assertEqual(response.headers["content-type"], "image/png")
                self.assertTrue(response.content.startswith(b"\x89PNG"))
                restored = service._session(ids[index])
                self.assertEqual(restored.name, f"image-{index}")
                np.testing.assert_array_equal(restored.humans[0]["joint_coords"], np.full((4, 3), index + 1))
                self.assertLessEqual(self.cache.in_memory_count, 8)
            # Re-selecting uses the cached recognized arrays in the normal retarget endpoint.
            rt = SimpleNamespace(hand_compensation={})
            with patch.object(service.tuning, "run", return_value=rt) as run, \
                    patch.object(service.tuning, "check", return_value=([], {})), \
                    patch.object(service, "to_brio_pose_json", return_value="{}"):
                response = self.client.post(f"/sessions/{ids[0]}/retarget", json={"overlay": False, "stick": False})
                self.assertEqual(response.status_code, 200, response.text)
                np.testing.assert_array_equal(run.call_args.args[0]["joint_coords"], np.ones((4, 3)))
            self.assertEqual(len(calls), 12)
        def archive_and_restore(sid):
            archive = self.client.get(f"/sessions/{sid}/archive")
            self.assertEqual(archive.status_code, 200, archive.text)
            restored = self.client.post("/sessions/archive", params={"name": "restored-" + sid}, content=archive.content)
            self.assertEqual(restored.status_code, 200, restored.text)
            info = restored.json()
            self.assertIsNone(info["device"])
            self.assertEqual(info["name"], "restored-" + sid)
            restored_id = info["sessionId"]
            self.assertNotEqual(restored_id, sid)
            np.testing.assert_array_equal(service._session(restored_id).humans[0]["joint_coords"],
                                          service._session(sid).humans[0]["joint_coords"])
            self.assertEqual(self.client.get(f"/sessions/{restored_id}/image").content,
                             self.client.get(f"/sessions/{sid}/image").content)
            self.assertEqual(self.client.delete(f"/sessions/{restored_id}").json(), {"ok": True})
        with patch.object(service, "_parallel", service.ParallelBatch(8, {})), \
                patch.object(service, "_estimate", side_effect=AssertionError("archive restoration must not infer")):
            # 12 source sessions plus simultaneous restored sessions exceed the
            # eight RAM slots: none may vanish between create and GET archive.
            with ThreadPoolExecutor(max_workers=8) as executor:
                list(executor.map(archive_and_restore, ids))
            self.assertFalse(service._model_info()["loaded"])
        self.assertEqual(len(calls), 12)
        for sid in ids:
            response = self.client.delete(f"/sessions/{sid}")
            self.assertEqual(response.status_code, 200, response.text)
            self.assertEqual(self.client.get(f"/sessions/{sid}/image").status_code, 404)
        self.assertEqual(self.client.delete(f"/sessions/{ids[0]}").status_code, 200)
        self.assertEqual(len(self.cache), 0)
        self.assertFalse(list(Path(self.cache._directory.name).iterdir()))


if __name__ == "__main__":
    unittest.main()
