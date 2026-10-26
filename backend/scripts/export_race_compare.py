"""导出多种族同一动作的 Blender 对比数据（配合 scripts/blender_race_compare.py）。

    .venv/Scripts/python scripts/export_race_compare.py outputs/xxx.npz [--races c0101,c0301,...] [-o out.json]

每个种族用自己的骨架数据重定向（骨长取该种族绑定姿势，相当于游戏里没有改体型的角色）。
很多种族在游戏里借用别的种族的装备模型（精灵 / 猫魅 / 敖龙 / 维埃拉 / 高地男的腿、手、脚用中原男 c0101 的模型），
载入时按种族形变文件 human.pbd 把网格逐骨骼变到本种族（见 retarget/pbd.py）。这里同样处理：
借用的部件带上每根骨骼的形变矩阵，Blender 里先按蒙皮权重形变顶点，再绑到本种族骨架上。
蒙皮变换 S = 平移(position) · (最终旋转 · 绑定旋转⁻¹) · 平移(-绑定位置)，与 rest 无关。
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

ROOT = Path(os.path.abspath(__file__)).parents[1]
sys.path.insert(0, str(ROOT / "src"))

TBSE = "outputs/tbsex"
RM = "outputs/race_models"


def _parts(body: str, head: str, deform_from: int | None = None, hands: str | None = None, top: str | None = None,
           extra: tuple[str, ...] = ()) -> dict:
    """部件 → (FBX 路径, 借用的种族编号或 None)。身体借用别的种族模型时 deform_from 为原种族（如 101）。"""
    parts = {p: (f"{body}/{p}.fbx", deform_from) for p in ("top", "dwn", "glv", "sho")}
    if hands:
        parts.update({p: (f"{hands}/{p}.fbx", None) for p in ("glv", "sho")})
    if top:
        parts["top"] = (f"{top}/top.fbx", None)
    parts["fac"] = (f"{head}/fac.fbx", None)
    for e in extra:
        parts[e] = (f"{head}/{e}.fbx", None)
    return parts


# 中原系男（骨架相同）都穿中原男的身体；高地男 TBSE-X 有专用上衣，腿手脚仍借用中原。
# 鲁加 / 硌狮用 hrBody 3（硌狮手脚专用），拉拉菲尔用游戏原版
MESHES = {
    "c0101": _parts(TBSE, TBSE),
    "c0301": _parts(TBSE, f"{RM}/c0301", 101, top=f"{RM}/c0301"),
    "c0701": _parts(TBSE, f"{RM}/c0701", 101, extra=("til",)),
    "c1301": _parts(TBSE, f"{RM}/c1301", 101, extra=("til",)),
    "c1701": _parts(TBSE, f"{RM}/c1701", 101, extra=("zer",)),
    "c0501": _parts(TBSE, f"{RM}/c0501", 101),
    "c0901": _parts(f"{RM}/c0901", f"{RM}/c0901"),
    "c1501": _parts(f"{RM}/c0901", f"{RM}/c1501", hands=f"{RM}/c1501", extra=("til",)),
    "c1101": _parts(f"{RM}/c1101", f"{RM}/c1101"),
    # 女性用游戏原版身体（e0000）：只有中原女与拉拉菲尔女有自己的模型，其余借用中原女 c0201 按 human.pbd 形变；
    # 拉拉菲尔女的手脚用拉拉菲尔男的（同一骨架）
    "c0201": _parts(f"{RM}/c0201", f"{RM}/c0201"),
    "c0401": _parts(f"{RM}/c0201", f"{RM}/c0401", 201),
    "c1601": _parts(f"{RM}/c0201", f"{RM}/c1601", 201),
    "c0601": _parts(f"{RM}/c0201", f"{RM}/c0601", 201),
    "c0801": _parts(f"{RM}/c0201", f"{RM}/c0801", 201),
    "c1001": _parts(f"{RM}/c0201", f"{RM}/c1001", 201),
    "c1401": _parts(f"{RM}/c0201", f"{RM}/c1401", 201),
    "c1801": _parts(f"{RM}/c0201", f"{RM}/c1801", 201),
    "c1201": _parts(f"{RM}/c1201", f"{RM}/c1201", hands=f"{RM}/c1101"),
}
GROUPS = {"base": "c0101,c0301,c0701,c1301,c1701", "all": "c0101,c0501,c0901,c1501,c1101",
          "female": "c0201,c0601,c0801,c1201"}

# 导出的 FBX 包含平时被属性隐藏的可选网格，叠在一起会出现碎片（hrBody 上衣 Part 1.x 是另一套体表变体）
HIDE = {f"{RM}/c0901/top.fbx": ["c0901e0000_top Part 1."]}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("npz", type=Path)
    ap.add_argument("--races", default="all", help="逗号分隔的编号，或 all（各骨架组代表）/ base（中原系男五族）")
    ap.add_argument("--neck", type=float, default=1.0)
    ap.add_argument("--pbd", help="IVCS 的 human.pbd（缺省取环境变量 POSEGRAPH_PBD）")
    ap.add_argument("-o", "--output", type=Path)
    args = ap.parse_args()

    import numpy as np
    from scipy.spatial.transform import Rotation as R

    from posegraph import tuning
    from posegraph.retarget.ffxiv import forward_kinematics, load_ffxiv_skeleton, retarget
    from posegraph.retarget.pbd import bone_matrix, load_ivcs_pbd
    from posegraph.retarget.races import RACES

    races = GROUPS.get(args.races, args.races).split(",")
    z = dict(np.load(args.npz))
    size = tuple(int(v) for v in z["image_size"])
    deformers = load_ivcs_pbd(args.pbd)
    out = {"source": args.npz.name, "races": []}
    for race in races:
        rt = retarget(z["joint_global_rots"], z["joint_coords"], z["rest_joint_coords"], z["rest_joint_rots"], race=race,
                      vertices_cam=z["vertices"], photo=(float(z["focal_length"]), z["cam_t"], size), neck_scale=args.neck,
                      rest_vertices=z.get("rest_vertices"))
        sk = load_ffxiv_skeleton(race)
        G_bind, P_bind = forward_kinematics(sk)
        ffxiv = {n: {"position": rt.all_positions[n].tolist(), "bind_position": P_bind[i].tolist(),
                     "delta": (R.from_quat(rt.all_rotations[n]) * G_bind[i].inv()).as_quat().tolist()}
                 for i, n in enumerate(sk.names)}
        parents = {n: (sk.names[p] if p >= 0 else None) for n, p in zip(sk.names, sk.parents)}
        parts, deform = {}, {}
        for part, (path, src) in MESHES[race].items():
            parts[part] = {"path": str((ROOT / path).resolve()), "deform_from": src, "hide": HIDE.get(path, [])}
            if src is not None and str(src) not in deform:
                target = int(race[1:])
                deform[str(src)] = {n: bone_matrix(deformers, src, target, n, parents).tolist() for n in sk.names}
        warnings, _ = tuning.check(rt, race)
        out["races"].append({
            "race": race, "name": RACES[race].name,
            "skeleton_json": str((ROOT / "src/posegraph/retarget/data" / f"ffxiv_{race}.json").resolve()),
            "ffxiv": ffxiv, "parts": parts, "deform": deform, "compensation": rt.hand_compensation,
            "warnings": [f"{w['bone']} {w['value']}° {w['message']}" for w in warnings],
        })
        print(f"{race} {RACES[race].name}: 补偿 {rt.hand_compensation}；提示 {len(warnings)} 条")
    tag = "" if args.races == "all" else f".{args.races}" if args.races in GROUPS else ".custom"
    dst = args.output or ROOT / RM / f"{args.npz.stem}{tag}.races.json"
    dst.write_text(json.dumps(out), encoding="utf-8")
    print(f"已写入 {dst}")


if __name__ == "__main__":
    main()
