using System.Numerics;
using System.Text.Json.Nodes;

namespace Posegraph.Video;

/// <summary>自动镜头（平滑跟拍、定点摇镜、拍不到人时换上的自动镜头）对准角色的哪里。</summary>
public enum ClipTarget
{
    Head,
    Chest,
    Hips,
    Body,
}

/// <summary>
/// 用户对镜头的整体调整（每个片段各存一份，在片段文件 meta.view 里）：
/// 环绕（机位绕中心点转）→ 距离（机位往中心点推近 / 拉远）→ 平移（沿镜头的左右、上下）→ 摇镜（镜头原地转、倾斜）→ 变焦（只改视角）。
/// 中心点：自动镜头用跟踪点；原运镜、固定机位、跟拍模式用「镜头正前方、角色所在深度的那一点」。
/// 跟踪点、全身入画只影响自动镜头。
/// </summary>
public sealed record ClipView
{
    public float Distance { get; init; } = 1f;   // 机位到中心点的距离倍数
    public float Zoom { get; init; } = 1f;       // 变焦倍数（>1 视角变窄、画面放大）
    public float OrbitYaw { get; init; }         // 环绕：左右（度，正 = 机位往右绕）
    public float OrbitPitch { get; init; }       // 环绕：上下（度，正 = 机位抬高往下拍）
    public float ShiftX { get; init; }           // 平移：沿镜头的右方向（米）
    public float ShiftY { get; init; }           // 平移：沿镜头的上方向（米）
    public float Pan { get; init; }              // 摇镜：镜头原地往右看（度）
    public float Tilt { get; init; }             // 摇镜：镜头原地往上看（度）
    public float Roll { get; init; }             // 倾斜（度，正 = 画面顺时针歪：镜头的上方向往左倒）
    public ClipTarget Target { get; init; } = ClipTarget.Chest;
    public bool FitBody { get; init; }           // 全身入画：自动镜头按整个人的范围留距离，举手踢腿时退一点

    public static readonly ClipView Default = new();

    /// <summary>镜头位置、朝向有没有被调整过（跟踪点、全身入画不算）。</summary>
    public bool Moves => Distance != 1f || Zoom != 1f || OrbitYaw != 0 || OrbitPitch != 0 || ShiftX != 0 || ShiftY != 0
                         || Pan != 0 || Tilt != 0 || Roll != 0;

    public JsonObject ToJson() => new()
    {
        ["distance"] = Distance, ["zoom"] = Zoom, ["orbitYaw"] = OrbitYaw, ["orbitPitch"] = OrbitPitch,
        ["shiftX"] = ShiftX, ["shiftY"] = ShiftY, ["pan"] = Pan, ["tilt"] = Tilt, ["roll"] = Roll,
        ["target"] = Target.ToString().ToLowerInvariant(), ["fitBody"] = FitBody,
    };

    public static ClipView FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return Default;
        float F(string key, float fallback) => o[key] is { } v ? (float)(double)v : fallback;
        return new ClipView
        {
            Distance = F("distance", 1f), Zoom = F("zoom", 1f), OrbitYaw = F("orbitYaw", 0), OrbitPitch = F("orbitPitch", 0),
            ShiftX = F("shiftX", 0), ShiftY = F("shiftY", 0), Pan = F("pan", 0), Tilt = F("tilt", 0), Roll = F("roll", 0),
            Target = Enum.TryParse<ClipTarget>((string?)o["target"], true, out var t) ? t : ClipTarget.Chest,
            FitBody = (bool?)o["fitBody"] ?? false,
        };
    }

    /// <summary>
    /// 按调整改镜头（世界坐标）：position / forward / up / fovY（度），pivot = 中心点，worldUp = 世界的上方向。
    /// </summary>
    public void Apply(ref Vector3 position, ref Vector3 forward, ref Vector3 up, ref float fovY, Vector3 pivot, Vector3 worldUp)
    {
        if (!Moves) return;
        const float rad = MathF.PI / 180f;
        forward = Vector3.Normalize(forward);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        up = Vector3.Cross(right, forward);

        // 环绕：绕世界上方向（左右）、绕镜头右方向（上下）转机位和朝向，中心点留在画面同一处
        var orbit = Quaternion.CreateFromAxisAngle(worldUp, OrbitYaw * rad);
        var pitchAxis = Vector3.Transform(right, orbit);
        orbit = Quaternion.CreateFromAxisAngle(pitchAxis, -OrbitPitch * rad) * orbit;
        position = pivot + Vector3.Transform(position - pivot, orbit);
        forward = Vector3.Transform(forward, orbit);
        up = Vector3.Transform(up, orbit);
        right = Vector3.Transform(right, orbit);

        position = pivot + (position - pivot) * Math.Clamp(Distance, 0.05f, 20f);
        position += right * ShiftX + up * ShiftY;

        // 摇镜：机位不动，镜头往右（绕世界上方向）、往上（绕镜头右方向）转，再绕视线倾斜
        var head = Quaternion.CreateFromAxisAngle(worldUp, -Pan * rad);
        right = Vector3.Transform(right, head);
        head = Quaternion.CreateFromAxisAngle(right, Tilt * rad) * head;
        forward = Vector3.Normalize(Vector3.Transform(forward, head));
        up = Vector3.Transform(up, head);
        up = Vector3.Normalize(Vector3.Transform(up, Quaternion.CreateFromAxisAngle(forward, -Roll * rad)));

        var half = MathF.Tan(fovY * rad / 2) / Math.Clamp(Zoom, 0.1f, 10f);
        fovY = 2 * MathF.Atan(half) / rad;
    }
}

/// <summary>
/// 自动镜头的取景数据（第一帧角色的模型系，已乘走位）：每帧的跟踪点（平滑后）、胸口（平滑后，后端轨道按它算的）、
/// 全身中心和半径（全身入画用）。换片段、换跟踪点时重算一次（几毫秒）。
/// </summary>
public sealed class ClipFraming
{
    private const double AimSigmaSeconds = 0.25;   // 同后端 motion.AIM_SIGMA_SECONDS
    private const double RadiusSigmaSeconds = 0.4;
    public const float FitMargin = 1.15f;          // 全身入画多留 15%

    public required ClipTarget Target { get; init; }
    public required Vector3[] Aim { get; init; }       // 跟踪点
    public required Vector3[] Chest { get; init; }     // 胸口（j_sebo_c）
    public required Vector3[] RawChest { get; init; }  // 胸口（没平滑，中心点用）
    public required Vector3[] Center { get; init; }    // 全身中心
    public required float[] Radius { get; init; }      // 全身半径（米）

    public static ClipFraming Build(Clip clip, ClipTarget target)
    {
        var person = clip.People[0];
        var n = person.Frames.Length;
        int Index(string name) => Array.IndexOf(person.Bones, name);
        var body = Enumerable.Range(0, person.Bones.Length)
                             .Where(i => person.Bones[i].StartsWith("j_") && !person.Bones[i].StartsWith("j_f_")).ToArray();
        int head = Index("j_kao"), chest = Index("j_sebo_c"), hips = Index("j_kosi");
        var aim = new Vector3[n];
        var chestArr = new Vector3[n];
        var center = new Vector3[n];
        var radius = new float[n];
        for (var k = 0; k < n; k++)
        {
            var f = person.Frames[k];
            Vector3 Pos(int i) => i < 0 ? Vector3.Zero : new Vector3(f[i * 10], f[i * 10 + 1], f[i * 10 + 2]);
            float top = float.MinValue, bottom = float.MaxValue;
            foreach (var i in body)
            {
                var y = f[i * 10 + 1];
                top = MathF.Max(top, y);
                bottom = MathF.Min(bottom, y);
            }
            top += 0.12f;   // 头顶在 j_kao 上面
            var hip = Pos(hips);
            var c = new Vector3(hip.X, (top + bottom) / 2, hip.Z);
            var r = (top - bottom) / 2;
            foreach (var i in body) r = MathF.Max(r, Vector3.Distance(Pos(i), c) + 0.1f);
            var p = target switch
            {
                ClipTarget.Head => Pos(head) + new Vector3(0, 0.06f, 0),
                ClipTarget.Hips => hip,
                ClipTarget.Body => c,
                _ => Pos(chest),
            };
            var root = clip.Root.Length == n ? clip.Root[k] : Vector4.Zero;
            aim[k] = Walk(p, root);
            chestArr[k] = Walk(Pos(chest), root);
            center[k] = Walk(c, root);
            radius[k] = r;
        }
        var sigma = AimSigmaSeconds * clip.Fps;
        return new ClipFraming
        {
            Target = target, Aim = Smooth(aim, sigma), Chest = Smooth(chestArr, sigma), RawChest = chestArr,
            Center = Smooth(center, sigma), Radius = SmoothMax(radius, RadiusSigmaSeconds * clip.Fps),
        };
    }

    /// <summary>模型系里的点 → 第一帧角色的模型系（乘这一帧的走位：绕 y 转 yaw 再平移）。</summary>
    private static Vector3 Walk(Vector3 p, Vector4 root)
    {
        var (c, s) = (MathF.Cos(root.W), MathF.Sin(root.W));
        return new Vector3(c * p.X + s * p.Z + root.X, p.Y + root.Y, -s * p.X + c * p.Z + root.Z);
    }

    private static float[] Gaussian(double sigma)
    {
        var r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var k = Enumerable.Range(-r, 2 * r + 1).Select(x => (float)Math.Exp(-0.5 * x * x / (sigma * sigma))).ToArray();
        var sum = k.Sum();
        return k.Select(v => v / sum).ToArray();
    }

    private static Vector3[] Smooth(Vector3[] v, double sigma)
    {
        if (sigma <= 0 || v.Length < 2) return v;
        var k = Gaussian(sigma);
        var r = k.Length / 2;
        var o = new Vector3[v.Length];
        for (var i = 0; i < v.Length; i++)
            for (var j = 0; j < k.Length; j++)
                o[i] += v[Math.Clamp(i + j - r, 0, v.Length - 1)] * k[j];
        return o;
    }

    /// <summary>半径：先取前后 sigma 窗口的最大值（举手的那一下也要留够），再平滑。</summary>
    private static float[] SmoothMax(float[] v, double sigma)
    {
        var w = Math.Max(1, (int)Math.Round(sigma));
        var m = v.Select((_, i) => Enumerable.Range(Math.Max(0, i - w), Math.Min(v.Length, i + w + 1) - Math.Max(0, i - w)).Max(j => v[j])).ToArray();
        var k = Gaussian(sigma);
        var r = k.Length / 2;
        var o = new float[v.Length];
        for (var i = 0; i < v.Length; i++)
            for (var j = 0; j < k.Length; j++)
                o[i] += m[Math.Clamp(i + j - r, 0, v.Length - 1)] * k[j];
        return o;
    }

    public Vector3 AimAt(Clip clip, double t) => Lerp(Aim, clip, t);
    public Vector3 ChestAt(Clip clip, double t) => Lerp(Chest, clip, t);
    public Vector3 RawChestAt(Clip clip, double t) => Lerp(RawChest, clip, t);
    public Vector3 CenterAt(Clip clip, double t) => Lerp(Center, clip, t);

    public float RadiusAt(Clip clip, double t)
    {
        var (a, b, k) = clip.Locate(t);
        return Radius[Math.Min(a, Radius.Length - 1)] + (Radius[Math.Min(b, Radius.Length - 1)] - Radius[Math.Min(a, Radius.Length - 1)]) * k;
    }

    private static Vector3 Lerp(Vector3[] v, Clip clip, double t)
    {
        var (a, b, k) = clip.Locate(t);
        return Vector3.Lerp(v[Math.Min(a, v.Length - 1)], v[Math.Min(b, v.Length - 1)], k);
    }
}
