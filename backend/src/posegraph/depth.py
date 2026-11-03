"""
照片的深度（MoGe-2，合并模式识别前先跑一次）。深度不直接改姿势，只帮 SAM 3D Body 识别：
- 镜头：MoGe 估的焦距交给 SAM（cam_int）。手机广角照片 SAM 自己按图片尺寸假设的视场角会差很多
- 修遮罩：几个人的网格叠在同一像素时，这个像素归「网格深度和照片深度最接近」的那个人（交叉处谁在前由深度决定）
- 远近：每个人看得见的那面身体，深度中位数对上照片深度；整个人以镜头为中心等比缩放（照片上的投影不变）
- 地面：不是人的像素里找最大的「朝上」平面（够大、人没沉到下面才算），给合并场景的重力方向和「都在地上」
所有深度都换算到 SAM 网格的尺度上（MoGe 的米和 SAM 按体型推出的米差一个整体比例，按看得见的顶点取中位比）。
"""

from __future__ import annotations

import sys
import threading
from dataclasses import dataclass

import numpy as np

from .config import settings

FLOOR_MAX_TORSO_DEG = 45.0  # 按这块面当地面时，人的躯干（髋 → 肩）离「上」不能超过这么多度（实测站着 2~4°、深蹲约 20°；
                            # 误认的面 43~85°）。弯腰、趴下的人会找不到地面：不扶正总比扶错好
FLOOR_BODY_HEIGHT = 0.8     # 人要「立」在面上：身体最高处离这块面至少这么高（米；深蹲约 0.9m、坐地上也有 0.9m 左右，
                            # 背景布、墙、飞扑时脚边的树丛只有 0.2~0.7m）
FLOOR_MIN_SHARE = 0.015     # 地面至少占这么多像素（低分辨率深度图里地面会缩小；有身体高度、躯干检查兜着，可以放宽）
ON_FLOOR = 0.15             # 每个人身体最低处离地面不到这么多（米）算「在地上」
# MoGe 的分辨率（ViT 块数，模型默认最高 3600）。2026-10-29 用 30 张单人照对比 3600 / 1500 / 1200：
# 1200 块时找到的地面和 3600 完全一样，视场角差中位 1.5°、人离镜头的距离差中位 2.7%、关节角度差中位 0.7°；
# 4070 上 0.10 → 0.04 秒，额外显存约 1030 → 350MB
NUM_TOKENS = 1200
MASK_RERUN = 0.05           # 深度修遮罩后，有人的遮罩变了这么多（占他原遮罩的比例）才重跑一遍 SAM

_lock = threading.Lock()
_model = None
_model_device = None


@dataclass
class PhotoDepth:
    focal: float               # 像素焦距（主点取画面中心）
    depth: np.ndarray          # (高, 宽) 米，MoGe 尺度；无效处为 nan
    points: np.ndarray         # (高, 宽, 3) 相机系（x 右、y 下、z 前）
    valid: np.ndarray          # (高, 宽) bool


def available() -> bool:
    try:
        import moge  # noqa: F401
    except ImportError:
        return False
    return settings.moge_model.is_file()


def estimate(rgb: np.ndarray, device: str) -> PhotoDepth:
    """跑 MoGe-2。模型第一次用时加载，换设备时重载；和 SAM 3D Body 一起在 Estimator.unload 时释放（unload）。"""
    import torch

    if device == "cuda" and sys.platform == "win32" and getattr(torch.version, "hip", None) and hasattr(torch, "backends"):
        torch.backends.cudnn.enabled = False

    global _model, _model_device
    with _lock:
        if _model is None or _model_device != device:
            from moge.model.v2 import MoGeModel

            _model = MoGeModel.from_pretrained(str(settings.moge_model)).to(device).eval()
            _model_device = device
        H, W = rgb.shape[:2]
        with torch.no_grad():
            # 半精度只在显卡上快；CPU 上开着会慢几十倍
            out = _model.infer(torch.tensor(rgb / 255.0, dtype=torch.float32, device=device).permute(2, 0, 1),
                               num_tokens=NUM_TOKENS, use_fp16=device != "cpu")
        K = out["intrinsics"].float().cpu().numpy()
        depth = out["depth"].float().cpu().numpy()
        valid = out["mask"].cpu().numpy().astype(bool) & np.isfinite(depth)
        return PhotoDepth(float(K[1, 1] * H), depth, out["points"].float().cpu().numpy(), valid)


def unload() -> None:
    global _model, _model_device
    with _lock:
        _model = _model_device = None


def camera_intrinsics(focal: float, size: tuple[int, int]):
    """SAM 3D Body 的 cam_int：(1, 3, 3)，fx = fy = 焦距，主点在画面中心（和后端其他地方一致）。"""
    import torch

    w, h = size
    return torch.tensor([[[focal, 0, w / 2], [0, focal, h / 2], [0, 0, 1]]], dtype=torch.float32)


# ---------------------------------------------------------------- 网格投影

def _project(v: np.ndarray, focal: float, size) -> np.ndarray:
    w, h = size
    return np.stack([focal * v[:, 0] / v[:, 2] + w / 2, focal * v[:, 1] / v[:, 2] + h / 2], 1)


def mesh_depth(vertices_cam: np.ndarray, faces: np.ndarray, focal: float, size) -> np.ndarray:
    """网格（相机系，已加 cam_t）光栅化成深度图（远处 / 没覆盖为 inf）。三角形按深度从远到近画。"""
    import cv2

    w, h = size
    uv = _project(vertices_cam, focal, size)
    tz = vertices_cam[faces, 2].mean(1)
    d = np.full((h, w), np.inf, np.float32)
    pts = np.round(uv[faces] * 4).astype(np.int32)
    for t in np.argsort(-tz):
        cv2.fillConvexPoly(d, pts[t], float(tz[t]), lineType=cv2.LINE_8, shift=2)
    return d


def _visible(vertices_cam: np.ndarray, own_depth: np.ndarray, own_mask: np.ndarray, photo: PhotoDepth, size):
    """看得见（不被自己挡住）、落在自己遮罩里、照片深度有效的顶点：(顶点序号, 像素 x, 像素 y)。"""
    w, h = size
    uv = _project(vertices_cam, photo.focal, size)
    ix, iy = uv[:, 0].astype(int), uv[:, 1].astype(int)
    inside = (ix >= 0) & (ix < w) & (iy >= 0) & (iy < h) & (vertices_cam[:, 2] > 0)
    ix, iy = np.clip(ix, 0, w - 1), np.clip(iy, 0, h - 1)
    ok = inside & (vertices_cam[:, 2] <= own_depth[iy, ix] + 0.02) & own_mask[iy, ix] & photo.valid[iy, ix]
    idx = np.nonzero(ok)[0]
    return idx, ix[idx], iy[idx]


def depth_scale(meshes: list[np.ndarray], masks: np.ndarray, photo: PhotoDepth, faces: np.ndarray, size) -> float:
    """照片深度 → SAM 网格尺度的整体比例（看得见的顶点上 网格深度 / 照片深度 的中位数）。"""
    ratios = []
    for v, m in zip(meshes, masks):
        own = mesh_depth(v, faces, photo.focal, size)
        idx, ix, iy = _visible(v[::2], own, m, photo, size)
        ratios.append(v[::2][idx, 2] / photo.depth[iy, ix])
    r = np.concatenate(ratios) if ratios else np.zeros(0)
    return float(np.median(r)) if len(r) else 1.0


def refine_masks(meshes: list[np.ndarray], masks: np.ndarray, photo: PhotoDepth, scale: float, faces: np.ndarray, size) -> np.ndarray:
    """几个人的网格叠在同一像素时，这个像素归网格深度离照片深度最近的人；只动原遮罩并集里的像素。"""
    Z = np.stack([mesh_depth(v, faces, photo.focal, size) for v in meshes])
    covered = np.isfinite(Z)
    err = np.where(covered, np.abs(Z - scale * np.nan_to_num(photo.depth)[None]), np.inf)
    best = err.argmin(0)
    change = masks.any(0) & (covered.sum(0) >= 2) & photo.valid
    out = masks.copy()
    for k in range(len(meshes)):
        out[k][change] = best[change] == k
    return out


def masks_changed(before: np.ndarray, after: np.ndarray, share: float = MASK_RERUN) -> bool:
    """修过的遮罩值不值得重跑 SAM：有一个人变了的像素超过他原遮罩的 share 就算。边缘抖几个像素不重跑。"""
    return any((b ^ a).sum() > share * max(int(b.sum()), 1) for b, a in zip(before, after))


def distance_ratios(meshes: list[np.ndarray], masks: np.ndarray, photo: PhotoDepth, scale: float, faces: np.ndarray, size) -> list[float]:
    """每个人该整体缩放多少，看得见那面身体的深度中位数才对上照片深度（1 = 不动）。"""
    out = []
    for v, m in zip(meshes, masks):
        own = mesh_depth(v, faces, photo.focal, size)
        idx, ix, iy = _visible(v[::2], own, m, photo, size)
        out.append(float(np.median(scale * photo.depth[iy, ix]) / np.median(v[::2][idx, 2])) if len(idx) >= 50 else 1.0)
    return out


# ---------------------------------------------------------------- 地面

def _planes(sample: np.ndarray, up: np.ndarray, count: int, iters: int, tol: float, rng):
    """依次找最大的几个平面（每找到一个就把它的点去掉），法线都朝镜头这边的「上」。返回 [(法线, d, 内点数)]。"""
    near = lambda q, n, d: np.abs(q @ n + d) < tol * np.maximum(1, q[:, 2] / 3)
    out, rest = [], sample
    for _ in range(count):
        if len(rest) < 300:
            break
        best, plane = 0, None
        for _ in range(iters):
            a, b, c = rest[rng.choice(len(rest), 3, replace=False)]
            n = np.cross(b - a, c - a)
            if np.linalg.norm(n) < 1e-9:
                continue
            n = n / np.linalg.norm(n)
            n = n if n @ up > 0 else -n
            k = int(near(rest, n, -n @ a).sum())
            if k > best:
                best, plane = k, (n, -n @ a)
        if plane is None:
            break
        q = rest[near(rest, *plane)]
        c = q.mean(0)
        n = np.linalg.svd(q - c, full_matrices=False)[2][-1]   # 只要法线：别算 N×N 的 U（内点多时会爆内存）
        n = n if n @ up > 0 else -n
        d = float(-n @ c)
        if d > 0.1:   # 镜头在它上面（法线朝上那一侧）才可能是地面；照片里看得到的墙、背景按「朝上」定向后镜头往往在背面
            out.append((n, d, len(q)))
        rest = rest[~near(rest, n, d)]
    return out


def find_floor(photo: PhotoDepth, people: np.ndarray, feet: list[tuple[int, int]] | None = None,
               torsos: list[np.ndarray] | None = None, bodies: list[np.ndarray] | None = None, scale: float = 1.0,
               iters: int = 400, tol: float = 0.03, samples: int = 60000):
    """
    地面。返回 (法线, d)（相机系、MoGe 尺度，n·p + d = 0，法线朝上）；没有为 None。
    只在看得见脚时找：在不是人的像素里找最大的几个平面，取脚踩着的那个（脚上的点离它最近、而且不到 15cm）——
    俯拍时地面的法线会朝镜头倒过来很多，墙反而更像「朝上」，只看朝向会把墙当成地面。
    再要求人是「立」在这块面上的：按它当地面时躯干离「上」不超过 FLOOR_MAX_TORSO_DEG，身体最高处离它至少
    FLOOR_BODY_HEIGHT（影棚的背景布弯到地上、人贴墙站时，脚边也有一块离脚很近的面，但人是靠在它前面、不是站在它上面）。
    看不见脚（半身照、特写）时不找：2026-10-29 用 30 张单人照对比，那时找到的「地面」都是背景墙、衣服、船舷，
    会把人扳歪 17~68°。
    feet：脚（踝 / 脚跟 / 脚尖）在深度图上的像素；torsos：每个人躯干（髋 → 肩）的方向（相机系）；
    bodies：每个人的网格顶点（相机系，和深度图同一尺度），给了才检查身体高度；scale：深度图 → 识别模型的米（depth_scale），
    身体高度按识别模型的尺度量（人的大小它估得准，深度图的绝对尺度每张图偏差不同）。
    """
    P = photo.points
    foot = np.array([P[y, x] for x, y in (feet or []) if photo.valid[y, x] and np.isfinite(P[y, x]).all()])
    if len(foot) < 2:
        return None
    valid = photo.valid & ~people.any(0) & np.isfinite(P).all(-1)
    pts = P[valid]
    if len(pts) < 500:
        return None
    rng = np.random.default_rng(0)
    sample = pts[rng.choice(len(pts), min(len(pts), samples), replace=False)]
    up = np.array([0.0, -1.0, 0.0])
    total = P.shape[0] * P.shape[1]
    best = None
    for n, d, k in _planes(sample, up, 4, iters, tol, rng):
        dist = float(np.min(np.abs(foot @ n + d)))   # 离得最近的脚点（通常是脚尖）；踝、脚跟的点常落在裤腿上，离地二三十厘米
        share = k / len(sample) * len(pts) / total
        tilt = float(np.mean([np.degrees(np.arccos(np.clip(t @ n / (np.linalg.norm(t) + 1e-9), -1, 1))) for t in torsos])) if torsos else 0.0
        tall = max(float(np.percentile(b @ n + d, 99)) for b in bodies) * scale if bodies else FLOOR_BODY_HEIGHT   # 多人时有一个人立在上面就行
        # 脚踩得到、人立得住的平面里取最大的（脚边常有小块台阶、地毯边之类的小平面）
        if share >= FLOOR_MIN_SHARE and dist < 0.15 and tilt < FLOOR_MAX_TORSO_DEG and tall >= FLOOR_BODY_HEIGHT \
                and (best is None or share > best[0]):
            best = (share, n, d)
    return None if best is None else (best[1], best[2])
