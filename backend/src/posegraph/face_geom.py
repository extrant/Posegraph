"""照片表情的几何测量：把 MediaPipe 的 478 个关键点对齐到它自带的标准脸（厘米），量出各部位相对中性脸的偏离。

标准脸（canonical face model，468 个顶点，厘米）和对齐用的 33 个稳定点权重都在 face_landmarker.task 里的
geometry_pipeline_metadata_landmarks.binarypb（MediaPipe 自己去掉头部姿态也用这一套），这里直接读出来。
坐标：+x 是被拍者的左边，+y 朝上，+z 朝镜头（和 FF14 面部骨架的模型空间同向：+x 角色左侧、+y 上、+z 脸朝向）。

和表情系数（blendshapes，分类器给的 0~1 打分）比，几何量是实打实的位移：歪嘴、单侧嘴角、半睁眼都能按实际幅度量出来。

中性基准：MediaPipe 的标准脸嘴唇微张（上下唇内缘差 0.55cm）、眼睛偏眯，直接拿它当中性会把闭嘴量成「上唇下拉」、
正常睁眼量成「睁大」。所以另存了一份「典型中性脸」data/face_neutral.npy：2026-10-28 用测试图里表情系数都低于 0.35 的 8 张脸
对齐到标准脸后逐点取中位数（只有几何坐标，不含图片）。测量都相对它。嘴部用相对距离（鼻底到上唇、下唇到下巴、鼻底到下巴），
不受整体对齐误差影响。
"""

from __future__ import annotations

import struct
import zipfile
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

import numpy as np

from .config import settings

# 关键点编号（MediaPipe 468 网格；r / l 是被拍者自己的右 / 左）
EYE = {"r": dict(upper=159, lower=145, outer=33, inner=133), "l": dict(upper=386, lower=374, outer=263, inner=362)}
BROW = {"r": dict(outer=70, mid=105, inner=107), "l": dict(outer=300, mid=334, inner=336)}
CORNER = {"r": 61, "l": 291}
UPPER_LIP_IN, UPPER_LIP_OUT, LOWER_LIP_IN, LOWER_LIP_OUT, CHIN, NOSE_BASE = 13, 0, 14, 17, 152, 2
IRIS_CENTERS = (468, 473)


@dataclass(frozen=True)
class Canonical:
    verts: np.ndarray        # (468, 3) 厘米
    basis: np.ndarray        # 稳定点编号
    weights: np.ndarray      # 稳定点权重


def _protobuf_fields(b: bytes):
    """最小的 protobuf 解码：逐个给出 (字段号, 类型, 值)。"""
    def varint(i):
        r = s = 0
        while True:
            c = b[i]
            i += 1
            r |= (c & 0x7F) << s
            s += 7
            if c < 0x80:
                return r, i
    i = 0
    while i < len(b):
        key, i = varint(i)
        fn, wt = key >> 3, key & 7
        if wt == 0:
            v, i = varint(i)
        elif wt == 2:
            n, i = varint(i)
            v, i = b[i:i + n], i + n
        elif wt == 5:
            v, i = b[i:i + 4], i + 4
        elif wt == 1:
            v, i = b[i:i + 8], i + 8
        else:
            raise ValueError(f"protobuf wire type {wt}")
        yield fn, wt, v


@lru_cache(maxsize=1)
def canonical() -> Canonical:
    """从 face_landmarker.task 读标准脸：GeometryPipelineMetadata { canonical_mesh = 1 (vertex_buffer = 3：每顶点 xyzuv),
    procrustes_landmark_basis = 2 (landmark_id = 1, weight = 2) }。"""
    data = zipfile.ZipFile(settings.face_model).read("geometry_pipeline_metadata_landmarks.binarypb")
    verts, basis = [], []
    for fn, _, v in _protobuf_fields(data):
        if fn == 1:
            for f2, w2, v2 in _protobuf_fields(v):
                if f2 == 3:
                    verts.extend(struct.unpack(f"<{len(v2) // 4}f", v2) if w2 == 2 else struct.unpack("<f", v2))
        elif fn == 2:
            d = {f2: v2 for f2, _, v2 in _protobuf_fields(v)}
            basis.append((d.get(1, 0), struct.unpack("<f", d[2])[0] if 2 in d else 0.0))
    v = np.asarray(verts, dtype=np.float64).reshape(-1, 5)[:, :3]
    b = np.asarray(basis)
    return Canonical(v, b[:, 0].astype(int), b[:, 1])


@lru_cache(maxsize=1)
def neutral() -> np.ndarray:
    """典型中性脸（468×3，标准脸空间厘米）；文件不在时退回标准脸。"""
    path = Path(__file__).with_name("data") / "face_neutral.npy"
    return np.load(path).astype(np.float64) if path.is_file() else canonical().verts


def align(landmarks: np.ndarray) -> np.ndarray:
    """照片关键点（像素 x 右、y 下、z 越小越近，同一尺度）→ 标准脸空间（厘米）。按稳定点加权求相似变换（旋转 + 平移 + 等比缩放）。"""
    c = canonical()
    p = np.asarray(landmarks, dtype=np.float64)[:, :3] * [1.0, -1.0, -1.0]
    src, dst, w = p[c.basis], c.verts[c.basis], c.weights / c.weights.sum()
    ms, md = (w[:, None] * src).sum(0), (w[:, None] * dst).sum(0)
    a, b = src - ms, dst - md
    cov = (w[:, None] * b).T @ a
    u, s, vt = np.linalg.svd(cov)
    d = np.sign(np.linalg.det(u @ vt))
    rot = u @ np.diag([1, 1, d]) @ vt
    scale = (s * [1, 1, d]).sum() / (w * (a ** 2).sum(1)).sum()
    return (scale * (rot @ (p - ms).T)).T + md


def align_2d(landmarks: np.ndarray, rotation: np.ndarray, flat_limit: float = 0.35) -> np.ndarray:
    """不用关键点深度的对齐（低头、侧脸、透视大时深度误差很大）：用 MediaPipe 的脸部朝向把典型中性脸转到照片里的角度，
    在画面上只求缩放和平移（稳定点加权最小二乘），每个点的画面偏离再按脸的朝向换回脸平面上的位移（假设表情位移在脸平面内）。
    返回「中性脸 + 位移」（468×3，标准脸空间厘米），和 align 的结果一样用。
    rotation：标准脸 → 相机（OpenGL：x 右、y 上、z 朝镜头外）。脸侧过去时横向那一维被压扁，flat_limit 以下按正则处理，不把噪声放大。"""
    c, n = canonical(), neutral()
    rot = np.asarray(rotation, dtype=np.float64)
    q = n @ rot.T
    q2 = np.c_[q[:, 0], -q[:, 1]]                         # 画面 y 朝下
    p2 = np.asarray(landmarks, dtype=np.float64)[:468, :2]
    w = c.weights / c.weights.sum()
    qb, pb = q2[c.basis], p2[c.basis]
    mq, mp = (w[:, None] * qb).sum(0), (w[:, None] * pb).sum(0)
    scale = (w * ((qb - mq) * (pb - mp)).sum(1)).sum() / (w * ((qb - mq) ** 2).sum(1)).sum()
    residual = p2 - (scale * (q2 - mq) + mp)
    # 脸平面的 x、y 轴在画面上的样子；按最小二乘（带一点正则）解出脸平面上的位移
    basis = scale * np.array([[rot[0, 0], rot[0, 1]], [-rot[1, 0], -rot[1, 1]]])
    lam = (flat_limit * scale) ** 2 * 0.1
    disp = np.linalg.solve(basis.T @ basis + lam * np.eye(2), basis.T @ residual.T).T
    out = n.copy()
    out[:, :2] += disp
    return out                                             # 瞳孔点没有中性位置，不换算（视线用表情系数）


def measure(aligned: np.ndarray) -> dict[str, float]:
    """各部位相对典型中性脸的偏离（厘米或比例）。键名带 _l / _r 的是被拍者的左 / 右。"""
    v0 = neutral()
    P = aligned
    d = lambda i: P[i] - v0[i]
    out: dict[str, float] = {}
    for s, e in EYE.items():
        corners = (P[e["outer"]] + P[e["inner"]]) / 2
        corners0 = (v0[e["outer"]] + v0[e["inner"]]) / 2
        out[f"eye_open_{s}"] = float((P[e["upper"]][1] - P[e["lower"]][1]) / (v0[e["upper"]][1] - v0[e["lower"]][1]))
        out[f"lower_lid_{s}"] = float((P[e["lower"]][1] - corners[1]) - (v0[e["lower"]][1] - corners0[1]))
        b = BROW[s]
        for part in ("outer", "mid", "inner"):
            out[f"brow_{part}_{s}"] = float(d(b[part])[1])
    out["brow_gap"] = float(abs(P[BROW["l"]["inner"]][0] - P[BROW["r"]["inner"]][0]) - abs(v0[BROW["l"]["inner"]][0] - v0[BROW["r"]["inner"]][0]))
    for s, i in CORNER.items():
        outward = 1.0 if s == "l" else -1.0
        out[f"corner_out_{s}"] = float(d(i)[0] * outward)
        out[f"corner_up_{s}"] = float(d(i)[1])
        out[f"corner_fwd_{s}"] = float(d(i)[2])
    gap = lambda a, b, ref: (ref[a][1] - ref[b][1])   # a 在 b 上方多少
    out["upper_lip_up"] = float(gap(NOSE_BASE, UPPER_LIP_IN, v0) - gap(NOSE_BASE, UPPER_LIP_IN, P))      # 鼻底到上唇变短 = 上唇上提
    out["lower_lip_down"] = float(gap(LOWER_LIP_IN, CHIN, v0) - gap(LOWER_LIP_IN, CHIN, P))           # 下唇到下巴变短 = 下唇往下拉开
    out["jaw_open"] = float(gap(NOSE_BASE, CHIN, P) - gap(NOSE_BASE, CHIN, v0))                        # 鼻底到下巴变长 = 张嘴
    out["lips_apart"] = float(gap(UPPER_LIP_IN, LOWER_LIP_IN, P) - gap(UPPER_LIP_IN, LOWER_LIP_IN, v0))
    out["upper_lip_fwd"] = float(d(UPPER_LIP_OUT)[2])
    out["lower_lip_fwd"] = float(d(LOWER_LIP_OUT)[2])
    out["jaw_side"] = float(d(CHIN)[0])               # + 朝被拍者的左边
    out["mouth_side"] = float((d(CORNER["l"])[0] + d(CORNER["r"])[0]) / 2)
    if len(P) >= 478:
        for s, e in EYE.items():
            center = (P[e["outer"]] + P[e["inner"]]) / 2
            iris = min((P[i] for i in IRIS_CENTERS), key=lambda q: np.linalg.norm(q[:2] - center[:2]))
            half = np.linalg.norm(P[e["outer"]][:2] - P[e["inner"]][:2]) / 2
            out[f"gaze_x_{s}"] = float((iris[0] - center[0]) / half)                 # + 朝被拍者的左边
            out[f"gaze_y_{s}"] = float((iris[1] - (P[e["upper"]][1] + P[e["lower"]][1]) / 2) / half)   # + 朝上
    return out


def eye_distance() -> float:
    """典型中性脸两眼中心的距离（厘米），换算到游戏脸的尺寸用。"""
    v0 = neutral()
    c = lambda e: (v0[e["outer"]] + v0[e["inner"]]) / 2
    return float(np.linalg.norm(c(EYE["l"]) - c(EYE["r"])))
