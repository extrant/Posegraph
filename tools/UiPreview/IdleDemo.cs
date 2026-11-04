using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Posegraph.Idle;
using Posegraph.UI;

namespace UiPreview;

/// <summary>
/// 教程「转换为 idle」窗口的截图（tut-idle）。离线没有游戏的 Havok，预览数据这样造：
/// 青色 = 后端按示例图真实算出的姿势；灰色「原版」= 骨架绑定姿势把手臂放下来的站姿，加一点呼吸起伏（只是示意界面）。
/// IdleService 不经构造（要游戏服务），直接填好槽位、Penumbra 已就绪等缓存。
/// </summary>
public static class IdleDemo
{
    private const string Race = "c0101";
    // 骨架数据用仓库里后端的那份（从程序目录往上找 backend\src\posegraph）
    private static string Skeleton
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "backend", "src", "posegraph", "retarget", "data", $"ffxiv_{Race}.json");
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException($"ffxiv_{Race}.json");
        }
    }

    public static void Register(Dictionary<string, Action<App, string>> scenes)
    {
        scenes["tut-idle"] = (app, path) =>
        {
            App.FakeActors = Demo.Actors;
            app.Gposing = true;
            app.Main.IsOpen = true;   // 主窗口开着才会去查后端在不在线
            Demo.WaitBackend(app, () => Host.DrawWindow(app.Main));
            app.Main.IsOpen = false;
            var image = Demo.Image("arms-open");
            var json = app.Main.PosesForRacesAsync(image, [Race]).GetAwaiter().GetResult()[Race];

            var families = new Dictionary<(IdleFamily, string), List<int>>();
            var inherited = new Dictionary<(IdleFamily, int, string), string?>();
            string[] races = [Race, "c0701", "c1301"];
            foreach (var r in races)
            {
                families[(IdleFamily.Stand, r)] = [0, 1, 2, 3, 4, 5, 6];
                inherited[(IdleFamily.Stand, 3, r)] = null;
            }
            var service = (IdleService)RuntimeHelpers.GetUninitializedObject(typeof(IdleService));
            App.Set(service, "_slots", families);
            App.Set(service, "_inherited", inherited);
            App.Set(service, "_penumbraChecked", long.MaxValue);
            App.Set(service, "_penumbraReady", true);

            var window = new IdleWindow(service, () => Race, app.Main.PosesForRacesAsync, () => true);
            window.Open(image);
            App.Set(window, "_family", IdleFamily.Stand);
            App.Set(window, "_slot", 3);
            foreach (var r in races.Skip(1)) App.Get<HashSet<string>>(window, "_races").Add(r);
            App.Get<System.Collections.Concurrent.ConcurrentDictionary<string, string>>(window, "_poseCache")[Race] = json;
            App.Set(window, "_preview", Build(json));
            App.Set(window, "_previewKey", ((IdleFamily, int, string)?)(IdleFamily.Stand, 3, Race));
            App.Set(window, "_playing", true);
            App.Set(window, "_frame", 18);
            App.Set(window, "_rotationDeg", new Vector3(0, 10, 0));
            window.Position = new Vector2(40, 40);
            window.Size = new Vector2(920, 800);
            window.SizeCondition = ImGuiCond.Always;
            app.Shot(path, () => Host.DrawWindow(window), [window.WindowName], frames: 6);
        };
    }

    /// <summary>预览数据：骨架（名字、父骨骼、绑定局部姿势）+ 示意的原版站姿逐帧 + 我们的姿势（局部，骨长用绑定的）。</summary>
    private static IdlePreview Build(string poseJson)
    {
        using var sk = JsonDocument.Parse(File.ReadAllText(Skeleton));
        var names = sk.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var parents = sk.RootElement.GetProperty("parents").EnumerateArray().Select(e => (short)e.GetInt32()).ToArray();
        Vector3 V3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        var t = sk.RootElement.GetProperty("local_translation").EnumerateArray().Select(V3).ToArray();
        var r = sk.RootElement.GetProperty("local_rotation").EnumerateArray()
                  .Select(e => new Quaternion(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle())).ToArray();
        var s = sk.RootElement.GetProperty("local_scale").EnumerateArray().Select(V3).ToArray();
        var bind = Enumerable.Range(0, names.Length).Select(i => new BoneTransform(t[i], r[i], s[i])).ToArray();
        var index = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);

        // 原版站姿（示意）：绑定姿势的上臂在模型空间里转到朝下、略微外开（肩 → 肘的方向）
        var stand = (BoneTransform[])bind.Clone();
        foreach (var side in new[] { "l", "r" })
        {
            var arm = index[$"j_ude_a_{side}"];
            var elbow = index[$"j_ude_b_{side}"];
            var model0 = IdleMath.ToModel(stand, parents);
            var from = Vector3.Normalize(model0[elbow].Translation - model0[arm].Translation);
            var to = Vector3.Normalize(new Vector3(MathF.Sign(from.X) * 0.22f, -1f, 0.04f));
            var axis = Vector3.Cross(from, to);
            var angle = MathF.Acos(Math.Clamp(Vector3.Dot(from, to), -1f, 1f));
            var turn = axis.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
            var parent = parents[arm] >= 0 ? model0[parents[arm]].Rotation : Quaternion.Identity;
            var best = IdleMath.RotateInModel(stand[arm], parent, turn);
            stand[arm] = best;
        }

        // 呼吸：脊椎、胸前后轻轻起伏，身体上下几毫米（2 秒一个循环）
        const int frames = 60;
        var breathing = new[] { "j_sebo_b", "j_sebo_c", "j_sako_l", "j_sako_r" }.Where(index.ContainsKey).Select(n => index[n]).ToArray();
        var sampled = new BoneTransform[frames][];
        for (var f = 0; f < frames; f++)
        {
            var phase = MathF.Sin(2 * MathF.PI * f / frames);
            var frame = (BoneTransform[])stand.Clone();
            foreach (var b in breathing)
                frame[b] = frame[b] with { Rotation = Quaternion.Normalize(frame[b].Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.03f * phase)) };
            frame[index["n_hara"]] = frame[index["n_hara"]] with { Translation = frame[index["n_hara"]].Translation + new Vector3(0, 0.004f * phase, 0) };
            sampled[f] = frame;
        }

        // 我们的姿势：模型空间 → 局部（姿势里没有的骨骼接着绑定姿势）
        using var pose = JsonDocument.Parse(poseJson);
        var model = new BoneTransform[names.Length];
        var ours = new BoneTransform?[names.Length];
        var bones = pose.RootElement.GetProperty("Bones");
        for (var b = 0; b < names.Length; b++)
        {
            var p = parents[b];
            var parent = p >= 0 ? model[p] : BoneTransform.Identity;
            if (bones.TryGetProperty(names[b], out var bone))
            {
                float[] F(string key) => [.. bone.GetProperty(key).GetString()!.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture))];
                var pos = F("Position");
                var rot = F("Rotation");
                var sc = F("Scale");
                model[b] = new BoneTransform(new Vector3(pos[0], pos[1], pos[2]), Quaternion.Normalize(new Quaternion(rot[0], rot[1], rot[2], rot[3])),
                                             new Vector3(sc[0], sc[1], sc[2]));
                ours[b] = IdleMath.Relative(parent, model[b]);
            }
            else
            {
                model[b] = IdleMath.Compose(parent, bind[b]);
            }
        }

        var planted = new bool[names.Length];
        for (var b = 0; b < names.Length; b++)
            planted[b] = names[b] is "j_asi_a_l" or "j_asi_a_r" || (parents[b] >= 0 && planted[parents[b]]);
        // 这份骨架数据来自 IVCS，中原男也带尾巴骨骼（游戏里的原版骨架没有）：示意图里不画
        var shown = names.Select(n => n.StartsWith("n_sippo") ? "" : n).ToArray();
        return new IdlePreview(shown, parents, sampled, ours, 30f, planted, true);
    }
}
