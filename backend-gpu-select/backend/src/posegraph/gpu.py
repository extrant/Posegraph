"""显卡状态（nvidia-smi）。不初始化 CUDA：在 CPU 模式或模型未加载时调用 torch.cuda 会建出几百 MB 的 CUDA 上下文。

双显卡扩展：list_gpus() 枚举全部物理 N 卡；query_gpu(gpu_id=...) 可指定按物理 index 或 UUID 取某张卡的显存，
其余逻辑与原先一致（gpu_id 为空取第一张，对应 cuda:0 / 默认 CUDA_VISIBLE_DEVICES）。
"""

from __future__ import annotations

import subprocess
import time

_cache: tuple[float, list | None] = (0.0, None)


def list_gpus(max_age: float = 2.0) -> list[dict] | None:
    """返回所有物理 N 卡的列表（按 nvidia-smi index 顺序）：
    {index, uuid, name, totalMB, usedMB, freeMB}；没有 N 卡或驱动时返回 None。结果缓存 max_age 秒。"""
    global _cache
    if time.monotonic() - _cache[0] < max_age:
        return _cache[1]
    gpus: list[dict] | None = None
    try:
        out = subprocess.run(
            ["nvidia-smi", "--query-gpu=index,uuid,name,memory.total,memory.used,memory.free",
             "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=5, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        ).stdout.strip().splitlines()
        result: list[dict] = []
        for line in out:
            parts = [s.strip() for s in line.split(",")]
            if len(parts) != 6:
                continue
            idx, uuid, name, total, used, free = parts
            result.append({"index": idx, "uuid": uuid, "name": name,
                           "totalMB": int(total), "usedMB": int(used), "freeMB": int(free)})
        gpus = result or None
    except (OSError, ValueError, subprocess.SubprocessError):
        gpus = None
    _cache = (time.monotonic(), gpus)
    return gpus


def query_gpu(max_age: float = 2.0, gpu_id: str | None = None) -> dict | None:
    """{name, index, uuid, totalMB, usedMB, freeMB}；没有 N 卡或驱动时返回 None。
    gpu_id 为空取第一张（对应 cuda:0 / 默认 CUDA_VISIBLE_DEVICES）；否则按物理 index 或 UUID 取指定卡。
    结果缓存 max_age 秒。"""
    gpus = list_gpus(max_age)
    if not gpus:
        return None
    if gpu_id:
        for g in gpus:
            if g["index"] == str(gpu_id) or g["uuid"].lower() == str(gpu_id).lower():
                return g
        return None
    return gpus[0]
