using System.Numerics;

namespace Posegraph.Editor;

/// <summary>
/// 姿势编辑器的 IK 数学（模型空间）。两段 IK 从后端 retarget/ffxiv.py 的 _two_bone_ik 移植，行为一致：
/// 肩 S、肘 E、腕 W 的双骨链，求让腕到达 T 的上臂旋转 R1 与前臂附加旋转 R2（新的上臂朝向 = R1·旧值，新的前臂朝向 = R2·R1·旧值）。
/// 给了 hinge（肘 / 膝的弯曲轴）时肘保持纯铰链：新的弯曲平面取最接近原弯曲轴的那个，前臂只绕弯曲轴转（游戏里肘是纯 z 轴铰链，侧摆会让蒙皮扭曲）。
/// 四元数乘法和 scipy 的 R1 * R2 一样：先作用右边的。
/// </summary>
public static class PoseIk
{
    private const float Eps = 1e-6f;

    public static (Quaternion R1, Quaternion R2) TwoBone(Vector3 s, Vector3 e, Vector3 w, Vector3 t, Vector3? hinge = null)
    {
        float a = (e - s).Length(), b = (w - e).Length();
        var toT = t - s;
        var d = Math.Clamp(toT.Length(), MathF.Abs(a - b) + 1e-4f, a + b - 1e-4f);
        var u = toT.LengthSquared() > Eps * Eps ? Vector3.Normalize(toT) : Vector3.Normalize(w - s);
        var cosA = (a * a + d * d - b * b) / (2 * a * d);
        var sinA = MathF.Sqrt(MathF.Max(0f, 1 - cosA * cosA));
        if (hinge is { } h)
        {
            var n = h - u * Vector3.Dot(h, u);
            if (n.Length() > Eps && Frame(e - s, h) is { } old)
            {
                n = Vector3.Normalize(n);
                // 肘在弯曲平面里的哪一侧：和原姿势一致（原肘相对肩→腕连线的偏移方向）
                var u0 = Vector3.Normalize(w - s);
                var v0 = (e - s) - u0 * Vector3.Dot(e - s, u0);
                var side = v0.Length() < Eps || Vector3.Dot(v0, Vector3.Cross(h, u0)) >= 0 ? 1f : -1f;
                var v = side * Vector3.Cross(n, u);
                var e2 = s + a * (cosA * u + sinA * v);
                var fresh = Frame(e2 - s, n);
                if (fresh is { } next)
                {
                    var r1 = FrameToFrame(old, next);
                    var w1 = e2 + Vector3.Transform(w - e, r1);
                    var r2 = TwistAbout(n, w1 - e2, s + u * d - e2);
                    return (r1, r2);
                }
            }
        }
        var vv = (e - s) - u * Vector3.Dot(e - s, u);   // 肘的弯曲方向：原肘位置在 S→T 垂面上的分量
        if (vv.Length() < Eps)
            vv = Vector3.Cross(u, MathF.Abs(u.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        vv = Vector3.Normalize(vv);
        var elbow = s + a * (cosA * u + sinA * vv);
        var q1 = Swing(e - s, elbow - s);
        var wrist = elbow + Vector3.Transform(w - e, q1);
        var q2 = Swing(wrist - elbow, s + u * d - elbow);
        return (q1, q2);
    }

    /// <summary>
    /// 在编辑姿势上解一条胳膊 / 腿：末端（手 / 脚）到 target（模型空间），肘 / 膝保持铰链（中段骨骼局部 z 轴），末端朝向不变。
    /// 上段整棵子树绕根转，中段整棵子树绕肘转，所以手指、辅助骨都跟着走。
    /// </summary>
    public static void SolveLimb(EditPose pose, string upper, string fore, string end, Vector3 target, Quaternion? endRotation = null,
                                 float minBend = MinBend)
    {
        var s = pose.Position(upper);
        var e = pose.Position(fore);
        var w = pose.Position(end);
        // 肘 / 膝最多折到 minBend（上臂和前臂的夹角），再近的目标往外推：不会折成一团、也不会因此翻到另一边
        float a = (e - s).Length(), b = (w - e).Length();
        var reachMin = MathF.Sqrt(MathF.Max(0f, a * a + b * b - 2 * a * b * MathF.Cos(minBend)));
        var toTarget = target - s;
        if (toTarget.Length() < reachMin)
            target = s + (toTarget.LengthSquared() > 1e-10f ? Vector3.Normalize(toTarget) : Vector3.Normalize(w - s)) * reachMin;
        var keep = endRotation ?? pose.Rotation(end);
        // 弯曲轴：胳膊 / 腿弯着时用现在的弯曲平面（目标不动时解出来就是原样，点一下不会变形）；
        // 几乎伸直时平面不确定，用中段骨骼的局部 z 轴（游戏里肘、膝是这个轴的铰链，和 Brio 的设置一致）
        var bend = Vector3.Cross(e - s, w - e);
        var hinge = bend.Length() > 0.09f * a * b ? Vector3.Normalize(bend) : Vector3.Transform(Vector3.UnitZ, pose.Rotation(fore));
        var (r1, r2) = TwoBone(s, e, w, target, hinge);
        pose.RotateSubtree(upper, s, r1);
        pose.RotateSubtree(fore, pose.Position(fore), r2);
        // 弯曲轴不完全垂直于手臂时，铰链解会差一点：整条胳膊绕肩再对一下方向，手正好跟着鼠标（肘仍是纯铰链）
        var reach = pose.Position(end) - s;
        var want = target - s;
        if (reach.LengthSquared() > 1e-8f && want.LengthSquared() > 1e-8f)
            pose.RotateSubtree(upper, s, Swing(reach, want));
        // 末端朝向保持拖之前的样子（或给定的朝向）
        pose.SetRotation(end, keep);
    }

    /// <summary>肘 / 膝最小夹角（完全折叠是 0，伸直是 180°）。</summary>
    public const float MinBend = 30f * MathF.PI / 180f;

    /// <summary>
    /// 拖肘 / 膝：整条胳膊 / 腿绕「根 → 末端」连线转，让肘 / 膝朝 target 那边。末端位置和朝向都不变，弯曲角度不变。
    /// </summary>
    public static void SwivelLimb(EditPose pose, string upper, string fore, string end, Vector3 target)
    {
        var s = pose.Position(upper);
        var axis = pose.Position(end) - s;
        if (axis.LengthSquared() < 1e-8f) return;
        var endRotation = pose.Rotation(end);
        var q = TwistAbout(axis, pose.Position(fore) - s, target - s);
        pose.RotateSubtree(upper, s, q);
        var fix = Quaternion.Normalize(endRotation * Quaternion.Inverse(pose.Rotation(end)));
        pose.RotateSubtree(end, pose.Position(end), fix);
    }

    /// <summary>整条胳膊 / 腿绕「根 → 末端」连线转 angle（弧度）：末端位置和朝向都不变，只是肘 / 膝换个朝向。</summary>
    public static void SwivelBy(EditPose pose, string upper, string end, float angle)
    {
        var s = pose.Position(upper);
        var axis = pose.Position(end) - s;
        if (axis.LengthSquared() < 1e-8f) return;
        var endRotation = pose.Rotation(end);
        pose.RotateSubtree(upper, s, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle));
        pose.SetRotation(end, endRotation);
    }

    /// <summary>把方向 a 转到方向 b 的最小旋转。</summary>
    public static Quaternion Swing(Vector3 a, Vector3 b)
    {
        a = Vector3.Normalize(a);
        b = Vector3.Normalize(b);
        var axis = Vector3.Cross(a, b);
        float s = axis.Length(), c = Vector3.Dot(a, b);
        if (s < 1e-9f)
        {
            if (c > 0) return Quaternion.Identity;
            var ortho = Vector3.Cross(a, MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ortho), MathF.PI);
        }
        return Quaternion.CreateFromAxisAngle(axis / s, MathF.Atan2(s, c));
    }

    /// <summary>绕 axis 旋转，使 a 在垂直于 axis 平面上的投影转到 b 的投影方向。</summary>
    public static Quaternion TwistAbout(Vector3 axis, Vector3 a, Vector3 b)
    {
        var n = Vector3.Normalize(axis);
        var pa = a - n * Vector3.Dot(a, n);
        var pb = b - n * Vector3.Dot(b, n);
        var angle = MathF.Atan2(Vector3.Dot(n, Vector3.Cross(pa, pb)), Vector3.Dot(pa, pb));
        return Quaternion.CreateFromAxisAngle(n, angle);
    }

    /// <summary>以 e 为第一轴、h（去掉 e 方向分量）为第二轴的正交基；h 和 e 共线时 null。</summary>
    private static (Vector3 E, Vector3 H)? Frame(Vector3 e, Vector3 h)
    {
        e = Vector3.Normalize(e);
        h -= e * Vector3.Dot(h, e);
        if (h.Length() < Eps) return null;
        return (e, Vector3.Normalize(h));
    }

    /// <summary>把正交基 from 转到 to 的旋转（先对齐第一轴，再绕它对齐第二轴；等价于后端的 new @ old.T）。</summary>
    private static Quaternion FrameToFrame((Vector3 E, Vector3 H) from, (Vector3 E, Vector3 H) to)
    {
        var swing = Swing(from.E, to.E);
        var twist = TwistAbout(to.E, Vector3.Transform(from.H, swing), to.H);
        return Quaternion.Normalize(twist * swing);
    }
}
