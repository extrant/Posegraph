using System.Numerics;
using Posegraph.Idle;
using Posegraph.Video;

namespace Posegraph.Dance;

/// <summary>走位怎么处理：Keep 照视频走（循环接缝处会瞬移回起点）；Drift 去掉慢慢漂走的部分、保留舞步里的小位移；InPlace 原地跳。</summary>
public enum RootMode { Keep, Drift, InPlace }

/// <summary>
/// 要烘焙的动作：每帧（30 帧/秒）每根骨骼的模型空间变换（角色朝 +z，走位已经并进去了）。
/// 帧数 N 的动画时长 = (N - 1) / 30 秒，时间轴总帧数也是 N - 1：接缝过渡打开时最后一帧和第 0 帧一样，循环无缝。
/// </summary>
public sealed class DanceMotion
{
    public const float Fps = 30f;

    public required string[] Bones { get; init; }

    /// <summary>[帧][骨骼]。</summary>
    public required BoneTransform[][] Frames { get; init; }

    public int Count => Frames.Length;

    /// <summary>时间轴总帧数（= 帧数 - 1）。</summary>
    public int TimelineFrames => Math.Max(1, Count - 1);

    public double Seconds => TimelineFrames / Fps;

    /// <summary>
    /// 从片段取 start~end 秒（片段时间）：重采样到 30 帧/秒（位置线性、旋转球面插值），按 rootMode 处理走位，
    /// 最后 seam 秒逐渐过渡回第 0 帧（0 = 不过渡）。
    /// </summary>
    public static DanceMotion FromClip(Clip clip, int person, double start, double end, RootMode rootMode, double seam)
        => Split(clip, person, start, end, rootMode, seam, null).Loop;

    /// <summary>
    /// 开场 + 循环：start~introEnd 只放一次（游戏先放开场 pap，放完接循环），introEnd~end 循环。走位整段一起处理，
    /// 开场的最后一帧就是循环的第 0 帧；接缝过渡只作用在循环上（最后回到 introEnd 那一帧）。introEnd 为 null 时没有开场。
    /// </summary>
    public static (DanceMotion? Intro, DanceMotion Loop) Split(Clip clip, int person, double start, double end, RootMode rootMode, double seam,
                                                             double? introEnd)
    {
        var p = clip.People[person];
        var bones = p.Bones;
        start = Math.Clamp(start, 0, clip.Duration);
        end = Math.Clamp(end, start, clip.Duration);
        var n = Math.Max(2, (int)Math.Round((end - start) * Fps) + 1);
        var roots = new Vector4[n];
        var frames = new BoneTransform[n][];
        for (var k = 0; k < n; k++)
        {
            var t = start + k / (double)Fps;
            var (a, b, w) = clip.Locate(t);
            frames[k] = new BoneTransform[bones.Length];
            var fa = p.Frames[a];
            var fb = p.Frames[b];
            for (var j = 0; j < bones.Length; j++)
            {
                var o = j * 10;
                var pa = new Vector3(fa[o], fa[o + 1], fa[o + 2]);
                var pb = new Vector3(fb[o], fb[o + 1], fb[o + 2]);
                var qa = new Quaternion(fa[o + 3], fa[o + 4], fa[o + 5], fa[o + 6]);
                var qb = new Quaternion(fb[o + 3], fb[o + 4], fb[o + 5], fb[o + 6]);
                var sa = new Vector3(fa[o + 7], fa[o + 8], fa[o + 9]);
                var sb = new Vector3(fb[o + 7], fb[o + 8], fb[o + 9]);
                frames[k][j] = new BoneTransform(Vector3.Lerp(pa, pb, w), Quaternion.Normalize(Quaternion.Slerp(qa, qb, w)), Vector3.Lerp(sa, sb, w));
            }
            roots[k] = person == 0 ? clip.RootAt(t) : Vector4.Zero;
        }

        // 走位：从这段的第 0 帧算起
        var r0 = roots[0];
        for (var k = 0; k < n; k++) roots[k] -= new Vector4(r0.X, 0, r0.Z, r0.W);
        if (rootMode == RootMode.InPlace)
            for (var k = 0; k < n; k++) roots[k] = new Vector4(0, 0, 0, roots[k].W);
        else if (rootMode == RootMode.Drift)
            RemoveDrift(roots, sigmaSeconds: 1.5);

        // 走位并进骨骼：先绕 y 转向、再平移（和播放器摆角色的顺序一样）
        for (var k = 0; k < n; k++)
        {
            var r = roots[k];
            if (r == Vector4.Zero) continue;
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.W);
            var move = new Vector3(r.X, 0, r.Z);
            for (var j = 0; j < bones.Length; j++)
            {
                var b = frames[k][j];
                frames[k][j] = b with { Translation = Vector3.Transform(b.Translation, q) + move, Rotation = Quaternion.Normalize(q * b.Rotation) };
            }
        }

        DanceMotion? intro = null;
        if (introEnd is { } ie)
        {
            var cut = Math.Clamp((int)Math.Round((ie - start) * Fps), 1, n - 2);
            intro = new DanceMotion { Bones = bones, Frames = frames[..(cut + 1)] };
            frames = frames[cut..];
            n = frames.Length;
        }

        // 接缝：最后 seam 秒逐渐变成循环的第 0 帧（模型空间逐骨骼插值），最后一帧 = 第 0 帧
        var blend = Math.Min(n - 1, (int)Math.Round(seam * Fps));
        if (blend > 0)
            for (var k = n - blend; k < n; k++)
            {
                var x = (k - (n - 1 - blend)) / (float)blend;   // (0, 1]
                var w = x * x * (3 - 2 * x);
                for (var j = 0; j < bones.Length; j++)
                {
                    var a = frames[k][j];
                    var z = frames[0][j];
                    frames[k][j] = new BoneTransform(Vector3.Lerp(a.Translation, z.Translation, w),
                                                     Quaternion.Normalize(Quaternion.Slerp(a.Rotation, z.Rotation, w)),
                                                     Vector3.Lerp(a.Scale, z.Scale, w));
                }
            }
        return (intro, new DanceMotion { Bones = bones, Frames = frames });
    }

    /// <summary>减掉高斯平滑后的轨迹（慢慢漂走的部分），只留比 sigma 快的小位移；转向不动。首尾按边缘值延伸。</summary>
    private static void RemoveDrift(Vector4[] roots, double sigmaSeconds)
    {
        var n = roots.Length;
        var sigma = sigmaSeconds * Fps;
        var radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new double[2 * radius + 1];
        for (var i = -radius; i <= radius; i++) kernel[i + radius] = Math.Exp(-0.5 * i * i / (sigma * sigma));
        var sum = kernel.Sum();
        var smooth = new Vector2[n];
        for (var k = 0; k < n; k++)
        {
            double x = 0, z = 0;
            for (var i = -radius; i <= radius; i++)
            {
                var r = roots[Math.Clamp(k + i, 0, n - 1)];
                x += kernel[i + radius] * r.X;
                z += kernel[i + radius] * r.Z;
            }
            smooth[k] = new Vector2((float)(x / sum), (float)(z / sum));
        }
        // roots[0] 已经是原点：减掉平滑轨迹后再加回第 0 帧的平滑值，第 0 帧仍在原点
        var s0 = smooth[0];
        for (var k = 0; k < n; k++)
            roots[k] = new Vector4(roots[k].X - smooth[k].X + s0.X, 0, roots[k].Z - smooth[k].Y + s0.Y, roots[k].W);
    }

    /// <summary>模型空间 → 这一帧的「骨骼名 → 模型空间变换」（给 IdleBaker.LocalPose 用）。</summary>
    public Dictionary<string, BoneTransform> Pose(int frame)
    {
        var d = new Dictionary<string, BoneTransform>(Bones.Length, StringComparer.Ordinal);
        for (var j = 0; j < Bones.Length; j++) d[Bones[j]] = Frames[frame][j];
        return d;
    }
}
