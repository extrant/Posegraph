"""插件界面用的可调重定向：参数、合理性检查、叠加层与 3D 火柴人数据。

识别（慢）只做一次，结果存在会话里；拖动滑杆时只重跑这里的重定向（几十毫秒）。
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from pydantic import BaseModel, Field
from scipy.spatial.transform import Rotation as R

from .retarget.brio_pose import BrioPose
from .i18n import tr
from .retarget.races import DEFAULT_RACE
from .retarget.ffxiv import (BONE_MAP, HAND_CONTACT_FAR, HAND_CONTACT_NEAR, UPRIGHT_MAX_DEG, RetargetResult,
                             _twist_swing_features, forward_kinematics, load_ffxiv_skeleton, load_mhr_rest, retarget)

# 幅度分区：缩放这些骨骼相对上一级的转动（1 = 照片原样，<1 收敛，>1 夸张）。
# 都是之前为修变形而改成"绑定朝向"或做过补偿的部位，修正后动作可能偏保守，用这里找回力度
AMPLITUDE_GROUPS: dict[str, tuple[str, ...]] = {
    "spine": ("j_sebo_a", "j_sebo_b", "j_sebo_c"),
    "neck": ("j_kubi",),
    "head": ("j_kao",),
    "clavicle": ("j_sako_l", "j_sako_r"),
    "wrist": ("j_te_l", "j_te_r"),
}


class RetargetParams(BaseModel):
    amplitude: dict[str, float] = Field(default_factory=dict)       # 分区名 → 幅度
    offsets: dict[str, list[float]] = Field(default_factory=dict)   # 骨骼名 → 手动偏移（度，模型系 xyz）
    hand_strength: float = 1.0
    hand_near: float = HAND_CONTACT_NEAR
    hand_far: float = HAND_CONTACT_FAR
    upright: bool = True
    upright_max_deg: float = UPRIGHT_MAX_DEG
    ground: bool = True
    helper_strength: float = 1.0
    root_rotation: list[float] = Field(default_factory=lambda: [0.0, 0.0, 0.0])   # 整体旋转（度，模型系 xyz，绕髋部）
    root_offset: list[float] = Field(default_factory=lambda: [0.0, 0.0, 0.0])     # 整体位移（米，模型系 xyz）
    face: bool = False              # 把照片表情套到脸部骨骼（face_rig.py）
    face_strength: float = 1.0      # 表情强度（各通道权重乘这个数）
    face_gains: dict[str, float] = Field(default_factory=dict)   # 分组权重 face_rig.FACE_GROUPS → 倍数（眼睑、眉、嘴、下巴、视线）
    face_head: float = 0.5          # 头部朝向跟面捕的脸走多少（0 = 只用 SAM，1 = 完全按脸；不开表情也生效）


# 预设：插画的头颈比例夸张，识别容易把头颈角度估大（游戏实测 0.7 合适）
PRESETS: dict[str, RetargetParams] = {
    "photo": RetargetParams(),
    "illustration": RetargetParams(amplitude={"neck": 0.7, "head": 0.7}),
}


def run(human: dict, image_size: tuple[int, int], params: RetargetParams,
        reference: BrioPose | None = None, race: str = DEFAULT_RACE, reference_lengths: bool = True,
        ivcs: bool = True, upright_override=None) -> RetargetResult:
    """human：会话里保存的单人识别结果（键同 image_to_pose 的 npz）；race：骨架编号（见 retarget/races.py）；
    reference_lengths：False 时骨长用该骨架的绑定骨长（游戏骨长不可信时）；ivcs：游戏角色是否加载了 IVCS 骨骼；
    upright_override：合并模式整个场景共用的重力校正（floor.scene），代替单人站姿估算。"""
    amp = {bone: k for group, k in params.amplitude.items() for bone in AMPLITUDE_GROUPS.get(group, ())}
    return retarget(human["joint_global_rots"], human["joint_coords"], human["rest_joint_coords"],
                    human["rest_joint_rots"], reference=reference, race=race, vertices_cam=human.get("vertices"),
                    rest_vertices=human.get("rest_vertices"),
                    photo=(float(human["focal_length"]), human["cam_t"], image_size),
                    upright=params.upright, upright_max_deg=params.upright_max_deg, ground=params.ground,
                    amplitude=amp, offsets={k: tuple(v) for k, v in params.offsets.items()},
                    hand_strength=params.hand_strength, hand_near=params.hand_near, hand_far=params.hand_far,
                    helper_strength=params.helper_strength, reference_lengths=reference_lengths, ivcs=ivcs,
                    upright_override=upright_override,
                    root_rotation=tuple(params.root_rotation), root_offset=tuple(params.root_offset))


# ---------------------------------------------------------------- 合理性检查

@dataclass(frozen=True)
class Limit:
    bones: tuple[str, ...]
    kind: str            # magnitude：总转角；twist / lateral / bend：扭转 / 侧摆（y）/ 弯曲（z）
    limit_deg: float
    message: str
    group: str | None = None       # 建议调整的幅度分区
    suggest: float | None = None   # 建议值
    base: str | None = None        # 相对哪根骨骼（缺省为父骨骼）


def _sides(fmt: str) -> tuple[str, ...]:
    return fmt.format(s="l"), fmt.format(s="r")


# 阈值参考 598 帧游戏动画采样（outputs/samples，抬手/转腕类动作，各骨相对绑定姿势的最大转角：
# 腰 j_sebo_a 63°（多为扭腰）、上背 16°、胸 20°、颈 32°、头 27°、锁骨 34°、上臂扭转 119°、肘弯 130°、腕 86°，
# 肘铰链侧摆恒为 0）以及之前实测出问题的部位
LIMITS: list[Limit] = [
    Limit(("j_sebo_a",), "magnitude", 60, "腰部转动过大", "spine", 0.8),
    Limit(("j_sebo_b", "j_sebo_c"), "magnitude", 25, "胸背单节转动过大，躯干可能折角", "spine", 0.8),
    Limit(("j_kubi",), "magnitude", 35, "颈部转动过大，可能出现细长/折角的脖子（插画常见）", "neck", 0.7),
    Limit(("j_kao",), "magnitude", 35, "头部转动过大（插画常见）", "head", 0.7),
    Limit(_sides("j_sako_{s}"), "magnitude", 35, "锁骨转动过大，斜方肌可能隆起、肩部出尖角", "clavicle", 0.7),
    Limit(_sides("j_ude_a_{s}"), "twist", 120, "上臂扭转过大，肩部辅助骨会变形"),
    Limit(_sides("j_ude_b_{s}"), "bend", 135, "肘部弯曲超过游戏动画常见最大值（约 130°），肘窝会有褶皱"),
    Limit(_sides("j_ude_b_{s}"), "lateral", 10, "肘部有侧向摆动（游戏里肘是纯铰链），蒙皮会扭曲"),
    Limit(_sides("j_te_{s}"), "magnitude", 90, "手腕转动过大", "wrist", 0.8),
    Limit(_sides("j_asi_c_{s}"), "bend", 150, "膝盖弯曲过大"),
    Limit(_sides("j_asi_c_{s}"), "lateral", 10, "膝盖有侧向摆动（游戏里膝是纯铰链）"),
]


def _local_deviation(sk, G: list[R], G_bind: list[R], bone: str, base: str | None) -> np.ndarray:
    """[扭转, 侧摆 y, 弯曲 z, 总转角]（度），相对绑定姿势。"""
    i = sk.index(bone)
    b = sk.index(base) if base else sk.parents[i]
    dev = (G_bind[b].inv() * G_bind[i]).inv() * (G[b].inv() * G[i])
    return np.append(np.degrees(_twist_swing_features(dev)), np.degrees(dev.magnitude()))


def check(rt: RetargetResult, race: str = DEFAULT_RACE) -> tuple[list[dict], dict[str, str]]:
    """返回 (提示列表, 骨骼名 → 严重程度 warn/bad)。超过阈值为 warn，超过 1.3 倍为 bad。"""
    sk = load_ffxiv_skeleton(race)
    G = [R.from_quat(rt.all_rotations[n]) for n in sk.names]
    G_bind, _ = forward_kinematics(sk)
    column = {"twist": 0, "lateral": 1, "bend": 2, "magnitude": 3}
    warnings, severity = [], {}
    for lim in LIMITS:
        for bone in lim.bones:
            # 膝：j_asi_b 是膝盖处的短骨，弯曲量看大腿 → 小腿
            base = lim.base or (f"j_asi_a_{bone[-1]}" if bone.startswith("j_asi_c_") else None)
            value = abs(float(_local_deviation(sk, G, G_bind, bone, base)[column[lim.kind]]))
            if value <= lim.limit_deg:
                continue
            level = "bad" if value > lim.limit_deg * 1.3 else "warn"
            if severity.get(bone) != "bad":
                severity[bone] = level
            warnings.append({"bone": bone, "level": level, "value": round(value, 1), "limit": lim.limit_deg,
                             "message": tr(lim.message), "group": lim.group, "suggest": lim.suggest})
    # 头颈组合：插画（926 号图，游戏实测不自然）的特征是颈、头同向侧倾叠加（-20° + -26°）且扭转方向相反，
    # 单看每节都在游戏范围内。照片样本叠加侧倾在 -16° 以内；阈值让用户确认合适的"颈/头幅度 0.7"不再报警
    neck, head = (_local_deviation(sk, G, G_bind, b, None) for b in ("j_kubi", "j_kao"))
    tilt = abs(float(neck[1] + head[1]))
    if tilt > 40:
        level = "bad" if tilt > 50 else "warn"
        for bone in ("j_kubi", "j_kao"):
            severity.setdefault(bone, level)
        warnings.append({"bone": "j_kubi", "level": level, "value": round(tilt, 1), "limit": 40,
                         "message": tr("颈和头同向侧倾叠加过大，脖子会显得歪拧（插画常见）"), "group": "neck", "suggest": 0.7})
    if neck[0] * head[0] < 0 and min(abs(neck[0]), abs(head[0])) > 13:
        severity.setdefault("j_kubi", "warn")
        warnings.append({"bone": "j_kubi", "level": "warn", "value": round(float(abs(neck[0] - head[0])), 1), "limit": 20,
                         "message": tr("颈和头扭转方向相反，脖子会显得拧"), "group": "neck", "suggest": 0.7})
    comp = rt.hand_compensation or {}
    for s, label in (("l", "左"), ("r", "右")):
        shift = comp.get(s, {}).get("shift_cm", 0)
        if shift > 15:
            warnings.append({"bone": f"j_te_{s}", "level": "info", "value": shift, "limit": 15,
                             "message": tr("{side}手末端补偿移动了 {shift:.0f}cm（手贴近头/躯干时为保持接触），不满意可调低补偿强度",
                                           side=tr(label), shift=shift),
                             "group": None, "suggest": None})
    return warnings, severity


# ---------------------------------------------------------------- 叠加层与 3D 预览

def _side_of(name: str) -> str:
    return "r" if name.endswith("_r") or name.startswith("r_") else "l" if name.endswith("_l") or name.startswith("l_") else "c"


# 识别骨架的躯干连线（BONE_MAP 里脊柱/颈/锁骨没有骨段终点）
_MHR_TRUNK = [("root", "c_spine1"), ("c_spine1", "c_spine2"), ("c_spine2", "c_spine3"), ("c_spine3", "c_neck"),
              ("c_neck", "c_head"), ("c_spine3", "l_clavicle"), ("l_clavicle", "l_uparm"), ("c_spine3", "r_clavicle"),
              ("r_clavicle", "r_uparm"), ("root", "l_upleg"), ("root", "r_upleg")]


def overlay(human: dict, image_size: tuple[int, int], rt: RetargetResult, race: str = DEFAULT_RACE) -> dict:
    """原图像素坐标下的线段：mhr（识别结果）与 ffxiv（重定向结果，每段从对应识别关节起画，只比较方向）。"""
    sk = load_ffxiv_skeleton(race)
    mhr_names = load_mhr_rest()[0]
    w, h = image_size
    f, cam_t = float(human["focal_length"]), np.asarray(human["cam_t"])
    flip = np.array([1.0, -1.0, -1.0])
    mhr_cam = np.asarray(human["joint_coords"])
    up_inv = R.from_quat(rt.upright_rotation).inv() if rt.upright_rotation is not None else R.identity()
    P = {n: up_inv.apply(p) for n, p in rt.all_positions.items()}   # 撤销重力校正，回到照片里的朝向

    def project(p_cam: np.ndarray) -> list[float]:
        q = p_cam + cam_t
        return [round(float(f * q[0] / q[2] + w / 2), 1), round(float(f * q[1] / q[2] + h / 2), 1)]

    mhr_of = {m.ffxiv: m.mhr for m in BONE_MAP}
    root = mhr_cam[mhr_names.index("root")]
    pairs = [(sk.names[sk.parents[sk.index(m.ffxiv)]], m.ffxiv) for m in BONE_MAP] +             [(m.ffxiv, m.ffxiv_tip) for m in BONE_MAP if m.ffxiv_tip]
    ff_seg = []
    for a, b in dict.fromkeys(pairs):
        anchor = mhr_of.get(a)
        start = mhr_cam[mhr_names.index(anchor)] if anchor else (P[a] - P["j_kosi"]) * flip + root
        if np.linalg.norm(P[b] - P[a]) < 1e-4:
            continue
        ff_seg.append({"a": project(start), "b": project(start + (P[b] - P[a]) * flip), "side": _side_of(b)})
    mhr_pairs = _MHR_TRUNK + [(m.mhr, m.mhr_tip) for m in BONE_MAP if m.mhr_tip]
    mhr_seg = [{"a": project(mhr_cam[mhr_names.index(a)]), "b": project(mhr_cam[mhr_names.index(b)]),
                "side": _side_of(b)} for a, b in mhr_pairs]
    return {"mhr": mhr_seg, "ffxiv": ff_seg}


# 火柴人只画身体主干与四肢（手指太密），bone → 画到的父关节
_STICK_BONES = ["j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi", "j_kao",
                *[f"{b}_{s}" for s in "lr" for b in ("j_sako", "j_ude_a", "j_ude_b", "j_te", "j_naka_a",
                                                       "j_asi_a", "j_asi_c", "j_asi_d", "j_asi_e")]]


def stick_figure(human: dict, rt: RetargetResult, race: str = DEFAULT_RACE) -> dict:
    """FFXIV 模型系（y 上、z 前、+x 角色左）的关节与连线；mhr 为识别骨架（按髋对齐、躯干长度缩放），用于对照。"""
    sk = load_ffxiv_skeleton(race)
    mhr_names = load_mhr_rest()[0]
    P = rt.all_positions
    drawn = set(_STICK_BONES)
    joints = []
    for n in _STICK_BONES:
        i = sk.parents[sk.index(n)]
        while i >= 0 and sk.names[i] not in drawn:
            i = sk.parents[i]
        joints.append({"name": n, "pos": [round(float(x), 4) for x in P[n]],
                       "parent": sk.names[i] if i >= 0 else None})

    flip = np.array([1.0, -1.0, -1.0])
    up = R.from_quat(rt.upright_rotation) if rt.upright_rotation is not None else R.identity()
    mc = up.apply(np.asarray(human["joint_coords"]) * flip)
    J = lambda n: mc[mhr_names.index(n)]
    mhr_hip, ff_hip = (J("l_upleg") + J("r_upleg")) / 2, (P["j_asi_a_l"] + P["j_asi_a_r"]) / 2
    scale = np.linalg.norm(P["j_kubi"] - ff_hip) / np.linalg.norm(J("c_neck") - mhr_hip)
    to_ff = lambda n: [round(float(x), 4) for x in ff_hip + scale * (J(n) - mhr_hip)]
    mhr = [{"a": to_ff(a), "b": to_ff(b)}
           for a, b in _MHR_TRUNK + [(m.mhr, m.mhr_tip) for m in BONE_MAP if m.mhr_tip]
           if b.split("_")[-1][:-1] not in ("index", "middle", "ring", "pinky", "thumb")]
    return {"joints": joints, "mhr": mhr}
