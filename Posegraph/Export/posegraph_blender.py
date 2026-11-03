"""Posegraph Scene Import — Blender add-on.

Imports a scene exported by the Posegraph Dalamud plugin ("Export to Blender"): the GPose camera, the lights Posegraph
placed, and an empty for every GPose character (world position and facing). Characters themselves come from Meddle
(import them with MeddleTools); this add-on only puts the camera and lights where they were in the game.

Coordinates: the game is Y-up like glTF, so the same conversion as Blender's glTF importer is used,
(x, y, z) -> (x, -z, y). A Meddle export made in world space lines up as it is; if Meddle exported a character at the
origin, select that character's Posegraph empty and click "Use selected character as origin".

"Apply game pose to selected armature" poses a Meddle armature exactly as the character stood in the game when the
scene was exported (Meddle may have captured another moment). Blender's glTF import turns each bone's axes (by 90°
steps), so the pose is applied as "how far each bone turned from the bind pose", using the bind pose the plugin exports.

License: MIT (same as Posegraph).
"""

bl_info = {
    "name": "Posegraph Scene Import",
    "author": "Siren",
    "version": (1, 0, 0),
    "blender": (4, 2, 0),
    "location": "View3D > Sidebar > Posegraph",
    "description": "Import the camera, lights and character positions exported by the Posegraph FFXIV plugin",
    "category": "Import-Export",
}

import json
import math

import bpy
from bpy.props import FloatProperty, StringProperty
from bpy_extras.io_utils import ImportHelper
from mathutils import Matrix, Quaternion, Vector

FORMAT = "posegraph-scene"
# Game light brightness (linear RGB, about 10 for a normal key light at ~2 m) -> Blender watts.
# Rough: a key light of 10 at 1.8 m comes out around 150 W; lights moved back for full-body framing are brighter in the
# game file too, so the inverse-square falloff stays about right. "Brightness" in the panel scales everything.
WATTS_PER_UNIT = 15.0
AREA_FACTOR = 1.5
# Game axes -> Blender axes (same as the glTF importer): x stays, game Y (up) becomes Blender Z, game Z becomes -Y.
AXES = Matrix(((1, 0, 0), (0, 0, -1), (0, 1, 0)))
TEXT = {
    "zh": {
        "panel": "Posegraph",
        "import": "导入 Posegraph 场景…",
        "origin": "以选中的角色为原点",
        "origin_tip": "Meddle 把角色导出在原点时用：选中这个角色对应的 Posegraph 空物体，整个场景按它挪到原点",
        "brightness": "灯光亮度",
        "apply_pose": "套用游戏里的姿势到选中的骨架",
        "pose_tip": "Meddle 导出的姿势和游戏里不一样时用：选中人物的骨架，按导出时游戏里的姿势摆好（另存一个动作，Meddle 原来的动作留着）",
        "pick_armature": "先选中一个骨架（Meddle 导出的人物）",
        "no_bind": "场景文件里没有这个角色的绑定姿势（用新版 Posegraph 重新导出）",
        "posed": "已套用 {name} 的姿势：{bones} 根骨骼",
        "no_scene": "先导入一个 Posegraph 场景",
        "pick_empty": "先选中一个 Posegraph 角色空物体",
        "bad_file": "不是 Posegraph 导出的场景文件",
        "done": "已导入：镜头 1 个、灯 {lights} 盏、角色 {people} 个",
    },
    "en": {
        "panel": "Posegraph",
        "import": "Import Posegraph scene…",
        "origin": "Use selected character as origin",
        "origin_tip": "When Meddle exported a character at the origin: select that character's Posegraph empty and the whole scene moves so it sits at the origin",
        "brightness": "Light brightness",
        "apply_pose": "Apply game pose to selected armature",
        "pose_tip": "When Meddle's pose differs from the game: select the character's armature to pose it as it was in the game at export (saved as a new action; Meddle's action is kept)",
        "pick_armature": "Select an armature first (a character exported by Meddle)",
        "no_bind": "The scene file has no bind pose for this character (export again with a newer Posegraph)",
        "posed": "Applied {name}'s pose to {bones} bones",
        "no_scene": "Import a Posegraph scene first",
        "pick_empty": "Select a Posegraph character empty first",
        "bad_file": "Not a scene file exported by Posegraph",
        "done": "Imported 1 camera, {lights} lights, {people} characters",
    },
}


def t(key: str, **kw) -> str:
    lang = "zh" if bpy.app.translations.locale.startswith("zh") else "en"
    return TEXT[lang][key].format(**kw)


def wrap(text: str, width: int) -> list[str]:
    """Panel labels don't wrap: cut long text into lines (CJK text has no spaces, so cut by characters)."""
    lines, line = [], ""
    for word in (text.split(" ") if " " in text else list(text)):
        sep = " " if " " in text and line else ""
        if len(line) + len(sep) + len(word) > width and line:
            lines.append(line)
            line, sep = "", ""
        line += sep + word
    return lines + ([line] if line else [])


def vec(v) -> Vector:
    return AXES @ Vector(v)


def rot(q) -> Matrix:
    """Game rotation (quaternion x, y, z, w) -> Blender 3x3 matrix."""
    x, y, z, w = q
    return AXES @ Quaternion((w, x, y, z)).to_matrix() @ AXES.transposed()


def look(forward: Vector, up: Vector) -> Matrix:
    """Rotation whose -Z looks along forward with +Y towards up (how Blender cameras and lights point)."""
    z = -forward.normalized()
    x = up.cross(z)
    if x.length < 1e-6:
        x = Vector((1, 0, 0)).cross(z) if abs(z.x) < 0.9 else Vector((0, 1, 0)).cross(z)
    x.normalize()
    y = z.cross(x)
    return Matrix((x, y, z)).transposed()


def collection(name: str):
    coll = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(coll)
    return coll


def import_scene(path: str, brightness: float):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    if data.get("format") != FORMAT:
        raise ValueError(t("bad_file"))
    coll = collection(f"Posegraph {data.get('created', '')}".strip())
    coll["posegraph_scene"] = True
    coll["posegraph_file"] = path

    cam = data["camera"]
    cam_data = bpy.data.cameras.new("Posegraph Camera")
    cam_data.sensor_fit = "VERTICAL"
    cam_data.angle_y = math.radians(cam["fovYDeg"])
    cam_data.clip_start = 0.05
    cam_data.clip_end = 2000
    cam_obj = bpy.data.objects.new("Posegraph Camera", cam_data)
    cam_obj.matrix_world = Matrix.Translation(vec(cam["position"])) @ look(vec(cam["forward"]), vec(cam["up"])).to_4x4()
    coll.objects.link(cam_obj)
    scene = bpy.context.scene
    scene.camera = cam_obj
    aspect = cam.get("aspect") or 16 / 9
    scene.render.resolution_x = max(16, round(scene.render.resolution_y * aspect))

    for i, light in enumerate(data.get("lights", [])):
        kind = light["kind"]
        rgb = Vector(light["color"])
        power = max(rgb) if max(rgb) > 0 else 0.0
        if kind == "spot":
            ld = bpy.data.lights.new(light["name"], "SPOT")
            ld.spot_size = math.radians(min(max(light["spotAngle"], 1.0), 179.0))
            ld.spot_blend = min(max(light["softness"] / max(light["spotAngle"], 1.0), 0.0), 1.0)
            ld.shadow_soft_size = 0.1
        elif kind == "flat":
            ld = bpy.data.lights.new(light["name"], "AREA")
            ld.shape = "RECTANGLE"
            ld.size = ld.size_y = max(0.3, light.get("size", 1.0))
        else:
            ld = bpy.data.lights.new(light["name"], "POINT")
            ld.shadow_soft_size = 0.1
        ld.color = (rgb / power)[:] if power > 0 else (1.0, 1.0, 1.0)
        ld["posegraph_watts"] = power * WATTS_PER_UNIT * (AREA_FACTOR if kind == "flat" else 1.0)
        ld.energy = ld["posegraph_watts"] * brightness
        ld.use_shadow = True
        if hasattr(ld, "use_custom_distance"):
            ld.use_custom_distance = True
            ld.cutoff_distance = light["range"]
        obj = bpy.data.objects.new(f"{i + 1:02d} {light['name']}", ld)
        direction = rot(light["rotation"]) @ vec((0, 0, 1))   # game lights shine along their own +Z (game axes)
        obj.matrix_world = Matrix.Translation(vec(light["position"])) @ look(direction, Vector((0, 0, 1))).to_4x4()
        obj["posegraph_group"] = light.get("group", 0)
        coll.objects.link(obj)

    people = data.get("characters", [])
    for person in people:
        empty = bpy.data.objects.new(f"Posegraph {person['name']}", None)
        empty.empty_display_type = "ARROWS"
        empty.empty_display_size = 0.5
        empty.matrix_world = Matrix.Translation(vec(person["position"])) @ rot(person["rotation"]).to_4x4()
        empty["posegraph_character"] = person["name"]
        coll.objects.link(empty)
    return coll, len(data.get("lights", [])), len(people)


def quat(q) -> Quaternion:
    x, y, z, w = q
    return Quaternion((w, x, y, z))


def apply_pose(arm, person: dict) -> int:
    """Pose arm like person was in the game. For each bone: game rotation relative to the bind pose (model space),
    turned into Blender axes, applied to the bone's rest orientation; position straight from the game.
    Parents first, so each bone's basis is relative to its parent's new pose. Returns the number of bones posed."""
    pose, bind = person.get("bones", {}), person.get("bind", {})
    world = {}   # bone name -> new pose matrix (armature space)
    count = 0
    for pb in sorted(arm.pose.bones, key=lambda b: len(b.parent_recursive)):
        bone = pb.bone
        rest = bone.matrix_local
        parent = bone.parent
        parent_pose = world.get(parent.name) if parent else None
        if parent is not None and parent_pose is None:
            parent_pose = parent.matrix_local
        base = rest if parent is None else parent_pose @ parent.matrix_local.inverted() @ rest
        target = base
        if bone.name in pose and bone.name in bind and "rotation" in pose[bone.name] and "position" in pose[bone.name]:
            delta = AXES @ (quat(pose[bone.name]["rotation"]) @ quat(bind[bone.name]["rotation"]).inverted()).to_matrix() @ AXES.transposed()
            target = Matrix.Translation(vec(pose[bone.name]["position"])) @ (delta @ rest.to_3x3()).to_4x4()
            count += 1
        world[bone.name] = target
        pb.rotation_mode = "QUATERNION"
        pb.matrix_basis = base.inverted() @ target
    return count


def scenes():
    return [c for c in bpy.data.collections if c.get("posegraph_scene")]


def update_brightness(self, context):
    for coll in scenes():
        for obj in coll.objects:
            if obj.type == "LIGHT" and "posegraph_watts" in obj.data:
                obj.data.energy = obj.data["posegraph_watts"] * context.scene.posegraph_brightness


class POSEGRAPH_OT_import(bpy.types.Operator, ImportHelper):
    bl_idname = "posegraph.import_scene"
    bl_label = "Import Posegraph scene"
    bl_description = "Camera, lights and character positions exported by the Posegraph plugin"
    bl_options = {"REGISTER", "UNDO"}
    filename_ext = ".json"
    filter_glob: StringProperty(default="*.posegraph.json;*.json", options={"HIDDEN"})

    def execute(self, context):
        try:
            _, lights, people = import_scene(self.filepath, context.scene.posegraph_brightness)
        except (OSError, ValueError, KeyError) as ex:
            self.report({"ERROR"}, str(ex))
            return {"CANCELLED"}
        self.report({"INFO"}, t("done", lights=lights, people=people))
        return {"FINISHED"}


class POSEGRAPH_OT_origin(bpy.types.Operator):
    bl_idname = "posegraph.character_origin"
    bl_label = "Use selected character as origin"
    bl_description = "Move the Posegraph scene so the selected character empty sits at the origin facing forward"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        empty = context.active_object
        if empty is None or "posegraph_character" not in empty:
            self.report({"WARNING"}, t("pick_empty"))
            return {"CANCELLED"}
        inverse = empty.matrix_world.inverted()
        for coll in empty.users_collection:
            for obj in coll.objects:
                obj.matrix_world = inverse @ obj.matrix_world
        return {"FINISHED"}


class POSEGRAPH_OT_apply_pose(bpy.types.Operator):
    bl_idname = "posegraph.apply_pose"
    bl_label = "Apply game pose to selected armature"
    bl_description = "Pose the selected armature as the character was in the game when the Posegraph scene was exported"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        arm = context.active_object
        if arm is None or arm.type != "ARMATURE":
            self.report({"WARNING"}, t("pick_armature"))
            return {"CANCELLED"}
        files = [c["posegraph_file"] for c in scenes() if "posegraph_file" in c]
        if not files:
            self.report({"WARNING"}, t("no_scene"))
            return {"CANCELLED"}
        with open(files[-1], encoding="utf-8") as f:
            people = json.load(f).get("characters", [])
        # Meddle names the armature "Character-<name>"; otherwise the only / first character
        person = next((p for p in people if p["name"] and p["name"] in arm.name), people[0] if people else None)
        if person is None or not person.get("bind"):
            self.report({"WARNING"}, t("no_bind"))
            return {"CANCELLED"}
        if arm.animation_data and arm.animation_data.action:
            arm.animation_data.action.use_fake_user = True   # keep Meddle's pose action
        arm.animation_data_create()
        arm.animation_data.action = bpy.data.actions.new(f"Posegraph {person['name']}")
        count = apply_pose(arm, person)
        for pb in arm.pose.bones:
            pb.keyframe_insert("location")
            pb.keyframe_insert("rotation_quaternion")
        self.report({"INFO"}, t("posed", name=person["name"], bones=count))
        return {"FINISHED"}


class POSEGRAPH_PT_panel(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Posegraph"
    bl_label = "Posegraph"

    def draw(self, context):
        col = self.layout.column()
        col.operator(POSEGRAPH_OT_import.bl_idname, text=t("import"), icon="IMPORT")
        col.prop(context.scene, "posegraph_brightness", text=t("brightness"))
        col.separator()
        col.operator(POSEGRAPH_OT_origin.bl_idname, text=t("origin"), icon="PIVOT_CURSOR")
        tip = col.column()
        tip.scale_y = 0.8
        for line in wrap(t("origin_tip"), 34):
            tip.label(text=line)
        col.separator()
        col.operator(POSEGRAPH_OT_apply_pose.bl_idname, text=t("apply_pose"), icon="ARMATURE_DATA")
        tip = col.column()
        tip.scale_y = 0.8
        for line in wrap(t("pose_tip"), 34):
            tip.label(text=line)


def menu_import(self, context):
    self.layout.operator(POSEGRAPH_OT_import.bl_idname, text="Posegraph scene (.json)")


CLASSES = (POSEGRAPH_OT_import, POSEGRAPH_OT_origin, POSEGRAPH_OT_apply_pose, POSEGRAPH_PT_panel)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.posegraph_brightness = FloatProperty(name="Brightness", default=1.0, min=0.0, soft_max=10.0,
                                                         update=update_brightness)
    bpy.types.TOPBAR_MT_file_import.append(menu_import)


def unregister():
    bpy.types.TOPBAR_MT_file_import.remove(menu_import)
    del bpy.types.Scene.posegraph_brightness
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
