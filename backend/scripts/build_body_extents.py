"""生成 src/posegraph/retarget/data/body_extents.json：各种族头 / 胸 / 髋在绑定姿势下的尺寸（手部末端补偿用）。

    .venv/Scripts/python scripts/build_body_extents.py <bind_points.json>

bind_points.json 在 Blender 对比场景（scripts/blender_race_compare.py 搭建）里导出：把各种族骨架切到 REST，
取身体（top/dwn）与头模（fac）顶点的模型空间坐标，以及 j_kao / j_kubi / j_sebo_c / j_asi_a / j_ude_a 的绑定位置。
同一骨架组的种族网格不同（如高地男更壮），所以每个种族单独量。
女性另带 "bust": {"1.5": 身体顶点}：j_mune_l/r 缩放 1.5 倍时的身体，用来求胸厚随胸骨缩放的斜率。
女性用游戏原版身体（2026-10-26，与 PR #1 作者在游戏内导出的中原女尺寸相差 2mm 以内）。
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

ROOT = Path(os.path.abspath(__file__)).parents[1]
sys.path.insert(0, str(ROOT / "src"))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("points", type=Path)
    args = ap.parse_args()

    import numpy as np

    from posegraph.retarget.body import DATA, measure

    src = json.loads(args.points.read_text(encoding="utf-8"))
    out = {}
    for race, d in src.items():
        b = {k: np.array(v) for k, v in d["bind"].items()}
        J = {"head": b["j_kao"], "neck": b["j_kubi"], "chest": b["j_sebo_c"], "hip": (b["j_asi_a_l"] + b["j_asi_a_r"]) / 2,
             "shoulder_l": b["j_ude_a_l"], "shoulder_r": b["j_ude_a_r"], "upleg_l": b["j_asi_a_l"], "upleg_r": b["j_asi_a_r"]}
        m = measure(np.array(d["body"]), np.array(d["head"]), J)
        out[race] = {part: {k: round(float(v), 4) for k, v in vals.items()} for part, vals in m.items()}
        # 女性：胸骨 j_mune 放大 0.5 倍时胸前表面前移多少（胸围滑条 / Customize+ 改的就是它的缩放）。
        # 胸厚与中心偏移各按这个斜率随缩放变化，见 body.chest_for_bust
        if "1.5" in d.get("bust", {}):
            big = measure(np.array(d["bust"]["1.5"]), np.array(d["head"]), J)["chest"]
            out[race]["chest"]["bust_slope"] = round(float(big["half_z"] - m["chest"]["half_z"]) / 0.5, 4)
        print(race, out[race])
    DATA.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"已写入 {DATA}")


if __name__ == "__main__":
    main()
