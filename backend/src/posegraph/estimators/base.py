"""姿态估计器的统一接口。换模型时只需新增一个实现，下游重定向/相机模块不用改。"""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field

import numpy as np


@dataclass
class HumanEstimate:
    """单个人物的估计结果（相机坐标系，单位米）。"""

    bbox: np.ndarray                      # (4,) xyxy，原图像素
    joint_coords: np.ndarray              # (J, 3) 骨架关节位置
    joint_global_rots: np.ndarray         # (J, 3, 3) 关节全局旋转矩阵
    keypoints_2d: np.ndarray              # (K, 2) 投影回原图的关键点
    keypoints_3d: np.ndarray              # (K, 3)
    focal_length: float                   # 像素焦距
    cam_t: np.ndarray                     # (3,) 人物相对相机的平移
    # 该人物体型下的零姿态骨架（模型系，非相机系），重定向的静止参考
    rest_joint_coords: np.ndarray | None = None   # (J, 3)
    rest_joint_rots: np.ndarray | None = None     # (J, 3, 3)
    extra: dict = field(default_factory=dict)  # 模型特有的原始输出


@dataclass
class EstimateResult:
    image_size: tuple[int, int]           # (width, height)
    humans: list[HumanEstimate]
    timings_ms: dict[str, float] = field(default_factory=dict)
    device: str = ""                      # 实际推理设备（cuda / cpu）


class PoseEstimator(ABC):
    name: str = "base"

    @abstractmethod
    def load(self) -> None:
        """加载模型到显存。"""

    @abstractmethod
    def estimate(self, image_rgb: np.ndarray, bboxes: np.ndarray | None = None,
                 masks: np.ndarray | None = None, focal: float | None = None) -> EstimateResult:
        """对一张 RGB 图片做估计。bboxes 为空时按整图处理；masks：每个框一张原图尺寸的 0/1 遮罩（人物遮罩提示）；
        focal：像素焦距（深度模型估的），None 时按图片尺寸假设。"""

    def faces(self) -> np.ndarray | None:
        """网格的三角形（画人物遮罩用）；不知道时为 None。"""
        return None

    def unload(self) -> None:
        """释放显存（游戏同时运行时，空闲可卸载）。"""
