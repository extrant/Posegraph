using System.Numerics;
using System.Text.Json.Serialization;
using Posegraph.Backend;

namespace Posegraph.Lighting;

/// <summary>
/// 一盏灯相对人物和镜头的摆法（和场景、站位无关，换地图换站位都能直接套）：
/// Azimuth：水平方向，0 = 从镜头那边照过来，正值偏向「脸转过去的那一侧」（窄光，最显脸型），90 = 正侧面，180 = 正后方；
/// Elevation：仰角（度，正 = 从上往下照）；Distance：离脸多远（米，按人族中原的身高，其他体型按比例缩放）；
/// Kelvin：色温；Rgb 给了就不用色温（霓虹这类不在色温线上的颜色）；Power：相对亮度（1 = 一盏正常的主光）。
/// AimYaw / AimPitch：灯头偏离「对准脸」的角度（度，左右 / 上下；0 = 正对着脸），在游戏里用旋转手柄调。
/// Effect：动态灯效（见 LightEffect），EffectSpeed 快慢倍数，EffectAmount 幅度 0～1。
/// </summary>
public sealed record LightSpec(
    string Name, LightKind Kind, float Azimuth, float Elevation, float Distance, float Kelvin = 5500f, float Power = 1f,
    float SpotAngle = 45f, float Softness = 10f, bool Shadows = false, float Falloff = 1f, float[]? Rgb = null,
    float AimYaw = 0f, float AimPitch = 0f, LightEffect Effect = LightEffect.None, float EffectSpeed = 1f, float EffectAmount = 0.5f);

/// <summary>动态灯效：闪烁（烛火、坏灯管）、呼吸（亮暗缓慢起伏）、环绕（绕着脸转圈）、变色（色相循环，霓虹）。</summary>
public enum LightEffect
{
    None,
    Flicker,
    Pulse,
    Orbit,
    Hue,
}

/// <summary>一套布光方案。</summary>
public sealed record LightRig(string Id, string Name, string Description, LightSpec[] Lights, bool Custom = false);

/// <summary>
/// 被照的人：脸的位置（世界）、脸朝向、头顶方向，以及体型比例（人族中原 = 1）。Pelvis / Feet：腰和脚（世界），算打光范围用。
/// Center / Reach / DistanceScale：打光范围（见 LightFraming、LightRigs.Framed）——灯围着 Center 摆、对准 Center，
/// 距离再乘 DistanceScale，聚光的光斑至少罩住半径 Reach（米）；默认就是脸。
/// </summary>
public readonly record struct Subject(Vector3 Face, Vector3 Forward, Vector3 Up, float Scale)
{
    public Vector3? Pelvis { get; init; }
    public Vector3? Feet { get; init; }
    public Vector3 Center { get; init; } = Face;
    public float Reach { get; init; } = FaceReach * Scale;
    public float DistanceScale { get; init; } = 1f;

    public const float FaceReach = 0.15f;
}

/// <summary>打光范围：脸（头像、特写）、半身（腰以上）、全身。</summary>
public enum LightFraming
{
    Face,
    Half,
    Full,
}

/// <summary>全局微调：整体亮度、冷暖（色温加减）、灯距倍数、补光倍数（只乘在补光上，见 LightRigs.IsFill；自动调亮度按场景光算）。</summary>
public readonly record struct RigTweaks(float Brightness, float Warmth, float Distance, float Fill = 1f)
{
    public static RigTweaks Default => new(1f, 0f, 1f, 1f);
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
        new("candle", "烛光", "低处一盏近距离暖光，衰减快，远处一点暖色环境，火苗轻轻跳动",
        [
            new("烛光", LightKind.Point, 25, -15, 0.7f, 1900, 0.8f, Falloff: 2f, Effect: LightEffect.Flicker, EffectAmount: 0.3f),
            new("暖环境光", LightKind.Point, -70, 20, 3.0f, 2400, 0.15f),
        ]),
    ];

    public const string PhotoRigId = "photo";

    /// <summary>
    /// 照片同款：按照片估出的灯（backend lighting.py）搭一套方案。主光用带投影的聚光，软硬按估出的柔和度；
    /// 估出第二盏时照样加上（在背后的叫轮廓光）；照片里的环境光换成主光对侧的一盏补光，颜色见 FillColor；
    /// 背景不暗时加一盏从人背后朝外照的背景光，颜色用照片背景的颜色（照片配色）。
    /// 光比接近 1:1 时方向不可信，换成正面的大面积柔光。距离照片里看不出来，用常见的摆法。
    /// </summary>
    public static LightRig FromPhoto(PhotoLighting photo)
    {
        var lights = photo.Lights ?? [];
        var specs = new List<LightSpec>();
        var flat = photo.Flat || lights.Length == 0;
        if (flat)
        {
            var color = lights.Length > 0 ? lights[0].Color : null;
            specs.Add(Colored(new("柔光", LightKind.Flat, 0, 15, 2.0f, 5500, 0.9f, 70, 30), color));
            specs.Add(Colored(new("补光", LightKind.Point, -50, 5, 2.4f, 5500, 0.3f), FillColor(photo) ?? color, vivid: true));
        }
        else
        {
            var key = lights[0];
            specs.Add(Colored(new("主光", LightKind.Spot, key.Azimuth, Math.Clamp(key.Elevation, -60f, 85f), 1.8f, 5500, 1f,
                                  50, 8 + Math.Clamp(key.Softness, 0f, 1.5f) * 20, Shadows: true), key.Color));
            foreach (var l in lights.Skip(1))
                specs.Add(Colored(new(MathF.Abs(l.Azimuth) > 110 ? "轮廓光" : "第二盏灯", LightKind.Spot, l.Azimuth,
                                      Math.Clamp(l.Elevation, -60f, 85f), 1.8f, 5500, Math.Clamp(l.Power, 0.15f, 1.5f), 40,
                                      8 + Math.Clamp(l.Softness, 0f, 1.5f) * 20), l.Color, vivid: MathF.Abs(l.Azimuth) > 110));
            specs.Add(Colored(new("补光", LightKind.Point, key.Azimuth >= 0 ? -45 : 45, 5, 2.2f, 5500,
                                  Math.Clamp(photo.Ambient * 0.8f, 0.05f, 0.6f)), FillColor(photo) ?? key.Color, vivid: true));
        }
        if (photo.Background is { Brightness: >= BackgroundMinBrightness } bg)
            specs.Add(Colored(BackgroundLight(bg.Brightness), bg, vivid: true));
        var description = flat
            ? Loc.T("照片里是均匀的正面光（光比接近 1:1）：用正面柔光")
            : Loc.F("按照片估的光：主光方位 {0:0}°、仰角 {1:0}°，光比约 {2:0.#}:1", lights[0].Azimuth, lights[0].Elevation, photo.Ratio);
        if (photo.Source == "body") description = Loc.T("照片里没有脸，按身上露出的皮肤估的。") + description;
        if (photo.Source == "coarse") description = Loc.T("照片里没有脸、露出的皮肤也少，按衣服的明暗强制估的，只当个大概。") + description;
        return new LightRig(PhotoRigId, "照片同款", description, [.. specs]);
    }

    /// <summary>照片背景比脸暗这么多以下（亮度比）就不加背景光：暗背景本来就没光照着。</summary>
    private const float BackgroundMinBrightness = 0.25f;

    /// <summary>背景光：在人背后一点、灯头朝外（背离镜头）照向身后的墙或地面，亮度跟照片背景相对脸的亮度走。</summary>
    private static LightSpec BackgroundLight(float brightness)
        => new("背景光", LightKind.Spot, 180, 15, 0.6f, 5500, Math.Clamp(brightness * 0.6f, 0.2f, 1.2f), 80, 30, AimYaw: 180);

    /// <summary>
    /// 补光的颜色：补光多是周围环境反射来的，照片有背景时用背景色往白里掺一半（环境不只是看得见的那块背景）；
    /// 没有背景时用估出的环境光颜色（暗部皮肤透光偏红，只当备用）。
    /// </summary>
    public static PhotoColor? FillColor(PhotoLighting photo)
    {
        if (photo.Background is not { } bg) return photo.AmbientColor;
        var rgb = bg.Rgb is { Length: 3 } c ? c.Select(v => (v + 1f) / 2f).ToArray() : null;
        return new PhotoColor((bg.Kelvin + 5500f) / 2f, rgb, bg.Tinted);
    }

    /// <summary>
    /// 按照片的颜色上色。主光照着脸，只在彩色（离色温线远）时用 RGB，否则用色温（「冷暖」微调对它有效）；
    /// vivid：补光、轮廓光、背景光直接用照片的 RGB 再加点饱和度（Vivid），不然淡蓝、淡橙这类颜色在游戏里几乎看不出来。
    /// </summary>
    private static LightSpec Colored(LightSpec spec, PhotoColor? color, bool vivid = false) => color switch
    {
        null => spec,
        { Rgb: { Length: 3 } rgb } when vivid => spec with { Rgb = Vivid(rgb) },
        { Rgb: { Length: 3 } rgb } when color.Tinted => spec with { Rgb = rgb },
        _ => spec with { Kelvin = color.Kelvin, Rgb = null },
    };

    /// <summary>照片的颜色在游戏里会被冲淡：离白色的距离放大 VividGain 倍（往纯色推），白、灰的还是白。</summary>
    private static float[] Vivid(float[] rgb) => [.. rgb.Select(v => Math.Clamp(1f - (1f - v) * VividGain, 0f, 1f))];

    private const float VividGain = 1.5f;

    /// <summary>两盏灯的颜色是否一样（色温和 RGB）。</summary>
    public static bool SameColor(LightSpec a, LightSpec b)
        => MathF.Abs(a.Kelvin - b.Kelvin) < 1 && (a.Rgb, b.Rgb) switch
        {
            (null, null) => true,
            ({ } x, { } y) => x.Length == y.Length && x.Zip(y).All(t => MathF.Abs(t.First - t.Second) < 0.005f),
            _ => false,
        };

    /// <summary>
    /// 照片配色：不动灯的位置，只按照片给现有方案的灯换颜色——第一盏（主光）用照片主光的颜色，在背后的（轮廓光）用照片背景色，
    /// 其余（补光等）用补光的颜色（FillColor）；照片背景不暗、方案里又没有背景光时加一盏。
    /// </summary>
    public static List<LightSpec> WithPhotoColors(IReadOnlyList<LightSpec> lights, PhotoLighting photo)
    {
        var key = photo.Lights is { Length: > 0 } l ? l[0].Color : null;
        var ambient = FillColor(photo) ?? key;
        var result = new List<LightSpec>();
        for (var i = 0; i < lights.Count; i++)
        {
            var spec = lights[i];
            result.Add(i == 0 ? Colored(spec, key) : Colored(spec, MathF.Abs(spec.Azimuth) > 110 ? photo.Background ?? ambient : ambient, vivid: true));
        }
        if (photo.Background is { Brightness: >= BackgroundMinBrightness } bg && lights.All(s => s.Name != "背景光"))
            result.Add(Colored(BackgroundLight(bg.Brightness), bg, vivid: true));
        return result;
    }

    /// <summary>
    /// 按打光范围换灯的中心：脸 = 脸；半身 = 脸和腰之间；全身 = 脸到脚之间偏上（脸比脚要紧）。
    /// 范围越大灯离得越远（头和脚离灯的远近差得少，光更均匀），聚光的光斑放宽到罩得住 Reach。读不到腰 / 脚时退回照脸。
    /// </summary>
    public static Subject Framed(Subject s, LightFraming framing) => (framing, s.Pelvis, s.Feet) switch
    {
        (LightFraming.Half, { } pelvis, _) => s with
        {
            Center = Vector3.Lerp(s.Face, pelvis, 0.45f), Reach = Vector3.Distance(s.Face, pelvis) * 0.65f + 0.1f * s.Scale,
            DistanceScale = 1.3f,
        },
        (LightFraming.Full, _, { } feet) => s with
        {
            Center = Vector3.Lerp(s.Face, feet, 0.42f), Reach = Vector3.Distance(s.Face, feet) * 0.62f + 0.15f * s.Scale,
            DistanceScale = 1.8f,
        },
        _ => s with { Center = s.Face, Reach = Subject.FaceReach * s.Scale, DistanceScale = 1f },
    };

    /// <summary>按人物和镜头位置算出每盏灯的世界状态。camera 为 null 时把脸朝向当成镜头方向。</summary>
    public static List<LightState> Solve(IEnumerable<LightSpec> lights, Subject subject, Vector3? camera, RigTweaks tweaks, float time = 0)
    {
        var up = Vector3.UnitY;
        var (toCamera, camRight, side) = Frame(subject, camera);
        var result = new List<LightState>();
        var index = 0;
        foreach (var spec in lights)
        {
            var i = index++;
            var (l, gain) = Animate(spec, time, i);
            if (IsFill(spec, i)) gain *= tweaks.Fill;
            var az = l.Azimuth * MathF.PI / 180f * side;
            var el = l.Elevation * MathF.PI / 180f;
            var horizontal = toCamera * MathF.Cos(az) + camRight * MathF.Sin(az);
            var dir = Vector3.Normalize(horizontal * MathF.Cos(el) + up * MathF.Sin(el));
            var distance = l.Distance * subject.Scale * tweaks.Distance * subject.DistanceScale;
            var position = subject.Center + dir * distance;
            // 范围大时灯拉远了：按平方衰减补回来，脸上的亮度和照脸时差不多
            var color = (l.Rgb is { Length: 3 } rgb ? new Vector3(rgb[0], rgb[1], rgb[2]) : KelvinToRgb(l.Kelvin + tweaks.Warmth))
                        * (BaseBrightness * l.Power * tweaks.Brightness * gain * subject.DistanceScale * subject.DistanceScale);
            // 光斑至少罩住要照的范围（照脸时几乎不影响，照全身时窄光会被放宽）
            var angle = l.Kind == LightKind.Point ? l.SpotAngle
                : MathF.Max(l.SpotAngle, 2 * MathF.Atan(subject.Reach * 1.1f / distance) * 180f / MathF.PI);
            result.Add(new LightState(l.Kind, position, LookRotation(Aim(subject.Center - position, l.AimYaw, l.AimPitch)), color,
                                      MathF.Max(distance * 2.5f + 2f, 5f), angle, l.Softness, l.Shadows, l.Falloff));
        }
        return result;
    }

    /// <summary>补光：第一盏（主光）以外、在脸前半圈、不是背景光的灯。整体微调里的「补光」和自动调亮度扣场景光只动这些。</summary>
    public static bool IsFill(LightSpec l, int index) => index > 0 && MathF.Abs(l.Azimuth) < 110 && l.Name != "背景光";

    /// <summary>
    /// 动态灯效在 time 秒时的样子：返回改过方位 / 颜色的灯和亮度倍数。index 错开各盏灯的相位，几盏一起闪时不会同步。
    /// </summary>
    public static (LightSpec Spec, float Gain) Animate(LightSpec l, float time, int index)
    {
        if (l.Effect == LightEffect.None || time <= 0) return (l, 1f);
        var t = time * Math.Clamp(l.EffectSpeed, 0.05f, 10f) + index * 1.7f;
        var amount = Math.Clamp(l.EffectAmount, 0f, 1f);
        switch (l.Effect)
        {
            case LightEffect.Flicker:
                // 几个不成倍数的正弦叠在一起：不规则的跳动（0～1）
                var n = (MathF.Sin(t * 7.3f) + MathF.Sin(t * 13.1f + 1f) + MathF.Sin(t * 23.7f + 2f) * 0.6f + 2.6f) / 5.2f;
                return (l, 1f - amount * n);
            case LightEffect.Pulse:
                return (l, 1f - amount * (0.5f - 0.5f * MathF.Cos(t * MathF.PI)));
            case LightEffect.Orbit:
                return (l with { Azimuth = l.Azimuth + t * 30f * (0.2f + amount) }, 1f);
            case LightEffect.Hue:
                var baseColor = l.Rgb is { Length: 3 } rgb ? new Vector3(rgb[0], rgb[1], rgb[2]) : KelvinToRgb(l.Kelvin);
                var hued = HueShift(baseColor, t * 60f, amount);
                return (l with { Rgb = [hued.X, hued.Y, hued.Z] }, 1f);
            default:
                return (l, 1f);
        }
    }

    /// <summary>颜色的色相转 degrees 度；amount 越大越往纯色靠（白光也能变出颜色）。最大分量保持 1。</summary>
    private static Vector3 HueShift(Vector3 c, float degrees, float amount)
    {
        var max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        var min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float h;
        if (max - min < 1e-4f) h = 0;
        else if (max == c.X) h = 60f * ((c.Y - c.Z) / (max - min) % 6f);
        else if (max == c.Y) h = 60f * ((c.Z - c.X) / (max - min) + 2f);
        else h = 60f * ((c.X - c.Y) / (max - min) + 4f);
        h = ((h + degrees) % 360f + 360f) % 360f;
        var sat = Math.Clamp(Math.Max((max - min) / MathF.Max(max, 1e-4f), amount), 0f, 1f);
        var x = 1f - MathF.Abs(h / 60f % 2f - 1f);
        var (r, g, b) = h switch
        {
            < 60 => (1f, x, 0f),
            < 120 => (x, 1f, 0f),
            < 180 => (0f, 1f, x),
            < 240 => (0f, x, 1f),
            < 300 => (x, 0f, 1f),
            _ => (1f, 0f, x),
        };
        return Vector3.Lerp(Vector3.One, new Vector3(r, g, b), sat);
    }

    /// <summary>摆灯用的水平参考：朝镜头的方向、镜头右手方向，以及窄光在哪一侧（+1 / -1）。</summary>
    private static (Vector3 ToCamera, Vector3 CamRight, float Side) Frame(Subject subject, Vector3? camera)
    {
        var toCamera = Flat(camera is { } c ? c - subject.Center : subject.Forward, subject.Forward);
        var camRight = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toCamera));
        // 窄光：主光放在脸转过去的那一侧；脸几乎正对镜头时默认画面右侧
        var side = Vector3.Dot(Flat(subject.Forward, toCamera), camRight) < -0.05f ? -1f : 1f;
        return (toCamera, camRight, side);
    }

    /// <summary>Solve 的反算：灯被拖到世界坐标 position 后，对应的方位、仰角、距离（度、度、米，未乘体型和灯距倍数）。</summary>
    public static (float Azimuth, float Elevation, float Distance) Place(Vector3 position, Subject subject, Vector3? camera, RigTweaks tweaks)
    {
        var (toCamera, camRight, side) = Frame(subject, camera);
        var offset = position - subject.Center;
        var length = MathF.Max(offset.Length(), 0.05f);
        var dir = offset / length;
        var elevation = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) * 180f / MathF.PI;
        var azimuth = MathF.Atan2(Vector3.Dot(dir, camRight), Vector3.Dot(dir, toCamera)) * 180f / MathF.PI * side;
        return (azimuth, elevation, length / MathF.Max(subject.Scale * tweaks.Distance * subject.DistanceScale, 0.01f));
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
