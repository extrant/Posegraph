using System.Numerics;

namespace Posegraph.Editor;

/// <summary>
/// 一只手的形状：角色手套 / 手模型里主要跟这只手（手腕和手指）动的顶点，按姿势蒙皮后用来量手贴着表面、钻进去多少——
/// 比拿骨骼加估计的厚度准（掌根、拇指根、指肚的实际形状都算进去）。顶点太多时均匀抽一部分。
/// </summary>
public sealed class HandShape
{
    private readonly BodyMesh.Part _part;
    private readonly int[] _vertices;          // 选中的顶点（Part 里的下标）
    private readonly string[] _bone;           // 每个选中顶点主要跟的骨骼
    private readonly Vector3[] _buffer;
    private readonly Dictionary<string, int[]> _under = [];

    public string Side { get; }

    private HandShape(BodyMesh.Part part, int[] vertices, string[] bone, string side)
        => (_part, _vertices, _bone, _buffer, Side) = (part, vertices, bone, new Vector3[vertices.Length], side);

    public int Count => _vertices.Length;
    /// <summary>第 i 个顶点主要跟的骨骼（调试用）。</summary>
    public string BoneOf(int i) => _bone[i];
    /// <summary>取自哪个模型文件（调试用）。</summary>
    public string PartPath => _part.Path;

    /// <summary>从角色的模型里找这只手：主要跟 j_te_{side} 及其子骨骼的顶点最多的那个模型。没有时 null。</summary>
    public static HandShape? From(IEnumerable<BodyMesh.Part> parts, EditPose pose, string side, int max = 500)
        => FromBones(parts, pose.Subtree($"j_te_{side}"), side, max);

    /// <summary>这条胳膊（上臂、前臂和它们的辅助骨，不含手）的形状：用来量胳膊有没有钻进身体。</summary>
    /// 看得见的是最外面那层：有上衣（_top.mdl）就用上衣量（比皮肤厚，用皮肤量会漏掉袖子压进身体的情况），没有才用顶点最多的那件。
    public static HandShape? Arm(IEnumerable<BodyMesh.Part> parts, EditPose pose, string side, int max = 600)
    {
        var bones = new[] { $"j_ude_a_{side}", $"j_ude_b_{side}", $"n_hhiji_{side}", $"n_hijisoubi_{side}", $"n_hte_{side}" }.Where(pose.Has).ToHashSet();
        var list = parts.ToList();
        var top = list.Where(p => p.Path.EndsWith("_top.mdl", StringComparison.OrdinalIgnoreCase)).ToList();
        return (top.Count > 0 ? FromBones(top, bones, side, max) : null) ?? FromBones(list, bones, side, max);
    }

    private static HandShape? FromBones(IEnumerable<BodyMesh.Part> parts, HashSet<string> hand, string side, int max)
    {
        if (hand.Count == 0) return null;
        HandShape? best = null;
        foreach (var part in parts)
        {
            var list = new List<int>();
            for (var v = 0; v < part.Positions.Length; v++)
                if (hand.Contains(BodyMesh.DominantBone(part, v))) list.Add(v);
            if (list.Count < 30 || (best is not null && list.Count <= best.Count)) continue;
            // 只要三角形里用到的顶点（有的模型带着不画的辅助顶点）
            var used = new HashSet<int>(part.Triangles);
            list = list.Where(used.Contains).ToList();
            if (list.Count > max) list = Thin(part, list, max);
            best = new HandShape(part, [.. list], [.. list.Select(v => BodyMesh.DominantBone(part, v))], side);
        }
        return best;
    }

    /// <summary>
    /// 均匀抽稀到 max 个以内：每个小立方体格子（按模型里的原始位置）留一个顶点，格子从 3mm 起逐步放大。
    /// 以前按顶点在文件里的顺序隔几个取一个，顺序和位置没关系，抽出来疏密不均——同一个模型左手掌心剩 95 个点、右手只剩 33 个，
    /// 贴合拟合的效果左右差一大截
    /// </summary>
    private static List<int> Thin(BodyMesh.Part part, List<int> list, int max)
    {
        for (var cell = 0.003f; ; cell *= 1.2f)
        {
            var seen = new HashSet<(int, int, int)>();
            var kept = new List<int>();
            foreach (var v in list)
            {
                var p = part.Positions[v] / cell;
                if (seen.Add(((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z)))) kept.Add(v);
            }
            if (kept.Count <= max) return kept;
        }
    }

    /// <summary>按姿势蒙皮（返回的数组每次复用，别留着）。</summary>
    public Vector3[] Skin(EditPose pose, PoseRig rig)
    {
        BodyMesh.SkinVertices(_part, _vertices, pose, rig, _buffer);
        return _buffer;
    }

    /// <summary>只蒙皮其中一部分（subset 是 Skin 结果里的下标），结果按 subset 的顺序。</summary>
    public Vector3[] Skin(EditPose pose, PoseRig rig, int[] subset)
    {
        var picked = subset.Select(i => _vertices[i]).ToArray();
        var result = new Vector3[picked.Length];
        BodyMesh.SkinVertices(_part, picked, pose, rig, result);
        return result;
    }

    private Vector3? _palmAxis;

    /// <summary>
    /// 掌心实际朝向（手腕 j_te 局部坐标，算一次缓存）：手掌皮肤顶点法线里朝掌心那一侧的平均。
    /// 中指根骨骼的 −y（以前当掌心朝向用的）和手掌皮肤真正的朝向差 27–34°，按它把「掌心对着表面」，手一开始就斜着，
    /// 后面的倾斜拟合（最多 25°）补不完，凸出的拇指根肉垫就压进身体。
    /// </summary>
    /// 按模型文件里的绑定姿势算（顶点、法线本来就是绑定姿势的），和当前姿势无关：以前按当前姿势蒙皮后的法线算，
    /// 指根附近的顶点带着手指的权重、C+ 的非均匀缩放也会把法线拧歪，第一次算的时候手指是什么样，之后一直用那个值（实测一个点选因此偏 18cm）。
    public Vector3? PalmAxis(EditPose pose, PoseRig rig)
    {
        if (_palmAxis is { } cached) return cached;
        string wrist = $"j_te_{Side}", naka = $"j_naka_a_{Side}";
        if (!pose.Has(wrist) || !pose.Has(naka) || !rig.HasBind(wrist) || !rig.HasBind(naka)) return null;
        var palm = Palm(pose);
        if (palm.Length < 10) return null;
        var normals = palm.Select(i => _part.Normals[_vertices[i]]).Where(n => n.LengthSquared() > 1e-8f).Select(Vector3.Normalize).ToArray();
        var axis = Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, rig.Bind(naka)));
        foreach (var threshold in new[] { 0.3f, 0.6f, 0.6f })
        {
            var sum = Vector3.Zero;
            foreach (var n in normals)
                if (Vector3.Dot(n, axis) > threshold) sum += n;
            if (sum.LengthSquared() < 1e-8f) return null;
            axis = Vector3.Normalize(sum);
        }
        _palmAxis = Vector3.Normalize(Vector3.Transform(axis, Quaternion.Conjugate(rig.Bind(wrist))));
        return _palmAxis;
    }

    /// <summary>只蒙皮其中一部分，连同顶点法线（单位向量）。</summary>
    public (Vector3[] Positions, Vector3[] Normals) SkinWithNormals(EditPose pose, PoseRig rig, int[] subset)
    {
        var picked = subset.Select(i => _vertices[i]).ToArray();
        var positions = new Vector3[picked.Length];
        var normals = new Vector3[picked.Length];
        BodyMesh.SkinVertices(_part, picked, pose, rig, positions, normals);
        return (positions, normals);
    }

    /// <summary>主要跟 bone 这根骨骼（exact 为 false 时含它的子骨骼）动的顶点（Skin 结果里的下标）。</summary>
    public int[] Under(EditPose pose, string bone, bool exact = false)
    {
        var key = (exact ? "=" : "") + bone;
        if (_under.TryGetValue(key, out var cached)) return cached;
        var set = exact ? new HashSet<string> { bone } : pose.Subtree(bone);
        var list = Enumerable.Range(0, _bone.Length).Where(i => set.Contains(_bone[i])).ToArray();
        _under[key] = list;
        return list;
    }

    /// <summary>手掌（不算手指）的顶点。</summary>
    public int[] Palm(EditPose pose)
    {
        if (_under.TryGetValue("#palm", out var cached)) return cached;
        var fingers = new HashSet<string>(new[] { "oya", "hito", "naka", "kusu", "ko" }
            .Select(f => $"j_{f}_a_{Side}").Where(pose.Has).SelectMany(pose.Subtree));
        var list = Enumerable.Range(0, _bone.Length).Where(i => !fingers.Contains(_bone[i])).ToArray();
        _under["#palm"] = list;
        return list;
    }
}
