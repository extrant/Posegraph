"""MHR（SAM 3D Body 骨架）→ FFXIV 骨骼 的重定向。

方法（世界空间增量，和两边骨骼局部轴的约定无关）：
1. 对齐静止姿态：从 FFXIV 参考姿势（.sklb 绑定姿势）出发，按层级顺序对每根映射骨骼做最小摆动（swing），
   让它的骨段方向与 MHR 静止姿态中对应骨段方向一致，得到 A[b]
2. 姿态增量：ΔR[m] = G_posed[m] · G_rest[m]⁻¹（MHR 模型空间）
3. FFXIV 新全局旋转：G_new[b] = ΔR[m] · A[b]；未映射骨骼保持相对父骨骼的参考局部旋转
4. 位置：沿层级用参考骨长做前向运动学，因此不同骨架比例不会被拉伸

MHR 与 FFXIV 模型空间轴向一致（y 上、z 前、+x 角色左侧），旋转无需换基。
Brio 通过 IPC 导入时位置/旋转/缩放全部生效，未写入的骨骼会跟随父骨骼，
所以输出只包含映射骨骼及其祖先链。
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from functools import cache
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation as R

from .brio_pose import BrioPose

DATA_DIR = Path(__file__).with_name("data")


@dataclass(frozen=True)
class BoneMap:
    ffxiv: str
    mhr: str
    # 用于对齐静止姿态的骨段终点（FFXIV 子骨骼、MHR 子关节）；None 表示叶子骨骼，不做摆动对齐
    ffxiv_tip: str | None = None
    mhr_tip: str | None = None
    # 副轴：两点连线（FFXIV、MHR 各一对），摆动对齐后再绕骨段轴扭转，使副轴也对齐（如掌面朝向）
    ffxiv_side: tuple[str, str] | None = None
    mhr_side: tuple[str, str] | None = None
    # 直接采用 FFXIV 绑定姿势的全局朝向，不继承父骨骼的对齐转动、也不做摆动对齐
    # （两边静止姿态语义一致的骨骼，如脊柱挺直、头部正视前方）
    absolute: bool = False
    # 铰链对齐：(FFXIV 子骨骼, MHR 子关节)，摆动对齐后再绕骨段轴扭转，使子关节的弯曲轴（两边都是局部 z）重合。
    # 肘/膝在游戏里是纯 z 轴铰链（采样中 y 恒为 0），轴不重合时弯曲会带出侧向摆动，关节蒙皮扭曲
    hinge: tuple[str, str] | None = None


def _side(side: str) -> list[BoneMap]:
    s = side
    maps = [
        # 锁骨：MHR 的锁骨从胸骨前方斜伸到肩，FFXIV 的是颈根横向的一小段，解剖方向不同。
        # 骨段对齐会在静止姿态里塞进约（扭转 -12°, 37°, -15°）的偏差，造成斜方肌隆起和肩部尖角，故用绑定朝向
        BoneMap(f"j_sako_{s}", f"{s}_clavicle", absolute=True),
        BoneMap(f"j_ude_a_{s}", f"{s}_uparm", f"j_ude_b_{s}", f"{s}_lowarm", hinge=(f"j_ude_b_{s}", f"{s}_lowarm")),
        BoneMap(f"j_ude_b_{s}", f"{s}_lowarm", f"j_te_{s}", f"{s}_wrist"),
        # 手：除手背方向外再对齐掌面横向（食指根→小指根），否则掌心朝向偏约 20°
        BoneMap(f"j_te_{s}", f"{s}_wrist", f"j_naka_a_{s}", f"{s}_middle1",
                (f"j_hito_a_{s}", f"j_ko_a_{s}"), (f"{s}_index1", f"{s}_pinky1")),
        # 腿：j_asi_b 是膝盖处的短骨，跟随大腿；小腿用 j_asi_c
        BoneMap(f"j_asi_a_{s}", f"{s}_upleg", f"j_asi_b_{s}", f"{s}_lowleg", hinge=(f"j_asi_c_{s}", f"{s}_lowleg")),
        BoneMap(f"j_asi_c_{s}", f"{s}_lowleg", f"j_asi_d_{s}", f"{s}_foot"),
        BoneMap(f"j_asi_d_{s}", f"{s}_transversetarsal", f"j_asi_e_{s}", f"{s}_ball"),
        BoneMap(f"j_asi_e_{s}", f"{s}_ball"),
        # 拇指：FFXIV 只有两节
        BoneMap(f"j_oya_a_{s}", f"{s}_thumb1", f"j_oya_b_{s}", f"{s}_thumb2"),
        BoneMap(f"j_oya_b_{s}", f"{s}_thumb2"),
    ]
    for ff, mh in (("hito", "index"), ("naka", "middle"), ("kusu", "ring"), ("ko", "pinky")):
        maps += [
            BoneMap(f"j_{ff}_a_{s}", f"{s}_{mh}1", f"j_{ff}_b_{s}", f"{s}_{mh}2"),
            BoneMap(f"j_{ff}_b_{s}", f"{s}_{mh}2", f"iv_{ff}_c_{s}", f"{s}_{mh}3"),
            BoneMap(f"iv_{ff}_c_{s}", f"{s}_{mh}3"),
        ]
    return maps


BONE_MAP: list[BoneMap] = [
    BoneMap("j_kosi", "root"),
    # 脊柱/颈：两边静止姿态都是"挺直、正视前方"，语义一致，直接用绑定朝向，不做骨段摆动对齐。
    # MHR 的 c_spine3→c_neck 解剖上向前斜（颈根在胸椎前方），强行对齐会在静止姿态里塞进
    # 胸前倾约 22°、颈后仰约 11° 的 S 形弯，所有姿势的脖子都会被拉成细长 S 形
    BoneMap("j_sebo_a", "c_spine1", absolute=True),
    BoneMap("j_sebo_b", "c_spine2", absolute=True),
    BoneMap("j_sebo_c", "c_spine3", absolute=True),
    BoneMap("j_kubi", "c_neck", absolute=True),
    # 头：继承颈部的摆动对齐会让头额外低头约 11°
    BoneMap("j_kao", "c_head", absolute=True),
    *_side("l"),
    *_side("r"),
]


@dataclass(frozen=True)
class HelperRule:
    """辅助骨联动。游戏动画里这些骨骼是烘焙好的，不写的话它们只会整体跟随父骨骼，关节处蒙皮会严重变形。

    驱动量：driver 相对 base（缺省为 driver 的父骨骼）的旋转与绑定姿势的偏差，按 x 轴做"摆动·扭转"分解，
    特征 = [扭转角, 摆动 y, 摆动 z]（弧度）。
    辅助骨：相对父骨骼绑定姿势，绕自身局部 axis 轴转动 coef · 特征 + cubic · 特征³ + intercept_deg。
    系数来自游戏动画采样拟合（scripts/sample_frames.py）：2026-10-24 男性 598 帧抬手 / 转腕；
    2026-10-26 中原男、中原女、精灵女、猫魅女、拉拉菲尔女各约 450 帧跪 / 坐 / 蹲（膝弯到 150°）。
    """
    helper: str
    driver: str
    axis: int
    coef: tuple[float, float, float]
    intercept_deg: float = 0.0
    base: str | None = None
    cubic: tuple[float, float, float] = (0.0, 0.0, 0.0)


# 膝 j_asi_b 分担大腿→小腿总弯曲的比例：弯得越深分担越多。男女相同（中原男与中原女 / 猫魅女拟合系数一致，
# 四族合并 R² ≥ 0.993，平均误差 0.2~0.6°）；拉拉菲尔的绑定比例不同，约分担一半以上
KNEE = (0.3002, 0.0198)
KNEE_LALAFELL = (0.6859, -0.0281)


def _side_rules(s: str, mirror: float, knee: tuple[float, float] = KNEE) -> list[HelperRule]:
    return [
        # 肘：反向转动肘弯的一半（R² 0.999，各种族相同）
        HelperRule(f"n_hhiji_{s}", f"j_ude_b_{s}", 2, (0, 0, -0.495)),
        HelperRule(f"n_hijisoubi_{s}", f"j_ude_b_{s}", 2, (0, 0, -0.495)),
        # 腕：前臂末端分担手腕扭转的一半（R² 0.99，各种族相同）
        HelperRule(f"n_hte_{s}", f"j_te_{s}", 0, (0.5, 0, 0)),
        # 肩：分担上臂扭转约一半，并随抬臂方向反扭（抬手动作 R² 0.91~0.93，左右镜像；
        # 跪坐类大幅摆臂时误差可达 30~60°，驱动关系更复杂，待查）
        HelperRule(f"n_hkata_{s}", f"j_ude_a_{s}", 0, (-0.47, -0.45, 0.18 * mirror), 10.3 * mirror),
        HelperRule(f"j_asi_b_{s}", f"j_asi_c_{s}", 2, (0, 0, knee[0]), base=f"j_asi_a_{s}", cubic=(0, 0, knee[1])),
    ]


HELPER_RULES: list[HelperRule] = _side_rules("l", 1.0) + _side_rules("r", -1.0)
HELPER_RULES_LALAFELL: list[HelperRule] = _side_rules("l", 1.0, KNEE_LALAFELL) + _side_rules("r", -1.0, KNEE_LALAFELL)


def _is_lalafell(race: str) -> bool:
    return race in ("c1101", "c1201")


def helper_rules(race: str) -> list[HelperRule]:
    return HELPER_RULES_LALAFELL if _is_lalafell(race) else HELPER_RULES


@dataclass(frozen=True)
class HelperScaleRule:
    """辅助骨缩放联动：游戏动画里肘、肩的辅助骨会随弯曲放大局部 Y / Z（X 不变），撑住关节处的体积，
    不写的话弯肘时手肘被压扁。Y、Z 各自 = clip(1 + 系数 · 特征 + 常数, 1, 上限)，特征同 HelperRule。
    以驱动骨的游戏缩放为底（Customize+ 改了手臂粗细也跟着走），不读辅助骨自己的游戏值——那可能是上次推送的结果。
    男女通用：男性 598 帧采样与 PR #1（uiharuayako）的中原女 2670 帧采样拟合出的肘部系数一致。
    """
    helper: str
    driver: str
    y: tuple[float, float, float, float]    # 扭转、摆动 y、摆动 z 的系数（每弧度），常数
    z: tuple[float, float, float, float]
    y_max: float
    z_max: float


def _side_scale_rules(s: str, mirror: float, elbow: bool = True) -> list[HelperScaleRule]:
    shoulder = (-0.0738 * mirror, -0.0716 * mirror, 0.0209, 0.0043)
    return [
        # 肘：Y = 1 + 弯曲角/180°、Z = 1 + 弯曲角/400°（R² 1.000，男女四族一致；弯曲角为负，伸直时不缩放）
        *([HelperScaleRule(f"n_hhiji_{s}", f"j_ude_b_{s}", (0, 0, -1 / np.pi, 0), (0, 0, -0.45 / np.pi, 0), 1.8, 1.35)]
          if elbow else []),
        # 肩：随上臂扭转与抬臂放大，最多 1.2（R² 0.92，左右镜像）
        HelperScaleRule(f"n_hkata_{s}", f"j_ude_a_{s}", shoulder, shoulder, 1.2, 1.2),
    ]


HELPER_SCALE_RULES: list[HelperScaleRule] = _side_scale_rules("l", 1.0) + _side_scale_rules("r", -1.0)
# 拉拉菲尔的动画不缩放肘辅助骨（采样中始终为 1）
HELPER_SCALE_RULES_LALAFELL: list[HelperScaleRule] = _side_scale_rules("l", 1.0, False) + _side_scale_rules("r", -1.0, False)


def helper_scale_rules(race: str) -> list[HelperScaleRule]:
    return HELPER_SCALE_RULES_LALAFELL if _is_lalafell(race) else HELPER_SCALE_RULES


def _twist_swing_features(dev: R) -> np.ndarray:
    """dev = swing · twist（扭转绕局部 x 轴），返回 [扭转, 摆动 y, 摆动 z]。"""
    q = dev.as_quat()
    twist = 2 * np.arctan2(q[0], q[3])
    twist = (twist + np.pi) % (2 * np.pi) - np.pi
    swing = dev * R.from_rotvec([-twist, 0, 0])
    sv = swing.as_rotvec()
    return np.array([twist, sv[1], sv[2]])


@dataclass
class Skeleton:
    names: list[str]
    parents: np.ndarray
    local_t: np.ndarray
    local_r: R

    def index(self, name: str) -> int:
        return self.names.index(name)


@cache
def load_ffxiv_skeleton(race: str = "c0101") -> Skeleton:
    d = json.loads((DATA_DIR / f"ffxiv_{race}.json").read_text(encoding="utf-8"))
    return Skeleton(d["names"], np.array(d["parents"]), np.array(d["local_translation"]),
                    R.from_quat(d["local_rotation"]))


@cache
def load_mhr_rest() -> tuple[list[str], np.ndarray, R]:
    d = json.loads((DATA_DIR / "mhr_rest.json").read_text(encoding="utf-8"))
    return d["names"], np.array(d["rest_position"]), R.from_quat(d["rest_rotation"])


def forward_kinematics(sk: Skeleton, global_rot: list[R | None] | None = None) -> tuple[list[R], np.ndarray]:
    """global_rot[i] 非空时使用给定全局旋转，否则沿用参考局部旋转。返回 (全局旋转, 模型空间位置)。"""
    n = len(sk.names)
    G: list[R] = [None] * n  # type: ignore[list-item]
    P = np.zeros((n, 3))
    for i in range(n):
        p = sk.parents[i]
        given = global_rot[i] if global_rot is not None else None
        if p < 0:
            G[i] = given if given is not None else sk.local_r[i]
            P[i] = sk.local_t[i]
        else:
            G[i] = given if given is not None else G[p] * sk.local_r[i]
            P[i] = P[p] + G[p].apply(sk.local_t[i])
    return G, P


def _subtree(sk: Skeleton, root: int) -> list[int]:
    """root 及其全部后代（骨骼按层级顺序存储，父在前）。"""
    inside = {root}
    for i in range(root + 1, len(sk.names)):
        if sk.parents[i] in inside:
            inside.add(i)
    return sorted(inside)


def _swing(a: np.ndarray, b: np.ndarray) -> R:
    """把方向 a 转到方向 b 的最小旋转。"""
    a = a / np.linalg.norm(a)
    b = b / np.linalg.norm(b)
    axis = np.cross(a, b)
    s, c = np.linalg.norm(axis), float(np.dot(a, b))
    if s < 1e-9:
        if c > 0:
            return R.identity()
        ortho = np.cross(a, [1.0, 0, 0] if abs(a[0]) < 0.9 else [0, 1.0, 0])
        return R.from_rotvec(ortho / np.linalg.norm(ortho) * np.pi)
    return R.from_rotvec(axis / s * np.arctan2(s, c))


def _twist_about(axis: np.ndarray, a: np.ndarray, b: np.ndarray) -> R:
    """绕 axis 旋转，使 a 在垂直于 axis 平面上的投影转到 b 的投影方向。"""
    n = axis / np.linalg.norm(axis)
    pa, pb = a - n * (a @ n), b - n * (b @ n)
    angle = np.arctan2(n @ np.cross(pa, pb), pa @ pb)
    return R.from_rotvec(n * angle)


def _aligned_rest(race: str, mhr_pos: np.ndarray, mhr_rot: R | None = None) -> list[R]:
    """FFXIV 骨架在 "MHR 静止姿态" 下的全局旋转 A[b]。mhr_pos / mhr_rot：MHR 静止姿态关节位置与全局旋转（模型系）。"""
    sk = load_ffxiv_skeleton(race)
    mhr_names = load_mhr_rest()[0]
    G_ref, P_ref = forward_kinematics(sk)
    by_bone = {m.ffxiv: m for m in BONE_MAP}

    A: list[R] = [None] * len(sk.names)  # type: ignore[list-item]
    for i, name in enumerate(sk.names):
        p = sk.parents[i]
        m = by_bone.get(name)
        base = G_ref[i] if p < 0 or (m and m.absolute) else A[p] * (G_ref[p].inv() * G_ref[i])
        if m and m.ffxiv_tip and not m.absolute:
            tip = sk.index(m.ffxiv_tip)
            seg_local = G_ref[i].inv().apply(P_ref[tip] - P_ref[i])
            target = mhr_pos[mhr_names.index(m.mhr_tip)] - mhr_pos[mhr_names.index(m.mhr)]
            base = _swing(base.apply(seg_local), target) * base
            if m.ffxiv_side:
                a, b = (sk.index(n) for n in m.ffxiv_side)
                side_now = base.apply(G_ref[i].inv().apply(P_ref[b] - P_ref[a]))
                c, d = (mhr_names.index(n) for n in m.mhr_side)
                base = _twist_about(target, side_now, mhr_pos[d] - mhr_pos[c]) * base
            if m.hinge and mhr_rot is not None:
                child = sk.index(m.hinge[0])
                chain = G_ref[i].inv() * G_ref[child]  # 绑定姿势下子骨骼相对本骨骼的旋转（含中间短骨）
                ff_axis = (base * chain).apply([0.0, 0.0, 1.0])
                mhr_axis = mhr_rot[mhr_names.index(m.hinge[1])].apply([0.0, 0.0, 1.0])
                if ff_axis @ mhr_axis < 0:  # 两边弯曲正方向约定可能相反，只要求轴线重合
                    mhr_axis = -mhr_axis
                base = _twist_about(target, ff_axis, mhr_axis) * base
        A[i] = base
    return A


def _with_reference_lengths(sk: Skeleton, reference: BrioPose | None) -> Skeleton:
    """按游戏内实际骨长缩放参考骨架的局部平移（方向仍取 .sklb 绑定姿势）。"""
    if reference is None:
        return sk
    local_t = sk.local_t.copy()
    # 离地高度（n_root 下的骨骼）按腿长比例缩放：腿骨长度不受贴地影响，能反映种族/身高差异
    leg = [("j_asi_a_l", "j_asi_b_l"), ("j_asi_b_l", "j_asi_c_l"), ("j_asi_c_l", "j_asi_d_l")]
    if all(reference.get(n) is not None for pair in leg for n in pair):
        _, P_bind = forward_kinematics(sk)
        ref_len = sum(np.linalg.norm(reference.get(a).position - reference.get(b).position) for a, b in leg)
        bind_len = sum(np.linalg.norm(P_bind[sk.index(a)] - P_bind[sk.index(b)]) for a, b in leg)
        for i in range(len(sk.names)):
            if sk.parents[i] >= 0 and sk.parents[sk.parents[i]] < 0:
                local_t[i] *= ref_len / bind_len
    for i, name in enumerate(sk.names):
        p = sk.parents[i]
        cur, par = reference.get(name), reference.get(sk.names[p]) if p >= 0 else None
        length = np.linalg.norm(local_t[i])
        # 挂在 n_root 下的骨骼（n_hara 等）的"长度"是身体离地高度，会被之前推送的自动贴地改变，不能取游戏值
        # （否则每推一次就下沉一次），已在上面按腿长比例处理
        if p < 0 or sk.parents[p] < 0 or cur is None or par is None or length < 1e-6:
            continue
        local_t[i] *= np.linalg.norm(cur.position - par.position) / length
    return Skeleton(sk.names, sk.parents, local_t, sk.local_r)


@dataclass
class RetargetResult:
    bones: dict[str, tuple[np.ndarray, np.ndarray, np.ndarray]]  # 名称 → (模型空间位置, 四元数 xyzw, 缩放)
    segment_error_deg: dict[str, float]              # 自检：FFXIV 骨段方向与 MHR 骨段方向的夹角
    # 全部骨骼（含未输出的）的最终全局旋转/位置，供 Blender 等外部预览使用
    all_rotations: dict[str, np.ndarray] | None = None
    all_positions: dict[str, np.ndarray] | None = None
    # 补偿信息：l/r → {weight, shift_cm}；head_shift_cm；upright_deg（站姿重力校正角，未校正时缺省）
    hand_compensation: dict[str, dict[str, float]] | None = None
    upright_rotation: np.ndarray | None = None   # 站姿重力校正旋转（xyzw，MHR 模型系），供预览/相机复刻使用
    root_offset: np.ndarray | None = None        # 调整页的整体位移（米，模型系）：复刻站位按位移前的位置算，不抵消它


# 末端补偿：手腕到部位（头/胸/髋）距离 ≤ NEAR 时完全按部位相对位置放置，≥ FAR 时只复制角度，中间平滑过渡
HAND_CONTACT_NEAR = 0.20
HAND_CONTACT_FAR = 0.40
# (FFXIV 部位, MHR 部位)；髋用两侧大腿根中点（j_kosi 在腰部，与 MHR root 高度不一致）
_LANDMARKS = {
    "head": (("j_kao",), ("c_head",)),
    "chest": (("j_sebo_c",), ("c_spine3",)),
    "hip": (("j_asi_a_l", "j_asi_a_r"), ("l_upleg", "r_upleg")),
}
# 部位朝向取自哪根关节：(MHR, FFXIV)
_LANDMARK_FRAMES = {"head": ("c_head", "j_kao"), "chest": ("c_spine3", "j_sebo_c"), "hip": ("root", "j_kosi")}


def _smoothstep(x: float) -> float:
    x = min(max(x, 0.0), 1.0)
    return x * x * (3 - 2 * x)


def _frame(e: np.ndarray, h: np.ndarray) -> np.ndarray | None:
    """以 e 为第一轴、h（去掉 e 方向分量）为第二轴的正交基（列向量）。"""
    e = e / np.linalg.norm(e)
    h = h - e * (h @ e)
    if np.linalg.norm(h) < 1e-6:
        return None
    h /= np.linalg.norm(h)
    return np.stack([e, h, np.cross(e, h)], axis=1)


def _two_bone_ik(S: np.ndarray, E: np.ndarray, W: np.ndarray, T: np.ndarray,
                 hinge: np.ndarray | None = None) -> tuple[R, R]:
    """肩 S、肘 E、腕 W 的双骨链，求让腕到达 T 的上臂旋转 R1 与前臂附加旋转 R2。
    新的上臂全局旋转 = R1·旧值；新的前臂全局旋转 = R2·R1·旧值。
    hinge：肘的弯曲轴（世界方向）。给出时新的弯曲平面取最接近原弯曲轴的那个，上臂转动把弯曲轴一起带过去，
    前臂只绕弯曲轴转——肘保持纯铰链（游戏里肘是纯 z 轴铰链，侧摆会让蒙皮扭曲）。"""
    a, b = np.linalg.norm(E - S), np.linalg.norm(W - E)
    to_t = T - S
    d = float(np.clip(np.linalg.norm(to_t), abs(a - b) + 1e-4, a + b - 1e-4))
    u = to_t / np.linalg.norm(to_t)
    cos_a = (a * a + d * d - b * b) / (2 * a * d)
    sin_a = np.sqrt(max(0.0, 1 - cos_a * cos_a))
    if hinge is not None:
        n = hinge - u * (hinge @ u)
        old = _frame(E - S, hinge)
        if np.linalg.norm(n) > 1e-6 and old is not None:
            n /= np.linalg.norm(n)
            # 肘在弯曲平面里的哪一侧：与原姿势一致（原肘相对肩→腕连线的偏移方向）
            u0 = (W - S) / np.linalg.norm(W - S)
            v0 = (E - S) - u0 * ((E - S) @ u0)
            side = 1.0 if np.linalg.norm(v0) < 1e-6 or v0 @ np.cross(hinge, u0) >= 0 else -1.0
            v = side * np.cross(n, u)
            E2 = S + a * (cos_a * u + sin_a * v)
            new = _frame(E2 - S, n)
            R1 = R.from_matrix(new @ old.T)
            W1 = E2 + R1.apply(W - E)
            R2 = _twist_about(n, W1 - E2, S + u * d - E2)
            return R1, R2
    v = (E - S) - u * ((E - S) @ u)  # 肘的弯曲方向：原肘位置在 S→T 垂面上的分量
    if np.linalg.norm(v) < 1e-6:
        v = np.cross(u, [0.0, 1.0, 0.0] if abs(u[1]) < 0.9 else [1.0, 0.0, 0.0])
    v /= np.linalg.norm(v)
    E2 = S + a * (cos_a * u + sin_a * v)
    R1 = _swing(E - S, E2 - S)
    W1 = E2 + R1.apply(W - E)
    R2 = _swing(W1 - E2, S + u * d - E2)
    return R1, R2


def _compensate_hands(sk: Skeleton, G: list[R], P: np.ndarray, G_new: list[R | None],
                      mhr_names: list[str], mhr_pos: np.ndarray, strength: float = 1.0,
                      near: float = HAND_CONTACT_NEAR, far: float = HAND_CONTACT_FAR,
                      frames: tuple | None = None, extents: tuple[dict, dict] | None = None) -> dict[str, dict[str, float]]:
    """手靠近头/胸/髋时，按部位相对位置重新求解上臂与前臂，手的朝向不变。
    strength 为总强度（0 关闭），near/far 为完全生效/不生效的距离（米）。
    extents=(FFXIV 部位尺寸, 识别人物部位尺寸)（retarget/body.py）给出时，偏移先转到部位的静止朝向，
    按部位实际宽度 / 前后厚度 / 头高分轴缩放（拉拉菲尔头大、鲁加腹厚），否则整体按躯干长度比例缩放。
    frames=(MHR 当前全局旋转, MHR 静止全局旋转, FFXIV 当前全局旋转, FFXIV 绑定全局旋转)，用于部位朝向。"""
    ff = lambda names: np.mean([P[sk.index(n)] for n in names], axis=0)
    mh = lambda names: np.mean([mhr_pos[mhr_names.index(n)] for n in names], axis=0)
    hip_ff, hip_mh = ff(_LANDMARKS["hip"][0]), mh(_LANDMARKS["hip"][1])
    # 体型比例：髋 → 颈的躯干长度
    scale = np.linalg.norm(P[sk.index("j_kubi")] - hip_ff) / np.linalg.norm(mhr_pos[mhr_names.index("c_neck")] - hip_mh)

    def to_ffxiv(part: str, offset: np.ndarray) -> np.ndarray:
        if extents is None or frames is None:
            return scale * offset
        from .body import map_offset

        posed, mhr_rest, G_cur, G_bind = frames
        mj, fj = _LANDMARK_FRAMES[part]
        mi, fi = mhr_names.index(mj), sk.index(fj)
        local = (posed[mi] * mhr_rest[mi].inv()).inv().apply(offset)   # 静止朝向下：x 侧、y 上、z 前
        local = map_offset(local, extents[0][part], extents[1][part], scale)
        return (G_cur[fi] * G_bind[fi].inv()).apply(local)

    report = {}
    for s in "lr":
        wrist_mh = mhr_pos[mhr_names.index(f"{s}_wrist")]
        targets, weights = [], []
        for part, (ff_names, mh_names) in _LANDMARKS.items():
            offset = wrist_mh - mh(mh_names)
            w = _smoothstep((far - np.linalg.norm(offset)) / max(far - near, 1e-3))
            if w > 0:
                targets.append(ff(ff_names) + to_ffxiv(part, offset))
                weights.append(w)
        ua, lb, te = sk.index(f"j_ude_a_{s}"), sk.index(f"j_ude_b_{s}"), sk.index(f"j_te_{s}")
        weight = min(1.0, sum(weights)) * strength
        if weight <= 0:
            report[s] = {"weight": 0.0, "shift_cm": 0.0}
            continue
        target = np.average(targets, axis=0, weights=weights)
        T = P[te] + weight * (target - P[te])
        # 肘的弯曲轴：前臂局部 z（游戏里肘是纯 z 轴铰链，绑定局部旋转之后）
        R1, R2 = _two_bone_ik(P[ua], P[lb], P[te], T, hinge=G[lb].apply([0.0, 0.0, 1.0]))
        G_new[ua] = R1 * G[ua]
        G_new[lb] = R2 * R1 * G[lb]
        report[s] = {"weight": round(float(weight), 3), "shift_cm": round(float(np.linalg.norm(T - P[te])) * 100, 1)}
    return report


# 颈部分配（默认关闭）：游戏动画中从胸 j_sebo_c 到头 j_kao 的总转动，j_sebo_c 几乎不参与（-13%），
# j_kubi 约 58%、j_kao 约 52%（598 帧采样拟合，点头方向 R² 0.83/0.87）。
# 当初用于修细长 S 形颈，根因其实是脊柱骨段对齐（已改为 absolute），开启会丢掉胸椎的真实侧弯。
NECK_KUBI_SHARE = 0.5


def _distribute_neck(sk: Skeleton, G: list[R], G_new: list[R | None]) -> None:
    """保持上背 j_sebo_b 与头 j_kao 的全局朝向不变，中间的总转动按游戏习惯重新分配：
    j_sebo_c 跟随 j_sebo_b（绑定局部旋转），j_kubi 取一半，其余由 j_kao 承担。"""
    b, c, k, h = (sk.index(n) for n in ("j_sebo_b", "j_sebo_c", "j_kubi", "j_kao"))
    Lc, Lk, Lh = sk.local_r[c], sk.local_r[k], sk.local_r[h]
    total = (Lc * Lk * Lh).inv() * (G[b].inv() * G[h])
    G_new[c] = G[b] * Lc
    G_new[k] = G_new[c] * Lk * R.from_rotvec(NECK_KUBI_SHARE * total.as_rotvec())
    G_new[h] = G[h]


UPRIGHT_MAX_DEG = 30.0


def _feet_in_frame(mhr_names: list[str], joint_coords_cam: np.ndarray, focal: float, cam_t: np.ndarray,
                   image_size: tuple[int, int], margin: float = 0.02) -> bool:
    """两个脚踝投影到照片后都在画面内（留 2% 边距）。"""
    w, h = image_size
    for n in ("l_foot", "r_foot"):
        p = joint_coords_cam[mhr_names.index(n)] + cam_t
        u, v = focal * p[0] / p[2] + w / 2, focal * p[1] / p[2] + h / 2
        if not (margin * w <= u <= (1 - margin) * w and margin * h <= v <= (1 - margin) * h):
            return False
    return True
_FOOT_JOINTS = ("l_foot", "l_ball", "r_foot", "r_ball")


def _upright_rotation(mhr_names: list[str], mhr_pos: np.ndarray, vertices: np.ndarray | None,
                      max_deg: float = UPRIGHT_MAX_DEG) -> R | None:
    """站姿重力校正：SAM 3D Body 的身体朝向相对相机，相机俯仰/侧倾会让人整体歪斜。
    双脚是身体最低处（站立）时，求最小旋转让重心落在支撑脚正上方；角度超过上限或非站姿时不校正。"""
    up = np.array([0.0, 1.0, 0.0])
    body = vertices if vertices is not None else mhr_pos
    feet = np.array([mhr_pos[mhr_names.index(n)] for n in _FOOT_JOINTS])
    if feet[:, 1].min() - body[:, 1].min() > 0.12:   # 脚不是最低点：坐/躺/跳
        return None
    support = feet[feet[:, 1] < feet[:, 1].min() + 0.08].mean(axis=0)  # 着地的脚
    com = body.mean(axis=0)
    rot = _swing(com - support, up)
    if np.degrees(rot.magnitude()) > max_deg:
        return None
    return rot


def _compensate_head(sk: Skeleton, G: list[R], P: np.ndarray, G_new: list[R | None],
                     mhr_names: list[str], mhr_pos: np.ndarray, mhr_rest_pos: np.ndarray) -> float:
    """头部末端补偿：FFXIV 颈根 j_kubi 比 MHR c_neck 高（在脖子中段），颈也更长，同样的上背/颈部弯曲会把头甩得更远。
    只转 j_kubi，让头相对双肩中点的方向与照片一致（距离按 FFXIV 自身颈长），头部朝向不变。返回头被移动的距离（cm）。"""
    G_bind, P_bind = forward_kinematics(sk)
    ff_sh = lambda pos: (pos[sk.index("j_ude_a_l")] + pos[sk.index("j_ude_a_r")]) / 2
    mh = lambda pos, n: pos[mhr_names.index(n)]
    mh_sh = lambda pos: (mh(pos, "l_uparm") + mh(pos, "r_uparm")) / 2
    ratio = (np.linalg.norm(P_bind[sk.index("j_kao")] - ff_sh(P_bind))
             / np.linalg.norm(mh(mhr_rest_pos, "c_head") - mh_sh(mhr_rest_pos)))
    target = ff_sh(P) + ratio * (mh(mhr_pos, "c_head") - mh_sh(mhr_pos))
    kubi, kao = sk.index("j_kubi"), sk.index("j_kao")
    G_new[kubi] = _swing(P[kao] - P[kubi], target - P[kubi]) * G[kubi]
    new_kao = P[kubi] + G_new[kubi].apply(sk.local_t[kao])
    return round(float(np.linalg.norm(new_kao - P[kao])) * 100, 1)


def retarget(joint_global_rots: np.ndarray, joint_coords_cam: np.ndarray | None = None,
             rest_coords: np.ndarray | None = None, rest_rots: np.ndarray | None = None,
             reference: BrioPose | None = None, race: str = "c0101",
             compensate_hands: bool = True, compensate_head: bool = False,
             upright: bool = True, vertices_cam: np.ndarray | None = None,
             distribute_neck: bool = False, ground: bool = True,
             photo: tuple[float, np.ndarray, tuple[int, int]] | None = None,
             neck_scale: float = 1.0, amplitude: dict[str, float] | None = None,
             offsets: dict[str, tuple[float, float, float]] | None = None,
             hand_strength: float = 1.0, hand_near: float = HAND_CONTACT_NEAR, hand_far: float = HAND_CONTACT_FAR,
             upright_max_deg: float = UPRIGHT_MAX_DEG, helper_strength: float = 1.0,
             rest_vertices: np.ndarray | None = None, reference_lengths: bool = True,
             ivcs: bool = True, upright_override: R | None = None, root_rotation: tuple[float, float, float] = (0.0, 0.0, 0.0),
             root_offset: tuple[float, float, float] = (0.0, 0.0, 0.0)) -> RetargetResult:
    """joint_global_rots: (127,3,3) SAM 3D Body 输出 pred_global_rots；
    joint_coords_cam: (127,3) pred_joint_coords（相机系，仅用于自检）；
    rest_coords / rest_rots: 该人物体型下的零姿态（缺省用平均体型 mhr_rest.json，骨段方向会有几度系统误差）；
    reference_lengths: 骨长是否取 reference（False 时用该骨架的绑定骨长，缩放仍取 reference）；
    reference: 游戏内角色当前姿势（Posegraph GET /pose）。提供时骨长与缩放取游戏实际值——
    Brio IPC 导入会覆盖位置和缩放，不按实际值写会改变其他种族 / Customize+ 角色的体型；
    compensate_hands: 末端补偿。两套骨架比例不同（如 TBSE-X 肩更低更窄），只复制角度时手相对头/躯干会错位，
    开启后手靠近头/胸/髋时用 IK 调整上臂与前臂（此时这两段的骨段自检误差会变大，属预期）；
    compensate_head: 头部末端补偿（只转 j_kubi），默认关闭——头相对双肩的位移主要来自上背弯曲，
    只靠离头很近的 j_kubi 去补需要转 40~60°，头颈之间会出现明显折角（游戏实测“头严重偏移”）；
    upright: 站姿重力校正，vertices_cam 为 MHR 网格顶点（相机系，用于估算重心，缺省用关节）；
    upright_override: 直接用这个重力校正（合并模式整个场景共用的：深度找到的地面，见 floor.py），不再按站姿估算；
    root_rotation / root_offset: 调整页的整体旋转（度，模型系 xyz，绕髋部转）和整体位移（米，贴地之后加上去，
    复刻站位时不会被抵消，见 placement）；
    distribute_neck: 按游戏动画习惯重新分配胸→颈→头的转动（见 NECK_KUBI_SHARE），默认关闭；
    photo: (焦距, cam_t, (宽, 高))，用于判断脚是否在画面内——脚在画面外（半身照）时脚是模型猜的，不做重力校正；
    neck_scale: 颈部幅度，按比例缩放颈 j_kubi、头 j_kao 相对上一级的转动（方向不变）。
    插画的头颈比例经过夸张，识别模型容易把头颈角度估大，可调到 0.5 左右；
    amplitude: 骨骼名 → 幅度（同 neck_scale 的做法，可用于任意骨骼，>1 放大）；与 neck_scale 同时给出时以它为准；
    offsets: 骨骼名 → 手动偏移（度，绕模型系 x/y/z 轴，按 x→y→z 顺序），整棵子树一起转，在末端补偿之后、辅助骨之前施加；
    hand_strength / hand_near / hand_far: 手部末端补偿的强度与生效距离；upright_max_deg: 重力校正角上限；
    helper_strength: 辅助骨联动强度（1 为游戏采样拟合值）；
    rest_vertices: 该人物零姿态网格顶点（MHR 模型系），给出时手部末端补偿按头 / 胸 / 髋实际尺寸换算接触位置；
    ground: 自动贴地。n_hara 的高度固定在站立高度，倒立/跪坐/躺下时角色会悬空，
    开启后把最低的关节平移到绑定姿势中最低关节的高度（跳跃照片也会被按到地面上）；
    ivcs: 游戏角色加载了 IVCS 骨骼。False 时手指第三节（iv_*_c）的弯曲取一半并入第二节，输出不含 iv_ 骨骼
    （原版骨架没有第三节，第二节的蒙皮一直覆盖到指尖）。脚趾 iv_asi_* 本来就不驱动，没有可折算的。"""
    sk = _with_reference_lengths(load_ffxiv_skeleton(race), reference if reference_lengths else None)
    mhr_names, mean_pos, mean_rot = load_mhr_rest()
    mhr_rest_pos = mean_pos if rest_coords is None else rest_coords
    mhr_rest_rot = mean_rot if rest_rots is None else R.from_matrix(rest_rots)
    A = _aligned_rest(race, mhr_rest_pos, mhr_rest_rot)
    posed = R.from_matrix(joint_global_rots)
    flip = np.array([1.0, -1.0, -1.0])  # 相机系 ↔ MHR 模型系
    mhr_model = joint_coords_cam * flip if joint_coords_cam is not None else None

    upright_rot = None
    if upright and upright_override is not None and mhr_model is not None:   # 调整页关掉重力校正时也不用场景的
        upright_rot = upright_override
        posed = upright_rot * posed
        mhr_model = upright_rot.apply(mhr_model)
    elif upright and mhr_model is not None and not (photo is not None and not _feet_in_frame(mhr_names, joint_coords_cam, *photo)):
        upright_rot = _upright_rotation(mhr_names, mhr_model, vertices_cam * flip if vertices_cam is not None else None,
                                        upright_max_deg)
        if upright_rot is not None:
            posed = upright_rot * posed
            mhr_model = upright_rot.apply(mhr_model)

    if any(root_rotation) and mhr_model is not None:
        manual = R.from_euler("xyz", root_rotation, degrees=True)
        posed = manual * posed
        mhr_model = manual.apply(mhr_model)

    G_new: list[R | None] = [None] * len(sk.names)
    for m in BONE_MAP:
        j = mhr_names.index(m.mhr)
        delta = posed[j] * mhr_rest_rot[j].inv()
        G_new[sk.index(m.ffxiv)] = delta * A[sk.index(m.ffxiv)]
    G, P = forward_kinematics(sk, G_new)
    if distribute_neck:
        _distribute_neck(sk, G, G_new)
        G, P = forward_kinematics(sk, G_new)
    scales = {"j_kubi": neck_scale, "j_kao": neck_scale} if neck_scale != 1.0 else {}
    scales = {**(amplitude or {}), **scales}
    # 按层级顺序（父在前）：子骨骼的局部转动要相对缩放后的父骨骼重新计算，子骨骼全局朝向不受父骨骼缩放影响
    for i in sorted(sk.index(n) for n, k in scales.items() if k != 1.0):
        p = sk.parents[i]
        dev = sk.local_r[i].inv() * (G[p].inv() * G[i])
        G_new[i] = G[p] * sk.local_r[i] * R.from_rotvec(scales[sk.names[i]] * dev.as_rotvec())
        G, P = forward_kinematics(sk, G_new)

    # 末端补偿（需要 MHR 关节坐标）：在辅助骨之前做，辅助骨会随新的手臂角度联动
    compensation = {}
    if compensate_head and mhr_model is not None:
        compensation["head_shift_cm"] = _compensate_head(sk, G, P, G_new, mhr_names, mhr_model, mhr_rest_pos)
        G, P = forward_kinematics(sk, G_new)
    if compensate_hands and hand_strength > 0 and mhr_model is not None:
        extents = None
        from .body import bust_scale, chest_for_bust, ffxiv_extents, mhr_extents
        ff_ext = ffxiv_extents(race)
        if ff_ext is not None:
            ff_ext = chest_for_bust(ff_ext, bust_scale(reference))
            compensation["bust_scale"] = round(bust_scale(reference), 3)
        if ff_ext is not None and rest_vertices is not None:
            extents = (ff_ext, mhr_extents(rest_vertices, mhr_rest_pos, mhr_names))
        G_bind0, _ = forward_kinematics(sk)
        compensation.update(_compensate_hands(sk, G, P, G_new, mhr_names, mhr_model, hand_strength, hand_near, hand_far,
                                              frames=(posed, mhr_rest_rot, G, G_bind0), extents=extents))
        compensation["contact_scaling"] = "body" if extents else "torso"
        G, P = forward_kinematics(sk, G_new)
    for i in sorted(sk.index(n) for n, v in (offsets or {}).items() if any(v)):
        rw = R.from_euler("xyz", offsets[sk.names[i]], degrees=True)
        if G_new[i] is None:
            G_new[i] = G[i]
        for j in _subtree(sk, i):
            if G_new[j] is not None:
                G_new[j] = rw * G_new[j]
        G, P = forward_kinematics(sk, G_new)
    if not ivcs:
        # 第三节超出"随第二节刚性转动"的部分（模型系），一半加到第二节：第二节就大致指向第二、三节合起来的方向，
        # 握拳不会变成伸直的手指
        for s in "lr":
            for ff in ("hito", "naka", "kusu", "ko"):
                b, c = sk.index(f"j_{ff}_b_{s}"), sk.index(f"iv_{ff}_c_{s}")
                extra = G[c] * (G[b] * sk.local_r[c]).inv()
                G_new[b] = R.from_rotvec(0.5 * extra.as_rotvec()) * G[b]
        G, P = forward_kinematics(sk, G_new)
    if upright_rot is not None:
        compensation["upright_deg"] = round(float(np.degrees(upright_rot.magnitude())), 1)

    # 辅助骨：按驱动骨相对绑定姿势的偏差计算，再做一次前向运动学
    G_bind, _ = forward_kinematics(load_ffxiv_skeleton(race))
    rules = helper_rules(race)
    for rule in rules:
        d, h = sk.index(rule.driver), sk.index(rule.helper)
        b = sk.index(rule.base) if rule.base else sk.parents[d]
        f = _twist_swing_features((G_bind[b].inv() * G_bind[d]).inv() * (G[b].inv() * G[d]))
        angle = helper_strength * (float(np.dot(rule.coef, f) + np.dot(rule.cubic, f ** 3)) + np.radians(rule.intercept_deg))
        rotvec = np.zeros(3)
        rotvec[rule.axis] = angle
        G_new[h] = G[sk.parents[h]] * sk.local_r[h] * R.from_rotvec(rotvec)
    if rules:
        G, P = forward_kinematics(sk, G_new)

    def ref_scale(name: str) -> np.ndarray:
        t = reference.get(name) if reference is not None else None
        return t.scale if t is not None else np.ones(3)

    helper_scales: dict[str, np.ndarray] = {}
    for rule in helper_scale_rules(race):
        d = sk.index(rule.driver)
        p = sk.parents[d]
        f = _twist_swing_features((G_bind[p].inv() * G_bind[d]).inv() * (G[p].inv() * G[d]))
        grow = lambda c, top: float(np.clip(1 + np.dot(c[:3], f) + c[3], 1.0, top)) - 1
        helper_scales[rule.helper] = ref_scale(rule.driver) * np.array(
            [1.0, 1 + helper_strength * grow(rule.y, rule.y_max), 1 + helper_strength * grow(rule.z, rule.z_max)])

    if ground:
        body = [i for i, n in enumerate(sk.names) if n.startswith(("j_", "iv_asi_")) and not n.startswith(("j_sk_", "j_kami", "j_f_", "j_ex_"))]
        _, P_bind = forward_kinematics(sk)
        drop = P[body, 1].min() - P_bind[body, 1].min()
        P = P.copy()
        P[1:, 1] -= drop  # n_root 不动，其余整体平移
        compensation["ground_drop_cm"] = round(float(drop) * 100, 1)
    if any(root_offset):
        P = P.copy()
        P[1:] += np.asarray(root_offset, dtype=float)   # n_root 不动，其余整体平移

    # 输出：映射骨骼 + 祖先链（不含 n_root）
    keep: set[int] = set()
    for i in [sk.index(m.ffxiv) for m in BONE_MAP] + [sk.index(r.helper) for r in rules]:
        while i >= 0 and i not in keep and sk.names[i] != "n_root":
            keep.add(i)
            i = sk.parents[i]
    scale_of = lambda name: helper_scales[name] if name in helper_scales else ref_scale(name)
    bones = {sk.names[i]: (P[i], G[i].as_quat(), scale_of(sk.names[i])) for i in sorted(keep)
             if ivcs or not sk.names[i].startswith("iv_")}

    errors: dict[str, float] = {}
    # 自检只看纯角度复制的骨骼：末端补偿过的骨骼本来就会偏离照片角度
    compensated = {"j_kubi"} if compensation.get("head_shift_cm") else set()
    if distribute_neck:
        compensated |= {"j_sebo_c", "j_kubi"}
    for sd in "lr":
        if compensation.get(sd, {}).get("weight", 0) > 0:
            compensated |= {f"j_ude_a_{sd}", f"j_ude_b_{sd}"}
    if mhr_model is not None:
        model = mhr_model
        for m in BONE_MAP:
            if not m.ffxiv_tip or m.ffxiv in compensated:
                continue
            a = P[sk.index(m.ffxiv_tip)] - P[sk.index(m.ffxiv)]
            b = model[mhr_names.index(m.mhr_tip)] - model[mhr_names.index(m.mhr)]
            cos = np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b))
            errors[m.ffxiv] = float(np.degrees(np.arccos(np.clip(cos, -1, 1))))
    return RetargetResult(bones=bones, segment_error_deg=errors, root_offset=np.asarray(root_offset, dtype=float),
                          all_rotations={n: G[i].as_quat() for i, n in enumerate(sk.names)},
                          all_positions={n: P[i] for i, n in enumerate(sk.names)},
                          hand_compensation=compensation,
                          upright_rotation=upright_rot.as_quat() if upright_rot is not None else None)


def to_brio_pose_json(result: RetargetResult, extra: dict | None = None) -> str:
    """extra：额外写进去的骨骼（如脸部骨骼，见 face_rig），格式同 result.bones。"""
    fmt = lambda v: ", ".join(f"{float(x):.7g}" for x in v)
    doc = {
        "FileExtension": ".pose",
        "TypeName": "Brio Pose",
        "FileVersion": 3,
        "ModelDifference": {"Position": "0, 0, 0", "Rotation": "0, 0, 0, 1", "Scale": "0, 0, 0"},
        "ModelAbsoluteValues": {"Position": "0, 0, 0", "Rotation": "0, 0, 0, 1", "Scale": "1, 1, 1"},
        "Bones": {name: {"Position": fmt(p), "Rotation": fmt(q), "Scale": fmt(sc)}
                  for name, (p, q, sc) in {**result.bones, **(extra or {})}.items()},
        "MainHand": {}, "OffHand": {}, "Prop": {}, "Ornament": {},
        "Position": "0, 0, 0", "Rotation": "0, 0, 0, 1", "Scale": "1, 1, 1",
    }
    return json.dumps(doc, ensure_ascii=False, indent=2)
