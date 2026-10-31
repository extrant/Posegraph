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
    def test_tracker_recovers_pan(self):
        import cv2

        rng = np.random.default_rng(1)
        base = cv2.GaussianBlur((rng.random((720, 1280)) * 255).astype(np.uint8), (0, 0), 2)
        base = cv2.cvtColor(base, cv2.COLOR_GRAY2RGB)
        f = 900.0
        K = np.array([[f, 0, 640], [0, f, 360], [0, 0, 1.0]])
        tracker = motion.CameraTracker()
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
    def test_side_by_side(self):
        n = 6
        geo = lambda p: motion.FrameGeometry(R.identity(), np.array(p, float), np.zeros(3), np.zeros(3), 1.0)
        main = [geo([0, 0, 5.0])] * n
        other = [geo([1, 0, 5.0])] * n
        offset, both = motion.person_offsets(main, other, 10, 0)
        self.assertTrue(both.all())
        np.testing.assert_allclose(offset[:, [0, 2, 3]], [[1, 0, 0]] * n, atol=1e-6)   # 相机系 x 向右 = 模型系 +x

    def test_missing_frames_interpolated(self):
        geo = lambda p: motion.FrameGeometry(R.identity(), np.array(p, float), np.zeros(3), np.zeros(3), 1.0)
        main = [geo([0, 0, 5.0])] * 5
        other = [geo([0.1 * k, 0, 5.0]) for k in range(5)]
        other[2] = None
        offset, both = motion.person_offsets(main, other, 10, 0)
        self.assertFalse(both[2])
        self.assertAlmostEqual(offset[2, 0], 0.2, places=6)



class StepTest(unittest.TestCase):
    def test_planted_feet_do_not_slide_and_follow_true_path(self):
        # 真实走位：每帧往 +x 走 0.05 米；左脚 0~9 帧踩着、右脚 10~19 帧踩着；识别估的位置有 ±10 厘米抖动
        n, rng = 20, np.random.default_rng(0)
        true = np.c_[0.05 * np.arange(n), np.zeros(n)]
        left_world, right_world = np.array([0.3, 0.1]), np.array([0.8, -0.1])
        feet = {"j_asi_d_l": np.zeros((n, 3)), "j_asi_d_r": np.zeros((n, 3))}
        for k in range(n):
            feet["j_asi_d_l"][k, [0, 2]] = left_world - true[k]
            feet["j_asi_d_r"][k, [0, 2]] = right_world - true[k]
        contacts = {"j_asi_d_l": np.arange(n) < 11, "j_asi_d_r": np.arange(n) >= 10}   # 第 10 帧双脚着地
        base = true + rng.normal(0, 0.1, (n, 2))
        yaw = np.zeros(n)
        out = motion.step_odometry(base, yaw, feet, contacts)
        self.assertGreater(motion.contact_slide(base, yaw, feet, contacts, 10), 0.5)
        self.assertLess(motion.contact_slide(out, yaw, feet, contacts, 10), 1e-6)
        # 和真实走位只差一个常数（起点的抖动）：形状对了
        diff = out - true
        np.testing.assert_allclose(diff, np.tile(diff[0], (n, 1)), atol=0.05)

    def test_contact_from_2d_keypoints(self):
        import sys
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
        from posegraph import video

        n = 8
        humans = []
        for k in range(n):
            kp = np.zeros((70, 2))
            kp[17] = [500, 900]                 # 左脚跟一直不动：踩着
            kp[20] = [600 + 40 * k, 900]        # 右脚跟每帧挪 40 像素：在迈步
            humans.append({"keypoints_2d": kp, "focal_length": 1000.0, "cam_t": np.array([0, 0, 5.0]), "bbox": [400, 100, 700, 950],
                           **{key: np.zeros((2, 3)) for key in ("joint_global_rots", "joint_coords", "rest_joint_coords", "rest_joint_rots")}})
        raw = video.Raw(10.0, 0.0, [k / 10 for k in range(n)], (1280, 1000), np.tile([0, 0, 0, 1.0], (n, 1)), [humans], [None], [[None] * n])
        feet = {"j_asi_d_l": np.zeros((n, 3)), "j_asi_d_r": np.zeros((n, 3))}
        c = motion.foot_contacts(raw, 0, feet, 10)
        self.assertTrue(c["j_asi_d_l"][1:-1].all())
        self.assertFalse(c["j_asi_d_r"].any())


if __name__ == "__main__":
    unittest.main()
