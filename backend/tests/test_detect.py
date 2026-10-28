"""合并模式：人物检测的后处理与 /detect、/sessions?boxes 接口（不需要模型文件）。"""
import sys
import tempfile
import unittest
from contextlib import ExitStack
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import app as service
from posegraph import detect
from posegraph.parallel_batch import ParallelBatch
from posegraph.shared_inference import ModelLifecycle
from inference_fakes import FakeEstimator, FakePool


class FakeSession:
    """按模型输入空间（640×640）给出候选框：(cx, cy, w, h, 分数)。"""

    def __init__(self, candidates, classes=80):
        out = np.zeros((4 + classes, max(1, len(candidates))), np.float32)
        for i, (cx, cy, w, h, score) in enumerate(candidates):
            out[:4, i] = cx, cy, w, h
            out[4, i] = score
        self.out, self.inputs = out[None], []

    def get_inputs(self):
        return [SimpleNamespace(name="images")]

    def run(self, _, feeds):
        self.inputs.append(feeds["images"])
        return [self.out]


IMAGE = np.zeros((640, 1280, 3), np.uint8)   # 宽 1280 高 640：缩放 0.5，上下各补 160


class DetectTests(unittest.TestCase):
    def sessions(self, photo, anime=None):
        fakes = {"photo": photo, "anime": anime or FakeSession([], classes=1)}
        return patch.object(detect, "_session", side_effect=lambda kind: fakes[kind])

    def test_boxes_map_back_to_image_and_are_numbered_left_to_right(self):
        photo = FakeSession([
            (500, 300, 60, 100, 0.5),     # 右边的人
            (100, 320, 40, 200, 0.9),     # 左边的人
            (102, 322, 40, 200, 0.6),     # 和左边的人几乎重合：只留分高的
            (300, 300, 50, 50, 0.1),      # 分数低于阈值
            (600, 200, 4, 4, 0.9),        # 太小
        ])
        with self.sessions(photo):
            found = detect.detect(IMAGE, "photo")
        self.assertEqual(found["kind"], "photo")
        self.assertEqual([p["bbox"] for p in found["people"]],
                         [[160.0, 120.0, 240.0, 520.0], [940.0, 180.0, 1060.0, 380.0]])
        self.assertEqual([p["score"] for p in found["people"]], [0.9, 0.5])
        x = photo.inputs[0]
        self.assertEqual(x.shape, (1, 3, 640, 640))
        self.assertAlmostEqual(float(x[0, 0, 0, 0]), 114 / 255, places=5)   # 补边是灰色
        self.assertAlmostEqual(float(x[0, 0, 320, 320]), 0.0, places=5)      # 图片本身

    def test_auto_falls_back_to_illustration_model_only_when_nobody_found(self):
        anime = FakeSession([(320, 320, 100, 200, 0.8)], classes=1)
        with self.sessions(FakeSession([]), anime), patch.object(detect, "available", return_value={"photo": True, "anime": True}):
            self.assertEqual(detect.detect(IMAGE)["kind"], "anime")
            self.assertEqual(len(detect.detect(IMAGE)["people"]), 1)
        with self.sessions(FakeSession([(320, 320, 100, 200, 0.8)]), anime):
            self.assertEqual(detect.detect(IMAGE)["kind"], "photo")
        with self.sessions(FakeSession([]), anime), patch.object(detect, "available", return_value={"photo": True, "anime": False}):
            found = detect.detect(IMAGE)
            self.assertEqual((found["kind"], found["people"]), ("photo", []))

    def test_endpoint_reports_missing_models_and_bad_kind(self):
        with tempfile.TemporaryDirectory() as empty, \
                patch.object(detect, "settings", SimpleNamespace(detector_dir=Path(empty))), \
                patch.object(service, "_decode", return_value=IMAGE), TestClient(service.app) as client:
            detect.unload()
            self.assertEqual(client.post("/detect?kind=photo", content=b"img").status_code, 503)
            self.assertEqual(client.post("/detect?kind=nope", content=b"img").status_code, 400)
            self.assertEqual(client.get("/health").json()["detectors"], {"photo": False, "anime": False})
        with self.sessions(FakeSession([(100, 320, 40, 200, 0.9)])), \
                patch.object(service, "_decode", return_value=IMAGE), TestClient(service.app) as client:
            body = client.post("/detect", content=b"img").json()
        self.assertEqual(body["imageSize"], [1280, 640])
        self.assertEqual(len(body["people"]), 1)


class BoxesTests(unittest.TestCase):
    def setUp(self):
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        for key, value in {"_estimator": FakeEstimator(), "_parallel": ParallelBatch(2, {}),
                           "_model_lifecycle": ModelLifecycle(), "_release_after": -1, "_held": False,
                           "_model_state": {"loading": False, "error": None}}.items():
            self.stack.enter_context(patch.object(service, key, value))
        self.factory = self.stack.enter_context(patch("posegraph.parallel_batch.SharedInferencePool", side_effect=FakePool))
        self.stack.enter_context(patch.object(service, "_decode", return_value=IMAGE))
        self.addCleanup(service._unload_model)

    def test_boxes_reach_the_estimator_clipped_to_the_image(self):
        service._estimate(b"img", False, False, "10,20,300,400; -5,0,1300,700")
        np.testing.assert_array_equal(service._estimator.bboxes, [[10, 20, 300, 400], [0, 0, 1280, 640]])

    def test_requests_with_boxes_never_use_the_shared_model(self):
        service._estimate(b"img", True, True, "10,20,300,400")
        self.factory.assert_not_called()
        self.assertTrue(service._estimator.loaded)

    def test_without_boxes_the_whole_image_is_one_person(self):
        service._estimate(b"img")
        self.assertIsNone(service._estimator.bboxes)

    def test_bad_boxes_are_rejected(self):
        for boxes in ("1,2,3", "a,b,c,d", "", "0,0,5,5", "2000,0,2100,600"):
            with self.subTest(boxes=boxes), self.assertRaises(service.HTTPException) as failure:
                service._estimate(b"img", False, False, boxes)
            self.assertEqual(failure.exception.status_code, 400)
        self.assertFalse(service._estimator.loaded)


if __name__ == "__main__":
    unittest.main()
