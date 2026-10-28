"""depth.masks_changed：深度修过的遮罩变化够大才重跑 SAM。"""
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import depth


class MasksChangedTest(unittest.TestCase):
    def setUp(self):
        self.before = np.zeros((2, 100, 100), bool)
        self.before[0, 10:90, 10:40] = True    # 2400 像素
        self.before[1, 10:90, 50:90] = True    # 3200 像素

    def test_unchanged(self):
        self.assertFalse(depth.masks_changed(self.before, self.before.copy()))

    def test_edge_jitter_is_ignored(self):
        after = self.before.copy()
        after[0, 10:90, 39] = False            # 80 像素 ≈ 3%
        self.assertFalse(depth.masks_changed(self.before, after))

    def test_limb_handed_over(self):
        after = self.before.copy()
        after[0, 60:90, 10:40] = False         # 一条腿（900 像素）判给了别人
        after[1, 60:90, 10:40] = True
        self.assertTrue(depth.masks_changed(self.before, after))

    def test_small_person_counts_relative_to_own_size(self):
        before = np.zeros((2, 100, 100), bool)
        before[0, :, :] = True
        before[1, 0:10, 0:10] = True           # 100 像素的小人
        after = before.copy()
        after[1, 0:10, 0:2] = False            # 20 像素：对小人来说是 20%
        self.assertTrue(depth.masks_changed(before, after))


if __name__ == "__main__":
    unittest.main()
