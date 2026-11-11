using System.Numerics;

namespace Posegraph.Editor;

/// <summary>
/// 手部吸附（姿势编辑器的可选功能）：拖手靠近身体表面（自己的或别人的）时，手掌贴上去、掌心朝向表面；
/// 手指顺着表面弯到指尖碰到为止；手和手指钻进表面时退出来；相邻手指不互相穿插。都是纯数学，表面由 Surface 给。
/// 手的朝向约定：游戏手指骨骼局部 +x 指向指尖、绕局部 −z 弯，所以弯曲时指尖往局部 −y 走——掌心朝向就是手指根部骨骼的局部 −y。
/// </summary>
public static class HandContact
{
    public const float SnapRadius = 0.06f;      // 手掌离表面这么近就吸上去
    public const float PalmGap = 0.012f;        // 掌心离表面（手掌的厚度一半）
    public const float FingerRadius = 0.008f;   // 手指粗细一半
    private const float FingerReach = 0.12f;    // 手指找表面的范围（要比手指弯一下走的距离大：埋得太深找不到表面，会被当成没碰到、越弯越深）

    private static readonly string[] Knuckles = ["hito", "naka", "kusu", "ko"];

    /// <summary>
    /// 掌心：手腕和四根手指根部的中点。给了 rig 就按游戏里实际画的位置算（有蒙皮修正时手的网格和 Brio 的手骨能差 2–3cm，
    /// 不按画的位置算的话掌心落在手背外面，「哪些顶点在掌心一侧」整个分错，贴合拟合把手背也当掌心去贴）
    /// </summary>
    public static Vector3? PalmCenter(EditPose pose, string side, PoseRig? rig = null)
    {
        var wrist = $"j_te_{side}";
        var roots = Knuckles.Select(k => $"j_{k}_a_{side}").Where(pose.Has).ToList();
        if (!pose.Has(wrist) || roots.Count == 0) return null;
        Vector3 At(string bone) => rig?.Rendered(pose, bone, pose.Position(bone)) ?? pose.Position(bone);
        var knuckles = roots.Aggregate(Vector3.Zero, (s, b) => s + At(b)) / roots.Count;
        return (At(wrist) + knuckles) / 2;
    }

    /// <summary>掌心朝向（手指根部骨骼的局部 −y；没有中指就用手腕的）。</summary>
    /// <summary>掌心朝向：有手的网格时按手掌皮肤的实际朝向（HandShape.PalmAxis），没有才按骨骼。</summary>
    public static Vector3 PalmNormal(EditPose pose, string side, HandShape? shape, PoseRig rig)
        => shape?.PalmAxis(pose, rig) is { } local && pose.Has($"j_te_{side}")
            ? Vector3.Normalize(Vector3.Transform(local, pose.Rotation($"j_te_{side}")))
            : PalmNormal(pose, side);

    public static Vector3 PalmNormal(EditPose pose, string side)
    {
        var bone = pose.Has($"j_naka_a_{side}") ? $"j_naka_a_{side}" : $"j_te_{side}";
        return Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, pose.Rotation(bone)));
    }

    /// <summary>
    /// 手掌贴到最近的表面上：掌心对着表面、离表面 PalmGap，手指方向尽量保持；整条胳膊用 IK 跟过去。
    /// 掌心附近没有表面时不动、返回 null。
    /// </summary>
    public static Contact? SnapPalm(EditPose pose, Node hand, Surface surface, float minBend)
    {
        if (PalmCenter(pose, hand.Side) is not { } palm || surface.Closest(palm, SnapRadius) is not { } contact) return null;
        var end = hand.Chain[2];
        var align = PoseIk.Swing(PalmNormal(pose, hand.Side), -contact.Normal);
        var rotation = Quaternion.Normalize(align * pose.Rotation(end));
        var wrist = contact.Point + contact.Normal * PalmGap + Vector3.Transform(pose.Position(end) - palm, align);
        PoseIk.SolveLimb(pose, hand.Chain[0], hand.Chain[1], end, wrist, rotation, minBend);
        return PalmCenter(pose, hand.Side) is { } now ? surface.Closest(now, SnapRadius) ?? contact : contact;
    }

    /// <summary>
    /// 一根手指和表面：钻进去了就伸直一点退出来（最多伸直 20°）；wrap 时（手掌贴着表面）再弯到指尖碰到表面为止，最多多弯 50°，
    /// 碰不到就保持原来的弯度（腰、胳膊这种往后弯走的表面，一路弯下去会勾成爪子）。
    /// 弯曲按手指自己的轴（各节绕局部 −z），指根弯一半。只看末节和指尖：指根附近的关节离掌心很近，表面一弯就会被误判成钻进去。
    /// 拇指根在手腕侧面，光伸直退不出来时再把它往表面外侧张开一点（最多 50°）；别的手指不这样转，不然会被掰到侧面去。
    /// </summary>
    public static void ConformFinger(EditPose pose, Node finger, IReadOnlyDictionary<string, Vector3> tips, Surface surface, bool wrap)
    {
        var chain = finger.Chain;
        if (chain.Length < 2 || !chain.All(pose.Has)) return;
        EditPose Curl(float angle)
        {
            var p = pose.Clone();
            for (var i = 0; i < chain.Length; i++)
            {
                var axis = Vector3.Transform(-Vector3.UnitZ, p.Rotation(chain[i]));
                p.RotateSubtree(chain[i], p.Position(chain[i]), Quaternion.CreateFromAxisAngle(axis, angle * (i == 0 ? 0.5f : 1f)));
            }
            return p;
        }
        IEnumerable<Vector3> Samples(EditPose p) => [p.Position(chain[^1]), PoseNodes.Point(p, finger, tips)];
        // 钻进表面多深（末节和指尖里最深的，留 2mm 余量；没碰到表面为负）
        float Depth(EditPose p)
        {
            var depth = float.NegativeInfinity;
            foreach (var point in Samples(p))
                if (surface.Closest(point, FingerReach) is { } c)
                    depth = MathF.Max(depth, FingerRadius - c.Signed - Tolerance);
            return depth;
        }
        const float step = 3f * MathF.PI / 180f;
        float? chosen = null;
        if (Depth(pose) > 0)
        {
            // 钻进去了：往伸直的方向找第一个不钻进去的角度，找不到就取钻得最浅的
            float best = 0, bestDepth = Depth(pose);
            for (var a = -step; a >= -MaxExtend; a -= step)
            {
                var d = Depth(Curl(a));
                if (d < bestDepth) (best, bestDepth) = (a, d);
                if (d <= 0) break;
            }
            chosen = best;
        }
        else if (wrap)
        {
            // 往弯的方向找第一个碰到表面的角度，再在这一格里二分到刚好贴上
            for (var a = step; a <= MaxWrap; a += step)
            {
                if (Depth(Curl(a)) <= 0) continue;
                float lo = a - step, hi = a;
                for (var i = 0; i < 8; i++)
                {
                    var mid = (lo + hi) / 2;
                    if (Depth(Curl(mid)) > 0) hi = mid;
                    else lo = mid;
                }
                chosen = lo;
                break;
            }
        }
        if (chosen is { } angle && MathF.Abs(angle) > 1e-5f)
        {
            var curled = Curl(angle);
            foreach (var bone in curled.SubtreeOrdered(chain[0])) pose.SetRotation(bone, curled.Rotation(bone));
        }
        if (!finger.Id.StartsWith("finger_oya", StringComparison.Ordinal)) return;
        var root = chain[0];
        var turned = 0f;
        for (var i = 0; i < 10 && turned < MaxThumbOpen; i++)
        {
            Vector3? deepest = null;
            var depth = 0f;
            var normal = Vector3.Zero;
            foreach (var point in Samples(pose))
                if (surface.Closest(point, FingerReach) is { } c && FingerRadius - c.Signed - Tolerance > depth)
                    (deepest, depth, normal) = (point, FingerRadius - c.Signed - Tolerance, c.Normal);
            if (deepest is not { } p) break;
            var pivot = pose.Position(root);
            var from = p - pivot;
            var to = p + normal * (depth + 0.001f) - pivot;
            if (from.LengthSquared() < 1e-8f) break;
            var swing = PoseIk.Swing(from, to);
            var swingAngle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(swing.W), 0f, 1f));
            if (turned + swingAngle > MaxThumbOpen)
                swing = Quaternion.Slerp(Quaternion.Identity, swing, (MaxThumbOpen - turned) / swingAngle);
            pose.RotateSubtree(root, pivot, swing);
            turned += swingAngle;
        }
    }

    private const float Tolerance = 0.002f;
    private const float MaxExtend = 20f * MathF.PI / 180f;
    private const float MaxWrap = 50f * MathF.PI / 180f;
    private const float MaxThumbOpen = 50f * MathF.PI / 180f;
    /// <summary>相邻手指靠得太近（穿插）时，绕掌心法线把两根往两边各掰开一点，最多掰 6°。</summary>
    public static void Spread(EditPose pose, IReadOnlyList<Node> fingers, IReadOnlyDictionary<string, Vector3> tips, Vector3 palmNormal)
    {
        const float minGap = 2 * FingerRadius * 0.85f;
        const float step = 2f * MathF.PI / 180f;
        var turned = new Dictionary<string, float>();
        IEnumerable<Vector3> Points(EditPose p, Node f) => f.Chain.Skip(1).Select(p.Position).Append(PoseNodes.Point(p, f, tips));
        float Gap(EditPose p, Node a, Node b) => Points(p, a).Min(x => Points(p, b).Min(y => Vector3.Distance(x, y)));
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var moved = false;
            for (var i = 0; i + 1 < fingers.Count; i++)
            {
                var (a, b) = (fingers[i], fingers[i + 1]);
                if (Gap(pose, a, b) >= minGap) continue;
                foreach (var f in new[] { a, b })
                {
                    if (turned.GetValueOrDefault(f.Id) >= 6f * MathF.PI / 180f) continue;
                    var root = f.Chain[0];
                    var other = f == a ? b : a;
                    // 往哪边转能拉开就往哪边
                    var plus = pose.Clone();
                    plus.RotateSubtree(root, plus.Position(root), Quaternion.CreateFromAxisAngle(palmNormal, step));
                    var minus = pose.Clone();
                    minus.RotateSubtree(root, minus.Position(root), Quaternion.CreateFromAxisAngle(palmNormal, -step));
                    var sign = Gap(plus, f, other) >= Gap(minus, f, other) ? 1f : -1f;
                    pose.RotateSubtree(root, pose.Position(root), Quaternion.CreateFromAxisAngle(palmNormal, sign * step));
                    turned[f.Id] = turned.GetValueOrDefault(f.Id) + step;
                    moved = true;
                }
            }
            if (!moved) break;
        }
    }

    /// <summary>
    /// 手拖动后的吸附。有手的网格（shape）时按真实形状贴（ApplyMesh）；没有时退回按骨骼估计（手掌贴表面、手指顺着弯、手指之间掰开）。返回接触点。
    /// </summary>
    public static Contact? Apply(EditPose pose, PoseRig rig, Node hand, IReadOnlyList<Node> all, IReadOnlyDictionary<string, Vector3> tips, Surface surface,
                                 HandShape? shape, float minBend, bool quick = false, HandShape? arm = null, float fit = 1f,
                                 Surface? region = null, Vector3? focus = null, EditPose? original = null)
    {
        _level = fit;
        _original = original;
        _soft = 0;
        if (shape is null || rig.BindOffset($"j_naka_a_{hand.Side}", hand.Chain[2]) is null)
            return ApplySimple(pose, hand, all, tips, surface, minBend);
        // 只动这条胳膊（躯干不动）。试过连脊椎、锁骨一起的全身数值求解（BodyReach）：为了够到会把上身、手腕拧出正常范围，已停用——
        // ApplyMesh 里 bodyReach 那几段和 BodyReach.cs 是它留下的，确认新做法没问题后一起删
        return ApplyMesh(pose, rig, hand, all, surface, shape, minBend, quick, arm, region, focus, false);
    }

    /// <summary>
    /// 一次吸附的结果好不好（越大越好）：手掌皮肤贴着的比例 − 悬空的比例 − 拇指根肉垫压太深（PalmQuality，×100），
    /// 减去胳膊压进身体（自己的、别人的，超出软组织的部分，每毫米 1 分），减去手腕超出活动范围（每度 0.5 分）。
    /// </summary>
    private static float FinalScore(EditPose pose, PoseRig rig, Node hand, HandShape shape, HandShape? arm, Surface surface, EditPose start)
    {
        var side = hand.Side;
        string upper = hand.Chain[0], fore = hand.Chain[1], wrist = hand.Chain[2];
        var axis = shape.PalmAxis(pose, rig) ?? Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, rig.BindLocal($"j_naka_a_{side}")));
        var quality = PalmQuality(pose, rig, hand, shape, surface, axis);
        var armInside = 0f;
        if (arm is not null)
        {
            var followed = pose.Clone();
            PoseOps.FollowHelpers(null, followed, rig, new HashSet<string> { upper, fore, wrist });
            var shoulder = followed.Position(upper);
            foreach (var q in arm.Skin(followed, rig))
                if (Vector3.Distance(q, shoulder) >= 0.10f && BodyReach.ArmContact(surface, q, 0.15f, followed, start) is { } c
                    && !BodyReach.OwnShoulder(c, side) && c.Signed < -BodyReach.Allow(c))
                    armInside = MathF.Max(armInside, -BodyReach.Allow(c) - c.Signed);
        }
        var (twist, sideways, bend) = WristAngles(Quaternion.Conjugate(rig.BindLocal(wrist)) * pose.LocalRotation(wrist));
        var wristOver = MathF.Max(0, MathF.Abs(twist) - WristTwist) + MathF.Max(0, MathF.Abs(sideways) - WristSide) + MathF.Max(0, MathF.Abs(bend) - WristBend);
        return quality.Score * 100f - armInside * 1000f - wristOver * 0.5f;
    }

    // ------------------------------------------------------------------ 按手的真实形状贴（参考 VR 抓取：手掌对齐表面、手指逐节合拢到碰到为止）

    // ---- 贴合度（0 松、1 自然、2 贴紧、3 压紧，中间连续）和这只手贴着的部位有多软（臀、胸、腹、大腿 1，肩、手臂、头 0）。
    // 游戏里身体不会变形，「压紧」只能让手掌、指肚稍微压进皮肤来表现；软的部位压得多一点。解算在一个线程里，按调用设置
    [ThreadStatic] private static float _level;
    // 这次操作开始前的姿势（拖动 / 点选之前）：量「胳膊本来就贴着身体多少」要用它，不能用已经被拖到目标上的姿势
    [ThreadStatic] private static EditPose? _original;
    [ThreadStatic] private static float _soft;

    private static float ByLevel(float loose, float natural, float snug, float pressed)
    {
        var l = Math.Clamp(_level, 0f, 3f);
        return l < 1 ? loose + (natural - loose) * l : l < 2 ? natural + (snug - natural) * (l - 1) : snug + (pressed - snug) * (l - 2);
    }

    private static float Press => MathF.Max(0, _level - 1) / 2f * _soft;                       // 软的部位额外压进去的比例
    private static float Gap => ByLevel(0.006f, 0.002f, 0f, -0.002f) - Press * 0.004f;          // 手掌皮肤离表面（负数是压进去）
    private static float FingerTarget => ByLevel(0.004f, 0f, -0.001f, -0.003f) - Press * 0.003f; // 手指贴到离表面多少停
    private static float TouchDistance => FingerTarget + 0.002f;                                // 比这近算碰到
    private static float Inside => FingerTarget - 0.002f;                                       // 比这深算穿模
    private static float MaxClose => ByLevel(0.45f, 0.65f, 0.85f, 0.95f);                       // 手指最多合拢到满弯的多少
    private static float DistalCap => ByLevel(0.15f, 0.25f, 0.4f, 0.5f);                        // 前一节贴住后，后面的关节最多再弯多少
    private static float PadSquash => ByLevel(0.003f, 0.006f, 0.010f, 0.014f) + Press * 0.004f;  // 掌根、拇指根肉垫允许「压扁」（陷进去）多少

    /// <summary>胳膊压进身体多少以内算「贴着挤压」，不算穿模。</summary>
    private const float ArmSoft = 0.015f;

    // 手腕活动范围（度，真人量测：屈约 70、伸约 65、尺偏约 30、桡偏约 20；这里两边都按 25 算），扭转大部分由前臂带
    private const float WristBend = 45f, WristSide = 35f, WristTwist = 80f;

    // ---- 关节范围，按 2238 帧游戏动画（后端 outputs/samples）统计，相对绑定姿势、「旋后 / 外旋为正」（左手取反后左右一致）。
    // 硬范围 ≈ 第 1 / 99 百分位，舒适范围 ≈ 第 5 / 95 百分位。和以前「左右对称 ±85°」差得最多的是扭转：
    // 绑定姿势掌心朝下，前臂已经旋前了大半，再往旋前只有 45°，往旋后有 80°；屈伸在游戏动画里很小（95% 的帧在 ±25° 内）。
    private static readonly (float Min, float Max) WristTwistHard = (-45f, 80f), WristTwistEasy = (-35f, 65f);
    private static readonly (float Min, float Max) WristSideHard = (-35f, 45f), WristSideEasy = (-25f, 38f);
    private static readonly (float Min, float Max) WristBendHard = (-45f, 45f), WristBendEasy = (-25f, 25f);
    private const float WristTotalHard = 90f;
    private static readonly (float Min, float Max) UpperTwistHard = (-85f, 100f), UpperTwistEasy = (-50f, 78f);

    private static float Outside(float value, (float Min, float Max) range) => MathF.Max(0f, MathF.Max(range.Min - value, value - range.Max));

    /// <summary>左手的扭转 / 侧偏方向和右手相反：乘这个数以后「旋后、外旋」都是正的。</summary>
    private static float SideSign(string bone) => bone.EndsWith("_l", StringComparison.Ordinal) ? -1f : 1f;

    /// <summary>
    /// 手腕姿势的代价。deviation = 绑定姿势的逆 × 手腕相对前臂的旋转。Over：超出硬范围多少度（含总转角超过 90° 的部分）；
    /// Strain：超出舒适范围的度数 + 一点点和角度成正比的量（同样可行时选手腕更松的）。
    /// </summary>
    private static (float Over, float Strain) WristCost(Quaternion deviation, string wrist)
    {
        var sign = SideSign(wrist);
        var (twist, sideways, bend) = WristAngles(deviation);
        twist *= sign;
        sideways *= sign;
        var d = Quaternion.Normalize(deviation);
        var total = 2 * MathF.Acos(Math.Clamp(MathF.Abs(d.W), 0f, 1f)) * 180f / MathF.PI;
        var over = Outside(twist, WristTwistHard) + Outside(sideways, WristSideHard) + Outside(bend, WristBendHard) + MathF.Max(0f, total - WristTotalHard);
        var strain = Outside(twist, WristTwistEasy) + Outside(sideways, WristSideEasy) + Outside(bend, WristBendEasy)
                     + 0.15f * (MathF.Abs(twist) + MathF.Abs(sideways) + MathF.Abs(bend));
        return (over, strain);
    }

    /// <summary>上臂绕自己轴线的扭转（度，外旋为正）超出硬范围 / 舒适范围多少。拧过头肩膀的蒙皮会鼓成一团。</summary>
    private static (float Over, float Strain, float Twist) UpperTwistCost(EditPose pose, PoseRig rig, string upper)
        => rig.HasBind(upper) ? UpperTwistCost(Quaternion.Conjugate(rig.BindLocal(upper)) * pose.LocalRotation(upper), upper) : (0f, 0f, 0f);

    /// <summary>deviation = 绑定姿势的逆 × 上臂相对锁骨的旋转。</summary>
    private static (float Over, float Strain, float Twist) UpperTwistCost(Quaternion deviation, string upper)
    {
        var d = Quaternion.Normalize(deviation);
        if (d.W < 0) d = new Quaternion(-d.X, -d.Y, -d.Z, -d.W);
        var twist = 2 * MathF.Atan2(d.X, d.W) * 180f / MathF.PI * SideSign(upper);
        return (Outside(twist, UpperTwistHard), Outside(twist, UpperTwistEasy), twist);
    }

    /// <summary>手指各节：放松时的弯曲和合拢时最多再弯多少（度，绕局部 −z）。中节（PIP）、末节（DIP）联动：末节约为中节的 2/3。</summary>
    private static (float Base, float Range)[] FingerRanges(Node finger)
        => finger.Id.StartsWith("finger_oya", StringComparison.Ordinal)
            ? [(0f, 30f), (5f, 55f)]
            : [(5f, 75f), (8f, 92f), (5f, 62f)];

    /// <summary>quick：拖动中每帧算的快速版——手掌朝向、贴合、肘让开都做，手指只放松不合拢（松手时再完整算一次）。</summary>
    private static Contact? ApplyMesh(EditPose pose, PoseRig rig, Node hand, IReadOnlyList<Node> all, Surface surface, HandShape shape, float minBend,
                                      bool quick, HandShape? arm, Surface? region, Vector3? focus, bool bodyReach)
    {
        var side = hand.Side;
        string upper = hand.Chain[0], fore = hand.Chain[1], wrist = hand.Chain[2];
        // 手掌往哪儿贴：选了部位就只看那个部位（防穿模、手指、胳膊还是看全身）
        // focus：「贴到部位」时点到的那一点，手掌以它为中心贴；胳膊够不着就弯腰去够
        var place = region ?? surface;
        if (Trace is not null) { _watch ??= System.Diagnostics.Stopwatch.StartNew(); _watch.Restart(); }
        // 够不着时不再弯腰去够：以前会把上身往目标歪最多 30°（并排站时整个人歪进对方头里）。躯干保持原样，手够到哪算哪
        // 肘最多弯到 145°（游戏动画里最多约 130°，但摸自己同侧的胸、肩时手腕离肩不到 20cm，135° 够不着）
        minBend = MathF.Min(minBend, 35f * MathF.PI / 180f);
        if (!quick) LastIssue = null;
        var reachMiss = 0f;      // ChooseArm 挑中的摆法手腕离目标还差多少（够不着）
        var reachTooFar = true;  // 差是因为太远（true）还是离肩太近、肘弯不过来（false）
        var chosenTilt = 0f;     // 挑中的摆法手掌偏开表面多少度（0 = 贴平）
        var chosenPosed = 0f;    // 用户是不是已经把手摆到附近了（0…1）
        // 手掌得偏开 70° 才搭得上（几乎是手背、掌缘碰着），又不是用户自己摆成这样的：真人用这只手做不到，不硬摆
        bool Awkward() => chosenTilt >= 70f && chosenPosed < 0.5f;
        var chosenInside = 0f;   // 挑中的摆法胳膊压进身体最深多少
        var chosenHard = 0f;     // 其中压进头、脖子（硬的部位）最深多少
        if (PalmCenter(pose, side, rig) is not { } palm) return null;
        var contactAt = focus is { } f0 ? place.Closest(f0, 0.05f) : place.Closest(palm, region is null ? SnapRadius : 0.25f);
        if (contactAt is not { } hitAt) return null;
        // 掌心朝向按手掌大小的一片表面的平均法线，不按点到的那一个三角形：低模身体（别人穿的原版身体、低面数装备）棱面很明显，
        // 点到胸肌下沿朝下的一个小面时，手会掌心朝上横在胸前、再被推开（实测偏 7cm）。真人的手也是贴合一整片
        var contact = PatchNormals ? hitAt with { Normal = PatchNormal(place, hitAt) } : hitAt;
        // 够不着（太远），或者离肩太近、肘弯不过来：不动躯干去够，把落点沿表面挪到够得着的最近处
        var slid = false;
        if (!quick)
        {
            var shoulderAt = pose.Position(upper);
            float upperLen = Vector3.Distance(pose.Position(upper), pose.Position(fore)), foreLen = Vector3.Distance(pose.Position(fore), pose.Position(wrist));
            var handLen = Vector3.Distance(pose.Position(wrist), palm);
            var far = (upperLen + foreLen) * 0.985f + handLen * 0.3f;
            var near = MathF.Max(0.03f, MathF.Sqrt(MathF.Max(0f, upperLen * upperLen + foreLen * foreLen - 2 * upperLen * foreLen * MathF.Cos(minBend))) - handLen * 0.6f);
            for (var i = 0; i < 4; i++)
            {
                var want = contact.Point + contact.Normal * 0.02f;
                var distance = Vector3.Distance(want, shoulderAt);
                if (distance <= far && distance >= near) break;
                var pulled = shoulderAt + (want - shoulderAt) * (Math.Clamp(distance, near, far) / MathF.Max(distance, 1e-4f));
                if (place.Closest(pulled, 0.3f) is not { } moved) break;
                contact = PatchNormals ? moved with { Normal = PatchNormal(place, moved) } : moved;
                slid = true;
            }
            if (slid) Trace?.Invoke($"  out of reach: contact moved {Vector3.Distance(contact.Point, hitAt.Point) * 100:0}cm along the surface");
        }
        Trace?.Invoke(FormattableString.Invariant($"  contact normal {hitAt.Normal.X:0.00} {hitAt.Normal.Y:0.00} {hitAt.Normal.Z:0.00} -> patch {contact.Normal.X:0.00} {contact.Normal.Y:0.00} {contact.Normal.Z:0.00}, point {contact.Point.X:0.000} {contact.Point.Y:0.000} {contact.Point.Z:0.000}"));
        var fingers = all.Where(n => n.Kind == NodeKind.Finger && n.Hand == hand.Id).ToList();
        var palmAxis = MeshPalm ? shape.PalmAxis(pose, rig) : null;
        _soft = BodyRegions.Softness(contact.Bone);

        // 1–3 整段（朝向、胳膊、贴实）包成 Placement，按手掌皮肤的朝向算一遍；个别情况（手伸到背后摸臀部）
        // 手腕转不到那个角度、手掌有四分之一以上悬空时，按中指根骨骼的朝向再算一遍，取贴得好的
        var offset = Vector3.Transform(pose.Position(wrist) - palm, Quaternion.Conjugate(pose.Rotation(wrist)));
        var palmTarget = contact.Point + contact.Normal * 0.02f;
        var axisUse = palmAxis;
        Quaternion? chosenRotation = null;   // ChooseArm 挑中的手的朝向
        Dictionary<string, Quaternion[]> rests = null!;
        // 换朝向重算时要连脊椎一起退回（全身协同会动脊椎）
        var shoulderRoot = bodyReach && pose.Has("j_sebo_a") ? "j_sebo_a" : pose.Parent(upper) ?? upper;
        var startPose = pose.Clone();
        Placement();
        if (!quick && (reachMiss > 0.03f || chosenInside > 0.045f || Awkward()))
        {
            // 够不着（落点沿表面挪过以后还是够不着），或者只有胳膊穿过身体才够得着：不硬贴、不动躯干——
            // 胳膊退回这次操作之前的样子，在状态栏告诉用户原因（要么贴好，要么不动；不留一只悬在半路或穿在身体里的手）
            var back = _original ?? startPose;
            foreach (var bone in pose.SubtreeOrdered(pose.Parent(upper) ?? upper))
                if (back.Has(bone)) pose.SetRotation(bone, back.Rotation(bone));
            LastIssue = reachMiss > 0.03f
                ? "这只手够不着那里（躯干保持不动），所以没有动：换另一只手，或让两人靠近一些"
                : chosenInside <= 0.045f
                ? "这只手的手掌转不到那个方向（手腕、肩已经到头），所以没有动：换另一只手，或先把手大致摆过去再贴"
                : "这只手要到那里，胳膊得穿过身体，所以没有动：换另一只手，或换个贴的地方";
            Trace?.Invoke($"  not placed: wrist misses by {reachMiss * 1000:0}mm, arm inside {chosenInside * 1000:0}mm");
            return null;
        }
        // （以前这里在手掌大半悬空时会按骨骼的掌心朝向整段重算一遍：现在手腕转不过去的情况由「斜着搭」的候选处理，不再重算）
        if (bodyReach && !quick && palmAxis is not null && PalmQuality(pose, rig, hand, shape, surface, palmAxis.Value) is { Far: > 0.25f } first)
        {
            var meshPose = pose.Clone();
            var (meshTarget, meshRests) = (palmTarget, rests);
            foreach (var bone in pose.SubtreeOrdered(shoulderRoot)) pose.SetRotation(bone, startPose.Rotation(bone));
            axisUse = null;
            palmTarget = contact.Point + contact.Normal * 0.02f;
            Placement();
            var second = PalmQuality(pose, rig, hand, shape, surface, palmAxis.Value);
            Trace?.Invoke($"  palm axis fallback: mesh {first.Score:0.00} (far {first.Far:0.00}) vs bone {second.Score:0.00} (far {second.Far:0.00})");
            if (second.Score <= first.Score)
            {
                foreach (var bone in pose.SubtreeOrdered(shoulderRoot)) pose.SetRotation(bone, meshPose.Rotation(bone));
                (palmTarget, rests) = (meshTarget, meshRests);
            }
        }

        void Placement()
        {
        // 1. 手的朝向：掌心对着表面，绕表面法线转到手腕最自然的角度（顺着前臂），再按手腕活动范围限一下。
        //    先粗放一次，让胳膊别穿过身体（肘让开），再按让开后的前臂重新挑一次朝向
        // 把掌心放到 palmTarget：手腕超出活动范围被拉回来时手会绕手腕转、掌心跑偏，按新的朝向再挪回去，来回几次
        // 掌心轴（手腕局部）：手掌皮肤的实际朝向，没有就用中指根骨骼的 −y
        var palmLocal = axisUse ?? Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, rig.BindLocal($"j_naka_a_{side}")));
        // 摸自己上半身时不转脊椎（见 BodyReach 的 lockSpine）
        var selfUpper = contact.Actor == 0 && pose.Has(contact.Bone) && BodyReach.UnderSpine(pose, contact.Bone);
        void Place(Quaternion rotation, bool facingOnly = false)
        {
            if (bodyReach)
            {
                // 只要求掌心朝向时手绕法线可以转；给定完整朝向时（贴实以后挪回原处）朝向要钉死，不然手腕往中立回弹会把贴好的手掌掰歪
                BodyReach.Solve(pose, rig, hand, palmTarget, rotation, startPose, p => PalmCenter(p, side, rig), surface, arm, minBend, surfacePose: startPose, lockSpine: selfUpper, iterations: quick ? 12 : 30,
                                orientationWeight: facingOnly ? 0.12f : 1.0f, palmFacing: facingOnly ? (palmLocal, -contact.Normal) : null,
                                positionWeight: facingOnly ? 1f : 10f);
                return;
            }
            for (var i = 0; i < 4; i++)
            {
                PoseIk.SolveLimb(pose, upper, fore, wrist, palmTarget + Vector3.Transform(offset, rotation), rotation, minBend);
                ClampWrist(pose, rig, fore, wrist);
                rotation = pose.Rotation(wrist);
                if (PalmCenter(pose, side, rig) is not { } now || Vector3.DistanceSquared(now, palmTarget) < 0.004f * 0.004f) break;
            }
        }
        // 手的朝向和肘的朝向一起挑：以前先按「现在的前臂」挑手腕最舒服的朝向再解胳膊，原来的前臂方向不对时（比如摸同侧胸口）
        // 所有朝向都别扭，只能挑最不坏的——手竖着立在胸口边上。现在 12 个朝向 × 12 个肘朝向都先把胳膊解出来再比：
        // 手腕弯曲 / 侧偏 / 扭转、肘的高度（自然垂着最舒服），挑整体最自然的
        void Elbow(string step)
        {
            if (Trace is null) return;
            // 调试：胳膊的真实网格（辅助骨跟上以后）压进表面最深多少、在哪
            var worst = (Depth: 0f, Where: "");
            if (arm is not null)
            {
                var followed = pose.Clone();
                PoseOps.FollowHelpers(null, followed, rig, new HashSet<string> { upper, fore, wrist });
                var shoulderAt = followed.Position(upper);
                var points = arm.Skin(followed, rig);
                for (var i = 0; i < points.Length; i++)
                    if (Vector3.Distance(points[i], shoulderAt) >= 0.10f && surface.Closest(points[i], 0.15f) is { } c && !BodyReach.OwnShoulder(c, side) && -c.Signed > worst.Depth)
                        worst = (-c.Signed, $"{arm.BoneOf(i)} {Vector3.Distance(points[i], shoulderAt) * 100:0}cm from shoulder into {(c.Actor == 0 ? "" : "other ")}{c.Bone}");
            }
            Trace($"  elbow {step}: {(pose.Position(fore).Y - pose.Position(upper).Y) * 100:0}cm vs shoulder, palm gap {(PalmCenter(pose, side, rig) is { } pc && place.Closest(pc, 0.2f) is { } pcc ? pcc.Signed * 1000 : -1):0}mm, arm mesh {worst.Depth * 1000:0}mm {worst.Where}");
        }
        if (bodyReach && !quick)
        {
            // 多起点：胳膊候选里挑几个不同的（最省力的、肘朝下最好的、肘位置不一样的），每个都让全身一起解一遍（掌心朝向对上、
            // 绕掌心法线怎么转由全身决定），比谁最自然。以前只挑一个候选、手的朝向先定死，胳膊只能硬凑，肘就翻上去搭在别人头上
            var starts = ChooseArm();
            MultiStart(starts);
            Elbow("multi-start");
        }
        else
        {
            chosenRotation = null;
            if (!quick)
            {
                ChooseArm();
                // 解出来手腕还是到不了（估的够得着范围不准）：按差的量把落点再沿表面挪一点，最多三次
                // 胳膊得穿过身体才够得着（比如胳膊横过自己胸口伸直去够）也算够不着：落点往肩这边每次挪 5cm
                // 手腕 / 前臂顶进对方的头、脖子 1cm 以上（硬的，挤不进去）也挪：点在歪着的头底下时，手往肩这边让一点
                for (var retry = 0; retry < 3 && (reachMiss > 0.01f || chosenInside > 0.03f || chosenHard > 0.01f); retry++)
                {
                    var (missBefore, insideBefore, hardBefore) = (reachMiss, chosenInside, chosenHard);
                    var before = (Pose: pose.Clone(), contact, palmTarget, reachTooFar, chosenTilt, chosenRotation, slid);
                    var shoulderAt = pose.Position(upper);
                    var want = contact.Point + contact.Normal * 0.02f;
                    var distance = Vector3.Distance(want, shoulderAt);
                    var moveTo = reachMiss > 0.01f ? distance - (reachMiss + 0.01f) * (reachTooFar ? 1f : -1f) : distance - 0.05f;
                    if (distance < 1e-4f || place.Closest(shoulderAt + (want - shoulderAt) * (moveTo / distance), 0.3f) is not { } moved) break;
                    contact = PatchNormals ? moved with { Normal = PatchNormal(place, moved) } : moved;
                    palmTarget = contact.Point + contact.Normal * 0.02f;
                    slid = true;
                    Trace?.Invoke($"  still {reachMiss * 1000:0}mm short ({(reachTooFar ? "too far" : "too close")}): contact moved to {Vector3.Distance(contact.Point, hitAt.Point) * 100:0}cm from the click");
                    ChooseArm();
                    // 挪了落点也没好转（差的量、压进去的量都没少 5mm 以上）：再挪也是白算，停（以前这种够不着的点要白算三遍，三秒多）
                    if (reachMiss > missBefore - 0.005f && chosenInside > insideBefore - 0.005f && chosenHard > hardBefore - 0.005f)
                    {
                        // 只是为了让开头 / 脖子才挪的、挪了又没用：回到挪之前（落点还在点的地方）
                        if (missBefore <= 0.01f && insideBefore <= 0.03f)
                        {
                            foreach (var bone in pose.SubtreeOrdered(upper)) pose.SetRotation(bone, before.Pose.Rotation(bone));
                            (contact, palmTarget, reachTooFar, chosenTilt, chosenRotation, slid) = (before.contact, before.palmTarget, before.reachTooFar, before.chosenTilt, before.chosenRotation, before.slid);
                            (reachMiss, chosenInside, chosenHard) = (missBefore, insideBefore, hardBefore);
                        }
                        break;
                    }
                }
            }
            Elbow("chosen");
            if (!quick && (reachMiss > 0.03f || chosenInside > 0.045f || Awkward())) return;   // 够不着 / 得穿过身体：后面的贴实、手指都不做（外面会把胳膊退回去）
            // 挑好了就用挑中的朝向（可能是斜着搭的）；拖动中没挑：按现在的前臂挑一个手腕最舒服的
            Place(chosenRotation ?? HandRotation(pose, rig, hand, contact.Normal, place, contact.Point, axisUse), facingOnly: bodyReach);
            Elbow("placed");
            if (quick) Avoid(false);   // 完整解算时胳膊已经连同穿模、关节范围一起挑好了；事后再单独转肘只会把关节拧出范围
            Elbow("avoided");
            Place(chosenRotation ?? HandRotation(pose, rig, hand, contact.Normal, place, contact.Point, axisUse), facingOnly: bodyReach);
            Elbow("placed again");
        }

        void MultiStart(List<(float Cost, EditPose Trial, Quaternion Rotation)> candidates)
        {
            var picked = new List<(float Cost, EditPose Trial, Quaternion Rotation)>();
            // 两个起点算不同：肘的位置差 10cm 以上，或者手的朝向差 40° 以上（同一个肘位置，手掌绕法线转的方向不同，最后手腕舒不舒服差很多）
            bool Distinct((float Cost, EditPose Trial, Quaternion Rotation) a, (float Cost, EditPose Trial, Quaternion Rotation) c)
                => Vector3.Distance(a.Trial.Position(fore), c.Trial.Position(fore)) >= 0.10f
                   || 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(a.Rotation, c.Rotation)), 0f, 1f)) > 40f * MathF.PI / 180f;
            void Take((float Cost, EditPose Trial, Quaternion Rotation) c, int limit)
            {
                if (picked.Count >= limit || picked.Any(p => !Distinct(p, c))) return;
                picked.Add(c);
            }
            var sorted = candidates.OrderBy(c => c.Cost).ToList();
            if (sorted.Count > 0) Take(sorted[0], 1);
            // 肘朝下的至少试三个（真人伸手摸东西多半肘朝下；先定死手的朝向时它们看起来手腕很别扭，全身一起调以后往往最自然）
            foreach (var c in sorted.Where(c => c.Trial.Position(fore).Y < c.Trial.Position(upper).Y - 0.05f)) Take(c, 3);
            foreach (var c in sorted) Take(c, 4);
            EditPose? best = null;
            var bestScore = float.MaxValue;
            var root = pose.Has("j_sebo_a") ? "j_sebo_a" : upper;
            foreach (var c in picked)
            {
                var trial = pose.Clone();
                foreach (var bone in trial.SubtreeOrdered(upper)) trial.SetRotation(bone, c.Trial.Rotation(bone));
                BodyReach.Solve(trial, rig, hand, palmTarget, c.Rotation, startPose, p => PalmCenter(p, side, rig), surface, arm, minBend, surfacePose: startPose, lockSpine: selfUpper, iterations: 15,
                                palmFacing: (palmLocal, -contact.Normal));
                var score = Natural(trial);
                if (Trace is not null)
                {
                    var (tw, sd, bd) = WristAngles(Quaternion.Conjugate(rig.BindLocal(wrist)) * trial.LocalRotation(wrist));
                    var facingNow = Vector3.Normalize(Vector3.Transform(palmLocal, trial.Rotation(wrist)));
                    Trace($"  start elbow {(c.Trial.Position(fore).Y - c.Trial.Position(upper).Y) * 100:0}cm -> {(trial.Position(fore).Y - trial.Position(upper).Y) * 100:0}cm, natural {score:0} " +
                          $"(wrist tw{tw:0} side{sd:0} bend{bd:0}, facing {MathF.Acos(Math.Clamp(Vector3.Dot(facingNow, -contact.Normal), -1f, 1f)) * 180f / MathF.PI:0}deg, " +
                          $"miss {(PalmCenter(trial, side, rig) is { } pp ? Vector3.Distance(pp, palmTarget) * 1000 : -1):0}mm)");
                }
                if (score < bestScore) (bestScore, best) = (score, trial);
            }
            // 最好的也很别扭（比如并排站着摸旁边人胸口正中：肘朝下手腕要拧 100°，肘朝上胳膊就从他头上绕）时，真人会把身子转过去。
            // 从「上身先转 ±18°」的样子再试肘朝下的起点（脊椎三节分摊），转了多少照样算进自然度里
            if (bestScore > 120f && pose.Has("j_sebo_a") && !selfUpper)
            {
                var downs = picked.Where(c => c.Trial.Position(fore).Y < c.Trial.Position(upper).Y - 0.05f).Take(1).ToList();
                foreach (var c in downs)
                    foreach (var sign in new[] { 1f, -1f })
                    {
                        var trial = pose.Clone();
                        foreach (var (bone, share) in new[] { ("j_sebo_a", 0.4f), ("j_sebo_b", 0.3f), ("j_sebo_c", 0.3f) })
                            if (trial.Has(bone)) trial.RotateSubtree(bone, trial.Position(bone), Quaternion.CreateFromAxisAngle(Vector3.UnitY, sign * share * 18f * MathF.PI / 180f));
                        // 胳膊按候选的样子（相对锁骨），手的朝向跟着上身一起转
                        var parentNow = trial.Parent(upper) is { } pu ? trial.Rotation(pu) : Quaternion.Identity;
                        var parentThen = c.Trial.Parent(upper) is { } pt ? c.Trial.Rotation(pt) : Quaternion.Identity;
                        var turn = Quaternion.Normalize(parentNow * Quaternion.Conjugate(parentThen));
                        foreach (var bone in trial.SubtreeOrdered(upper)) trial.SetRotation(bone, Quaternion.Normalize(turn * c.Trial.Rotation(bone)));
                        BodyReach.Solve(trial, rig, hand, palmTarget, Quaternion.Normalize(turn * c.Rotation), startPose, p => PalmCenter(p, side, rig), surface, arm, minBend, surfacePose: startPose, lockSpine: selfUpper,
                                        iterations: 15, palmFacing: (palmLocal, -contact.Normal));
                        var score = Natural(trial);
                        Trace?.Invoke($"  turned start {sign * 18:0}deg, elbow {(c.Trial.Position(fore).Y - c.Trial.Position(upper).Y) * 100:0}cm -> {(trial.Position(fore).Y - trial.Position(upper).Y) * 100:0}cm, natural {score:0}");
                        if (score < bestScore) (bestScore, best) = (score, trial);
                    }
            }
            if (best is not null)
                foreach (var bone in pose.SubtreeOrdered(root)) pose.SetRotation(bone, best.Rotation(bone));
        }

        // 姿势有多不自然（越小越好）：手腕的角度、肘的高度、胳膊压进身体、脊椎 / 锁骨动了多少、掌心没到位 / 朝向不对
        float Natural(EditPose p)
        {
            static float Over(float value, float limit) => MathF.Max(0, MathF.Abs(value) - limit);
            var (twist, sideways, bend) = WristAngles(Quaternion.Conjugate(rig.BindLocal(wrist)) * p.LocalRotation(wrist));
            var shoulderY = p.Position(upper).Y;
            var elbowY = p.Position(fore).Y;
            var wristY = p.Position(wrist).Y;
            float Turn(string bone) => p.Has(bone) && startPose.Has(bone)
                ? 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(p.LocalRotation(bone)), Quaternion.Normalize(startPose.LocalRotation(bone)))), 0f, 1f)) * 180f / MathF.PI
                : 0f;
            var facing = Vector3.Normalize(Vector3.Transform(palmLocal, p.Rotation(wrist)));
            var facingError = MathF.Acos(Math.Clamp(Vector3.Dot(facing, -contact.Normal), -1f, 1f)) * 180f / MathF.PI;
            var miss = PalmCenter(p, side, rig) is { } pc ? Vector3.Distance(pc, palmTarget) : 1f;
            var armInside = 0f;
            if (arm is not null)
            {
                var shoulder = p.Position(upper);
                foreach (var q in arm.Skin(p, rig))
                    if (Vector3.Distance(q, shoulder) >= 0.10f && BodyReach.ArmContact(surface, q, 0.15f, p, startPose) is { } c && !BodyReach.OwnShoulder(c, side) && c.Signed < -BodyReach.Allow(c)) armInside = MathF.Max(armInside, -BodyReach.Allow(c) - c.Signed);
            }
            return Over(bend, WristBend) * 4 + Over(sideways, WristSide) * 4 + Over(twist, 60f) * 3 + MathF.Abs(twist) * 0.5f + MathF.Abs(bend) * 0.3f + MathF.Abs(sideways) * 0.5f
                   + (elbowY - shoulderY) * 100f + MathF.Max(0, elbowY - MathF.Max(shoulderY, wristY)) * 100f * 4f
                   + MathF.Max(0, elbowY - shoulderY - 0.05f) * 100f * 4f   // 肘抬过肩 5cm 以上：胳膊架起来绕过去，很少这么摸人
                   + armInside * 1000f * 2f
                   + (Turn("j_sebo_a") + Turn("j_sebo_b") + Turn("j_sebo_c")) * 1.5f + Turn($"j_sako_{side}") * 1f
                   + facingError * 1f + miss * 1000f * 3f;
        }

        // 胳膊让开身体：全身协同时手原地不动（位置、朝向都钉住），让脊椎、锁骨、肩、肘一起挪开；否则用以前的转肘 / 耸肩
        void Avoid(bool thorough)
        {
            if (!bodyReach)
            {
                AvoidBody(pose, rig, hand, surface, arm, thorough);
                return;
            }
            if (PalmCenter(pose, side, rig) is not { } here) return;
            BodyReach.Solve(pose, rig, hand, here, pose.Rotation(wrist), startPose, p => PalmCenter(p, side, rig), surface, arm, minBend, surfacePose: startPose, lockSpine: selfUpper, iterations: quick ? 8 : 20,
                            orientationWeight: 1.0f, positionWeight: 10f);
        }

        // 胳膊怎么摆（只动这条胳膊，躯干不动——双人交互重定向的开源实现 R2ET / MeshRet / GMR 都是这么做的）：
        //  · 两段骨骼解析 IK，手腕位置精确到位；
        //  · 手掌绕表面法线的 24 个朝向 × 肘的 10 个朝向（绕「肩 → 腕」连线，20°–200°）都解出来比：
        //    手腕、上臂扭转不超出游戏动画里的范围（超了重罚）、胳膊别压进身体（自己的、别人的）、肘朝下偏外最自然。
        //    肘的朝向试过照搬 VRArmIK 的经验公式，在 2238 帧游戏动画上并不比「固定 131°」准（中位误差 26° 对 24°），所以只当弱偏好。
        // 以前这里靠「肘越低越好」+ 事后转肘让开身体，而且手腕按左右对称 ±85° 算，会挑出前臂旋前 95°、上臂拧成麻花的解。
        List<(float Cost, EditPose Trial, Quaternion Rotation)> ChooseArm()
        {
            var bindInverse = rig.HasBind(wrist) ? Quaternion.Conjugate(rig.BindLocal(wrist)) : Quaternion.Identity;
            var shoulder = pose.Position(upper);
            var parentRotation = pose.Parent(upper) is { } clavicleBone ? pose.Rotation(clavicleBone) : Quaternion.Identity;
            var upperBindInverse = rig.HasBind(upper) ? Quaternion.Conjugate(rig.BindLocal(upper)) : (Quaternion?)null;

            // 胳膊当两个胶囊量穿模：半径取这条胳膊网格离骨骼轴线的距离（85% 分位），原姿势里本来就贴着的量（腋下、搭着的地方）不算。
            // 关节位置要用游戏里实际画的（Customize+ 缩放过的角色，画出来的上臂、前臂比 Brio 的骨骼偏 4–5cm；
            // 按骨骼量的话「以为没穿」，实际前臂压进对方胸口 6cm）：各关节在自己骨骼局部坐标里的偏移，跟着骨骼转
            Vector3 Shift(string bone) => rig.SkinFixOf(bone, out var fix) ? fix.Translation * pose[bone].Scale : Vector3.Zero;
            Vector3 shiftUpper = Shift(upper), shiftFore = Shift(fore), shiftWrist = Shift(wrist);
            Vector3 Drawn(EditPose p, string bone) => rig.Rendered(p, bone, p.Position(bone));
            var basePose = _original ?? startPose;
            float[] upperAt = [0.4f, 0.55f, 0.7f, 0.85f, 1f], foreAt = [0.15f, 0.3f, 0.45f, 0.6f, 0.75f, 0.9f];   // 0.9：手腕这一截（搭在对方锁骨上时手腕会顶进脖子 / 下巴）
            var radii = ArmRadii();
            var ownTorso = TorsoVolume.Of(surface, basePose);
            // 原姿势里本来就压着的量不算——但只在压的是同一个角色时才扣（原来手腕贴着自己大腿的量，不能拿来抵「手腕顶进对方脖子」）
            var baseline = Penetrations(Drawn(basePose, upper), Drawn(basePose, fore), Drawn(basePose, wrist), out var baselineActor);

            // 手的朝向候选：掌心正对表面（绕法线 24 个方向）；再加上斜着搭的（掌心偏开 35°、70°，各 6 个偏的方向 × 12 个朝向）。
            // 有的地方手掌贴平了胳膊就得穿过对方身体、或者手腕得拧出范围（比如用左手摸站在自己左边的人的胸口正中）——
            // 真人这时是手斜着搭上去、指尖或掌缘碰着，不会硬贴平。斜着的每度罚 1.15，能贴平时还是贴平
            var orientations = new List<(Quaternion Rotation, float Drop, float Tilt)>();
            foreach (var (rotation, drop) in HandCandidates(pose, rig, hand, contact.Normal, place, contact.Point, 24, axisUse))
                orientations.Add((rotation, drop, 0f));
            {
                var n0 = Vector3.Normalize(contact.Normal);
                var u0 = Vector3.Normalize(Vector3.Cross(n0, MathF.Abs(n0.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
                var v0 = Vector3.Cross(n0, u0);
                foreach (var tilt in new[] { 35f, 70f })
                    for (var k = 0; k < 6; k++)
                    {
                        var around = k * MathF.PI / 3f;
                        var tilted = Vector3.Normalize(MathF.Cos(tilt * MathF.PI / 180f) * n0
                                                       + MathF.Sin(tilt * MathF.PI / 180f) * (MathF.Cos(around) * u0 + MathF.Sin(around) * v0));
                        foreach (var (rotation, _) in HandCandidates(pose, rig, hand, tilted, place, contact.Point, 12, axisUse, grasp: false))
                            orientations.Add((rotation, 0f, tilt));
                    }
            }

            // 每个朝向解一次两段 IK，肘绕「肩 → 腕」连线转一圈（每 20°）直接用四元数算，不用每个都复制姿势；
            // 先算关节的代价，已经比现有最好的差就不去量穿模（量穿模要查表面，是最贵的一步）
            // 用户通常先把胳膊大致掰到位再吸附：吸附是在现有姿势上小幅修正，不是重新摆一遍。
            // 所以肘离用户摆的位置每远 1cm 罚 2.5、手每多转 1° 罚 0.3（把肘从下面翻到上面 ≈ 罚 100，比「斜着搭」「压进去几厘米」都重）；
            // 「肘该垂着」的线也不低于用户自己摆的高度（用户故意把肘抬起来的，不往下拽）
            var kept = _original ?? startPose;
            var keptElbow = kept.Position(fore);
            var keptHand = kept.Rotation(wrist);
            var keptLift = (keptElbow.Y - kept.Position(upper).Y) / MathF.Max(Vector3.Distance(keptElbow, kept.Position(upper)), 1e-3f);
            // 手的朝向和「用户摆的手、用最小的转动把掌心对上表面」比：贴平本来就得转的那部分不算，只罚多转的（绕法线换了个方向搭）。
            // 手本来就在点的地方附近、掌心大致朝着表面（10cm 内算摆好了，25cm 外算没摆）：说明用户已经摆过，
            // 这时保持原样的权重加到 4 倍 / 3 倍（手多转 1° 罚 1.2、肘每挪 1cm 罚 7.5），比「斜着搭」「压进去两三厘米」都重
            var keptPalm = Vector3.Normalize(Vector3.Transform(palmLocal, keptHand));
            var keptFlush = Quaternion.Normalize(Between(keptPalm, -Vector3.Normalize(contact.Normal)) * keptHand);
            var posed = 0f;
            if (PalmCenter(kept, side, rig) is { } keptCenter)
                posed = Math.Clamp(1f - (Vector3.Distance(keptCenter, contact.Point) - 0.10f) / 0.15f, 0f, 1f)
                        * Math.Clamp(Vector3.Dot(keptPalm, -Vector3.Normalize(contact.Normal)) * 2f, 0f, 1f);
            float turnWeight = 0.3f + 0.9f * posed, elbowWeight = 2.5f + 5f * posed;
            // 已经摆好的：关节只要没超出范围就照用户的来，「不够舒服」的罚分只留三成（不为了舒服一点去改用户摆的手向）
            var comfort = 1f - 0.7f * posed;
            chosenPosed = posed;
            Trace?.Invoke($"  pre-posed {posed:0.00}");
            // 用户摆的手向本身（只转到贴平）也当一个候选：24 个朝向每 15° 一个，不加的话最多要白转 7.5°
            if (posed > 0f) orientations.Add((keptFlush, 0f, 0f));
            // 斜着搭的方向：真人是抬起手腕用指腹 / 指尖碰，或者抬起手指用掌根按（绕手的横轴俯仰）；
            // 侧过来用小指那一侧或拇指那一侧的掌缘去碰（绕手指方向翻）很别扭，手指也合不到表面上——同样的角度罚双倍
            var fingerAxis = rig.BindOffset($"j_naka_a_{side}", wrist) is { } toKnuckle && toKnuckle.LengthSquared() > 1e-8f ? Vector3.Normalize(toKnuckle) : Vector3.UnitX;
            fingerAxis = Vector3.Normalize(fingerAxis - palmLocal * Vector3.Dot(fingerAxis, palmLocal));
            var sideAxis = Vector3.Cross(palmLocal, fingerAxis);
            var toward = -Vector3.Normalize(contact.Normal);
            float bestCost = float.MaxValue;
            (Quaternion Rotation, float Angle)? bestChoice = null;
            var bestInfo = "";
            var lowCost = float.MaxValue;   // 调试：肘垂着的摆法里最好的那个
            var lowInfo = "";
            var flatCost = float.MaxValue;  // 调试：贴平的摆法里最好的那个
            var flatInfo = "";
            var keptCost = float.MaxValue;  // 调试：照用户摆的手向（只转到贴平）的摆法里最好的那个
            var keptInfo = "";
            foreach (var (rotation, drop, tilt) in orientations)
            {
                var wristTarget = palmTarget + Vector3.Transform(offset, rotation);
                var solved = pose.Clone();
                PoseIk.SolveLimb(solved, upper, fore, wrist, wristTarget, rotation, minBend);
                Vector3 elbow0 = solved.Position(fore), wristAt = solved.Position(wrist);
                var axis = wristAt - shoulder;
                if (axis.LengthSquared() < 1e-8f) continue;
                axis = Vector3.Normalize(axis);
                var miss = Vector3.Distance(wristAt, wristTarget);
                Quaternion fore0 = solved.Rotation(fore), upper0 = solved.Rotation(upper);
                // 和「用户摆的手」「用户摆的手转到贴平」两个比，取小的：用户摆成斜着搭的，照原样斜着也算没改
                float TurnFrom(Quaternion reference) => 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(rotation), reference)), 0f, 1f)) * 180f / MathF.PI;
                var handTurn = MathF.Min(TurnFrom(keptFlush), TurnFrom(Quaternion.Normalize(keptHand)));
                var edge = 0f;
                if (tilt > 0f)
                {
                    float alongFinger = Vector3.Dot(toward, Vector3.Transform(fingerAxis, rotation)), alongSide = Vector3.Dot(toward, Vector3.Transform(sideAxis, rotation));
                    edge = MathF.Abs(alongSide) / MathF.Max(MathF.Sqrt(alongFinger * alongFinger + alongSide * alongSide), 1e-4f);
                }
                var fixedCost = tilt * 1.15f * (1f + edge) + miss * 100f * 10f - drop * 400f + handTurn * turnWeight;   // 没贴平每度 1.15（斜 35° ≈ 胳膊多压进 3cm）；够不着每厘米 10；能握的东西手指横过去
                for (var step = 0; step < 18; step++)
                {
                    var angle = step * 20f * MathF.PI / 180f;
                    var turn = Quaternion.CreateFromAxisAngle(axis, angle);
                    var elbow = shoulder + Vector3.Transform(elbow0 - shoulder, turn);
                    var wristDeviation = bindInverse * Quaternion.Conjugate(Quaternion.Normalize(turn * fore0)) * rotation;
                    var (wristOver, wristStrain) = WristCost(wristDeviation, wrist);
                    var (upperOver, upperStrain, upperTwist) = upperBindInverse is { } ubi
                        ? UpperTwistCost(ubi * Quaternion.Conjugate(parentRotation) * Quaternion.Normalize(turn * upper0), upper)
                        : (0f, 0f, 0f);
                    // 肘该垂着。2238 帧游戏动画里，手在肩以下时肘比肩低的量（除以上臂长）：中位数 0.64、75% 的帧至少低 0.39、
                    // 90% 至少低 0.14、95% 不高过肩；手举过肩时肘才跟着抬（手高出 0.6 个上臂长时，75% 的帧肘不超过肩上 0.27、95% 不超过 0.59）。
                    // 高过「舒适线」每个上臂长罚 60，高过「极限线」再罚 400（肘抬到肩那么高 ≈ 胳膊多压进身体 6cm——基本只在别无选择时才抬）
                    var upperArm = MathF.Max(Vector3.Distance(elbow0, shoulder), 1e-3f);
                    var handUp = Math.Clamp((wristAt.Y - shoulder.Y) / upperArm / 0.6f, 0f, 1f);
                    var liftRatio = (elbow.Y - shoulder.Y) / upperArm;
                    float easyLine = MathF.Max(-0.39f + 0.66f * handUp, keptLift + 0.05f), hardLine = MathF.Max(-0.14f + 0.73f * handUp, keptLift + 0.15f);
                    var lift = (elbow.Y - shoulder.Y) * 100f;                                  // 肘比肩高多少厘米
                    var cost = fixedCost
                               + wristOver * 8f + wristStrain * comfort                        // 手腕：超出范围每度 8，超出舒适范围每度 1
                               + upperOver * 8f + upperStrain * comfort                        // 上臂扭转：同上
                               + MathF.Max(0f, liftRatio - easyLine) * 60f + MathF.Max(0f, liftRatio - hardLine) * 400f
                               + Vector3.Distance(elbow, keptElbow) * 100f * elbowWeight;        // 肘离用户摆的位置：每厘米 2.5（已经摆好的 7.5）
                    var isKept = posed > 0f && rotation == keptFlush;
                    var hanging = Trace is not null && (liftRatio <= easyLine || tilt == 0f);
                    if (cost >= bestCost && !hanging) continue;
                    var inside = 0f;
                    var depths = Penetrations(shoulder + Vector3.Transform(shiftUpper, Quaternion.Normalize(turn * upper0)),
                                              elbow + Vector3.Transform(shiftFore, Quaternion.Normalize(turn * fore0)),
                                              wristAt + Vector3.Transform(shiftWrist, rotation), out var depthActor);
                    var deepest = 0f;
                    var hardDeepest = 0f;
                    for (var i = 0; i < depths.Length; i++)
                    {
                        var extra = MathF.Max(0f, depths[i] - (depthActor[i] == baselineActor[i] ? baseline[i] : 0f));
                        inside += extra;
                        deepest = MathF.Max(deepest, extra);
                        if (depthActor[i] >= 1000) hardDeepest = MathF.Max(hardDeepest, extra);
                    }
                    cost += deepest * 100f * 12f + inside * 100f * 2f;                        // 胳膊压进身体（自己的、别人的）：最深处每厘米 12，各处加起来每厘米再 2
                    string Info()
                    {
                        var (twist, sideways, bend) = WristAngles(wristDeviation);
                        return $"tilt {tilt:0} (edge {edge:0.0}), elbow {lift:0}cm vs shoulder, wrist tw{twist * SideSign(wrist):0} side{sideways * SideSign(wrist):0} bend{bend:0} (over {wristOver:0}), " +
                               $"upper twist {upperTwist:0} (over {upperOver:0}), inside {inside * 1000:0}mm (deepest {deepest * 1000:0}), miss {miss * 1000:0}mm, hand turned {handTurn:0}, elbow moved {Vector3.Distance(elbow, keptElbow) * 100:0}cm, swivel {step * 20}";
                    }
                    if (hanging && liftRatio <= easyLine && cost < lowCost) (lowCost, lowInfo) = (cost, Info());
                    if (Trace is not null && tilt == 0f && cost < flatCost) (flatCost, flatInfo) = (cost, Info());
                    if (Trace is not null && isKept && cost < keptCost) (keptCost, keptInfo) = (cost, Info());
                    if (cost >= bestCost) continue;
                    (bestCost, bestChoice) = (cost, (rotation, angle));
                    (reachMiss, chosenTilt, chosenInside, chosenHard) = (miss, tilt, deepest, hardDeepest);
                    reachTooFar = Vector3.Distance(wristTarget, shoulder) >= Vector3.Distance(wristAt, shoulder);
                    if (Trace is not null) bestInfo = Info();
                }
            }
            if (bestChoice is { } choice)
            {
                PoseIk.SolveLimb(pose, upper, fore, wrist, palmTarget + Vector3.Transform(offset, choice.Rotation), choice.Rotation, minBend);
                if (choice.Angle != 0f) PoseIk.SwivelBy(pose, upper, wrist, choice.Angle);
                chosenRotation = choice.Rotation;
                if (Trace is not null)
                {
                    // 调试：挑中的摆法各采样点的读数（解出来的姿势上重新量一遍，和挑的时候按四元数算的应该一样）
                    Vector3 ds = Drawn(pose, upper), de = Drawn(pose, fore), dw = Drawn(pose, wrist);
                    var again = Penetrations(ds, de, dw, out _);
                    var parts = new List<string>();
                    for (var i = 0; i < again.Length; i++)
                    {
                        var onFore = i >= upperAt.Length;
                        var point = onFore ? de + (dw - de) * foreAt[i - upperAt.Length] : ds + (de - ds) * upperAt[i];
                        var near = surface.Closest(point, 0.25f);
                        parts.Add($"{(onFore ? "f" : "u")}{(onFore ? foreAt[i - upperAt.Length] : upperAt[i]):0.00}:{again[i] * 1000:0}/{baseline[i] * 1000:0}({(near is { } nc ? $"{nc.Signed * 1000:0}@{nc.Bone}" : "none")})");
                    }
                    Trace($"  capsule r={string.Join("/", radii.Select(r => (r * 1000).ToString("0")))}mm samples depth/baseline(signed@bone): {string.Join(" ", parts)}");
                }
            }
            Trace?.Invoke($"  choose arm: cost {bestCost:0.0}, {bestInfo}");
            Trace?.Invoke($"  best with elbow hanging: cost {lowCost:0.0}, {lowInfo}");
            Trace?.Invoke($"  best flat: cost {flatCost:0.0}, {flatInfo}");
            if (posed > 0f) Trace?.Invoke($"  best keeping the hand: cost {keptCost:0.0}, {keptInfo}");
            return [];

            // 每个采样位置各自的半径：这条胳膊的网格在那一段（沿骨骼前后 12% 以内）离轴线的距离的 85% 分位。
            // 前臂从肘到腕细一半，用整段一个半径的话手腕那头会被当成粗的——手掌贴平时前臂末端本来就挨着表面，会被误判成穿模、逼着手斜起来
            float[] ArmRadii()
            {
                var radii = new float[upperAt.Length + foreAt.Length];
                for (var i = 0; i < radii.Length; i++) radii[i] = i >= upperAt.Length ? 0.04f : 0.05f;
                if (arm is null) return radii;
                var points = arm.Skin(basePose, rig);
                Vector3 s = Drawn(basePose, upper), e = Drawn(basePose, fore), w = Drawn(basePose, wrist);
                var perStation = new List<float>[radii.Length];
                for (var i = 0; i < perStation.Length; i++) perStation[i] = [];
                for (var i = 0; i < points.Length; i++)
                {
                    var bone = arm.BoneOf(i);
                    var onFore = bone.StartsWith("j_ude_b", StringComparison.Ordinal) || bone.StartsWith("n_hte", StringComparison.Ordinal);
                    var (a, b) = onFore ? (e, w) : (s, e);
                    var along = Math.Clamp(Vector3.Dot(points[i] - a, b - a) / MathF.Max((b - a).LengthSquared(), 1e-8f), 0f, 1f);
                    var distance = Vector3.Distance(points[i], a + (b - a) * along);
                    var stations = onFore ? foreAt : upperAt;
                    for (var k = 0; k < stations.Length; k++)
                        if (MathF.Abs(along - stations[k]) <= 0.12f) perStation[(onFore ? upperAt.Length : 0) + k].Add(distance);
                }
                for (var i = 0; i < radii.Length; i++)
                {
                    var d = perStation[i];
                    if (d.Count < 5) continue;
                    d.Sort();
                    radii[i] = Math.Clamp(d[d.Count * 85 / 100], 0.02f, 0.10f);
                }
                return radii;
            }

            // 各采样点压进表面多少（超出软组织的部分）；自己这侧的肩不算（它跟着胳膊动）
            // 压进去多少算「肉挤肉」不计：上臂压着自己的躯干（手伸到对侧时上臂内侧压着胸肌）2.5cm，别处 1.5cm，头发 2.5cm；
            // 头、脖子是硬的（下颌、颈椎），只给 0.5cm——不然手腕会顶进对方脖子侧面 2cm
            static bool Hard(string bone) => bone.StartsWith("j_kao", StringComparison.Ordinal) || bone.StartsWith("j_kubi", StringComparison.Ordinal)
                                             || bone.StartsWith("j_ago", StringComparison.Ordinal) || bone.StartsWith("j_f_", StringComparison.Ordinal);
            float AllowOf(Contact c, bool onFore) => Surface.IsHair(c.Bone) ? BodyReach.Allow(c) : Hard(c.Bone) ? 0.005f : !onFore && c.Actor == 0 ? 0.025f : BodyReach.Allow(c);
            float[] Penetrations(Vector3 s, Vector3 e, Vector3 w, out int[] actors)
            {
                var result = new float[upperAt.Length + foreAt.Length];
                actors = new int[result.Length];
                for (var i = 0; i < result.Length; i++)
                {
                    var onFore = i >= upperAt.Length;
                    var point = onFore ? e + (w - e) * foreAt[i - upperAt.Length] : s + (e - s) * upperAt[i];
                    var radius = radii[i];
                    if (surface.Closest(point, radius + 0.10f) is not { } c)
                    {
                        // 附近找不到表面：可能整段埋在自己躯干深处（胳膊横穿胸口时轴线离表面十几厘米），按躯干截面量埋多深
                        if (ownTorso is not null && ownTorso.Depth(point) is > 0f and var deep) result[i] = deep + radius - 0.025f;
                        continue;
                    }
                    if (BodyReach.OwnShoulder(c, side)) continue;
                    // 上臂压着自己的躯干（手伸到对侧时上臂内侧压着胸肌）：肉挤肉，多允许一点
                    var allow = AllowOf(c, onFore);
                    result[i] = MathF.Max(0f, radius - allow - c.Signed);
                    actors[i] = c.Actor + (!Surface.IsHair(c.Bone) && Hard(c.Bone) ? 1000 : 0);
                    // 被两边夹着（胳膊搭在对方肩上、上面还压着对方歪过来的头）：Closest 只给最近的那一面，另一面量不到。
                    // 已经挨着一面时，往背离它的方向半个半径处再找一次，找到的要是在另一侧，就按它离轴线的距离再量
                    if (c.Signed < radius && c.Signed > -radius)
                    {
                        var away = point - c.Point;
                        if (c.Signed < 0) away = -away;
                        if (away.LengthSquared() > 1e-8f)
                        {
                            away = Vector3.Normalize(away);
                            if (surface.Closest(point + away * (radius * 0.5f), radius * 0.5f) is { } far && !Surface.IsHair(far.Bone) && !BodyReach.OwnShoulder(far, side)
                                && Vector3.Dot(far.Point - point, away) > 0.01f)
                            {
                                var farAllow = AllowOf(far, onFore);
                                var depth = far.Signed < 0 ? radius - farAllow - far.Signed : radius - farAllow - Vector3.Distance(point, far.Point);
                                if (depth > result[i]) (result[i], actors[i]) = (depth, far.Actor + (Hard(far.Bone) ? 1000 : 0));
                            }
                        }
                    }
                }
                return result;
            }
        }

        // 2. 手指先放松张开（不然原来握着的手指会把手掌顶开）
        rests = fingers.ToDictionary(f => f.Id, f => RestOf(pose, rig, f));
        foreach (var f in fingers) SetFinger(pose, rests[f.Id], f, FingerRanges(f).Select(r => r.Base).ToArray());
        // 拇指：绑定姿势里张开约 45°，像在比「点赞」；放松的手拇指会往食指那边收一半
        if (fingers.FirstOrDefault(f => f.Id.StartsWith("finger_oya", StringComparison.Ordinal)) is { } thumbNode)
        {
            // 先把拇指原姿势里的弯曲放松（末节往掌心弯着会让「拇指最高点」显得很高），再收、再放平
            SetFinger(pose, rests[thumbNode.Id], thumbNode, FingerRanges(thumbNode).Select(r => r.Base).ToArray());
            AdductThumb(pose, thumbNode, 0.5f);
            FlattenThumb(pose, rig, hand, thumbNode, shape);
            rests[thumbNode.Id] = thumbNode.Chain.Select(pose.LocalRotation).ToArray();
            SetFinger(pose, rests[thumbNode.Id], thumbNode, new float[thumbNode.Chain.Length]);
        }

        // 3. 按手掌的真实形状贴实：掌心那一面的顶点整体去贴表面（倾斜 + 推进一起拟合），最后保证哪儿都不压进去
        TraceFlush("before flush", pose, rig, hand, shape, place);
        palmTarget = FlushPalm(pose, rig, hand, shape, place, region is null ? null : surface, contact.Normal, palmTarget, minBend, iterations: quick ? 3 : 6);
        TraceFlush("after flush", pose, rig, hand, shape, place);
        // 手贴上去以后胳膊可能又碰到身体了：再让一次（手不动），手腕超了就拉回来、掌心挪回原处
        if (quick) Avoid(false);
        TraceFlush("after avoid", pose, rig, hand, shape, place);
        Place(pose.Rotation(wrist));
        TraceFlush("after place", pose, rig, hand, shape, place);
        // 挪回原处时手腕被拉回会让手掌又翘一点：最后再整面贴一次（手腕只在明显超出时才拉回）。拖动中省掉这一轮
        if (!quick) palmTarget = FlushPalm(pose, rig, hand, shape, place, region is null ? null : surface, contact.Normal, palmTarget, minBend);
        // 手腕明显超出活动范围才拉回来；拉回来时手绕手腕转、可能压进去或翘起来，再沿法线推 / 拉一次（不再倾斜）
        ClampWrist(pose, rig, fore, wrist, 4f);
        palmTarget = FlushPalm(pose, rig, hand, shape, place, region is null ? null : surface, contact.Normal, palmTarget, minBend, pushOnly: true);
        // 最后这几下挪了手，胳膊可能又碰到身体：再让一次（转肘的朝向，还不行就动一点肩；手的位置、朝向都不变）。拖动中不做，松手时做
        // （以前这里还会再转一次肘 / 动一点肩让开身体：现在胳膊在 ChooseArm 里一次挑好，不再事后改）
        TraceFlush("final", pose, rig, hand, shape, place);
        if (Trace is not null) TraceFlush("after final avoid", pose, rig, hand, shape, place);
        }

        void Pad(string step)
        {
            if (Trace is null) return;
            var deepest = float.PositiveInfinity;
            foreach (var q in shape.Skin(pose, rig, shape.Under(pose, $"j_oya_a_{side}", exact: true)))
                if (surface.Closest(q, 0.1f) is { } c) deepest = MathF.Min(deepest, c.Signed);
            Trace($"  pad depth {step}: {deepest * 1000:0}mm");
        }
        Pad("after flush");
        // 4. 手指合拢到碰到表面为止
        if (!quick)
        {
            // 拇指从现在的样子开始合拢：rests 是绑定姿势，CloseFinger 一开始会把手指设回「rest + 放松角度」——
            // 拇指前面收拢、放平、抬肉垫转过的拇指根会全被撤掉，肉垫又压回身体里（实测 −16mm → −24mm）
            if (fingers.FirstOrDefault(f => f.Id.StartsWith("finger_oya", StringComparison.Ordinal)) is { } thumbNow)
            {
                var ranges = FingerRanges(thumbNow);
                var rest = rests[thumbNow.Id];
                for (var i = 0; i < thumbNow.Chain.Length && i < ranges.Length && i < rest.Length; i++)
                    if (pose.Parent(thumbNow.Chain[i]) is not null)
                        rest[i] = Quaternion.Normalize(pose.LocalRotation(thumbNow.Chain[i]) * Quaternion.Conjugate(Quaternion.CreateFromAxisAngle(-Vector3.UnitZ, ranges[i].Base * MathF.PI / 180f)));
            }
            foreach (var f in fingers) CloseFinger(pose, rig, rests[f.Id], f, shape, surface);
            // 四指协调（真人相邻手指连着动），再对还钻着的手指往旁边偏一点
            var four = new[] { "hito", "naka", "kusu", "ko" }
                .Select(n => fingers.FirstOrDefault(f => f.Chain[0] == $"j_{n}_a_{side}")).OfType<Node>().ToList();
            Pad("after close");
            Harmonize(pose, rig, rests, four, shape, surface);
            // 每根手指 4 个自由度一起优化（指根弯 / 张开、中节、末节）：指腹整片贴表面，允许压进软组织一点（参考 ContactOpt）
            // 参考 DexGraspNet 的能量（接触距离 + 穿模 + 手指互相穿插 + 关节限位）：四根手指轮流优化两遍，
            // 每次调一根时其它固定，代价里算上和相邻手指的胶囊体距离——手指不会交叉、也不会单独一根翘出去
            if (RefineFingers)
                for (var sweep = 0; sweep < 2; sweep++)
                    for (var i = 0; i < four.Count; i++)
                    {
                        var neighbors = new List<Node>();
                        if (i > 0) neighbors.Add(four[i - 1]);
                        if (i + 1 < four.Count) neighbors.Add(four[i + 1]);
                        var bends = neighbors.Select(n =>
                        {
                            var a = FingerAngles(pose, rests[n.Id], n, Math.Min(n.Chain.Length, 3));
                            return a[0] + (a.Length > 1 ? a[1] : 0f);
                        }).ToList();
                        RefineFinger(pose, rig, rests[four[i].Id], four[i], shape, surface, PalmNormal(pose, side), neighbors, bends);
                    }
            if (fingers.FirstOrDefault(f => f.Id.StartsWith("finger_oya", StringComparison.Ordinal)) is { } thumbFinal)
                LowerThumb(pose, rig, thumbFinal, shape, surface);
        }
        // 拇指根总共别偏离绑定姿势超过 45°：前面放平、张开、落下几步叠起来转多了，拇指会翻到手背、拇指根的蒙皮被拧出折痕
        if (fingers.FirstOrDefault(f => f.Id.StartsWith("finger_oya", StringComparison.Ordinal)) is { } thumbLimit)
        {
            LimitThumbRoot(pose, rig, thumbLimit, ThumbRootMax + 5f);
            // 拇指末节最后落到表面上：拇指根转不动（肉垫已经贴着 / 到了活动范围）时拇指会整根悬着或者戳进去，
            // 真人这时是拇指第二个关节弯一下 / 伸一下
            Pad("after limit");
            if (!quick) SettleThumbTip(pose, rig, thumbLimit, shape, surface);
            Pad("end");
        }
        if (Trace is not null && arm is not null)
        {
            var h = pose.Clone();
            PoseOps.FollowHelpers(null, h, rig, new HashSet<string> { upper, fore, wrist });
            var worst = 0f;
            foreach (var q in arm.Skin(h, rig))
                if (Vector3.Distance(q, h.Position(upper)) >= 0.10f && surface.Closest(q, 0.15f) is { } cq) worst = MathF.Max(worst, -cq.Signed);
            var worst0 = 0f;
            foreach (var q in arm.Skin(pose, rig))
                if (Vector3.Distance(q, pose.Position(upper)) >= 0.10f && surface.Closest(q, 0.15f) is { } cq) worst0 = MathF.Max(worst0, -cq.Signed);
            Trace($"  end of mesh: arm worst {worst * 1000:0}mm (no helpers {worst0 * 1000:0}mm)");
        }
        // 贴上了但不理想的，告诉用户为什么（不为了贴平去拧关节）
        if (!quick)
            LastIssue = chosenInside > 0.02f ? "这个位置胳膊会压进身体：换另一只手、或换个贴的地方会更自然"
                : chosenTilt > 0f ? "这个位置手掌贴不平（再贴就得把手腕拧出正常范围），手是斜着搭上去的"
                : slid ? "点的地方这只手够不着（躯干保持不动），贴到了够得着的最近处"
                : null;
        return PalmCenter(pose, side, rig) is { } now ? surface.Closest(now, SnapRadius) ?? contact : contact;
    }

    /// <summary>
    /// 手掌整面贴实：只用掌心那一面（不含手背、手指）的皮肤顶点，最小二乘同时求一个小的倾斜（绕掌心转）和沿法线的推进，
    /// 让这些顶点离表面都尽量等于 Gap；迭代几次。以前只让「离表面最近的那一个顶点」碰到，朝向估得稍有偏差就只有掌根或指根碰到、手掌中间悬空。
    /// 最后再整体推一下，保证没有地方压进去超过贴合度允许的深度（只往选中部位贴时，也不能压进旁边的部位）。返回最终的掌心目标位置。
    /// </summary>
    private static Vector3 FlushPalm(EditPose pose, PoseRig rig, Node hand, HandShape shape, Surface place, Surface? whole, Vector3 normal,
                                     Vector3 palmTarget, float minBend, bool pushOnly = false, int iterations = 6)
    {
        string upper = hand.Chain[0], fore = hand.Chain[1], wrist = hand.Chain[2];
        // 拇指根那块肉（大鱼际，主要跟 j_oya_a 动）也是手掌的一部分：不算进来的话它会整个压进身体两三厘米
        //   它比掌心凸出一截、肉也最软：参与拟合但权重低，压扁的余量也单独给大一些（不参与下面「最深 15%」的统计）
        //   （之前先 FlattenThumb 把拇指放回掌心平面，不然它整根伸在掌心前面，会把手掌顶开）
        var thenar = shape.Under(pose, $"j_oya_a_{hand.Side}", exact: true).ToHashSet();
        var all = shape.Palm(pose).Concat(thenar).Distinct().ToArray();
        // 掌心那一面：在掌心朝向一侧的顶点
        var center = PalmCenter(pose, hand.Side, rig) ?? pose.Position(wrist);
        var facing = PalmNormal(pose, hand.Side, MeshPalm ? shape : null, rig);
        var start = shape.Skin(pose, rig, all);
        var skin = all.Where((_, i) => Vector3.Dot(start[i] - center, facing) > 0.002f).ToArray();
        if (skin.Length < 6) skin = all;
        // 四指前两节掌侧：只在钻进去的时候参与倾斜拟合（表面朝手指那边鼓起来时，手掌稍微顺着倾斜，手指才搭得上去）
        var fingerVerts = new[] { "hito", "naka", "kusu", "ko" }
            .SelectMany(f => new[] { $"j_{f}_a_{hand.Side}", $"j_{f}_b_{hand.Side}" })
            .Where(pose.Has).SelectMany(b => shape.Under(pose, b, exact: true)).ToArray();
        if (fingerVerts.Length > 0)
        {
            var fs = shape.Skin(pose, rig, fingerVerts);
            fingerVerts = fingerVerts.Where((_, i) => Vector3.Dot(fs[i] - center, facing) > -0.004f).ToArray();
        }
        var fitVerts = skin.Concat(fingerVerts).ToArray();
        var fingerSet = fingerVerts.ToHashSet();

        // 一次贴合总共最多倾斜 25°，每步都按手腕活动范围（放宽 10°）限一下：不然拟合会把手掌转到手腕做不到的角度，
        // 之后手腕被拉回时手绕手腕大幅转回去，拇指根一下压进身体好几厘米，最后整只手被推开
        var tiltBudget = TiltBudget * MathF.PI / 180f;
        // 压进去的限度（和下面推出来那一步一样）：拟合时就把它们当成重的惩罚项一起解，能靠倾斜解决的就别整只手往外推
        //   （以前只拟合离表面的距离，掌根 / 拇指根肉垫压进去的部分留给后面整体平移，结果整只手被顶开一厘米多）
        var squash = PadSquash;
        var deepCap = MathF.Min(3 * squash, 0.03f);
        // 整体评分（越小越好）：掌心一侧离表面的平均悬空 + 3 × 压得太深的量（和下面推的规则一样）。
        // 拟合 + 推完以后要是比一开始还差（比如凸的地方又倾斜又推，结果整只手被顶开），就回到一开始的样子
        var thenarAll = thenar.ToArray();
        float Score(EditPose p)
        {
            var depths = new List<float>();
            var thenarDeepest = float.PositiveInfinity;
            var pts = shape.Skin(p, rig, skin);
            for (var k = 0; k < pts.Length; k++)
            {
                Contact? hit = place.Closest(pts[k], 0.08f);
                if (whole?.Closest(pts[k], 0.08f) is { } w && (hit is not { } h0 || w.Signed < h0.Signed)) hit = w;
                if (hit is not { } c) continue;
                if (thenar.Contains(skin[k])) thenarDeepest = MathF.Min(thenarDeepest, c.Signed);
                else depths.Add(c.Signed);
            }
            foreach (var q in shape.Skin(p, rig, thenarAll))
            {
                Contact? hit = place.Closest(q, 0.08f);
                if (whole?.Closest(q, 0.08f) is { } w && (hit is not { } h0 || w.Signed < h0.Signed)) hit = w;
                if (hit is { } c) thenarDeepest = MathF.Min(thenarDeepest, c.Signed);
            }
            if (depths.Count == 0) return float.MaxValue;
            depths.Sort();
            var hover = depths.Average(d => MathF.Max(0f, d - Gap));
            var low = depths[Math.Min(depths.Count - 1, depths.Count * 15 / 100)];
            var low5 = depths[Math.Min(depths.Count - 1, depths.Count * 5 / 100)];
            var need = MathF.Max(MathF.Max(Gap - squash - low, Gap - 2 * squash - low5), Gap - deepCap - depths[0]);
            if (!float.IsPositiveInfinity(thenarDeepest)) need = MathF.Max(need, Gap - ThenarSquash * squash - 0.006f - thenarDeepest);
            return hover + 3f * MathF.Max(0f, need);
        }
        var startPose = pose.Clone();
        var startTarget = palmTarget;
        var startScore = Score(pose);
        for (var iteration = 0; iteration < (pushOnly ? 0 : iterations) && tiltBudget > 0.002f; iteration++)
        {
            var points = shape.Skin(pose, rig, fitVerts);
            var pivot = PalmCenter(pose, hand.Side, rig) ?? pose.Position(wrist);
            // 法方程：未知数 [ωx, ωy, ωz, t]，残差 d_i + ω·(r_i × n_i) + t − Gap
            var a = new double[4, 4];
            var b = new double[4];
            var count = 0;
            for (var i = 0; i < points.Length; i++)
            {
                var p = points[i];
                var r = p - pivot;
                var isFinger = fingerSet.Contains(fitVerts[i]) && i >= skin.Length;
                var isThenar = thenar.Contains(fitVerts[i]);
                void Add(Vector3 n, double weight, double residual)
                {
                    var j = Vector3.Cross(r, n);
                    double[] row = [j.X * weight, j.Y * weight, j.Z * weight, weight * Vector3.Dot(n, normal)];
                    for (var x = 0; x < 4; x++)
                    {
                        b[x] += row[x] * residual * weight;
                        for (var y = 0; y < 4; y++) a[x, y] += row[x] * row[y];
                    }
                }
                var c0 = place.Closest(p, 0.1f);
                if (c0 is { } c && !(isFinger && c.Signed >= Inside))
                {
                    Add(c.Normal, isFinger ? 0.6 : isThenar ? 0.3 : 1.0, (isFinger ? Inside : Gap) - c.Signed);
                    count++;
                }
                // 惩罚：压得比允许的还深（选中部位以外的身体也算）
                Contact? deepest = c0;
                if (whole?.Closest(p, 0.1f) is { } w && (deepest is not { } d0 || w.Signed < d0.Signed)) deepest = w;
                var limit = isFinger ? Inside - 0.004f : isThenar ? Gap - ThenarSquash * squash - 0.006f : Gap - squash;
                if (deepest is { } h && h.Signed < limit) Add(h.Normal, 3.0, limit - h.Signed);
            }
            if (count < 6) break;
            // 倾斜加一点阻尼（顶点少、表面弯得厉害时别乱转），推进不加
            for (var x = 0; x < 3; x++) a[x, x] += count * 1e-4;
            if (Solve4(a, b) is not { } s) break;
            var omega = new Vector3((float)s[0], (float)s[1], (float)s[2]);
            // 贴实只倾斜、不绕法线转：手指朝哪边是前面挑好的（用户摆的），弯的表面上拟合会顺着弯的方向把手转走十几二十度
            var spinAxis = Vector3.Normalize(normal);
            omega -= spinAxis * Vector3.Dot(omega, spinAxis);
            var push = (float)s[3];
            var angle = omega.Length();
            var step = MathF.Min(MathF.Min(angle, 12f * MathF.PI / 180f), tiltBudget);
            tiltBudget -= step;
            var tilt = angle > 1e-6f ? Quaternion.CreateFromAxisAngle(omega / angle, step) : Quaternion.Identity;
            var rotation = Quaternion.Normalize(tilt * pose.Rotation(wrist));
            var newWrist = pivot + Vector3.Transform(pose.Position(wrist) - pivot, tilt) + normal * Math.Clamp(push, -0.03f, 0.05f);
            palmTarget += normal * Math.Clamp(push, -0.03f, 0.05f);
            PoseIk.SolveLimb(pose, upper, fore, wrist, newWrist, rotation, minBend);
            ClampWrist(pose, rig, fore, wrist, 4f);
            Trace?.Invoke($"    fit: tilt {angle * 180 / MathF.PI:0.0}deg push {push * 1000:0}mm");
            if (angle < 0.3f * MathF.PI / 180f && MathF.Abs(push) < 0.0005f) break;
        }

        // 别压得太深：掌根、拇指根的肉垫比手掌中间凸出将近 2cm，真人按在身上时它们会被压扁，手掌中间才贴得上。
        // 游戏里模型不会变形，肉垫稍微陷进去看不出来，悬空的缝却很显眼——所以只要求「最深的 15% 顶点」陷进去不超过肉垫压扁量，
        // 个别点可以再深一些（见下面的分级；选中部位以外的身体也算）
        // 单点最深：3 倍压扁量，但不超过 3cm（真人的肉按下去也就两厘米上下，再深就是穿模了）——deepCap 在上面定义
        // 推的方向：压进去的那些点所在表面的法线（按压进深度加权平均）。一直沿最开始接触点的法线推的话，
        // 压进去的是旁边另一块朝向不同的表面（胯侧、腰带）时会越推越深。每一步都记下违规最小的状态，最后回到它（不来回振荡）
        var bestNeed = float.PositiveInfinity;
        EditPose? bestPose = null;
        var bestTarget = palmTarget;
        var thenarArray = thenar.ToArray();
        var lifts = 0;
        for (var i = 0; i < 6; i++)
        {
            var depths = new List<float>();
            var thenarDeepest = float.PositiveInfinity;
            var pushDir = Vector3.Zero;
            var pts = shape.Skin(pose, rig, skin);
            for (var k = 0; k < pts.Length; k++)
            {
                var p = pts[k];
                Contact? hit = place.Closest(p, 0.08f);
                if (whole?.Closest(p, 0.08f) is { } w && (hit is not { } h0 || w.Signed < h0.Signed)) hit = w;
                if (hit is not { } c) continue;
                var d = c.Signed;
                if (thenar.Contains(skin[k])) thenarDeepest = MathF.Min(thenarDeepest, d);
                else depths.Add(d);
                if (d < Gap - squash) pushDir += c.Normal * (Gap - squash - d);
            }
            // 整只手掌（含手背、手腕那头）也不能埋进去：只看掌心一侧的话，手掌贴好了、手腕那头却可能整个陷进胯里
            var backDeepest = float.PositiveInfinity;
            var allPoints = shape.Skin(pose, rig, all);
            for (var k = 0; k < allPoints.Length; k++)
            {
                var p = allPoints[k];
                Contact? hit = place.Closest(p, 0.08f);
                if (whole?.Closest(p, 0.08f) is { } w && (hit is not { } h0 || w.Signed < h0.Signed)) hit = w;
                if (hit is not { } c) continue;
                // 拇指根那块（含手背那面）单独按肉垫的规则算（下面），能靠抬拇指根解决
                if (thenar.Contains(all[k]))
                {
                    thenarDeepest = MathF.Min(thenarDeepest, c.Signed);
                    continue;
                }
                backDeepest = MathF.Min(backDeepest, c.Signed);
                if (c.Signed < Gap - deepCap) pushDir += c.Normal * (Gap - deepCap - c.Signed);
            }
            if (depths.Count == 0) break;
            depths.Sort();
            var low = depths[Math.Min(depths.Count - 1, depths.Count * 15 / 100)];
            // 分级：最深 15% 不超过 1 倍压扁量，最深 5% 不超过 2 倍，单个最深点不超过 3 倍（一个凸起顶着一个点，不该把整只手推开）
            var low5 = depths[Math.Min(depths.Count - 1, depths.Count * 5 / 100)];
            var need = MathF.Max(MathF.Max(Gap - squash - low, Gap - 2 * squash - low5), Gap - deepCap - depths[0]);
            if (!float.IsPositiveInfinity(backDeepest)) need = MathF.Max(need, Gap - deepCap - backDeepest);
            // 只是拇指根肉垫压得太深（凸的地方：肩头、前臂、胸肌外沿）：真人是拇指根往外让一点、肉垫压扁，不会整只手离开。
            // 先转拇指根把肉垫抬起来（最多 20°），抬不动的部分再整只手推
            var thenarNeed = float.IsPositiveInfinity(thenarDeepest) ? float.NegativeInfinity : Gap - ThenarSquash * squash - 0.006f - thenarDeepest;
            if (thenarNeed > MathF.Max(need, 0f) + 0.001f && lifts < 2 && LiftThenar(pose, rig, hand, shape, thenarArray, place, whole, normal, thenarNeed))
            {
                lifts++;
                continue;
            }
            need = MathF.Max(need, thenarNeed);
            Trace?.Invoke($"    push: low15={low * 1000:0} deepest={depths[0] * 1000:0} thenar={thenarDeepest * 1000:0} need={need * 1000:0}mm");
            // 违规程度：压得太深（need > 0）比离得远（need < 0）更不能接受
            var badness = need > 0 ? need * 3f : -need;
            if (badness < bestNeed) (bestNeed, bestPose, bestTarget) = (badness, pose.Clone(), palmTarget);
            // 两头都管：压得太深就推出来，离得太远（最紧的约束都还松着）就拉近
            if (MathF.Abs(need) <= 0.0005f) break;
            var move = Math.Clamp(need, -0.03f, 0.05f);
            var dir = need > 0 && pushDir.LengthSquared() > 1e-10f ? Vector3.Normalize(pushDir) : normal;
            palmTarget += dir * move;
            PoseIk.SolveLimb(pose, upper, fore, wrist, pose.Position(wrist) + dir * move, pose.Rotation(wrist), minBend);
        }
        if (bestPose is not null)
        {
            foreach (var bone in pose.SubtreeOrdered(upper)) pose.SetRotation(bone, bestPose.Rotation(bone));
            palmTarget = bestTarget;
        }
        var endScore = Score(pose);
        if (startScore < endScore - 0.0005f)
        {
            foreach (var bone in pose.SubtreeOrdered(upper)) pose.SetRotation(bone, startPose.Rotation(bone));
            palmTarget = startTarget;
            Trace?.Invoke($"    flush made it worse ({startScore * 1000:0} -> {endScore * 1000:0}), keep start");
        }
        return palmTarget;
    }

    /// <summary>
    /// 手掌贴得怎么样（和用哪个掌心朝向算的无关）：手掌皮肤里法线朝掌心那面（和 axis 夹角 < 53°）的顶点，
    /// Touch = 离表面 −8…+5mm 的比例，Far = 离开 15mm 以上的比例；拇指根肉垫压进去超过 16mm 的部分扣分。Score 越大越好。
    /// </summary>
    private static (float Score, float Touch, float Far) PalmQuality(EditPose pose, PoseRig rig, Node hand, HandShape shape, Surface surface, Vector3 axisLocal)
    {
        var axis = Vector3.Normalize(Vector3.Transform(axisLocal, pose.Rotation(hand.Chain[2])));
        var (points, normals) = shape.SkinWithNormals(pose, rig, shape.Palm(pose));
        int count = 0, touch = 0, far = 0;
        for (var i = 0; i < points.Length; i++)
        {
            if (Vector3.Dot(normals[i], axis) <= 0.6f || surface.Closest(points[i], 0.1f) is not { } c) continue;
            count++;
            if (c.Signed < 0.005f && c.Signed > -0.008f) touch++;
            if (c.Signed >= 0.015f) far++;
        }
        var pad = float.PositiveInfinity;
        foreach (var q in shape.Skin(pose, rig, shape.Under(pose, $"j_oya_a_{hand.Side}", exact: true)))
            if (surface.Closest(q, 0.1f) is { } c) pad = MathF.Min(pad, c.Signed);
        if (count == 0) return (float.MinValue, 0f, 1f);
        float t = (float)touch / count, f = (float)far / count;
        var padPenalty = float.IsPositiveInfinity(pad) ? 0f : MathF.Max(0f, -pad - 0.016f) * 20f;
        return (t - f - padPenalty, t, f);
    }

    /// <summary>
    /// 转拇指根（j_oya_a，绕它自己的关节）把压进去的拇指根肉垫抬出来：试几个方向和角度（4°–20°），
    /// 挑肉垫最深点抬得最多的；离绑定姿势不超过 ThumbRootMax。没改善就不动、返回 false。
    /// </summary>
    private static bool LiftThenar(EditPose pose, PoseRig rig, Node hand, HandShape shape, int[] thenar, Surface place, Surface? whole, Vector3 normal, float need)
    {
        var root = $"j_oya_a_{hand.Side}";
        if (!pose.Has(root) || thenar.Length == 0) return false;
        float Deepest(EditPose p)
        {
            var deepest = float.PositiveInfinity;
            foreach (var point in shape.Skin(p, rig, thenar))
            {
                Contact? hit = place.Closest(point, 0.08f);
                if (whole?.Closest(point, 0.08f) is { } w && (hit is not { } h0 || w.Signed < h0.Signed)) hit = w;
                if (hit is { } c) deepest = MathF.Min(deepest, c.Signed);
            }
            return deepest;
        }
        var start = Deepest(pose);
        if (float.IsPositiveInfinity(start)) return false;
        // 拇指本身（j_oya_b 以下）不能因为抬肉垫被转进别的地方（比如另一只手）
        var distal = shape.Under(pose, $"j_oya_b_{hand.Side}");
        float DistalDeepest(EditPose p)
        {
            var deepest = float.PositiveInfinity;
            foreach (var point in shape.Skin(p, rig, distal))
            {
                Contact? hit = place.Closest(point, 0.08f);
                if (whole?.Closest(point, 0.08f) is { } w && (hit is not { } h0 || w.Signed < h0.Signed)) hit = w;
                if (hit is { } c) deepest = MathF.Min(deepest, c.Signed);
            }
            return deepest;
        }
        var distalFloor = MathF.Min(DistalDeepest(pose), Inside);
        var pivot = pose.Position(root);
        var pad = shape.Skin(pose, rig, thenar).Aggregate(Vector3.Zero, (s, v) => s + v) / thenar.Length;
        var lever = pad - pivot;
        // 绕「杠杆 × 法线」转：肉垫沿法线方向抬起；再加上它的垂直方向（往掌心侧 / 往外侧偏一点）
        var a1 = Vector3.Cross(lever, normal);
        if (a1.LengthSquared() < 1e-10f) return false;
        a1 = Vector3.Normalize(a1);
        var a2 = Vector3.Normalize(Vector3.Cross(normal, a1));
        var bindInverse = rig.HasBind(root) ? Quaternion.Conjugate(rig.BindLocal(root)) : (Quaternion?)null;
        var best = start;
        Quaternion? bestTurn = null;
        foreach (var axis in new[] { a1, -a1, Vector3.Normalize(a1 + a2), Vector3.Normalize(a1 - a2), Vector3.Normalize(-a1 + a2), Vector3.Normalize(-a1 - a2) })
            for (var degrees = 4f; degrees <= 20f; degrees += 4f)
            {
                var turn = Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f);
                var trial = pose.Clone();
                trial.RotateSubtree(root, pivot, turn);
                if (bindInverse is { } bi)
                {
                    var deviation = Quaternion.Normalize(bi * trial.LocalRotation(root));
                    if (2 * MathF.Acos(Math.Clamp(MathF.Abs(deviation.W), 0f, 1f)) * 180f / MathF.PI > ThumbRootMax) continue;
                }
                if (distal.Length > 0 && DistalDeepest(trial) < distalFloor - 0.001f) continue;
                var d = Deepest(trial);
                // 要的是抬起来：比现在至少好 1mm；同样好时角度小的优先（循环从小到大，严格大于才换）
                if (d > best + 0.001f) (best, bestTurn) = (d, turn);
                if (d >= start + need) break;
            }
        if (bestTurn is not { } chosen) return false;
        pose.RotateSubtree(root, pivot, chosen);
        Trace?.Invoke($"    lift thenar: {start * 1000:0}mm -> {best * 1000:0}mm");
        return true;
    }

    /// <summary>4×4 线性方程组（高斯消元，选主元）；奇异时 null。</summary>
    private static double[]? Solve4(double[,] a, double[] b)
    {
        var m = new double[4, 5];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++) m[i, j] = a[i, j];
            m[i, 4] = b[i];
        }
        for (var col = 0; col < 4; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 4; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-12) return null;
            for (var k = 0; k < 5; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            for (var r = 0; r < 4; r++)
            {
                if (r == col) continue;
                var f = m[r, col] / m[col, col];
                for (var k = col; k < 5; k++) m[r, k] -= f * m[col, k];
            }
        }
        return [m[0, 4] / m[0, 0], m[1, 4] / m[1, 1], m[2, 4] / m[2, 2], m[3, 4] / m[3, 3]];
    }

    /// <summary>拇指往食指那边收（绕掌心法线转拇指根）：fraction 是收多少（拇指方向和食指根方向在手掌平面里的夹角的比例）。</summary>
    private static void AdductThumb(EditPose pose, Node thumb, float fraction)
    {
        var root = thumb.Chain[0];
        var index = $"j_hito_a_{thumb.Side}";
        if (!pose.Has(index) || !pose.Has(thumb.Chain[^1])) return;
        var n = PalmNormal(pose, thumb.Side);
        var pivot = pose.Position(root);
        Vector3 Flat(Vector3 v) => v - n * Vector3.Dot(v, n);
        var a = Flat(pose.Position(thumb.Chain[^1]) - pivot);
        var b = Flat(pose.Position(index) - pivot);
        if (a.LengthSquared() < 1e-8f || b.LengthSquared() < 1e-8f) return;
        var angle = MathF.Atan2(Vector3.Dot(n, Vector3.Cross(a, b)), Vector3.Dot(a, b));
        pose.RotateSubtree(root, pivot, Quaternion.CreateFromAxisAngle(n, angle * fraction));
    }
    /// <summary>
    /// 拇指放平：绑定姿势里拇指整根伸在掌心前面（像要去对掌），手掌平按到身上时会插进去、把手掌顶开。
    /// 真人把手平按下去时拇指落回掌心那个平面：绕「拇指方向 × 掌心法线」转拇指根，让拇指掌心侧最高的点不比掌心皮肤高出 8mm（拇指比手掌厚，拇指肚本来就高一截）。
    /// 最多转 25°：再多拇指就翻到手背后面去了，拇指根的蒙皮也会被拧出折痕。
    /// </summary>
    private static void FlattenThumb(EditPose pose, PoseRig rig, Node hand, Node thumb, HandShape shape)
    {
        var root = thumb.Chain[0];
        if (!pose.Has(thumb.Chain[^1])) return;
        var thumbVerts = shape.Under(pose, root);
        var palmVerts = shape.Palm(pose).Except(shape.Under(pose, root, exact: true)).ToArray();
        if (thumbVerts.Length == 0 || palmVerts.Length < 6) return;
        var center = PalmCenter(pose, hand.Side, rig) ?? pose.Position(hand.Chain[2]);
        var n = PalmNormal(pose, hand.Side, MeshPalm ? shape : null, rig);
        float Facing(Vector3 p) => Vector3.Dot(p - center, n);
        // 掌心皮肤的高度：掌心一侧顶点里靠外的那些（80% 分位）
        var palmLevels = shape.Skin(pose, rig, palmVerts).Select(Facing).Order().ToArray();
        var level = palmLevels[palmLevels.Length * 8 / 10];
        float Highest() => shape.Skin(pose, rig, thumbVerts).Max(Facing);
        const float allowance = 0.008f;
        var excess = Highest() - level - allowance;
        if (excess <= 0.002f) return;
        var pivot = pose.Position(root);
        var along = pose.Position(thumb.Chain[^1]) - pivot;
        along -= n * Vector3.Dot(along, n);
        if (along.LengthSquared() < 1e-8f) return;
        // 转轴：让拇指尖往手背方向（−n）走
        var axis = Vector3.Normalize(Vector3.Cross(n, along));
        float best = 0, bestExcess = excess;
        for (var deg = 5f; deg <= 25f; deg += 5f)
        {
            var trial = pose.Clone();
            trial.RotateSubtree(root, pivot, Quaternion.CreateFromAxisAngle(axis, deg * MathF.PI / 180f));
            if (ThumbRoom(trial, rig, thumb) <= 0) break;
            var e = shape.Skin(trial, rig, thumbVerts).Max(p => Vector3.Dot(p - center, n)) - level - allowance;
            if (MathF.Abs(e) < MathF.Abs(bestExcess)) (best, bestExcess) = (deg, e);
            if (e <= 0) break;
        }
        if (best > 0) pose.RotateSubtree(root, pivot, Quaternion.CreateFromAxisAngle(axis, best * MathF.PI / 180f));
        Trace?.Invoke($"  flatten thumb: excess {excess * 1000:0}mm -> {bestExcess * 1000:0}mm by {best}deg");
    }

    /// <summary>
    /// 手够不着 target 时弯腰去够（真人伸手摸大腿前面、膝盖时会俯身）：腰、背（j_sebo_a、j_sebo_b）往目标那边转，
    /// 转到肩膀离目标不超过胳膊长度的 95% 为止，最多 30°。整个上身跟着转，钉住的另一只手之后会被拉回原处。
    /// </summary>
    public static void Lean(EditPose pose, Node hand, Vector3 target, PoseRig? rig = null)
    {
        string upper = hand.Chain[0], fore = hand.Chain[1], end = hand.Chain[2];
        if (!pose.Has("j_sebo_a") || !pose.Has("j_sebo_b")) return;
        var palm = PalmCenter(pose, hand.Side, rig) ?? pose.Position(end);
        var reach = 0.95f * (Vector3.Distance(pose.Position(upper), pose.Position(fore)) + Vector3.Distance(pose.Position(fore), pose.Position(end))
                             + Vector3.Distance(pose.Position(end), palm));
        if (Vector3.Distance(pose.Position(upper), target) <= reach) return;
        var pivot = pose.Position("j_sebo_a");
        var full = PoseIk.Swing(pose.Position(upper) - pivot, target - pivot);
        var fullAngle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(full.W), 0f, 1f));
        if (fullAngle < 1e-4f) return;
        var limit = MathF.Min(1f, 30f * MathF.PI / 180f / fullAngle);
        // 二分：转多少能刚好够着（两节各分一半）
        float lo = 0, hi = limit;
        for (var i = 0; i < 12; i++)
        {
            var mid = (lo + hi) / 2;
            var trial = pose.Clone();
            PoseOps.RotateChain(trial, ["j_sebo_a", "j_sebo_b"], [0.5f, 0.5f], Quaternion.Slerp(Quaternion.Identity, full, mid));
            if (Vector3.Distance(trial.Position(upper), target) > reach) lo = mid;
            else hi = mid;
        }
        PoseOps.RotateChain(pose, ["j_sebo_a", "j_sebo_b"], [0.5f, 0.5f], Quaternion.Slerp(Quaternion.Identity, full, hi));
    }

    /// <summary>
    /// 胳膊穿过身体时（手伸到对侧胸口、背后，原来的肘朝向会让上臂、前臂钻进躯干），绕「肩 → 手腕」连线转一下肘的朝向：
    /// 手的位置、朝向都不变，挑上臂和前臂离身体最远、转得最少的角度。上臂靠近肩膀那段本来就连着身体，不算。
    /// </summary>
    public static void AvoidBody(EditPose pose, PoseRig rig, Node limb, Surface surface, HandShape? arm = null, bool thorough = false)
    {
        // 胳膊贴着自己身体时肌肉会互相挤压一两厘米（摸同侧胸口时前臂贴着胸侧），这点不算穿模，超出的部分才计
        static float Soft(float depth) => MathF.Max(0f, depth - ArmSoft);
        string upper = limb.Chain[0], fore = limb.Chain[1], end = limb.Chain[2];
        // 有胳膊的网格时量真实的皮肤（贴着身体 3mm 以内算挨着，不算穿）；没有时按估计的粗细量骨骼上几个点。
        // 肩膀附近（本来就连着身体）和手腕附近（手正贴着表面）不量
        // 胳膊表面很平滑：拖动中抽 80 个点量（高面数身体上每个点找最近表面都不便宜）；松手时那次完整的用全部点，免得漏掉腋下
        var all = arm is null ? [] : Enumerable.Range(0, arm.Count).Where((_, i) => thorough || arm.Count <= 80 || i % ((arm.Count + 79) / 80) == 0).ToArray();
        var helperDrivers = new HashSet<string> { upper, fore, end };
        var torso = TorsoVolume.Of(surface, pose);
        float Depth(EditPose p)
        {
            Vector3 s = p.Position(upper), e = p.Position(fore), w = p.Position(end);
            var depth = 0f;
            if (arm is not null)
            {
                // 袖子跟着肩、肘、腕的辅助骨（n_hkata、n_hhiji…）动，那些最后才更新：量之前先在副本上算一遍，不然量的是旧袖子
                var h = p;
                if (thorough)
                {
                    h = p.Clone();
                    PoseOps.FollowHelpers(null, h, rig, helperDrivers);
                }
                foreach (var point in arm.Skin(h, rig, all))
                {
                    if (Vector3.DistanceSquared(point, s) < 0.10f * 0.10f || Vector3.DistanceSquared(point, w) < 0.07f * 0.07f) continue;
                    if (surface.Closest(point, 0.08f) is { } c) depth = MathF.Max(depth, -c.Signed - 0.003f);
                    // 8cm 内找不到表面：可能整段埋在躯干里（伸手摸对侧胸口时上臂压进胸肌），用躯干截面轮廓量埋多深
                    else if (torso is not null) depth = MathF.Max(depth, torso.Depth(point) - 0.003f);
                }
                return depth;
            }
            (Vector3 Point, float Radius)[] samples = [(Vector3.Lerp(s, e, 0.6f), 0.045f), (e, 0.04f), (Vector3.Lerp(e, w, 0.4f), 0.033f)];
            foreach (var (point, radius) in samples)
                if (surface.Closest(point, 0.15f) is { } c) depth = MathF.Max(depth, radius - c.Signed);
            return depth;
        }
        // 肘抬过肩膀很别扭（真人摸对侧胸口、肚子时肘是往前、往下让开的）：高出肩膀的部分重重扣分，整体也偏好肘低一点
        // 手的朝向不变、前臂转了，手腕的角度就变了：超出活动范围的度数稍微扣分（超了之后会单独拉回来，所以以胳膊不穿为主）
        var bindInverse = rig.HasBind(end) ? Quaternion.Conjugate(rig.BindLocal(end)) : Quaternion.Identity;
        float WristExcess(EditPose p)
        {
            if (!rig.HasBind(end)) return 0;
            return WristCost(bindInverse * p.LocalRotation(end), end).Over + UpperTwistCost(p, rig, upper).Over;
        }
        float Score(EditPose p, int step)
        {
            var lift = p.Position(fore).Y - p.Position(upper).Y;
            // 肘高过「肩和手腕里高的那个」才重罚：捂胸口时肘抬到脸前很假；手伸到对侧肩顶时肘跟着抬到那个高度是正常的
            var raise = p.Position(fore).Y - MathF.Max(p.Position(upper).Y, p.Position(end).Y);
            // 肘自然垂着最舒服：肘每高 10cm 相当于多压进 1cm（以前几乎不管，摸同侧胸口时会选前臂横放、肘抬到肩那么高）
            return Soft(Depth(p)) * 3 + step * 0.002f + MathF.Max(0, raise) * 2f + lift * 0.3f + WristExcess(p) * 0.006f;   // 手腕 / 上臂扭转每超 1°，相当于多压进 2mm
        }
        var now = Depth(pose);
        if (Trace is not null && arm is not null)
        {
            var h = pose.Clone();
            PoseOps.FollowHelpers(null, h, rig, helperDrivers);
            var pts = arm.Skin(h, rig, all);
            var worst = pts.Select(q => (q, c: surface.Closest(q, 0.08f))).Where(x => x.c is not null).OrderBy(x => x.c!.Value.Signed).FirstOrDefault();
            if (worst.c is { } wc)
                Trace($"  avoid body check: {now * 1000:0}mm; raw worst {-wc.Signed * 1000:0}mm at {Vector3.Distance(worst.q, h.Position(upper)) * 100:0}cm from shoulder, {Vector3.Distance(worst.q, h.Position(end)) * 100:0}cm from wrist, into {wc.Bone}");
        }
        if (now <= ArmSoft) return;
        var best = 0f;
        var bestScore = Score(pose, 0);
        // 先每 20° 粗找，再在最好的角度两边各 10° 细找
        for (var step = 2; step <= 18; step += 2)
            foreach (var sign in new[] { 1f, -1f })
            {
                var angle = sign * step * 10f * MathF.PI / 180f;
                var trial = pose.Clone();
                PoseIk.SwivelBy(trial, upper, end, angle);
                var score = Score(trial, step);
                if (score < bestScore) (bestScore, best) = (score, angle);
            }
        var coarse = best;
        foreach (var offset in new[] { -10f, 10f })
        {
            var angle = coarse + offset * MathF.PI / 180f;
            var steps = (int)MathF.Round(MathF.Abs(angle) * 180f / MathF.PI / 10f);
            if (steps == 0 || steps > 18) continue;
            var trial = pose.Clone();
            PoseIk.SwivelBy(trial, upper, end, angle);
            var score = Score(trial, steps);
            if (score < bestScore) (bestScore, best) = (score, angle);
        }
        if (best != 0) PoseIk.SwivelBy(pose, upper, end, best);
        Trace?.Invoke($"  avoid body: depth {now * 1000:0}mm -> {Depth(pose) * 1000:0}mm swivel {best * 180 / MathF.PI:0}deg");

        // 胳膊快伸直时转肘没用（肩到手腕的距离定了）：真人会动肩胛——伸手摸对侧肩时肩往前探，手垂在腿边、胳膊贴着身体时肩稍微往外、往上。
        // 试着转锁骨（j_sako）5–15°，手的位置、朝向不变重新解胳膊，再配一点转肘，挑压得最浅、动得最少的
        var clavicle = $"j_sako_{limb.Side}";
        if (!thorough || Depth(pose) <= ArmSoft + 0.004f || !pose.Has(clavicle) || pose.Parent(upper) != clavicle) return;
        var wristAt = pose.Position(end);
        var wristRot = pose.Rotation(end);
        var outward = limb.Side == "l" ? Vector3.UnitX : -Vector3.UnitX;   // 模型 +x 是角色左边
        // 只往上（耸肩）、往前（伸肩）和两者之间转，不往下沉、不往后收——真人伸手时锁骨不会往下压，那样看起来就是「肩塌了、拧了」
        var up = Vector3.Normalize(Vector3.Cross(outward, Vector3.UnitY));
        var forward = Vector3.Normalize(Vector3.Cross(outward, Vector3.UnitZ));
        Vector3[] axes = [up, forward, Vector3.Normalize(up + forward)];
        var start = pose.Clone();
        var bestPose = pose;
        var bestTotal = Soft(Depth(pose)) * 3 - 0.03f;   // 至少少压进 1cm 才值得动肩
        foreach (var axis in axes)
            foreach (var degrees in new[] { 4f, 8f })
            {
                var trial = start.Clone();
                trial.RotateSubtree(clavicle, trial.Position(clavicle), Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f));
                PoseIk.SolveLimb(trial, upper, fore, end, wristAt, wristRot, 30f);
                // 肩膀挪开以后胳膊够不着原来的手腕位置（手会被拉开）的不要
                if (Vector3.DistanceSquared(trial.Position(end), wristAt) > 0.005f * 0.005f) continue;
                var trialBest = trial;
                var trialScore = Soft(Depth(trial)) * 3;
                foreach (var swivel in new[] { -30f, -15f, 15f, 30f })
                {
                    var s2 = trial.Clone();
                    PoseIk.SwivelBy(s2, upper, end, swivel * MathF.PI / 180f);
                    var sc = Soft(Depth(s2)) * 3 + MathF.Abs(swivel) * 0.00005f;
                    if (sc < trialScore) (trialScore, trialBest) = (sc, s2);
                }
                var total = trialScore + degrees * 0.002f;
                if (total < bestTotal) (bestTotal, bestPose) = (total, trialBest);
            }
        if (!ReferenceEquals(bestPose, pose))
        {
            foreach (var bone in pose.SubtreeOrdered(clavicle)) pose.SetRotation(bone, bestPose.Rotation(bone));
            Trace?.Invoke($"  avoid body (clavicle): -> {Depth(pose) * 1000:0}mm");
        }
    }

    private static readonly string[] Spine = ["j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi"];

    /// <summary>点到脊椎折线（腰 → 背 → 胸 → 脖子）的距离；骨骼不全时 null。</summary>
    private static float? SpineDistance(EditPose pose, Vector3 point)
    {
        float? best = null;
        for (var i = 0; i + 1 < Spine.Length; i++)
        {
            if (!pose.Has(Spine[i]) || !pose.Has(Spine[i + 1])) continue;
            Vector3 a = pose.Position(Spine[i]), b = pose.Position(Spine[i + 1]);
            var ab = b - a;
            var t = Math.Clamp(Vector3.Dot(point - a, ab) / MathF.Max(ab.LengthSquared(), 1e-8f), 0f, 1f);
            var d = Vector3.Distance(point, a + ab * t);
            best = best is { } x ? MathF.Min(x, d) : d;
        }
        return best;
    }

    /// <summary>
    /// 掌心对着表面（−normal）时手的朝向：绕法线还有一个自由角度，试 36 个，挑手腕弯、侧偏、扭转都最小的（手顺着前臂），
    /// 和现在手指的方向差太多的稍微扣分（拖动时不乱跳）。
    /// </summary>
    /// <summary>
    /// contact 周围 radius 以内同一个角色的表面三角形法线的加权平均（按面积，越近越重），朝向和 contact 法线相反的（另一面）不算。
    /// 平面、腹肌沟壑上就是整体朝向；圆柱（前臂）上两边对称，还是点到那一面的朝向。
    /// </summary>
    /// <summary>拇指根肉垫允许压进去多少：ThenarSquash 倍压扁量再加 6mm（测试时可调）。</summary>
    public static float ThenarSquash { get; set; } = 1.5f;

    /// <summary>一次贴合（FlushPalm）总共最多倾斜多少度（测试时可调）。</summary>
    public static float TiltBudget { get; set; } = 25f;

    /// <summary>掌心朝向按手掌皮肤（HandShape.PalmAxis）；测试用开关，关掉就按中指根骨骼（对照）。</summary>
    public static bool MeshPalm { get; set; } = true;

    /// <summary>测试用开关：关掉就按点到的三角形法线（对照）。</summary>
    public static bool PatchNormals { get; set; } = true;

    /// <summary>
    /// 最近一次完整吸附（松手时那次）的提示：够不着、手掌贴不平、胳膊会压进身体时是一句给用户看的话，正常时 null。
    /// 解算在后台线程，界面在主线程读。
    /// </summary>
    public static volatile string? LastIssue;

    internal static Vector3 PatchNormal(Surface place, Contact contact, float radius = 0.05f)
    {
        var sum = contact.Normal * 1e-5f;
        foreach (var (a, b, c, n, d) in place.Around(contact.Point, radius, (actor, _, _) => actor == contact.Actor))
        {
            if (Vector3.Dot(n, contact.Normal) < 0.1f) continue;
            sum += n * (Vector3.Cross(b - a, c - a).Length() * 0.5f * (1f - d / radius));
        }
        return sum.LengthSquared() > 1e-14f ? Vector3.Normalize(sum) : contact.Normal;
    }

    private static Quaternion HandRotation(EditPose pose, PoseRig rig, Node hand, Vector3 normal, Surface? surface = null, Vector3? point = null, Vector3? palmAxis = null)
    {
        var side = hand.Side;
        string fore = hand.Chain[1], wrist = hand.Chain[2];
        // 手腕局部坐标里：掌心朝向（手掌皮肤的实际朝向；没有网格时用中指根绑定时的 −y）、手指方向（手腕到中指根）
        var palmLocal = palmAxis ?? Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, rig.BindLocal($"j_naka_a_{side}")));
        var fingerLocal = Vector3.Normalize(rig.BindOffset($"j_naka_a_{side}", wrist)!.Value);
        fingerLocal = Vector3.Normalize(fingerLocal - palmLocal * Vector3.Dot(fingerLocal, palmLocal));
        var local = Basis(fingerLocal, palmLocal);
        var n = Vector3.Normalize(normal);
        var u = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(n, u);
        var current = Vector3.Transform(fingerLocal, pose.Rotation(wrist));
        var foreInverse = Quaternion.Conjugate(pose.Rotation(fore));
        var bindInverse = Quaternion.Conjugate(rig.BindLocal(wrist));
        var best = pose.Rotation(wrist);
        var bestCost = float.MaxValue;
        // 表面形状：从接触点往各个方向走 6cm，表面掉下去多少。细长的东西（前臂、上臂）顺着轴走不掉、横着走掉好几厘米——
        // 这时手指朝掉得多的方向（横过去），才能绕过去握住；平的地方（腹肌、胸口）各方向都差不多，不影响
        var drops = new float[36];
        if (surface is not null && point is { } at)
            for (var i = 0; i < 36; i++)
            {
                var phi = i * MathF.PI / 18f;
                var dir = MathF.Cos(phi) * u + MathF.Sin(phi) * v;
                drops[i] = surface.Closest(at + dir * 0.06f, 0.1f) is { } c ? MathF.Max(0f, c.Signed) : 0.06f;
            }
        var maxDrop = drops.Max();
        var graspable = maxDrop > 0.015f;
        for (var i = 0; i < 36; i++)
        {
            var phi = i * MathF.PI / 18f;
            var t = MathF.Cos(phi) * u + MathF.Sin(phi) * v;
            var candidate = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(local) * Basis(t, -n)));
            var (wristOver, wristStrain) = WristCost(bindInverse * foreInverse * candidate, wrist);
            var cost = wristOver * 8f + wristStrain
                       + MathF.Acos(Math.Clamp(Vector3.Dot(t, current), -1f, 1f)) * 180f / MathF.PI * 0.1f;
            // 能握的东西：手指方向的表面掉得越多越好（每厘米相当于 10° 的手腕代价）
            if (graspable) cost -= drops[i] * 1000f;
            if (cost < bestCost) (bestCost, best) = (cost, candidate);
        }
        return best;
    }

    /// <summary>
    /// 掌心对着表面（−normal）时手可以有的朝向：绕法线每 360/count 度一个，和那个手指方向上表面掉下去多少（能握的细长东西才算，否则 0）。
    /// </summary>
    private static List<(Quaternion Rotation, float Drop)> HandCandidates(EditPose pose, PoseRig rig, Node hand, Vector3 normal, Surface surface, Vector3 point, int count, Vector3? palmAxis = null,
                                                                          bool grasp = true)
    {
        var side = hand.Side;
        var wrist = hand.Chain[2];
        var palmLocal = palmAxis ?? Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, rig.BindLocal($"j_naka_a_{side}")));
        var fingerLocal = Vector3.Normalize(rig.BindOffset($"j_naka_a_{side}", wrist)!.Value);
        fingerLocal = Vector3.Normalize(fingerLocal - palmLocal * Vector3.Dot(fingerLocal, palmLocal));
        var local = Basis(fingerLocal, palmLocal);
        var n = Vector3.Normalize(normal);
        var u = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(n, u);
        var result = new List<(Quaternion, float)>();
        var drops = new float[count];
        for (var i = 0; i < count; i++)
        {
            var phi = i * 2 * MathF.PI / count;
            var dir = MathF.Cos(phi) * u + MathF.Sin(phi) * v;
            drops[i] = surface.Closest(point + dir * 0.06f, 0.1f) is { } c ? MathF.Max(0f, c.Signed) : 0.06f;
        }
        var graspable = grasp && drops.Max() > 0.015f;
        if (grasp) Trace?.Invoke($"  drops: {string.Join(" ", drops.Select(d => (d * 1000).ToString("0")))}mm graspable={graspable}");
        for (var i = 0; i < count; i++)
        {
            var phi = i * 2 * MathF.PI / count;
            var t = MathF.Cos(phi) * u + MathF.Sin(phi) * v;
            result.Add((Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(local) * Basis(t, -n))), graspable ? drops[i] : 0f));
        }
        return result;
    }

    /// <summary>把单位向量 a 转到 b 的最小转动。</summary>
    private static Quaternion Between(Vector3 a, Vector3 b)
    {
        var dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        var axis = Vector3.Cross(a, b);
        if (axis.LengthSquared() < 1e-10f)
        {
            if (dot > 0) return Quaternion.Identity;
            axis = Vector3.Cross(a, MathF.Abs(a.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        }
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }

    /// <summary>行向量约定的旋转矩阵：把 x、y 轴转到 a、b（z 轴到 a × b）。</summary>
    private static Matrix4x4 Basis(Vector3 a, Vector3 b)
    {
        var c = Vector3.Cross(a, b);
        return new Matrix4x4(a.X, a.Y, a.Z, 0, b.X, b.Y, b.Z, 0, c.X, c.Y, c.Z, 0, 0, 0, 0, 1);
    }

    /// <summary>手相对前臂（和绑定姿势比）：扭转（绕局部 x）、侧偏（绕局部 y）、弯曲（绕局部 −z，往掌心为正），度。</summary>
    public static (float Twist, float Side, float Bend) WristAngles(Quaternion deviation)
    {
        var d = Quaternion.Normalize(deviation);
        if (d.W < 0) d = new Quaternion(-d.X, -d.Y, -d.Z, -d.W);
        var twist = 2 * MathF.Atan2(d.X, d.W);
        var swing = d * Quaternion.Conjugate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, twist));
        var angle = 2 * MathF.Acos(Math.Clamp(swing.W, -1f, 1f));
        var s = MathF.Sin(angle / 2);
        var axis = s > 1e-5f ? new Vector3(swing.X, swing.Y, swing.Z) / s : Vector3.Zero;
        const float deg = 180f / MathF.PI;
        return (twist * deg, axis.Y * angle * deg, -axis.Z * angle * deg);
    }

    /// <summary>手腕超出活动范围时拉回来（掌心会稍微不贴，但不会折手腕）。</summary>
    private static void ClampWrist(EditPose pose, PoseRig rig, string fore, string wrist, float slack = 0f)
    {
        var bind = rig.BindLocal(wrist);
        var sign = SideSign(wrist);
        var (twist, sideways, bend) = WristAngles(Quaternion.Conjugate(bind) * pose.LocalRotation(wrist));
        float ct = sign * Math.Clamp(sign * twist, WristTwistHard.Min - slack, WristTwistHard.Max + slack),
              cs = sign * Math.Clamp(sign * sideways, WristSideHard.Min - slack, WristSideHard.Max + slack),
              cb = Math.Clamp(bend, WristBendHard.Min - slack, WristBendHard.Max + slack);
        const float rad = MathF.PI / 180f;
        Quaternion Compose(float t, float s, float b)
        {
            var swingVector = new Vector3(0, s * rad, -b * rad);
            var swing = swingVector.LengthSquared() > 1e-12f ? Quaternion.CreateFromAxisAngle(Vector3.Normalize(swingVector), swingVector.Length()) : Quaternion.Identity;
            return swing * Quaternion.CreateFromAxisAngle(Vector3.UnitX, t * rad);
        }
        // 三个角各自在范围里、合起来还是太多（总转角超过 90°）：三个一起按比例缩
        var deviation = Compose(ct, cs, cb);
        var scaled = false;
        for (var i = 0; i < 3; i++)
        {
            var total = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Normalize(deviation).W), 0f, 1f)) / rad;
            if (total <= WristTotalHard + slack) break;
            var k = (WristTotalHard + slack) / total;
            (ct, cs, cb) = (ct * k, cs * k, cb * k);
            deviation = Compose(ct, cs, cb);
            scaled = true;
        }
        if (!scaled && ct == twist && cs == sideways && cb == bend) return;
        pose.SetRotation(wrist, pose.Rotation(fore) * (bind * deviation));
    }

    /// <summary>
    /// 手指各节的「零度」：绑定姿势下相对父骨骼的旋转（手指的侧摆回到绑定时，不会交叉）；骨架里没有这节的绑定姿势（比如没加载 IVCS 时的第三节）
    /// 就用它现在的样子。要在改手指之前取。
    /// </summary>
    private static Quaternion[] RestOf(EditPose pose, PoseRig rig, Node finger)
        => finger.Chain.Select(b => rig.HasBind(b) && pose.Parent(b) is { } p && rig.HasBind(p) ? rig.BindLocal(b) : pose.LocalRotation(b)).ToArray();

    /// <summary>把手指各节设成「零度 + 绕局部 −z 弯 angles 度」。</summary>
    private static void SetFinger(EditPose pose, Quaternion[] rest, Node finger, float[] angles)
    {
        for (var i = 0; i < finger.Chain.Length && i < angles.Length && i < rest.Length; i++)
        {
            var bone = finger.Chain[i];
            if (pose.Parent(bone) is not { } parent) continue;
            var local = rest[i] * Quaternion.CreateFromAxisAngle(-Vector3.UnitZ, angles[i] * MathF.PI / 180f);
            pose.SetRotation(bone, pose.Rotation(parent) * local);
        }
    }

    /// <summary>
    /// 手指合拢（参考 VR 里的 Auto Hand：各关节按比例一起弯，哪一节碰到表面，它和它前面的关节就停住，后面的继续弯）。
    /// 一直碰不到（表面往后弯走了）就留在稍微弯一点的放松样子，不会握成拳；放松时就钻进去了就往伸直的方向退。
    /// </summary>
    private static void CloseFinger(EditPose pose, PoseRig rig, Quaternion[] rest, Node finger, HandShape shape, Surface surface)
    {
        var ranges = FingerRanges(finger);
        var m = Math.Min(finger.Chain.Length, ranges.Length);
        var segments = new int[m][];
        for (var k = 0; k < m; k++)
        {
            var verts = shape.Under(pose, finger.Chain[k], exact: k < m - 1);
            segments[k] = verts.Length > 16 ? Enumerable.Range(0, 16).Select(i => verts[i * verts.Length / 16]).ToArray() : verts;
        }
        var all = segments.SelectMany(s => s).ToArray();
        if (all.Length == 0) return;

        // 每节离表面最近的距离（没找到表面为 +∞）。一开始放松时看远一点（可能已经埋得深），合拢时一点点弯过去，看近处就够
        var reach = FingerReach;
        float[] Distances()
        {
            var points = shape.Skin(pose, rig, all);
            var result = new float[m];
            var at = 0;
            for (var k = 0; k < m; k++)
            {
                result[k] = float.PositiveInfinity;
                for (var i = 0; i < segments[k].Length; i++, at++)
                    if (surface.Closest(points[at], reach) is { } c) result[k] = MathF.Min(result[k], c.Signed);
            }
            return result;
        }

        var angles = ranges.Take(m).Select(r => r.Base).ToArray();
        SetFinger(pose, rest, finger, angles);
        var isThumb = finger.Id.StartsWith("finger_oya", StringComparison.Ordinal);
        // 指根那一节的掌侧和手掌连成一片：手掌允许压扁多少，它也允许压扁多少（不然手掌一贴紧，指根就算「钻进去」、手指被迫往后翘）
        var allow = new float[m];
        for (var k = 0; k < m; k++) allow[k] = k == 0 ? Inside - PadSquash : Inside;
        float Excess(float[] d)
        {
            var e = 0f;
            for (var k = 0; k < m; k++) e = MathF.Max(e, allow[k] - d[k]);
            return e;
        }
        var frozen = new bool[m];
        var touched = false;
        if (Excess(Distances()) > 0)
        {
            // 放松时就钻进去了：往伸直的方向退。真人只有指根关节（MCP）能往后翘（被按着时被动能到 20° 多），中节、末节最多伸直、不反弓；
            // 退到底也出不来（是指根贴着表面压着）就挑钻得最浅的那一档，别为了一点点白白翘起来
            var best = 0;
            var bestExcess = float.PositiveInfinity;
            for (var s = 0; s <= 10; s++)
            {
                var trial = angles.Select((a, k) => k == 0 ? a - s * 2.5f : MathF.Max(0f, a - s * 2.5f)).ToArray();
                SetFinger(pose, rest, finger, trial);
                var e = Excess(Distances()) + s * 0.0004f;
                if (e < bestExcess) (bestExcess, best) = (e, s);
                if (e <= s * 0.0004f) break;
            }
            angles = angles.Select((a, k) => k == 0 ? a - best * 2.5f : MathF.Max(0f, a - best * 2.5f)).ToArray();
            SetFinger(pose, rest, finger, angles);
            if (isThumb)
            {
                OpenThumb(pose, rig, finger, shape, surface, all);
                return;
            }
            // 指根那节压在表面上停住，中节、末节照样往下合拢（握圆柱时手掌压上去，手指还要绕过去包住）
            frozen[0] = true;
            touched = true;
        }

        reach = 0.08f;   // 握圆柱时指尖伸出边缘，离表面可能 5cm 以上：看远一点才知道「往下弯还在靠近」
        // 没有顶点跟着动的那节（比如原版手套没绑第三节）量不到碰没碰：跟前一节按比例走
        void Couple()
        {
            for (var j = 1; j < m; j++)
                if (segments[j].Length == 0)
                    angles[j] = ranges[j].Base + (angles[j - 1] - ranges[j - 1].Base) / ranges[j - 1].Range * ranges[j].Range;
        }
        // 合拢（参考 AutoHand）：各关节一起弯，碰到的那节和它前面的关节停住，后面的继续弯——只要还在靠近表面就继续（握圆柱时手指绕过去包住），
        // 越弯离表面越远（表面往后弯走了，比如胯侧）就停，不会在空中勾成钩子。一直没碰到的手指最多弯到 MaxClose，之后回到放松的样子
        const int fineSteps = 30;
        var prevRemain = float.PositiveInfinity;
        var noProgress = 0;
        for (var s = 1; s <= fineSteps; s++)
        {
            var t = (float)s / fineSteps;
            if (!touched && t > MaxClose) break;
            var previous = (float[])angles.Clone();
            for (var j = 0; j < m; j++)
                if (!frozen[j]) angles[j] = ranges[j].Base + t * ranges[j].Range;
            Couple();
            SetFinger(pose, rest, finger, angles);
            var d = Distances();
            var deepest = -1;
            for (var k = 0; k < m; k++)
                if (d[k] < allow[k]) deepest = k;
            if (deepest >= 0)
            {
                // 这一步弯过头钻进去了：钻进去的那节和它前面的关节退回上一步并停住
                for (var j = 0; j <= deepest; j++)
                {
                    angles[j] = previous[j];
                    frozen[j] = true;
                }
                Couple();
                SetFinger(pose, rest, finger, angles);
                touched = true;
            }
            else
                for (var k = 0; k < m; k++)
                    if (d[k] < allow[k] + (TouchDistance - Inside))
                    {
                        for (var j = 0; j <= k; j++) frozen[j] = true;
                        touched = true;
                    }
            if (frozen.All(f => f)) break;
            if (!touched) continue;
            // 碰到以后：还没停的那几节离表面越来越近才继续弯
            var remain = float.PositiveInfinity;
            for (var k = 0; k < m; k++)
                if (!frozen[k]) remain = MathF.Min(remain, d[k]);
            Trace?.Invoke($"      close {finger.Id} t={t:0.00} d={string.Join("/", d.Select(x => float.IsPositiveInfinity(x) ? "inf" : (x * 1000).ToString("0")))} frozen={string.Join("", frozen.Select(f => f ? "1" : "0"))} remain={(float.IsPositiveInfinity(remain) ? "inf" : (remain * 1000).ToString("0"))}");
            if (float.IsPositiveInfinity(remain) || remain >= prevRemain - 0.0005f) noProgress++;
            else noProgress = 0;
            prevRemain = MathF.Min(prevRemain, remain);
            if (noProgress >= 2)
            {
                for (var j = 0; j < m; j++)
                    if (!frozen[j]) angles[j] = previous[j];
                Couple();
                SetFinger(pose, rest, finger, angles);
                break;
            }
        }
        if (!touched) SetFinger(pose, rest, finger, ranges.Take(m).Select(r => r.Base + 0.15f * r.Range).ToArray());
        else
        {
            Couple();
            SetFinger(pose, rest, finger, angles);
        }
        if (isThumb) OpenThumb(pose, rig, finger, shape, surface, all);
    }

    /// <summary>
    /// 拇指落下：合拢完拇指还悬在空中（比如搭肩时拇指横着伸出去），真人会让它落到旁边的表面上。
    /// 拇指尖附近 10cm 内有表面就绕拇指根把拇指尖往那儿转（最多 50°），落到贴着为止，钻进去就退回一半。
    /// </summary>
    private static void LowerThumb(EditPose pose, PoseRig rig, Node thumb, HandShape shape, Surface surface)
    {
        if (thumb.Chain.Length < 2) return;
        var verts = shape.Under(pose, thumb.Chain[1]);   // 只看拇指后两节：拇指根那块肉本来就贴着
        if (verts.Length == 0) return;
        var root = thumb.Chain[0];
        var tipBone = thumb.Chain[^1];
        float Nearest(EditPose p)
        {
            var best = float.PositiveInfinity;
            foreach (var point in shape.Skin(p, rig, verts))
                if (surface.Closest(point, FingerReach) is { } c) best = MathF.Min(best, c.Signed);
            return best;
        }
        var pad = shape.Under(pose, root, exact: true);
        float PadDepth(EditPose p)
        {
            var best = float.PositiveInfinity;
            foreach (var point in shape.Skin(p, rig, pad))
                if (surface.Closest(point, FingerReach) is { } c) best = MathF.Min(best, c.Signed);
            return best;
        }
        var start = Nearest(pose);
        if (start < TouchDistance + 0.003f) return;
        // 拇指根那块肉：转的时候不能比现在压得更深（已经比允许的浅就以允许的为准）
        var padLimit = MathF.Min(PadDepth(pose), Inside - PadSquash) - 0.001f;
        var tip = pose.Position(tipBone) + Vector3.Transform(Vector3.UnitX * 0.015f, pose.Rotation(tipBone));
        if (surface.Closest(tip, 0.10f) is not { } spot) return;
        var pivot = pose.Position(root);
        var full = PoseIk.Swing(tip - pivot, spot.Point + spot.Normal * FingerTarget - pivot);
        var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(full.W), 0f, 1f));
        if (angle < 1e-3f) return;
        var limit = MathF.Min(1f, 50f * MathF.PI / 180f / angle);
        // 一点点转过去，碰到就停；钻进去就退回上一步
        var previous = 0f;
        for (var s = 1; s <= 10; s++)
        {
            var f = limit * s / 10f;
            var trial = pose.Clone();
            trial.RotateSubtree(root, pivot, Quaternion.Slerp(Quaternion.Identity, full, f));
            if (ThumbRoom(trial, rig, thumb) <= 0) break;
            var d = Nearest(trial);
            if (d < Inside || PadDepth(trial) < padLimit) break;
            previous = f;
            if (d < TouchDistance) break;
        }
        if (previous > 0) pose.RotateSubtree(root, pivot, Quaternion.Slerp(Quaternion.Identity, full, previous));
        Trace?.Invoke($"  lower thumb: {start * 1000:0}mm -> {Nearest(pose) * 1000:0}mm ({previous * angle * 180 / MathF.PI:0}deg)");
    }

    /// <summary>
    /// 拇指最后落到表面：拇指根绕两个轴（朝表面摆、绕拇指自己转）和 j_oya_b 弯曲（局部 −z，和手指一样）三个量一起网格搜索，
    /// 让拇指末节离表面最近的点最接近 FingerTarget、不压进去超过 Inside；拇指根那块肉垫不能比现在压得更深，
    /// 拇指根不超过 ThumbRootMax、末节偏离绑定姿势不超过 60°。以前只转拇指根：肉垫一贴上就转不动，拇指整根悬在半空。
    /// 末节附近 FingerReach 内没有表面（悬空的手）就不动。
    /// </summary>
    private static void SettleThumbTip(EditPose pose, PoseRig rig, Node thumb, HandShape shape, Surface surface)
    {
        if (thumb.Chain.Length < 2) return;
        string root = thumb.Chain[0], joint = thumb.Chain[1];
        var verts = shape.Under(pose, joint);
        var pad = shape.Under(pose, root, exact: true);
        if (verts.Length == 0 || !pose.Has(joint) || !pose.Has(root)) return;
        float Nearest(EditPose p, int[] which)
        {
            var best = float.PositiveInfinity;
            foreach (var point in shape.Skin(p, rig, which))
                if (surface.Closest(point, FingerReach) is { } c) best = MathF.Min(best, c.Signed);
            return best;
        }
        var start = Nearest(pose, verts);
        if (float.IsPositiveInfinity(start) || (start >= Inside && start <= FingerTarget + 0.003f)) return;
        var padLimit = MathF.Min(Nearest(pose, pad), Inside - PadSquash) - 0.001f;
        var rootPivot = pose.Position(root);
        var tip = pose.Position(joint) + Vector3.Transform(Vector3.UnitX * 0.015f, pose.Rotation(joint));
        if (surface.Closest(tip, 0.10f) is not { } spot) return;
        var along = Vector3.Normalize(tip - rootPivot);
        var swingAxis = Vector3.Cross(along, -spot.Normal);   // 绕它正转 = 拇指尖往表面走
        if (swingAxis.LengthSquared() < 1e-8f) return;
        swingAxis = Vector3.Normalize(swingAxis);
        var rootBind = rig.HasBind(root) ? Quaternion.Conjugate(rig.BindLocal(root)) : (Quaternion?)null;
        var jointBind = rig.HasBind(joint) ? Quaternion.Conjugate(rig.BindLocal(joint)) : (Quaternion?)null;
        static float Off(Quaternion bindInverse, Quaternion local)
        {
            var d = Quaternion.Normalize(bindInverse * local);
            return 2 * MathF.Acos(Math.Clamp(MathF.Abs(d.W), 0f, 1f)) * 180f / MathF.PI;
        }
        float Cost(float d) => d < Inside ? 1f + (Inside - d) * 100f : MathF.Abs(d - FingerTarget);
        var bestCost = Cost(start) - 0.0005f;   // 要比现在好一点才动
        (float Swing, float Roll, float Flex)? best = null;
        var bestMotion = float.MaxValue;
        for (var swing = -10f; swing <= 40f; swing += 5f)
        for (var roll = -30f; roll <= 30f; roll += 10f)
        {
            var trial = pose.Clone();
            var turn = Quaternion.CreateFromAxisAngle(swingAxis, swing * MathF.PI / 180f) * Quaternion.CreateFromAxisAngle(along, roll * MathF.PI / 180f);
            trial.RotateSubtree(root, rootPivot, turn);
            if (rootBind is { } rb && Off(rb, trial.LocalRotation(root)) > ThumbRootMax + 5f && (swing != 0f || roll != 0f)) continue;
            if ((swing != 0f || roll != 0f) && Nearest(trial, pad) < padLimit) continue;
            var jointPivot = trial.Position(joint);
            var flexAxis = Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, trial.Rotation(joint)));
            for (var flex = -20f; flex <= 55f; flex += 5f)
            {
                var t2 = flex == 0f ? trial : trial.Clone();
                if (flex != 0f) t2.RotateSubtree(joint, jointPivot, Quaternion.CreateFromAxisAngle(flexAxis, flex * MathF.PI / 180f));
                if (jointBind is { } jb && flex != 0f && Off(jb, t2.LocalRotation(joint)) > 60f) continue;
                var cost = Cost(Nearest(t2, verts));
                var motion = MathF.Abs(swing) + MathF.Abs(roll) + MathF.Abs(flex);
                // 同样好（差不到 0.5mm）时动得少的优先
                if (cost < bestCost - 0.0005f || (cost <= bestCost + 0.0005f && best is not null && motion < bestMotion))
                    (bestCost, best, bestMotion) = (MathF.Min(cost, bestCost), (swing, roll, flex), motion);
            }
        }
        if (best is not { } b) return;
        pose.RotateSubtree(root, rootPivot, Quaternion.CreateFromAxisAngle(swingAxis, b.Swing * MathF.PI / 180f) * Quaternion.CreateFromAxisAngle(along, b.Roll * MathF.PI / 180f));
        var axis = Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, pose.Rotation(joint)));
        pose.RotateSubtree(joint, pose.Position(joint), Quaternion.CreateFromAxisAngle(axis, b.Flex * MathF.PI / 180f));
        Trace?.Invoke($"  settle thumb: {start * 1000:0}mm -> {Nearest(pose, verts) * 1000:0}mm (swing {b.Swing:0} roll {b.Roll:0} flex {b.Flex:0})");
    }

    /// <summary>拇指根（j_oya_a）相对绑定姿势最多转多少度（放平、张开、落下几步共用）：再多拇指会翻到手背、拇指根的蒙皮被拧出折痕。</summary>
    private const float ThumbRootMax = 50f;

    /// <summary>拇指根离 ThumbRootMax 还剩多少度（没有绑定数据时不限）。</summary>
    private static float ThumbRoom(EditPose pose, PoseRig rig, Node thumb)
    {
        var root = thumb.Chain[0];
        if (!rig.HasBind(root)) return 180f;
        var deviation = Quaternion.Normalize(Quaternion.Conjugate(rig.BindLocal(root)) * pose.LocalRotation(root));
        var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(deviation.W), 0f, 1f)) * 180f / MathF.PI;
        return MathF.Max(0f, ThumbRootMax - angle);
    }

    /// <summary>拇指根（j_oya_a）相对绑定姿势转的角度超过 maxDegrees 就按比例拉回（后面几节跟着走）。</summary>
    private static void LimitThumbRoot(EditPose pose, PoseRig rig, Node thumb, float maxDegrees)
    {
        var root = thumb.Chain[0];
        if (!rig.HasBind(root) || pose.Parent(root) is not { } parent) return;
        var bind = rig.BindLocal(root);
        var deviation = Quaternion.Normalize(Quaternion.Conjugate(bind) * pose.LocalRotation(root));
        if (deviation.W < 0) deviation = -deviation;
        var angle = 2 * MathF.Acos(Math.Clamp(deviation.W, 0f, 1f)) * 180f / MathF.PI;
        Trace?.Invoke($"  thumb root: {angle:0}deg from bind");
        if (angle <= maxDegrees) return;
        var limited = Quaternion.Slerp(Quaternion.Identity, deviation, maxDegrees / angle);
        pose.SetRotation(root, pose.Rotation(parent) * bind * limited);
    }

    /// <summary>
    /// 一根手指的贴合优化（参考 ContactOpt：优化的是「整片接触」，并且允许指腹压进去一点模拟软组织）。
    /// 4 个量一起调：指根弯曲、指根张开（绕掌心法线）、中节弯曲、末节弯曲。
    /// 目标：每节指腹离表面接近「贴着」（附近 3cm 内有表面的那几节才算），压进去超过软组织阈值重罚，
    /// 偏离初值（合拢 + 协调后的样子）、末节和中节不联动、张开太多都轻罚。模式搜索（步长 8° → 1°），每个量都有限位。
    /// </summary>
    private static void RefineFinger(EditPose pose, PoseRig rig, Quaternion[] rest, Node finger, HandShape shape, Surface surface, Vector3 palmNormal,
                                     IReadOnlyList<Node>? neighbors = null, IReadOnlyList<float>? neighborBends = null)
    {
        var ranges = FingerRanges(finger);
        var m = Math.Min(Math.Min(finger.Chain.Length, ranges.Length), 3);
        if (m < 3) return;
        // 每节的指腹顶点：主要跟这一节动、在掌心朝向一侧的
        var pads = new int[m][];
        var all = new List<int>();
        var allSeg = new List<int>();
        for (var k = 0; k < m; k++)
        {
            var verts = shape.Under(pose, finger.Chain[k], exact: true);
            if (verts.Length == 0) { pads[k] = []; continue; }
            var skinned = shape.Skin(pose, rig, verts);
            var axisFrom = pose.Position(finger.Chain[k]);
            var pad = verts.Where((_, i) => Vector3.Dot(skinned[i] - axisFrom, palmNormal) > -0.002f).ToArray();
            pads[k] = pad.Length > 12 ? Enumerable.Range(0, 12).Select(i => pad[i * pad.Length / 12]).ToArray() : pad;
            var sample = verts.Length > 16 ? Enumerable.Range(0, 16).Select(i => verts[i * verts.Length / 16]).ToArray() : verts;
            all.AddRange(sample);
            allSeg.AddRange(Enumerable.Repeat(k, sample.Length));
        }
        if (all.Count == 0) return;
        var allArr = all.ToArray();
        var flex0 = FingerAngles(pose, rest, finger, m);
        float[] init = [flex0[0], 0f, flex0[1], flex0[2]];
        // 限位：指根弯 [底−20°, 底+幅度]（被按着时能后翘一点），张开 ±12°，中节 / 末节 [0, 底+幅度]
        (float Lo, float Hi)[] bounds =
        [
            (ranges[0].Base - 20f, ranges[0].Base + ranges[0].Range),
            (-8f, 8f),
            (0f, ranges[1].Base + ranges[1].Range),
            (0f, ranges[2].Base + ranges[2].Range),
        ];
        void Set(EditPose p, float[] th)
        {
            for (var i = 0; i < m; i++)
            {
                var bone = finger.Chain[i];
                if (p.Parent(bone) is not { } parent) continue;
                var flex = Quaternion.CreateFromAxisAngle(-Vector3.UnitZ, (i == 0 ? th[0] : th[i + 1]) * MathF.PI / 180f);
                var local = i == 0 ? rest[i] * Quaternion.CreateFromAxisAngle(Vector3.UnitY, th[1] * MathF.PI / 180f) * flex : rest[i] * flex;
                p.SetRotation(bone, p.Rotation(parent) * local);
            }
        }
        var softFirst = Inside - PadSquash;
        float Cost(float[] th)
        {
            var trial = pose.Clone();
            Set(trial, th);
            var cost = 0f;
            // 穿模：任何顶点压进去超过阈值（指根那节和手掌一样允许多压一点）
            var pts = shape.Skin(trial, rig, allArr);
            for (var i = 0; i < pts.Length; i++)
                if (surface.Closest(pts[i], 0.03f) is { } c)
                {
                    var allow = allSeg[i] == 0 ? softFirst : Inside;
                    if (c.Signed < allow) cost += (allow - c.Signed) * (allow - c.Signed) * 40f;
                }
            // 接触：每节指腹最近的点贴着表面（附近有表面才要求）
            var touching = false;
            for (var k = 0; k < m; k++)
            {
                if (pads[k].Length == 0) continue;
                var nearest = float.PositiveInfinity;
                foreach (var q in shape.Skin(trial, rig, pads[k]))
                    if (surface.Closest(q, 0.03f) is { } c) nearest = MathF.Min(nearest, c.Signed);
                if (!float.IsPositiveInfinity(nearest) && nearest > FingerTarget) cost += (nearest - FingerTarget) * (nearest - FingerTarget);
                if (nearest < 0.01f) touching = true;
            }
            // 什么都没碰到的手指跟着旁边的手指弯（握东西时无名指、小指伸出了边缘也会跟着中指收拢，不会直直地悬着）
            if (!touching && neighborBends is { Count: > 0 })
            {
                var over = MathF.Max(0f, MathF.Abs(th[0] + th[2] - neighborBends.Max()) - 15f);
                cost += 4e-6f * over * over;
            }
            // 姿势先验（度²）：别离初值太远、末节约为中节的 2/3、少张开
            for (var i = 0; i < 4; i++) cost += 2e-7f * (th[i] - init[i]) * (th[i] - init[i]);
            var couple = th[3] - 0.67f * th[2];
            cost += 3e-7f * couple * couple + 3e-7f * th[1] * th[1];
            // 相邻手指连着动（共用屈肌腱）：指根 + 中节的弯曲和旁边手指差 45° 以上就罚——不然会有一根手指单独弯下去 / 翘出来。
            // 罚得轻：能碰到表面时贴着比跟旁边一致重要（不然手指会悬空 2–3cm）
            // 食指有自己的伸肌、能单独动得多：它和中指之间放宽到 70°
            if (neighborBends is not null)
                for (var ni = 0; ni < neighborBends.Count; ni++)
                {
                    var indexPair = finger.Id.StartsWith("finger_hito", StringComparison.Ordinal)
                                    || (neighbors is not null && ni < neighbors.Count && neighbors[ni].Id.StartsWith("finger_hito", StringComparison.Ordinal));
                    var over = MathF.Max(0f, MathF.Abs(th[0] + th[2] - neighborBends[ni]) - (indexPair ? 70f : 45f));
                    cost += 1e-6f * over * over;
                }
            // 手指互相穿插（DexGraspNet 的 E_spen）：手指当半径 7.5mm 的胶囊，和相邻手指的距离不足 15mm 就罚
            if (neighbors is not null)
            {
                var mine = FingerCapsule(trial, finger);
                foreach (var other in neighbors)
                {
                    var theirs = FingerCapsule(trial, other);
                    for (var a = 0; a + 1 < mine.Length; a++)
                    for (var b = 0; b + 1 < theirs.Length; b++)
                    {
                        var dist = SegmentDistance(mine[a], mine[a + 1], theirs[b], theirs[b + 1]);
                        if (dist < 0.015f) cost += (0.015f - dist) * (0.015f - dist) * 60f;
                    }
                }
            }
            return cost;
        }
        var best = (float[])init.Clone();
        var bestCost = Cost(best);
        var start = bestCost;
        foreach (var step in new[] { 8f, 4f, 2f, 1f })
            for (var round = 0; round < 4; round++)
            {
                var improved = false;
                for (var i = 0; i < 4; i++)
                    foreach (var dir in new[] { 1f, -1f })
                    {
                        var trial = (float[])best.Clone();
                        trial[i] = Math.Clamp(trial[i] + dir * step, bounds[i].Lo, bounds[i].Hi);
                        if (trial[i] == best[i]) continue;
                        var c = Cost(trial);
                        if (c < bestCost - 1e-9f) (best, bestCost, improved) = (trial, c, true);
                    }
                if (!improved) break;
            }
        Set(pose, best);
        Trace?.Invoke($"  refine {finger.Id}: cost {start * 1e6:0.0} -> {bestCost * 1e6:0.0} angles {string.Join("/", best.Select(a => a.ToString("0")))}");
        if (Trace is not null)
        {
            var check = pose.Clone();
            Set(check, best);
            var pts = shape.Skin(check, rig, allArr);
            var worst = (Depth: 0f, Seg: -1, Bone: "", Actor: 0);
            for (var i = 0; i < pts.Length; i++)
                if (surface.Closest(pts[i], 0.03f) is { } c && -c.Signed > worst.Depth) worst = (-c.Signed, allSeg[i], c.Bone, c.Actor);
            Trace($"    worst penetration {worst.Depth * 1000:0}mm at segment {worst.Seg} into {worst.Bone}");
        }
    }

    /// <summary>手指的骨架折线：各节关节 + 估计的指尖（末节方向再伸出 0.75 倍末节长）。</summary>
    private static Vector3[] FingerCapsule(EditPose pose, Node finger)
    {
        var points = finger.Chain.Where(pose.Has).Select(pose.Position).ToList();
        if (points.Count >= 2) points.Add(points[^1] + (points[^1] - points[^2]) * 0.75f);
        return [.. points];
    }

    /// <summary>两条线段之间的最短距离。</summary>
    private static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-10f && e <= 1e-10f) return r.Length();
        if (a <= 1e-10f) { s = 0; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-10f) { t = 0; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                var b = Vector3.Dot(d1, d2);
                var denom = a * e - b * b;
                s = denom > 1e-10f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        return Vector3.Distance(p1 + d1 * s, p2 + d2 * t);
    }

    /// <summary>手指上（含各节）抽样的顶点，量穿模用。</summary>
    private static int[] FingerSample(EditPose pose, HandShape shape, Node finger)
    {
        var verts = shape.Under(pose, finger.Chain[0]);
        return verts.Length > 48 ? Enumerable.Range(0, 48).Select(i => verts[i * verts.Length / 48]).ToArray() : verts;
    }

    /// <summary>手指各节相对放松零度绕局部 −z 弯了多少度。</summary>
    private static float[] FingerAngles(EditPose pose, Quaternion[] rest, Node finger, int m)
    {
        var result = new float[m];
        for (var i = 0; i < m && i < rest.Length; i++)
        {
            var q = Quaternion.Normalize(Quaternion.Conjugate(rest[i]) * pose.LocalRotation(finger.Chain[i]));
            if (q.W < 0) q = -q;
            result[i] = 2 * MathF.Atan2(-q.Z, q.W) * 180f / MathF.PI;
        }
        return result;
    }

    /// <summary>
    /// 四指协调：每根手指各自合拢以后，相邻手指的合拢程度（各节相对放松的平均比例）相差不超过 0.3。
    /// 真人的手指是连着动的（中指、无名指、小指共用肌腱），一根弯到底、旁边一根直挺挺的样子很假。
    /// 差太多时：弯得少的那根如果是悬空的就跟着多弯（弯到碰到为止）；跟不上（会钻进去）就让弯得多的那根放松一点（放松后不能钻进去）。
    /// </summary>
    private static void Harmonize(EditPose pose, PoseRig rig, Dictionary<string, Quaternion[]> rests, List<Node> four, HandShape shape, Surface surface)
    {
        if (four.Count < 2) return;
        const float maxGap = 0.3f;
        var ranges = four.Select(FingerRanges).ToArray();
        var m = four.Select((f, i) => Math.Min(f.Chain.Length, ranges[i].Length)).ToArray();
        var samples = four.Select(f => FingerSample(pose, shape, f)).ToArray();
        float Closure(int i)
        {
            var a = FingerAngles(pose, rests[four[i].Id], four[i], m[i]);
            var sum = 0f;
            for (var k = 0; k < m[i]; k++) sum += (a[k] - ranges[i][k].Base) / ranges[i][k].Range;
            return sum / m[i];
        }
        float Excess(int i)
        {
            var e = 0f;
            var first = shape.Under(pose, four[i].Chain[0], exact: true).ToHashSet();
            var points = shape.Skin(pose, rig, samples[i]);
            for (var j = 0; j < points.Length; j++)
                if (surface.Closest(points[j], FingerReach) is { } c)
                    e = MathF.Max(e, (first.Contains(samples[i][j]) ? Inside - PadSquash : Inside) - c.Signed);
            return e;
        }
        // 把第 i 根手指的合拢程度改成 target：各节按原来的比例缩放（悬空、各节一样时就是均匀地弯）
        void SetClosure(int i, float target)
        {
            var a = FingerAngles(pose, rests[four[i].Id], four[i], m[i]);
            var now = Closure(i);
            var result = new float[m[i]];
            for (var k = 0; k < m[i]; k++)
            {
                var r = ranges[i][k];
                var part = now > 0.02f ? (a[k] - r.Base) / r.Range / now : 1f;
                result[k] = r.Base + Math.Clamp(target * part, -0.2f, 1f) * r.Range;
            }
            SetFinger(pose, rests[four[i].Id], four[i], result);
        }
        for (var pass = 0; pass < 3; pass++)
        {
            var changed = false;
            for (var i = 0; i + 1 < four.Count; i++)
            {
                var (hi, lo) = Closure(i) > Closure(i + 1) ? (i, i + 1) : (i + 1, i);
                var gap = Closure(hi) - Closure(lo);
                if (gap <= maxGap + 0.01f) continue;
                // 先试着让弯得少的那根跟上（一点点弯，钻进去了就停在上一步）
                var loStart = Closure(lo);
                var goal = Closure(hi) - maxGap;
                var reached = loStart;
                var loBackup = four[lo].Chain.Select(pose.LocalRotation).ToArray();
                var baseExcess = Excess(lo);
                for (var s = 1; s <= 8; s++)
                {
                    var c = loStart + (goal - loStart) * s / 8f;
                    var before = four[lo].Chain.Select(pose.LocalRotation).ToArray();
                    SetClosure(lo, c);
                    if (Excess(lo) > MathF.Max(0.001f, baseExcess + 0.001f))
                    {
                        for (var k = 0; k < before.Length; k++) pose.SetRotation(four[lo].Chain[k], pose.Rotation(pose.Parent(four[lo].Chain[k])!) * before[k]);
                        break;
                    }
                    reached = c;
                }
                if (reached < loStart + 0.01f)
                    for (var k = 0; k < loBackup.Length; k++) pose.SetRotation(four[lo].Chain[k], pose.Rotation(pose.Parent(four[lo].Chain[k])!) * loBackup[k]);
                // 还差得多：弯得多的那根放松一点（放松后不能钻进去）
                if (Closure(hi) - Closure(lo) > maxGap + 0.01f)
                {
                    var hiBackup = four[hi].Chain.Select(pose.LocalRotation).ToArray();
                    var hiExcess = Excess(hi);
                    SetClosure(hi, Closure(lo) + maxGap);
                    if (Excess(hi) > MathF.Max(0.001f, hiExcess + 0.001f))
                        for (var k = 0; k < hiBackup.Length; k++) pose.SetRotation(four[hi].Chain[k], pose.Rotation(pose.Parent(four[hi].Chain[k])!) * hiBackup[k]);
                }
                changed = true;
            }
            if (!changed) break;
        }
        Trace?.Invoke($"  harmonize: {string.Join(" ", Enumerable.Range(0, four.Count).Select(i => $"{Closure(i):0.00}"))}");
    }

    /// <summary>
    /// 手指侧面碰到旁边的东西（比如摸胸口时食指戳到上臂），光伸直、弯曲退不出来：绕掌心法线往左或往右偏一点（最多 12°），挑钻得最浅的。
    /// 只偏一点，不会像以前那样把手指甩到侧面去。
    /// </summary>
    private static void Sidestep(EditPose pose, PoseRig rig, Node finger, HandShape shape, Surface surface, int[] vertices)
    {
        float Depth(EditPose p)
        {
            var depth = 0f;
            foreach (var point in shape.Skin(p, rig, vertices))
                if (surface.Closest(point, FingerReach) is { } c) depth = MathF.Max(depth, Inside - c.Signed);
            return depth;
        }
        var now = Depth(pose);
        if (now <= 0) return;
        var root = finger.Chain[0];
        var axis = PalmNormal(pose, finger.Side);
        var best = 0f;
        var bestDepth = now;
        foreach (var degrees in new[] { -4f, 4f, -8f, 8f, -12f, 12f })
        {
            var trial = pose.Clone();
            trial.RotateSubtree(root, trial.Position(root), Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f));
            var d = Depth(trial) + MathF.Abs(degrees) * 0.0001f;
            if (d < bestDepth) (bestDepth, best) = (d, degrees);
        }
        if (best != 0) pose.RotateSubtree(root, pose.Position(root), Quaternion.CreateFromAxisAngle(axis, best * MathF.PI / 180f));
    }

    /// <summary>
    /// 拇指根在手掌侧面，绑定姿势时斜着朝掌心那边：手掌平贴上去时拇指会插进身体，光伸直退不出来。
    /// 真人这时把拇指张开、贴着表面放——按真实网格找钻得最深的点，把拇指根往表面外侧转，直到不钻进去（最多转 60°）。
    /// </summary>
    private static void OpenThumb(EditPose pose, PoseRig rig, Node thumb, HandShape shape, Surface surface, int[] vertices)
    {
        var root = thumb.Chain[0];
        var turned = 0f;
        const float max = 60f * MathF.PI / 180f;
        for (var i = 0; i < 10 && turned < max; i++)
        {
            var points = shape.Skin(pose, rig, vertices);
            Vector3? deepest = null;
            var depth = 0f;
            var normal = Vector3.Zero;
            foreach (var p in points)
                if (surface.Closest(p, FingerReach) is { } c && Inside - c.Signed > depth)
                    (deepest, depth, normal) = (p, Inside - c.Signed, c.Normal);
            if (deepest is not { } point) break;
            var pivot = pose.Position(root);
            var from = point - pivot;
            if (from.LengthSquared() < 1e-8f) break;
            var swing = PoseIk.Swing(from, point + normal * (depth + 0.001f) - pivot);
            var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(swing.W), 0f, 1f));
            if (turned + angle > max) swing = Quaternion.Slerp(Quaternion.Identity, swing, (max - turned) / angle);
            // 拇指根总额度用完就停在额度边上（二分找能转多少）
            var test = pose.Clone();
            test.RotateSubtree(root, pivot, swing);
            if (ThumbRoom(test, rig, thumb) <= 0)
            {
                float lo = 0, hi = 1;
                for (var k = 0; k < 8; k++)
                {
                    var mid = (lo + hi) / 2;
                    var probe = pose.Clone();
                    probe.RotateSubtree(root, pivot, Quaternion.Slerp(Quaternion.Identity, swing, mid));
                    if (ThumbRoom(probe, rig, thumb) > 0) lo = mid; else hi = mid;
                }
                if (lo > 0) pose.RotateSubtree(root, pivot, Quaternion.Slerp(Quaternion.Identity, swing, lo));
                break;
            }
            pose.RotateSubtree(root, pivot, swing);
            turned += angle;
        }
    }

    // ------------------------------------------------------------------ 没有手的网格时：按骨骼估计

    /// <summary>调试：每一步之后掌心那一面贴得怎么样（离线验证工程用，游戏里为 null）。</summary>
    public static Action<string>? Trace;

    /// <summary>手指 4 自由度贴合优化开关（离线对比用）。</summary>
    public static bool RefineFingers = true;

    [ThreadStatic] private static System.Diagnostics.Stopwatch? _watch;
    private static void TraceFlush(string step, EditPose pose, PoseRig rig, Node hand, HandShape shape, Surface surface)
    {
        if (Trace is not null)
        {
            _watch ??= System.Diagnostics.Stopwatch.StartNew();
            var turned = _original is { } o
                ? 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(pose.Rotation(hand.Chain[2])), Quaternion.Normalize(o.Rotation(hand.Chain[2])))), 0f, 1f)) * 180f / MathF.PI
                : 0f;
            Trace($"  [{_watch.Elapsed.TotalMilliseconds:0}ms] {step} (hand {turned:0} deg from start)");
            _watch.Restart();
        }
        if (Trace is null) return;
        var all = shape.Palm(pose);
        var center = PalmCenter(pose, hand.Side, rig)!.Value;
        var n = PalmNormal(pose, hand.Side, MeshPalm ? shape : null, rig);
        var pts = shape.Skin(pose, rig, all);
        var d = new List<float>();
        foreach (var p in pts)
            if (Vector3.Dot(p - center, n) > 0.002f && surface.Closest(p, 0.1f) is { } c) d.Add(c.Signed);
        Trace($"  {step}: n={d.Count} avg={(d.Count > 0 ? d.Average() * 1000 : 0):0}mm min={(d.Count > 0 ? d.Min() * 1000 : 0):0}mm within5={(d.Count > 0 ? d.Count(x => x < 0.005f) * 100 / d.Count : 0)}%");
    }

    private static Contact? ApplySimple(EditPose pose, Node hand, IReadOnlyList<Node> all, IReadOnlyDictionary<string, Vector3> tips, Surface surface, float minBend)
    {
        var contact = SnapPalm(pose, hand, surface, minBend);
        var fingers = all.Where(n => n.Kind == NodeKind.Finger && n.Hand == hand.Id).ToList();
        foreach (var f in fingers) ConformFinger(pose, f, tips, surface, wrap: contact is not null);
        if (contact is not null)
        {
            var four = fingers.Where(f => !f.Id.StartsWith("finger_oya", StringComparison.Ordinal)).ToList();
            Spread(pose, four, tips, PalmNormal(pose, hand.Side));
        }
        return contact;
    }
}
