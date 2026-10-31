"""
手指精修：SAM 3D Body 对小手、模糊的手和比心 / 比枪这类手势常估错（2026-11-01 实测：它的二维关键点和三维手指彼此一致，
但和画面对不上；快速模式和原版结果一样，重定向也照原样搬到了游戏骨骼）。这里按 SAM 的手在原图上裁一块，
用 MediaPipe Hand Landmarker 重新找 21 个手部关键点，再按它们重算每节手指的转动（手腕朝向不动）：
- 裁图：手的范围的 4 / 2.2 / 6 倍依次试（太紧找不到手掌，太松手太小），找到的手腕离 SAM 的手腕太远的不要（是另一只手）
- 视角：MediaPipe 的三维点按裁图正对着算，按裁图中心的视线方向转回相机系
- 远近翻转：只看画面分不清手心还是手背朝镜头，MediaPipe 常认反左右手、给出镜像的手（左右手标签在同一段视频里前后矛盾）。
  按手性定：拇指根在四指掌面的哪一侧，左右手相反（SAM 的手在一段舞蹈里 100% 是对的那一侧），反了就把远近翻过来
- 手腕朝向保留 SAM 的：MediaPipe 在画面平面里的方向准，但手心朝哪边（远近方向）常和 SAM 差几十度到一百多度，
  用它的手腕时认出 / 认不出的帧一交替，手在原地乱转（2026-11-01 用户实测）。所以只取手指相对手掌的姿势：
  按四个指根的方向把 MediaPipe 的手掌对齐到 SAM 的手掌，手指每节从父关节的静止相对朝向出发，最小转动到对齐后的骨段方向
- 关节活动范围：四指只能往掌心弯，后翘、侧摆有上限（FINGER_LIMITS），拇指限总转角。MediaPipe 偶尔认错一帧，
  不限的话手指会被拧到反方向 150° 以上
认不出的手保留 SAM 的结果。视频里另按时间补齐短空档、和 SAM 的结果平滑过渡（refine_track）。
"""

from __future__ import annotations

import threading

import numpy as np
from scipy.spatial.transform import Rotation as R

from .config import settings

CROP_SCALES = (4.0, 2.2, 6.0)   # 裁图边长 = 手的范围 × 这些倍数，依次试（2026-11-01 一段 4K 竖屏舞蹈：4 倍认出 62%，三种都试 78%）
MIN_SPREAD_PX = 80.0           # 手的范围至少按这么多像素算（SAM 把手指估成缩成一团时）
WRIST_MATCH = 0.8              # 找到的手腕离 SAM 的手腕不超过手的范围的这么多倍
MIN_DETECTION, MIN_PRESENCE = 0.3, 0.5
GAP_SECONDS = 0.4              # 视频：两次认出之间空这么久以内按前后插值
BLEND_SECONDS = 0.3            # 视频：认出的段落两头用这么久和 SAM 的结果过渡
SMOOTH_SECONDS = 0.1           # 视频：手指转动的时间平滑（MediaPipe 逐帧有抖动：2026-11-01 一段舞蹈帧间变化 0.05 秒时 11~16°，0.1 秒时 6~9°，和 SAM 相当）

# MediaPipe：0 手腕；拇指 1 根部 2 掌指 3 指间 4 指尖；食指 5~8、中指 9~12、无名指 13~16、小指 17~20（指根 → 指尖）
# MHR 的拇指：thumb0 根部、thumb1 掌骨中段、thumb2 掌指、thumb3 指间；其他手指 1 指根、2、3、_null 指尖；pinky0 是小指掌骨
CHAINS = (("thumb0", "thumb1", "thumb2", "thumb3", "thumb_null"),
          ("index1", "index2", "index3", "index_null"),
          ("middle1", "middle2", "middle3", "middle_null"),
          ("ring1", "ring2", "ring3", "ring_null"),
          ("pinky0", "pinky1", "pinky2", "pinky3", "pinky_null"))
SEGMENTS = {"thumb1": (1, 2), "thumb2": (2, 3), "thumb3": (3, 4),   # thumb0 → thumb1 很短，跟着手腕走
            "index1": (5, 6), "index2": (6, 7), "index3": (7, 8),
            "middle1": (9, 10), "middle2": (10, 11), "middle3": (11, 12),
            "ring1": (13, 14), "ring2": (14, 15), "ring3": (15, 16),
            "pinky1": (17, 18), "pinky2": (18, 19), "pinky3": (19, 20)}
PALM = (("index1", 5), ("middle1", 9), ("ring1", 13), ("pinky1", 17))
# 四指每节相对静止姿态（MHR 静止时手指微弯）的活动范围：(往掌心弯的范围, 侧摆上限)，度；拇指只限总转角
FINGER_LIMITS = {"1": ((-30.0, 95.0), 30.0), "2": ((-10.0, 110.0), 10.0), "3": ((-10.0, 90.0), 10.0)}
THUMB_LIMIT = 80.0
FOREARM = "lowarm"
_FLIP = np.array([1.0, -1.0, -1.0])   # 相机系（x 右、y 下、z 前）↔ MHR 模型系

_lock = threading.Lock()
_landmarker = None


def available() -> bool:
    try:
        import mediapipe  # noqa: F401
    except ImportError:
        return False
    return settings.hand_model.is_file()


def _get_landmarker():
    global _landmarker
    if _landmarker is None:
        from mediapipe.tasks.python import BaseOptions, vision

        # 路径含中文时 C 层打不开，按字节给模型
        options = vision.HandLandmarkerOptions(
            base_options=BaseOptions(model_asset_buffer=settings.hand_model.read_bytes()),
            running_mode=vision.RunningMode.IMAGE, num_hands=2,   # 裁图里常有另一只手，挑手腕离 SAM 最近的
            min_hand_detection_confidence=MIN_DETECTION, min_hand_presence_confidence=MIN_PRESENCE)
        _landmarker = vision.HandLandmarker.create_from_options(options)
    return _landmarker


def _names() -> list[str]:
    from .retarget.ffxiv import load_mhr_rest

    return load_mhr_rest()[0]


def _hand_joints(names: list[str], side: str) -> list[int]:
    return [names.index(f"{side}_wrist")] + [names.index(f"{side}_{j}") for chain in CHAINS for j in chain]


def _project(human: dict, size: tuple[int, int]) -> np.ndarray:
    X = np.asarray(human["joint_coords"], float) + np.asarray(human["cam_t"], float)
    f = float(human["focal_length"])
    return np.c_[f * X[:, 0] / X[:, 2] + size[0] / 2, f * X[:, 1] / X[:, 2] + size[1] / 2]


def _unit(v: np.ndarray) -> np.ndarray:
    v = np.asarray(v, float)
    return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-9)


def _swing(a: np.ndarray, b: np.ndarray) -> R:
    """把方向 a 转到 b 的最小转动。"""
    a, b = _unit(a), _unit(b)
    axis = np.cross(a, b)
    s, c = np.linalg.norm(axis), float(np.clip(a @ b, -1, 1))
    if s < 1e-9:
        return R.identity() if c > 0 else R.from_rotvec(np.pi * _unit(np.cross(a, [1.0, 0, 0] if abs(a[0]) < 0.9 else [0, 1.0, 0])))
    return R.from_rotvec(axis / s * np.arctan2(s, c))


def _limit(rest_dir: np.ndarray, palm: np.ndarray, target: np.ndarray, flex: tuple[float, float], spread: float) -> np.ndarray:
    """把骨段目标方向限制在活动范围里：相对静止方向，往掌心（palm）弯 flex 度、侧摆不超过 spread 度。"""
    d0 = _unit(rest_dir)
    side = _unit(np.cross(palm, d0))
    up = np.cross(d0, side)                     # 掌心方向里和骨段垂直的分量
    t = _unit(target)
    a, f, l = float(t @ d0), float(t @ up), float(t @ side)
    bend = np.clip(np.arctan2(f, a), *np.radians(flex))
    sway = np.clip(np.arctan2(l, np.hypot(a, f)), -np.radians(spread), np.radians(spread))
    return np.cos(sway) * (np.cos(bend) * d0 + np.sin(bend) * up) + np.sin(sway) * side


def _clamp_angle(rest_dir: np.ndarray, target: np.ndarray, limit: float) -> np.ndarray:
    """目标方向和静止方向的夹角不超过 limit 度。"""
    d0, t = _unit(rest_dir), _unit(target)
    angle = np.arccos(np.clip(d0 @ t, -1, 1))
    if angle <= np.radians(limit):
        return t
    return R.from_rotvec(np.radians(limit) / angle * _swing(d0, t).as_rotvec()).apply(d0)


def _chirality(wrist, index, middle, pinky, thumb_mcp, thumb_ip) -> float:
    """手性：拇指（掌指、指间关节中点）在四指掌面法线方向上的偏移（按手掌长度归一）。左手正、右手负（MHR 模型系）。"""
    x, y = middle - wrist, index - pinky
    n = np.cross(x, y)
    n /= max(np.linalg.norm(n), 1e-9)
    return float(n @ ((thumb_mcp + thumb_ip) / 2 - (index + pinky) / 2) / max(np.linalg.norm(x), 1e-9))


def _mp_chirality(p: np.ndarray) -> float:
    return _chirality(p[0], p[5], p[9], p[17], p[2], p[3])


def detect(rgb: np.ndarray, human: dict) -> dict[str, np.ndarray]:
    """在原图（RGB）里找这个人的两只手。返回 {"l" / "r": (21, 3) MediaPipe 的关键点，MHR 模型系、手腕为原点（米）}，
    认不出的手不在结果里。"""
    import mediapipe as mp

    names = _names()
    h, w = rgb.shape[:2]
    uv = _project(human, (w, h))
    f = float(human["focal_length"])
    out: dict[str, np.ndarray] = {}
    for side in "lr":
        pts = uv[_hand_joints(names, side)]
        wrist = uv[names.index(f"{side}_wrist")]
        if not (0 <= wrist[0] < w and 0 <= wrist[1] < h):
            continue
        center = pts.mean(axis=0)
        spread = max(float(np.ptp(pts[:, 0])), float(np.ptp(pts[:, 1])), MIN_SPREAD_PX)
        for scale in CROP_SCALES:
            half = spread * scale / 2
            x0, y0 = int(max(0, center[0] - half)), int(max(0, center[1] - half))
            x1, y1 = int(min(w, center[0] + half)), int(min(h, center[1] + half))
            if x1 - x0 < 16 or y1 - y0 < 16:
                break
            crop = np.ascontiguousarray(rgb[y0:y1, x0:x1])
            with _lock:
                res = _get_landmarker().detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=crop))
            best = None
            for marks, world in zip(res.hand_landmarks, res.hand_world_landmarks):
                d = float(np.linalg.norm([marks[0].x * (x1 - x0) + x0 - wrist[0], marks[0].y * (y1 - y0) + y0 - wrist[1]]))
                if d < WRIST_MATCH * spread and (best is None or d < best[0]):
                    best = (d, np.array([[q.x, q.y, q.z] for q in world], float))
            if best is None:
                continue
            # 裁图正对着看 → 相机系：转到裁图中心的视线方向
            ray = np.array([((x0 + x1) / 2 - w / 2) / f, ((y0 + y1) / 2 - h / 2) / f, 1.0])
            cam = _swing(np.array([0.0, 0.0, 1.0]), ray).apply(best[1])
            pts = (cam - cam[0]) * _FLIP
            if (_mp_chirality(pts) > 0) != (side == "l"):
                pts = pts * np.array([1.0, 1.0, -1.0])               # 镜像的手：远近翻过来
            out[side] = pts
            break
    return out


def solve(human: dict, side: str, points: np.ndarray) -> dict[int, R]:
    """MediaPipe 的关键点（detect 的结果） → 这只手各关节新的全局转动（MHR 模型系），键是关节序号。
    手腕保留 SAM 的朝向（结果里照样有它），手指按对齐到 SAM 手掌后的关键点。"""
    names = _names()
    ix = names.index
    G = R.from_matrix(np.asarray(human["joint_global_rots"], float))
    Gr = R.from_matrix(np.asarray(human["rest_joint_rots"], float))
    rest = np.asarray(human["rest_joint_coords"], float)
    pos = np.asarray(human["joint_coords"], float) * _FLIP
    w = ix(f"{side}_wrist")
    sam_dirs = _unit(np.array([pos[ix(f"{side}_{j}")] - pos[w] for j, _ in PALM]))
    mp_dirs = _unit(np.array([points[m] - points[0] for _, m in PALM]))
    turn, _ = R.align_vectors(sam_dirs, mp_dirs)        # MediaPipe 的手掌 → SAM 的手掌
    points = turn.apply(np.asarray(points, float))
    out = {w: G[w]}
    # 掌心方向（静止姿态里）：四指掌面的法线，按左右手取朝拇指那一侧（拇指在掌心一侧）
    n = np.cross(rest[ix(f"{side}_middle1")] - rest[w], rest[ix(f"{side}_index1")] - rest[ix(f"{side}_pinky1")])
    palm_rest = _unit(n) * (1.0 if side == "l" else -1.0)
    for chain in CHAINS:
        parent = w
        for name in chain:
            j = ix(f"{side}_{name}")
            g = out[parent] * Gr[parent].inv() * Gr[j]          # 先按静止姿态时相对父关节的朝向
            if name in SEGMENTS:
                child = ix(f"{side}_{chain[chain.index(name) + 1]}")
                a, b = SEGMENTS[name]
                moved = g * Gr[j].inv()                          # 静止 → 现在（跟着父关节走）
                seg = moved.apply(rest[child] - rest[j])
                target = points[b] - points[a]
                if name.startswith("thumb"):
                    target = _clamp_angle(seg, target, THUMB_LIMIT)
                else:
                    flex, spread = FINGER_LIMITS[name[-1]]
                    target = _limit(seg, moved.apply(palm_rest), target, flex, spread)
                g = _swing(seg, target) * g
            out[j] = g
            parent = j
    return out


def apply(human: dict, solved: dict[str, dict[int, R]]) -> dict:
    """把 solve 的转动写回（返回新的 dict），手部关节的位置按静止骨长从手腕重新串出来。"""
    if not solved:
        return human
    names = _names()
    rots = np.array(human["joint_global_rots"], float)
    Gr = R.from_matrix(np.asarray(human["rest_joint_rots"], float))
    rest = np.asarray(human["rest_joint_coords"], float)
    pos = np.array(human["joint_coords"], float) * _FLIP
    for side, rot in solved.items():
        for j, g in rot.items():
            rots[j] = g.as_matrix()
        w = names.index(f"{side}_wrist")
        for chain in CHAINS:
            parent = w
            for name in chain:
                j = names.index(f"{side}_{name}")
                pos[j] = pos[parent] + (R.from_matrix(rots[parent]) * Gr[parent].inv()).apply(rest[j] - rest[parent])
                parent = j
    return {**human, "joint_global_rots": rots, "joint_coords": pos * _FLIP}


def refine(rgb: np.ndarray, human: dict) -> tuple[dict, list[str]]:
    """照片：认得出的手换成 MediaPipe 的手指。返回 (新的 human, 换了的手 ["l", "r"])。"""
    found = detect(rgb, human)
    return apply(human, {side: solve(human, side, pts) for side, pts in found.items()}), sorted(found)


def _slerp(a: R, b: R, t: float) -> R:
    return a * R.from_rotvec(t * (a.inv() * b).as_rotvec())


def refine_track(humans: list[dict | None], found: list[dict[str, np.ndarray] | None], fps: float) -> list[dict | None]:
    """视频：一个人每帧的识别结果 + 每帧 detect 的结果 → 换好手指的识别结果。
    按「每节手指相对父关节」的局部转动处理（手腕始终是 SAM 的）：认出的帧用 MediaPipe 的，空档不超过 GAP_SECONDS 的按前后插值，
    认出的段落两头用 BLEND_SECONDS 和 SAM 的结果过渡，再在时间上稍微平滑。"""
    from .video import smooth_quats

    names = _names()
    n = len(humans)
    out = list(humans)
    gap, blend = max(1, round(GAP_SECONDS * fps)), max(1, round(BLEND_SECONDS * fps))
    for side in "lr":
        joints = _hand_joints(names, side)[1:]            # 不含手腕
        parents = _chain_parents(names, side)[1:]
        # 每帧的局部转动：SAM 的（全部有识别的帧）、MediaPipe 的（认出的帧）
        sam = np.full((n, len(joints), 4), np.nan)
        hand = np.full((n, len(joints), 4), np.nan)
        for k, h in enumerate(humans):
            if h is None:
                continue
            G = R.from_matrix(np.asarray(h["joint_global_rots"], float))
            sam[k] = _locals(G, joints, parents).as_quat()
            if found[k] and side in found[k]:
                new = solve(h, side, found[k][side])
                hand[k] = _locals(R.from_quat(np.array([(new[j] if j in new else G[j]).as_quat() for j in range(len(G))])),
                                  joints, parents).as_quat()
        seen = np.flatnonzero(~np.isnan(hand[:, 0, 0]))
        if len(seen) == 0:
            continue
        weight = np.zeros(n)
        target = np.full_like(hand, np.nan)
        for k in range(n):
            if np.isnan(sam[k, 0, 0]):
                continue
            after = seen[seen >= k]
            before = seen[seen <= k]
            a = before[-1] if len(before) else None
            b = after[0] if len(after) else None
            if a is not None and b is not None and (a == b or b - a <= gap):
                t = 0.0 if a == b else (k - a) / (b - a)
                target[k] = np.array([_slerp(R.from_quat(hand[a, i]), R.from_quat(hand[b, i]), t).as_quat() for i in range(len(joints))])
                weight[k] = 1.0
            else:
                near = a if b is None or (a is not None and k - a <= b - k) else b
                d = abs(k - near)
                weight[k] = max(0.0, 1.0 - d / (blend + 1))
                target[k] = hand[near]
        have = weight > 0
        if not have.any():
            continue
        for i in range(len(joints)):
            q = np.where(have[:, None], target[:, i], np.nan_to_num(sam[:, i]))
            q[~have & np.isnan(sam[:, i, 0])] = [0, 0, 0, 1]
            target[:, i] = smooth_quats(q, SMOOTH_SECONDS * fps)
        for k in np.flatnonzero(have):
            h = out[k]
            G = R.from_matrix(np.asarray(h["joint_global_rots"], float))
            mixed = [_slerp(R.from_quat(sam[k, i]), R.from_quat(target[k, i]), weight[k]) for i in range(len(joints))]
            rot = {}
            for i, (j, p) in enumerate(zip(joints, parents)):
                rot[j] = (G[p] if p not in rot else rot[p]) * mixed[i]
            out[k] = apply(h, {side: rot})
    return out


def _chain_parents(names: list[str], side: str) -> list[int]:
    """_hand_joints 顺序的每个关节的父关节：手腕的父是前臂（只占位），各手指链从手腕串下去。"""
    parents = [names.index(f"{side}_{FOREARM}")]
    for chain in CHAINS:
        parent = names.index(f"{side}_wrist")
        for name in chain:
            parents.append(parent)
            parent = names.index(f"{side}_{name}")
    return parents


def _locals(G: R, joints: list[int], parents: list[int]) -> R:
    return R.concatenate([G[p].inv() * G[j] for j, p in zip(joints, parents)])
