"""
面部表情（面捕）：SAM 3D Body 不出表情（表情和下巴在模型里被清零），这里用 MediaPipe Face Landmarker
在每个人的头部区域里找脸，得到 52 个 ARKit 风格的表情系数（blendshapes）和头部朝向。

置信度：MediaPipe 只给「找到 / 没找到」，这里再按三件事打分，乘起来：
- 对得上：找到的脸的鼻尖要在 SAM 识别的鼻子附近（否则是旁边的人或背景里的脸）
- 够大：脸太小（几十像素）时表情系数基本是噪声
- 正脸：侧过去太多时另一半脸是猜的
低于 MIN_CONFIDENCE 不用（used = False，reason 说明原因）。插画的脸 MediaPipe 多半找不到，也会不用。
"""

from __future__ import annotations

import threading
from dataclasses import dataclass, field

import numpy as np

from .config import settings

MIN_CONFIDENCE = 0.5
HEAD_KEYPOINTS = (0, 1, 2, 3, 4)    # MHR70：鼻、左眼、右眼、左耳、右耳
SHOULDERS = (5, 6)
NOSE_TIP = 1                        # MediaPipe 脸部网格的鼻尖
FULL_SIZE_PX, MIN_SIZE_PX = 72.0, 24.0   # 脸宽（预览图像素）到这么大满分、小于这么大零分
FULL_YAW_DEG, MAX_YAW_DEG = 45.0, 75.0   # 侧脸到这么多度满分、超过这么多度零分
ROLL_RETRY_DEG = 15.0                    # 两眼连线歪过这么多度时摆正再找一次

_lock = threading.Lock()
_landmarker = None


@dataclass
class FaceResult:
    found: bool
    confidence: float
    used: bool
    reason: str = ""
    blendshapes: dict[str, float] = field(default_factory=dict)
    yaw_deg: float = 0.0
    pitch_deg: float = 0.0
    box: list[float] = field(default_factory=list)   # 找脸用的裁剪框（预览图像素 x1,y1,x2,y2）
    landmarks: list = field(default_factory=list)    # 478 个关键点（预览图像素 x、y，z 同尺度、越小越近），几何测量用（face_geom）
    rotation: list = field(default_factory=list)     # 脸的朝向（3×3，标准脸 → 相机，OpenGL 约定：x 右、y 上、z 朝镜头外），裁剪图视角

    def to_json(self) -> dict:
        return {"found": self.found, "confidence": round(self.confidence, 3), "used": self.used, "reason": self.reason,
                "blendshapes": {k: round(v, 3) for k, v in self.blendshapes.items()},
                "yawDeg": round(self.yaw_deg, 1), "pitchDeg": round(self.pitch_deg, 1),
                "box": [round(v, 1) for v in self.box]}


def available() -> bool:
    try:
        import mediapipe  # noqa: F401
    except ImportError:
        return False
    return settings.face_model.is_file()


def _get_landmarker():
    global _landmarker
    if _landmarker is None:
        from mediapipe.tasks.python import BaseOptions, vision

        # 路径含中文时 C 层打不开，按字节给模型
        options = vision.FaceLandmarkerOptions(
            base_options=BaseOptions(model_asset_buffer=settings.face_model.read_bytes()),
            running_mode=vision.RunningMode.IMAGE, num_faces=4,   # 裁剪框里常有别人的脸，挑鼻尖最近的
            min_face_detection_confidence=0.3, min_face_presence_confidence=0.3,
            output_face_blendshapes=True, output_facial_transformation_matrixes=True)
        _landmarker = vision.FaceLandmarker.create_from_options(options)
    return _landmarker


def head_box(keypoints_2d: np.ndarray, scale: float, size: tuple[int, int]) -> list[float] | None:
    """按 SAM 的头部关键点（原图像素，乘 scale 换到预览图）定一个正方形裁剪框；头不在画面里时为 None。"""
    k = np.asarray(keypoints_2d, dtype=float)[:, :2] * scale
    head = k[list(HEAD_KEYPOINTS)]
    w, h = size
    spread = float(np.ptp(head, axis=0).max())
    shoulders = float(np.linalg.norm(k[SHOULDERS[0]] - k[SHOULDERS[1]]))
    side = max(3.0 * spread, 0.8 * shoulders, 32.0)
    cx, cy = head.mean(axis=0)
    x1, y1, x2, y2 = cx - side / 2, cy - side / 2, cx + side / 2, cy + side / 2
    if x2 <= 0 or y2 <= 0 or x1 >= w or y1 >= h:
        return None
    return [max(0.0, x1), max(0.0, y1), min(float(w), x2), min(float(h), y2)]


def _angles(matrix: np.ndarray) -> tuple[float, float]:
    """脸部变换矩阵 → 左右转（yaw）、上下点头（pitch），度。"""
    r = np.asarray(matrix, dtype=float)[:3, :3]
    r = r / np.linalg.norm(r, axis=0, keepdims=True)
    yaw = np.degrees(np.arcsin(np.clip(-r[2, 0], -1, 1)))
    pitch = np.degrees(np.arctan2(r[2, 1], r[2, 2]))
    return float(yaw), float(pitch)


def score(nose_offset: float, side: float, face_width: float, yaw_deg: float,
          sizes: tuple[float, float] = (MIN_SIZE_PX, FULL_SIZE_PX)) -> tuple[float, str]:
    """三项打分相乘；返回置信度和最拉低分数的原因（满分时为空）。sizes：脸宽零分、满分的像素。"""
    parts = {
        "找到的脸不是这个人的（可能是旁边的人）": float(np.clip(1 - (nose_offset / side - 0.15) / 0.2, 0, 1)),
        "脸太小": float(np.clip((face_width - sizes[0]) / (sizes[1] - sizes[0]), 0, 1)),
        "侧脸太多": float(np.clip((MAX_YAW_DEG - abs(yaw_deg)) / (MAX_YAW_DEG - FULL_YAW_DEG), 0, 1)),
    }
    worst = min(parts, key=parts.get)
    return float(np.prod(list(parts.values()))), worst if parts[worst] < 1 else ""


VIDEO_SIZES = (14.0, 40.0)   # 视频的脸宽打分：远景里的脸常只有三四十像素，照样能找到；表情强度另按脸宽打折（small_face_factor）


def small_face_factor(face_width: float) -> float:
    """小脸的表情打折：脸宽 72 像素以上不打折，越小越淡（最低 0.3）。小脸的关键点和表情系数偏差大（眼睛常被认成半闭）。"""
    return float(np.clip((face_width - VIDEO_SIZES[0]) / (FULL_SIZE_PX - VIDEO_SIZES[0]), 0.3, 1.0))


def analyze(preview_rgb: np.ndarray, keypoints_2d: np.ndarray, image_size,
            sizes: tuple[float, float] = (MIN_SIZE_PX, FULL_SIZE_PX)) -> FaceResult:
    """在预览图（session.preview_png）里找这个人的脸。keypoints_2d 是 SAM 的关键点（原图像素）。
    sizes：脸宽的零分、满分像素（视频用 VIDEO_SIZES，收小脸）。"""
    h, w = preview_rgb.shape[:2]
    scale = w / float(image_size[0])
    box = head_box(keypoints_2d, scale, (w, h))
    if box is None:
        return FaceResult(False, 0.0, False, "头不在画面里")
    x1, y1, x2, y2 = (int(round(v)) for v in box)
    crop = np.ascontiguousarray(preview_rgb[y1:y2, x1:x2, :3])
    if min(crop.shape[:2]) < 16:
        return FaceResult(False, 0.0, False, "脸太小", box=box)
    import cv2

    # 小图放大到 256 再找（Face Landmarker 内部按 192~256 处理，太小的输入找不到）
    zoom = max(1.0, 256.0 / max(crop.shape[:2]))
    if zoom > 1:
        crop = cv2.resize(crop, None, fx=zoom, fy=zoom, interpolation=cv2.INTER_CUBIC)
    k = np.asarray(keypoints_2d, dtype=float)[:, :2] * scale
    nose = k[HEAD_KEYPOINTS[0]]
    # 歪头、低头时先按两眼连线把头摆正再找一次（MediaPipe 对歪得多的脸常找不到），取分高的
    eyes = k[HEAD_KEYPOINTS[1]] - k[HEAD_KEYPOINTS[2]]
    roll = float(np.degrees(np.arctan2(eyes[1], eyes[0])))
    best = None
    for angle in [0.0] + ([roll] if abs(roll) > ROLL_RETRY_DEG else []):
        found = _detect(crop, angle)
        if found is None:
            continue
        pts, matrix, shapes = found
        faces = [p / zoom + [x1, y1, 0] for p in pts]
        i = int(np.argmin([np.linalg.norm(f[NOSE_TIP, :2] - nose) for f in faces]))
        yaw, pitch = _angles(matrix[i]) if matrix else (0.0, 0.0)
        rotation = []
        if matrix:
            m = np.asarray(matrix[i], dtype=float)[:3, :3]
            m = m / np.linalg.norm(m[:, 0])
            # 摆正重试时找到的姿态 = 绕相机 z 转了 angle × 真实姿态（实测），转回去
            c, s_ = np.cos(np.radians(-angle)), np.sin(np.radians(-angle))
            rotation = (np.array([[c, -s_, 0], [s_, c, 0], [0, 0, 1]]) @ m).round(5).tolist()
        conf, reason = score(float(np.linalg.norm(faces[i][NOSE_TIP, :2] - nose)), x2 - x1, float(np.ptp(faces[i][:, 0])), yaw, sizes)
        if best is None or conf > best.confidence:
            best = FaceResult(True, conf, conf >= MIN_CONFIDENCE, "" if conf >= MIN_CONFIDENCE else (reason or "置信度太低"),
                              shapes[i], yaw, pitch, box, faces[i].round(2).tolist(), rotation)
    return best or FaceResult(False, 0.0, False, "没找到脸（插画、遮挡或背对）", box=box)


def _detect(crop: np.ndarray, angle: float):
    """把裁剪图转 angle 度（摆正歪头）后找脸；返回各张脸的关键点（裁剪图像素 x、y、z，已转回去）、变换矩阵和表情系数，没找到为 None。"""
    import cv2
    import mediapipe as mp

    h, w = crop.shape[:2]
    rot = None
    if angle:
        rot = cv2.getRotationMatrix2D((w / 2, h / 2), angle, 1.0)
        crop = cv2.warpAffine(crop, rot, (w, h), borderMode=cv2.BORDER_REPLICATE)
    with _lock:
        result = _get_landmarker().detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=np.ascontiguousarray(crop)))
    if not result.face_landmarks:
        return None
    pts = [np.array([(p.x, p.y, p.z) for p in f]) * [w, h, w] for f in result.face_landmarks]
    if rot is not None:
        back = cv2.invertAffineTransform(rot)
        pts = [np.c_[p[:, :2] @ back[:, :2].T + back[:, 2], p[:, 2]] for p in pts]
    shapes = []
    for i in range(len(pts)):
        s = {c.category_name: float(c.score) for c in result.face_blendshapes[i]} if result.face_blendshapes else {}
        s.pop("_neutral", None)
        shapes.append(s)
    return pts, list(result.facial_transformation_matrixes or []), shapes


HEAD_JOINT = "c_head"
HEAD_MAX_DIFF_DEG = 45.0   # 脸的朝向和 SAM 的头差这么多以上时不信（多半认错了脸）


def head_from_face(human: dict, result: FaceResult, image_size, preview_scale: float, weight: float) -> tuple[dict, float | None]:
    """用面捕的脸部朝向修正 SAM 的头部朝向：SAM 看的是整个人，头只占几十像素；MediaPipe 专看脸，低头、侧脸的角度更准。
    image_size：原图（宽, 高）；preview_scale：预览图 / 原图。返回 (human，改过时是浅拷贝、只换了 c_head 的全局旋转；和 SAM 头部原来差多少度)。weight：0 = 不改，1 = 完全按脸。

    换算：MediaPipe 的朝向是「标准脸 → 相机」，标准脸和 MHR 绑定姿势同向（+x 本人左、+y 上、+z 脸朝前），
    所以头的全局旋转 = 朝向 · 绑定姿势的头。两边都是 OpenGL 相机系（2026-10-28 六张测试图对照：差 2~15°，换成 OpenCV 差 10~87°）。
    MediaPipe 把找脸的裁剪图当作正对相机，裁剪不在画面中间时再补一个「光轴 → 脸方向」的旋转（多数图更接近 SAM）。"""
    from scipy.spatial.transform import Rotation as R

    from .retarget.mhr import MHR_JOINT_NAMES

    if weight <= 0 or not result.used or not result.rotation:
        return human, None
    head = MHR_JOINT_NAMES.index(HEAD_JOINT)
    rots = np.asarray(human["joint_global_rots"], dtype=np.float64)
    rest = np.asarray(human["rest_joint_rots"], dtype=np.float64)[head]
    # 脸中心在原图里的位置（像素，y 朝下）→ 相机看过去的方向（OpenGL：y 上、z 朝镜头外，所以看向 -z）
    pts = np.asarray(result.landmarks, dtype=np.float64)[:468, :2] if result.landmarks else None
    center = (pts.mean(0) if pts is not None else np.array([(result.box[0] + result.box[2]) / 2, (result.box[1] + result.box[3]) / 2])) / preview_scale
    principal = np.asarray(image_size, dtype=np.float64)[:2] / 2
    focal = float(np.ravel(human["focal_length"])[0])
    ray = np.array([center[0] - principal[0], -(center[1] - principal[1]), -focal])
    ray /= np.linalg.norm(ray)
    axis = np.cross([0.0, 0.0, -1.0], ray)
    to_ray = R.from_rotvec(axis / (np.linalg.norm(axis) + 1e-12) * np.arcsin(min(1.0, np.linalg.norm(axis))))
    face_head = to_ray * R.from_matrix(np.asarray(result.rotation)) * R.from_matrix(rest)
    sam_head = R.from_matrix(rots[head])
    diff = float(np.degrees((face_head * sam_head.inv()).magnitude()))
    if diff > HEAD_MAX_DIFF_DEG:
        return human, diff
    blended = (sam_head * R.from_rotvec((sam_head.inv() * face_head).as_rotvec() * min(weight, 1.0))).as_matrix()
    rots = rots.copy()
    rots[head] = blended
    out = dict(human)
    out["joint_global_rots"] = rots
    return out, diff
