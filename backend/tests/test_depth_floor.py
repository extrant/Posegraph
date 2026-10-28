"""depth.find_floor：从照片的点云里认出地面（不需要深度模型，用合成的地面 + 墙）。"""
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import depth


def room(pitch_deg: float, size: int = 120, focal: float = 100.0):
    """镜头离地 1.5m、往下俯 pitch_deg，前方 4m 一面墙。返回 PhotoDepth（相机系：x 右、y 下、z 前）。"""
    p = np.radians(pitch_deg)
    # 世界（y 向上）→ 相机：先按俯仰转
    rot = np.array([[1, 0, 0], [0, np.cos(p), np.sin(p)], [0, -np.sin(p), np.cos(p)]])
    ys, xs = np.mgrid[0:size, 0:size]
    rays_cam = np.stack([(xs - size / 2) / focal, (ys - size / 2) / focal, np.ones_like(xs, float)], -1)
    flip = np.array([1.0, -1.0, 1.0])
    rays_world = (rays_cam * flip) @ rot            # 相机 → 世界（y 上、z 前）
    eye = np.array([0.0, 1.5, 0.0])
    t_floor = np.where(rays_world[..., 1] < -1e-6, -eye[1] / np.minimum(rays_world[..., 1], -1e-6), np.inf)
    t_wall = np.where(rays_world[..., 2] > 1e-6, 4.0 / np.maximum(rays_world[..., 2], 1e-6), np.inf)
    t = np.minimum(t_floor, t_wall)
    world = eye + rays_world * t[..., None]
    cam = ((world - eye) @ rot.T) * flip
    valid = np.isfinite(t)
    cam[~valid] = np.nan
    return depth.PhotoDepth(focal, cam[..., 2], cam, valid), (t_floor <= t_wall)


class FloorTests(unittest.TestCase):
    def test_steep_top_down_shot_picks_the_floor_under_the_feet(self):
        photo, is_floor = room(65)
        ys, xs = np.nonzero(is_floor)
        feet = [(int(xs[i]), int(ys[i])) for i in np.linspace(0, len(xs) - 1, 4).astype(int)]
        torso = [np.array([0.0, -np.cos(np.radians(65)), -np.sin(np.radians(65))])]   # 站直的人在俯拍相机里的「上」
        n, d = depth.find_floor(photo, np.zeros((1, *is_floor.shape), bool), feet, torso)
        up = np.array([0.0, -np.cos(np.radians(65)), -np.sin(np.radians(65))])
        self.assertGreater(float(n @ up), 0.99)
        self.assertAlmostEqual(d, 1.5, delta=0.1)

    def test_without_feet_only_a_gentle_floor_counts(self):
        photo, is_floor = room(20)
        n, _ = depth.find_floor(photo, np.zeros((1, *is_floor.shape), bool))
        self.assertGreater(float(n @ [0.0, -np.cos(np.radians(20)), -np.sin(np.radians(20))]), 0.99)
        steep, _ = room(65)
        self.assertIsNone(depth.find_floor(steep, np.zeros((1, *is_floor.shape), bool)))


if __name__ == "__main__":
    unittest.main()
