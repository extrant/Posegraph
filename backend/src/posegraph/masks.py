"""
合并模式的人物遮罩：每个像素属于谁的标签图（0 没人，k + 1 为会话里的第 k 个人）。

第一次识别后，把每个人的识别网格投影到照片上（近的挡住远的），得到「模型以为」的遮罩；
用户在插件里用画笔 / 橡皮擦 / 魔棒改正（椅子不是人、袖子是这个人的…），再连同放大的人物框交给
SAM 3D Body 重新识别：遮罩作为提示叠加在图像特征上，告诉模型哪些像素是这个人的。
遮罩只说「看得见的像素归谁」，被挡住的部分不在遮罩里，人物框要把被挡住的身体也包进去。

传输格式：标签按行展开的 uint8 字节，zlib 压缩；尺寸为长边不超过 MAX_SIDE 的缩小图（与 image_size 同比例）。
"""

from __future__ import annotations

import zlib

import numpy as np

MAX_SIDE = 1280
MAX_PEOPLE = 32


def label_size(image_size) -> tuple[int, int]:
    """标签图的尺寸（宽、高）：原图等比缩到长边不超过 MAX_SIDE（小图不放大）。"""
    w, h = (int(v) for v in image_size)
    s = min(1.0, MAX_SIDE / max(w, h))
    return max(1, round(w * s)), max(1, round(h * s))


def render_labels(humans: list[dict], image_size, faces: np.ndarray) -> np.ndarray:
    """把每个人的网格投影到照片上，返回 (高, 宽) 的标签图。三角形按深度从远到近画（画家算法），近的覆盖远的。"""
    import cv2

    w, h = label_size(image_size)
    s = w / float(image_size[0])
    tris, depth, owner = [], [], []
    for k, hu in enumerate(humans):
        v = np.asarray(hu["vertices"], dtype=np.float64) + np.asarray(hu["cam_t"], dtype=np.float64).reshape(1, 3)
        f = float(np.ravel(hu["focal_length"])[0])
        z = v[:, 2]
        uv = np.stack([f * v[:, 0] / np.maximum(z, 1e-6) + image_size[0] / 2,
                       f * v[:, 1] / np.maximum(z, 1e-6) + image_size[1] / 2], axis=1) * s
        ok = (z[faces] > 1e-3).all(axis=1)
        tris.append(uv[faces[ok]])
        depth.append(z[faces[ok]].mean(axis=1))
        owner.append(np.full(int(ok.sum()), k + 1, dtype=np.uint8))
    labels = np.zeros((h, w), dtype=np.uint8)
    if not tris:
        return labels
    tris, depth, owner = np.concatenate(tris), np.concatenate(depth), np.concatenate(owner)
    order = np.argsort(-depth, kind="stable")
    pts = np.round(tris[order] * 16).astype(np.int32)   # 4 位小数精度（shift=4）
    owner = owner[order]
    # 相邻的同一个人的三角形一次画完
    cuts = np.flatnonzero(np.diff(owner)) + 1
    for a, b in zip(np.r_[0, cuts], np.r_[cuts, len(owner)]):
        cv2.fillPoly(labels, list(pts[a:b]), int(owner[a]), lineType=cv2.LINE_8, shift=4)
    return labels


def encode(labels: np.ndarray) -> bytes:
    return zlib.compress(np.ascontiguousarray(labels, dtype=np.uint8).tobytes(), 6)


def decode(data: bytes, width: int, height: int, people: int) -> np.ndarray:
    """插件传回的标签图 → (高, 宽) uint8。格式不对抛 ValueError。"""
    if not 0 < width <= 8192 or not 0 < height <= 8192:
        raise ValueError("遮罩尺寸不对")
    try:
        raw = zlib.decompress(data, bufsize=width * height)
    except zlib.error as ex:
        raise ValueError(f"遮罩数据损坏：{ex}") from None
    if len(raw) != width * height:
        raise ValueError("遮罩数据长度与尺寸不符")
    labels = np.frombuffer(raw, dtype=np.uint8).reshape(height, width)
    if labels.max(initial=0) > people:
        raise ValueError("遮罩里的人数比人物框多")
    return labels


def per_person(labels: np.ndarray, image_size, people: int) -> np.ndarray:
    """标签图 → 每个人一张原图尺寸的 0/1 遮罩，(人数, 高, 宽) uint8（SAM 3D Body 的 masks 参数）。"""
    import cv2

    w, h = (int(v) for v in image_size)
    full = cv2.resize(labels, (w, h), interpolation=cv2.INTER_NEAREST) if labels.shape != (h, w) else labels
    return np.stack([(full == k + 1).astype(np.uint8) for k in range(people)])


def image_rgb(rgb: np.ndarray) -> np.ndarray:
    """原图缩到标签图尺寸（插件的魔棒按它取色，坐标和标签图一致）。"""
    import cv2

    w, h = label_size((rgb.shape[1], rgb.shape[0]))
    return cv2.resize(rgb, (w, h), interpolation=cv2.INTER_AREA) if (w, h) != (rgb.shape[1], rgb.shape[0]) else rgb
