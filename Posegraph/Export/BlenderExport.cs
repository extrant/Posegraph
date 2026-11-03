using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using Posegraph.Lighting;

namespace Posegraph.Export;

/// <summary>
/// 导出到 Blender：把 GPose 现在的镜头、Posegraph 打的灯、每个 GPose 角色的位置朝向和姿势写成一个场景文件
/// （文档\Posegraph\Blender\时间\scene.posegraph.json），旁边放上 Blender 插件 posegraph_blender.py 和使用说明。
/// 人物模型用 Meddle 导出（MeddleTools 导入 Blender），这边的 Blender 插件把镜头和灯放回游戏里的位置。
/// 坐标都是游戏世界坐标（米，Y 朝上，同 glTF）；Blender 插件按 glTF 导入的规则换成 Z 朝上，所以和 Meddle 的导出对得上。
/// </summary>
public sealed class BlenderExport(IFramework framework, IClientState clientState, PoseApplier applier, LightingService lighting,
                                  Func<(int Index, string Name)[]> actors)
{
    private const string AddonResource = "Posegraph.Export.posegraph_blender.py";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Posegraph", "Blender");

    /// <summary>导出，返回场景文件夹。不在 GPose、读不到镜头时抛 InvalidOperationException（消息给用户看）。</summary>
    public async Task<string> ExportAsync()
    {
        if (!clientState.IsGPosing) throw new InvalidOperationException(Loc.T("进入 GPose 后才能导出"));
        var camera = await framework.RunOnFrameworkThread(GposeCamera.Pose)
                     ?? throw new InvalidOperationException(Loc.T("读不到 GPose 镜头"));
        var now = DateTime.Now;
        var root = new JsonObject
        {
            ["format"] = "posegraph-scene",
            ["version"] = 1,
            ["plugin"] = typeof(BlenderExport).Assembly.GetName().Version?.ToString(3),
            ["created"] = now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["axes"] = "game world, meters, Y up (same as glTF)",
            ["camera"] = new JsonObject
            {
                ["position"] = Vec(camera.Position), ["forward"] = Vec(camera.Forward), ["up"] = Vec(camera.Up),
                ["fovYDeg"] = Round(camera.FovY * 180f / MathF.PI), ["aspect"] = Round(camera.Aspect),
            },
            ["lights"] = new JsonArray([.. lighting.WorldLights.Select(l => (JsonNode)Light(l.Name, l.Group, l.State, l.Distance))]),
            ["characters"] = await CharactersAsync(),
        };
        var folder = Path.Combine(Folder, now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "scene.posegraph.json"), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        await using (var addon = typeof(BlenderExport).Assembly.GetManifestResourceStream(AddonResource))
        await using (var file = File.Create(Path.Combine(folder, "posegraph_blender.py")))
            if (addon is not null) await addon.CopyToAsync(file);
        await File.WriteAllTextAsync(Path.Combine(folder, Loc.T("使用说明.txt")), Loc.T(Readme));
        return folder;
    }

    private const string Readme =
        "Posegraph 导出到 Blender\n\n" +
        "1. 第一次用：Blender（4.2 以上）→ 编辑 → 偏好设置 → 插件 → 右上角下拉「从磁盘安装」，选这个文件夹里的 posegraph_blender.py。\n" +
        "2. 人物：在游戏里用 Meddle 导出角色，在 Blender 里用 MeddleTools 导入。\n" +
        "3. 镜头和灯：Blender 右侧栏（N）→ Posegraph →「导入 Posegraph 场景…」，选 scene.posegraph.json。\n" +
        "4. 人物和镜头、灯没对上（Meddle 把角色导出在原点）：选中那个角色对应的「Posegraph 角色名」空物体，点「以选中的角色为原点」。\n" +
        "5. 灯太亮或太暗：调 Posegraph 面板里的「灯光亮度」。游戏的灯和 Blender 的物理单位对不上，亮度只是近似。\n";

    /// <summary>每个 GPose 角色：名字、世界位置 / 朝向 / 缩放（Brio 的模型变换），和姿势（每根骨骼相对模型的位置、朝向、缩放）。</summary>
    private async Task<JsonArray> CharactersAsync()
    {
        var list = new JsonArray();
        foreach (var (index, name) in actors())
        {
            var model = await applier.GetTransformAsync(index);
            if (model is null) continue;
            var person = new JsonObject
            {
                ["name"] = name, ["index"] = index,
                ["position"] = Array(model.Position), ["rotation"] = Array(model.Rotation), ["scale"] = Array(model.Scale),
            };
            if (await applier.GetPoseAsync(index) is { } pose && Bones(pose) is { } bones) person["bones"] = bones;
            if (await framework.RunOnFrameworkThread(() => BindPose(index)) is { } bind) person["bind"] = bind;
            list.Add(person);
        }
        return list;
    }

    /// <summary>
    /// 角色骨架的绑定姿势（模型系）：每个分部骨架（身体、脸、头发…）的 Havok 参考姿势沿父骨骼串起来；
    /// 分部骨架的根按它连着的身体骨骼对齐（同名骨骼以身体的为准）。Blender 里套姿势时按「游戏姿势相对绑定姿势转了多少」
    /// 去转 Meddle 骨架的静止姿态——Blender 导入 glTF 时会把每根骨骼的轴转 90° 之类，直接用游戏的朝向对不上。必须在主线程调用。
    /// </summary>
    private unsafe JsonObject? BindPose(int index)
    {
        if (applier.Resolve(index) is not { } actor) return null;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
        var draw = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)go->DrawObject;
        if (draw == null || draw->Skeleton == null) return null;
        var skeleton = draw->Skeleton;
        var bones = new JsonObject();
        Vector3[] bodyPos = [];
        Quaternion[] bodyRot = [];
        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var resource = partial->SkeletonResourceHandle;
            if (resource == null || resource->HavokSkeleton == null) continue;
            var hk = resource->HavokSkeleton;
            var n = hk->Bones.Length;
            var pos = new Vector3[n];
            var rot = new Quaternion[n];
            for (var i = 0; i < n; i++)
            {
                var t = hk->ReferencePose[i];
                var local = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
                var turn = Quaternion.Normalize(new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W));
                var parent = hk->ParentIndices[i];
                if (parent < 0 || parent >= i)
                {
                    pos[i] = local;
                    rot[i] = turn;
                }
                else
                {
                    pos[i] = pos[parent] + Vector3.Transform(local, rot[parent]);
                    rot[i] = rot[parent] * turn;
                }
            }
            if (p == 0)
            {
                (bodyPos, bodyRot) = (pos, rot);
            }
            else
            {
                // 分部骨架挂在身体的某根骨骼上：把它连着的那根骨骼对齐到身体那根
                int own = partial->ConnectedBoneIndex, body = partial->ConnectedParentBoneIndex;
                if (own < 0 || own >= n || body < 0 || body >= bodyPos.Length) continue;
                var delta = bodyRot[body] * Quaternion.Inverse(rot[own]);
                var shift = bodyPos[body] - Vector3.Transform(pos[own], delta);
                for (var i = 0; i < n; i++)
                {
                    pos[i] = shift + Vector3.Transform(pos[i], delta);
                    rot[i] = delta * rot[i];
                }
            }
            for (var i = 0; i < n; i++)
            {
                var name = hk->Bones[i].Name.String;
                if (string.IsNullOrEmpty(name) || bones.ContainsKey(name)) continue;
                bones[name] = new JsonObject { ["position"] = Vec(pos[i]), ["rotation"] = Quat(rot[i]) };
            }
        }
        return bones.Count > 0 ? bones : null;
    }

    private static JsonObject Light(string name, int group, LightState s, float distance)
    {
        var o = new JsonObject
        {
            ["name"] = Loc.T(name), ["group"] = group,
            ["kind"] = s.Kind switch { LightKind.Spot => "spot", LightKind.Flat => "flat", _ => "point" },
            ["position"] = Vec(s.Position), ["rotation"] = Quat(s.Rotation), ["color"] = Vec(s.Color),
            ["range"] = Round(s.Range), ["spotAngle"] = Round(s.SpotAngle), ["softness"] = Round(s.Softness),
            ["shadows"] = s.Shadows, ["falloff"] = Round(s.Falloff), ["distance"] = Round(distance),
        };
        // 面光在 Blender 里换成矩形面光：边长大约罩住光斑的一半
        if (s.Kind == LightKind.Flat) o["size"] = Round(Math.Clamp(distance * MathF.Tan(s.SpotAngle * MathF.PI / 360f), 0.3f, 3f));
        return o;
    }

    /// <summary>Brio 姿势 JSON 的骨骼 → {骨骼名: {position, rotation, scale}}；解析不了时为 null。</summary>
    private static JsonObject? Bones(string poseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(poseJson);
            var bones = new JsonObject();
            foreach (var bone in doc.RootElement.GetProperty("Bones").EnumerateObject())
            {
                var o = new JsonObject();
                foreach (var (key, field) in new[] { ("position", "Position"), ("rotation", "Rotation"), ("scale", "Scale") })
                    if (bone.Value.TryGetProperty(field, out var v) && v.GetString() is { } text)
                        o[key] = new JsonArray([.. text.Split(',').Select(x => (JsonNode)Round(float.Parse(x, CultureInfo.InvariantCulture)))]);
                bones[bone.Name] = o;
            }
            return bones;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static float Round(float v) => MathF.Round(v, 5);

    private static JsonArray Vec(Vector3 v) => [Round(v.X), Round(v.Y), Round(v.Z)];

    private static JsonArray Quat(Quaternion q) => [Round(q.X), Round(q.Y), Round(q.Z), Round(q.W)];

    private static JsonArray Array(float[] v) => new([.. v.Select(x => (JsonNode)Round(x))]);
}
