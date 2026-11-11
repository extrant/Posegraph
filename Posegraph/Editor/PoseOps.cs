using System.Numerics;

namespace Posegraph.Editor;

/// <summary>
/// 姿势编辑器里除两段 IK 以外的操作（都在模型空间里改 EditPose）：
/// 骨骼链对准目标（腰、胸、头、肩、脚尖）、按比例分到几节的旋转、手指 CCD、左右镜像、视线、关节限位。
/// </summary>
public static class PoseOps
{
    /// <summary>
    /// 让 tip 这一点朝 target 转：链上每节按权重分一部分（从根往末端，第 i 节转「剩下的」里它那份），最后一节把剩下的转完，
    /// 所以 tip 一定正对 target 方向，弯曲均匀分到几节上（弯腰、低头不会只折一节）。
    /// </summary>
    public static void AimChain(EditPose pose, IReadOnlyList<string> chain, IReadOnlyList<float> weights, Func<EditPose, Vector3> tip, Vector3 target)
    {
        // 各节的转轴不在同一点，一遍下来 tip 离目标方向还差一点：再按同样比例补两遍
        for (var pass = 0; pass < 3; pass++)
        {
            var remaining = weights.Sum();
            for (var i = 0; i < chain.Count; i++)
            {
                if (!pose.Has(chain[i])) continue;
                var pivot = pose.Position(chain[i]);
                var from = tip(pose) - pivot;
                var to = target - pivot;
                var share = remaining > 1e-6f ? weights[i] / remaining : 1f;
                remaining -= weights[i];
                if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f) continue;
                pose.RotateSubtree(chain[i], pivot, Quaternion.Slerp(Quaternion.Identity, PoseIk.Swing(from, to), share));
            }
        }
    }

    /// <summary>把旋转 q（模型空间）按权重分到链上几节，每节绕自己的位置转；最后一节的朝向正好多转了 q。</summary>
    public static void RotateChain(EditPose pose, IReadOnlyList<string> chain, IReadOnlyList<float> weights, Quaternion q)
    {
        var total = weights.Sum();
        for (var i = 0; i < chain.Count; i++)
            if (pose.Has(chain[i]))
                pose.RotateSubtree(chain[i], pose.Position(chain[i]), Quaternion.Slerp(Quaternion.Identity, q, total > 1e-6f ? weights[i] / total : 0f));
    }

    /// <summary>
    /// 手指：指尖离指根的距离决定弯多少，方向由指根转过去（AnimChar 的思路：拉近掌心就是握拳，往外拉就是伸直）。
    /// 弯曲平均分到指根以外的各节，每节绕自己的局部 −z 轴弯（游戏动画里手指关节都是这个轴，左右手一样，拇指也是），
    /// 所以不会像 CCD 那样只勾最后一节、也不会侧折。
    /// </summary>
    public static void CurlFinger(EditPose pose, IReadOnlyList<string> chain, Func<EditPose, Vector3> tip, Vector3 target)
    {
        if (chain.Count < 2 || !chain.All(pose.Has)) return;
        var root = pose.Position(chain[0]);
        var want = Vector3.Distance(target, root);
        EditPose Curl(float angle)
        {
            var p = pose.Clone();
            for (var i = 1; i < chain.Count; i++)
            {
                var axis = Vector3.Transform(-Vector3.UnitZ, p.Rotation(chain[i]));
                p.RotateSubtree(chain[i], p.Position(chain[i]), Quaternion.CreateFromAxisAngle(axis, angle));
            }
            return p;
        }
        float Error(float angle) => Vector3.Distance(tip(Curl(angle)), root) - want;
        // 从现在的弯曲（0）出发就近找：距离随弯曲并不单调（伸直后再往外翘、握紧后再弯，指尖都会往回走），
        // 所以不能在整个范围里二分——那样手指几乎伸直时一碰就翻到 −40°。往误差变小的那边每次走 2°，
        // 越过目标就在这一格里二分；走到头也没越过（够不着）就停在最接近的地方（比如刚好伸直）
        const float step = 2f * MathF.PI / 180f, lo = -40f * MathF.PI / 180f, hi = 110f * MathF.PI / 180f;
        var e0 = Error(0);
        var angle = 0f;
        if (MathF.Abs(e0) > 1e-5f)
        {
            var dir = MathF.Abs(Error(step)) < MathF.Abs(Error(-step)) ? 1f : -1f;
            float a = 0, ea = e0, best = 0, bestErr = MathF.Abs(e0);
            while (true)
            {
                var b = a + dir * step;
                if (b < lo || b > hi) break;
                var eb = Error(b);
                if (MathF.Sign(eb) != MathF.Sign(ea))
                {
                    for (var i = 0; i < 16; i++)
                    {
                        var mid = (a + b) / 2;
                        var em = Error(mid);
                        if (MathF.Sign(em) == MathF.Sign(ea)) (a, ea) = (mid, em);
                        else b = mid;
                    }
                    best = (a + b) / 2;
                    break;
                }
                if (MathF.Abs(eb) >= bestErr) break;   // 误差不再变小：够不着，停在最接近处
                (a, ea, best, bestErr) = (b, eb, b, MathF.Abs(eb));
            }
            angle = best;
        }
        var curled = Curl(angle);
        foreach (var bone in curled.SubtreeOrdered(chain[0])) pose.SetRotation(bone, curled.Rotation(bone));
        // 指根转向目标
        var from = tip(pose) - root;
        var to = target - root;
        if (from.LengthSquared() > 1e-12f && to.LengthSquared() > 1e-12f) pose.RotateSubtree(chain[0], root, PoseIk.Swing(from, to));
    }

    /// <summary>
    /// 把 bones（一侧的骨骼，从根往末端排好）的姿势镜像到另一侧。左右是模型 x 轴；两侧骨骼的局部坐标轴约定不同，
    /// 所以比的是「相对绑定姿势转了多少」：先扣掉参考骨骼（胸 / 骨盆）的转动，镜像后再加回去——身体扭着时，镜像也是相对身体的。
    /// </summary>
    public static void Mirror(EditPose pose, PoseRig rig, IEnumerable<string> bones, string reference)
    {
        var refDelta = pose.Has(reference) ? Quaternion.Normalize(pose.Rotation(reference) * Quaternion.Conjugate(rig.Bind(reference))) : Quaternion.Identity;
        var refInv = Quaternion.Conjugate(refDelta);
        foreach (var bone in bones)
        {
            if (PoseRig.Mirror(bone) is not { } other || !pose.Has(bone) || !pose.Has(other) || !rig.HasBind(bone) || !rig.HasBind(other)) continue;
            var delta = refInv * pose.Rotation(bone) * Quaternion.Conjugate(rig.Bind(bone));   // 这一侧相对绑定姿势（扣掉身体转动）
            var mirrored = new Quaternion(delta.X, -delta.Y, -delta.Z, delta.W);                 // 沿 x 轴镜像：旋转轴 (x,y,z) → (x,-y,-z)
            pose.SetRotation(other, refDelta * mirrored * rig.Bind(other));
        }
    }

    /// <summary>眼睛：模型 +z 是绑定姿势下的正前方；眼球朝 target，偏离脸的正前方最多 maxDeg。</summary>
    public static void Gaze(EditPose pose, PoseRig rig, IEnumerable<string> eyes, string head, Vector3 target, float maxDeg = 35f)
    {
        var headForward = Forward(pose, rig, head);
        foreach (var eye in eyes)
        {
            if (!pose.Has(eye)) continue;
            var want = target - pose.Position(eye);
            if (want.LengthSquared() < 1e-8f) continue;
            want = Vector3.Normalize(want);
            var off = MathF.Acos(Math.Clamp(Vector3.Dot(want, headForward), -1f, 1f));
            var max = maxDeg * MathF.PI / 180f;
            if (off > max) want = Vector3.Transform(headForward, Quaternion.Slerp(Quaternion.Identity, PoseIk.Swing(headForward, want), max / off));
            pose.SetRotation(eye, PoseIk.Swing(Forward(pose, rig, eye), want) * pose.Rotation(eye));
        }
    }

    /// <summary>骨骼的「正前方」：绑定姿势下朝模型 +z，跟着骨骼转。</summary>
    public static Vector3 Forward(EditPose pose, PoseRig rig, string bone)
        => Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, pose.Rotation(bone) * Quaternion.Conjugate(rig.Bind(bone))));

    // ------------------------------------------------------------------ 辅助骨

    /// <summary>
    /// 辅助骨跟着主骨（照后端 retarget/ffxiv.py 的 HelperRule，系数是从游戏动画采样拟合的）：
    /// 主骨相对父骨骼和绑定姿势比的偏差按 x 轴做「摆动·扭转」分解成 [扭转, 摆动 y, 摆动 z]（弧度），
    /// 辅助骨相对绑定姿势绕自己的局部 Axis 轴转 Coef·特征 + 截距。只管胳膊：手腕扭转的一半给 n_hte（不然手腕会拧成糖纸），
    /// 肘弯的一半反向给 n_hhiji / n_hijisoubi（肘窝不凹），上臂的扭转和抬起给 n_hkata（肩膀不塌）。
    /// </summary>
    private sealed record HelperRule(string Helper, string Driver, int Axis, Vector3 Coef, float InterceptDeg = 0f);

    private static readonly HelperRule[] HelperRules =
    [
        .. new[] { ("l", 1f), ("r", -1f) }.SelectMany(s => new HelperRule[]
        {
            new($"n_hhiji_{s.Item1}", $"j_ude_b_{s.Item1}", 2, new Vector3(0, 0, -0.495f)),
            new($"n_hijisoubi_{s.Item1}", $"j_ude_b_{s.Item1}", 2, new Vector3(0, 0, -0.495f)),
            new($"n_hte_{s.Item1}", $"j_te_{s.Item1}", 0, new Vector3(0.5f, 0, 0)),
            new($"n_hkata_{s.Item1}", $"j_ude_a_{s.Item1}", 0, new Vector3(-0.47f, -0.45f, 0.18f * s.Item2), 10.3f * s.Item2),
        }),
    ];

    /// <summary>这次操作改过的主骨（和 start 比局部旋转变了）对应的辅助骨重新算。start 为 null 时只算 only 里的主骨。</summary>
    public static void FollowHelpers(EditPose? start, EditPose pose, PoseRig rig, ICollection<string>? only = null)
    {
        foreach (var rule in HelperRules)
        {
            if (!pose.Has(rule.Helper) || !pose.Has(rule.Driver) || !rig.HasBind(rule.Helper) || !rig.HasBind(rule.Driver)
                || pose.Parent(rule.Helper) is not { } parent || !rig.HasBind(parent)) continue;
            if (only is not null && !only.Contains(rule.Driver)) continue;
            if (start is not null && MathF.Abs(Quaternion.Dot(pose.LocalRotation(rule.Driver), start.LocalRotation(rule.Driver))) > 0.99999f) continue;
            var deviation = Quaternion.Normalize(Quaternion.Conjugate(rig.BindLocal(rule.Driver)) * pose.LocalRotation(rule.Driver));
            if (deviation.W < 0) deviation = new Quaternion(-deviation.X, -deviation.Y, -deviation.Z, -deviation.W);
            var twist = 2 * MathF.Atan2(deviation.X, deviation.W);
            var swing = deviation * Quaternion.Conjugate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, twist));
            var swingAngle = 2 * MathF.Acos(Math.Clamp(swing.W, -1f, 1f));
            var sin = MathF.Sin(swingAngle / 2);
            var rotvec = sin > 1e-6f ? new Vector3(swing.X, swing.Y, swing.Z) / sin * swingAngle : Vector3.Zero;
            var features = new Vector3(twist, rotvec.Y, rotvec.Z);
            var angle = Vector3.Dot(rule.Coef, features) + rule.InterceptDeg * MathF.PI / 180f;
            var axis = rule.Axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
            pose.SetRotation(rule.Helper, pose.Rotation(parent) * rig.BindLocal(rule.Helper) * Quaternion.CreateFromAxisAngle(axis, angle));
        }
    }

    // ------------------------------------------------------------------ 关节限位

    public enum LimitKind { Magnitude, Twist, Bend }

    /// <summary>bone 的限位：Magnitude 相对绑定姿势的总转角；Twist 绕骨骼方向（局部 x）的扭转；Bend 肘 / 膝弯曲（180° − 夹角，Base 是上一段的根）。</summary>
    public sealed record Limit(string Bone, LimitKind Kind, float Degrees, string? Base = null);

    /// <summary>阈值同后端 tuning.LIMITS（598 帧游戏动画统计）。超过为黄，超过 1.3 倍为红。</summary>
    public static readonly Limit[] Limits =
    [
        new("j_sebo_a", LimitKind.Magnitude, 60),
        new("j_sebo_b", LimitKind.Magnitude, 25),
        new("j_sebo_c", LimitKind.Magnitude, 25),
        new("j_kubi", LimitKind.Magnitude, 35),
        new("j_kao", LimitKind.Magnitude, 35),
        new("j_sako_l", LimitKind.Magnitude, 35),
        new("j_sako_r", LimitKind.Magnitude, 35),
        new("j_ude_a_l", LimitKind.Twist, 120),
        new("j_ude_a_r", LimitKind.Twist, 120),
        new("j_ude_b_l", LimitKind.Bend, 135, "j_ude_a_l"),
        new("j_ude_b_r", LimitKind.Bend, 135, "j_ude_a_r"),
        new("j_te_l", LimitKind.Magnitude, 90),
        new("j_te_r", LimitKind.Magnitude, 90),
        new("j_asi_c_l", LimitKind.Bend, 150, "j_asi_a_l"),
        new("j_asi_c_r", LimitKind.Bend, 150, "j_asi_a_r"),
    ];

    /// <summary>当前值（度）。</summary>
    public static float Measure(EditPose pose, PoseRig rig, Limit limit)
    {
        if (!pose.Has(limit.Bone)) return 0f;
        switch (limit.Kind)
        {
            case LimitKind.Bend:
            {
                var child = limit.Bone.StartsWith("j_ude_b", StringComparison.Ordinal) ? "j_te" + limit.Bone[^2..] : "j_asi_d" + limit.Bone[^2..];
                if (limit.Base is null || !pose.Has(limit.Base) || !pose.Has(child)) return 0f;
                var up = pose.Position(limit.Base) - pose.Position(limit.Bone);
                var down = pose.Position(child) - pose.Position(limit.Bone);
                if (up.LengthSquared() < 1e-10f || down.LengthSquared() < 1e-10f) return 0f;
                var inner = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(up), Vector3.Normalize(down)), -1f, 1f)) * 180f / MathF.PI;
                return 180f - inner;
            }
            case LimitKind.Twist:
            {
                var d = Deviation(pose, rig, limit.Bone);
                var twist = 2 * MathF.Atan2(d.X, d.W);
                if (twist > MathF.PI) twist -= 2 * MathF.PI;
                if (twist < -MathF.PI) twist += 2 * MathF.PI;
                return MathF.Abs(twist) * 180f / MathF.PI;
            }
            default:
            {
                var d = Deviation(pose, rig, limit.Bone);
                return 2 * MathF.Acos(Math.Clamp(MathF.Abs(d.W), 0f, 1f)) * 180f / MathF.PI;
            }
        }
    }

    /// <summary>0 正常、1 超出、2 超出 1.3 倍。</summary>
    public static int Level(float value, float limit) => value <= limit ? 0 : value <= limit * 1.3f ? 1 : 2;

    /// <summary>把 bones 里超出范围的（总转角、扭转）拉回范围内。肘 / 膝弯曲由 IK 的最小夹角限制，这里不动。</summary>
    public static void Clamp(EditPose pose, PoseRig rig, IReadOnlySet<string> bones)
    {
        foreach (var limit in Limits)
        {
            if (!bones.Contains(limit.Bone) || !pose.Has(limit.Bone) || !rig.HasBind(limit.Bone) || limit.Kind == LimitKind.Bend) continue;
            var d = Deviation(pose, rig, limit.Bone);
            var max = limit.Degrees * MathF.PI / 180f;
            Quaternion clamped;
            if (limit.Kind == LimitKind.Magnitude)
            {
                var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(d.W), 0f, 1f));
                if (angle <= max) continue;
                if (d.W < 0) d = new Quaternion(-d.X, -d.Y, -d.Z, -d.W);
                clamped = Quaternion.Slerp(Quaternion.Identity, d, max / angle);
            }
            else
            {
                var twist = 2 * MathF.Atan2(d.X, d.W);
                if (twist > MathF.PI) twist -= 2 * MathF.PI;
                if (twist < -MathF.PI) twist += 2 * MathF.PI;
                if (MathF.Abs(twist) <= max) continue;
                var swing = d * Quaternion.Conjugate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, twist));
                clamped = swing * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.Sign(twist) * max);
            }
            var parent = pose.Parent(limit.Bone);
            var local = rig.BindLocal(limit.Bone) * clamped;
            pose.SetRotation(limit.Bone, parent is null ? local : pose.Rotation(parent) * local);
        }
    }

    /// <summary>局部旋转相对绑定姿势的变化（局部坐标）。</summary>
    private static Quaternion Deviation(EditPose pose, PoseRig rig, string bone)
        => Quaternion.Normalize(Quaternion.Conjugate(rig.BindLocal(bone)) * pose.LocalRotation(bone));
}
