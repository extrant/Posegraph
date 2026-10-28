using System.Numerics;
using System.Text.Json.Serialization;

namespace Posegraph.Lighting;

/// <summary>
/// 一盏灯相对人物和镜头的摆法（和场景、站位无关，换地图换站位都能直接套）：
/// Azimuth：水平方向，0 = 从镜头那边照过来，正值偏向「脸转过去的那一侧」（窄光，最显脸型），90 = 正侧面，180 = 正后方；
/// Elevation：仰角（度，正 = 从上往下照）；Distance：离脸多远（米，按人族中原的身高，其他体型按比例缩放）；
/// Kelvin：色温；Rgb 给了就不用色温（霓虹这类不在色温线上的颜色）；Power：相对亮度（1 = 一盏正常的主光）。
/// AimYaw / AimPitch：灯头偏离「对准脸」的角度（度，左右 / 上下；0 = 正对着脸），在游戏里用旋转手柄调。
/// </summary>
public sealed record LightSpec(
    string Name, LightKind Kind, float Azimuth, float Elevation, float Distance, float Kelvin = 5500f, float Power = 1f,
    float SpotAngle = 45f, float Softness = 10f, bool Shadows = false, float Falloff = 1f, float[]? Rgb = null,
    float AimYaw = 0f, float AimPitch = 0f);

/// <summary>一套布光方案。</summary>
public sealed record LightRig(string Id, string Name, string Description, LightSpec[] Lights, bool Custom = false);

/// <summary>被照的人：脸的位置（世界）、脸朝向、头顶方向，以及体型比例（人族中原 = 1）。</summary>
public readonly record struct Subject(Vector3 Face, Vector3 Forward, Vector3 Up, float Scale);

/// <summary>全局微调：整体亮度、冷暖（色温加减）、灯距倍数。</summary>
public readonly record struct RigTweaks(float Brightness, float Warmth, float Distance)
{
    public static RigTweaks Default => new(1f, 0f, 1f);
}

public static class LightRigs
{
    /// <summary>主光亮度 1 时的颜色幅度。Brio 新建灯默认 20，游戏里实测这几套方案用 20 偏亮，取一半。</summary>
    public const float BaseBrightness = 10f;

    public static readonly LightRig[] Builtin =
    [
        new("loop", "环形光", "最常用的自然人像：主光略偏一侧，鼻子下有一小块阴影",
        [
            new("主光", LightKind.Spot, 35, 25, 1.8f, 5200, 1.0f, 50, 15, Shadows: true),
            new("补光", LightKind.Point, -45, 5, 2.2f, 5600, 0.3f),
            new("轮廓光", LightKind.Spot, 155, 35, 1.8f, 6000, 0.7f, 35, 10),
        ]),
        new("rembrandt", "伦勃朗光", "主光更偏更高，暗侧脸颊留一个光三角，有油画感",
        [
            new("主光", LightKind.Spot, 50, 40, 1.8f, 4800, 1.1f, 45, 12, Shadows: true),
            new("补光", LightKind.Point, -50, 0, 2.5f, 5200, 0.12f),
            new("轮廓光", LightKind.Spot, 160, 40, 2.0f, 5500, 0.4f, 35, 10),
        ]),
        new("butterfly", "蝴蝶光", "正面高处打下来，下面再补一点，脸干净、显精致（美妆、写真）",
        [
            new("主光", LightKind.Spot, 0, 45, 1.6f, 5600, 1.0f, 55, 20, Shadows: true),
            new("下补光", LightKind.Flat, 0, -25, 1.4f, 5600, 0.35f, 60, 25),
            new("轮廓光", LightKind.Point, 180, 30, 1.5f, 6000, 0.4f),
        ]),
        new("split", "分割光", "光从正侧面来，半边脸亮半边暗，冷峻、有张力",
        [
            new("主光", LightKind.Spot, 90, 5, 1.6f, 5000, 1.0f, 40, 8, Shadows: true),
            new("轮廓光", LightKind.Spot, -150, 25, 1.8f, 6500, 0.5f, 30, 8),
        ]),
        new("highkey", "高调柔光", "大面积柔光四面包住，几乎没有阴影，明亮清新",
        [
            new("主光", LightKind.Flat, 15, 15, 2.0f, 6000, 0.9f, 70, 30),
            new("左补光", LightKind.Flat, -60, 10, 2.0f, 6000, 0.5f, 70, 30),
            new("右补光", LightKind.Flat, 75, 10, 2.0f, 6000, 0.45f, 70, 30),
            new("轮廓光", LightKind.Point, 180, 40, 1.6f, 6500, 0.6f),
        ]),
        new("lowkey", "低调戏剧", "一盏窄光从侧上方打下，其余沉在暗里，加一道冷轮廓",
        [
            new("主光", LightKind.Spot, 70, 45, 1.8f, 4500, 1.2f, 30, 6, Shadows: true),
            new("轮廓光", LightKind.Spot, -160, 30, 1.8f, 6000, 0.8f, 25, 6),
        ]),
        new("golden", "黄昏暖光", "低角度暖色主光加冷色补光和暖轮廓，像傍晚的太阳",
        [
            new("主光", LightKind.Spot, 65, 10, 2.2f, 2800, 1.2f, 55, 25, Shadows: true),
            new("冷补光", LightKind.Point, -60, 15, 2.5f, 9000, 0.3f),
            new("暖轮廓光", LightKind.Spot, 150, 15, 2.0f, 2600, 0.9f, 40, 15),
        ]),
        new("moon", "月光", "冷蓝色的高处主光和明亮的冷轮廓，安静的夜景",
        [
            new("主光", LightKind.Spot, -55, 50, 2.2f, 9500, 0.6f, 45, 20, Shadows: true),
            new("轮廓光", LightKind.Spot, 160, 35, 2.0f, 11000, 1.0f, 35, 10),
            new("环境光", LightKind.Point, 0, 0, 3.0f, 9000, 0.12f),
        ]),
        new("neon", "霓虹双色", "两侧一洋红一青色，赛博朋克、夜店感",
        [
            new("洋红", LightKind.Point, 80, 5, 1.2f, Power: 0.9f, Rgb: [1f, 0.15f, 0.75f]),
            new("青色", LightKind.Point, -80, 5, 1.2f, Power: 0.9f, Rgb: [0.1f, 0.85f, 1f]),
            new("正面弱光", LightKind.Point, 0, 10, 2.5f, 6500, 0.15f),
        ]),
        new("backlight", "逆光", "两盏灯从背后勾出轮廓，正面只留一点，剪影感",
        [
            new("左轮廓光", LightKind.Spot, 140, 30, 1.8f, 5500, 1.2f, 40, 10),
            new("右轮廓光", LightKind.Spot, -140, 30, 1.8f, 5500, 1.2f, 40, 10),
            new("正面补光", LightKind.Flat, 0, 5, 2.5f, 5500, 0.2f, 60, 25),
        ]),
        new("candle", "烛光", "低处一盏近距离暖光，衰减快，远处一点暖色环境",
        [
            new("烛光", LightKind.Point, 25, -15, 0.7f, 1900, 0.8f, Falloff: 2f),
            new("暖环境光", LightKind.Point, -70, 20, 3.0f, 2400, 0.15f),
        ]),
    ];

    /// <summary>按人物和镜头位置算出每盏灯的世界状态。camera 为 null 时把脸朝向当成镜头方向。</summary>
    public static List<LightState> Solve(IEnumerable<LightSpec> lights, Subject subject, Vector3? camera, RigTweaks tweaks)
    {
        var up = Vector3.UnitY;
        var (toCamera, camRight, side) = Frame(subject, camera);
        var result = new List<LightState>();
        foreach (var l in lights)
        {
            var az = l.Azimuth * MathF.PI / 180f * side;
            var el = l.Elevation * MathF.PI / 180f;
            var horizontal = toCamera * MathF.Cos(az) + camRight * MathF.Sin(az);
            var dir = Vector3.Normalize(horizontal * MathF.Cos(el) + up * MathF.Sin(el));
            var distance = l.Distance * subject.Scale * tweaks.Distance;
            var position = subject.Face + dir * distance;
            var color = (l.Rgb is { Length: 3 } rgb ? new Vector3(rgb[0], rgb[1], rgb[2]) : KelvinToRgb(l.Kelvin + tweaks.Warmth))
                        * (BaseBrightness * l.Power * tweaks.Brightness);
            result.Add(new LightState(l.Kind, position, LookRotation(Aim(subject.Face - position, l.AimYaw, l.AimPitch)), color,
                                      MathF.Max(distance * 2.5f + 2f, 5f), l.SpotAngle, l.Softness, l.Shadows, l.Falloff));
        }
        return result;
    }

    /// <summary>摆灯用的水平参考：朝镜头的方向、镜头右手方向，以及窄光在哪一侧（+1 / -1）。</summary>
    private static (Vector3 ToCamera, Vector3 CamRight, float Side) Frame(Subject subject, Vector3? camera)
    {
        var toCamera = Flat(camera is { } c ? c - subject.Face : subject.Forward, subject.Forward);
        var camRight = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toCamera));
        // 窄光：主光放在脸转过去的那一侧；脸几乎正对镜头时默认画面右侧
        var side = Vector3.Dot(Flat(subject.Forward, toCamera), camRight) < -0.05f ? -1f : 1f;
        return (toCamera, camRight, side);
    }

    /// <summary>Solve 的反算：灯被拖到世界坐标 position 后，对应的方位、仰角、距离（度、度、米，未乘体型和灯距倍数）。</summary>
    public static (float Azimuth, float Elevation, float Distance) Place(Vector3 position, Subject subject, Vector3? camera, RigTweaks tweaks)
    {
        var (toCamera, camRight, side) = Frame(subject, camera);
        var offset = position - subject.Face;
        var length = MathF.Max(offset.Length(), 0.05f);
        var dir = offset / length;
        var elevation = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) * 180f / MathF.PI;
        var azimuth = MathF.Atan2(Vector3.Dot(dir, camRight), Vector3.Dot(dir, toCamera)) * 180f / MathF.PI * side;
        return (azimuth, elevation, length / MathF.Max(subject.Scale * tweaks.Distance, 0.01f));
    }

    /// <summary>对准方向 toFace 再偏 yaw（左右）/ pitch（上下）度后的方向。</summary>
    public static Vector3 Aim(Vector3 toFace, float yaw, float pitch)
    {
        if (yaw == 0 && pitch == 0) return toFace;
        var (x, y, z) = Axes(toFace);
        var (sy, cy) = MathF.SinCos(yaw * MathF.PI / 180f);
        var (sp, cp) = MathF.SinCos(pitch * MathF.PI / 180f);
        return x * (sy * cp) + y * sp + z * (cy * cp);
    }

    /// <summary>Aim 的反算：灯头实际朝 forward 时，相对「对准脸」偏了多少（度）。</summary>
    public static (float Yaw, float Pitch) AimOf(Vector3 toFace, Vector3 forward)
    {
        var (x, y, z) = Axes(toFace);
        var f = Vector3.Normalize(forward);
        return (MathF.Atan2(Vector3.Dot(f, x), Vector3.Dot(f, z)) * 180f / MathF.PI,
                MathF.Asin(Math.Clamp(Vector3.Dot(f, y), -1f, 1f)) * 180f / MathF.PI);
    }

    /// <summary>LookRotation 的三根轴（右、上、前）。</summary>
    private static (Vector3 X, Vector3 Y, Vector3 Z) Axes(Vector3 forward)
    {
        var z = Vector3.Normalize(forward);
        var x = Vector3.Cross(Vector3.UnitY, z);
        x = x.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(x);
        return (x, Vector3.Cross(z, x), z);
    }

    /// <summary>朝向：灯沿自身 +Z 发光，让 +Z 指向 forward、头顶尽量朝上。</summary>
    public static Quaternion LookRotation(Vector3 forward)
    {
        var z = Vector3.Normalize(forward);
        var x = Vector3.Cross(Vector3.UnitY, z);
        x = x.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(x);
        var y = Vector3.Cross(z, x);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>色温 → 线性 RGB（最大分量 1）。近似公式，1000K～40000K。</summary>
    public static Vector3 KelvinToRgb(float kelvin)
    {
        var t = Math.Clamp(kelvin, 1000f, 40000f) / 100f;
        float r, g, b;
        if (t <= 66)
        {
            r = 255;
            g = 99.4708025861f * MathF.Log(t) - 161.1195681661f;
            b = t <= 19 ? 0 : 138.5177312231f * MathF.Log(t - 10) - 305.0447927307f;
        }
        else
        {
            r = 329.698727446f * MathF.Pow(t - 60, -0.1332047592f);
            g = 288.1221695283f * MathF.Pow(t - 60, -0.0755148492f);
            b = 255;
        }
        var srgb = new Vector3(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255)) / 255f;
        var linear = new Vector3(MathF.Pow(srgb.X, 2.2f), MathF.Pow(srgb.Y, 2.2f), MathF.Pow(srgb.Z, 2.2f));
        return linear / MathF.Max(linear.X, MathF.Max(linear.Y, linear.Z));
    }

    private static Vector3 Flat(Vector3 v, Vector3 fallback)
    {
        var f = new Vector3(v.X, 0, v.Z);
        if (f.LengthSquared() > 1e-6f) return Vector3.Normalize(f);
        var g = new Vector3(fallback.X, 0, fallback.Z);
        return g.LengthSquared() > 1e-6f ? Vector3.Normalize(g) : Vector3.UnitZ;
    }
}
