"""种族骨架：模型编号 cXX01（XX 为种族/部族与性别，男为奇数、女为偶数，见 RACES）、显示名、验证状态，以及按骨长推断种族。

IVCS 2.2 的 18 套骨架骨骼名与父子结构完全相同，区别只在绑定姿势（骨长、朝向）。我们用到的骨骼按绑定姿势只有 7 种：
男：中原系（中原 / 高地 / 猫魅 / 敖龙 / 维埃拉，只有耳朵挂点不同）、精灵、鲁加系（鲁加 / 硌狮）；
女：中原系（中原 / 高地 / 维埃拉）、精灵系（精灵 / 鲁加 / 硌狮）、猫魅系（猫魅 / 敖龙）；拉拉菲尔男女相同。
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np


@dataclass(frozen=True)
class RaceInfo:
    code: str
    name: str
    male: bool
    group: str          # 绑定姿势相同（只差耳朵等不写入的骨骼）的骨架归为一组
    validated: bool     # 游戏内实测过
    # 女性骨架 2026-10-26 离线核对：游戏原版身体 + IVCS 骨架，骨段方向误差、合理性提示条数都与对应男性骨架组一致，
    # Blender 里肩、腋下、肘、手抱头正常；重定向规则（辅助骨、合理性阈值）沿用男性的，尚待游戏内复测


def _r(code, name, male, group, validated=False):
    return RaceInfo(code, name, male, group, validated)


RACES: dict[str, RaceInfo] = {r.code: r for r in [
    _r("c0101", "人族中原 男", True, "中原系男", validated=True),
    _r("c0301", "人族高地 男", True, "中原系男", validated=True),
    _r("c0701", "猫魅 男", True, "中原系男", validated=True),
    _r("c1301", "敖龙 男", True, "中原系男", validated=True),
    _r("c1701", "维埃拉 男", True, "中原系男", validated=True),
    _r("c0501", "精灵 男", True, "精灵男", validated=True),
    _r("c0901", "鲁加 男", True, "鲁加系男"),
    _r("c1501", "硌狮 男", True, "鲁加系男"),
    _r("c1101", "拉拉菲尔 男", True, "拉拉菲尔"),
    _r("c0201", "人族中原 女", False, "中原系女"),
    _r("c0401", "人族高地 女", False, "中原系女"),
    _r("c1801", "维埃拉 女", False, "中原系女"),
    _r("c0601", "精灵 女", False, "精灵系女"),
    _r("c1001", "鲁加 女", False, "精灵系女"),
    _r("c1601", "硌狮 女", False, "精灵系女"),
    _r("c0801", "猫魅 女", False, "猫魅系女"),
    _r("c1401", "敖龙 女", False, "猫魅系女"),
    _r("c1201", "拉拉菲尔 女", False, "拉拉菲尔"),
]}

DEFAULT_RACE = "c0101"

# 推断用的骨段：大腿+小腿+脚踝、上臂+前臂、锁骨（半肩宽）、脊柱、髋宽。每段都是父子骨骼间的距离之和，
# 不随姿势变化（以前用髋→颈、左右肩直线距离，弯腰、耸肩时会变，动画中常推断错）
_SEGMENTS = {
    "leg": [("j_asi_a_l", "j_asi_b_l"), ("j_asi_b_l", "j_asi_c_l"), ("j_asi_c_l", "j_asi_d_l")],
    "arm": [("j_ude_a_l", "j_ude_b_l"), ("j_ude_b_l", "j_te_l")],
    "shoulder": [("j_sebo_c", "j_sako_l"), ("j_sako_l", "j_ude_a_l")],
    "torso": [("j_sebo_a", "j_sebo_b"), ("j_sebo_b", "j_sebo_c"), ("j_sebo_c", "j_kubi")],
    "hip": [("j_kosi", "j_asi_a_l")],
}


def _lengths(pos) -> np.ndarray:
    """pos：骨骼名 → 模型空间位置。"""
    return np.array([sum(np.linalg.norm(np.asarray(pos(a)) - np.asarray(pos(b))) for a, b in segs)
                     for segs in _SEGMENTS.values()])


def guess_race(reference) -> tuple[str, float]:
    """按游戏内角色的骨长（BrioPose）找最接近的骨架，返回 (编号, 相对误差)。
    用比例而非绝对长度：身高滑条、Customize+ 等会整体缩放。相同骨架的种族返回组内第一个。"""
    from .ffxiv import forward_kinematics, load_ffxiv_skeleton

    try:
        ref = _lengths(lambda n: reference.get(n).position)
    except AttributeError:   # 缺骨骼
        return DEFAULT_RACE, float("inf")
    ref = ref / ref[0]
    best, best_err, seen = DEFAULT_RACE, float("inf"), set()
    for code, info in RACES.items():
        if info.group in seen:
            continue
        seen.add(info.group)
        sk = load_ffxiv_skeleton(code)
        _, P = forward_kinematics(sk)
        lengths = _lengths(lambda n: P[sk.index(n)])
        err = float(np.abs(np.log(ref / (lengths / lengths[0]))).max())
        if err < best_err:
            best, best_err = code, err
    return best, best_err


# 残留姿势检测用的骨段（四肢、脊柱、颈）
_LENGTH_SEGMENTS = [("j_ude_a_l", "j_ude_b_l"), ("j_ude_b_l", "j_te_l"), ("j_asi_a_l", "j_asi_b_l"),
                    ("j_asi_b_l", "j_asi_c_l"), ("j_asi_c_l", "j_asi_d_l"), ("j_sebo_b", "j_sebo_c"),
                    ("j_sebo_c", "j_kubi"), ("j_kubi", "j_kao")]
STALE_DEVIATION = 0.10


def length_deviation(reference, code: str) -> float | None:
    """游戏骨长与该骨架绑定骨长的最大相对偏差（先去掉整体比例）。正常角色在 3% 以内；
    换种族后残留的 Brio 姿势栈（按旧骨架算的位置增量）会让它明显变大。缺骨骼时返回 None。"""
    from .ffxiv import forward_kinematics, load_ffxiv_skeleton

    try:
        ref = np.array([np.linalg.norm(reference.get(b).position - reference.get(a).position) for a, b in _LENGTH_SEGMENTS])
    except AttributeError:
        return None
    sk = load_ffxiv_skeleton(code)
    _, P = forward_kinematics(sk)
    bind = np.array([np.linalg.norm(P[sk.index(b)] - P[sk.index(a)]) for a, b in _LENGTH_SEGMENTS])
    if (ref < 1e-6).any():
        return float("inf")
    lr = np.log(ref / bind)
    return float(np.exp(np.abs(lr - np.median(lr)).max()) - 1)


def describe(code: str) -> dict:
    info = RACES.get(code)
    if info is None:
        return {"code": code, "name": code, "group": "", "validated": False, "supported": False, "known": False}
    return {"code": code, "name": info.name, "group": info.group, "validated": info.validated,
            "supported": True, "known": True}
