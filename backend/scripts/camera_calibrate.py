"""GPose 相机校准：自动把相机转一遍，记录「设置的参数 → 实际机位、视线、视角」，以及设完之后多久才稳定。
用来拟合 GPose 相机的换算公式，让复刻镜头直接算出参数，不再在屏幕上反复试（camera.py）。

    python scripts\\camera_calibrate.py [--actor N] [--quick]

准备：进 GPose，让角色站在空旷平地上（相机不要被墙、地面挡住），Posegraph 的本地 HTTP 服务开着（/posegraph on）。
运行约 2 分钟，相机会自动转动；结束（或中途 Ctrl+C）后相机恢复原样。
输出 outputs/camera/<时间>.jsonl：第一行是角色位置等信息，之后每行一次设置：参数 + 设完后每 50ms 读回的状态。
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path

ROOT = Path(os.path.abspath(__file__)).parents[1]
sys.path.insert(0, str(ROOT / "src"))

SAMPLE_EVERY = 0.05   # 读回间隔（秒）
SAMPLE_FOR = 0.6      # 每次设置后读多久（看相机有没有平滑过渡、多久稳定）


def settings(quick: bool) -> list[tuple[str, dict]]:
    """要试的参数组合：(阶段, 参数)。没写的参数用基准值。"""
    base = {"ax": 0.0, "ay": 0.0, "dist": 4.0, "px": 0.0, "py": 0.0, "roll": 0.0, "zoom": 0.0}
    step = 0.3 if quick else 0.15
    pitches = [round(-1.5 + i * step, 3) for i in range(int(3.0 / step) + 1)]
    out = []
    # 绕角色转：俯仰 × 距离 × 两个水平角（俯仰的非线性、饱和区都在这里）
    for ax in (0.0, 1.6):
        for dist in ((2.0, 6.0, 14.0) if quick else (2.0, 4.0, 8.0, 14.0)):
            for ay in pitches:
                out.append(("orbit", {**base, "ax": ax, "ay": ay, "dist": dist}))
    # 水平角一圈
    for ax in [round(-3.0 + i * 0.5, 2) for i in range(13)]:
        out.append(("yaw", {**base, "ax": ax}))
    # 视角（Zoom）
    for zoom in [round(-0.6 + i * 0.2, 2) for i in range(9)]:
        out.append(("zoom", {**base, "zoom": zoom}))
    # 原地转视线（Pan）与滚转（Roll）
    for px in (-0.3, 0.0, 0.3):
        for py in (-0.3, 0.0, 0.3):
            out.append(("pan", {**base, "px": px, "py": py}))
    for roll in (-0.4, -0.2, 0.2, 0.4):
        out.append(("roll", {**base, "roll": roll}))
    # 大跳一次再跳回：看过渡有多慢
    out += [("jump", {**base, "ax": 2.5, "ay": 0.8, "dist": 12.0}), ("jump", dict(base))]
    return out


def state(client) -> dict:
    c = client.get_camera()["camera"]
    if c is None:
        raise RuntimeError("找不到活动相机：先进 GPose")
    return {k: c[k] for k in ("angle", "pan", "roll", "distance", "fov", "zoom", "position", "lookAt", "view", "projection")}


def apply(client, p: dict) -> None:
    client.set_camera(angle=[p["ax"], p["ay"]], distance=p["dist"], pan=[p["px"], p["py"]],
                      roll=p["roll"], zoom=p["zoom"], delimit=True)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--actor", type=int, help="角色编号（不填为 GPose 当前目标）")
    ap.add_argument("--quick", action="store_true", help="少一些组合（约 1 分钟）")
    args = ap.parse_args()

    from posegraph.bridge import BridgeClient

    client = BridgeClient()
    original = state(client)
    pose = json.loads(client.get_pose(args.actor))
    head = pose["Bones"].get("j_kao") or pose["Bones"].get("j_kubi")
    out = ROOT / "outputs" / "camera" / f"{time.strftime('%Y%m%d-%H%M%S')}.jsonl"
    out.parent.mkdir(parents=True, exist_ok=True)
    todo = settings(args.quick)
    print(f"共 {len(todo)} 组，约 {len(todo) * (SAMPLE_FOR + 0.1) / 60:.1f} 分钟；相机会自动转动，结束后恢复")
    try:
        with out.open("w", encoding="utf-8") as f:
            f.write(json.dumps({"meta": {"model": pose.get("ModelAbsoluteValues"), "head": head, "original": original,
                                         "sample_every": SAMPLE_EVERY, "sample_for": SAMPLE_FOR}}, ensure_ascii=False) + "\n")
            for i, (phase, p) in enumerate(todo, 1):
                apply(client, p)
                t0 = time.monotonic()
                samples = []
                while (t := time.monotonic() - t0) < SAMPLE_FOR:
                    samples.append({"t": round(t, 3), **state(client)})
                    time.sleep(SAMPLE_EVERY)
                f.write(json.dumps({"phase": phase, "params": p, "samples": samples}) + "\n")
                if i % 20 == 0:
                    print(f"  {i}/{len(todo)}")
    finally:
        o = original
        client.set_camera(angle=o["angle"], distance=o["distance"], pan=o["pan"], roll=o["roll"], zoom=o["zoom"])
    print(f"完成 → {out}")


if __name__ == "__main__":
    main()
