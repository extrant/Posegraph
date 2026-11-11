using System.Numerics;
using System.Runtime.CompilerServices;

namespace Posegraph.Editor;

/// <summary>
/// 躯干的体积（判断胳膊埋进身体多深用）：沿脊椎（腰 → 背 → 胸 → 脖子）每 3cm 切一层，每层按 24 个方向记下躯干表面离脊椎多远。
/// 找最近表面只能看几厘米以内（再远太慢），上臂整段压进胸肌超过这个距离就看不见了；
/// 有了截面轮廓，任何一点「在不在躯干里、埋多深」都是常数时间，也适合肌肉体型、Customize+ 放大过的身体。
/// 只用自己身上主要跟脊椎骨骼动的三角形（胸、腹、腰）建。每个表面建一次、缓存。
/// </summary>
public sealed class TorsoVolume
{
    private const float Slice = 0.03f;
    private const int Bins = 24;
    private static readonly string[] SpineBones = ["j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi"];
    private static readonly HashSet<string> TorsoBones = ["j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_mune_l", "j_mune_r", "iv_c_mune_l", "iv_c_mune_r", "iv_fukubu"];
    private static readonly ConditionalWeakTable<Surface, TorsoVolume?> Cache = new();

    private readonly Vector3[] _spine;     // 脊椎折线的点
    private readonly float[] _length;      // 每个点沿折线的累计长度
    private readonly float[,] _radius;     // [层, 方向] 表面离脊椎的最大距离（0 表示没数据）

    private TorsoVolume(Vector3[] spine, float[] length, float[,] radius) => (_spine, _length, _radius) = (spine, length, radius);

    /// <summary>这个表面上自己的躯干体积（姿势用来定脊椎）；骨骼或三角形不够时 null。</summary>
    public static TorsoVolume? Of(Surface surface, EditPose pose)
        => Cache.GetValue(surface, s => Build(s, pose));

    private static TorsoVolume? Build(Surface surface, EditPose pose)
    {
        if (!SpineBones.All(pose.Has)) return null;
        var spine = SpineBones.Select(pose.Position).ToArray();
        var length = new float[spine.Length];
        for (var i = 1; i < spine.Length; i++) length[i] = length[i - 1] + Vector3.Distance(spine[i - 1], spine[i]);
        var slices = (int)MathF.Ceiling(length[^1] / Slice) + 1;
        var radius = new float[slices, Bins];
        var probe = new TorsoVolume(spine, length, radius);
        var any = false;
        foreach (var (a, b, c) in surface.Triangles((actor, bone, _) => actor == 0 && TorsoBones.Contains(bone)))
            foreach (var p in new[] { a, b, c, (a + b + c) / 3f })
            {
                if (probe.Locate(p) is not { } at || at.Slice < 0 || at.Slice >= slices) continue;
                if (at.Radius > radius[at.Slice, at.Bin]) radius[at.Slice, at.Bin] = at.Radius;
                any = true;
            }
        return any ? probe : null;
    }

    /// <summary>点在脊椎折线上的位置：第几层、哪个方向、离脊椎多远。脊椎两端以外 null。</summary>
    private (int Slice, int Bin, float Radius)? Locate(Vector3 p)
    {
        var best = float.PositiveInfinity;
        (float S, Vector3 Q, Vector3 Axis)? hit = null;
        for (var i = 0; i + 1 < _spine.Length; i++)
        {
            var ab = _spine[i + 1] - _spine[i];
            var len2 = ab.LengthSquared();
            if (len2 < 1e-8f) continue;
            var t = Vector3.Dot(p - _spine[i], ab) / len2;
            if ((t < 0 && i > 0) || (t > 1 && i + 2 < _spine.Length)) t = Math.Clamp(t, 0f, 1f);
            if (t < -0.2f || t > 1.2f) continue;   // 腰以下、脖子以上不算躯干
            var q = _spine[i] + ab * t;
            var d = Vector3.DistanceSquared(p, q);
            if (d < best) (best, hit) = (d, (_length[i] + MathF.Sqrt(len2) * t, q, ab / MathF.Sqrt(len2)));
        }
        if (hit is not { } h) return null;
        var radial = p - h.Q;
        radial -= h.Axis * Vector3.Dot(radial, h.Axis);
        var r = radial.Length();
        // 方向：在垂直脊椎的平面里，以模型前方（+Z）为 0
        var forward = Vector3.UnitZ - h.Axis * Vector3.Dot(Vector3.UnitZ, h.Axis);
        if (forward.LengthSquared() < 1e-6f) forward = Vector3.UnitX;
        forward = Vector3.Normalize(forward);
        var side = Vector3.Cross(h.Axis, forward);
        var angle = MathF.Atan2(Vector3.Dot(radial, side), Vector3.Dot(radial, forward));
        var bin = (int)MathF.Floor((angle + MathF.PI) / (2 * MathF.PI) * Bins) % Bins;
        return ((int)MathF.Floor(h.S / Slice), bin, r);
    }

    /// <summary>点埋进躯干多深（米）；不在躯干里时 0。相邻层、相邻方向取最小的轮廓（偏保守，不把腋下的空隙当成身体）。</summary>
    public float Depth(Vector3 p)
    {
        if (Locate(p) is not { } at || at.Slice < 0 || at.Slice >= _radius.GetLength(0)) return 0f;
        var outline = float.PositiveInfinity;
        for (var ds = -1; ds <= 1; ds++)
        for (var db = -1; db <= 1; db++)
        {
            var s = at.Slice + ds;
            if (s < 0 || s >= _radius.GetLength(0)) continue;
            var value = _radius[s, (at.Bin + db + Bins) % Bins];
            if (value <= 0) return 0f;   // 附近没有躯干表面（腋下、脖子两侧的空隙）：不算埋进去
            outline = MathF.Min(outline, value);
        }
        return MathF.Max(0f, outline - at.Radius);
    }
}
