using System.Numerics;

namespace Posegraph.Editor;

/// <summary>控制点怎么动：拖动（自由拖 / G）时的行为。</summary>
public enum NodeKind
{
    Pelvis,   // 整个人平移（脚钉住时腿自动弯）
    Aim,      // 骨骼链转向目标（腰、胸、头、肩、脚尖）
    Limb,     // 两段 IK（手、脚）
    Pole,     // 肘 / 膝朝向（胳膊 / 腿绕根→末端连线转）
    Finger,   // 手指 CCD
    Gaze,     // 视线
}

/// <summary>
/// 一个控制点。Chain：Aim / Finger 是要转的骨骼链（Weights 是分配比例），Limb / Pole 是 [根, 中, 末端]；
/// Rotate / RotateWeights：R 键旋转时转哪几节；Bone：点画在哪根骨骼上，给了 TipFrom 时是骨骼前方的虚拟点（跟着 Bone 一起转）。
/// </summary>
public sealed record Node(string Id, string Label, NodeKind Kind, string Side, string Bone, string[] Chain, float[] Weights,
                          string[] Rotate, float[] RotateWeights, string? TipFrom = null, float TipLength = 0f)
{
    /// <summary>手指：属于哪只手（显示手指时用）。</summary>
    public string? Hand { get; init; }

    /// <summary>左右镜像、对称编辑时同步的那一组骨骼的根和参考骨骼（胳膊相对胸，腿相对骨盆）。</summary>
    public (string Root, string Reference)? MirrorGroup => Side switch
    {
        "" => null,
        _ when Kind == NodeKind.Gaze => null,
        _ when Bone.StartsWith("j_asi", StringComparison.Ordinal) => ($"j_asi_a_{Side}", "j_kosi"),
        _ => ($"j_sako_{Side}", "j_sebo_c"),
    };
}

/// <summary>控制点表和每个控制点的拖动 / 旋转。都是纯数学，离线可测。</summary>
public static class PoseNodes
{
    public const string Root = "n_hara";
    public const string Head = "j_kao";
    public static readonly string[] Eyes = ["j_f_eye_l", "j_f_eye_r"];

    private static readonly (string Key, string Label)[] Fingers =
        [("oya", "拇指"), ("hito", "食指"), ("naka", "中指"), ("kusu", "无名指"), ("ko", "小指")];

    /// <summary>这副骨架上能用的控制点。</summary>
    public static List<Node> Build(EditPose pose)
    {
        var nodes = new List<Node>
        {
            new("pelvis", "骨盆", NodeKind.Pelvis, "", "j_kosi", [], [], [Root], [1f]),
            new("waist", "腰", NodeKind.Aim, "", "j_sebo_c", ["j_sebo_a", "j_sebo_b"], [0.5f, 0.5f], ["j_sebo_a", "j_sebo_b"], [0.5f, 0.5f]),
            new("chest", "胸", NodeKind.Aim, "", "j_kubi", ["j_sebo_c"], [1f], ["j_sebo_c"], [1f]),
            new("head", "头", NodeKind.Aim, "", Head, ["j_kubi", Head], [0.4f, 0.6f], ["j_kubi", Head], [0.4f, 0.6f], "j_kubi", 0.14f),
        };
        foreach (var (s, side) in new[] { ("l", "左"), ("r", "右") })
        {
            nodes.Add(new($"shoulder_{s}", side + "肩", NodeKind.Aim, s, $"j_ude_a_{s}", [$"j_sako_{s}"], [1f], [$"j_sako_{s}"], [1f]));
            nodes.Add(new($"elbow_{s}", side + "肘", NodeKind.Pole, s, $"j_ude_b_{s}", [$"j_ude_a_{s}", $"j_ude_b_{s}", $"j_te_{s}"], [],
                          [$"j_ude_b_{s}"], [1f]));
            nodes.Add(new($"hand_{s}", side + "手", NodeKind.Limb, s, $"j_te_{s}", [$"j_ude_a_{s}", $"j_ude_b_{s}", $"j_te_{s}"], [],
                          [$"j_te_{s}"], [1f]));
            nodes.Add(new($"knee_{s}", side + "膝", NodeKind.Pole, s, $"j_asi_c_{s}", [$"j_asi_a_{s}", $"j_asi_c_{s}", $"j_asi_d_{s}"], [],
                          [$"j_asi_c_{s}"], [1f]));
            nodes.Add(new($"foot_{s}", side + "脚", NodeKind.Limb, s, $"j_asi_d_{s}", [$"j_asi_a_{s}", $"j_asi_c_{s}", $"j_asi_d_{s}"], [],
                          [$"j_asi_d_{s}"], [1f]));
            nodes.Add(new($"toe_{s}", side + "脚尖", NodeKind.Aim, s, $"j_asi_e_{s}", [$"j_asi_d_{s}"], [1f], [$"j_asi_e_{s}"], [1f]));
            foreach (var (key, label) in Fingers)
            {
                string a = $"j_{key}_a_{s}", b = $"j_{key}_b_{s}", c = $"iv_{key}_c_{s}";
                if (!pose.Has(a) || !pose.Has(b)) continue;
                string[] chain = pose.Has(c) ? [a, b, c] : [a, b];
                // 指尖：最后一节往前延长（第三节有时只有 IVCS 骨架才有；没有时第二节就是末节）。
                // 拇指只有两根骨骼：j_oya_a 在手腕根部，到 j_oya_b 约 7.5cm（掌骨 + 第一节），末节只有它的 0.4 倍左右
                var tipLength = -(pose.Has(c) ? 0.9f : key == "oya" ? 0.4f : 0.8f);
                nodes.Add(new($"finger_{key}_{s}", side + label, NodeKind.Finger, s, chain[^1], chain, [], [a], [1f], chain[^2], tipLength)
                          { Hand = $"hand_{s}" });
            }
        }
        if (Eyes.All(pose.Has))
            nodes.Add(new("gaze", "视线", NodeKind.Gaze, "", Eyes[0], [.. Eyes], [], [], []));
        return nodes.Where(n => pose.Has(n.Bone) && n.Chain.All(pose.Has) && n.Rotate.All(pose.Has)).ToList();
    }

    /// <summary>
    /// 虚拟指尖 / 头顶点在骨骼局部坐标里的偏移（进入编辑时量一次，之后跟着骨骼刚性转动）。
    /// TipLength 为负时是相对长度（TipFrom → Bone 这一节的倍数）。
    /// </summary>
    public static Dictionary<string, Vector3> Tips(EditPose pose, IEnumerable<Node> nodes)
    {
        var tips = new Dictionary<string, Vector3>();
        foreach (var n in nodes)
        {
            if (n.TipFrom is null) continue;
            var dir = pose.Position(n.Bone) - pose.Position(n.TipFrom);
            if (dir.LengthSquared() < 1e-12f) continue;
            var length = n.TipLength < 0 ? -n.TipLength * dir.Length() : n.TipLength;
            // 手指：沿末节自己的方向延长（游戏骨骼的局部 +x 都指向下一节；手指弯着时末节和上一节不在一条线上）
            tips[n.Id] = n.Kind == NodeKind.Finger
                ? Vector3.UnitX * length
                : Vector3.Transform(Vector3.Normalize(dir) * length, Quaternion.Conjugate(pose.Rotation(n.Bone)));
        }
        return tips;
    }

    /// <summary>控制点现在在哪（模型空间）。视线点由编辑器自己记（gaze）。</summary>
    public static Vector3 Point(EditPose pose, Node n, IReadOnlyDictionary<string, Vector3> tips, Vector3? gaze = null)
    {
        if (n.Kind == NodeKind.Gaze && gaze is { } g) return g;
        var p = pose.Position(n.Bone);
        return tips.TryGetValue(n.Id, out var local) ? p + Vector3.Transform(local, pose.Rotation(n.Bone)) : p;
    }

    /// <summary>
    /// 编辑选项。Surface：手部吸附开着时的身体表面（不吸附为 null）；Contacts：解算时记下每只手贴着的表面（钉在表面上用）。
    /// </summary>
    public sealed record Options(bool Clamp, bool Symmetric, Surface? Surface = null, Dictionary<string, Contact>? Contacts = null,
                                 IReadOnlyDictionary<string, HandShape>? Hands = null, bool Quick = false, float Fit = 1f, Surface? Region = null,
                                 Vector3? Focus = null);

    /// <summary>
    /// 钉住的手 / 脚：末端的位置和朝向。Bone 为空时是模型空间里的固定位置；不为空时是相对这根骨骼的（钉在自己身体表面上，身体动了手跟着走）。
    /// </summary>
    public readonly record struct Pin(Vector3 Position, Quaternion Rotation, string? Bone = null)
    {
        public (Vector3 Position, Quaternion Rotation) Resolve(EditPose pose)
            => Bone is { } b && pose.Has(b)
                ? (pose.Position(b) + Vector3.Transform(Position, pose.Rotation(b)), Quaternion.Normalize(pose.Rotation(b) * Rotation))
                : (Position, Rotation);
    }

    /// <summary>
    /// 一次操作：从 start 出发，拖动（target 为控制点的新位置）或旋转（rotation 为模型空间里的转动），
    /// 然后限位、对称、把钉住的手脚拉回原处。返回新的姿势。
    /// </summary>
    public static EditPose Solve(EditPose start, PoseRig rig, Node node, IReadOnlyDictionary<string, Vector3> tips, Options options,
                                 IReadOnlyDictionary<string, Pin> pins, IReadOnlyList<Node> all, Vector3? target = null, Quaternion? rotation = null)
    {
        var pose = start.Clone();
        var clampOn = options.Clamp;
        if (target is { } t) Drag(pose, rig, node, tips, t, clampOn);
        else if (rotation is { } q) PoseOps.RotateChain(pose, node.Rotate, node.RotateWeights, q);

        // 手部吸附：拖手时手掌贴表面、手指顺着弯；拖手指时只把钻进表面的退出来
        var snapped = false;
        if (options.Surface is { } surface)
        {
            if (node.Kind == NodeKind.Limb && node.Bone.StartsWith("j_te", StringComparison.Ordinal) && target is not null)
            {
                var contact = HandContact.Apply(pose, rig, node, all, tips, surface, options.Hands?.GetValueOrDefault(node.Side), MinBend(node, clampOn), options.Quick,
                                                  options.Hands?.GetValueOrDefault("arm_" + node.Side), options.Fit, options.Region, options.Focus, start);
                if (options.Contacts is { } contacts)
                {
                    if (contact is { } c) contacts[node.Id] = c;
                    else contacts.Remove(node.Id);
                }
                snapped = contact is not null;
            }
            else if (node.Kind == NodeKind.Finger)
                HandContact.ConformFinger(pose, node, tips, surface, wrap: false);
        }

        // 手贴上表面以后不再做一遍「限制在正常范围内」：吸附自己按关节范围解（手腕、上臂扭转、肘都是硬约束），
        // 事后再把某个关节拉回去，整条胳膊跟着动，贴好的手会被从目标上拉开十几厘米（游戏里「根本没摸上去」就是这么来的）
        if (clampOn && !snapped)
        {
            var changed = new HashSet<string>();
            pose.CollectChanged(start, changed);
            PoseOps.Clamp(pose, rig, changed);
        }
        if (options.Symmetric && node.MirrorGroup is { } group)
            PoseOps.Mirror(pose, rig, pose.SubtreeOrdered(group.Root), group.Reference);

        // 钉住的手脚拉回原处。正在操作的就是这只手 / 脚（或者它的脚尖、手指、朝向）时不管它；对称编辑时对侧也被带动了，但钉住优先
        var operated = new HashSet<string>(node.Chain.Concat(node.Rotate));
        foreach (var (id, pin) in pins)
        {
            if (id == node.Id || all.FirstOrDefault(n => n.Id == id) is not { Kind: NodeKind.Limb } limb) continue;
            var end = limb.Chain[2];
            if (operated.Contains(end) || (node.Kind == NodeKind.Pole && node.Chain[2] == end)) continue;
            var (pinPosition, pinRotation) = pin.Resolve(pose);
            var moved = Vector3.DistanceSquared(pose.Position(end), pinPosition) >= 1e-8f;
            var turned = MathF.Abs(Quaternion.Dot(pose.Rotation(end), pinRotation)) <= 0.9999999f;
            // 只是朝向变了（位置还在）：只把末端转回去，不重解整条腿——重解会让膝盖每次都偏一点
            if (moved) PoseIk.SolveLimb(pose, limb.Chain[0], limb.Chain[1], end, pinPosition, pinRotation, MinBend(limb, clampOn));
            else if (turned) pose.SetRotation(end, pinRotation);
        }
        // 肘、腕、肩的辅助骨跟着主骨（不然手腕扭大了会拧成糖纸、肘窝凹进去）
        PoseOps.FollowHelpers(start, pose, rig);
        return pose;
    }

    /// <summary>镜像：把 node 这一侧（整条胳膊含手指，或整条腿）的姿势复制到另一侧。</summary>
    public static EditPose MirrorSide(EditPose start, PoseRig rig, Node node)
    {
        var pose = start.Clone();
        if (node.MirrorGroup is { } group) PoseOps.Mirror(pose, rig, pose.SubtreeOrdered(group.Root), group.Reference);
        PoseOps.FollowHelpers(start, pose, rig);
        return pose;
    }

    private static float MinBend(Node limb, bool clamp)
        // 限位开着时肘最多弯到 135°（游戏动画常见最大值），膝 150°；关着时都留 30° 夹角，不会折成一团
        => clamp && limb.Bone.StartsWith("j_te", StringComparison.Ordinal) ? 45f * MathF.PI / 180f : PoseIk.MinBend;

    private static void Drag(EditPose pose, PoseRig rig, Node n, IReadOnlyDictionary<string, Vector3> tips, Vector3 target, bool clamp)
    {
        switch (n.Kind)
        {
            case NodeKind.Pelvis:
                pose.TranslateSubtree(Root, target - pose.Position(n.Bone));
                break;
            case NodeKind.Aim:
                PoseOps.AimChain(pose, n.Chain, n.Weights, p => Point(p, n, tips), target);
                break;
            case NodeKind.Limb:
                PoseIk.SolveLimb(pose, n.Chain[0], n.Chain[1], n.Chain[2], target, null, MinBend(n, clamp));
                break;
            case NodeKind.Pole:
                PoseIk.SwivelLimb(pose, n.Chain[0], n.Chain[1], n.Chain[2], target);
                break;
            case NodeKind.Finger:
                PoseOps.CurlFinger(pose, n.Chain, p => Point(p, n, tips), target);
                break;
            case NodeKind.Gaze:
                PoseOps.Gaze(pose, rig, n.Chain, Head, target);
                break;
        }
    }
}
