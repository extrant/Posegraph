"""Meta SAM 3D Body 封装。

上游仓库 clone 在 third_party/sam-3d-body（不改动它），这里只做：
- 从本地目录加载权重（不依赖 detectron2 / MoGe / SAM2 等可选组件）
- 设备选择：GPU / CPU / 自动（按可用显存）。上游推理代码写死了 CUDA，CPU 模式在推理期间临时重定向
- 显存优化：身体头/手部头共用一份 MHR；GPU 模式下 MHR 留在 CPU（约省 660MB）
- 骨干网络精度控制：上游配置默认 bf16（TRAIN.USE_FP16 + FP16_TYPE=bfloat16），可切 fp16 / fp32
- 快速模式（fast）：移植自 Fast SAM 3D Body 的提速，见 sam3d_fast.py；同时删掉 MHR 的修正形变权重，MHR 只剩约 29MB，直接放显卡
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

FACES_FILE = "mhr_faces.npy"   # 网格三角形，第一次加载模型时存到权重的 assets 目录

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
    """device：auto 时按可用显存决定（低于 min_free_vram_mb 用 CPU），每次加载时重新判断；
    precision：auto 时 GPU 用 bf16（上游默认；不支持硬件 bf16 的 ROCm 卡用 fp16）、CPU 用 fp32；
    mhr_on_cpu：GPU 模式下把 MHR 留在 CPU（快速模式下 MHR 只有约 29MB，忽略此项、放显卡）；
    fast：快速模式（sam3d_fast），加载时决定，改了要重新加载；
    backbone_scale：骨干输入缩放（低配模式 0.75），见 sam3d_fast._scaled_backbone。"""

    name = "sam3d_body"

    def __init__(self, repo_dir: Path, checkpoint_dir: Path, device: Device = "auto", precision: Precision = "auto",
                 share_mhr: bool = True, mhr_on_cpu: bool = True, min_free_vram_mb: int = 3000, fast: bool = False,
                 backbone_scale: float = 1.0):
        self.repo_dir = Path(repo_dir)
        self.checkpoint_dir = Path(checkpoint_dir)
        self.device = device
        self.precision = precision
        self.share_mhr = share_mhr
        self.mhr_on_cpu = mhr_on_cpu
        self.min_free_vram_mb = min_free_vram_mb
        self.fast = fast
        self.backbone_scale = backbone_scale
        # 加载后实际使用的设备 / 精度 / 是否快速模式，以及选择原因
        self.active_device: str | None = None
        self.active_precision: str | None = None
        self.active_fast: bool | None = None
        self.device_reason: tuple = ("",)   # (中文模板, 参数)，返回给界面时按语言翻译（i18n.tr）
        self._estimator = None

    @property
    def loaded(self) -> bool:
        return self._estimator is not None

    def _pick_device(self) -> str:
        if self.device != "auto":
            self.device_reason = ("手动指定",)
            return self.device
        import torch
        from ..gpu import query_gpu_vendor

        has_hip = bool(getattr(torch.version, "hip", None))
        has_cuda = bool(getattr(torch.version, "cuda", None))
        if not has_cuda and not has_hip:
            self.device_reason = ("PyTorch 未包含 GPU 支持",)
            return "cpu"

        gpu, vendor = query_gpu_vendor(max_age=0)
        if gpu is None:
            self.device_reason = ("未检测到可用独立显卡",)
            return "cpu"
        # 在调用 torch.cuda.is_available() 前先核对构建后端与显卡厂商，不匹配时不建上下文
        if has_hip and vendor != "amd":
            self.device_reason = ("未检测到 AMD 显卡",)
            return "cpu"
        if has_cuda and not has_hip and vendor != "nvidia":
            self.device_reason = ("未检测到 NVIDIA 显卡",)
            return "cpu"
        if gpu["freeMB"] < self.min_free_vram_mb:
            self.device_reason = ("可用显存 {free} MB，低于 {need} MB", {"free": gpu["freeMB"], "need": self.min_free_vram_mb})
            return "cpu"
        if not torch.cuda.is_available():
            self.device_reason = ("当前 PyTorch 环境不支持此显卡",)
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
        if device == "cuda" and sys.platform == "win32" and getattr(torch.version, "hip", None) and hasattr(torch, "backends"):
            # Windows ROCm MIOpen 的文件锁（ufdb.txt.lock.fslock）在部分环境或非 ASCII 用户目录下会报错并导致退出卡死；
            # 模型仅含少量 patchify 卷积，关闭 MIOpen 后直接走 ATen 原生 im2col + hipBLASLt
            torch.backends.cudnn.enabled = False
        if self.precision != "auto":
            precision = self.precision
        elif device != "cuda":
            precision = "fp32"
        elif getattr(torch.version, "hip", None) and not torch.cuda.is_bf16_supported():
            precision = "fp16"   # RDNA 2 等无硬件 bf16 的 ROCm 显卡改用 fp16
        else:
            precision = "bf16"
        try:
            model, cfg = self._build(ckpt, mhr, device, precision)
        except torch.OutOfMemoryError:
            # 与游戏共用显卡时偶发瞬时显存不足（报错时实际仍有大量空闲），清理后重试一次
            torch.cuda.empty_cache()
            time.sleep(2)
            model, cfg = self._build(ckpt, mhr, device, precision)
        with _quiet():
            self._estimator = SAM3DBodyEstimator(sam_3d_body_model=model, model_cfg=cfg)
        self.active_device, self.active_precision, self.active_fast = device, precision, self.fast
        faces = self.checkpoint_dir / "assets" / FACES_FILE
        if not faces.exists():   # 存一份三角形：模型卸载后（或刚启动、从识别缓存恢复的会话）也能画人物遮罩
            try:
                np.save(faces, np.asarray(self._estimator.faces, dtype=np.int32))
            except OSError:
                pass

    def faces(self) -> np.ndarray | None:
        if self._estimator is not None:
            return np.asarray(self._estimator.faces, dtype=np.int32)
        path = self.checkpoint_dir / "assets" / FACES_FILE
        return np.load(path) if path.exists() else None

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
        if self.fast:
            from . import sam3d_fast

            for mhr in {id(m): m for m in (model.head_pose.mhr, model.head_pose_hand.mhr)}.values():
                sam3d_fast.strip_correctives(mhr)
        elif device == "cuda" and self.mhr_on_cpu:
            body = _cpu_mhr(model.head_pose.mhr)
            model.head_pose.mhr = body
            model.head_pose_hand.mhr = body if self.share_mhr else _cpu_mhr(model.head_pose_hand.mhr)
        model = model.to(device).eval()
        if self.fast or self.backbone_scale != 1.0:
            import dataclasses
            from . import sam3d_fast

            base = sam3d_fast.FastOptions() if self.fast else sam3d_fast.ORIGINAL
            sam3d_fast.apply(model, dataclasses.replace(base, backbone_scale=self.backbone_scale))
        return model, cfg

    def unload(self) -> None:
        import gc

        self._estimator = None
        self.active_device = self.active_precision = self.active_fast = None
        from .. import depth
        depth.unload()   # 深度模型跟着一起释放
        gc.collect()
        if "torch" in sys.modules:
            import torch

            if torch.cuda.is_initialized():
                torch.cuda.empty_cache()

    def _device_scope(self):
        return _cpu_redirect() if self.active_device == "cpu" else nullcontext()

    def estimate(self, image_rgb: np.ndarray, bboxes: np.ndarray | None = None,
                 masks: np.ndarray | None = None, focal: float | None = None) -> EstimateResult:
        """bboxes：人物框（N×4，原图像素）；None 时整张图当一个人。人多时按 max_people_per_batch 分批，
        相机内参对所有批次相同（focal 给了就用它，否则按图片尺寸假设），分批不影响各人的相对位置。结果顺序与 bboxes 一致。
        masks：(N, 高, 宽) 0/1，每个框的人物遮罩（看得见的像素），作为提示叠加到图像特征上。"""
        from ..config import settings

        self.load()
        h, w = image_rgb.shape[:2]
        t0 = time.perf_counter()
        chunks = [None] if bboxes is None else np.array_split(bboxes.reshape(-1, 4), max(1, -(-len(bboxes) // settings.max_people_per_batch)))
        outputs = []
        start = 0
        with _quiet(), self._device_scope():
            for chunk in chunks:
                n = 1 if chunk is None else len(chunk)
                part = None if masks is None else masks[start:start + n]
                cam_int = None
                if focal is not None:
                    from ..depth import camera_intrinsics
                    cam_int = camera_intrinsics(focal, (w, h))
                outputs += self._estimator.process_one_image(image_rgb, bboxes=chunk, masks=part, cam_int=cam_int)
                start += n
        elapsed = (time.perf_counter() - t0) * 1000
        return self._make_result(outputs, w, h, elapsed)

    def _make_result(self, outputs: list[dict], w: int, h: int, elapsed: float) -> EstimateResult:
        """Convert one request's outputs; also used by isolated shared-model views."""
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
