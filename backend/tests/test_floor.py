"""多人场景的重力与地面（floor.py）：按深度找到的地面摆正（不需要权重）。"""
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import floor


def people(floor_info=None):
    h = {} if floor_info is None else {"floor": np.asarray(floor_info, dtype=np.float32)}
    return [dict(h), dict(h)]


class FloorTests(unittest.TestCase):
    def test_single_person_without_floor_is_left_to_the_standing_estimate(self):
        self.assertEqual(floor.scene([{}]), floor.Scene(None, False))

    def test_single_person_with_floor_is_leveled_to_it(self):
        n = np.array([0.0, -np.cos(np.radians(60)), -np.sin(np.radians(60))])   # 俯拍 60°
        s = floor.scene([{"floor": np.asarray([*n, 1.2, 1.0], dtype=np.float32)}])
        np.testing.assert_allclose(s.upright.apply(n * [1, -1, -1]), [0, 1, 0], atol=1e-6)

    def test_without_a_floor_nobody_is_rotated(self):
        s = floor.scene(people())
        self.assertAlmostEqual(s.upright.magnitude(), 0.0)
        self.assertFalse(s.on_floor)

    def test_floor_normal_becomes_up(self):
        # 镜头往下俯 30°：相机系里地面法线朝上偏向镜头
        n = np.array([0.0, -np.cos(np.radians(30)), -np.sin(np.radians(30))])
        s = floor.scene(people([*n, 1.5, 1.0]))
        np.testing.assert_allclose(s.upright.apply(n * [1, -1, -1]), [0, 1, 0], atol=1e-6)
        self.assertTrue(s.on_floor)

    def test_floor_found_but_someone_is_off_it(self):
        self.assertFalse(floor.scene(people([0, -1, 0, 1.5, 0.0])).on_floor)


if __name__ == "__main__":
    unittest.main()
