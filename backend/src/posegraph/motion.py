"""视频里谁在动：解算原视频镜头的运动，分出角色在场景里的走位，再给走动模式配镜头。

识别时（和种族无关，存进 Raw）：
- CameraTracker：逐采样帧在背景上找角点（框掉画面里所有人），用光流跟到下一帧。
  有深度（MoGe）时按这一帧的深度把角点变成 3D，和下一帧的 2D 位置解 PnP，得到镜头的转动 + 平移；
  没有深度时按单应矩阵只解转动（摇镜头、俯仰、手持晃动）。深度按「人的 cam_t ÷ 这里的深度」换到 SAM 的尺度。
  跟丢（切镜、叠化、背景太单调）的那一步标成不可用，片段生成时在那里分段

片段生成时（build_clip 调 world_tracks，按种族骨长）：
- 角色走位 root：第 k 帧角色的模型系 → 第一帧角色的模型系 = 把「相机 k 系 → 模型 k 系」和镜头运动串起来，
  只留水平位移和转向（高度、俯仰交给骨骼）。同一段里镜头解算不可用的一步当作镜头没动；
  分段处（切镜）让角色位置、朝向（身体面朝的方向）接上，镜头跟着跳
- 脚底防滑：脚踩地（够低、几乎不动）的那几帧，把脚在场景里的位置钉住，差多少挪角色的根（不改腿）
- 镜头轨道（都在第一帧角色的模型系里，插件乘角色的基准变换）：
  original 原运镜（看得到人的帧 = 角色变换 × 每帧相对镜头，构图和原视频一样；看不到人的帧按镜头解算接着走）
  follow 平滑跟拍（保持开头的拍摄距离和角度，跟着角色走；角色长时间转向新的方向时慢慢绕过去）
  pan 定点摇镜（机位停在开头，镜头转着对准角色，走远了推近、走近了拉远，画面里大小不变）
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from scipy.spatial.transform import Rotation as R

_FLIP = np.array([1.0, -1.0, -1.0])   # 相机系（x 右、y 下、z 前）↔ MHR 模型系（y 上、z 朝镜头）

TRACK_WIDTH = 640           # 光流在这么宽的灰度图上做
MAX_CORNERS = 600
MIN_INLIERS = 25            # 一步至少这么多内点才信
MIN_INLIER_SHARE = 0.35     # 且内点占跟上的点至少这么多
MAX_STEP_DEG = 25.0         # 一步（采样间隔）里镜头最多转这么多，再多当解错了
MAX_STEP_METERS = 1.5       # 一步里镜头最多平移这么多（SAM 尺度，米）
PERSON_MARGIN = 0.12        # 人物框往外扩这么多再框掉（衣摆、头发、影子）
PARALLAX_MIN_PX = 0.4       # 只按转动解时重投影误差中位数（缩小图像素）超过这么多，才考虑镜头有平移…
PARALLAX_GAIN = 0.7         # …且加上平移后误差降到这么多倍以下

FOOT_LOW = 0.08             # 脚离这附近最低处不到这么高（米）…
FOOT_STILL = 0.5            # …且水平速度低于这么多（米/秒）算踩着地（人离镜头的远近每帧有十几厘米的识别抖动）
ROOT_SIGMA_SECONDS = 0.2    # 走位的平滑（远近方向的识别抖动大，脚底防滑再把踩地的脚钉住）
YAW_SIGMA_SECONDS = 0.1
FOOT_WINDOW_SECONDS = 1.0   # 「附近最低处」的时间窗
JUMP_METERS_PER_SECOND = 6.0   # 镜头解算断开时，角色按「镜头没动」算出来移动得比这还快，就当切镜分段

# 踩地（二维）：脚跟 / 脚尖在画面上的移动（扣掉镜头运动后）按人的身高换成米每秒，低于这个算踩着
CONTACT_SPEED = 0.3
CONTACT_HEIGHT = 0.10       # 且脚离附近（前后 FOOT_WINDOW_SECONDS）最低处不到这么高（米，模型系）
BODY_HEIGHT_M = 1.7         # 人物框高度当这么多米（只用来把像素换成米，粗一点没关系）
STEP_DRIFT_GAIN = 0.2       # 每迈出新的一步，把累积的偏差往识别估的位置拉回这么多（防止长时间走偏；踩着的脚照样不动）
GAP_FRAMES = 3              # 两只脚都没踩地不超过这么多帧时延续踩地时的速度
FOOT_POINTS = {             # 模型系的骨骼 ← MHR70 的二维关键点（脚跟用脚踝骨代表）
    "j_asi_d_l": 17, "j_asi_e_l": 15,
    "j_asi_d_r": 20, "j_asi_e_r": 18,
}

FOLLOW_SIGMA_SECONDS = 0.8  # 平滑跟拍：机位跟角色的平滑
AIM_SIGMA_SECONDS = 0.25    # 镜头瞄准点（胸口）的平滑
FACING_SIGMA_SECONDS = 2.0  # 面朝方向的平滑（转圈不算转向）
FACING_MIN = 0.35           # 平滑后面朝向量短于这个（一直在转圈）就保持之前的方向
PAN_FOV_RANGE = (8.0, 60.0) # 定点摇镜的视角范围（度）


@dataclass
class CameraSteps:
    """镜头解算结果：steps[k] = 第 k-1 帧相机系 → 第 k 帧相机系的刚体变换（四元数 xyzw + 平移，SAM 尺度），
    ok[k] = 这一步可信（ok[0] 恒为 False）；mode：depth（转动 + 平移）/ rotation（只有转动）/ none。"""
    steps: np.ndarray       # (帧, 7)
    ok: np.ndarray          # (帧,) bool
    mode: str


class CameraTracker:
    """逐帧喂 add()，最后 result()。depth_of(rgb) → depth.PhotoDepth 或 None（没装深度模型）。"""

    def __init__(self, depth_of=None):
        self._depth_of = depth_of
        self._prev = None            # (灰度图, 角点 (n,2) 缩小后像素, 3D (n,3) 或 None)
        self._steps: list[np.ndarray] = []
        self._ok: list[bool] = []
        self._ratios: list[float] = []
        self._focal: float | None = None   # 解算用的焦距（第一帧定下，原图像素）
        self._depth_used = False
        self.last_depth = None             # 最近一帧的深度（depth.PhotoDepth），找地面用
        self.last_turn = 0.0

    def add(self, rgb: np.ndarray, exclude: list, focal: float | None, root_cam: np.ndarray | None = None,
            roots: dict[int, np.ndarray] | None = None) -> dict[int, float]:
        """exclude：这一帧所有人物框（原图像素）；focal：SAM 的焦距（没有深度时用）；root_cam：主角根节点在相机系的位置（深度换尺度）。
        roots：每个人（序号 → 躯干关键点的原图像素 (k, 2)）；返回每个人躯干处的场景深度（MoGe 的米，没深度时为空），多人站位按它校正远近。"""
        import cv2

        h, w = rgb.shape[:2]
        s = min(1.0, TRACK_WIDTH / w)
        gray = cv2.cvtColor(cv2.resize(rgb, (round(w * s), round(h * s)), interpolation=cv2.INTER_AREA), cv2.COLOR_RGB2GRAY)
        mask = np.full(gray.shape, 255, np.uint8)
        for b in exclude:
            x1, y1, x2, y2 = b
            mx, my = (x2 - x1) * PERSON_MARGIN, (y2 - y1) * PERSON_MARGIN
            cv2.rectangle(mask, (int((x1 - mx) * s), int((y1 - my) * s)), (int((x2 + mx) * s), int((y2 + my) * s)), 0, -1)
        depth = self._depth_of(rgb) if self._depth_of is not None else None
        if self._focal is None:
            self._focal = float(depth.focal) if depth is not None else float(focal or max(w, h))
        f = self._focal * s
        K = np.array([[f, 0, gray.shape[1] / 2], [0, f, gray.shape[0] / 2], [0, 0, 1]])

        step, ok = np.r_[0, 0, 0, 1, 0, 0, 0].astype(float), False
        if self._prev is not None:
            ok, step = self._solve(self._prev, gray, K)
        self._steps.append(step)
        self.last_turn = float(np.degrees(R.from_quat(step[:4]).magnitude())) if ok else 0.0   # 这一步镜头转了多少度
        self._ok.append(ok)

        pts = cv2.goodFeaturesToTrack(gray, MAX_CORNERS, 0.01, 8, mask=mask)
        pts = np.zeros((0, 2), np.float32) if pts is None else pts.reshape(-1, 2)
        p3 = None
        if depth is not None and len(pts):
            self._depth_used = True
            dh, dw = depth.depth.shape
            u = np.clip((pts[:, 0] / s * dw / w).astype(int), 0, dw - 1)
            v = np.clip((pts[:, 1] / s * dh / h).astype(int), 0, dh - 1)
            z = depth.depth[v, u]
            good = depth.valid[v, u] & np.isfinite(z) & (z > 0)
            pts, z = pts[good], z[good]
            p3 = np.c_[(pts[:, 0] - K[0, 2]) / f * z, (pts[:, 1] - K[1, 2]) / f * z, z]
            if root_cam is not None and root_cam[2] > 0 and focal:
                # 人在画面上的位置按 SAM 自己的焦距投影；这里的深度 × 比例 = SAM 的距离
                ru = int(np.clip(focal * root_cam[0] / root_cam[2] + w / 2, 0, w - 1) * dw / w)
                rv = int(np.clip(focal * root_cam[1] / root_cam[2] + h / 2, 0, h - 1) * dh / h)
                patch = depth.depth[max(0, rv - 3):rv + 4, max(0, ru - 3):ru + 4]
                patch = patch[np.isfinite(patch) & (patch > 0)]
                if len(patch):
                    self._ratios.append(float(root_cam[2] / np.median(patch)))
        self._prev = (gray, pts.astype(np.float32), p3)
        self.last_depth = depth
        return {p: d for p, r in (roots or {}).items() if depth is not None and (d := _depth_at(depth, r, w, h)) is not None}

    def _solve(self, prev, gray, K):
        import cv2

        g0, p0, p3 = prev
        identity = np.r_[0, 0, 0, 1, 0, 0, 0].astype(float)
        if len(p0) < MIN_INLIERS:
            return False, identity
        p1, st, _ = cv2.calcOpticalFlowPyrLK(g0, gray, p0, None, winSize=(21, 21), maxLevel=4)
        back, st2, _ = cv2.calcOpticalFlowPyrLK(gray, g0, p1, None, winSize=(21, 21), maxLevel=4)
        good = (st.ravel() == 1) & (st2.ravel() == 1) & (np.linalg.norm(back - p0, axis=1) < 1.0)
        if good.sum() < MIN_INLIERS:
            return False, identity
        a, b = p0[good], p1[good]
        # 先按「只转不移」解（单应矩阵 → 转动）：远处背景接近一个平面时，转动和平移很难分开，摇镜头最常见
        H, inl = cv2.findHomography(a, b, cv2.RANSAC, 2.0)
        if H is None or inl is None or inl.sum() < max(MIN_INLIERS, MIN_INLIER_SHARE * len(a)):
            return False, identity
        u, _, vt = np.linalg.svd(np.linalg.inv(K) @ H @ K)
        Rm = u @ vt
        if np.linalg.det(Rm) < 0:
            return False, identity
        rot, t = R.from_matrix(Rm), np.zeros(3)
        if p3 is not None:
            # 有深度时再看要不要平移：近处、远处的点移动得不一样（视差）才算镜头在走，平移要让重投影误差明显变小
            X = p3[good].astype(np.float64)
            t_try = _solve_translation(rot, X, b, K)
            e0, e1 = _reproject(rot, np.zeros(3), X, b, K), _reproject(rot, t_try, X, b, K)
            if np.median(e0) > PARALLAX_MIN_PX and np.median(e1) < PARALLAX_GAIN * np.median(e0):
                keep = e1 < 3.0
                if keep.sum() >= MIN_INLIERS:
                    rvec, tvec = cv2.solvePnPRefineLM(X[keep], b[keep].astype(np.float64), K, None,
                                                      rot.as_rotvec().reshape(3, 1), t_try.reshape(3, 1))
                    rot, t = R.from_rotvec(rvec.ravel()), tvec.ravel()
        if np.degrees(rot.magnitude()) > MAX_STEP_DEG:
            return False, identity
        return True, np.r_[rot.as_quat(), t]

    def result(self) -> CameraSteps:
        steps = np.array(self._steps, float).reshape(-1, 7)
        ok = np.array(self._ok, bool)
        mode = "depth" if self._depth_used else ("rotation" if ok.any() else "none")
        if len(self._ratios) and mode == "depth":
            steps[:, 4:7] *= float(np.median(self._ratios))   # 深度的米 → SAM 的米
        too_far = np.linalg.norm(steps[:, 4:7], axis=1) > MAX_STEP_METERS
        ok &= ~too_far
        steps[~ok] = [0, 0, 0, 1, 0, 0, 0]
        return CameraSteps(steps, ok, mode)


def _reproject(rot: R, t: np.ndarray, X: np.ndarray, uv: np.ndarray, K: np.ndarray) -> np.ndarray:
    Y = rot.apply(X) + t
    z = np.clip(Y[:, 2], 1e-6, None)
    proj = np.c_[K[0, 0] * Y[:, 0] / z + K[0, 2], K[1, 1] * Y[:, 1] / z + K[1, 2]]
    return np.linalg.norm(proj - uv, axis=1)


def _solve_translation(rot: R, X: np.ndarray, uv: np.ndarray, K: np.ndarray) -> np.ndarray:
    """转动定了，按 (u - cx)(r3·X + tz) = fx (r1·X + tx) 这类线性方程解平移（Huber 加权迭代三次）。"""
    Y = rot.apply(X)
    du, dv = uv[:, 0] - K[0, 2], uv[:, 1] - K[1, 2]
    f = K[0, 0]
    A = np.zeros((2 * len(X), 3))
    rhs = np.zeros(2 * len(X))
    A[0::2, 0], A[0::2, 2], rhs[0::2] = f, -du, du * Y[:, 2] - f * Y[:, 0]
    A[1::2, 1], A[1::2, 2], rhs[1::2] = f, -dv, dv * Y[:, 2] - f * Y[:, 1]
    w = np.ones(len(rhs))
    t = np.zeros(3)
    for _ in range(3):
        t, *_ = np.linalg.lstsq(A * w[:, None], rhs * w, rcond=None)
        r = np.abs(A @ t - rhs) / np.clip(np.repeat(Y[:, 2] + t[2], 2), 1e-3, None)
        w = 1.0 / np.sqrt(np.maximum(1.0, r / 2.0))
    return t


TORSO_DEPTH_PERCENTILE = 25   # 躯干几个点的深度取偏近的一端：人在画面里小时有的点会漏到背后的墙上（背景总比人远）


def _depth_at(depth, points: np.ndarray, w: int, h: int) -> float | None:
    """躯干关键点（原图像素）处的场景深度：每点 3×3 中位数，再取 TORSO_DEPTH_PERCENTILE 分位（MoGe 的米）；都在画面外时 None。"""
    dh, dw = depth.depth.shape
    values = []
    for x, y in np.asarray(points, float).reshape(-1, 2):
        if not (0 <= x < w and 0 <= y < h):
            continue
        u, v = int(x * dw / w), int(y * dh / h)
        patch = depth.depth[max(0, v - 1):v + 2, max(0, u - 1):u + 2]
        patch = patch[np.isfinite(patch) & (patch > 0)]
        if len(patch):
            values.append(float(np.median(patch)))
    return float(np.percentile(values, TORSO_DEPTH_PERCENTILE)) if len(values) >= 2 else None


# ---------------------------------------------------------------- 角色走位、镜头轨道


@dataclass
class FrameGeometry:
    """一帧主角的「相机系 → 角色模型系（FFXIV）」：p_ff = hip_ff + scale × (up ∘ flip (p_cam - cam_t) - hip_mhr)。"""
    up: R
    cam_t: np.ndarray
    hip_mhr: np.ndarray
    hip_ff: np.ndarray
    scale: float

    def to_model(self, p_cam: np.ndarray) -> np.ndarray:
        return self.hip_ff + self.scale * (self.up.apply((np.asarray(p_cam) - self.cam_t) * _FLIP) - self.hip_mhr)

    def rot_to_model(self) -> R:
        return self.up * R.from_matrix(np.diag(_FLIP))

    def origin_cam(self) -> np.ndarray:
        """角色模型系的原点在相机系里的位置。"""
        return self.cam_t + self.up.inv().apply(self.hip_mhr - self.hip_ff / self.scale) * _FLIP


def geometry(human: dict, rt, mhr_names: list[str]) -> FrameGeometry:
    """和 camera.photo_camera_in_world 同一套换算。"""
    up = R.from_quat(rt.upright_rotation) if rt.upright_rotation is not None else R.identity()
    mc = up.apply(np.asarray(human["joint_coords"]) * _FLIP)
    J = lambda n: mc[mhr_names.index(n)]
    P = rt.all_positions
    hip_mhr, hip_ff = (J("l_upleg") + J("r_upleg")) / 2, (P["j_asi_a_l"] + P["j_asi_a_r"]) / 2
    scale = float(np.linalg.norm(P["j_kubi"] - hip_ff) / np.linalg.norm(J("c_neck") - hip_mhr))
    return FrameGeometry(up, np.asarray(human["cam_t"], float), hip_mhr, hip_ff, scale)


def _yaw_of(rot: R) -> float:
    z = rot.apply([0.0, 0.0, 1.0])
    return float(np.arctan2(z[0], z[2]))


def _facing(bones: dict[str, np.ndarray]) -> float | None:
    """身体面朝的方向（模型系水平角，弧度；+z 为 0）：两髋连线的垂直方向。"""
    if "j_asi_a_l" not in bones or "j_asi_a_r" not in bones:
        return None
    right = bones["j_asi_a_r"] - bones["j_asi_a_l"]
    fwd = np.cross([0.0, 1.0, 0.0], right)
    if np.hypot(fwd[0], fwd[2]) < 1e-6:
        return None
    return float(np.arctan2(fwd[0], fwd[2]))


def _unwrap_fill(a: np.ndarray, valid: np.ndarray) -> np.ndarray:
    from .video import fill_gaps

    a = np.asarray(a, float).copy()
    idx = np.flatnonzero(valid)
    if len(idx):
        a[idx] = np.unwrap(a[idx])
    return fill_gaps(a, valid)


def root_track(geo: list[FrameGeometry | None], cam: CameraSteps | None, facing: list[float | None], fps: float,
               forced_breaks: set[int] | None = None) -> tuple[np.ndarray, np.ndarray]:
    """每帧角色模型系在「第一帧角色模型系」里的水平位置 + 转向：(帧, 4) [x, 0, z, yaw]，和分段处标记 (帧,) bool。
    geo[k] 为 None 的帧（没识别到人）按前后插值。forced_breaks：一定要分段的帧（跟踪按外观找回了人 = 切镜）。"""
    n = len(geo)
    valid = np.array([g is not None for g in geo])
    out = np.zeros((n, 4))
    breaks = np.zeros(n, bool)
    if not valid.any():
        return out, breaks
    steps = cam.steps if cam is not None else np.tile([0, 0, 0, 1, 0, 0, 0], (n, 1)).astype(float)
    ok = cam.ok if cam is not None else np.zeros(n, bool)
    forced = forced_breaks or set()

    # 相机 k 系 → 段首相机系 的累积变换（pose[k] 把相机 k 系的点变到段首相机系）
    pose_rot, pose_t = [R.identity()] * n, [np.zeros(3)] * n
    seg_start = [0] * n
    start = 0
    for k in range(n):
        if k > 0:
            step_rot, step_t = R.from_quat(steps[k, :4]), steps[k, 4:7]
            # 第 k-1 帧 → 第 k 帧：p_k = step (p_{k-1})；第 k 帧系 → 段首：pose[k-1] ∘ step⁻¹
            inv_rot = step_rot.inv()
            pose_rot[k] = pose_rot[k - 1] * inv_rot
            pose_t[k] = pose_t[k - 1] + pose_rot[k - 1].apply(inv_rot.apply(-step_t))
            if k in forced or (not ok[k] and _jumped(geo, k, fps)):
                start = k
                pose_rot[k], pose_t[k] = R.identity(), np.zeros(3)
                breaks[k] = True
        seg_start[k] = start

    # 每段的参照帧：段里第一个有人的帧
    yaw = np.zeros(n)
    have = np.zeros(n, bool)
    prev_end: tuple[np.ndarray, float, float] | None = None   # 上一段最后有人的帧：(位置, 转向, 面朝)
    k = 0
    while k < n:
        s = seg_start[k]
        e = k
        while e + 1 < n and seg_start[e + 1] == s:
            e += 1
        idx = [i for i in range(s, e + 1) if valid[i]]
        if idx:
            r = idx[0]
            gr = geo[r]
            rot_r = gr.rot_to_model()
            local = np.zeros((len(idx), 4))
            for j, i in enumerate(idx):
                g = geo[i]
                # 角色 i 的模型系原点、朝向 → 相机 i 系 → 段首相机系 → 角色 r 的模型系
                origin_cam = g.cam_t + g.up.inv().apply(g.hip_mhr - g.hip_ff / g.scale) * _FLIP
                o_seg = pose_rot[i].apply(origin_cam) + pose_t[i]
                o_r = gr.to_model(pose_rot[r].inv().apply(o_seg - pose_t[r]))
                rel = rot_r * pose_rot[r].inv() * pose_rot[i] * g.rot_to_model().inv()
                local[j] = [o_r[0], 0.0, o_r[2], _yaw_of(rel)]
            local[:, 0] -= local[0, 0]
            local[:, 2] -= local[0, 2]
            local[:, 3] = np.unwrap(local[:, 3])
            # 接上一段：位置接着，转向让身体面朝方向连续
            if prev_end is None:
                base_pos, base_yaw = np.zeros(3), 0.0
            else:
                pos, yw, face = prev_end
                f_r = facing[r] if facing[r] is not None else face
                base_yaw = yw + face - f_r if (face is not None and f_r is not None) else yw
                base_pos = pos
            cy, sy = np.cos(base_yaw), np.sin(base_yaw)
            for j, i in enumerate(idx):
                x, z = local[j, 0], local[j, 2]
                out[i] = [base_pos[0] + cy * x + sy * z, 0.0, base_pos[2] - sy * x + cy * z, base_yaw + local[j, 3]]
                have[i] = True
            last = idx[-1]
            prev_end = (out[last, :3].copy(), out[last, 3], facing[last])
        k = e + 1
    for c in (0, 2):
        out[:, c] = _fill(out[:, c], have)
    out[:, 3] = _unwrap_fill(out[:, 3], have)
    return out, breaks


def _fill(a, valid):
    from .video import fill_gaps

    return fill_gaps(a, valid)


def _jumped(geo, k: int, fps: float) -> bool:
    """镜头解算断开的这一步，按「镜头没动」算角色是不是瞬移了（切镜）。"""
    a, b = geo[k - 1] if k > 0 else None, geo[k]
    if a is None or b is None:
        return False
    return float(np.linalg.norm(a.cam_t - b.cam_t)) * fps > JUMP_METERS_PER_SECOND


def foot_slide(root: np.ndarray, feet: dict[str, np.ndarray], fps: float, valid: np.ndarray) -> tuple[float, float]:
    """（踩地帧占比，踩地时脚的平均水平速度 米/秒），看防滑效果用。"""
    contacts, worlds = _contacts(root, feet, fps, valid)
    if not contacts:
        return 0.0, 0.0
    share = float(np.mean([c.mean() for c in contacts]))
    speeds = [np.linalg.norm(np.diff(w[:, [0, 2]], axis=0), axis=1)[c[1:] & c[:-1]] * fps for c, w in zip(contacts, worlds)]
    sp = np.concatenate(speeds) if speeds else np.zeros(0)
    return share, float(sp.mean()) if len(sp) else 0.0


def _contacts(root, feet, fps, valid):
    n = len(root)
    cy, sy = np.cos(root[:, 3]), np.sin(root[:, 3])

    def world(p):
        return np.c_[root[:, 0] + cy * p[:, 0] + sy * p[:, 2], p[:, 1], root[:, 2] - sy * p[:, 0] + cy * p[:, 2]]

    win = max(1, round(FOOT_WINDOW_SECONDS * fps / 2))
    contacts, worlds = [], []
    for side in ("l", "r"):
        pts = [world(feet[n_]) for n_ in (f"j_asi_d_{side}", f"j_asi_e_{side}") if n_ in feet]
        if not pts:
            continue
        w = np.mean(pts, axis=0)
        height = np.min([p[:, 1] for p in pts], axis=0)
        low = np.array([height[max(0, k - win):k + win + 1].min() for k in range(n)])
        speed = np.r_[0, np.linalg.norm(np.diff(w[:, [0, 2]], axis=0), axis=1) * fps]
        speed = np.maximum(speed, np.r_[speed[1:], 0])
        contacts.append(valid & (height <= low + FOOT_LOW) & (speed <= FOOT_STILL))
        worlds.append(w)
    return contacts, worlds


def foot_contacts(raw, person: int, feet: dict[str, np.ndarray], fps: float, breaks: np.ndarray | None = None) -> dict[str, np.ndarray]:
    """每个脚部点（脚跟、脚尖）每帧是否踩着地：二维关键点前后帧的移动扣掉镜头这一步造成的移动，按人物框高度换成米每秒，
    前后两步的平均低于 CONTACT_SPEED，且这只脚在模型系里离附近最低处不到 CONTACT_HEIGHT。feet：{骨骼名: (帧, 3) 模型系位置}。"""
    n = len(raw.times)
    steps = raw.camera.steps if raw.camera is not None else np.tile([0, 0, 0, 1, 0, 0, 0], (n, 1)).astype(float)
    cut = np.zeros(n, bool) if breaks is None else np.asarray(breaks, bool)
    win = max(1, round(FOOT_WINDOW_SECONDS * fps / 2))
    out = {}
    for name, kp in FOOT_POINTS.items():
        if name not in feet:
            continue
        speed = np.full(n, np.nan)   # 第 k-1 → k 帧这一步
        for k in range(1, n):
            a, b = raw.human(person, k - 1), raw.human(person, k)
            if a is None or b is None or cut[k]:
                continue
            pa, pb = np.asarray(a["keypoints_2d"], float)[kp, :2], np.asarray(b["keypoints_2d"], float)[kp, :2]
            w, h = raw.image_size
            fa, fb = float(a["focal_length"]), float(b["focal_length"])
            z = float(a["cam_t"][2])
            X = np.array([(pa[0] - w / 2) / fa * z, (pa[1] - h / 2) / fa * z, z])   # 上一帧这个点（当它在世界里没动）
            Y = R.from_quat(steps[k, :4]).apply(X) + steps[k, 4:7]
            if Y[2] <= 1e-3:
                continue
            guess = np.array([fb * Y[0] / Y[2] + w / 2, fb * Y[1] / Y[2] + h / 2])
            box = np.asarray(b["bbox"], float)
            meters_per_px = BODY_HEIGHT_M / max(box[3] - box[1], 1.0)
            speed[k] = float(np.linalg.norm(pb - guess)) * meters_per_px * fps
        both = np.vstack([speed, np.r_[speed[1:], np.nan]])
        still = np.nanmean(np.where(np.isfinite(both), both, np.nan), axis=0) if n else speed
        height = feet[name][:, 1]
        low = np.array([height[max(0, k - win):k + win + 1].min() for k in range(n)])
        contact = np.nan_to_num(still, nan=np.inf) < CONTACT_SPEED
        contact &= height <= low + CONTACT_HEIGHT
        # 只踩了一帧的不算（多半是挥脚时刚好慢了一下）
        lone = contact & ~np.r_[False, contact[:-1]] & ~np.r_[contact[1:], False]
        out[name] = contact & ~lone
    return out


def step_odometry(base: np.ndarray, yaw: np.ndarray, feet: dict[str, np.ndarray], contacts: dict[str, np.ndarray],
                  breaks: np.ndarray | None = None) -> np.ndarray:
    """按踩着的脚推水平位置：base (帧, 2) 识别估的水平位置（x、z），yaw 每帧转向，feet 脚部点模型系位置，contacts 每点是否踩地。
    踩着的点钉在场景里不动，人的位置 = 钉住的点 - 转向 × 这一帧它在模型系里的位置（几只脚一起踩着取平均）；
    都没踩地时跟着识别估的位移走；每迈出新的一步把累积偏差往识别估的位置拉回 STEP_DRIFT_GAIN。返回 (帧, 2)。"""
    n = len(base)
    out = np.array(base, float, copy=True)
    if n == 0:
        return out
    cut = np.zeros(n, bool) if breaks is None else np.asarray(breaks, bool)

    def world(name, k, pos):
        c, s = np.cos(yaw[k]), np.sin(yaw[k])
        x, z = feet[name][k, 0], feet[name][k, 2]
        return pos + np.array([c * x + s * z, -s * x + c * z])

    pos = np.array(base[0], float)
    anchors: dict[str, np.ndarray] = {}
    landed: dict[str, int] = {}        # 每个点这次是第几帧落地的
    velocity, since = np.zeros(2), 99   # 最近踩着地时每帧的位移；离上次踩着地过了几帧
    for k in range(n):
        if k > 0 and not cut[k]:
            # 刚离地（迈步、小跳）：延续踩地时的速度；离地久了才跟识别估的位移（它前后方向抖得厉害）
            pos = pos + (velocity if since <= GAP_FRAMES else base[k] - base[k - 1])
        if cut[k]:
            anchors.clear()
        planted = [name for name in contacts if contacts[name][k]]
        for name in list(anchors):
            if name not in planted:
                del anchors[name]
        held = [name for name in planted if name in anchors]
        if held:
            # 只钉最早落地的那只脚（正在承重的）；同一只脚的脚跟、脚尖一起取平均。两只脚都着地时识别的脚距每帧有误差，
            # 两只都钉就只能取平均、两只都滑；钉住承重脚，误差留给刚落地的那只
            first = min(held, key=lambda name: landed[name])
            side = first[-1]
            pins = [name for name in held if name[-1] == side]
            new = np.mean([anchors[name] - (world(name, k, np.zeros(2))) for name in pins], axis=0)
            velocity, since = new - out[k - 1] if k > 0 else np.zeros(2), 0
            pos = new
        else:
            since += 1
        if not held and planted:
            pos = pos + STEP_DRIFT_GAIN * (base[k] - pos)   # 新的一步落地：往识别估的位置拉回一点
        for name in planted:
            if name not in anchors:
                anchors[name] = world(name, k, pos)
                landed[name] = k
        out[k] = pos
    return out


def contact_slide(xz: np.ndarray, yaw: np.ndarray, feet: dict[str, np.ndarray], contacts: dict[str, np.ndarray], fps: float) -> float:
    """踩地时脚部点在场景里的平均水平速度（米/秒），看防滑效果。"""
    speeds = []
    for name, c in contacts.items():
        cs, sn = np.cos(yaw), np.sin(yaw)
        x, z = feet[name][:, 0], feet[name][:, 2]
        w = np.c_[xz[:, 0] + cs * x + sn * z, xz[:, 1] - sn * x + cs * z]
        both = c[1:] & c[:-1]
        speeds.append(np.linalg.norm(np.diff(w, axis=0), axis=1)[both] * fps)
    s = np.concatenate(speeds) if speeds else np.zeros(0)
    return float(s.mean()) if len(s) else 0.0


def lock_feet(root: np.ndarray, feet: dict[str, np.ndarray], fps: float, valid: np.ndarray) -> np.ndarray:
    """脚底防滑：feet = {名: (帧, 3) 模型系位置}（脚踝、脚尖），root 同 root_track。
    踩地的帧把脚在场景里的位置钉在踩下去那一刻，差多少挪角色的根；没踩地时保持当前修正量。返回修正后的 root。"""
    from .video import smooth

    n = len(root)
    if n < 3 or not feet:
        return root
    contacts, worlds = _contacts(root, feet, fps, valid)
    if not contacts:
        return root
    offset = np.zeros((n, 2))
    current = np.zeros(2)
    anchors: list[np.ndarray | None] = [None] * len(contacts)
    for k in range(n):
        shifts = []
        for i, (c, w) in enumerate(zip(contacts, worlds)):
            if c[k]:
                if anchors[i] is None:
                    anchors[i] = w[k, [0, 2]] + current
                shifts.append(anchors[i] - w[k, [0, 2]])
            else:
                anchors[i] = None
        if shifts:
            current = np.mean(shifts, axis=0)
        offset[k] = current
    offset = smooth(offset, 0.05 * fps)
    out = root.copy()
    out[:, 0] += offset[:, 0]
    out[:, 2] += offset[:, 1]
    return out


def _transform_camera(cam: dict, root_row: np.ndarray) -> dict:
    """角色模型系里的镜头 → 第一帧角色模型系（乘这一帧的走位）。"""
    rot = R.from_euler("y", root_row[3])
    shift = np.array([root_row[0], root_row[1], root_row[2]])
    return {"position": (rot.apply(cam["position"]) + shift).tolist(), "forward": rot.apply(cam["forward"]).tolist(),
            "up": rot.apply(cam["up"]).tolist(), "fovY": cam["fovY"]}


def _look(position: np.ndarray, target: np.ndarray, fov: float) -> dict:
    f = target - position
    f = f / max(np.linalg.norm(f), 1e-9)
    up = np.array([0.0, 1.0, 0.0]) - f * f[1]
    up = up / max(np.linalg.norm(up), 1e-9)
    return {"position": position.tolist(), "forward": f.tolist(), "up": up.tolist(), "fovY": float(fov)}


def camera_tracks(root: np.ndarray, local_cams: list[dict], chest: np.ndarray, facing: np.ndarray, valid: np.ndarray,
                  fps: float, chained: list[dict | None] | None = None) -> dict[str, list[dict]]:
    """三条镜头轨道（第一帧角色模型系）：original / follow / pan。local_cams：平滑后的每帧相对镜头（角色模型系）；
    chest：(帧, 3) 胸口在角色模型系的位置；facing：(帧,) 身体面朝（模型系水平角）；
    chained[k]：看不到人的帧按镜头解算接出来的镜头（第一帧角色模型系），None 时按前后插值。"""
    from .video import smooth, smooth_camera

    n = len(root)
    original: list[dict | None] = []
    for k in range(n):
        if valid[k] or chained is None or chained[k] is None:
            original.append(_transform_camera(local_cams[k], root[k]))
        else:
            original.append({**chained[k], "fovY": local_cams[k]["fovY"]})
    original = smooth_camera(original, 0.1 * fps)

    cy, sy = np.cos(root[:, 3]), np.sin(root[:, 3])
    chest_w = np.c_[root[:, 0] + cy * chest[:, 0] + sy * chest[:, 2], chest[:, 1], root[:, 2] - sy * chest[:, 0] + cy * chest[:, 2]]
    aim = smooth(chest_w, AIM_SIGMA_SECONDS * fps)
    anchor = smooth(chest_w, FOLLOW_SIGMA_SECONDS * fps)
    anchor[:, 1] = smooth(chest_w[:, 1], 4 * FOLLOW_SIGMA_SECONDS * fps)
    ref = int(np.flatnonzero(valid)[0]) if valid.any() else 0
    fov0 = float(original[ref]["fovY"])

    # 平滑跟拍：开头的机位相对胸口的偏移，随（重度平滑的）身体朝向慢慢转
    world_face = facing + root[:, 3]
    vec = smooth(np.c_[np.sin(world_face), np.cos(world_face)], FACING_SIGMA_SECONDS * fps)
    angle = np.zeros(n)
    hold = float(np.arctan2(vec[ref, 0], vec[ref, 1]))
    for k in range(n):
        if np.linalg.norm(vec[k]) >= FACING_MIN:
            a = float(np.arctan2(vec[k, 0], vec[k, 1]))
            hold = hold + (a - hold + np.pi) % (2 * np.pi) - np.pi
        angle[k] = hold
    angle -= angle[ref]
    offset = np.asarray(original[ref]["position"]) - anchor[ref]
    dist = np.linalg.norm(offset)
    if dist > 12:
        offset *= 12 / dist
    elif dist < 1.5:
        offset *= 1.5 / max(dist, 1e-6)
    follow = []
    for k in range(n):
        c, s = np.cos(angle[k]), np.sin(angle[k])
        o = np.array([c * offset[0] + s * offset[2], offset[1], -s * offset[0] + c * offset[2]])
        follow.append(_look(anchor[k] + o, aim[k], fov0))

    # 定点摇镜：机位停在开头，视角按距离缩放保持角色在画面里的大小
    pos0 = np.asarray(original[ref]["position"])
    d = np.linalg.norm(aim - pos0, axis=1).clip(0.5)
    half = np.tan(np.radians(fov0) / 2) * d[ref] / d
    fov = np.clip(np.degrees(2 * np.arctan(half)), *PAN_FOV_RANGE)
    fov = smooth(fov, 0.5 * fps)
    pan = [_look(pos0, aim[k], fov[k]) for k in range(n)]
    return {"original": original, "follow": follow, "pan": pan}


def world_tracks(raw, geos: list, names: list[str], frames: np.ndarray, valid: np.ndarray, local_cams: list[dict],
                 sigma: float) -> dict:
    """build_clip 用：主角的走位（脚底防滑后）和三条镜头轨道。frames：平滑后的骨骼帧 (帧, 骨骼, 10)。"""
    from .video import smooth

    fps = raw.fps
    n = len(geos)
    bone = lambda b: frames[:, names.index(b), 0:3] if b in names else None
    facing = []
    for k in range(n):
        f = _facing({b: frames[k, names.index(b), 0:3] for b in ("j_asi_a_l", "j_asi_a_r") if b in names}) if valid[k] else None
        facing.append(f)
    forced = set(np.flatnonzero(raw.breaks).tolist()) if raw.breaks is not None else set()
    root, breaks = root_track(geos, raw.camera, facing, fps, forced)
    root[:, [0, 2]] = smooth(root[:, [0, 2]], max(sigma, ROOT_SIGMA_SECONDS * fps))
    root[:, 3] = smooth(root[:, 3], max(sigma, YAW_SIGMA_SECONDS * fps))
    feet = {b: bone(b) for b in ("j_asi_d_l", "j_asi_e_l", "j_asi_d_r", "j_asi_e_r") if bone(b) is not None}
    contacts = foot_contacts(raw, 0, feet, fps, breaks)
    if any(c.any() for c in contacts.values()):
        # 二维判踩地 + 踩着的脚推走位
        before = (float(np.mean([c.mean() for c in contacts.values()])), contact_slide(root[:, [0, 2]], root[:, 3], feet, contacts, fps))
        root[:, [0, 2]] = step_odometry(root[:, [0, 2]], root[:, 3], feet, contacts, breaks)
        after = (before[0], contact_slide(root[:, [0, 2]], root[:, 3], feet, contacts, fps))
    else:
        # 没有二维关键点（旧数据）：按三维高度和速度判踩地，挪根防滑
        before = foot_slide(root, feet, fps, valid)
        root = lock_feet(root, feet, fps, valid)
        after = foot_slide(root, feet, fps, valid)
    face_arr = np.array([f if f is not None else np.nan for f in facing], float)
    have = ~np.isnan(face_arr)
    face_arr = _unwrap_fill(np.where(have, face_arr, 0.0), have) if have.any() else np.zeros(n)
    chest = bone("j_sebo_c") if bone("j_sebo_c") is not None else bone("j_kosi")
    chained = chain_cameras(geos, raw.camera, root, breaks)
    cams = camera_tracks(root, local_cams, chest, face_arr, valid, fps, chained)
    cam = raw.camera
    info = {"camera": cam.mode if cam is not None else "none",
            "solved": round(float(cam.ok[1:].mean()), 3) if cam is not None and n > 1 else 0.0,
            "breaks": [round(raw.times[k], 2) for k in np.flatnonzero(breaks)],
            "travel": round(float(np.sum(np.linalg.norm(np.diff(root[:, [0, 2]], axis=0), axis=1))), 2) if n > 1 else 0.0,
            "footContact": round(after[0], 3), "footSlide": [round(before[1], 3), round(after[1], 3)]}
    return {"root": root, "cameras": cams, "info": info, "breaks": breaks}


def partner_steps(raw, person: int, root: np.ndarray, offset: np.ndarray, feet: dict[str, np.ndarray], fps: float,
                  breaks: np.ndarray | None) -> tuple[np.ndarray, float, float]:
    """配角也按踩着的脚推：先换到场景里（主角走位 × 站位），推完再换回相对主角的站位。返回 (新站位, 推之前、之后的踩地滑动)。"""
    cs, sn = np.cos(root[:, 3]), np.sin(root[:, 3])
    world = np.c_[root[:, 0] + cs * offset[:, 0] + sn * offset[:, 2], root[:, 2] - sn * offset[:, 0] + cs * offset[:, 2]]
    yaw = root[:, 3] + offset[:, 3]
    contacts = foot_contacts(raw, person, feet, fps, breaks)
    if not any(c.any() for c in contacts.values()):
        return offset, 0.0, 0.0
    before = contact_slide(world, yaw, feet, contacts, fps)
    world = step_odometry(world, yaw, feet, contacts, breaks)
    after = contact_slide(world, yaw, feet, contacts, fps)
    out = offset.copy()
    dx, dz = world[:, 0] - root[:, 0], world[:, 1] - root[:, 2]
    out[:, 0], out[:, 2] = cs * dx - sn * dz, sn * dx + cs * dz
    return out, before, after


FLOOR_MAX_TILT_DEG = 35.0    # 看不到脚时：地面法线和镜头「上」最多差这么多（再斜多半是墙、台面）
FLOOR_MIN_SHARE_NO_FEET = 0.05   # 且至少占画面这么多
FLOOR_LOW_ROWS = 0.55        # 且这块面的点大多在画面下面（行号中位数在这个比例以下）
FLOOR_ITERS = 150            # 视频里每次找地面的拟合轮数、采样点数（照片用 400 / 60000；视频要找很多次，地面又大，少一些够用）
FLOOR_SAMPLES = 20000
FLOOR_ENOUGH = 3             # 同一个镜头里找到这么多次地面就够了…
FLOOR_RETURN_DEG = 5.0       # …除非镜头又转了这么多度（摇镜头后地面在画面里的样子变了）


def floor_up(depth, boxes: list, feet: np.ndarray, size: tuple[int, int]) -> np.ndarray | None:
    """这一帧深度图里的地面，返回「上」方向（相机系，y 朝下所以 y < 0）；找不到 None。
    看得到脚时找脚踩着的那块面（depth.find_floor）；脚不在画面里（近景、半身）时，取人下方、面积最大、接近水平的平面。
    boxes：所有人物框（原图像素，框掉不算地面）；feet：脚踝、脚尖、脚跟的原图像素 (k, 2)；size：原图 (宽, 高)。"""
    from .depth import find_floor

    if depth is None:
        return None
    feet = np.asarray(feet, float).reshape(-1, 2)
    if not ((feet[:, 0] >= 0) & (feet[:, 0] < size[0]) & (feet[:, 1] >= 0) & (feet[:, 1] < size[1])).sum() >= 2:
        return _floor_without_feet(depth, boxes, size)
    dh, dw = depth.depth.shape
    sx, sy = dw / size[0], dh / size[1]
    people = np.zeros((max(1, len(boxes)), dh, dw), bool)
    for i, (x1, y1, x2, y2) in enumerate(boxes):
        people[i, max(0, int(y1 * sy)):int(y2 * sy) + 1, max(0, int(x1 * sx)):int(x2 * sx) + 1] = True
    pix = [(int(np.clip(x * sx, 0, dw - 1)), int(np.clip(y * sy, 0, dh - 1))) for x, y in np.asarray(feet).reshape(-1, 2)
           if 0 <= x < size[0] and 0 <= y < size[1]]
    found = find_floor(depth, people, pix, iters=FLOOR_ITERS, samples=FLOOR_SAMPLES)
    if found is None:
        return None
    n = np.asarray(found[0], float)
    return n if n[1] < 0 else -n


def _floor_without_feet(depth, boxes: list, size: tuple[int, int]) -> np.ndarray | None:
    from .depth import _planes

    dh, dw = depth.depth.shape
    sx, sy = dw / size[0], dh / size[1]
    keep = depth.valid & np.isfinite(depth.points).all(-1)
    for x1, y1, x2, y2 in boxes:
        keep[max(0, int(y1 * sy)):int(y2 * sy) + 1, max(0, int(x1 * sx)):int(x2 * sx) + 1] = False
    rows = np.nonzero(keep)[0]
    pts = depth.points[keep]
    if len(pts) < 500:
        return None
    rng = np.random.default_rng(0)
    pick = rng.choice(len(pts), min(len(pts), FLOOR_SAMPLES), replace=False)
    sample, sample_rows = pts[pick], rows[pick]
    up = np.array([0.0, -1.0, 0.0])
    best = None
    for n, d, k in _planes(sample, up, 4, FLOOR_ITERS, 0.03, rng):
        near = np.abs(sample @ n + d) < 0.03 * np.maximum(1, sample[:, 2] / 3)
        share = k / len(sample) * keep.sum() / (dh * dw)
        tilt = np.degrees(np.arccos(np.clip(n @ up, -1, 1)))
        low = np.median(sample_rows[near]) / dh if near.any() else 0.0
        if tilt <= FLOOR_MAX_TILT_DEG and share >= FLOOR_MIN_SHARE_NO_FEET and low >= FLOOR_LOW_ROWS and (best is None or share > best[0]):
            best = (share, n)
    if best is None:
        return None
    n = np.asarray(best[1], float)
    return n if n[1] < 0 else -n


def gravity_upright(floor: dict[int, np.ndarray], cam: CameraSteps | None, breaks: np.ndarray | None, n: int) -> tuple[np.ndarray, np.ndarray]:
    """按地面定每帧的重力校正（四元数 xyzw，和身体估的 upright 同一个意思：相机系翻成模型系后转到 y 朝上）。
    floor：第 k 帧找到的地面「上」方向（相机系）。同一个镜头（两次切镜之间）里，各帧的观测用镜头解算的转动换到段首相机系
    取中位数，再换回每一帧——镜头在转、地面只在少数几帧看得见也能用。返回 (四元数 (帧, 4), 这一帧有没有按地面定)。"""
    steps = cam.steps if cam is not None else np.tile([0, 0, 0, 1, 0, 0, 0], (n, 1)).astype(float)
    out = np.tile([0.0, 0.0, 0.0, 1.0], (n, 1))
    have = np.zeros(n, bool)
    starts = [0] + [k for k in range(1, n) if breaks is not None and breaks[k]] + [n]
    for a, b in zip(starts[:-1], starts[1:]):
        rot = [R.identity()]
        for k in range(a + 1, b):
            rot.append(rot[-1] * R.from_quat(steps[k, :4]).inv())   # 第 k 帧相机系 → 段首相机系
        obs = [rot[k - a].apply(floor[k]) for k in range(a, b) if k in floor]
        if not obs:
            continue
        g = np.median(np.array(obs), axis=0)
        g /= max(np.linalg.norm(g), 1e-9)
        for k in range(a, b):
            up_cam = rot[k - a].inv().apply(g)
            out[k] = R.align_vectors([[0.0, 1.0, 0.0]], [up_cam * _FLIP])[0].as_quat()
            have[k] = True
    return out, have


PERSON_SIGMA_SECONDS = 0.3   # 配角站位的平滑（各人远近是分别估的，有抖动）
PERSON_MEDIAN_SECONDS = 0.35 # 平滑前先按前后这么多秒取中值，去掉单帧跳变
DEPTH_FIX_SIGMA_SECONDS = 1.0
DEPTH_FIX_RANGE = (0.5, 2.0)


def person_offsets(main: list, other: list, scene_depth: np.ndarray | None, fps: float, sigma: float) -> tuple[np.ndarray, np.ndarray]:
    """配角每帧相对主角的站位：配角模型系 → 主角模型系（同一帧、同一台相机），只留水平位置和转向：(帧, 4) [x, 0, z, yaw]。
    main / other：每帧的 FrameGeometry（没识别到为 None）；scene_depth：(2, 帧) 两人根节点处的场景深度（MoGe），
    SAM 对每个人的远近是分别估的，两人前后差常有几十厘米：按场景深度之比把配角沿视线挪到对的远近（主角不动）。
    返回 (站位, 两人都识别到的帧)。"""
    from .video import fill_gaps, smooth

    n = len(main)
    both = np.array([a is not None and b is not None for a, b in zip(main, other)])
    out = np.zeros((n, 4))
    if not both.any():
        return out, both
    # 远近校正：配角该在的深度 / SAM 估的深度 = (场景深度比) / (SAM 深度比)
    fix = np.full(n, np.nan)
    if scene_depth is not None:
        for k in np.flatnonzero(both):
            d0, d1 = scene_depth[0, k], scene_depth[1, k]
            z0, z1 = main[k].origin_cam()[2], other[k].origin_cam()[2]
            if np.isfinite(d0) and np.isfinite(d1) and d0 > 0 and z0 > 0 and z1 > 0:
                fix[k] = (d1 / d0) / (z1 / z0)
    have = np.isfinite(fix)
    if have.any():
        # 先按 DEPTH_FIX_SIGMA_SECONDS 的窗口取中值去掉跳变（某几帧取到了别人或背景），再平滑
        filled = fill_gaps(np.where(have, fix, 1.0), have)
        win = max(1, round(DEPTH_FIX_SIGMA_SECONDS * fps))
        filled = np.array([np.median(filled[max(0, k - win):k + win + 1]) for k in range(n)])
        fix = np.clip(smooth(filled, DEPTH_FIX_SIGMA_SECONDS * fps), *DEPTH_FIX_RANGE)
    else:
        fix = np.ones(n)
    for k in np.flatnonzero(both):
        g0, g1 = main[k], other[k]
        o = g0.to_model(g1.origin_cam() * fix[k])
        rel = g0.rot_to_model() * g1.rot_to_model().inv()
        out[k] = [o[0], 0.0, o[2], _yaw_of(rel)]
    # 先中值滤波去掉单帧跳变（某一帧检测框跳到别处、两人认反），再平滑
    win = max(1, round(PERSON_MEDIAN_SECONDS * fps))
    idx = np.flatnonzero(both)
    for c in (0, 2):
        vals = out[idx, c].copy()
        out[idx, c] = [np.median(vals[max(0, i - win):i + win + 1]) for i in range(len(idx))]
    for c in (0, 2):
        out[:, c] = smooth(fill_gaps(out[:, c], both), max(sigma, PERSON_SIGMA_SECONDS * fps))
    out[:, 3] = smooth(_unwrap_fill(out[:, 3], both), max(sigma, PERSON_SIGMA_SECONDS * fps))
    return out, both


def chain_cameras(geo: list[FrameGeometry | None], cam: CameraSteps | None, root: np.ndarray, breaks: np.ndarray) -> list[dict | None]:
    """看不到人的帧：用镜头解算从同一段里最近一个有人的帧接出镜头位置（第一帧角色模型系）；接不上为 None。"""
    n = len(geo)
    out: list[dict | None] = [None] * n
    if cam is None or not cam.ok.any():
        return out
    for k in range(n):
        if geo[k] is not None:
            continue
        # 往前找同一段里有人的帧 r，镜头 k 系 → 镜头 r 系 → 角色 r 模型系 → 第一帧角色模型系
        r, rel_rot, rel_t, okc = k, R.identity(), np.zeros(3), True
        while r > 0 and geo[r] is None:
            if breaks[r] or not cam.ok[r]:
                okc = False
                break
            step_rot, step_t = R.from_quat(cam.steps[r, :4]), cam.steps[r, 4:7]
            # 相机 r 系的点 p → 相机 r-1 系：step⁻¹(p)
            inv = step_rot.inv()
            rel_t = inv.apply(rel_t - step_t)
            rel_rot = inv * rel_rot
            r -= 1
        if not okc or geo[r] is None:
            continue
        g = geo[r]
        pos_r = g.to_model(rel_t)
        rot_r = g.rot_to_model() * rel_rot
        local = {"position": pos_r.tolist(), "forward": rot_r.apply([0.0, 0.0, 1.0]).tolist(),
                 "up": rot_r.apply([0.0, -1.0, 0.0]).tolist(), "fovY": None}
        out[k] = _transform_camera(local, root[r])
    return out
