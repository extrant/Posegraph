"""面捕（face.py）的裁剪框和置信度打分（不需要模型）。"""
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import face


def keypoints(head_center=(100.0, 50.0), spread=10.0, shoulders=40.0):
    k = np.zeros((70, 3))
    cx, cy = head_center
    k[0, :2] = (cx, cy)
    k[1, :2], k[2, :2] = (cx + spread / 2, cy - 2), (cx - spread / 2, cy - 2)
    k[3, :2], k[4, :2] = (cx + spread, cy), (cx - spread, cy)
    k[5, :2], k[6, :2] = (cx + shoulders / 2, cy + 40), (cx - shoulders / 2, cy + 40)
    return k


class HeadBoxTests(unittest.TestCase):
    def test_box_is_centered_on_the_head_and_scaled_to_the_preview(self):
        box = face.head_box(keypoints(), 0.5, (400, 400))
        self.assertAlmostEqual((box[0] + box[2]) / 2, 50, delta=1)
        self.assertGreaterEqual(box[2] - box[0], 32)

    def test_head_outside_the_image_has_no_box(self):
        self.assertIsNone(face.head_box(keypoints((-500, -500)), 1.0, (400, 400)))


class ScoreTests(unittest.TestCase):
    def test_clear_frontal_face_is_fully_trusted(self):
        conf, reason = face.score(nose_offset=5, side=200, face_width=120, yaw_deg=10)
        self.assertEqual((conf, reason), (1.0, ""))

    def test_each_problem_lowers_confidence_below_the_threshold(self):
        cases = {"找到的脸不是这个人的（可能是旁边的人）": (80, 200, 120, 0), "脸太小": (1, 200, 30, 0), "侧脸太多": (1, 200, 120, 70)}
        for reason, args in cases.items():
            conf, why = face.score(*args)
            self.assertLess(conf, face.MIN_CONFIDENCE, reason)
            self.assertEqual(why, reason)


if __name__ == "__main__":
    unittest.main()
