"""hands.py：按手部关键点重算手指（不需要 MediaPipe，用合成的关键点）。"""

import sys
import unittest
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import hands
from posegraph.retarget.ffxiv import load_mhr_rest

NAMES, REST_POS, REST_ROT = load_mhr_rest()
FLIP = np.array([1.0, -1.0, -1.0])
# MediaPipe 21 点 ← MHR 关节（拇指根用 thumb1）
MP_FROM_MHR = ["wrist", "thumb1", "thumb2", "thumb3", "thumb_null", "index1", "index2", "index3", "index_null",
               "middle1", "middle2", "middle3", "middle_null", "ring1", "ring2", "ring3", "ring_null",
               "pinky1", "pinky2", "pinky3", "pinky_null"]


def _human(turn: R) -> dict:
    """静止姿态整体转一下的人（相机系的位置 = 模型系 × FLIP）。"""
    rots = (turn * REST_ROT).as_matrix()
    return {"joint_global_rots": rots, "joint_coords": turn.apply(REST_POS) * FLIP,
            "rest_joint_coords": REST_POS, "rest_joint_rots": REST_ROT.as_matrix()}


def _points(human: dict, side: str) -> np.ndarray:
    model = np.asarray(human["joint_coords"]) * FLIP
    pts = np.array([model[NAMES.index(f"{side}_{n}")] for n in MP_FROM_MHR])
    return pts - pts[0]


def _flex(pts: np.ndarray, side: str, start: int, end: int, degrees: float) -> np.ndarray:
    """MediaPipe 点里一根手指从 start 关节起往掌心弯（负数往手背翘），start+1..end-1 跟着转。"""
    palm = np.cross(pts[9] - pts[0], pts[5] - pts[17]) * (1.0 if side == "l" else -1.0)
    seg = pts[start + 1] - pts[start]
    axis = np.cross(seg, palm)
    bend = R.from_rotvec(np.radians(degrees) * axis / np.linalg.norm(axis))
    out = pts.copy()
    out[start + 1:end] = pts[start] + bend.apply(pts[start + 1:end] - pts[start])
    return out


def _angle(a: np.ndarray, b: np.ndarray) -> float:
    return float(np.degrees((R.from_matrix(a).inv() * R.from_matrix(b)).magnitude()))


class SolveTest(unittest.TestCase):
    def test_own_points_reproduce_the_hand(self):
        h = _human(R.from_euler("xyz", [20, -35, 50], degrees=True))
        for side in "lr":
            solved = hands.solve(h, side, _points(h, side))
            for j, g in solved.items():
                self.assertLess(_angle(h["joint_global_rots"][j], g.as_matrix()), 1e-4, NAMES[j])

    def test_curled_index_follows_points(self):
        h = _human(R.identity())
        side = "r"
        pts = _points(h, side)
        pts = _flex(pts, side, 6, 9, 60)          # 食指第二节往掌心弯 60°
        new = hands.apply(h, {side: hands.solve(h, side, pts)})
        model = np.asarray(new["joint_coords"]) * FLIP
        got = model[NAMES.index("r_index3")] - model[NAMES.index("r_index2")]
        want = pts[7] - pts[6]
        cos = got @ want / np.linalg.norm(got) / np.linalg.norm(want)
        self.assertGreater(cos, 0.9999)
        # 别的手指不动
        for n in ("r_middle3", "r_pinky_null"):
            np.testing.assert_allclose(model[NAMES.index(n)], np.asarray(h["joint_coords"])[NAMES.index(n)] * FLIP, atol=1e-6)

    def test_bent_backwards_is_limited(self):
        h = _human(R.identity())
        side = "l"
        pts = _flex(_points(h, side), side, 6, 9, -120)   # 认错的一帧：食指第二节往手背反折 120°
        new = hands.apply(h, {side: hands.solve(h, side, pts)})
        j = NAMES.index("l_index2")
        self.assertLess(_angle(h["joint_global_rots"][j], new["joint_global_rots"][j]), 10.5)


class TrackTest(unittest.TestCase):
    def test_short_gap_interpolated_long_gap_kept(self):
        h = _human(R.identity())
        curled = _points(h, "l")
        for a, b in ((5, 9), (9, 13), (13, 17), (17, 21)):
            curled = _flex(curled, "l", a, b, 70)
        n = 40
        found = [None] * n
        for k in (0, 2, 3):
            found[k] = {"l": curled}
        out = hands.refine_track([h] * n, found, fps=10)
        j = NAMES.index("l_middle1")
        self.assertGreater(_angle(h["joint_global_rots"][j], out[1]["joint_global_rots"][j]), 50)   # 空一帧：插值
        self.assertLess(_angle(h["joint_global_rots"][j], out[30]["joint_global_rots"][j]), 1e-6)  # 远离认出的帧：保留原来的
        right = NAMES.index("r_middle1")
        self.assertLess(_angle(h["joint_global_rots"][right], out[1]["joint_global_rots"][right]), 1e-6)   # 另一只手不动


if __name__ == "__main__":
    unittest.main()
