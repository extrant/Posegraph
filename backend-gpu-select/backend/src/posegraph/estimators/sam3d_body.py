"""Meta SAM 3D Body 封装。

上游仓库 clone 在 third_party/sam-3d-body（不改动它），这里只做：
- 从本地目录加载权重（不依赖 detectron2 / MoGe / SAM2 等可选组件）
- 设备选择：GPU / CPU / 自动（按可用显存）。上游推理代码写死了 CUDA，CPU 模式在推理期间临时重定向
- 显存优化：身体头/手部头共用一份 MHR；GPU 模式下 MHR 留在 CPU（约省 660MB）
- 骨干网络精度控制：上游配置默认 bf16（TRAIN.USE_FP16 + FP16_TYPE=bfloat16），可切 fp16 / fp32
- 把输出整理成 HumanEstimate
"""

from __future__ import annotations

import io
import os
import sys
import time
from contextlib import contextmanager, nullcontext, redirect_stderr, redirect_stdout
from pathlib import Path
from typing import Literal

import numpy as np

from ..i18n import tr
from .base import EstimateResult, HumanEstimate, PoseEstimator

Precision = Literal["auto", "bf16", "fp16", "fp32"]
Device = Literal["auto", "cuda", "cpu"]


@contextmanager
def _quiet():
    """屏蔽上游的 print 与警告输出。"""
    import warnings

    with open(os.devnull, "w", encoding="utf-8") as devnull, redirect_stdout(devnull), redirect_stderr(devnull), \
            warnings.catch_warnings():
        warnings.simplefilter("ignore")
        yield


@contextmanager
def _jit_load_via_python_io(map_location: str | None = None, reuse: bool = False):
    """Windows 上 torch.jit.load 走 C fopen，打不开含中文的路径；改为先用 Python 读成字节流。
    map_location 非空时强制加载到该设备（先在 CPU 上组装模型，避免加载峰值占显存）；
    reuse：同一文件只加载一次（身体头/手部头各加载一份相同的 MHR，约 660MB，共用时没必要读两遍）。"""
    import torch

    original = torch.jit.load
    cache: dict[Path, object] = {}

    def patched(f, *args, **kwargs):
        key = Path(f) if isinstance(f, (str, Path)) else None
        if reuse and key in cache:
            return cache[key]
        if key is not None:
            f = io.BytesIO(key.read_bytes())
        if map_location is not None:
            kwargs["map_location"] = map_location
        module = original(f, *args, **kwargs)
        if reuse and key is not None:
            cache[key] = module
        return module

    torch.jit.load = patched
    try:
        yield
    finally:
        torch.jit.load = original


@contextmanager
def _cpu_redirect():
    """上游推理代码把 CUDA 写死了（recursive_to(batch, "cuda")、若干 .cuda()），CPU 推理期间临时改为留在 CPU。"""
    import torch
    import sam_3d_body.models.meta_arch.sam3d_body as meta
    import sam_3d_body.sam_3d_body_estimator as est

    originals = [(m, m.recursive_to) for m in (meta, est)]
    tensor_cuda = torch.Tensor.cuda
    for m, fn in originals:
        m.recursive_to = (lambda f: lambda x, device, *a, **k: f(x, "cpu" if device == "cuda" else device, *a, **k))(fn)
    torch.Tensor.cuda = lambda self, *a, **k: self
    try:
        yield
    finally:
        for m, fn in originals:
            m.recursive_to = fn
        torch.Tensor.cuda = tensor_cuda


def _cpu_mhr(scripted):
    """把 MHR（TorchScript，约 660MB）留在 CPU：输入搬到 CPU，输出搬回调用方设备。
    MHR 只做蒙皮/前向运动学，每张图调用几次。"""
    import torch

    class CpuMhr(torch.nn.Module):
        def __init__(self):
            super().__init__()
            # 不注册为子模块：model.to("cuda") 时不会被搬进显存
            object.__setattr__(self, "inner", scripted.cpu())

        def forward(self, *args):
            device = next(a.device for a in args if torch.is_tensor(a))
            out = self.inner(*(a.cpu() if torch.is_tensor(a) else a for a in args))
            return tuple(o.to(device) for o in out)

        def parameters(self, recurse: bool = True):
            return self.inner.parameters(recurse)

    return CpuMhr()


class Sam3dBodyEstimator(PoseEstimator):
    """device：auto 时按 nvidia-smi 的可用显存决定（低于 min_free_vram_mb 用 CPU），每次加载时重新判断；
    precision：auto 时 GPU 用 bf16（Ampere 及以上；更老架构如 Turing 无 bf16 硬件则退回 fp16）、CPU 用 fp32；
    mhr_on_cpu：GPU 模式下把 MHR 留在 CPU。"""

    name = "sam3d_body"

    def __init__(self, repo_dir: Path, checkpoint_dir: Path, device: Device = "auto", precision: Precision = "auto",
                 share_mhr: bool = True, mhr_on_cpu: bool = True, min_free_vram_mb: int = 3000, cuda_device: str = ""):
        self.repo_dir = Path(repo_dir)
        self.checkpoint_dir = Path(checkpoint_dir)
        self.device = device
        self.precision = precision
        self.share_mhr = share_mhr
        self.mhr_on_cpu = mhr_on_cpu
        self.min_free_vram_mb = min_free_vram_mb
        # 双显卡：指定的物理 N 卡（index 或 UUID）；空为不指定（用默认 cuda:0）
        self.cuda_device = cuda_device
        # 加载后实际使用的设备 / 精度，以及选择原因
        self.active_device: str | None = None
        self.active_precision: str | None = None
        self.device_reason: tuple = ("",)   # (中文模板, 参数)，返回给界面时按语言翻译（i18n.tr）
        self._estimator = None

    @property
    def loaded(self) -> bool:
        return self._estimator is not None

    def _pick_device(self) -> str:
        # 双显卡：用户显式指定了物理卡，优先用它（即便显存偏紧），除非强制 CPU
        spec = (self.cuda_device or "").strip()
        if spec:
            from ..gpu import query_gpu
            g = query_gpu(max_age=0, gpu_id=spec)
            if g is None:
                self.device_reason = ("找不到指定的显卡「{spec}」，已退回 CPU", {"spec": spec})
                return "cpu"
            if self.device == "cpu":
                self.device_reason = ("手动指定",)
                return "cpu"
            self.device_reason = ("指定显卡：{name}", {"name": g["name"]})
            return "cuda"
        # 未指定物理卡：沿用原逻辑（auto 按第一张卡显存决定，cuda/cpu 直接返回）
        if self.device != "auto":
            self.device_reason = ("手动指定",)
            return self.device
        from ..gpu import query_gpu

        gpu = query_gpu(max_age=0)
        if gpu is None:
            self.device_reason = ("未检测到 NVIDIA 显卡",)
            return "cpu"
        if gpu["freeMB"] < self.min_free_vram_mb:
            self.device_reason = ("可用显存 {free} MB，低于 {need} MB", {"free": gpu["freeMB"], "need": self.min_free_vram_mb})
            return "cpu"
        self.device_reason = ("可用显存 {free} MB", {"free": gpu["freeMB"]})
        return "cuda"

    def load(self) -> None:
        if self._estimator is not None:
            return
        if not self.repo_dir.exists():
            raise FileNotFoundError(tr("找不到 SAM 3D Body 源码：{path}，请先运行 scripts/setup_sam3d.ps1", path=self.repo_dir))
        ckpt = self.checkpoint_dir / "model.ckpt"
        mhr = self.checkpoint_dir / "assets" / "mhr_model.pt"
        if not ckpt.exists() or not mhr.exists():
            raise FileNotFoundError(tr("找不到权重：{ckpt} / {mhr}，请先运行 scripts/setup_sam3d.ps1", ckpt=ckpt, mhr=mhr))

        if str(self.repo_dir) not in sys.path:
            sys.path.insert(0, str(self.repo_dir))
        import torch
        import logging

        logging.getLogger("torch.utils.flop_counter").setLevel(logging.ERROR)  # "triton not found"，与推理无关
        with _quiet():  # 导入时有 triton / Momentum 等无关警告
            from sam_3d_body import SAM3DBodyEstimator

        device = self._pick_device()
        if self.precision != "auto":
            precision = self.precision
        elif device == "cpu":
            precision = "fp32"
        else:
            # bf16 需要 Ampere 及以上（compute capability >= 8.0）；更老架构（Turing/Pascal 等）
            # 没有 bf16 硬件，硬上会极慢或报错，退回 fp16（Turing 有 fp16 Tensor Core，能正常跑）。
            _cap = torch.cuda.get_device_capability(0)
            precision = "bf16" if _cap[0] >= 8 else "fp16"
        try:
            model, cfg = self._build(ckpt, mhr, device, precision)
        except torch.OutOfMemoryError:
            # 与游戏共用显卡时偶发瞬时显存不足（报错时实际仍有大量空闲），清理后重试一次
            torch.cuda.empty_cache()
            time.sleep(2)
            model, cfg = self._build(ckpt, mhr, device, precision)
        with _quiet():
            self._estimator = SAM3DBodyEstimator(sam_3d_body_model=model, model_cfg=cfg)
        self.active_device, self.active_precision = device, precision

    def _build(self, ckpt: Path, mhr: Path, device: str, precision: str):
        """同上游 build_models.load_sam_3d_body，区别：在 CPU 上组装好（共用 / 外置 MHR、设精度）再整体搬到目标设备，
        重复的那份 MHR 不会先进显存。"""
        import torch
        from sam_3d_body.models.meta_arch import SAM3DBody
        from sam_3d_body.utils.checkpoint import load_state_dict
        from sam_3d_body.utils.config import get_config

        cfg_path = ckpt.parent / "model_config.yaml"
        if not cfg_path.exists():
            cfg_path = ckpt.parent.parent / "model_config.yaml"
        # 上游加载时会打印上百行 missing keys（MHR 部分本就由 TorchScript 单独加载，属正常），静默掉
        with _jit_load_via_python_io("cpu", reuse=self.share_mhr), _quiet():
            cfg = get_config(str(cfg_path))
            cfg.defrost()
            cfg.MODEL.MHR_HEAD.MHR_MODEL_PATH = str(mhr)
            cfg.freeze()
            model = SAM3DBody(cfg)
            # 内存映射读取：不把 2.5GB 权重整个读进内存再复制一份（与游戏同跑时内存峰值会翻倍）
            state = torch.load(str(ckpt), map_location="cpu", weights_only=False, mmap=True)
            load_state_dict(model, state.get("state_dict", state), strict=False)
            del state

        # 上游配置构造时已把骨干转成 bf16；forward 按 backbone_dtype 转换输入、再把骨干输出转回 fp32，其余头部始终 fp32
        if precision == "fp16":
            model._set_fp16(model.backbone, torch.float16)
            model.backbone_dtype = torch.float16
        elif precision == "fp32":
            model.backbone.float()
            model.backbone_dtype = torch.float32
        if self.share_mhr:
            # 身体头和手部头各自从同一文件加载了一份冻结的 MHR（约 660MB），只用于无状态前向，共用一份即可
            model.head_pose_hand.mhr = model.head_pose.mhr
        if device == "cuda" and self.mhr_on_cpu:
            body = _cpu_mhr(model.head_pose.mhr)
            model.head_pose.mhr = body
            model.head_pose_hand.mhr = body if self.share_mhr else _cpu_mhr(model.head_pose_hand.mhr)
        return model.to(device).eval(), cfg

    def unload(self) -> None:
        import gc

        self._estimator = None
        self.active_device = self.active_precision = None
        gc.collect()
        if "torch" in sys.modules:
            import torch

            if torch.cuda.is_initialized():
                torch.cuda.empty_cache()

    def _device_scope(self):
        return _cpu_redirect() if self.active_device == "cpu" else nullcontext()

    def estimate(self, image_rgb: np.ndarray, bboxes: np.ndarray | None = None) -> EstimateResult:
        self.load()
        h, w = image_rgb.shape[:2]
        t0 = time.perf_counter()
        with _quiet(), self._device_scope():
            outputs = self._estimator.process_one_image(image_rgb, bboxes=bboxes)
        elapsed = (time.perf_counter() - t0) * 1000

        humans = []
        for o in outputs:
            rest_coords, rest_rots, rest_verts = self._rest_pose(o["shape_params"], o["scale_params"])
            humans.append(HumanEstimate(
                bbox=np.asarray(o["bbox"], dtype=np.float32),
                joint_coords=np.asarray(o["pred_joint_coords"], dtype=np.float32),
                joint_global_rots=self._final_rotations(o),
                keypoints_2d=np.asarray(o["pred_keypoints_2d"], dtype=np.float32),
                keypoints_3d=np.asarray(o["pred_keypoints_3d"], dtype=np.float32),
                focal_length=float(np.asarray(o["focal_length"]).reshape(-1)[0]),
                cam_t=np.asarray(o["pred_cam_t"], dtype=np.float32),
                rest_joint_coords=rest_coords,
                rest_joint_rots=rest_rots,
                extra={**{k: o[k] for k in ("body_pose_params", "hand_pose_params", "shape_params", "scale_params", "global_rot") if k in o},
                       "vertices": np.asarray(o["pred_vertices"], dtype=np.float32), "faces": self._estimator.faces,
                       "rest_vertices": rest_verts},
            ))
        return EstimateResult(image_size=(w, h), humans=humans,
                              timings_ms={"inference": elapsed}, device=self.active_device or "")

    def _final_rotations(self, o: dict) -> np.ndarray:
        """用最终参数重算关节全局旋转。

        上游 run_inference 在合并手部解码器结果后重跑了 mhr_forward，更新了顶点/关键点/关节坐标，
        却没有写回 joint_global_rots（sam3d_body.py「Re-run forward」段）。触发手部精修的图片里，
        pred_global_rots 仍是精修前的值，与 pred_joint_coords 可相差 90°。
        """
        import torch

        head = self._estimator.model.head_pose
        dev = head.scale_mean.device
        t = lambda a: torch.as_tensor(np.asarray(a), dtype=torch.float32, device=dev).reshape(1, -1)
        global_rot = t(o["global_rot"])
        with torch.no_grad():
            _, coords, rots = head.mhr_forward(
                global_trans=global_rot * 0, global_rot=global_rot,
                body_pose_params=t(o["body_pose_params"]), hand_pose_params=t(o["hand_pose_params"]),
                scale_params=t(o["scale_params"]), shape_params=t(o["shape_params"]), expr_params=t(o["expr_params"]),
                return_joint_coords=True, return_joint_rotations=True,
            )
        coords = coords[0].float().cpu().numpy() * np.array([1.0, -1.0, -1.0])  # 与上游输出同为相机系
        mismatch = np.abs(coords - np.asarray(o["pred_joint_coords"])).max()
        if mismatch > 1e-3:
            raise RuntimeError(f"重算的关节坐标与上游输出不一致（{mismatch:.4f} m），上游实现可能已变化")
        return rots[0].float().cpu().numpy()

    def _rest_pose(self, shape_params: np.ndarray, scale_params: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
        """该人物体型下的零姿态（MHR 模型空间，米）：关节位置、全局旋转、网格顶点。
        关节位置随体型变化，重定向必须用它作静止参考；网格用于量头 / 胸 / 髋的尺寸（手部末端补偿）。"""
        import torch

        head = self._estimator.model.head_pose
        dev = head.scale_mean.device
        t = lambda a: torch.as_tensor(np.asarray(a), dtype=torch.float32, device=dev).reshape(1, -1)
        z = lambda n: torch.zeros(1, n, device=dev)
        with torch.no_grad():
            verts, coords, rots = head.mhr_forward(
                global_trans=z(3), global_rot=z(3), body_pose_params=z(133), hand_pose_params=None,
                scale_params=t(scale_params), shape_params=t(shape_params), expr_params=z(72),
                return_joint_coords=True, return_joint_rotations=True,
            )
        return coords[0].float().cpu().numpy(), rots[0].float().cpu().numpy(), verts[0].float().cpu().numpy()
