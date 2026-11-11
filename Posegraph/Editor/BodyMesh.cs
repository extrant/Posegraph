using System.Numerics;

namespace Posegraph.Editor;

/// <summary>
/// 角色身体表面的网格（手部吸附用）。做法参考 SkinTattoo（Apache-2.0）的 Mesh 部分：
/// 从游戏里角色每个模型的资源句柄拿到实际加载的 .mdl 路径（Penumbra 换过的是磁盘路径，带 |…| 前缀），解析出顶点和三角形；
/// 子网格按模型当前启用的属性过滤（被衣服挡住、游戏不画的那部分身体不要）。模型文件用自己的 MdlReader 读（Lumina 读不了 7.x 的模型）。
/// 和 SkinTattoo 不同的是这里要的是摆好姿势的表面：按每个顶点的骨骼权重做一遍 CPU 蒙皮（顶点 × 逆绑定 × 当前骨骼变换）。
/// 不处理种族变形（穿别的种族的装备时差一两厘米）。
/// </summary>
public static partial class BodyMesh
{
    private const int K = MdlReader.Influences;

    /// <summary>一个模型（最高 LOD 的主网格、只留看得见的三角形）。BoneOf / WeightOf：每个顶点 K 个（Bones 里的下标、权重）。</summary>
    public sealed record Part(string Path, Vector3[] Positions, Vector3[] Normals, int[] BoneOf, float[] WeightOf, string[] Bones, int[] Triangles);

    /// <summary>解析一个模型文件（原始字节）；attributes：模型当前启用的属性。</summary>
    public static Part Parse(string path, byte[] data, uint attributes)
    {
        var mdl = MdlReader.Read(data, attributes);
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var boneOf = new List<int>();
        var weightOf = new List<float>();
        var triangles = new List<int>();
        foreach (var mesh in mdl.Meshes)
        {
            var first = positions.Count;
            positions.AddRange(mesh.Positions);
            normals.AddRange(mesh.Normals);
            boneOf.AddRange(mesh.BoneIndex);
            weightOf.AddRange(mesh.BoneWeight);
            triangles.AddRange(mesh.Triangles.Select(i => i + first));
        }
        return new Part(path, [.. positions], [.. normals], [.. boneOf], [.. weightOf], mdl.Bones, [.. triangles]);
    }

    /// <summary>只蒙皮一部分顶点（手部吸附每帧要算手的形状）。</summary>
    public static void SkinVertices(Part part, IReadOnlyList<int> vertices, EditPose pose, PoseRig rig, Vector3[] into, Vector3[]? normals = null)
    {
        var skin = new Dictionary<int, Matrix4x4?>();
        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            Vector3 p = Vector3.Zero, nn = Vector3.Zero;
            float total = 0;
            for (var k = 0; k < K; k++)
            {
                var w = part.WeightOf[v * K + k];
                if (w <= 0) continue;
                var bone = part.BoneOf[v * K + k];
                if (!skin.TryGetValue(bone, out var m))
                {
                    var name = bone < part.Bones.Length ? part.Bones[bone] : "";
                    m = pose.Has(name) && rig.InverseBind(name, out var inverseBind)
                        ? inverseBind * (Matrix4x4.CreateScale(pose[name].Scale) * Matrix4x4.CreateFromQuaternion(pose[name].Rotation) * Matrix4x4.CreateTranslation(pose[name].Translation))
                        : null;
                    skin[bone] = m;
                }
                if (m is not { } matrix) continue;
                p += Vector3.Transform(part.Positions[v], matrix) * w;
                if (normals is not null) nn += Vector3.TransformNormal(part.Normals[v], matrix) * w;
                total += w;
            }
            into[i] = total > 0 ? p / total : Vector3.Zero;
            if (normals is not null) normals[i] = nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : Vector3.Zero;
        }
    }

    /// <summary>
    /// 低模代理网格（影子 / 占位用，游戏里看不见）：三角形边长的中位数超过 8cm。正常的身体、衣服、头发都在 1–3cm，
    /// 最低模的原版身体也只有 4.5–6.2cm（c0101b0001 的腿是 6.2cm，以前的 6cm 阈值会把它当代理扔掉，别人穿原版身体时腿就摸不到了）。
    /// 比如有的角色身上挂着一个 460 个三角形的 c0101b0003_top（中位数 9.4cm），全是十几厘米的大平面，当成身体表面的话手会被看不见的平面推开。
    /// </summary>
    public static bool IsProxy(Part part)
    {
        var count = part.Triangles.Length / 3;
        if (count == 0) return true;
        var step = Math.Max(1, count / 500);
        var edges = new List<float>();
        for (var t = 0; t < count; t += step)
            edges.Add(Vector3.Distance(part.Positions[part.Triangles[t * 3]], part.Positions[part.Triangles[t * 3 + 1]]));
        edges.Sort();
        return edges[edges.Count / 2] > 0.08f;
    }

    /// <summary>主要跟着哪根骨骼动（权重最大的那根）。</summary>
    public static string DominantBone(Part part, int vertex)
    {
        int best = 0;
        float weight = -1;
        for (var k = 0; k < K; k++)
            if (part.WeightOf[vertex * K + k] > weight)
                (weight, best) = (part.WeightOf[vertex * K + k], part.BoneOf[vertex * K + k]);
        return best < part.Bones.Length ? part.Bones[best] : "";
    }

    /// <summary>
    /// 按姿势蒙皮，换到编辑中角色的模型空间，变成 Surface 的一块。toEditor：这个角色的模型空间 → 编辑中角色的模型空间（自己时为单位矩阵）。
    /// skip：不要的骨骼（主要跟着这些骨骼动的顶点组成的三角形不要，比如正在拖的那只手自己）。
    /// </summary>
    public static Surface.Piece? Skin(Part part, EditPose pose, PoseRig rig, Matrix4x4 toEditor, int actor, IReadOnlySet<string>? skip = null)
    {
        // 每根骨骼：逆绑定 × 当前变换 × 换到编辑中角色的模型空间
        var skin = new Matrix4x4?[part.Bones.Length];
        for (var b = 0; b < part.Bones.Length; b++)
        {
            var name = part.Bones[b];
            if (!pose.Has(name) || !rig.InverseBind(name, out var inverseBind)) continue;
            var t = pose[name];
            var current = Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(t.Rotation) * Matrix4x4.CreateTranslation(t.Translation);
            skin[b] = inverseBind * current * toEditor;
        }
        var n = part.Positions.Length;
        var positions = new Vector3[n];
        var normals = new Vector3[n];
        var dominant = new int[n];
        var valid = new bool[n];
        for (var v = 0; v < n; v++)
        {
            Vector3 p = Vector3.Zero, nn = Vector3.Zero;
            float total = 0, best = -1;
            for (var k = 0; k < K; k++)
            {
                var w = part.WeightOf[v * K + k];
                if (w <= 0) continue;
                var bone = part.BoneOf[v * K + k];
                if (w > best) (best, dominant[v]) = (w, bone);
                if (bone >= skin.Length || skin[bone] is not { } m) continue;
                p += Vector3.Transform(part.Positions[v], m) * w;
                nn += Vector3.TransformNormal(part.Normals[v], m) * w;
                total += w;
            }
            if (total < 0.5f) continue;   // 骨骼在姿势里找不到（比如别的种族骨架没有的骨骼）
            positions[v] = p / total;
            normals[v] = nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : Vector3.UnitY;
            valid[v] = true;
        }
        var triangles = part.Triangles;
        var triBones = new string[triangles.Length / 3];
        for (var t = 0; t < triBones.Length; t++) triBones[t] = part.Bones.Length > 0 ? part.Bones[dominant[triangles[t * 3]]] : "";
        bool Keep(int t)
        {
            for (var k = 0; k < 3; k++)
            {
                var v = triangles[t * 3 + k];
                if (!valid[v] || (skip is not null && skip.Contains(part.Bones[dominant[v]]))) return false;
            }
            return true;
        }
        return new Surface.Piece(positions, normals, triangles, triBones, actor, Keep);
    }
}
