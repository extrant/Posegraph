"""照片表情 → FF14 脸部骨骼（几何驱动，按游戏实测的骨骼限度夹紧）。

数据来自游戏自带的表情动画：插件命令 /posegraph facedump 把每个种族、每套面部骨架的表情动画逐帧采样导出
（文档\\Posegraph\\FaceDump\\cXXXX_fYYYY.json.gz，见插件 FaceAnimationDump.cs），这里不分发任何游戏数据。
这些动画**只用来量骨骼能动多少、怎么动**，不把现成的表情当模板套：

1. 认骨架：拿角色当前姿势里几根脸部骨骼相对头（j_kao）的位置，和这个种族的每套导出骨架比，取最像的。
2. 骨骼限度：统计这套骨架所有动画所有帧里，每根骨骼局部旋转（旋转向量）和位移的上下限，
   以及「位移多少时通常伴随多少旋转」的线性关系（嘴角上提时会跟着转一点）。
3. 照片测量见 face_geom：关键点对齐到 MediaPipe 的标准脸（厘米），量各部位相对中性脸的偏离。
4. 驱动：
   - 嘴角、嘴唇、眉、脸颊、鼻翼这些靠位移动的骨骼：按「游戏脸 ↔ 标准脸」对齐后，
     每根骨骼跟随正前方最近的标准脸关键点，位移按两张脸的眼距比例换算；旋转按第 2 步的关系补上。
     左右各自独立，歪嘴、单侧笑都按实际量还原。
   - 下唇挂在下巴下面：先转下巴，下唇只补下巴带不到的部分（露齿笑：下巴不怎么张，上唇上提、下唇下拉）。
   - 眼睑、下巴主要靠旋转：按睁眼比例、下眼睑抬起、下巴张开量，在游戏实测的中性 → 极限之间插值
     （极限 = 上眼睑转得最多的那一帧、下巴转得最多的那一帧，同侧同部位的骨骼一起取）。
   - 视线：瞳孔在眼眶里的位置换成角度。
5. 每根骨骼每个分量都夹在第 2 步的范围内，不超出游戏动画做得到的幅度。
6. 以导出的中性脸为底，挂到重定向后的头上，换成 Brio 姿势用的模型空间。

MediaPipe 的 Left = 被拍者自己的左 = FF14 骨骼名的 _l（2026-10-28 用合成闭眼图验证），标准脸 +x 也是被拍者的左。
"""

from __future__ import annotations

import gzip
import json
import re
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation as R

from . import face_geom
from .config import settings

# 跟随关键点、按位移驱动的骨骼（名字去掉 j_f_ 和 _l/_r）→ 只在这一部位的标准脸关键点里找最近的
# （两张脸眉眼、嘴的比例不同，不限定部位时眉骨骼会配到眼睑上）
_LIPS = (61, 146, 91, 181, 84, 17, 314, 405, 321, 375, 291, 185, 40, 39, 37, 0, 267, 269, 270, 409,
         78, 95, 88, 178, 87, 14, 317, 402, 318, 324, 308, 191, 80, 81, 82, 13, 312, 311, 310, 415)
_LIPS_UPPER = (61, 185, 40, 39, 37, 0, 267, 269, 270, 409, 291, 191, 80, 81, 82, 13, 312, 311, 310, 415)
_LIPS_LOWER = (61, 146, 91, 181, 84, 17, 314, 405, 321, 375, 291, 95, 88, 178, 87, 14, 317, 402, 318, 324)
_BROWS = (70, 63, 105, 66, 107, 46, 53, 52, 65, 55, 300, 293, 334, 296, 336, 276, 283, 282, 295, 285, 9, 8, 168)
_CHEEKS = (50, 101, 118, 117, 123, 147, 187, 205, 36, 206, 216, 207, 212, 214, 192,
           280, 330, 347, 346, 352, 376, 411, 425, 266, 426, 436, 427, 432, 434, 416)
_NOSE = (48, 64, 98, 97, 99, 240, 235, 219, 278, 294, 327, 326, 328, 460, 455, 439)
_CHIN = (202, 204, 210, 211, 32, 194, 422, 424, 430, 431, 262, 418, 200, 199, 175, 18)
FOLLOW = {"uslip": _LIPS, "dslip": _LIPS, "ulip_01": _LIPS_UPPER, "umlip_01": _LIPS_UPPER, "dlip_01": _LIPS_LOWER,
          "dmlip_01": _LIPS_LOWER, "dmemoto": _LIPS_LOWER + _CHIN, "mayu": _BROWS, "mmayu": _BROWS, "miken_01": _BROWS,
          "dmiken": _BROWS, "hoho": _CHEEKS, "shoho": _CHEEKS, "dhoho": _CHEEKS, "hana": _NOSE}
# 界面上的分组权重（RetargetParams.face_gains 的键）：跟随骨骼按部位归组，眉毛之外的（唇、嘴角、脸颊、鼻翼、下巴肉）都算嘴部
FACE_GROUPS = ("eyes", "brows", "mouth", "jaw", "gaze")
_BROW_FOLLOW = ("mayu", "mmayu", "miken_01", "dmiken")
JAW_CHILD = re.compile(r"^j_f_(dlip|dmlip|dago|hagukidn|bero)")   # 挂在下巴下的
LID_BONES = re.compile(r"^j_f_(mab|mabup|mabdn|eyeprm|eyeprmroll|irisprm)")
EYE_CLOSED_RATIO = 0.12      # 睁眼比例低于这个算全闭
EYE_WIDE_RATIO = 1.45        # 高于这个算睁到最大
LOWER_LID_FULL_CM = 0.25     # 下眼睑抬这么多（厘米，标准脸）算眯到最大
GAZE_DEG_PER_RATIO = 45.0    # 瞳孔偏离眼眶半宽的比例 → 眼球角度（只在没有表情系数时用）
GAZE_DEG_PER_COEF = 35.0     # 视线系数（eyeLook*，去掉 0.1 底噪后）1 → 眼球角度
JAW_PER_LIP_GAP = 0.6        # 上下唇分开 1cm ≈ 下巴尖往下 0.6cm（唇缝的一部分是嘴唇自己张开的，比如露齿笑）
FOLLOW_DEADZONE_CM = 0.1     # 关键点位移先扣掉这么多（软死区）：标准脸和本人的脸型差、识别抖动不当表情
MATCH_BONES = ("j_f_face", "j_f_eye_l", "j_f_eye_r", "j_f_mab_l", "j_f_mab_r", "j_f_hana_l", "j_f_hana_r", "j_f_uhana", "j_f_ago")


def dump_dir() -> Path:
    return settings.face_dump_dir


def available(race: str) -> bool:
    return any(dump_dir().glob(f"{race}_f*.json.gz"))


# ---------------------------------------------------------------- 骨架和限度

class FaceSkeleton:
    """一套面部骨架的导出数据：骨骼、父子、参考姿势（局部），以及从全部动画统计出的各骨骼限度。"""

    def __init__(self, path: Path):
        d = json.load(gzip.open(path))
        self.name = path.name.removesuffix(".json.gz")
        self.bones: list[str] = d["bones"]
        self.parents: list[int] = d["parents"]
        ref = np.asarray(d["reference"], dtype=np.float64)
        self.ref_t, self.ref_q, self.ref_s = ref[:, :3], ref[:, 3:7], ref[:, 7]
        self.index = {b: i for i, b in enumerate(self.bones)}
        self.has_anims = any(a["bones"] for a in d["anims"].values())
        self._limits(d["anims"])
        self._extremes = {}
        self._follow = None

    def _limits(self, anims: dict) -> None:
        """每根骨骼：旋转向量（弧度）和位移的上下限（含中性 0）、位移 → 旋转的线性关系；以及全部帧（找极限帧用）。"""
        n = len(self.bones)
        rot = [[] for _ in range(n)]
        tr = [[] for _ in range(n)]
        self.frames: list[dict[int, tuple[np.ndarray, np.ndarray]]] = []
        ref_inv = [R.from_quat(q).inv() for q in self.ref_q]
        for a in anims.values():
            idx = a["bones"]
            for fr in a["frames"]:
                frame = {}
                for k, b in enumerate(idx):
                    t = np.asarray(fr[k], dtype=np.float64)
                    rv = (ref_inv[b] * R.from_quat(t[3:7])).as_rotvec()
                    dt = t[:3] - self.ref_t[b]
                    rot[b].append(rv)
                    tr[b].append(dt)
                    frame[b] = (rv, dt)
                self.frames.append(frame)
        self.rot_lo, self.rot_hi = np.zeros((n, 3)), np.zeros((n, 3))
        self.tr_lo, self.tr_hi = np.zeros((n, 3)), np.zeros((n, 3))
        self.tr_to_rot = np.zeros((n, 3, 3))
        for b in range(n):
            if not rot[b]:
                continue
            rv, dt = np.asarray(rot[b]), np.asarray(tr[b])
            self.rot_lo[b], self.rot_hi[b] = np.minimum(rv.min(0), 0), np.maximum(rv.max(0), 0)
            self.tr_lo[b], self.tr_hi[b] = np.minimum(dt.min(0), 0), np.maximum(dt.max(0), 0)
            if np.abs(dt).max() > 5e-4:   # 位移过 0.5mm 才算位移驱动；最小二乘 rv ≈ dt @ A（加一点岭回归防病态）
                self.tr_to_rot[b] = np.linalg.solve(dt.T @ dt + 1e-6 * np.eye(3), dt.T @ rv)

    def fk(self, t: np.ndarray, q: np.ndarray) -> tuple[np.ndarray, R]:
        """局部 → 面部骨架模型空间（位置、旋转）。父骨骼总在子骨骼前面。"""
        pos = np.zeros((len(self.bones), 3))
        rots = [None] * len(self.bones)
        for i, p in enumerate(self.parents):
            local = R.from_quat(q[i])
            if p < 0:
                pos[i], rots[i] = t[i], local
            else:
                pos[i], rots[i] = pos[p] + rots[p].apply(t[i]), rots[p] * local
        return pos, R.concatenate(rots)

    def extreme(self, key: str, bone: str, axis: int, sign: float, group: re.Pattern, side: str | None) -> dict[int, tuple[np.ndarray, np.ndarray]]:
        """极限帧：bone 的局部旋转第 axis 个分量朝 sign 方向最大的那一帧；返回这一帧里 group 部位、side 一侧骨骼的（旋转向量, 位移）。"""
        if key in self._extremes:
            return self._extremes[key]
        i = self.index.get(bone)
        best, best_v = None, 0.0
        if i is not None:
            for fr in self.frames:
                if i in fr and fr[i][0][axis] * sign > best_v:
                    best, best_v = fr, fr[i][0][axis] * sign
        out = {}
        if best is not None:
            for b, v in best.items():
                name = self.bones[b]
                if group.search(name) and (side is None or name.endswith("_" + side)):
                    out[b] = v
        self._extremes[key] = out
        return out

    def follow_map(self) -> tuple[dict[int, int], R, float, object]:
        """跟随表：骨骼下标 → 中性脸关键点编号；中性脸 → 游戏脸的旋转、比例（厘米 → 米），以及把中性脸上的点换到游戏面部空间的函数
        （两眼、两嘴角对齐）。"""
        if self._follow is not None:
            return self._follow
        pos, _ = self.fk(self.ref_t, self.ref_q)
        canon = face_geom.neutral()
        eye = lambda e: (canon[e["outer"]] + canon[e["inner"]]) / 2
        pairs = [("j_f_eye_l", eye(face_geom.EYE["l"])), ("j_f_eye_r", eye(face_geom.EYE["r"])),
                 ("j_f_uslip_l", canon[face_geom.CORNER["l"]]), ("j_f_uslip_r", canon[face_geom.CORNER["r"]])]
        pairs = [(pos[self.index[b]], c) for b, c in pairs if b in self.index]
        src, dst = np.array([p for p, _ in pairs]), np.array([c for _, c in pairs])
        ms, md = src.mean(0), dst.mean(0)
        u, s, vt = np.linalg.svd((dst - md).T @ (src - ms))
        dfix = np.sign(np.linalg.det(u @ vt))
        rot = u @ np.diag([1, 1, dfix]) @ vt                  # 游戏 → 标准脸
        scale = (s * [1, 1, dfix]).sum() / ((src - ms) ** 2).sum()
        in_canon = (scale * (rot @ (pos - ms).T)).T + md
        mapping = {}
        for i, b in enumerate(self.bones):
            core = re.sub(r"^j_f_|_[lr]$", "", b)
            if core in FOLLOW:
                cand = np.asarray(FOLLOW[core])
                if b.endswith(("_l", "_r")):   # 只在同一侧找（+x 是左）
                    side = 1 if b.endswith("_l") else -1
                    cand = cand[canon[cand, 0] * side > -0.05]
                d = np.linalg.norm(canon[cand, :2] - in_canon[i, :2], axis=1)
                mapping[i] = int(cand[np.argmin(d)])
        to_game = R.from_matrix(rot).inv()
        place = lambda p: to_game.apply((np.asarray(p) - md) / scale) + ms
        self._follow = (mapping, to_game, 1.0 / scale, place)
        return self._follow


@lru_cache(maxsize=32)
def _load(path: str) -> FaceSkeleton:
    return FaceSkeleton(Path(path))


def skeletons(race: str) -> list[FaceSkeleton]:
    """这个种族所有带表情动画的面部骨架。"""
    return [sk for p in sorted(dump_dir().glob(f"{race}_f*.json.gz")) if (sk := _load(str(p))).has_anims]


def match(race: str, reference=None) -> FaceSkeleton | None:
    """按角色当前姿势（BrioPose，模型空间）认出用的是哪套面部骨架；没有可比的骨骼时取第一套。"""
    candidates = skeletons(race)
    if not candidates:
        return None
    if reference is None or reference.get("j_kao") is None:
        return candidates[0]
    head = reference.get("j_kao")
    kq = R.from_quat(head.rotation)
    rel = {b: kq.inv().apply(np.asarray(t.position) - np.asarray(head.position))
           for b in MATCH_BONES if (t := reference.get(b)) is not None}
    if len(rel) < 4:
        return candidates[0]

    def error(sk: FaceSkeleton) -> float:
        pos, rot = sk.fk(sk.ref_t, sk.ref_q)
        k = sk.index.get("j_kao")
        if k is None or any(b not in sk.index for b in rel):
            return np.inf
        return float(np.mean([np.linalg.norm(rot[k].inv().apply(pos[sk.index[b]] - pos[k]) - v) for b, v in rel.items()]))

    return min(candidates, key=error)


# ---------------------------------------------------------------- 换算

BLINK_LO, BLINK_HI = 0.35, 0.75   # 表情系数 eyeBlink 在这之间换成闭眼 0~1


def controls(m: dict[str, float], bs: dict[str, float] | None = None) -> dict[str, float]:
    """测量值 → 眼睑的控制量（0~1）。闭眼取几何（睁眼比例）和表情系数 eyeBlink 里大的那个：
    笑到眯眼和眨一只眼在眼睑位置上差不多，分类器分得更清楚（MediaPipe 的 Left = 被拍者的左）。"""
    out = {}
    bs = bs or {}
    for s in "lr":
        r = m.get(f"eye_open_{s}", 1.0)
        blink = bs.get("eyeBlinkLeft" if s == "l" else "eyeBlinkRight", 0.0)
        out[f"eye_close_{s}"] = float(max(np.clip((1 - r) / (1 - EYE_CLOSED_RATIO), 0, 1),
                                          np.clip((blink - BLINK_LO) / (BLINK_HI - BLINK_LO), 0, 1)))
        out[f"eye_wide_{s}"] = float(np.clip((r - 1) / (EYE_WIDE_RATIO - 1), 0, 1))
        out[f"squint_{s}"] = float(np.clip(m.get(f"lower_lid_{s}", 0.0) / LOWER_LID_FULL_CM, 0, 1))
    return out


SUMMARY = (("eye_close_l", "左眼闭"), ("eye_close_r", "右眼闭"), ("eye_wide_l", "左眼睁大"), ("eye_wide_r", "右眼睁大"),
           ("squint_l", "左眼眯"), ("squint_r", "右眼眯"), ("brow_inner_l", "左眉头高(cm)"), ("brow_inner_r", "右眉头高(cm)"),
           ("corner_up_l", "左嘴角上(cm)"), ("corner_up_r", "右嘴角上(cm)"), ("corner_out_l", "左嘴角外(cm)"), ("corner_out_r", "右嘴角外(cm)"),
           ("upper_lip_up", "上唇上提(cm)"), ("lower_lip_down", "下唇下拉(cm)"), ("jaw_open", "下巴张开(cm)"), ("mouth_side", "嘴偏向左(cm)"))


def summary(m: dict, bs: dict | None = None) -> dict[str, float]:
    """给界面看的主要测量（中文名 → 值，小于 0.05 的不列）。左 / 右 = 照片里那个人自己的左 / 右。"""
    values = {**{k: v for k, v in m.items() if not k.startswith("_")}, **controls(m, bs)}
    return {label: round(float(values[k]), 2) for k, label in SUMMARY if k in values and abs(values[k]) >= 0.05}


def solve(sk: FaceSkeleton, measures: dict[str, float], strength: float = 1.0,
          blendshapes: dict[str, float] | None = None, gains: dict[str, float] | None = None) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """面部骨架的局部姿势（位移、四元数 xyzw、缩放）。measures 见 face_geom.measure；没有几何测量时（旧会话）只转视线。
    gains：分组权重（FACE_GROUPS → 倍数，缺省 1），和总强度相乘；结果照样夹在游戏实测范围里。"""
    gain = lambda group: strength * float((gains or {}).get(group, 1.0))
    n = len(sk.bones)
    rv = np.zeros((n, 3))        # 局部旋转增量（旋转向量，弧度）
    dt = np.zeros((n, 3))        # 局部位移增量
    c = controls(measures, blendshapes) if measures else {}

    def add(extreme: dict, w: float) -> None:
        for b, (r, t) in extreme.items():
            rv[b] += r * w
            dt[b] += t * w

    lid = lambda s: sk.index.get(f"j_f_mabup_01_{s}")
    for s in "lr":
        if lid(s) is None or not measures:
            continue
        # 上眼睑绕局部 z 转：负 = 闭（实测 -30°），正 = 睁大；下眼睑抬起取 mabdn_01 绕 x 转得最多的一帧
        add(sk.extreme(f"close_{s}", f"j_f_mabup_01_{s}", 2, -1, LID_BONES, s), c[f"eye_close_{s}"] * gain("eyes"))
        add(sk.extreme(f"wide_{s}", f"j_f_mabup_01_{s}", 2, +1, LID_BONES, s), c[f"eye_wide_{s}"] * gain("eyes"))
        sign = -1 if s == "l" else 1
        add(sk.extreme(f"squint_{s}", f"j_f_mabdn_01_{s}", 0, sign, re.compile(r"^j_f_mabdn"), s), c[f"squint_{s}"] * gain("eyes"))

    # 下巴：按下巴（关键点 152）往下的量，在中性 → 下巴转得最多的那一帧之间插值
    jaw = sk.extreme("jaw", "j_f_ago", 2, -1, re.compile(r"^j_(f_)?ago$"), None)
    mapping, to_game, cm_to_m, place = sk.follow_map()
    if measures and jaw and "j_f_ago" in sk.index:
        ext_t, ext_q = sk.ref_t.copy(), sk.ref_q.copy()
        for b, (r, t) in jaw.items():
            ext_q[b] = (R.from_quat(sk.ref_q[b]) * R.from_rotvec(r)).as_quat()
            ext_t[b] += t
        p0, r0 = sk.fk(sk.ref_t, sk.ref_q)
        p1, r1 = sk.fk(ext_t, ext_q)
        # 下巴尖：把中性脸的下巴关键点换到游戏脸上，跟着下巴骨骼转（下巴骨骼本身离转轴太近，转 18° 也只挪一点点）
        a = sk.index["j_f_ago"]
        chin = place(face_geom.neutral()[face_geom.CHIN])
        chin_open = p1[a] + (r1[a] * r0[a].inv()).apply(chin - p0[a])
        game_drop = float(chin[1] - chin_open[1])                         # 游戏下巴最多往下多少（米）
        # 照片下巴往下多少（换到游戏脸的米）。鼻底到下巴的长度因人而异（下巴短的人张嘴也量成负的），
        # 再参考上下唇内缘的间距：闭嘴时谁都接近 0，张开主要靠下巴
        drop = max(measures.get("jaw_open", 0.0), JAW_PER_LIP_GAP * measures.get("lips_apart", 0.0))
        want = drop * cm_to_m
        if game_drop > 1e-5:
            add(jaw, float(np.clip(want / game_drop * gain("jaw"), 0, 1)))

    # 跟随关键点的骨骼：照片上对应关键点的位移 → 游戏脸的位移（下巴下的骨骼扣掉下巴已经带过去的部分）
    if measures.get("_aligned") is not None:
        aligned = np.asarray(measures["_aligned"])
        canon = face_geom.neutral()
        q_now = np.stack([(R.from_quat(sk.ref_q[i]) * R.from_rotvec(rv[i])).as_quat() for i in range(n)])
        pos_now, rot_now = sk.fk(sk.ref_t + dt, q_now)
        pos_ref, _ = sk.fk(sk.ref_t, sk.ref_q)
        for b, v in mapping.items():
            disp = aligned[v] - canon[v]
            size = np.linalg.norm(disp)
            if size <= FOLLOW_DEADZONE_CM:
                continue
            disp = disp * (1 - FOLLOW_DEADZONE_CM / size)
            group = "brows" if re.sub(r"^j_f_|_[lr]$", "", sk.bones[b]) in _BROW_FOLLOW else "mouth"
            want = to_game.apply(disp) * cm_to_m * gain(group)            # 游戏面部空间里想要的位移
            if JAW_CHILD.search(sk.bones[b]):
                want = want - (pos_now[b] - pos_ref[b])                     # 下巴已经带过去的
            parent = sk.parents[b]
            local = rot_now[parent].inv().apply(want) if parent >= 0 else want
            dt[b] += local
            rv[b] += local @ sk.tr_to_rot[b]

    # 夹到游戏实测的范围
    rv = np.clip(rv, sk.rot_lo, sk.rot_hi)
    dt = np.clip(dt, sk.tr_lo, sk.tr_hi)
    t = sk.ref_t + dt
    q = np.stack([(R.from_quat(sk.ref_q[i]) * R.from_rotvec(rv[i])).as_quat() for i in range(n)])
    _gaze(sk, t, q, measures, blendshapes or {}, gain("gaze"))
    return t, q, sk.ref_s.copy()


def _eye_angles(m: dict[str, float], bs: dict[str, float]) -> dict[str, tuple[float, float]]:
    """每只眼看的方向（度）：左右（+ 朝被拍者的左边）、上下（+ 朝上），相对头。"""
    dz = lambda v: max(0.0, v - 0.1) / 0.9
    out = {}
    for side in "lr":
        if bs:
            name = "Left" if side == "l" else "Right"
            toward_left = (dz(bs.get("eyeLookOutLeft", 0)) - dz(bs.get("eyeLookInLeft", 0)) if side == "l"
                           else dz(bs.get("eyeLookInRight", 0)) - dz(bs.get("eyeLookOutRight", 0)))
            up = dz(bs.get(f"eyeLookUp{name}", 0)) - dz(bs.get(f"eyeLookDown{name}", 0))
            out[side] = (toward_left * GAZE_DEG_PER_COEF, up * GAZE_DEG_PER_COEF)
        elif f"gaze_x_{side}" in m:
            out[side] = (m[f"gaze_x_{side}"] * GAZE_DEG_PER_RATIO, m[f"gaze_y_{side}"] * GAZE_DEG_PER_RATIO)
    return out


def _eye_visibility(rotation) -> dict[str, float]:
    """两只眼各自朝镜头的程度（权重）：侧脸时远侧那只眼被鼻梁挡住，它的视线系数不可信（2026-10-28 测试图里侧脸的远侧眼
    常和近侧眼反着来）。没有脸部朝向时两只一样。"""
    if rotation is None or not len(rotation):
        return {"l": 1.0, "r": 1.0}
    rot = np.asarray(rotation, dtype=np.float64)
    out = {}
    for side, x in (("l", 1.0), ("r", -1.0)):
        toward_camera = float((rot @ np.array([x * 0.7, 0.0, 0.7]))[2])   # 眼所在那一侧的脸面朝镜头的分量（OpenGL：+z 朝镜头）
        out[side] = max(toward_camera, 0.05) ** 2
    return out


def _gaze(sk: FaceSkeleton, t: np.ndarray, q: np.ndarray, m: dict[str, float], bs: dict[str, float], strength: float) -> None:
    """视线：在面部骨架的模型空间里转眼球（y 朝上、角色面朝 +z、+x 是角色左侧），再换回局部；夹到游戏实测的眼球转动范围。
    用 MediaPipe 的视线系数（eyeLookOutLeft = 左眼朝被拍者的左边看）：它按各种头部姿态训练过，
    低头、侧脸、透视大时比按瞳孔在眼眶里的位置算准得多（2026-10-28 测试图对比）；没有系数时才退回瞳孔位置。
    两眼看同一个方向（共轭）：按各自朝镜头的程度加权平均，侧脸时以看得见的那只眼为准。"""
    angles = _eye_angles(m, bs)
    if not angles:
        return
    vis = _eye_visibility(m.get("_rotation"))
    total = sum(vis[s] for s in angles)
    yaw = sum(angles[s][0] * vis[s] for s in angles) / total
    pitch = sum(angles[s][1] * vis[s] for s in angles) / total
    for side in "lr":
        i = sk.index.get(f"j_f_eye_{side}")
        if i is None:
            continue
        # 游戏实测：眼球左右最多约 27.5°，上下按导出动画里的范围（局部转动量的大小）
        limit = float(np.degrees(np.abs([sk.rot_lo[i], sk.rot_hi[i]]).max())) or 27.5
        y = float(np.clip(yaw * strength, -limit, limit))
        p = float(np.clip(pitch * strength, -limit, limit))
        if abs(y) < 0.5 and abs(p) < 0.5:
            continue
        pos, rots = sk.fk(t, q)
        turn = R.from_euler("YX", [y, -p], degrees=True)
        parent = sk.parents[i]
        new_model = turn * rots[i]
        q[i] = ((rots[parent].inv() * new_model) if parent >= 0 else new_model).as_quat()


def to_model(sk: FaceSkeleton, t: np.ndarray, q: np.ndarray, s: np.ndarray, head) -> dict[str, tuple[np.ndarray, np.ndarray, np.ndarray]]:
    """挂到角色的头上：head = (模型空间位置, 四元数 xyzw, 缩放) 是重定向结果里的 j_kao。返回 Brio 姿势用的骨骼（不含 j_kao）。"""
    pos, rots = sk.fk(t, q)
    k = sk.index["j_kao"]
    hp, hq, hs = np.asarray(head[0], dtype=np.float64), R.from_quat(head[1]), float(np.asarray(head[2]).reshape(-1)[0])
    kq_inv = rots[k].inv()
    out = {}
    for i, b in enumerate(sk.bones):
        if i == k:
            continue
        rel_p = kq_inv.apply(pos[i] - pos[k])
        out[b] = (hp + hq.apply(rel_p * hs), (hq * kq_inv * rots[i]).as_quat(), np.full(3, s[i]))
    return out


def measure(landmarks, rotation=None) -> dict:
    """照片关键点 → 测量值（含对齐后的全部关键点 _aligned，跟随骨骼用）；没有关键点时为空。
    有脸部朝向（rotation）时用不依赖深度的二维对齐，否则按三维关键点对齐。"""
    if landmarks is None or len(landmarks) < 468:
        return {}
    if rotation is not None and len(rotation):
        aligned = face_geom.align_2d(np.asarray(landmarks), np.asarray(rotation))
    else:
        aligned = face_geom.align(np.asarray(landmarks))
    out = face_geom.measure(aligned)
    out["_aligned"] = aligned
    if rotation is not None and len(rotation):
        out["_rotation"] = np.asarray(rotation, dtype=np.float64)
    return out


def face_bones(race: str, reference, head, landmarks, strength: float = 1.0, blendshapes: dict | None = None,
               rotation=None, gains: dict | None = None) -> tuple[dict, str | None, dict]:
    """一步到位：认骨架 → 测量 → 换算 → 模型空间。返回 (骨骼, 用的骨架名, 控制量)；没有导出数据时 ({}, None, {})。
    rotation：脸的朝向（face.FaceResult.rotation），有它时测量不依赖关键点深度、视线以看得见的那只眼为准。"""
    sk = match(race, reference)
    if sk is None:
        return {}, None, {}
    m = measure(landmarks, rotation)
    t, q, s = solve(sk, m, strength, blendshapes, gains)
    return to_model(sk, t, q, s, head), sk.name, summary(m, blendshapes) if m else {}
