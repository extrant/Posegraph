"""
视频片段的时间轴修正：在某些时间点（关键帧）改几根骨骼，相邻的帧按影响范围过渡。

每个关键帧存「骨骼 → 四元数」，两种意思（kind）：
- delta：相对父骨骼的改动量 D，新的局部朝向 = D × 原来的局部朝向。跟着原动作走（身体转身、走动时修正一起转），
  用户在 Brio 里改骨骼收进来的、「粘贴改动」贴的都是这种
- pose：相对父骨骼的朝向，原样贴上（「粘贴骨骼姿势」：比如把一整只手的手势贴到一段时间里）
影响范围（mode）：ease 前后 ease 秒内渐入渐出；hold 一直保持到这根骨骼的下一个关键帧（之前同样按 ease 秒渐入）。
用户挑的帧要以用户为准：过渡时间的前 FULL_SHARE 完全按修正（权重 1），后面才平滑退回原动作——
不然一离开关键帧就开始变淡，再经过时间平滑，改的那一下看起来没改到位（2026-11-01 用户要求「修改的权重大一点」）。
同一根骨骼相邻两个关键帧的影响范围连着（或前一个是 hold）时，从前一个直接过渡到后一个，中间不回到原动作。

时间是片段里的秒（第 k 帧 = k / fps，和插件播放器的时间一致）。修正在重定向里、辅助骨之前施加（ffxiv.retarget 的 edits），
肘 / 膝 / 肩的辅助骨照样联动。
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np
from scipy.spatial.transform import Rotation as R

KINDS = ("delta", "pose")
FULL_SHARE = 0.4    # 过渡时间里离关键帧最近的这一段完全按修正
MODES = ("ease", "hold")


@dataclass
class Key:
    time: float
    bones: dict[str, np.ndarray] = field(default_factory=dict)   # 骨骼名 → 四元数 xyzw
    kind: str = "delta"
    mode: str = "ease"
    ease: float = 0.5

    @staticmethod
    def from_json(d: dict) -> "Key":
        kind, mode = d.get("kind", "delta"), d.get("mode", "ease")
        if kind not in KINDS or mode not in MODES:
            raise ValueError(f"kind / mode 不对：{kind} / {mode}")
        bones = {}
        for name, q in (d.get("bones") or {}).items():
            q = np.asarray(q, float)
            if q.shape != (4,) or not np.isfinite(q).all() or np.linalg.norm(q) < 1e-6:
                raise ValueError(f"{name} 的四元数不对")
            bones[name] = q / np.linalg.norm(q)
        return Key(float(d["time"]), bones, kind, mode, max(0.0, float(d.get("ease", 0.5))))

    def to_json(self) -> dict:
        return {"time": round(self.time, 6), "kind": self.kind, "mode": self.mode, "ease": round(self.ease, 4),
                "bones": {k: np.round(v, 7).tolist() for k, v in self.bones.items()}}


@dataclass
class BoneEdit:
    """某一帧一根骨骼的修正：从 a 往 b 插值 s（b 为 None 时只有 a），再按权重 w 从「不改」过渡到它。a、b = (kind, 四元数)。"""
    a: tuple[str, np.ndarray]
    b: tuple[str, np.ndarray] | None = None
    s: float = 0.0
    w: float = 1.0


def _smoothstep(x: float) -> float:
    x = min(max(x, 0.0), 1.0)
    return x * x * (3 - 2 * x)


def _falloff(distance: float, ease: float) -> float:
    """离关键帧 distance 秒时的权重：前 FULL_SHARE × ease 秒为 1，之后到 ease 秒平滑降到 0；ease 为 0 时只影响关键帧那一刻。"""
    if distance <= 1e-6 or distance <= FULL_SHARE * ease:
        return 1.0
    return 0.0 if ease <= 0 else _smoothstep(1 - (distance - FULL_SHARE * ease) / ((1 - FULL_SHARE) * ease))


def _between(x: float) -> float:
    """两个关键帧之间的过渡（0~1）：两头各留一段完全按那一头的关键帧，中间平滑换过去。"""
    hold = FULL_SHARE / 2
    return _smoothstep((x - hold) / (1 - 2 * hold))


def at(keys: list[Key], t: float) -> dict[str, BoneEdit]:
    """第 t 秒每根被修正的骨骼的修正（权重为 0 的不在结果里）。"""
    out: dict[str, BoneEdit] = {}
    for bone in sorted({b for k in keys for b in k.bones}):
        ks = sorted((k for k in keys if bone in k.bones), key=lambda k: k.time)
        prev = next((k for k in reversed(ks) if k.time <= t + 1e-6), None)
        nxt = next((k for k in ks if k.time > t + 1e-6), None)
        val = lambda k: (k.kind, k.bones[bone])
        edit = None
        if prev is None:
            if (w := _falloff(nxt.time - t, nxt.ease)) > 0:
                edit = BoneEdit(val(nxt), w=w)
        elif nxt is None:
            w = 1.0 if prev.mode == "hold" else _falloff(t - prev.time, prev.ease)
            if w > 0:
                edit = BoneEdit(val(prev), w=w)
        elif prev.mode == "hold" or nxt.time - prev.time <= prev.ease + nxt.ease:
            # 连着：从前一个过渡到后一个（hold 的在后一个的渐入时间里过渡）
            start = max(prev.time, nxt.time - nxt.ease) if prev.mode == "hold" else prev.time
            s = 1.0 if nxt.time <= start else _between((t - start) / (nxt.time - start))
            edit = BoneEdit(val(prev), val(nxt), s)
        else:
            wa, wb = _falloff(t - prev.time, prev.ease), _falloff(nxt.time - t, nxt.ease)
            if wa > 0 or wb > 0:
                edit = BoneEdit(val(prev), w=wa) if wa >= wb else BoneEdit(val(nxt), w=wb)
        if edit is not None:
            out[bone] = edit
    return out


def span(key: Key, keys: list[Key], duration: float) -> tuple[float, float]:
    """这个关键帧改动后要重算的时间段（秒）：渐入开始 ~ 渐出结束；hold 到它的骨骼后面最近的关键帧（没有就到结尾）。"""
    start = key.time - key.ease
    if key.mode == "hold":
        later = [k.time for k in keys if k.time > key.time + 1e-6 and set(k.bones) & set(key.bones)]
        end = min(later) if later else duration
    else:
        end = key.time + key.ease
    # 和前后的关键帧连着时，过渡段也会变
    for k in keys:
        if k is key or not set(k.bones) & set(key.bones):
            continue
        if k.time < key.time and (k.mode == "hold" or key.time - k.time <= k.ease + key.ease):
            start = min(start, k.time)
        if k.time > key.time and (key.mode == "hold" or k.time - key.time <= k.ease + key.ease):
            end = max(end, k.time)
    return max(0.0, start), min(duration, end)


def _slerp(a: R, b: R, s: float) -> R:
    return a * R.from_rotvec(s * (a.inv() * b).as_rotvec())


def apply(parents: list[int], index: dict[str, int], G: list[R], G_new: list[R | None], edits: dict[str, BoneEdit],
          subtree) -> None:
    """把修正施加到全局转动上（原地改 G 和 G_new）：父骨骼在前逐根处理，整棵子树一起转。
    parents / index：骨架的父骨骼序号、骨骼名 → 序号；subtree(i) → i 及其后代的序号。"""
    for name in sorted((n for n in edits if n in index), key=index.get):
        i, e = index[name], edits[name]
        p = parents[i]
        Gp = G[p] if p >= 0 else R.identity()
        local = Gp.inv() * G[i]

        def delta(term):
            kind, q = term
            q = R.from_quat(q)
            return q if kind == "delta" else q * local.inv()

        d = delta(e.a) if e.b is None else _slerp(delta(e.a), delta(e.b), e.s)
        d = R.from_rotvec(e.w * d.as_rotvec())
        turn = Gp * d * Gp.inv()          # 模型系里绕这根骨骼转
        if G_new[i] is None:
            G_new[i] = G[i]
        for j in subtree(i):
            G[j] = turn * G[j]
            if G_new[j] is not None:
                G_new[j] = turn * G_new[j]


# ---------------------------------------------------------------- 收进 Brio 的改动、复制骨骼姿势

_FINGERS = ("oya", "hito", "naka", "kusu", "ko")


def _hand(name: str, side: str) -> bool:
    if not name.endswith(f"_{side}"):
        return False
    return name == f"j_te_{side}" or any(name.startswith(f"j_{f}_") or name.startswith(f"iv_{f}_c") for f in _FINGERS)


# 「复制骨骼姿势」可选的骨骼组：组名 → 判断骨骼名是否属于这组（全身 = 能修正的全部骨骼）
GROUPS = {
    "hand_l": lambda n: _hand(n, "l"),
    "hand_r": lambda n: _hand(n, "r"),
    "hands": lambda n: _hand(n, "l") or _hand(n, "r"),
    "arm_l": lambda n: _hand(n, "l") or n in ("j_sako_l", "j_ude_a_l", "j_ude_b_l"),
    "arm_r": lambda n: _hand(n, "r") or n in ("j_sako_r", "j_ude_a_r", "j_ude_b_r"),
    "head": lambda n: n in ("j_kubi", "j_kao"),
    "body": lambda n: True,
}


def _local(names: list[str], parents: list[int], rots: dict[str, np.ndarray], name: str) -> R | None:
    if name not in rots:
        return None
    p = parents[names.index(name)]
    if p < 0:
        return R.from_quat(rots[name])
    if names[p] not in rots:
        return None
    return R.from_quat(rots[names[p]]).inv() * R.from_quat(rots[name])


def capture(names: list[str], parents: list[int], allowed: set[str], base: dict[str, np.ndarray],
            shown: dict[str, np.ndarray], brio: dict[str, np.ndarray], min_deg: float = 1.0) -> dict[str, np.ndarray]:
    """用户在 Brio 里改过的骨骼 → 关键帧（delta）：只收 allowed 里、局部朝向和画面上的（shown）差 min_deg 度以上的骨骼，
    改动量按原动作（不含任何修正，base）算：D = L_brio · L_base⁻¹，这一帧换成这个关键帧后正好是用户摆的样子。
    base / shown / brio：骨骼名 → 模型系四元数（同一帧）。只比局部朝向：改了上臂，下面的骨骼跟着转但局部没变，不算改。"""
    out = {}
    for name in sorted(allowed):
        lb, ls, lr = (_local(names, parents, rots, name) for rots in (base, shown, brio))
        if lb is None or ls is None or lr is None:
            continue
        if np.degrees((ls.inv() * lr).magnitude()) >= min_deg:
            out[name] = (lr * lb.inv()).as_quat()
    return out


def locals_of(names: list[str], parents: list[int], rots: dict[str, np.ndarray], bones: list[str]) -> dict[str, np.ndarray]:
    """这些骨骼相对父骨骼的朝向（pose 关键帧用）。"""
    out = {}
    for name in bones:
        if (local := _local(names, parents, rots, name)) is not None:
            out[name] = local.as_quat()
    return out
