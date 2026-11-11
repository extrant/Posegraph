using System.Numerics;

namespace Posegraph.Editor;

/// <summary>
/// 全身协同伸手：脊椎（j_sebo_a/b/c）、锁骨、肩、肘（纯铰链）、手腕一起解，参考 mink / Pink（任务式微分 IK）和
/// 虚幻 Full Body IK 的「刚度」：
/// <list type="bullet">
/// <item>任务：掌心中心到目标点、手的朝向到目标朝向；</item>
/// <item>姿势：每个关节尽量不离开出发时的样子，按刚度加权（脊椎硬、锁骨中、肩肘软）——够不着时才弯腰、耸肩，而不是胳膊硬拉到极限；</item>
/// <item>限位：关节范围（游戏动画统计的 PoseOps.Limits + 手腕的屈伸 / 侧偏 / 旋前旋后）当很陡的惩罚；</item>
/// <item>碰撞：胳膊（上臂、前臂）上的采样点不能压进任何表面（自己的身体、别的角色）超过软组织的量，也是惩罚项；</item>
/// <item>肘自然下垂：肘比肩高就轻罚。</item>
/// </list>
/// 每一轮按当前姿势算残差和雅可比（旋转关节：dp/dθ = a × (p − o)），Levenberg–Marquardt 求一步，各关节绕世界轴转一点，
/// 迭代到收敛。和以前「先只解胳膊、再单独弯腰、再单独让开身体」不同，这些都在同一个最小二乘里互相权衡。
/// 前臂旋前 / 旋后由手腕骨骼的扭转承担（游戏骨架的做法，一半由 PoseOps.FollowHelpers 分给 n_hte），肘只绕局部 z 弯。
/// </summary>
public static class BodyReach
{
    /// <summary>调试输出（离线测试用）。</summary>
    public static Action<string>? Trace { get; set; }

    private enum Kind { Ball, Hinge }

    private sealed record Joint(string Bone, Kind Kind, float Stiffness, float MaxChange);

    // 刚度（姿势项权重，单位：每弧度相当于多少米的掌心误差）：脊椎下段最硬，越往手越软
    private static Joint[] Chain(string side) =>
    [
        new("j_sebo_a", Kind.Ball, 0.30f, 15f),
        new("j_sebo_b", Kind.Ball, 0.25f, 12f),
        new("j_sebo_c", Kind.Ball, 0.25f, 12f),
        new($"j_sako_{side}", Kind.Ball, 0.12f, 20f),
        new($"j_ude_a_{side}", Kind.Ball, 0.02f, 180f),
        new($"j_ude_b_{side}", Kind.Hinge, 0.01f, 180f),
        new($"j_te_{side}", Kind.Ball, 0.08f, 180f),   // 手腕的姿势参照是「中立」（绑定姿势），不是出发时：手腕越弯越费劲
    ];

    private const float OrientationWeight = 0.12f;   // 1 弧度朝向误差 ≈ 12cm 掌心误差
    private const float FacingWeight = 0.18f;        // 只要求掌心朝向时：掌心偏 1 弧度 ≈ 18cm
    private const float TwistPreference = 0.02f;     // 绕掌心法线转多少：很弱的偏好
    private const float LimitWeight = 2.0f;          // 超出关节范围 1 弧度 ≈ 2m（基本是硬约束）
    private const float CollisionWeight = 8.0f;      // 胳膊压进去 1cm ≈ 掌心差 8cm
    private const float ElbowUpWeight = 0.08f;
    private const float ArmSoft = 0.015f;            // 胳膊贴着身体允许压进去这么多（软组织）
    private const float HairSoft = 0.025f;           // 头发蓬松，能压下去这么多（再多胳膊就从别人头顶上绕过去了）

    /// <summary>
    /// 胳膊上的点 p 碰到身体的哪里（Surface.Closest 的结果）。surfacePose 是建表面时的姿势：碰到的是自己上半身（j_sebo_a 以上，
    /// 会跟着这次求解转）时，表面还停在建的时候，按那根骨骼从那时到现在的移动把点倒推回去再量，结果（点、法线）再换回现在的位置。
    /// 以前直接量，脊椎一转，判断胳膊碰没碰到自己胸口 / 肩就差一两厘米。
    /// </summary>
    public static Contact? ArmContact(Surface surface, Vector3 p, float radius, EditPose pose, EditPose? surfacePose)
    {
        var first = surface.Closest(p, radius);
        if (first is not { } hit || surfacePose is null || hit.Actor != 0 || !pose.Has(hit.Bone) || !surfacePose.Has(hit.Bone) || !UnderSpine(pose, hit.Bone))
            return first;
        if (!Matrix4x4.Invert(Matrix(pose[hit.Bone]), out var inverseNow)) return first;
        var back = inverseNow * Matrix(surfacePose[hit.Bone]);
        if (!Matrix4x4.Invert(back, out var forward)) return first;
        if (surface.Closest(Vector3.Transform(p, back), radius) is not { } then || then.Actor != 0) return first;
        var normal = Vector3.TransformNormal(then.Normal, forward);
        return then with { Point = Vector3.Transform(then.Point, forward), Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : then.Normal };
    }

    /// <summary>
    /// 这只胳膊自己那侧的肩（锁骨、n_hkata 辅助骨）：它们的蒙皮跟着上臂抬起、扭转一起动（游戏里由辅助骨带），
    /// 表面却是建的时候的样子，胳膊一抬就像「压进了自己的肩」——其实没有。算胳膊碰撞时不算它们。
    /// </summary>
    public static bool OwnShoulder(Contact contact, string side)
        => contact.Actor == 0 && (contact.Bone == $"j_sako_{side}" || contact.Bone.StartsWith($"n_hkata_{side}", StringComparison.Ordinal));

    public static bool UnderSpine(EditPose pose, string bone)
    {
        for (var b = (string?)bone; b is not null; b = pose.Parent(b))
            if (b == "j_sebo_a") return true;
        return false;
    }

    private static Matrix4x4 Matrix(Posegraph.Idle.BoneTransform t)
        => Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(t.Rotation)) * Matrix4x4.CreateTranslation(t.Translation);

    /// <summary>胳膊碰到这里时允许压进去多少：头发 4cm，别的 1.5cm。</summary>
    public static float Allow(Contact contact) => Surface.IsHair(contact.Bone) ? HairSoft : ArmSoft;

    /// <summary>
    /// 把这只手的掌心放到 palmTarget、手（j_te）转到 handRotation，全身协同。reference 是这次操作出发时的姿势（姿势项以它为准）。
    /// palmOf：当前姿势下的掌心位置（和 HandContact.PalmCenter 一致，按游戏里实际画的位置）。surface 不含这条胳膊自己。
    /// orientationWeight 为 0 时只管位置（比如拖动中）。返回最后的掌心误差（米）。
    /// </summary>
    public static float Solve(EditPose pose, PoseRig rig, Node hand, Vector3 palmTarget, Quaternion handRotation, EditPose reference,
                              Func<EditPose, Vector3?> palmOf, Surface? surface, HandShape? arm, float minBend,
                              float orientationWeight = OrientationWeight, int iterations = 30, (Vector3 Local, Vector3 Facing)? palmFacing = null,
                              float positionWeight = 1f, EditPose? surfacePose = null, bool lockSpine = false)
    {
        // lockSpine：摸的是自己上半身（脊椎以上）时脊椎不动——目标跟着脊椎走，转脊椎去够只会追着目标跑，贴合用的表面也不会跟着动
        // surfacePose：建表面（surface）时的姿势。这次求解会转脊椎，自己上半身的表面却停在建的时候：碰撞要按 ArmContact 倒推回去量
        // positionWeight：掌心到位有多重要。手原地不动地让开身体时要 10 倍（不然姿势项把手拉走一两厘米），第一次伸过去时 1 倍（够不着就算了，别为了那一点把腰扭坏）
        // palmFacing：只要求掌心朝向（手腕局部的掌心轴 → 世界里该朝的方向），绕掌心法线怎么转留给全身一起决定，
        // handRotation 只当很弱的偏好（手指大致朝哪边）。不给就要求整个手的朝向对上 handRotation
        var side = hand.Side;
        var joints = Chain(side).Where(j => pose.Has(j.Bone) && !(lockSpine && j.Bone.StartsWith("j_sebo", StringComparison.Ordinal))).ToArray();
        string upper = hand.Chain[0], fore = hand.Chain[1], wrist = hand.Chain[2];
        if (!joints.Any(j => j.Bone == wrist) || !joints.Any(j => j.Bone == upper)) return float.MaxValue;
        var dofs = joints.Sum(j => j.Kind == Kind.Ball ? 3 : 1);
        var refLocal = joints.ToDictionary(j => j.Bone, j => j.Bone == wrist && rig.HasBind(wrist) ? rig.BindLocal(wrist)
                                                            : reference.Has(j.Bone) ? reference.LocalRotation(j.Bone) : pose.LocalRotation(j.Bone));
        var start = pose.Clone();

        // 胳膊碰撞采样点：上臂、前臂（离肩 10cm 以外，腋下本来就贴着）
        int[] armSample = [];
        if (arm is not null && surface is not null)
        {
            var all = Enumerable.Range(0, arm.Count).ToArray();
            var step = Math.Max(1, all.Length / 110);
            armSample = all.Where((_, i) => i % step == 0).ToArray();
        }

        var lambda = 1e-3;
        var lastError = float.MaxValue;
        for (var it = 0; it < iterations; it++)
        {
            if (palmOf(pose) is not { } palm) break;
            var rows = new List<(double[] J, double R)>();

            // 1. 掌心位置
            var posError = palmTarget - palm;
            lastError = posError.Length();
            var axes = DofAxes(pose, joints);
            for (var c = 0; c < 3; c++)
            {
                var row = new double[dofs];
                for (var k = 0; k < dofs; k++)
                    if (axes[k].Affects(pose, wrist)) row[k] = Component(Vector3.Cross(axes[k].Axis, palm - axes[k].Origin), c) * positionWeight;
                rows.Add((row, Component(posError, c) * positionWeight));
            }
            // 2. 手的朝向（世界坐标的旋转误差向量）
            if (palmFacing is { } pf)
            {
                var p = Vector3.Normalize(Vector3.Transform(pf.Local, pose.Rotation(wrist)));
                var need = Vector3.Cross(p, pf.Facing);
                for (var c = 0; c < 3; c++)
                {
                    var row = new double[dofs];
                    for (var k = 0; k < dofs; k++)
                        if (axes[k].Affects(pose, wrist))
                        {
                            var a = axes[k].Axis;
                            row[k] = Component(a - p * Vector3.Dot(a, p), c) * FacingWeight;
                        }
                    rows.Add((row, Component(need, c) * FacingWeight));
                }
                var rotError = RotationVector(Quaternion.Normalize(handRotation * Quaternion.Conjugate(pose.Rotation(wrist))));
                for (var c = 0; c < 3; c++)
                {
                    var row = new double[dofs];
                    for (var k = 0; k < dofs; k++)
                        if (axes[k].Affects(pose, wrist)) row[k] = Component(axes[k].Axis, c) * TwistPreference;
                    rows.Add((row, Component(rotError, c) * TwistPreference));
                }
            }
            else if (orientationWeight > 0)
            {
                var rotError = RotationVector(Quaternion.Normalize(handRotation * Quaternion.Conjugate(pose.Rotation(wrist))));
                for (var c = 0; c < 3; c++)
                {
                    var row = new double[dofs];
                    for (var k = 0; k < dofs; k++)
                        if (axes[k].Affects(pose, wrist)) row[k] = Component(axes[k].Axis, c) * orientationWeight;
                    rows.Add((row, Component(rotError, c) * orientationWeight));
                }
            }
            // 3. 姿势：各关节离出发时的样子（世界坐标的偏差向量，近似对自己的自由度是单位阵）
            {
                var k0 = 0;
                foreach (var j in joints)
                {
                    var parent = pose.Parent(j.Bone);
                    var parentRot = parent is null ? Quaternion.Identity : pose.Rotation(parent);
                    var deviation = RotationVector(Quaternion.Normalize(pose.Rotation(j.Bone) * Quaternion.Conjugate(parentRot * refLocal[j.Bone])));
                    if (j.Kind == Kind.Ball)
                        for (var c = 0; c < 3; c++)
                        {
                            var row = new double[dofs];
                            row[k0 + c] = j.Stiffness;
                            rows.Add((row, -Component(deviation, c) * j.Stiffness));
                        }
                    else
                    {
                        var row = new double[dofs];
                        row[k0] = j.Stiffness;
                        rows.Add((row, -Vector3.Dot(deviation, axes[k0].Axis) * j.Stiffness));
                    }
                    k0 += j.Kind == Kind.Ball ? 3 : 1;
                }
            }
            // 4. 关节范围（有限差分：只动这个关节自己的自由度）
            {
                var k0 = 0;
                foreach (var j in joints)
                {
                    var n = j.Kind == Kind.Ball ? 3 : 1;
                    var excess = Excess(pose, rig, j, reference, start, minBend);
                    if (excess > 0)
                    {
                        var row = new double[dofs];
                        const float h = 0.01f;
                        for (var c = 0; c < n; c++)
                        {
                            var trial = pose.Clone();
                            Rotate(trial, j.Bone, axes[k0 + c], h);
                            row[k0 + c] = (Excess(trial, rig, j, reference, start, minBend) - excess) / h * LimitWeight;
                        }
                        rows.Add((row, -excess * LimitWeight));
                    }
                    k0 += n;
                }
            }
            // 5. 胳膊不压进身体（自己的、别人的）
            if (armSample.Length > 0 && arm is not null && surface is not null)
            {
                var points = arm.Skin(pose, rig, armSample);
                var shoulder = pose.Position(upper);
                for (var i = 0; i < points.Length; i++)
                {
                    var p = points[i];
                    if (Vector3.Distance(p, shoulder) < 0.10f) continue;
                    if (ArmContact(surface, p, 0.15f, pose, surfacePose) is not { } contact || OwnShoulder(contact, side)) continue;
                    var soft = Allow(contact);
                    if (contact.Signed > -soft) continue;
                    var bone = arm.BoneOf(armSample[i]);
                    var row = new double[dofs];
                    for (var k = 0; k < dofs; k++)
                        if (axes[k].Affects(pose, bone)) row[k] = Vector3.Dot(contact.Normal, Vector3.Cross(axes[k].Axis, p - axes[k].Origin)) * CollisionWeight;
                    rows.Add((row, (-soft - contact.Signed) * CollisionWeight));
                }
            }
            // 6. 肘自然下垂：肘比肩高多少轻罚多少
            {
                var elbowUp = pose.Position(fore).Y - pose.Position(upper).Y;
                if (elbowUp > 0)
                {
                    var row = new double[dofs];
                    var e = pose.Position(fore);
                    for (var k = 0; k < dofs; k++)
                        if (axes[k].Affects(pose, fore)) row[k] = -Vector3.Cross(axes[k].Axis, e - axes[k].Origin).Y * ElbowUpWeight;
                    rows.Add((row, elbowUp * ElbowUpWeight));   // 行取了负号：等价于 dY·Δ = −elbowUp
                }
            }

            // Levenberg–Marquardt：(JᵀJ + λ·diag) Δ = Jᵀr
            var cost = rows.Sum(r => r.R * r.R);
            var delta = Step(rows, dofs, lambda);
            if (delta is null) break;
            var before = pose.Clone();
            Apply(pose, joints, axes, delta);
            var after = Cost(pose, rig, joints, palmTarget, handRotation, orientationWeight, palmOf, refLocal, reference, start, minBend, arm, armSample, surface, upper, fore, wrist, palmFacing, positionWeight, surfacePose);
            if (after > cost)
            {
                // 变差了：退回，加大阻尼
                foreach (var j in joints) pose.SetRotation(j.Bone, before.Rotation(j.Bone));
                RestoreSubtreePositions(pose, before, joints[0].Bone);
                lambda = Math.Min(lambda * 10, 1e3);
                continue;
            }
            lambda = Math.Max(lambda * 0.3, 1e-6);
            if (lastError < 0.001f && cost - after < 1e-9) break;
        }
        if (palmOf(pose) is { } final) lastError = Vector3.Distance(final, palmTarget);
        Trace?.Invoke($"  body reach: palm error {lastError * 1000:0}mm, spine {Change(pose, reference, "j_sebo_a"):0}/{Change(pose, reference, "j_sebo_b"):0}/{Change(pose, reference, "j_sebo_c"):0}deg, " +
                      $"clavicle {Change(pose, reference, $"j_sako_{side}"):0}deg, elbow {(pose.Position(fore).Y - pose.Position(upper).Y) * 100:0}cm vs shoulder");
        return lastError;
    }

    // ------------------------------------------------------------------ 内部

    private readonly record struct Dof(string Bone, Vector3 Origin, Vector3 Axis)
    {
        /// <summary>这个自由度会不会动到 bone（bone 在它的子树里）。</summary>
        public bool Affects(EditPose pose, string bone)
        {
            for (var b = (string?)bone; b is not null; b = pose.Parent(b))
                if (b == Bone) return true;
            return false;
        }
    }

    private static Dof[] DofAxes(EditPose pose, Joint[] joints)
    {
        var list = new List<Dof>();
        foreach (var j in joints)
        {
            var origin = pose.Position(j.Bone);
            if (j.Kind == Kind.Ball)
            {
                list.Add(new Dof(j.Bone, origin, Vector3.UnitX));
                list.Add(new Dof(j.Bone, origin, Vector3.UnitY));
                list.Add(new Dof(j.Bone, origin, Vector3.UnitZ));
            }
            else list.Add(new Dof(j.Bone, origin, Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, pose.Rotation(j.Bone)))));
        }
        return list.ToArray();
    }

    private static void Rotate(EditPose pose, string bone, Dof dof, float angle)
        => pose.RotateSubtree(bone, pose.Position(bone), Quaternion.CreateFromAxisAngle(dof.Axis, angle));

    private static void Apply(EditPose pose, Joint[] joints, Dof[] axes, double[] delta)
    {
        var k0 = 0;
        foreach (var j in joints)
        {
            if (j.Kind == Kind.Ball)
            {
                var v = new Vector3((float)delta[k0], (float)delta[k0 + 1], (float)delta[k0 + 2]);
                var angle = MathF.Min(v.Length(), 10f * MathF.PI / 180f);
                if (angle > 1e-7f) pose.RotateSubtree(j.Bone, pose.Position(j.Bone), Quaternion.CreateFromAxisAngle(Vector3.Normalize(v), angle));
                k0 += 3;
            }
            else
            {
                var angle = Math.Clamp((float)delta[k0], -0.17f, 0.17f);
                // 铰链轴按现在的肘重新取（上面的关节转过以后它跟着转了）
                var axis = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, pose.Rotation(j.Bone)));
                if (MathF.Abs(angle) > 1e-7f) pose.RotateSubtree(j.Bone, pose.Position(j.Bone), Quaternion.CreateFromAxisAngle(axis, angle));
                k0 += 1;
            }
        }
    }

    /// <summary>撤回一步时，SetRotation 只管旋转；子树的位置由根骨骼的旋转带回来（根骨骼位置本来就没变）。</summary>
    private static void RestoreSubtreePositions(EditPose pose, EditPose before, string root)
    {
        foreach (var bone in pose.SubtreeOrdered(root))
            if (Vector3.DistanceSquared(pose.Position(bone), before.Position(bone)) > 1e-12f)
                pose.TranslateSubtree(bone, before.Position(bone) - pose.Position(bone));
    }

    private static double[]? Step(List<(double[] J, double R)> rows, int n, double lambda)
    {
        var a = new double[n, n];
        var b = new double[n];
        foreach (var (j, r) in rows)
            for (var x = 0; x < n; x++)
            {
                if (j[x] == 0) continue;
                b[x] += j[x] * r;
                for (var y = 0; y < n; y++) a[x, y] += j[x] * j[y];
            }
        for (var x = 0; x < n; x++) a[x, x] += lambda * (1 + a[x, x]) + 1e-9;
        return SolveLinear(a, b, n);
    }

    private static double[]? SolveLinear(double[,] a, double[] b, int n)
    {
        var m = new double[n, n + 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n] = b[i];
        }
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-14) return null;
            if (pivot != col)
                for (var j = 0; j <= n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (var j = col; j <= n; j++) m[r, j] -= f * m[col, j];
            }
        }
        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = m[i, n] / m[i, i];
        return x;
    }

    /// <summary>超出关节范围多少（弧度，没超为 0）：PoseOps.Limits 的总转角 / 扭转、肘的弯曲、手腕的屈伸 / 侧偏 / 旋前旋后、相对出发时的最大变化。</summary>
    private static float Excess(EditPose pose, PoseRig rig, Joint j, EditPose reference, EditPose start, float minBend)
    {
        const float rad = MathF.PI / 180f;
        var excess = 0f;
        // 相对这次操作出发时最多变多少（脊椎、锁骨不能转太多）
        if (reference.Has(j.Bone))
        {
            var change = Angle(Quaternion.Conjugate(reference.LocalRotation(j.Bone)) * pose.LocalRotation(j.Bone));
            excess += MathF.Max(0f, change - j.MaxChange * rad);
        }
        if (!rig.HasBind(j.Bone)) return excess;
        foreach (var limit in PoseOps.Limits)
        {
            if (limit.Bone != j.Bone) continue;
            if (limit.Kind == PoseOps.LimitKind.Bend)
            {
                // 肘：最多弯到 limit，最少留 minBend 的夹角（不反弓）
                var bend = PoseOps.Measure(pose, rig, limit) * rad;
                excess += MathF.Max(0f, bend - limit.Degrees * rad);
                excess += MathF.Max(0f, -bend + 2f * rad);
                excess += MathF.Max(0f, bend - (MathF.PI - minBend));
            }
            else
            {
                // 出发时就已经超了的不追究（只要不更糟）
                var allowed = MathF.Max(limit.Degrees, PoseOps.Measure(start, rig, limit));
                excess += MathF.Max(0f, PoseOps.Measure(pose, rig, limit) - allowed) * rad;
            }
        }
        if (j.Bone.StartsWith("j_te", StringComparison.Ordinal))
        {
            var bindInverse = Quaternion.Conjugate(rig.BindLocal(j.Bone));
            var (twist, sideways, bend) = HandContact.WristAngles(bindInverse * pose.LocalRotation(j.Bone));
            var (t0, s0, b0) = HandContact.WristAngles(bindInverse * start.LocalRotation(j.Bone));
            excess += (MathF.Max(0f, MathF.Abs(twist) - MathF.Max(85f, MathF.Abs(t0))) + MathF.Max(0f, MathF.Abs(sideways) - MathF.Max(25f, MathF.Abs(s0)))
                       + MathF.Max(0f, MathF.Abs(bend) - MathF.Max(65f, MathF.Abs(b0)))) * rad;
        }
        return excess;
    }

    private static double Cost(EditPose pose, PoseRig rig, Joint[] joints, Vector3 palmTarget, Quaternion handRotation, float orientationWeight,
                               Func<EditPose, Vector3?> palmOf, Dictionary<string, Quaternion> refLocal, EditPose reference, EditPose start,
                               float minBend, HandShape? arm, int[] armSample, Surface? surface, string upper, string fore, string wrist,
                               (Vector3 Local, Vector3 Facing)? palmFacing, float positionWeight, EditPose? surfacePose)
    {
        if (palmOf(pose) is not { } palm) return double.MaxValue;
        double cost = (palmTarget - palm).LengthSquared() * positionWeight * positionWeight;
        if (palmFacing is { } pf)
        {
            var p = Vector3.Normalize(Vector3.Transform(pf.Local, pose.Rotation(wrist)));
            cost += Vector3.Cross(p, pf.Facing).LengthSquared() * FacingWeight * FacingWeight;
            if (Vector3.Dot(p, pf.Facing) < 0) cost += FacingWeight * FacingWeight * 4;   // 掌心朝反了
            var rotError = RotationVector(Quaternion.Normalize(handRotation * Quaternion.Conjugate(pose.Rotation(wrist))));
            cost += rotError.LengthSquared() * TwistPreference * TwistPreference;
        }
        else if (orientationWeight > 0)
        {
            var rotError = RotationVector(Quaternion.Normalize(handRotation * Quaternion.Conjugate(pose.Rotation(wrist))));
            cost += rotError.LengthSquared() * orientationWeight * orientationWeight;
        }
        foreach (var j in joints)
        {
            var parent = pose.Parent(j.Bone);
            var parentRot = parent is null ? Quaternion.Identity : pose.Rotation(parent);
            var deviation = RotationVector(Quaternion.Normalize(pose.Rotation(j.Bone) * Quaternion.Conjugate(parentRot * refLocal[j.Bone])));
            cost += deviation.LengthSquared() * j.Stiffness * j.Stiffness;
            var e = Excess(pose, rig, j, reference, start, minBend) * LimitWeight;
            cost += e * e;
        }
        if (arm is not null && surface is not null && armSample.Length > 0)
        {
            var shoulder = pose.Position(upper);
            foreach (var p in arm.Skin(pose, rig, armSample))
            {
                if (Vector3.Distance(p, shoulder) < 0.10f) continue;
                if (ArmContact(surface, p, 0.15f, pose, surfacePose) is { } contact && !OwnShoulder(contact, wrist[^1..]) && contact.Signed < -Allow(contact))
                {
                    var d = (-Allow(contact) - contact.Signed) * CollisionWeight;
                    cost += d * d;
                }
            }
        }
        var elbowUp = pose.Position(fore).Y - pose.Position(upper).Y;
        if (elbowUp > 0) cost += elbowUp * elbowUp * ElbowUpWeight * ElbowUpWeight;
        return cost;
    }

    private static float Change(EditPose pose, EditPose reference, string bone)
        => pose.Has(bone) && reference.Has(bone) ? Angle(Quaternion.Conjugate(reference.LocalRotation(bone)) * pose.LocalRotation(bone)) * 180f / MathF.PI : 0f;

    private static float Angle(Quaternion q) => 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Normalize(q).W), 0f, 1f));

    /// <summary>旋转 → 旋转向量（轴 × 角度，弧度，取短的那一边）。</summary>
    private static Vector3 RotationVector(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        var s = MathF.Sqrt(MathF.Max(0f, 1f - q.W * q.W));
        if (s < 1e-7f) return new Vector3(q.X, q.Y, q.Z) * 2f;
        var angle = 2 * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
        return new Vector3(q.X, q.Y, q.Z) / s * angle;
    }

    private static float Component(Vector3 v, int c) => c == 0 ? v.X : c == 1 ? v.Y : v.Z;
}
