"""合并模式的站位复刻：一张图里的几个人共用同一个照片镜头。

以基准人物为准，把照片镜头系换算到游戏世界（与 camera.photo_camera_in_world 同一套换算：重力校正、两套骨架髋部对齐、
体型比例、角色模型的世界变换），再算出其他人的角色应该站在哪、朝哪：
- 位置：这个人的髋部在镜头系里的位置（joint_coords + cam_t）经上面的换算落到世界里，角色的位置让它自己重定向后的髋部正好在那；
  高度也照搬（不强制同一地面）
- 朝向：重定向后的姿势相对镜头定义，角色朝向 = 基准角色朝向 × 两人重力校正之差，只保留水平转角（角色不会歪着站）
之后用基准人物复刻镜头，所有人都在画面里对应的位置。

framing_errors：把摆好的每个人的 FF14 骨架用照片镜头投影回图片，和这个人在图里的关节比（不用进游戏就能检查）。
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from scipy.spatial.transform import Rotation as R

from .camera import _FLIP, _FRAMING_PAIRS, CameraTarget, photo_camera_in_world
from .retarget.brio_pose import BrioPose


@dataclass
class ModelTransform:
    position: np.ndarray            # 世界坐标
    rotation: R
    scale: np.ndarray

    @staticmethod
    def from_dict(d: dict) -> "ModelTransform":
        """插件用 Brio 的 GetModelTransform 读到的变换（和移动角色用的 SetModelTransform 同一套数值）。"""
        return ModelTransform(np.asarray(d["position"], float), R.from_quat(d["rotation"]), np.asarray(d.get("scale", [1, 1, 1]), float))

    @staticmethod
    def of(pose: BrioPose | None) -> "ModelTransform":
        """Brio 姿势 JSON 的 ModelAbsoluteValues；没有游戏姿势时为原点（只算相对位置）。"""
        m = pose.raw.get("ModelAbsoluteValues") if pose is not None else None
        if not m:
            return ModelTransform(np.zeros(3), R.identity(), np.ones(3))
        vec = lambda s: np.array([float(x) for x in s.split(",")])
        return ModelTransform(vec(m["Position"]), R.from_quat(vec(m["Rotation"])), vec(m["Scale"]))


@dataclass
class Person:
    human: dict                     # 会话里的识别结果
    rt: object                      # 重定向结果（需含 all_positions / upright_rotation）
    model: ModelTransform           # 这个人对应角色现在的模型变换（缩放沿用）


def _offset(rt) -> np.ndarray:
    """调整页的整体位移（模型系，米）。"""
    off = getattr(rt, "root_offset", None)
    return np.zeros(3) if off is None else np.asarray(off, dtype=float)


def _upright(rt) -> R:
    return R.from_quat(rt.upright_rotation) if rt.upright_rotation is not None else R.identity()


def _hips(human: dict, rt, mhr_names: list[str]) -> tuple[np.ndarray, np.ndarray, np.ndarray, float]:
    """(MHR 髋部镜头系坐标, MHR 髋部模型系坐标, FF14 髋部模型系坐标, FF14 / MHR 体型比例)。"""
    up = _upright(rt)
    joints = np.asarray(human["joint_coords"])
    J = lambda n: joints[mhr_names.index(n)]
    hip_cam = (J("l_upleg") + J("r_upleg")) / 2
    model = lambda n: up.apply(J(n) * _FLIP)
    mhr_hip = (model("l_upleg") + model("r_upleg")) / 2
    P = rt.all_positions
    ff_hip = (P["j_asi_a_l"] + P["j_asi_a_r"]) / 2 - _offset(rt)   # 手动整体位移不算：摆站位后它还在
    scale = np.linalg.norm(P["j_kubi"] - ff_hip - _offset(rt)) / np.linalg.norm(model("c_neck") - mhr_hip)
    return hip_cam, mhr_hip, ff_hip, float(scale)


def _yaw(rotation: R) -> R:
    """只保留绕竖直轴的转动（角色不能歪着站）。"""
    forward = rotation.apply([0.0, 0.0, 1.0])
    return R.from_euler("y", np.arctan2(forward[0], forward[2]))


def _gravity(people: list[Person]) -> R:
    return R.concatenate([_upright(p.rt) for p in people]).mean()


def scene_rotation(people: list[Person], base: int) -> R:
    """场景空间 → 世界的旋转。场景空间：x 照片右、y 上、z 朝向镜头（前）；微调沿这三个方向。"""
    return people[base].model.rotation * _yaw(_gravity(people) * _upright(people[base].rt).inv()).inv()


def _scene(people: list[Person], base: int, mhr_names: list[str]):
    """(各人髋部信息, 各人髋部的场景坐标, 场景 → 世界的旋转, 场景原点的世界坐标)。场景原点就是照片镜头的位置。"""
    hips = [_hips(p.human, p.rt, mhr_names) for p in people]
    scale = float(np.mean([h[3] for h in hips]))
    gravity = _gravity(people)
    # 场景空间：镜头系翻成 y 向上、去掉重力倾斜、按 FF14 体型缩放；每个人的髋部在场景里的位置
    scene_hips = [scale * gravity.apply((h[0] + np.asarray(p.human["cam_t"])) * _FLIP) for p, h in zip(people, hips)]
    b = people[base]
    rotation = scene_rotation(people, base)
    origin = b.model.position + b.model.rotation.apply(b.model.scale * hips[base][2]) - rotation.apply(scene_hips[base])
    return hips, scene_hips, rotation, origin


def place(people: list[Person], base: int, mhr_names: list[str]) -> list[ModelTransform]:
    """
    每个人的角色应有的模型变换（基准人物的不变）。
    整个场景用同一个体型比例（各人的平均）和同一个重力方向（各人重力校正的平均）从镜头系换算出来，
    基准人物只用来把场景放到世界里：换哪个人当基准、反复应用，其他人的位置都不变
    （按基准人物自己的比例和校正换算的话，换基准来回几次就会越来越高 / 越来越偏）。
    """
    hips, scene_hips, world_rotation, world_origin = _scene(people, base, mhr_names)
    gravity = _gravity(people)
    # 每个人自己的模型系 → 场景空间：差一个重力校正，只保留水平转角
    to_scene = [_yaw(gravity * _upright(p.rt).inv()) for p in people]
    b = people[base]
    out = []
    for i, p in enumerate(people):
        if i == base:
            out.append(b.model)
            continue
        rotation = world_rotation * to_scene[i]
        hip = world_origin + world_rotation.apply(scene_hips[i])
        out.append(ModelTransform(hip - rotation.apply(p.model.scale * hips[i][2]), rotation, p.model.scale))
    return out


_GROUND_EXCLUDE = ("j_sk_", "j_kami", "j_f_", "j_ex_")   # 裙摆、头发、表情、装备骨不算身体最低点（同 retarget 的贴地）


def _lowest(person: Person, model: ModelTransform) -> float:
    """这个人摆好后身体最低点的世界高度（不算调整页的整体位移：手动抬高 / 放低的不会被放回地面）。"""
    P = person.rt.all_positions
    off = _offset(person.rt)
    return float(min((model.position + model.rotation.apply(model.scale * (v - off)))[1]
                     for n, v in P.items() if n.startswith(("j_", "iv_asi_")) and not n.startswith(_GROUND_EXCLUDE)))


def ground(people: list[Person], placed: list[ModelTransform], base: int) -> None:
    """
    大家看得见在同一块地上（floor.scene 的 on_floor）时：其他人上下挪到最低点和基准人物的一样高。
    照片里的髋部高度按平均体型换算，FF14 各人腿长、体型不同，直接照搬会有人浮空或陷进地里。
    """
    floor = _lowest(people[base], placed[base])
    for i, (p, m) in enumerate(zip(people, placed)):
        if i != base:
            placed[i] = ModelTransform(m.position + np.array([0.0, floor - _lowest(p, m), 0.0]), m.rotation, m.scale)


def nudge(people: list[Person], placed: list[ModelTransform], base: int, offsets: list) -> None:
    """手动微调（列表里每个人的前后 / 左右 / 上下 / 转向）：offsets[i] = (右, 上, 前 米, 转向 度)，沿场景方向，基准人物不动。"""
    rotation = scene_rotation(people, base)
    for i, o in enumerate(offsets):
        if i == base or o is None or not any(o):
            continue
        x, y, z, yaw = (float(v) for v in o)
        m = placed[i]
        placed[i] = ModelTransform(m.position + rotation.apply([x, y, z]),
                                   R.from_euler("y", yaw, degrees=True) * m.rotation, m.scale)


def framing_errors(people: list[Person], placed: list[ModelTransform], base: int, image_size,
                   mhr_names: list[str]) -> list[float]:
    """摆好后每个人的 FF14 关节经照片镜头投影回图片，与图里这个人对应关节的平均偏差（占画面高度的比例）。"""
    b = people[base]
    target: CameraTarget = photo_camera_in_world(b.human, b.rt, b.model.position, b.model.rotation.as_quat(),
                                                 b.model.scale, mhr_names)
    w, h = (int(v) for v in image_size)
    f = h / 2 / np.tan(np.radians(target.fov_y_deg) / 2)
    right = np.cross(target.forward, target.up)
    errors = []
    for p, m in zip(people, placed):
        joints = np.asarray(p.human["joint_coords"]) + np.asarray(p.human["cam_t"])
        diffs = []
        for ff, mh in _FRAMING_PAIRS:
            world = m.position + m.rotation.apply(m.scale * p.rt.all_positions[ff]) - target.position
            depth = world @ target.forward
            game = np.array([f * (world @ right) / depth, -f * (world @ target.up) / depth])
            q = joints[mhr_names.index(mh)]
            focal = float(p.human["focal_length"])
            photo = np.array([focal * q[0] / q[2], focal * q[1] / q[2]])   # 镜头系：x 右、y 下、z 向前，原点在画面中心
            if abs(photo[0]) > w / 2 or abs(photo[1]) > h / 2:
                continue   # 画面外的关节是模型猜的
            diffs.append(np.linalg.norm(game - photo) / h)
        errors.append(float(np.mean(diffs)) if diffs else float("nan"))
    return errors
