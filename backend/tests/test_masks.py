"""合并模式人物遮罩（masks.py）与相关接口：画遮罩、编码、带遮罩识别（不需要权重和 Torch）。"""
import base64
import sys
import unittest
import zlib
from pathlib import Path
from unittest.mock import patch

import numpy as np
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import app as service
from posegraph import masks

# 一块正对镜头的方板（两个三角形）
QUAD = np.array([[0, 1, 2], [0, 2, 3]], dtype=np.int32)


def board(x0, y0, x1, y1, z):
    """镜头系（x 右、y 下）里的方板，焦距 100、图 200×200 时投影到像素 (100 + 100x/z, 100 + 100y/z)。"""
    return {"vertices": np.array([[x0, y0, 0], [x1, y0, 0], [x1, y1, 0], [x0, y1, 0]], dtype=np.float32),
            "cam_t": np.array([0.0, 0.0, z]), "focal_length": 100.0}


class MaskTests(unittest.TestCase):
    def test_nearer_person_covers_the_one_behind(self):
        far = board(-0.5, -0.5, 0.5, 0.5, 1.0)      # 像素 50..150
        near = board(0.0, -0.25, 0.5, 0.25, 0.5)     # 像素 100..200 × 50..150，在前面
        labels = masks.render_labels([far, near], (200, 200), QUAD)
        self.assertEqual(labels.shape, (200, 200))
        self.assertEqual(labels[100, 75], 1)     # 只有远的
        self.assertEqual(labels[100, 125], 2)    # 两个重叠：近的
        self.assertEqual(labels[20, 20], 0)
        # 换个顺序结果一样（按深度，不按人物顺序）
        swapped = masks.render_labels([near, far], (200, 200), QUAD)
        self.assertEqual(swapped[100, 125], 1)

    def test_large_images_are_scaled_down(self):
        labels = masks.render_labels([board(-0.5, -0.5, 0.5, 0.5, 1.0)], (4000, 2000), QUAD)
        self.assertEqual(labels.shape, (640, 1280))

    def test_encode_decode_round_trip_and_errors(self):
        labels = np.zeros((3, 4), dtype=np.uint8)
        labels[1, 2] = 2
        np.testing.assert_array_equal(masks.decode(masks.encode(labels), 4, 3, 2), labels)
        with self.assertRaises(ValueError):
            masks.decode(masks.encode(labels), 5, 3, 2)          # 长度不符
        with self.assertRaises(ValueError):
            masks.decode(masks.encode(labels), 4, 3, 1)          # 标签比人数多
        with self.assertRaises(ValueError):
            masks.decode(b"not zlib", 4, 3, 2)

    def test_per_person_masks_are_upscaled_to_the_photo(self):
        labels = np.array([[1, 0], [2, 2]], dtype=np.uint8)
        out = masks.per_person(labels, (4, 4), 3)
        self.assertEqual(out.shape, (3, 4, 4))
        self.assertEqual(out[0].sum(), 4)
        self.assertEqual(out[1].sum(), 8)
        self.assertEqual(out[2].sum(), 0)


class MaskApiTests(unittest.TestCase):
    def test_masks_endpoint_returns_labels_and_photo(self):
        from posegraph.session_cache import Session, SessionCache
        from PIL import Image
        import io

        png = io.BytesIO()
        Image.new("RGB", (200, 200), (10, 20, 30)).save(png, format="PNG")
        cache = SessionCache(2)
        self.addCleanup(cache.close)
        cache["s"] = Session("s", "s", (200, 200), png.getvalue(), [board(-0.5, -0.5, 0.5, 0.5, 1.0)], 1)
        fake = type("E", (), {"faces": lambda self: QUAD})()
        with patch.object(service, "_sessions", cache), patch.object(service, "_estimator", fake), \
                TestClient(service.app) as client:
            r = client.get("/sessions/s/masks").json()
        self.assertEqual((r["width"], r["height"], r["count"]), (200, 200, 1))
        labels = np.frombuffer(zlib.decompress(base64.b64decode(r["labels"])), np.uint8).reshape(200, 200)
        photo = np.frombuffer(zlib.decompress(base64.b64decode(r["image"])), np.uint8).reshape(200, 200, 3)
        self.assertEqual(labels[100, 100], 1)
        self.assertEqual(tuple(photo[0, 0]), (10, 20, 30))
        # 不投影：只要照片（从空白开始涂人），不需要模型
        with patch.object(service, "_sessions", cache), patch.object(service, "_estimator", object()),                 TestClient(service.app) as client:
            r = client.get("/sessions/s/masks?render=false").json()
        blank = np.frombuffer(zlib.decompress(base64.b64decode(r["labels"])), np.uint8)
        self.assertEqual((r["width"], r["height"], int(blank.max())), (200, 200, 0))

    def test_multipart_session_passes_masks_to_estimation(self):
        labels = np.zeros((4, 5), dtype=np.uint8)
        labels[0, 0] = 1
        with patch.object(service, "_estimate", side_effect=service.HTTPException(422, "fake")) as estimate, \
                TestClient(service.app) as client:
            r = client.post("/sessions?boxes=0,0,30,40;1,1,20,20&mask_width=5&mask_height=4",
                            files={"image": ("a.png", b"image"), "masks": ("m.bin", masks.encode(labels))})
            self.assertEqual(r.status_code, 422)
            args = estimate.call_args.args
            self.assertEqual(args[:4], (b"image", False, False, "0,0,30,40;1,1,20,20"))
            np.testing.assert_array_equal(args[4], labels)
            # 遮罩没有人物框 / 尺寸：拒绝
            bad = client.post("/sessions?mask_width=5&mask_height=4",
                              files={"image": ("a.png", b"image"), "masks": ("m.bin", masks.encode(labels))})
            self.assertEqual(bad.status_code, 400)
            # 标签比人物框多
            labels[1, 1] = 3
            bad = client.post("/sessions?boxes=0,0,30,40;1,1,20,20&mask_width=5&mask_height=4",
                              files={"image": ("a.png", b"image"), "masks": ("m.bin", masks.encode(labels))})
            self.assertEqual(bad.status_code, 400)


if __name__ == "__main__":
    unittest.main()
