"""实测 SAM 3D Body 的显存占用与推理耗时。

用法（在 backend 目录）：
    .venv/Scripts/python scripts/bench_sam3d.py                      # 依次测 bf16 / fp16 / fp32
    .venv/Scripts/python scripts/bench_sam3d.py --precision bf16 --image samples/a.jpg

每种精度在独立子进程中运行，保证显存统计互不干扰。
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(os.path.abspath(__file__)).parents[1]
sys.path.insert(0, str(ROOT / "src"))


def gpu_free_mb() -> tuple[int, int]:
    from posegraph.gpu import query_gpu

    info = query_gpu(max_age=0)
    if info is None:
        return 0, 0
    return info["usedMB"], info["totalMB"]


def run_one(precision: str, image: Path, runs: int, share_mhr: bool = True) -> dict:
    import cv2
    import torch

    from posegraph.config import settings
    from posegraph.estimators.sam3d_body import Sam3dBodyEstimator

    used_before, total = gpu_free_mb()
    est = Sam3dBodyEstimator(settings.sam3d_repo, settings.sam3d_checkpoint_dir, precision=precision, share_mhr=share_mhr)

    t0 = time.perf_counter()
    est.load()
    torch.cuda.synchronize()
    load_s = time.perf_counter() - t0
    weights_mb = torch.cuda.memory_allocated() / 2**20

    import numpy as np

    # cv2.imread 不支持中文路径
    rgb = cv2.cvtColor(cv2.imdecode(np.fromfile(image, dtype=np.uint8), cv2.IMREAD_COLOR), cv2.COLOR_BGR2RGB)
    torch.cuda.reset_peak_memory_stats()
    est.estimate(rgb)  # 预热
    times = []
    for _ in range(runs):
        torch.cuda.synchronize()
        t = time.perf_counter()
        res = est.estimate(rgb)
        torch.cuda.synchronize()
        times.append((time.perf_counter() - t) * 1000)
    used_after, _ = gpu_free_mb()

    h = res.humans[0] if res.humans else None
    return {
        "precision": precision,
        "share_mhr": share_mhr,
        "load_s": round(load_s, 1),
        "weights_mb": round(weights_mb),
        "peak_allocated_mb": round(torch.cuda.max_memory_allocated() / 2**20),
        "peak_reserved_mb": round(torch.cuda.max_memory_reserved() / 2**20),
        "process_gpu_mb": used_after - used_before,
        "gpu_used_before_mb": used_before,
        "gpu_total_mb": total,
        "infer_ms_avg": round(sum(times) / len(times)),
        "humans": len(res.humans),
        "joints": int(h.joint_coords.shape[0]) if h else 0,
        "focal_px": round(h.focal_length, 1) if h else None,
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--precision", choices=["bf16", "fp16", "fp32"])
    ap.add_argument("--image", type=Path)
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--no-share-mhr", action="store_true", help="身体/手部头各用一份 MHR（上游原始行为）")
    args = ap.parse_args()

    image = args.image
    if image is None:
        from posegraph.config import settings

        image = next((settings.sam3d_repo / "notebook" / "images").glob("*.jpg"))

    if args.precision:
        try:
            print("RESULT " + json.dumps(run_one(args.precision, image, args.runs, not args.no_share_mhr), ensure_ascii=False))
        except Exception as e:  # 显存不足等
            print("RESULT " + json.dumps({"precision": args.precision, "error": f"{type(e).__name__}: {e}"[:300]}, ensure_ascii=False))
        return

    print(f"测试图片：{image}")
    for p in ("bf16", "fp16", "fp32"):
        out = subprocess.run([sys.executable, __file__, "--precision", p, "--image", str(image), "--runs", str(args.runs)] + (["--no-share-mhr"] if args.no_share_mhr else []),
                             capture_output=True, text=True, encoding="utf-8", errors="replace")
        line = next((l for l in out.stdout.splitlines() if l.startswith("RESULT ")), None)
        print(line[7:] if line else f'{{"precision": "{p}", "error": "子进程失败: {out.stderr.strip()[-300:]}"}}')


if __name__ == "__main__":
    main()
