"""
多人场景的重力与地面：同一张照片里的人共用一个镜头和一块地面。

合并模式识别时深度模型找过地面（depth.find_floor，结果记在每个人的 "floor"：相机系法线 xyz、d、大家是否都在地上）：
- 重力：地面法线就是「上」，所有人按它摆正（俯拍、仰拍的照片也能摆正）；深度没找到地面时多人谁都不转，单人按站姿估算
- 都在地上时，复刻站位把每个人的最低点放到同一个地面高度（placement.ground）——FF14 各人腿长、体型不同，
  照搬照片里的高度会有人浮空或陷进地里
歪了的话在调整页用「整体旋转」或点对应的骨骼手动修。
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from scipy.spatial.transform import Rotation as R

_FLIP = np.array([1.0, -1.0, -1.0])   # 相机系 ↔ MHR 模型系（y 向上、z 朝镜头）


@dataclass(frozen=True)
class Scene:
    upright: R | None     # 所有人共用的重力校正；None 为按单人估算（单人会话）
    on_floor: bool        # 大家都在深度找到的地面上：复刻站位时最低点放到同一高度


def scene(humans: list[dict]) -> Scene:
    """有深度找到的地面时按地面摆正（单人、多人都一样）。没有地面时：多人谁都不转，单人交给站姿估算（Scene(None, False)）。"""
    floor = humans[0].get("floor") if humans else None
    if floor is None or len(floor) < 5:
        return Scene(None if len(humans) < 2 else R.identity(), False)
    up = np.asarray(floor[:3], dtype=float) * _FLIP          # 地面法线（相机系，朝上）→ 模型系
    rotation = R.align_vectors([[0.0, 1.0, 0.0]], [up])[0]
    return Scene(rotation, bool(floor[4] > 0.5))
