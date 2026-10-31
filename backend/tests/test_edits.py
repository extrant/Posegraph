"""edits.py：时间轴修正的关键帧插值、施加、从 Brio 改动收进来。"""

import sys
import unittest
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import edits

UP = R.from_euler("x", 30, degrees=True).as_quat()
DOWN = R.from_euler("x", -30, degrees=True).as_quat()


def _key(t, q, **kw):
    return edits.Key(t, {"arm": np.asarray(q, float)}, **kw)


class AtTest(unittest.TestCase):
    def test_ease_window(self):
        keys = [_key(2.0, UP, ease=0.5)]
        self.assertEqual(edits.at(keys, 1.4), {})
        self.assertAlmostEqual(edits.at(keys, 2.0)["arm"].w, 1.0)
        self.assertAlmostEqual(edits.at(keys, 2.15)["arm"].w, 1.0)     # 过渡的前 40% 完全按修正
        self.assertAlmostEqual(edits.at(keys, 2.35)["arm"].w, 0.5)     # 剩下的一段中点
        self.assertEqual(edits.at(keys, 2.6), {})

    def test_hold_until_next_key(self):
        keys = [_key(1.0, UP, mode="hold", ease=0.2), _key(3.0, DOWN, ease=0.5)]
        e = edits.at(keys, 2.0)["arm"]                       # 保持着第一个
        self.assertEqual((e.s, e.w), (0.0, 1.0))
        e = edits.at(keys, 2.75)["arm"]                      # 第二个的渐入里过渡过去
        self.assertAlmostEqual(e.s, 0.5)
        np.testing.assert_allclose(e.b[1], DOWN)
        self.assertAlmostEqual(edits.at(keys, 3.35)["arm"].w, 0.5)
        self.assertEqual(edits.at(keys, 2.55)["arm"].s, 0.0)                # 后一个的渐入刚开始：还完全是前一个

    def test_connected_and_separate_keys(self):
        near = [_key(1.0, UP, ease=0.5), _key(1.6, DOWN, ease=0.5)]
        e = edits.at(near, 1.3)["arm"]
        self.assertEqual(e.w, 1.0)                           # 影响范围连着：不回到原动作
        self.assertAlmostEqual(e.s, 0.5)
        far = [_key(1.0, UP, ease=0.2), _key(3.0, DOWN, ease=0.2)]
        self.assertEqual(edits.at(far, 2.0), {})

    def test_json_roundtrip_and_bad_input(self):
        k = edits.Key.from_json(_key(1.5, UP, kind="pose", mode="hold", ease=0.2).to_json())
        self.assertEqual((k.kind, k.mode, k.ease), ("pose", "hold", 0.2))
        np.testing.assert_allclose(k.bones["arm"], UP, atol=1e-6)
        with self.assertRaises(ValueError):
            edits.Key.from_json({"time": 1, "kind": "what", "bones": {}})

    def test_span(self):
        a = _key(1.0, UP, mode="hold", ease=0.2)
        b = _key(3.0, DOWN, ease=0.5)
        self.assertEqual(edits.span(a, [a, b], 10.0), (0.8, 3.0))
        self.assertEqual(edits.span(b, [a, b], 10.0), (1.0, 3.5))


class ApplyTest(unittest.TestCase):
    # 三根骨骼的链：root → arm → hand，全局朝向都是单位
    names = ["root", "arm", "hand"]
    parents = [-1, 0, 1]
    index = {n: i for i, n in enumerate(names)}
    sub = staticmethod(lambda i: list(range(i, 3)))

    def _run(self, e):
        G = [R.from_euler("y", 40, degrees=True)] * 3
        G_new = [G[0], G[1], None]
        edits.apply(self.parents, self.index, G, G_new, {"arm": e}, self.sub)
        return G, G_new

    def test_delta_turns_subtree_in_parent_frame(self):
        G, G_new = self._run(edits.BoneEdit(("delta", UP)))
        local = G[0].inv() * G[1]
        np.testing.assert_allclose(local.as_quat(), UP, atol=1e-9)    # 原来局部是单位，改后 = D
        self.assertTrue(np.allclose((G[1].inv() * G[2]).as_quat(), [0, 0, 0, 1]))   # 手跟着转，局部不变
        self.assertIsNone(G_new[2])

    def test_pose_sets_local_and_weight_blends(self):
        G, _ = self._run(edits.BoneEdit(("pose", DOWN), w=0.5))
        angle = np.degrees((G[0].inv() * G[1]).magnitude())
        self.assertAlmostEqual(angle, 15.0, places=6)

    def test_capture_counts_only_local_changes(self):
        names, parents = self.names, self.parents
        base = {n: R.identity().as_quat() for n in names}
        shown = dict(base)
        turn = R.from_euler("z", 20, degrees=True)
        brio = {"root": base["root"], "arm": turn.as_quat(), "hand": turn.as_quat()}   # 只转了上臂，手跟着
        got = edits.capture(names, parents, {"arm", "hand"}, base, shown, brio)
        self.assertEqual(list(got), ["arm"])
        np.testing.assert_allclose(R.from_quat(got["arm"]).as_quat(), turn.as_quat(), atol=1e-9)


if __name__ == "__main__":
    unittest.main()
