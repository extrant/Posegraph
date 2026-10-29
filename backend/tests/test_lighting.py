"""照片估光（lighting.py）：用已知的灯把典型中性脸打光画成照片，再估回来。"""
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import face_geom, lighting

try:
    import mediapipe  # noqa: F401
    HAS_MEDIAPIPE = True
except ImportError:
    HAS_MEDIAPIPE = False

SIZE = 400


def landmarks(scale: float = 9.0) -> np.ndarray:
    """中性脸（厘米，x 右 y 上 z 朝镜头外）→ 照片像素（x 右 y 下，z 越小越近），补到 478 个点。"""
    v = face_geom.neutral()
    p = np.stack([SIZE / 2 + v[:, 0] * scale, SIZE / 2 - v[:, 1] * scale, -v[:, 2] * scale], 1)
    return np.concatenate([p, p[:10]])


def photo(ambient: float, lights, color=(1.0, 1.0, 1.0), background=(128, 128, 128)) -> np.ndarray:
    """按 lighting 自己的光照模型把脸画成 sRGB 照片（color：灯的颜色），背景纯色。"""
    lm = landmarks()
    yy, xx, n = lighting.face_normals(lm, np.array([0.0, 0.0, 1.0]), (SIZE, SIZE))
    linear = lighting.shade(n, ambient, lights)[:, None] * lighting.SKIN * np.asarray(color) * 0.6
    srgb = np.where(linear <= 0.0031308, linear * 12.92, 1.055 * np.clip(linear, 0, 1) ** (1 / 2.4) - 0.055)
    img = np.empty((SIZE, SIZE, 3), np.uint8)
    img[:] = background
    img[yy, xx] = np.clip(srgb * 255, 0, 255).astype(np.uint8)
    return img


def estimate(img: np.ndarray) -> dict:
    return lighting.estimate(img, landmarks(), focal=1000.0, image_scale=1.0, up=[0, -1, 0], forward=[0, 0, -1])


@unittest.skipUnless(HAS_MEDIAPIPE, "mediapipe 没装")
class LightingTests(unittest.TestCase):
    def test_key_light_direction_is_recovered(self):
        d = np.array([-0.5, -0.6, -0.6])   # 相机系：画面左、上方、镜头这一侧
        d /= np.linalg.norm(d)
        r = estimate(photo(0.1, [(1.0, d, 0.0)]))
        self.assertTrue(r["found"])
        self.assertFalse(r["flat"])
        key = r["lights"][0]
        self.assertAlmostEqual(key["azimuth"], np.degrees(np.arctan2(-0.5, 0.6)), delta=8)
        self.assertAlmostEqual(key["elevation"], np.degrees(np.arcsin(d @ [0, -1, 0])), delta=8)
        self.assertGreater(r["ratio"], 2)

    def test_azimuth_sign_follows_the_face_turn(self):
        d = np.array([0.6, -0.3, -0.6])
        d /= np.linalg.norm(d)
        img = photo(0.1, [(1.0, d, 0.0)])
        right = lighting.estimate(img, landmarks(), 1000.0, 1.0, [0, -1, 0], [0.3, 0, -1])["lights"][0]["azimuth"]
        left = lighting.estimate(img, landmarks(), 1000.0, 1.0, [0, -1, 0], [-0.3, 0, -1])["lights"][0]["azimuth"]
        self.assertGreater(right, 20)
        self.assertAlmostEqual(left, -right, delta=1)

    def test_even_light_is_flat(self):
        r = estimate(photo(1.0, [(0.05, np.array([0.0, 0.0, -1.0]), 0.0)]))
        self.assertTrue(r["found"])
        self.assertTrue(r["flat"])

    def test_colored_light_and_background(self):
        d = np.array([-0.5, -0.4, -0.7])
        d /= np.linalg.norm(d)
        img = photo(0.1, [(1.0, d, 0.0)], color=(1.0, 0.15, 0.8), background=(30, 200, 230))
        yy, xx, _ = lighting.face_normals(landmarks(), np.array([0.0, 0.0, 1.0]), (SIZE, SIZE))
        bg = np.ones((SIZE, SIZE), bool)
        bg[yy, xx] = False
        r = lighting.estimate(img, landmarks(), 1000.0, 1.0, [0, -1, 0], [0, 0, -1], bg)
        key = r["lights"][0]
        self.assertTrue(key["tinted"])
        self.assertGreater(key["rgb"][2], key["rgb"][1] * 3)   # 洋红：蓝远多于绿
        self.assertTrue(r["background"]["tinted"])
        self.assertLess(r["background"]["rgb"][0], 0.1)      # 青色背景几乎没有红

    def test_neutral_light_is_not_tinted(self):
        d = np.array([0.3, -0.5, -0.8])
        d /= np.linalg.norm(d)
        r = estimate(photo(0.1, [(1.0, d, 0.0)]))
        self.assertFalse(r["lights"][0]["tinted"])
        self.assertIsNone(r["background"])

    def test_tiny_face_is_rejected(self):
        lm = landmarks(scale=0.8)
        r = lighting.estimate(photo(0.1, []), lm, 1000.0, 1.0)
        self.assertFalse(r["found"])


def body_photo(direction, size=400, focal=800.0, radius=0.4, depth=2.0, albedo=None):
    """肤色的球当「身体」：解析求每个像素的法线，按已知的灯打光；返回 (照片, 球的网格顶点和三角形)。"""
    ys, xs = np.mgrid[0:size, 0:size]
    rays = np.stack([(xs + 0.5 - size / 2) / focal, (ys + 0.5 - size / 2) / focal, np.ones_like(xs, float)], -1)
    rays /= np.linalg.norm(rays, axis=-1, keepdims=True)
    c = np.array([0.0, 0.0, depth])
    b = rays @ c
    disc = b * b - (c @ c - radius * radius)
    hit = disc > 0
    t = b - np.sqrt(np.where(hit, disc, 0))
    n = rays * t[..., None] - c
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    albedo = np.array([0.72, 0.42, 0.28]) if albedo is None else albedo   # 默认常见肤色（线性）
    linear = (0.08 + np.maximum(0, n @ direction))[..., None] * albedo * 0.8
    srgb = np.where(linear <= 0.0031308, linear * 12.92, 1.055 * np.clip(linear, 0, 1) ** (1 / 2.4) - 0.055)
    img = np.full((size, size, 3), 90, np.uint8)
    img[hit] = np.clip(srgb[hit] * 255, 0, 255).astype(np.uint8)
    th, ph = np.meshgrid(np.linspace(0, np.pi, 60), np.linspace(0, 2 * np.pi, 120), indexing="ij")
    verts = np.stack([np.sin(th) * np.cos(ph), np.cos(th), np.sin(th) * np.sin(ph)], -1).reshape(-1, 3) * radius + c
    faces = []
    for i in range(59):
        for j in range(119):
            a, bb, cc, d = i * 120 + j, i * 120 + j + 1, (i + 1) * 120 + j, (i + 1) * 120 + j + 1
            faces += [(a, cc, bb), (bb, cc, d)]
    return img, verts, np.array(faces)


def body_joints():
    """面向镜头站着：角色左边在画面右边（+x），头顶朝上（相机系 -y）。"""
    from posegraph.retarget.mhr import MHR_JOINT_NAMES
    j = np.zeros((len(MHR_JOINT_NAMES), 3))
    for name, p in {"l_uparm": (0.2, -0.2, 2.0), "r_uparm": (-0.2, -0.2, 2.0), "c_neck": (0, -0.3, 2.0),
                    "root": (0, 0.3, 2.0), "c_spine2": (0, -0.1, 2.0)}.items():
        j[MHR_JOINT_NAMES.index(name)] = p
    return j


class BodyLightingTests(unittest.TestCase):
    def test_body_skin_recovers_light(self):
        d = np.array([-0.5, -0.6, -0.6])
        d /= np.linalg.norm(d)
        img, verts, faces = body_photo(d)
        r = lighting.estimate_body(img, verts, faces, 800.0, 1.0, np.zeros((70, 3)), body_joints(), [0, -1, 0])
        self.assertTrue(r["found"], r.get("reason"))
        self.assertEqual(r["source"], "body")
        key = r["lights"][0]
        self.assertAlmostEqual(key["azimuth"], np.degrees(np.arctan2(-0.5, 0.6)), delta=10)
        self.assertAlmostEqual(key["elevation"], np.degrees(np.arcsin(0.6)), delta=10)

    def test_clothed_body_falls_back_to_coarse(self):
        """没有皮肤（整个人穿蓝衣服）：按衣服的受光强制估，方向照样估得出来。"""
        d = np.array([0.5, -0.5, -0.7])
        d /= np.linalg.norm(d)
        img, verts, faces = body_photo(d, albedo=np.array([0.05, 0.12, 0.45]))
        r = lighting.estimate_body(img, verts, faces, 800.0, 1.0, np.zeros((70, 3)), body_joints(), [0, -1, 0])
        self.assertTrue(r["found"], r.get("reason"))
        self.assertEqual(r["source"], "coarse")
        key = r["lights"][0]
        self.assertAlmostEqual(key["azimuth"], np.degrees(np.arctan2(0.5, 0.7)), delta=15)
        self.assertAlmostEqual(key["elevation"], np.degrees(np.arcsin(0.5)), delta=15)


class KelvinTests(unittest.TestCase):
    def test_round_trip(self):
        for k in (2800, 5500, 9000):
            self.assertAlmostEqual(lighting.to_kelvin(lighting.kelvin_to_rgb(k)), k, delta=200)


if __name__ == "__main__":
    unittest.main()
