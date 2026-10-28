"""镜头求解（camera.solve_gpose_camera）：模拟一个按游戏内校准规律工作的 GPose 相机，检查几次设置能到位。"""
import sys
import unittest
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import camera

PIVOT = np.array([10.0, 1.6, -4.0])


class FakeGposeCamera:
    """位置 = 轴心 + Distance × 方向(Angle.x, -Angle.y)；基准视线比正对轴心低一点（随距离变），
    Pan.x / Roll 精确、Pan.y 略不对称；垂直视角 = 0.76 + Zoom，下限 0.22。读回立即生效（不模拟平滑过渡）。"""

    def __init__(self):
        self.p = {"ax": 2.4, "ay": 0.1, "dist": 2.75, "px": 0.0, "py": 0.0, "roll": 0.0, "zoom": 0.0}
        self.sets = 0

    def set_camera(self, angle, distance, pan, roll, zoom, delimit=True):
        self.p.update(ax=angle[0], ay=angle[1], dist=distance, px=pan[0], py=pan[1], roll=roll, zoom=zoom)
        self.sets += 1

    def get_camera(self):
        p = self.p
        tilt = -0.05 - 0.15 / p["dist"]
        pan_pitch = p["py"] * (1.0 if p["py"] > 0 else 0.93)
        rot = R.from_euler("YXZ", [p["ax"] - p["px"], -p["ay"] + tilt + pan_pitch, -p["roll"]]).as_matrix()
        view = np.eye(4)
        view[:3, :3] = rot
        fov = max(0.76 + p["zoom"], 0.22)
        proj = np.zeros((4, 4))
        proj[1][1] = 1 / np.tan(fov / 2)
        proj[0][0] = proj[1][1] / 1.7
        position = PIVOT + p["dist"] * camera._direction(p["ax"], -p["ay"])
        return {"camera": {"angle": [p["ax"], p["ay"]], "distance": p["dist"], "pan": [p["px"], p["py"]], "roll": p["roll"],
                           "zoom": p["zoom"], "position": position.tolist(), "view": view.tolist(), "projection": proj.tolist()}}


class SmoothLowFpsCamera(FakeGposeCamera):
    """游戏帧率低：每读 3 次才过一帧；每帧相机向设定值靠近 35%（平滑过渡）。读数带帧号。"""

    def __init__(self):
        super().__init__()
        self.goal = dict(self.p)
        self.frame = 0
        self.reads = 0

    def set_camera(self, angle, distance, pan, roll, zoom, delimit=True):
        self.goal.update(ax=angle[0], ay=angle[1], dist=distance, px=pan[0], py=pan[1], roll=roll, zoom=zoom)
        self.sets += 1

    def get_camera(self):
        self.reads += 1
        if self.reads % 3 == 0:
            self.frame += 1
            for k, v in self.goal.items():
                self.p[k] += (v - self.p[k]) * (1.0 if abs(v - self.p[k]) < 1e-5 else 0.35)
        return {**super().get_camera(), "frame": self.frame}


class CameraSolverTests(unittest.TestCase):
    def setUp(self):
        # 不真的等：设置立即生效
        self.sleep = camera.time.sleep
        camera.time.sleep = lambda _: None
        self.addCleanup(setattr, camera.time, "sleep", self.sleep)

    def test_reaches_the_photo_camera_in_a_few_moves(self):
        rng = np.random.default_rng(3)
        for _ in range(30):
            cam = FakeGposeCamera()
            position = PIVOT + rng.uniform(1.5, 12) * camera._direction(rng.uniform(-np.pi, np.pi), rng.uniform(-1.1, 1.1))
            aim = PIVOT + rng.normal(0, 0.3, 3)
            forward = (aim - position) / np.linalg.norm(aim - position)
            up = np.cross(np.cross(forward, [0, 1, 0]), forward)
            up = R.from_rotvec(forward * rng.normal(0, 0.15)).apply(up / np.linalg.norm(up))
            target = camera.CameraTarget(position, forward, up, float(np.degrees(rng.uniform(0.3, 1.2))))
            r = camera.solve_gpose_camera(cam, target, log=lambda *_: None)
            self.assertLessEqual(r["moves"], 4)
            self.assertLess(r["position_error_cm"], 0.5)
            self.assertLess(r["forward_error_deg"], 0.2)
            self.assertLess(r["up_error_deg"], 0.2)
            self.assertLess(abs(r["fov_deg"] - r["target_fov_deg"]), 0.3)

    def test_camera_starting_straight_above_still_lands(self):
        cam = FakeGposeCamera()
        cam.p.update(ay=-1.5, dist=6.0)
        target = camera.CameraTarget(PIVOT + 4 * camera._direction(0.5, 0.2), -camera._direction(0.5, 0.2), np.array([0, 1.0, 0]), 40.0)
        r = camera.solve_gpose_camera(cam, target, log=lambda *_: None)
        self.assertLess(r["position_error_cm"], 0.5)
        self.assertLess(r["forward_error_deg"], 0.2)

    def test_low_frame_rate_does_not_stop_mid_transition(self):
        # 帧率低时同一帧会被连读几次；按帧号判断停稳，过渡中途不会被当成到位
        cam = SmoothLowFpsCamera()
        target = camera.CameraTarget(PIVOT + 5 * camera._direction(-1.0, 0.3), -camera._direction(-1.0, 0.3), np.array([0, 1.0, 0]), 35.0)
        r = camera.solve_gpose_camera(cam, target, log=lambda *_: None)
        self.assertLess(r["position_error_cm"], 0.5)
        self.assertLess(r["forward_error_deg"], 0.2)

    def test_wide_photo_fits_by_width(self):
        tall = camera.CameraTarget(PIVOT, np.array([0, 0, 1.0]), np.array([0, 1.0, 0]), 40.0, aspect=535 / 758)
        self.assertAlmostEqual(np.degrees(camera.game_fov_y(tall, 16 / 9)), 40.0)     # 竖构图：上下对齐
        wide = camera.CameraTarget(PIVOT, np.array([0, 0, 1.0]), np.array([0, 1.0, 0]), 30.0, aspect=21 / 9)
        fov = camera.game_fov_y(wide, 16 / 9)
        # 左右对齐：游戏画面的水平视角 = 照片的水平视角
        photo_h = 2 * np.arctan(np.tan(np.radians(30.0) / 2) * 21 / 9)
        self.assertAlmostEqual(2 * np.arctan(np.tan(fov / 2) * 16 / 9), photo_h)


if __name__ == "__main__":
    unittest.main()
