"""在 Blender 中并排搭建多种族同一动作的对比场景（在 Blender 的 Python 环境里运行）。

前置：scripts/export_race_compare.py 生成 <名称>.races.json；网格 FBX 在 outputs/tbsex 与 outputs/race_models/<种族>/。
在 Blender 中：exec(open(r"<本文件路径>", encoding="utf-8").read())，再调用 build(json_path, row=0, tag="flex")。
Blender 的 FBX 导入对非 ASCII 路径不稳定，项目请放在纯英文路径下；也可设置环境变量 POSEGRAPH_ASCII_ROOT="原路径|英文目录联接"。

每个种族一个集合（race_<编号>_<tag>），沿 X 排开、不同动作沿 -Y 分排；身体与头模都重新绑定到该种族的 IVCS 骨架
（同 blender_compare_scene.py）。借用别的种族模型的部件先按 human.pbd 的形变矩阵变形（同游戏），再绑定。
"""

import json
import os

import bpy
import numpy as np
from mathutils import Matrix, Vector

_HERE = os.path.dirname(os.path.abspath(__file__))   # exec 时请在 globals 里传入 __file__
exec(open(os.path.join(_HERE, "blender_compare_scene.py"), encoding="utf-8").read())   # pose_armature / build_ivcs_armature / TO_WORLD

ASCII_ROOT = tuple(os.environ.get("POSEGRAPH_ASCII_ROOT", "|").split("|", 1))
COLORS = {"c0101": (0.95, 0.55, 0.2, 1), "c0301": (0.95, 0.75, 0.3, 1), "c0701": (0.95, 0.45, 0.6, 1),
          "c1301": (0.55, 0.6, 0.95, 1), "c1701": (0.6, 0.85, 0.75, 1), "c0501": (0.35, 0.75, 1.0, 1),
          "c0901": (0.9, 0.35, 0.35, 1), "c1501": (0.75, 0.5, 0.95, 1), "c1101": (0.45, 0.9, 0.4, 1),
          "c0201": (0.95, 0.55, 0.2, 1), "c0601": (0.35, 0.75, 1.0, 1), "c0801": (0.95, 0.45, 0.6, 1),
          "c1001": (0.9, 0.35, 0.35, 1), "c1401": (0.55, 0.6, 0.95, 1), "c1801": (0.6, 0.85, 0.75, 1),
          "c1201": (0.45, 0.9, 0.4, 1)}


def _ascii(path: str) -> str:
    return path.replace(*ASCII_ROOT) if ASCII_ROOT[0] else path


def _deform_mesh(o, mats: dict) -> None:
    """按蒙皮权重混合每根骨骼的形变矩阵（模型空间），v' = Σ wᵢ·Mᵢ·v，与游戏的种族形变一致。"""
    if o.data.shape_keys:
        o.shape_key_clear()
    to_model = np.array(TO_WORLD.inverted() @ o.matrix_world)
    n = len(o.data.vertices)
    co = np.empty(n * 3)
    o.data.vertices.foreach_get("co", co)
    cm = np.c_[co.reshape(n, 3), np.ones(n)] @ to_model.T
    group_mats = {g.index: np.array(mats[g.name]) if g.name in mats else np.eye(4) for g in o.vertex_groups}
    acc, wsum = np.zeros((n, 4)), np.zeros(n)
    for v in o.data.vertices:
        for g in v.groups:
            if g.weight > 0 and g.group in group_mats:
                acc[v.index] += g.weight * (group_mats[g.group] @ cm[v.index])
                wsum[v.index] += g.weight
    res = np.where(wsum[:, None] > 0, acc / np.maximum(wsum, 1e-9)[:, None], cm)
    back = res @ np.linalg.inv(to_model).T
    o.data.vertices.foreach_set("co", back[:, :3].ravel())
    o.data.update()


def _build_race(entry: dict, offset: Vector, tag: str) -> dict:
    race = entry["race"]
    name = f"race_{race}" + (f"_{tag}" if tag else "")
    col = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    if col.name not in [c.name for c in bpy.context.scene.collection.children]:
        bpy.context.scene.collection.children.link(col)
    for o in list(col.objects):
        bpy.data.objects.remove(o, do_unlink=True)

    imported, hidden, deformed = [], [], 0
    for part, spec in entry["parts"].items():
        before = set(bpy.data.objects)
        bpy.ops.import_scene.fbx(filepath=_ascii(spec["path"]))
        new = [o for o in bpy.data.objects if o not in before]
        bpy.context.view_layer.update()
        for o in new:
            if o.type != "MESH":
                continue
            if spec["deform_from"] is not None:
                _deform_mesh(o, entry["deform"][str(spec["deform_from"])])
                deformed += 1
            if any(o.name.startswith(p) for p in spec["hide"]):
                hidden.append(o)
        imported += new

    ivcs_names = set(json.load(open(_ascii(entry["skeleton_json"]), encoding="utf-8"))["names"])
    extra = {}
    for a in [o for o in imported if o.type == "ARMATURE"]:
        for b in a.data.bones:
            if b.name not in ivcs_names and b.name not in extra:
                extra[b.name] = (tuple(b.head_local), tuple(b.tail_local), b.parent.name if b.parent else None)
    arm = build_ivcs_armature(_ascii(entry["skeleton_json"]), entry["ffxiv"], extra)
    arm.name = arm.data.name = f"IVCS_{name}"
    meshes = [o for o in imported if o.type == "MESH"]
    for o in meshes:
        world = o.matrix_world.copy()
        o.parent = arm
        o.matrix_world = world
        for m in o.modifiers:
            if m.type == "ARMATURE":
                m.object = arm
    for o in imported:
        if o.type in ("ARMATURE", "EMPTY"):
            bpy.data.objects.remove(o, do_unlink=True)

    mat = bpy.data.materials.get(f"race_{race}") or bpy.data.materials.new(f"race_{race}")
    mat.diffuse_color = COLORS.get(race, (0.8, 0.8, 0.8, 1))
    for o in [arm, *meshes]:
        for c in list(o.users_collection):
            c.objects.unlink(o)
        col.objects.link(o)
    for o in meshes:
        o.data.materials.clear()
        o.data.materials.append(mat)
    for o in hidden:
        o.hide_set(True)
        o.hide_render = True
    missing = pose_armature(arm, entry["ffxiv"])
    arm.matrix_world = Matrix.Translation(offset) @ TO_WORLD

    label = bpy.data.curves.new(f"label_{name}", "FONT")
    label.body = f"{race} {entry['name']}" + (f"  [{tag}]" if tag else "")
    label.size = 0.08
    lo = bpy.data.objects.new(f"label_{name}", label)
    lo.location = (offset.x - 0.35, offset.y + 0.5, -0.15)
    lo.rotation_euler = (1.5708, 0, 0)
    col.objects.link(lo)
    return {"race": race, "meshes": len(meshes), "deformed": deformed, "follow_parent": len(missing)}


def build(json_path: str, spacing: float = 1.3, row: int = 0, tag: str = "") -> list:
    """row：第几排（沿 -Y 方向排开，多个动作并排对比时用），tag：集合名后缀。"""
    data = json.load(open(_ascii(json_path), encoding="utf-8"))
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    return [_build_race(e, Vector((i * spacing, -row * 2.5, 0)), tag) for i, e in enumerate(data["races"])]
