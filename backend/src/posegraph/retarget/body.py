"""身体部位尺寸：头 / 胸 / 髋在静止姿态下的包围范围，用于手部末端补偿换算接触位置。

两套骨架比例不同，手贴在头 / 胸 / 髋上时，"手相对部位的偏移"要按部位实际尺寸换算，
否则拉拉菲尔（头大）拳头会陷进脸里、鲁加（腹厚）叉腰的手会陷进腰里。
两边的部位关节解剖位置并不一致（MHR 的 c_head 在颈顶，FFXIV 的 j_kao 在头部中间），
所以偏移相对"部位包围盒中心"换算：头取下巴—头顶、脸前—后脑的中点；胸 / 髋取截面前后的中点（高度仍按关节）。
FF14 一侧按各种族网格在绑定姿势下量取（data/body_extents.json，scripts/build_body_extents.py 生成），
识别一侧按 SAM 3D Body 给出的该人物零姿态网格量取，量法相同。
"""

from __future__ import annotations

import json
from functools import cache
from pathlib import Path

import numpy as np

DATA = Path(__file__).with_name("data") / "body_extents.json"


def _pct(v: np.ndarray, q: float) -> float:
    return float(np.percentile(v, q)) if len(v) else 0.0


def _sides(dz: np.ndarray) -> tuple[float, float]:
    """前、后厚度分开统计（截面前后顶点数常不均衡，整体分位数会偏）。"""
    return _pct(dz[dz > 0], 95), _pct(-dz[dz < 0], 95)


def measure(body: np.ndarray, head: np.ndarray, J: dict[str, np.ndarray]) -> dict[str, dict[str, float]]:
    """body / head：静止姿态网格顶点（模型空间，y 上、z 前、+x 角色左，米）；head 为空时从 body 里取头部。
    J：head（头关节）、neck、chest、hip（两侧大腿根中点）、shoulder_l/r、upleg_l/r 的位置。
    返回 {部位: {half_x, half_z, cz, [half_y, cy]}}：半宽、半厚、包围盒中心相对部位关节的前后（与上下）偏移，
    取分位数，避开耳朵、角等突出物。"""
    torso = float(np.linalg.norm(J["neck"] - J["hip"]))
    shoulder_half = float(abs(J["shoulder_l"][0] - J["shoulder_r"][0])) / 2
    upleg_half = float(abs(J["upleg_l"][0] - J["upleg_r"][0])) / 2

    c = J["head"]
    if len(head) == 0:
        mask = (body[:, 1] > J["neck"][1]) & (np.abs(body[:, 0] - c[0]) < 0.8 * shoulder_half)
        head = body[mask]
    top = _pct(head[head[:, 1] > c[1]][:, 1], 98)
    height = top - c[1]
    slab = head[np.abs(head[:, 1] - (c[1] + 0.45 * height)) < 0.15 * height]
    front, back = _sides(slab[:, 2] - c[2])
    face = slab[slab[:, 2] > c[2] + 0.3 * front]   # 头宽只看脸的前半部分：耳朵（拉拉菲尔、精灵）、角会把宽度撑大
    # 下巴：最靠前那部分顶点里最低处（脖子在下巴后方，不会被选进来）
    chin_zone = head[head[:, 2] > c[2] + 0.5 * front]
    chin = _pct(chin_zone[:, 1], 2) if len(chin_zone) else c[1] - height
    out = {"head": {"half_x": _pct(np.abs(face[:, 0] - c[0]), 95), "half_z": (front + back) / 2, "cz": (front - back) / 2,
                    "half_y": (top - chin) / 2, "cy": (top + chin) / 2 - c[1]}}

    def section(center: np.ndarray, lateral: float) -> dict[str, float]:
        s = body[(np.abs(body[:, 1] - center[1]) < 0.04 * torso) & (np.abs(body[:, 0] - center[0]) < lateral)]
        f, k = _sides(s[:, 2] - center[2])
        return {"half_x": _pct(np.abs(s[:, 0] - center[0]), 95), "half_z": (f + k) / 2, "cz": (f - k) / 2}

    out["chest"] = section(J["chest"], 0.95 * shoulder_half)
    out["hip"] = section(J["hip"], 2.5 * upleg_half)
    return out


@cache
def ffxiv_extents(race: str) -> dict[str, dict[str, float]] | None:
    if not DATA.exists():
        return None
    data = json.loads(DATA.read_text(encoding="utf-8"))
    return data.get(race)


def bust_scale(reference) -> float:
    """游戏角色胸骨 j_mune 的缩放（胸围滑条、Customize+ 改的就是它）；读不到为 1。"""
    if reference is None:
        return 1.0
    scales = [reference.get(n).scale for n in ("j_mune_l", "j_mune_r") if reference.get(n) is not None]
    return float(np.clip(np.mean(scales), 0.5, 2.0)) if scales else 1.0


def chest_for_bust(ext: dict, scale: float) -> dict:
    """按胸骨缩放调整胸部尺寸：每放大 1，胸厚与中心前移各加 bust_slope（胸前表面前移，后背不动，宽度几乎不变）。
    bust_slope 只有女性有（Blender 里把 j_mune 放大 1.5 倍量出来的）。"""
    slope = ext.get("chest", {}).get("bust_slope")
    if not slope or abs(scale - 1) < 1e-3:
        return ext
    d = slope * (scale - 1)
    return {**ext, "chest": {**ext["chest"], "half_z": ext["chest"]["half_z"] + d, "cz": ext["chest"]["cz"] + d}}


def mhr_extents(rest_vertices: np.ndarray, rest_coords: np.ndarray, names: list[str]) -> dict[str, dict[str, float]]:
    j = lambda n: rest_coords[names.index(n)]
    J = {"head": j("c_head"), "neck": j("c_neck"), "chest": j("c_spine3"), "hip": (j("l_upleg") + j("r_upleg")) / 2,
         "shoulder_l": j("l_uparm"), "shoulder_r": j("r_uparm"), "upleg_l": j("l_upleg"), "upleg_r": j("r_upleg")}
    return measure(rest_vertices, np.zeros((0, 3)), J)


def map_offset(local: np.ndarray, ff: dict, mhr: dict, torso_scale: float) -> np.ndarray:
    """静止朝向下（x 侧、y 上、z 前）的偏移：识别部位 → FFXIV 部位。
    部位近似为椭球（头）/ 椭圆柱（胸、髋，y 方向按躯干长度比例）：
    手在表面以内时相对包围盒中心按半宽 / 半厚 / 半高缩放；在表面以外时，只把"表面上最近的那一点"按尺寸换算，
    手到表面的空隙按整体体型比例保留（贴近但没碰到的手，空隙不会被缩没，例如健美图拳头离下巴几厘米）。"""
    ratio = lambda k: ff[k] / mhr[k] if mhr.get(k, 0) > 1e-4 else 1.0
    c_m = np.array([0.0, mhr.get("cy", 0.0), mhr["cz"]])
    c_f = np.array([0.0, ff.get("cy", 0.0), ff["cz"]])
    s = np.array([ratio("half_x"), ratio("half_y") if "half_y" in ff else torso_scale, ratio("half_z")])
    d = local - c_m
    axes = [0, 1, 2] if "half_y" in ff else [0, 2]
    half = np.array([mhr["half_x"], mhr.get("half_y", 1.0), mhr["half_z"]])
    r = float(np.linalg.norm(d[axes] / half[axes]))
    if r <= 1:
        return c_f + d * s
    surface = d.copy()
    surface[axes] = d[axes] / r
    return c_f + surface * s + (d - surface) * torso_scale
