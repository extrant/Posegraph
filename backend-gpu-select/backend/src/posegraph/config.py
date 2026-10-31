"""服务配置。全部可用环境变量覆盖，前缀 POSEGRAPH_。"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

# 注意：项目路径含中文。torch.jit / OpenCV 等 C 层库在 Windows 上打不开非 ASCII 路径，
# 传给它们的路径要先用 Python 读成字节（见 estimators/sam3d_body.py、bench 脚本）
SERVICE_ROOT = Path(os.path.abspath(__file__)).parents[2]


def _env(name: str, default: str) -> str:
    return os.environ.get(f"POSEGRAPH_{name}", default)


@dataclass(frozen=True)
class Settings:
    # SAM 3D Body 源码（git clone 到 third_party）与权重目录
    sam3d_repo: Path = field(default_factory=lambda: Path(_env("SAM3D_REPO", str(SERVICE_ROOT / "third_party" / "sam-3d-body"))))
    sam3d_checkpoint_dir: Path = field(default_factory=lambda: Path(_env("SAM3D_CKPT", str(SERVICE_ROOT / "checkpoints" / "sam-3d-body-vith"))))

    # 推理设备：auto（按可用显存选 GPU/CPU）/ cuda / cpu
    device: str = field(default_factory=lambda: _env("DEVICE", "auto"))
    # 骨干网络精度：auto（GPU bf16、CPU fp32）/ bf16（上游默认）/ fp16 / fp32
    precision: str = field(default_factory=lambda: _env("PRECISION", "auto"))
    # GPU 模式下把 MHR（约 660MB）留在 CPU
    mhr_on_cpu: bool = field(default_factory=lambda: _env("MHR_ON_CPU", "1") == "1")
    # auto 模式下选 GPU 所需的最低可用显存（MB）：MHR 放 CPU 时模型峰值约 2GB，另加 CUDA 上下文与余量
    min_free_vram_mb: int = field(default_factory=lambda: int(_env("MIN_FREE_VRAM_MB", "3000")))

    # 双显卡时指定的物理 N 卡：nvidia-smi 的 index（如 "0" "1"）或 GPU UUID；空为不指定（用默认 cuda:0）。
    # 插件会在启动时把它同时写进 CUDA_VISIBLE_DEVICES（把这张卡映射成唯一 cuda:0）与 POSEGRAPH_CUDA_DEVICE。
    cuda_device: str = field(default_factory=lambda: _env("CUDA_DEVICE", ""))
    # 识别完成后多少秒释放模型：0 立即释放，-1 常驻
    release_after: float = field(default_factory=lambda: float(_env("RELEASE_AFTER", "0")))

    # Posegraph 插件的本地 HTTP 地址
    bridge_url: str = field(default_factory=lambda: _env("BRIDGE_URL", "http://127.0.0.1:42429"))

    host: str = field(default_factory=lambda: _env("HOST", "127.0.0.1"))
    port: int = field(default_factory=lambda: int(_env("PORT", "42430")))
    # 每次识别另存一份 npz 到 outputs/ui（命令行脚本、Blender 对比可直接复用），开发用
    save_sessions: bool = field(default_factory=lambda: _env("SAVE_SESSIONS", "0") == "1")
    # 启动后立即在后台加载模型（插件一键启动时开启，首次识别不用等加载）
    preload: bool = field(default_factory=lambda: _env("PRELOAD", "0") == "1")


settings = Settings()

# 双显卡：把指定的物理 N 卡映射成唯一的 cuda:0（其余隐藏），再让 PyTorch 用 cuda:0。
# 关键：数值索引在 nvidia-smi 与 CUDA 中的含义可能不同（CUDA 默认 FASTEST_FIRST，多卡/eGPU 时
# 顺序可能与 nvidia-smi 相反），这里统一解析成 GPU UUID 再写入——UUID 与枚举顺序无关，永远指向
# 同一张物理卡，避免“界面显示 1650、实际跑 5060”这类错位。必须在任何 torch / CUDA 库导入之前执行。
_spec = (settings.cuda_device or os.environ.get("CUDA_VISIBLE_DEVICES") or "").strip()
if _spec:
    # 万一 UUID 解析失败、只能退回数值索引时，让数值索引与 nvidia-smi 一致。
    # 仅在明确指定了卡时设置；不指定时保持库默认（FASTEST_FIRST，自动用最强卡）。
    os.environ.setdefault("CUDA_DEVICE_ORDER", "PCI_BUS_ID")
    _target = _spec
    try:
        from .gpu import list_gpus

        for _g in (list_gpus(max_age=0) or []):
            if _g["index"] == _spec or _g["uuid"].lower() == _spec.lower():
                _target = _g["uuid"]
                break
    except Exception:
        pass  # 查不到 nvidia-smi 时保持原样
    os.environ["CUDA_VISIBLE_DEVICES"] = _target
