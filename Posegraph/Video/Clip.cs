using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Posegraph.Video;

/// <summary>
/// 视频识别出的片段（后端 video.py 的结果）：每个人每帧每根骨骼的位置 / 旋转 / 缩放，主角的每帧镜头（角色模型系）。
/// 存成 文档\Posegraph\Clips\*.pgclip（就是后端结果的 JSON，外加 meta：名字、视频名、创建时间、种族、角色体型、原始数据文件）。
/// 名字 = 基础名（视频名 + 起止）+ 种族性别；识别的原始结果（和种族无关）存在旁边的 *.pgraw，换种族、调表情权重时传回后端重算，
/// 同一个基础名、同一个种族的片段重算后覆盖。
/// 新版后端另给主角的走位 root（每帧相对第一帧的水平位置 + 转向）和三条镜头轨道 cameras（original 原运镜 / follow 平滑跟拍 /
/// pan 定点摇镜，都在第一帧角色的模型系里），走动模式用；旧片段没有，走动模式退回「固定机位」的推算。
/// </summary>
public sealed class Clip
{
    public required string Name { get; init; }
    public required double Fps { get; init; }
    public required double Start { get; init; }
    public required int FrameCount { get; init; }
    public required int[] ImageSize { get; init; }
    public required ClipPerson[] People { get; init; }
    public required ClipCamera[] Camera { get; init; }
    public string[] Warnings { get; init; } = [];
    public string? Path { get; set; }

    /// <summary>不带种族的名字（视频名 + 起止），重算出的各种族版本共用。</summary>
    public string BaseName { get; init; } = "";
    public string Video { get; init; } = "";

    /// <summary>骨骼按哪个种族算的（cXXXX）。</summary>
    public string? Race { get; init; }

    /// <summary>识别 / 重算时角色的体型签名（种族 + 外貌数据），和角色现在的不一样就要重算。</summary>
    public string? Body { get; init; }

    /// <summary>识别原始数据文件（*.pgraw）的完整路径；旧片段没有。</summary>
    public string? RawPath { get; set; }

    public double Smooth { get; init; } = 0.5;

    /// <summary>原视频的完整路径（录制配音乐用）；旧片段没有。</summary>
    public string? VideoPath { get; init; }

    /// <summary>主角每帧的走位：x、y、z（米，第一帧角色的模型系）+ 转向（弧度，绕 y）；旧片段为空。</summary>
    public Vector4[] Root { get; init; } = [];

    /// <summary>镜头轨道：original / follow / pan → 每帧镜头（第一帧角色的模型系）；旧片段为空。</summary>
    public Dictionary<string, ClipCamera[]> Tracks { get; init; } = new();

    /// <summary>镜头解算的情况（新版后端）：depth 转动 + 平移 / rotation 只有转动 / none；可信步数占比；切镜时刻。</summary>
    public ClipMotion? Motion { get; init; }

    /// <summary>每帧「拍得到人」的程度 0~1（没识别到的帧为 0，前后约 0.4 秒渐变），拍不到人时换自动镜头用。</summary>
    public float[] Visible { get; init; } = [];

    /// <summary>镜头整体调整（meta.view；每个片段各存一份）。</summary>
    public ClipView View { get; set; } = ClipView.Default;

    /// <summary>有走位和镜头轨道（新版后端识别 / 重算的）。</summary>
    public bool HasWalkTracks => Root.Length == FrameCount && FrameCount > 0 && Tracks.ContainsKey("original");

    /// <summary>片段时长（秒，第一帧到最后一帧）。</summary>
    public double Duration => FrameCount <= 1 ? 0 : (FrameCount - 1) / Fps;

    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Posegraph", "Clips");

    public static Clip Parse(string json, string name)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var times = root["times"]!.AsArray();
        var people = root["people"]!.AsArray().Select(p =>
        {
            var bones = p!["bones"]!.AsArray().Select(b => (string)b!).ToArray();
            var frames = p["frames"]!.AsArray().Select(f => f!.AsArray().Select(v => (float)(double)v!).ToArray()).ToArray();
            var missing = p["missing"]?.AsArray().Select(m => (bool)m!).ToArray() ?? new bool[frames.Length];
            var offset = p["offset"]?.AsArray().Select(r =>
            {
                var a = r!.AsArray();
                return new Vector4((float)(double)a[0]!, (float)(double)a[1]!, (float)(double)a[2]!, (float)(double)a[3]!);
            }).ToArray() ?? [];
            return new ClipPerson(bones, frames, missing) { Offset = offset, Race = (string?)p["race"] };
        }).ToArray();
        var camera = root["camera"]!.AsArray().Select(c => new ClipCamera(
            Vec(c!["position"]!), Vec(c["forward"]!), Vec(c["up"]!), (float)(double)c["fovY"]!)).ToArray();
        var meta = root["meta"];
        var race = (string?)meta?["race"] ?? (string?)root["people"]?.AsArray().FirstOrDefault()?["race"];
        var walk = root["root"]?.AsArray().Select(r =>
        {
            var a = r!.AsArray();
            return new Vector4((float)(double)a[0]!, (float)(double)a[1]!, (float)(double)a[2]!, (float)(double)a[3]!);
        }).ToArray() ?? [];
        var tracks = new Dictionary<string, ClipCamera[]>();
        if (root["cameras"] is JsonObject cams)
            foreach (var (key, list) in cams)
                tracks[key] = list!.AsArray().Select(Cam).ToArray();
        ClipMotion? motion = null;
        if (root["motion"] is JsonObject m)
            motion = new ClipMotion((string?)m["camera"] ?? "none", (double?)m["solved"] ?? 0,
                                    m["breaks"]?.AsArray().Select(b => (double)b!).ToArray() ?? []);
        var fps = (double)root["fps"]!;
        return new Clip
        {
            Name = (string?)meta?["name"] ?? name,
            BaseName = (string?)meta?["base"] ?? (string?)meta?["name"] ?? name,
            Video = (string?)meta?["video"] ?? "",
            Race = race,
            Body = (string?)meta?["body"],
            RawPath = (string?)meta?["raw"],
            Smooth = (double?)root["smooth"] ?? 0.5,
            VideoPath = (string?)meta?["videoPath"],
            View = ClipView.FromJson(meta?["view"]),
            Root = walk,
            Tracks = tracks,
            Motion = motion,
            Visible = people.Length > 0 ? VisibleFrom(people[0].Missing, fps) : [],
            Fps = fps,
            Start = (double?)root["start"] ?? 0,
            FrameCount = times.Count,
            ImageSize = root["imageSize"]!.AsArray().Select(v => (int)v!).ToArray(),
            People = people,
            Camera = camera,
            Warnings = root["warnings"]?.AsArray().Select(w => (string)w!).ToArray() ?? [],
        };
    }

    private static ClipCamera Cam(JsonNode? c) =>
        new(Vec(c!["position"]!), Vec(c["forward"]!), Vec(c["up"]!), (float)(double)c["fovY"]!);

    /// <summary>没识别到人的帧为 0、识别到为 1，按约 0.8 秒的窗口取平均再 smoothstep（切换时镜头慢慢过渡）。</summary>
    private static float[] VisibleFrom(bool[] missing, double fps)
    {
        var n = missing.Length;
        var half = Math.Max(1, (int)Math.Round(0.4 * fps));
        var v = new float[n];
        for (var k = 0; k < n; k++)
        {
            var sum = 0f;
            var count = 0;
            for (var j = Math.Max(0, k - half); j <= Math.Min(n - 1, k + half); j++, count++)
                sum += missing[j] ? 0 : 1;
            var x = sum / count;
            v[k] = x * x * (3 - 2 * x);
        }
        return v;
    }

    private static Vector3 Vec(JsonNode node)
    {
        var a = node.AsArray();
        return new Vector3((float)(double)a[0]!, (float)(double)a[1]!, (float)(double)a[2]!);
    }

    /// <summary>
    /// 后端结果原文加上 meta 存进片段文件夹，返回路径。名字 = baseName + 种族性别（种族取结果里实际用的）。
    /// raw：识别原始数据文件名（和片段同目录）。overwrite：同名就覆盖（重算），否则重名自动加编号。
    /// </summary>
    public static string Save(string resultJson, string baseName, string videoName, string? body, string? raw, bool overwrite,
                              string? videoPath = null, ClipView? view = null)
    {
        var root = JsonNode.Parse(resultJson)!.AsObject();
        var race = (string?)root["people"]?.AsArray().FirstOrDefault()?["race"];
        var name = race is null ? baseName : $"{baseName} {Races.Label(race)}";
        root["meta"] = new JsonObject
        {
            ["name"] = name, ["base"] = baseName, ["video"] = videoName, ["created"] = DateTime.Now.ToString("s"),
            ["race"] = race, ["body"] = body, ["raw"] = raw, ["videoPath"] = videoPath,
        };
        if (view is not null && view != ClipView.Default) root["meta"]!["view"] = view.ToJson();
        Directory.CreateDirectory(Folder);
        var path = System.IO.Path.Combine(Folder, SafeName(name) + ".pgclip");
        if (!overwrite) path = Library.ImageLibrary.Unique(path);
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    /// <summary>把镜头调整写回片段文件（meta.view；默认值时删掉）。先写临时文件再替换，写到一半不会弄坏片段。</summary>
    public static void SaveView(string path, ClipView view)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (root["meta"] is not JsonObject meta) root["meta"] = meta = new JsonObject();
        if (view == ClipView.Default) meta.Remove("view");
        else meta["view"] = view.ToJson();
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString());
        File.Move(temp, path, true);
    }

    /// <summary>识别原始数据存进片段文件夹（重名加编号），返回文件名。</summary>
    public static string SaveRaw(byte[] data, string baseName)
    {
        Directory.CreateDirectory(Folder);
        var path = Library.ImageLibrary.Unique(System.IO.Path.Combine(Folder, SafeName(baseName) + ".pgraw"));
        File.WriteAllBytes(path, data);
        return System.IO.Path.GetFileName(path);
    }

    private static string SafeName(string name)
    {
        var safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return safe.Length > 0 ? safe : "clip";
    }

    public static Clip Load(string path)
    {
        var clip = Parse(File.ReadAllText(path), System.IO.Path.GetFileNameWithoutExtension(path));
        clip.Path = path;
        if (clip.RawPath is { } raw)
        {
            var full = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? Folder, raw);
            clip.RawPath = File.Exists(full) ? full : null;
        }
        return clip;
    }

    /// <summary>第 t 秒（从片段开头算）落在哪两帧之间：(前一帧, 后一帧, 插值系数)。</summary>
    public (int A, int B, float T) Locate(double t)
    {
        var f = Math.Clamp(t * Fps, 0, Math.Max(0, FrameCount - 1));
        var a = (int)Math.Floor(f);
        var b = Math.Min(a + 1, FrameCount - 1);
        return (a, b, (float)(f - a));
    }

    /// <summary>第 t 秒的镜头（模型系），相邻两帧插值。</summary>
    public ClipCamera? CameraAt(double t) => Interpolate(Camera, t);

    /// <summary>第 t 秒某条镜头轨道上的镜头（第一帧角色的模型系）；没有这条轨道时 null。</summary>
    public ClipCamera? TrackAt(string name, double t) => Tracks.TryGetValue(name, out var track) ? Interpolate(track, t) : null;

    /// <summary>第 t 秒的走位（相邻两帧线性插值；转向在后端已经展开成连续的角度）。</summary>
    public Vector4 RootAt(double t)
    {
        if (Root.Length == 0) return Vector4.Zero;
        var (a, b, k) = Locate(t);
        return Vector4.Lerp(Root[Math.Min(a, Root.Length - 1)], Root[Math.Min(b, Root.Length - 1)], k);
    }

    /// <summary>第 t 秒「拍得到人」的程度 0~1。</summary>
    public float VisibleAt(double t)
    {
        if (Visible.Length == 0) return 1f;
        var (a, b, k) = Locate(t);
        var va = Visible[Math.Min(a, Visible.Length - 1)];
        return va + (Visible[Math.Min(b, Visible.Length - 1)] - va) * k;
    }

    private ClipCamera? Interpolate(ClipCamera[] cams, double t)
    {
        if (cams.Length == 0) return null;
        var (a, b, k) = Locate(t);
        return ClipCamera.Lerp(cams[Math.Min(a, cams.Length - 1)], cams[Math.Min(b, cams.Length - 1)], k);
    }
}

/// <summary>每帧每根骨骼 10 个数：位置 xyz、四元数 xyzw、缩放 xyz。missing：这一帧没识别到（数据是前后插值补的）。
/// Offset：配角每帧相对主角的站位（主角模型系的 x、y、z 米 + 转向弧度；主角和旧片段为空）。</summary>
public sealed record ClipPerson(string[] Bones, float[][] Frames, bool[] Missing)
{
    public Vector4[] Offset { get; init; } = [];
    public string? Race { get; init; }

    /// <summary>第 t 秒的站位（相邻两帧线性插值）。</summary>
    public Vector4? OffsetAt(Clip clip, double t)
    {
        if (Offset.Length == 0) return null;
        var (a, b, k) = clip.Locate(t);
        return Vector4.Lerp(Offset[Math.Min(a, Offset.Length - 1)], Offset[Math.Min(b, Offset.Length - 1)], k);
    }

    /// <summary>第 t 秒的 Brio 姿势 JSON（相邻两帧插值：位置、缩放线性，旋转球面插值）。</summary>
    public string PoseJson(Clip clip, double t)
    {
        var (a, b, k) = clip.Locate(t);
        var fa = Frames[Math.Min(a, Frames.Length - 1)];
        var fb = Frames[Math.Min(b, Frames.Length - 1)];
        var sb = new StringBuilder(Bones.Length * 120 + 400);
        sb.Append("{\"FileExtension\":\".pose\",\"TypeName\":\"Brio Pose\",\"FileVersion\":3,")
          .Append("\"ModelDifference\":{\"Position\":\"0, 0, 0\",\"Rotation\":\"0, 0, 0, 1\",\"Scale\":\"0, 0, 0\"},")
          .Append("\"ModelAbsoluteValues\":{\"Position\":\"0, 0, 0\",\"Rotation\":\"0, 0, 0, 1\",\"Scale\":\"1, 1, 1\"},\"Bones\":{");
        for (var i = 0; i < Bones.Length; i++)
        {
            var o = i * 10;
            var pos = Vector3.Lerp(new Vector3(fa[o], fa[o + 1], fa[o + 2]), new Vector3(fb[o], fb[o + 1], fb[o + 2]), k);
            var qa = new Quaternion(fa[o + 3], fa[o + 4], fa[o + 5], fa[o + 6]);
            var qb = new Quaternion(fb[o + 3], fb[o + 4], fb[o + 5], fb[o + 6]);
            var q = Quaternion.Normalize(Quaternion.Slerp(qa, qb, k));
            var scale = Vector3.Lerp(new Vector3(fa[o + 7], fa[o + 8], fa[o + 9]), new Vector3(fb[o + 7], fb[o + 8], fb[o + 9]), k);
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Bones[i]).Append("\":{\"Position\":\"").Append(F(pos.X)).Append(", ").Append(F(pos.Y)).Append(", ").Append(F(pos.Z))
              .Append("\",\"Rotation\":\"").Append(F(q.X)).Append(", ").Append(F(q.Y)).Append(", ").Append(F(q.Z)).Append(", ").Append(F(q.W))
              .Append("\",\"Scale\":\"").Append(F(scale.X)).Append(", ").Append(F(scale.Y)).Append(", ").Append(F(scale.Z)).Append("\"}");
        }
        sb.Append("},\"MainHand\":{},\"OffHand\":{},\"Prop\":{},\"Ornament\":{},\"Position\":\"0, 0, 0\",\"Rotation\":\"0, 0, 0, 1\",\"Scale\":\"1, 1, 1\"}");
        return sb.ToString();
    }

    private static string F(float v) => v.ToString("0.#######", CultureInfo.InvariantCulture);

    /// <summary>第 t 秒某根骨骼的位置（模型系，相邻两帧插值）；没有这根骨骼时为原点。</summary>
    public Vector3 BoneAt(Clip clip, double t, string bone)
    {
        var i = Array.IndexOf(Bones, bone);
        if (i < 0 || Frames.Length == 0) return Vector3.Zero;
        var (a, b, k) = clip.Locate(t);
        var fa = Frames[Math.Min(a, Frames.Length - 1)];
        var fb = Frames[Math.Min(b, Frames.Length - 1)];
        var o = i * 10;
        return Vector3.Lerp(new Vector3(fa[o], fa[o + 1], fa[o + 2]), new Vector3(fb[o], fb[o + 1], fb[o + 2]), k);
    }
}

/// <summary>镜头（角色模型系，米）：位置、视线、上方向、垂直视角（度）。</summary>
public readonly record struct ClipCamera(Vector3 Position, Vector3 Forward, Vector3 Up, float FovY)
{
    public static ClipCamera Lerp(ClipCamera a, ClipCamera b, float k) =>
        new(Vector3.Lerp(a.Position, b.Position, k), Vector3.Normalize(Vector3.Lerp(a.Forward, b.Forward, k)),
            Vector3.Normalize(Vector3.Lerp(a.Up, b.Up, k)), a.FovY + (b.FovY - a.FovY) * k);
}

/// <summary>镜头解算的情况：Camera = depth（转动 + 平移）/ rotation（只有转动）/ none；Solved = 可信的步数占比；Breaks = 切镜时刻（秒，原视频时间）。</summary>
public sealed record ClipMotion(string Camera, double Solved, double[] Breaks);
