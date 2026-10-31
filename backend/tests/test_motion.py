"""motion.py：镜头运动解算、角色走位、脚底防滑、镜头轨道。"""

import sys
import unittest
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import motion


def _scene(n, person, camera):
    """person(k) → 人在世界里的位置；camera(k) → (转动, 位置)：p_world = rot(p_cam) + pos。
    返回每帧几何（重力校正为单位、两套骨架髋部对齐在原点、比例 1）和镜头每一步。"""
    geos, steps, prev = [], [], None
    for k in range(n):
        rot, pos = camera(k)
        cam_t = rot.inv().apply(person(k) - pos)
        geos.append(motion.FrameGeometry(R.identity(), cam_t, np.zeros(3), np.zeros(3), 1.0))
        if prev is None:
            steps.append([0, 0, 0, 1, 0, 0, 0])
        else:
            prot, ppos = prev
            steps.append(list((rot.inv() * prot).as_quat()) + list(rot.inv().apply(ppos - pos)))
        prev = (rot, pos)
    ok = np.r_[False, np.ones(n - 1, bool)]
    return geos, motion.CameraSteps(np.array(steps, float), ok, "depth")


class RootTrackTest(unittest.TestCase):
    def test_static_person_moving_camera(self):
        geos, cam = _scene(20, lambda k: np.array([0.3, 0.1, 6.0]),
                           lambda k: (R.from_euler("y", np.radians(1.5 * k)), np.array([0.1 * k, 0, 0.05 * k])))
        root, breaks = motion.root_track(geos, cam, [0.0] * 20, 10)
        np.testing.assert_allclose(root[:, [0, 2]], 0, atol=1e-6)
        self.assertFalse(breaks.any())
        # 模型系跟着相机转，所以角色的转向要把镜头的转动补回来
        self.assertAlmostEqual(np.degrees(np.ptp(root[:, 3])), 1.5 * 19, places=3)

    def test_walking_person_fixed_camera(self):
        geos, cam = _scene(11, lambda k: np.array([0.2 * k, 0, 6.0]), lambda k: (R.identity(), np.zeros(3)))
        root, _ = motion.root_track(geos, cam, [0.0] * 11, 10)
        # 相机系 x 向右 → 模型系（镜头在 +z 看过来）也是 +x
        np.testing.assert_allclose(root[:, 0], 0.2 * np.arange(11), atol=1e-6)
        np.testing.assert_allclose(root[:, 2], 0, atol=1e-6)

    def test_cut_keeps_position_and_facing(self):
        # 第 5 帧切镜：人在画面里一下跳远，但人在场景里没动；切镜后位置、身体朝向接着
        geos, _ = _scene(10, lambda k: np.array([0, 0, 4.0 if k < 5 else 9.0]), lambda k: (R.identity(), np.zeros(3)))
        facing = [0.0] * 5 + [0.5] * 5   # 新机位从侧面拍：身体在模型系里转了 0.5 弧度
        root, breaks = motion.root_track(geos, None, facing, 10, forced_breaks={5})
        self.assertTrue(breaks[5])
        np.testing.assert_allclose(root[:, [0, 2]], 0, atol=1e-6)
        np.testing.assert_allclose(root[5:, 3] + 0.5, 0.0, atol=1e-6)   # 场景里的面朝方向 = 转向 + 身体朝向，前后都是 0

    def test_missing_frames_interpolated(self):
        geos, cam = _scene(9, lambda k: np.array([0.1 * k, 0, 5.0]), lambda k: (R.identity(), np.zeros(3)))
        geos[4] = None
        root, _ = motion.root_track(geos, cam, [0.0] * 4 + [None] + [0.0] * 4, 10)
        self.assertAlmostEqual(root[4, 0], 0.4, places=6)


class FootLockTest(unittest.TestCase):
    def test_planted_foot_stops_sliding(self):
        n, fps = 30, 10
        root = np.zeros((n, 4))
        root[:, 0] = np.linspace(0, 0.3, n)    # 识别误差：根在慢慢漂
        foot = np.tile([0.1, 0.0, 0.0], (n, 1))   # 脚一直踩在地上（模型系里不动）
        feet = {"j_asi_d_l": foot, "j_asi_e_l": foot + [0, 0, 0.1]}
        valid = np.ones(n, bool)
        before = motion.foot_slide(root, feet, fps, valid)
        locked = motion.lock_feet(root, feet, fps, valid)
        after = motion.foot_slide(locked, feet, fps, valid)
        self.assertGreater(before[0], 0.9)
        self.assertLess(after[1], before[1] * 0.3)

    def test_lifted_foot_not_locked(self):
        n, fps = 30, 10
        root = np.zeros((n, 4))
        root[:, 0] = np.linspace(0, 3.0, n)    # 跑起来了：脚离地
        foot = np.c_[np.full(n, 0.1), 0.3 + 0.1 * np.sin(np.arange(n)), np.zeros(n)]
        feet = {"j_asi_d_l": foot, "j_asi_e_l": foot}
        locked = motion.lock_feet(root, feet, fps, np.ones(n, bool))
        self.assertAlmostEqual(locked[-1, 0], 3.0, delta=0.5)


class CameraTracksTest(unittest.TestCase):
    def setUp(self):
        n = 40
        self.n = n
        self.root = np.zeros((n, 4))
        self.root[:, 0] = np.linspace(0, 2, n)
        self.cams = [{"position": [0.0, 1.2, 5.0], "forward": [0.0, 0.0, -1.0], "up": [0.0, 1.0, 0.0], "fovY": 40.0}] * n
        self.chest = np.tile([0.0, 1.3, 0.0], (n, 1))
        self.valid = np.ones(n, bool)

    def test_tracks(self):
        tracks = motion.camera_tracks(self.root, self.cams, self.chest, np.zeros(self.n), self.valid, 10)
        self.assertEqual(set(tracks), {"original", "follow", "pan"})
        # 原运镜：镜头跟着角色走（相对构图不变）
        self.assertAlmostEqual(tracks["original"][-1]["position"][0], 2.0, delta=0.05)
        # 定点摇镜：机位不动，视线对着角色，走远了视角变窄
        pan = tracks["pan"]
        np.testing.assert_allclose(pan[0]["position"], pan[-1]["position"])
        self.assertGreater(pan[-1]["forward"][0], 0.1)
        self.assertLess(pan[-1]["fovY"], pan[0]["fovY"])
        # 平滑跟拍：和角色的距离大致保持
        f0, f1 = tracks["follow"][0], tracks["follow"][-1]
        d = lambda c, k: np.linalg.norm(np.asarray(c["position"]) - np.array([self.root[k, 0], 1.3, 0]))
        self.assertAlmostEqual(d(f1, -1), d(f0, 0), delta=0.4)

    def test_hidden_frames_use_chained_camera(self):
        valid = self.valid.copy()
        valid[10:15] = False
        chained = [None] * self.n
        for k in range(10, 15):
            chained[k] = {"position": [9.0, 9.0, 9.0], "forward": [1.0, 0.0, 0.0], "up": [0.0, 1.0, 0.0], "fovY": None}
        tracks = motion.camera_tracks(self.root, self.cams, self.chest, np.zeros(self.n), valid, 10, chained)
        self.assertGreater(tracks["original"][12]["position"][0], 3.0)
        self.assertAlmostEqual(tracks["original"][12]["fovY"], 40.0, places=6)


class CameraSolveTest(unittest.TestCase):
    def test_rotation_vs_translation(self):
        rng = np.random.default_rng(0)
        f = 500.0
        K = np.array([[f, 0, 320], [0, f, 180], [0, 0, 1.0]])

        def case(rot, t):
            uv = np.c_[rng.uniform(20, 620, 400), rng.uniform(20, 340, 400)]
            z = rng.uniform(2, 12, 400)
            X = np.c_[(uv[:, 0] - 320) / f * z, (uv[:, 1] - 180) / f * z, z]
            Y = rot.apply(X) + t
            uv2 = np.c_[f * Y[:, 0] / Y[:, 2] + 320, f * Y[:, 1] / Y[:, 2] + 180] + rng.normal(0, 0.3, (400, 2))
            t_try = motion._solve_translation(rot, X, uv2, K)
            return np.median(motion._reproject(rot, np.zeros(3), X, uv2, K)), np.median(motion._reproject(rot, t_try, X, uv2, K)), t_try

        e0, e1, _ = case(R.from_euler("y", np.radians(1.0)), np.zeros(3))
        self.assertLess(e0, motion.PARALLAX_MIN_PX)        # 纯转动：不加平移
        e0, e1, t = case(R.identity(), np.array([0.0, 0.0, 0.3]))
        self.assertGreater(e0, motion.PARALLAX_MIN_PX)
        self.assertLess(e1, motion.PARALLAX_GAIN * e0)     # 推镜头：加上平移误差明显变小
        self.assertAlmostEqual(t[2], 0.3, delta=0.02)

    def test_tracker_recovers_pan_without_depth(self):
        import cv2

        rng = np.random.default_rng(1)
        base = cv2.GaussianBlur((rng.random((720, 1280)) * 255).astype(np.uint8), (0, 0), 2)
        base = cv2.cvtColor(base, cv2.COLOR_GRAY2RGB)
        f = 900.0
        K = np.array([[f, 0, 640], [0, f, 360], [0, 0, 1.0]])
        tracker = motion.CameraTracker(None)
        for k in range(6):
            rot = R.from_euler("y", np.radians(0.5 * k)).as_matrix()
            frame = cv2.warpPerspective(base, K @ rot.T @ np.linalg.inv(K), (1280, 720))
            tracker.add(frame[:, :, :3], [], f)
        res = tracker.result()
        self.assertEqual(res.mode, "rotation")
        self.assertTrue(res.ok[1:].all())
        total = R.identity()
        for k in range(1, 6):
            total = R.from_quat(res.steps[k, :4]) * total
        self.assertAlmostEqual(np.degrees(total.magnitude()), 2.5, delta=0.15)


class PersonOffsetTest(unittest.TestCase):
    def test_side_by_side_and_depth_fix(self):
        n = 6
        geo = lambda p: motion.FrameGeometry(R.identity(), np.array(p, float), np.zeros(3), np.zeros(3), 1.0)
        main = [geo([0, 0, 5.0])] * n
        other = [geo([1, 0, 5.0])] * n
        offset, both = motion.person_offsets(main, other, None, 10, 0)
        self.assertTrue(both.all())
        np.testing.assert_allclose(offset[:, [0, 2, 3]], [[1, 0, 0]] * n, atol=1e-6)   # 相机系 x 向右 = 模型系 +x
        # 场景深度说配角其实远 20%（SAM 把两人估成一样远）：沿视线往后挪
        depth = np.array([[5.0] * n, [6.0] * n])
        offset, _ = motion.person_offsets(main, other, depth, 10, 0)
        np.testing.assert_allclose(offset[:, 0], 1.2, atol=1e-6)
        np.testing.assert_allclose(offset[:, 2], -1.0, atol=1e-6)                    # 离镜头更远 = 模型系 -z

    def test_missing_frames_interpolated(self):
        geo = lambda p: motion.FrameGeometry(R.identity(), np.array(p, float), np.zeros(3), np.zeros(3), 1.0)
        main = [geo([0, 0, 5.0])] * 5
        other = [geo([0.1 * k, 0, 5.0]) for k in range(5)]
        other[2] = None
        offset, both = motion.person_offsets(main, other, None, 10, 0)
        self.assertFalse(both[2])
        self.assertAlmostEqual(offset[2, 0], 0.2, places=6)



class GravityTest(unittest.TestCase):
    def test_floor_seen_once_propagates_with_camera_turn(self):
        n = 6
        pitch = R.from_euler("x", np.radians(8))            # 镜头一开始往下俯 8°
        steps = [[0, 0, 0, 1, 0, 0, 0]] + [list(R.from_euler("x", np.radians(1)).as_quat()) + [0, 0, 0]] * (n - 1)   # 之后每帧再俯 1°
        cam = motion.CameraSteps(np.array(steps, float), np.r_[False, [True] * (n - 1)], "rotation")
        up0 = pitch.apply([0, -1.0, 0])                       # 第 0 帧相机系里的「上」
        quats, have = motion.gravity_upright({0: up0}, cam, None, n)
        self.assertTrue(have.all())
        for k in range(n):
            cam_k = R.from_euler("x", np.radians(1 * k)) * pitch
            up_k = cam_k.apply([0, -1.0, 0])
            model_up = R.from_quat(quats[k]).apply(up_k * np.array([1, -1, -1.0]))
            np.testing.assert_allclose(model_up, [0, 1, 0], atol=1e-6)

    def test_segment_without_floor_untouched(self):
        n = 4
        breaks = np.array([False, False, True, False])
        quats, have = motion.gravity_upright({0: np.array([0, -1.0, 0])}, None, breaks, n)
        self.assertEqual(have.tolist(), [True, True, False, False])


if __name__ == "__main__":
    unittest.main()
