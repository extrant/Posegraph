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
    # 人物检测模型（合并模式，见 detect.py）：yolo11m.onnx（真人）、anime_person_v1.3_s.onnx（插画）
    detector_dir: Path = field(default_factory=lambda: Path(_env("DETECTOR_DIR", str(SERVICE_ROOT / "checkpoints" / "detectors"))))
    # 面捕模型（MediaPipe Face Landmarker，见 face.py）
    face_model: Path = field(default_factory=lambda: Path(_env("FACE_MODEL", str(SERVICE_ROOT / "checkpoints" / "face" / "face_landmarker.task"))))
    # 手部关键点模型（MediaPipe Hand Landmarker，见 hands.py）：SAM 估错的手指按它重算
    hand_model: Path = field(default_factory=lambda: Path(_env("HAND_MODEL", str(SERVICE_ROOT / "checkpoints" / "hand" / "hand_landmarker.task"))))
    # 插件 /posegraph facedump 导出的游戏表情动画（见 face_rig.py）；插件启动后端时传准确路径（文档文件夹可能被移动过）
    face_dump_dir: Path = field(default_factory=lambda: Path(_env("FACE_DUMP", str(Path.home() / "Documents" / "Posegraph" / "FaceDump"))))
    # 深度模型（MoGe-2，合并模式的镜头 / 遮罩 / 远近 / 地面，见 depth.py）
    moge_model: Path = field(default_factory=lambda: Path(_env("MOGE_MODEL", str(SERVICE_ROOT / "checkpoints" / "moge" / "model.pt"))))
    # 多人识别时一次送进模型的人数上限（人多时分批，显存占用随人数增加）
    max_people_per_batch: int = field(default_factory=lambda: max(1, int(_env("MAX_PEOPLE_PER_BATCH", "4"))))

    # 推理设备：auto（按可用显存选 GPU/CPU）/ cuda / cpu
    device: str = field(default_factory=lambda: _env("DEVICE", "auto"))
    # 骨干网络精度：auto（GPU bf16、CPU fp32）/ bf16（上游默认）/ fp16 / fp32
    precision: str = field(default_factory=lambda: _env("PRECISION", "auto"))
    # GPU 模式下把 MHR（约 660MB）留在 CPU
    mhr_on_cpu: bool = field(default_factory=lambda: _env("MHR_ON_CPU", "1") == "1")
    # auto 模式下选 GPU 所需的最低可用显存（MB）：MHR 放 CPU 时模型峰值约 2GB，另加 CUDA 上下文与余量
    min_free_vram_mb: int = field(default_factory=lambda: int(_env("MIN_FREE_VRAM_MB", "3000")))
    # 识别完成后多少秒释放模型：0 立即释放，-1 常驻
    release_after: float = field(default_factory=lambda: float(_env("RELEASE_AFTER", "0")))
    # 快速识别（移植自 Fast SAM 3D Body，见 estimators/sam3d_fast.py）：合并双手、MHR 不算修正形变并放显卡
    fast: bool = field(default_factory=lambda: _env("FAST", "1") == "1")
    # 实验：骨干在缩小的图上算（0.75 = 384×288）。CPU 快约 1.7 倍，但关节平均差约 53mm，不进插件设置，见 estimators/sam3d_fast.py
    backbone_scale: float = field(default_factory=lambda: float(_env("BACKBONE_SCALE", "1")))
    # 深度步（MoGe，见 depth.py）：镜头焦距、多人按深度修遮罩和远近、找地面扶正。装了 MoGe 才生效
    use_depth: bool = field(default_factory=lambda: _env("USE_DEPTH", "1") == "1")
    # 手指精修（实验，默认关）：装了手部关键点模型时，认得出的手按它重算手指（hands.py）。2026-11-01 实测不够稳，
    # 先用 SAM 自带的手指，不准的地方用户在时间轴上手动修正；POSEGRAPH_HAND_REFINE=1 打开
    hand_refine: bool = field(default_factory=lambda: _env("HAND_REFINE", "0") == "1")
    # 批量识别并行数（实验，见 parallel_batch.py）：1 不并行；2–8 时只有插件标了 parallel 的批量请求共享一份模型并行
    recognition_parallelism: int = field(default_factory=lambda: min(8, max(1, int(_env("RECOGNITION_PARALLELISM", "1")))))
    # 总开关：设为 1 时完全不用共享模型并行（出问题时临时关闭，所有识别走原串行）
    disable_parallel: bool = field(default_factory=lambda: _env("DISABLE_PARALLEL", "0") == "1")

    # Posegraph 插件的本地 HTTP 地址
    bridge_url: str = field(default_factory=lambda: _env("BRIDGE_URL", "http://127.0.0.1:42429"))

    host: str = field(default_factory=lambda: _env("HOST", "127.0.0.1"))
    port: int = field(default_factory=lambda: int(_env("PORT", "42430")))
    # 每次识别另存一份 npz 到 outputs/ui（命令行脚本、Blender 对比可直接复用），开发用
    save_sessions: bool = field(default_factory=lambda: _env("SAVE_SESSIONS", "0") == "1")
    # 启动后立即在后台加载模型（插件一键启动时开启，首次识别不用等加载）
    preload: bool = field(default_factory=lambda: _env("PRELOAD", "0") == "1")


settings = Settings()
