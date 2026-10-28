"""face_rig：照片表情 → 脸部骨骼（几何驱动 + 游戏实测限度）。用合成的小面部骨架和动画，不依赖游戏导出的数据。"""
import gzip
import json
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import face_geom, face_rig
from posegraph.config import settings
from posegraph.retarget.brio_pose import BrioPose

BONES = ["j_kao", "j_f_face", "j_f_eye_l", "j_f_eye_r", "j_f_mab_l", "j_f_mab_r", "j_f_hana_l", "j_f_hana_r", "j_f_uhana",
         "j_f_ago", "j_f_mabup_01_l", "j_f_mabup_01_r", "j_f_uslip_l", "j_f_uslip_r", "j_f_dlip_01_l"]
PARENTS = [-1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 4, 5, 1, 1, 9]
OFFSETS = {"j_kao": (0, 1.6, 0.04), "j_f_face": (0, 0, 0), "j_f_eye_l": (0.03, 0.03, 0.08), "j_f_eye_r": (-0.03, 0.03, 0.08),
           "j_f_mab_l": (0.03, 0.03, 0.08), "j_f_mab_r": (-0.03, 0.03, 0.08), "j_f_hana_l": (0.01, -0.01, 0.1),
           "j_f_hana_r": (-0.01, -0.01, 0.1), "j_f_uhana": (0, -0.02, 0.1), "j_f_ago": (0, -0.03, 0.02),
           "j_f_mabup_01_l": (0, 0.005, 0), "j_f_mabup_01_r": (0, 0.005, 0), "j_f_uslip_l": (0.025, -0.05, 0.08),
           "j_f_uslip_r": (-0.025, -0.05, 0.08), "j_f_dlip_01_l": (0.01, -0.03, 0.06)}


# 下巴骨骼的朝向同真实骨架：局部 z 轴朝侧面（绕 z 转 = 张合，负 = 张开）
JAW_ROT = R.from_euler("y", -90, degrees=True)


def reference(scale=1.0):
    rot = lambda b: list(JAW_ROT.as_quat()) if b == "j_f_ago" else [0, 0, 0, 1]
    return [[*(np.array(OFFSETS[b]) * (scale if b != "j_kao" else 1)), *rot(b), 1] for b in BONES]


def frame(changes, ref):
    """把 ref 里某些骨骼绕局部 axis 转若干度、平移若干米，返回只含这些骨骼的帧。"""
    idx, out = [], []
    for b, (axis, deg, move) in changes.items():
        t = list(ref[BONES.index(b)])
        t[3:7] = list((R.from_quat(t[3:7]) * R.from_euler(axis, deg, degrees=True)).as_quat())
        t[0:3] = list(np.array(t[0:3]) + move)
        idx.append(BONES.index(b))
        out.append(t)
    return idx, [out]


def write_dump(folder: Path, name: str, scale: float):
    ref = reference(scale)
    anims = {}
    for anim, changes in {
        "close": {"j_f_mabup_01_l": ("z", -30, 0), "j_f_mabup_01_r": ("z", -30, 0)},
        "wide": {"j_f_mabup_01_l": ("z", 8, 0), "j_f_mabup_01_r": ("z", 8, 0)},
        "open": {"j_f_ago": ("z", -18, 0)},
        "smile": {"j_f_uslip_l": ("x", 5, np.array([0, 0.006, 0])), "j_f_uslip_r": ("x", 5, np.array([0, 0.006, 0]))},
    }.items():
        idx, frames = frame(changes, ref)
        anims[anim] = {"blend": 1, "duration": 0.03, "fps": 5, "bones": idx, "frames": frames}
    with gzip.open(folder / f"{name}.json.gz", "wt", encoding="utf-8") as f:
        json.dump({"bones": BONES, "parents": PARENTS, "reference": ref, "anims": anims}, f)


def pose_from(skeleton_scale: float) -> BrioPose:
    """角色当前姿势（模型空间）：头在 (1, 0.9, 2)、朝向转 90°，脸按 skeleton_scale 那套骨架摆。"""
    hp, hq = np.array([1.0, 0.9, 2.0]), R.from_euler("y", 90, degrees=True)
    fmt = lambda v: ", ".join(str(float(x)) for x in v)
    bones = {}
    for b in BONES:
        p = np.array(OFFSETS[b]) * skeleton_scale if b != "j_kao" else np.zeros(3)
        local = JAW_ROT if b == "j_f_ago" else R.identity()
        bones[b] = {"Position": fmt(hp + hq.apply(p)), "Rotation": fmt((hq * local).as_quat()), "Scale": "1, 1, 1"}
    return BrioPose({"Bones": bones})


def neutral_measures(**changes) -> dict:
    """中性脸的测量值，再改几项。"""
    m = {f"eye_open_{s}": 1.0 for s in "lr"}
    m.update({f"lower_lid_{s}": 0.0 for s in "lr"})
    m.update(jaw_open=0.0)
    m.update(changes)
    return m


class HeadFromFaceTest(unittest.TestCase):
    def human(self, head_rot):
        from posegraph.retarget.mhr import MHR_JOINT_NAMES
        n = len(MHR_JOINT_NAMES)
        rots = np.tile(np.eye(3), (n, 1, 1))
        rots[MHR_JOINT_NAMES.index("c_head")] = head_rot
        return {"joint_global_rots": rots, "rest_joint_rots": np.tile(np.eye(3), (n, 1, 1)), "focal_length": np.array([1000.0])}

    def result(self, rot, center=(500, 500)):
        from posegraph.face import FaceResult
        pts = np.tile([*center, 0.0], (468, 1))
        return FaceResult(True, 1.0, True, landmarks=pts.tolist(), rotation=np.asarray(rot).tolist())

    def test_head_moves_toward_the_face_by_the_weight(self):
        from posegraph import face
        from posegraph.retarget.mhr import MHR_JOINT_NAMES
        k = MHR_JOINT_NAMES.index("c_head")
        face_rot = R.from_euler("x", 20, degrees=True).as_matrix()     # 脸在画面正中，低头 20°；SAM 认为没低头
        h = self.human(np.eye(3))
        out, diff = face.head_from_face(h, self.result(face_rot), (1000, 1000), 1.0, 0.5)
        self.assertAlmostEqual(diff, 20, places=3)
        self.assertAlmostEqual(np.degrees(R.from_matrix(out["joint_global_rots"][k]).magnitude()), 10, places=3)
        self.assertIs(h["joint_global_rots"][k].tolist() == np.eye(3).tolist(), True)   # 原数据没被改
        out, _ = face.head_from_face(h, self.result(face_rot), (1000, 1000), 1.0, 1.0)
        np.testing.assert_allclose(out["joint_global_rots"][k], face_rot, atol=1e-9)

    def test_face_off_center_adds_the_view_ray(self):
        from posegraph import face
        from posegraph.retarget.mhr import MHR_JOINT_NAMES
        k = MHR_JOINT_NAMES.index("c_head")
        # 脸在画面右边：裁剪图里看是正脸，其实是相机斜着看过去 → 头朝向要往右偏（绕 y 轴负向，OpenGL）
        out, diff = face.head_from_face(self.human(np.eye(3)), self.result(np.eye(3), (1000, 500)), (1000, 1000), 1.0, 1.0)
        expected = np.degrees(np.arctan(500 / 1000))
        self.assertAlmostEqual(diff, expected, places=3)
        self.assertLess(R.from_matrix(out["joint_global_rots"][k]).as_rotvec()[1], 0)

    def test_way_off_face_is_ignored(self):
        from posegraph import face
        h = self.human(np.eye(3))
        out, diff = face.head_from_face(h, self.result(R.from_euler("y", 80, degrees=True).as_matrix()), (1000, 1000), 1.0, 1.0)
        self.assertIs(out, h)
        self.assertGreater(diff, 45)


class FaceRigTest(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        folder = Path(self.dir.name)
        write_dump(folder, "c0101_f0002", 1.0)
        write_dump(folder, "c0101_f0003", 1.3)   # 另一套：脸大三成
        self.saved = settings.face_dump_dir
        object.__setattr__(settings, "face_dump_dir", folder)
        face_rig._load.cache_clear()
        self.sk = face_rig.match("c0101", None)

    def tearDown(self):
        object.__setattr__(settings, "face_dump_dir", self.saved)
        face_rig._load.cache_clear()
        self.dir.cleanup()

    def angle(self, q, bone, axis=2):
        i = self.sk.index[bone]
        return np.degrees((R.from_quat(self.sk.ref_q[i]).inv() * R.from_quat(q[i])).as_rotvec())[axis]

    def test_matches_the_skeleton_the_character_uses(self):
        self.assertEqual(face_rig.match("c0101", pose_from(1.3)).name, "c0101_f0003")
        self.assertEqual(face_rig.match("c0101", pose_from(1.0)).name, "c0101_f0002")
        self.assertIsNone(face_rig.match("c0201", None))

    def test_limits_come_from_the_animations(self):
        i = self.sk.index["j_f_mabup_01_l"]
        self.assertAlmostEqual(np.degrees(self.sk.rot_lo[i, 2]), -30, places=3)
        self.assertAlmostEqual(np.degrees(self.sk.rot_hi[i, 2]), 8, places=3)
        j = self.sk.index["j_f_uslip_l"]
        self.assertAlmostEqual(self.sk.tr_hi[j, 1], 0.006, places=6)

    def test_neutral_measures_keep_the_neutral_face(self):
        t, q, s = face_rig.solve(self.sk, neutral_measures())
        np.testing.assert_allclose(t, self.sk.ref_t, atol=1e-9)
        for b in ("j_f_mabup_01_l", "j_f_ago"):
            self.assertAlmostEqual(self.angle(q, b), 0, places=6)

    def test_one_eye_closes_only_its_own_lid_and_stops_at_the_game_limit(self):
        t, q, s = face_rig.solve(self.sk, neutral_measures(eye_open_l=0.0))
        self.assertAlmostEqual(self.angle(q, "j_f_mabup_01_l"), -30, places=3)
        self.assertAlmostEqual(self.angle(q, "j_f_mabup_01_r"), 0, places=3)
        # 强度加倍也不会超过游戏里眼睑转得最多的 -30°
        t, q, s = face_rig.solve(self.sk, neutral_measures(eye_open_l=0.0), strength=2.0)
        self.assertAlmostEqual(self.angle(q, "j_f_mabup_01_l"), -30, places=3)

    def test_blink_score_closes_an_eye_the_geometry_missed(self):
        t, q, s = face_rig.solve(self.sk, neutral_measures(), blendshapes={"eyeBlinkRight": 0.9})
        self.assertAlmostEqual(self.angle(q, "j_f_mabup_01_r"), -30, places=3)
        self.assertAlmostEqual(self.angle(q, "j_f_mabup_01_l"), 0, places=3)

    def test_jaw_opens_in_proportion_and_clamps(self):
        t, q, s = face_rig.solve(self.sk, neutral_measures(jaw_open=0.2))
        partial = self.angle(q, "j_f_ago")
        self.assertLess(partial, -0.5)
        self.assertGreater(partial, -18)
        t, q, s = face_rig.solve(self.sk, neutral_measures(jaw_open=50.0))
        self.assertAlmostEqual(self.angle(q, "j_f_ago"), -18, places=3)

    def test_parted_lips_open_the_jaw_even_if_the_chin_measures_short(self):
        t, q, s = face_rig.solve(self.sk, neutral_measures(jaw_open=-0.5, lips_apart=1.0))
        self.assertLess(self.angle(q, "j_f_ago"), -0.5)

    def test_mouth_corner_follows_its_landmark_one_side_only(self):
        aligned = face_geom.neutral().copy()
        mapping, to_game, cm_to_m, place = self.sk.follow_map()
        vl = mapping[self.sk.index["j_f_uslip_l"]]
        aligned[vl, 1] += 0.3                      # 被拍者的左嘴角上提 3mm（标准脸厘米）
        t, q, s = face_rig.solve(self.sk, neutral_measures(_aligned=aligned))
        pos0, _ = self.sk.fk(self.sk.ref_t, self.sk.ref_q)
        pos1, _ = self.sk.fk(t, q)
        left, right = self.sk.index["j_f_uslip_l"], self.sk.index["j_f_uslip_r"]
        self.assertGreater(pos1[left][1] - pos0[left][1], 0.001)
        self.assertAlmostEqual(pos1[right][1] - pos0[right][1], 0, places=6)
        # 上提很多也只到游戏里嘴角最多上提的 6mm
        aligned[vl, 1] += 5.0
        t, q, s = face_rig.solve(self.sk, neutral_measures(_aligned=aligned))
        self.assertLessEqual(t[left][1] - self.sk.ref_t[left][1], 0.006 + 1e-9)

    def test_group_gain_scales_only_its_group(self):
        t, q, s = face_rig.solve(self.sk, neutral_measures(eye_open_l=0.5, jaw_open=0.2), gains={"eyes": 0.0})
        self.assertAlmostEqual(self.angle(q, "j_f_mabup_01_l"), 0, places=6)
        self.assertLess(self.angle(q, "j_f_ago"), -0.5)

    def eye_yaw(self, q, side):
        """眼球在面部模型空间里朝 +x（被拍者的左）转了多少度。"""
        _, rots = self.sk.fk(self.sk.ref_t, q)
        _, rots0 = self.sk.fk(self.sk.ref_t, self.sk.ref_q)
        z = (rots[self.sk.index[f"j_f_eye_{side}"]] * rots0[self.sk.index[f"j_f_eye_{side}"]].inv()).apply([0, 0, 1])
        return np.degrees(np.arctan2(z[0], z[2]))

    def test_gaze_follows_the_visible_eye_on_a_profile(self):
        # 脸转向被拍者的左边 60°：右眼朝镜头、左眼被挡；左眼的系数说往右看，右眼说往左看 → 以右眼为准，两眼同向
        bs = {"eyeLookInLeft": 0.8, "eyeLookInRight": 0.5}
        turned = R.from_euler("y", 60, degrees=True).as_matrix()
        t, q, s = face_rig.solve(self.sk, neutral_measures(_rotation=turned), blendshapes=bs)
        self.assertGreater(self.eye_yaw(q, "r"), 5)
        self.assertAlmostEqual(self.eye_yaw(q, "l"), self.eye_yaw(q, "r"), places=3)
        # 视线权重 0：不转眼
        t, q, s = face_rig.solve(self.sk, neutral_measures(_rotation=turned), blendshapes=bs, gains={"gaze": 0})
        self.assertAlmostEqual(self.eye_yaw(q, "r"), 0, places=3)

    def test_neutral_face_sits_on_the_head(self):
        ref = pose_from(1.0)
        head = ref.get("j_kao")
        bones, name, _ = face_rig.face_bones("c0101", ref, (head.position, head.rotation, head.scale), None)
        self.assertEqual(name, "c0101_f0002")
        for b in ("j_f_eye_l", "j_f_ago", "j_f_uslip_r"):
            np.testing.assert_allclose(bones[b][0], ref.get(b).position, atol=1e-6)
        self.assertNotIn("j_kao", bones)

    def test_pose_json_includes_face_bones(self):
        from posegraph.retarget.ffxiv import RetargetResult, to_brio_pose_json
        rt = RetargetResult(bones={"j_kao": (np.zeros(3), np.array([0, 0, 0, 1.0]), np.ones(3))}, segment_error_deg={})
        extra = {"j_f_ago": (np.array([0, -0.07, 0.05]), np.array([0, 0, 0, 1.0]), np.ones(3))}
        doc = json.loads(to_brio_pose_json(rt, extra))
        self.assertIn("j_f_ago", doc["Bones"])
        self.assertIn("j_kao", doc["Bones"])


if __name__ == "__main__":
    unittest.main()
