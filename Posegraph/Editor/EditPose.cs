using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Posegraph.Idle;

namespace Posegraph.Editor;

/// <summary>
/// 编辑中的姿势：每根骨骼的模型空间变换（和 Brio 姿势 JSON 一样），加上骨骼父子关系（从游戏 Havok 骨架读，见 PoseRig）。
/// 改姿势都是「把某根骨骼连同它下面的整棵子树绕一个点转 / 平移」，所以不用来回换局部 / 模型空间。
/// </summary>
public sealed class EditPose
{
    private readonly Dictionary<string, BoneTransform> _bones;
    private readonly Dictionary<string, string[]> _children;
    private readonly IReadOnlyDictionary<string, string> _parents;
    private readonly string _header;   // Brio 姿势 JSON 里 Bones 之外的字段（原样写回）

    private EditPose(Dictionary<string, BoneTransform> bones, Dictionary<string, string[]> children, IReadOnlyDictionary<string, string> parents,
                     string header)
        => (_bones, _children, _parents, _header) = (bones, children, parents, header);

    public IReadOnlyCollection<string> Names => _bones.Keys;

    public bool Has(string bone) => _bones.ContainsKey(bone);

    public BoneTransform this[string bone] => _bones[bone];

    public Vector3 Position(string bone) => _bones[bone].Translation;

    public Quaternion Rotation(string bone) => _bones[bone].Rotation;

    public string? Parent(string bone) => _parents.TryGetValue(bone, out var p) && _bones.ContainsKey(p) ? p : null;

    /// <summary>相对父骨骼的旋转（没有父骨骼时就是模型空间旋转）。</summary>
    public Quaternion LocalRotation(string bone)
        => Parent(bone) is { } p ? Quaternion.Normalize(Quaternion.Conjugate(Rotation(p)) * Rotation(bone)) : Rotation(bone);

    public EditPose Clone() => new(new Dictionary<string, BoneTransform>(_bones), _children, _parents, _header);

    /// <summary>把 bone 和它下面的所有骨骼绕 pivot 转 q（模型空间）。</summary>
    public void RotateSubtree(string bone, Vector3 pivot, Quaternion q)
    {
        if (!_bones.ContainsKey(bone)) return;
        foreach (var name in Walk(bone))
            if (_bones.TryGetValue(name, out var t))
                _bones[name] = t with
                {
                    Translation = pivot + Vector3.Transform(t.Translation - pivot, q),
                    Rotation = Quaternion.Normalize(q * t.Rotation),
                };
    }

    /// <summary>把 bone 和它下面的所有骨骼平移 delta（模型空间）。</summary>
    public void TranslateSubtree(string bone, Vector3 delta)
    {
        if (!_bones.ContainsKey(bone)) return;
        foreach (var name in Walk(bone))
            if (_bones.TryGetValue(name, out var t))
                _bones[name] = t with { Translation = t.Translation + delta };
    }

    /// <summary>把 bone 的模型空间旋转设成 rotation（绕自己的位置转，子树跟着）。</summary>
    public void SetRotation(string bone, Quaternion rotation)
    {
        if (!_bones.TryGetValue(bone, out var t)) return;
        RotateSubtree(bone, t.Translation, Quaternion.Normalize(Quaternion.Normalize(rotation) * Quaternion.Conjugate(t.Rotation)));
    }

    /// <summary>bone 和它下面的所有骨骼的名字。</summary>
    public HashSet<string> Subtree(string bone)
    {
        var result = new HashSet<string>(Walk(bone));
        result.IntersectWith(_bones.Keys);
        return result;
    }

    /// <summary>bone 和它下面的所有骨骼，父骨骼一定排在子骨骼前面。</summary>
    public List<string> SubtreeOrdered(string bone) => _bones.ContainsKey(bone) ? Walk(bone).Where(_bones.ContainsKey).ToList() : [];

    /// <summary>和 other 比，局部旋转（或平移，给了 positionBones 时）变了的骨骼加进 into。</summary>
    public void CollectChanged(EditPose other, HashSet<string> into, IReadOnlySet<string>? positionBones = null)
    {
        foreach (var name in _bones.Keys)
        {
            if (!other.Has(name)) continue;
            var a = LocalRotation(name);
            var b = other.LocalRotation(name);
            if (MathF.Abs(Quaternion.Dot(a, b)) < 0.9999995f) into.Add(name);
            else if (positionBones?.Contains(name) == true && Vector3.DistanceSquared(Position(name), other.Position(name)) > 1e-10f) into.Add(name);
        }
    }

    private IEnumerable<string> Walk(string bone)
    {
        var stack = new Stack<string>();
        var seen = new HashSet<string>();
        stack.Push(bone);
        while (stack.Count > 0)
        {
            var name = stack.Pop();
            if (!seen.Add(name)) continue;
            yield return name;
            if (_children.TryGetValue(name, out var kids))
                foreach (var kid in kids) stack.Push(kid);
        }
    }

    /// <summary>从 Brio 姿势 JSON（模型空间）和父子关系（骨骼名 → 父骨骼名）建出来；读不了时 null。</summary>
    public static EditPose? Parse(string json, IReadOnlyDictionary<string, string> parents)
    {
        var bones = new Dictionary<string, BoneTransform>();
        var header = new StringBuilder("{");
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "Bones")
                {
                    foreach (var bone in prop.Value.EnumerateObject())
                    {
                        if (Vec(bone.Value, "Position") is not { } p || Quat(bone.Value, "Rotation") is not { } r) continue;
                        bones[bone.Name] = new BoneTransform(p, r, Vec(bone.Value, "Scale") ?? Vector3.One);
                    }
                    continue;
                }
                header.Append(JsonSerializer.Serialize(prop.Name)).Append(':').Append(prop.Value.GetRawText()).Append(',');
            }
        }
        catch (JsonException)
        {
            return null;
        }
        if (bones.Count == 0) return null;
        var children = parents.Where(kv => bones.ContainsKey(kv.Key))
                              .GroupBy(kv => kv.Value)
                              .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToArray());
        return new EditPose(bones, children, parents, header.ToString());
    }

    /// <summary>
    /// 写成 Brio 姿势 JSON（Bones 之外的字段照读进来的原样）。only：只写这些骨骼（null 全写）；
    /// rotationFix：写出前改某些骨骼的旋转（脸部骨架的校正，见 PoseEditor）。
    /// </summary>
    public string ToJson(IEnumerable<string>? only = null, IReadOnlyDictionary<string, Quaternion>? rotationFix = null)
    {
        var sb = new StringBuilder(_header, _header.Length + _bones.Count * 120 + 16);
        sb.Append("\"Bones\":{");
        var first = true;
        foreach (var name in only ?? _bones.Keys)
        {
            if (!_bones.TryGetValue(name, out var t)) continue;
            var r = rotationFix is not null && rotationFix.TryGetValue(name, out var fix) ? Quaternion.Normalize(fix * t.Rotation) : t.Rotation;
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(name))
              .Append(":{\"Position\":\"").Append(F(t.Translation.X)).Append(", ").Append(F(t.Translation.Y)).Append(", ").Append(F(t.Translation.Z))
              .Append("\",\"Rotation\":\"").Append(F(r.X)).Append(", ").Append(F(r.Y)).Append(", ").Append(F(r.Z)).Append(", ").Append(F(r.W))
              .Append("\",\"Scale\":\"").Append(F(t.Scale.X)).Append(", ").Append(F(t.Scale.Y)).Append(", ").Append(F(t.Scale.Z)).Append("\"}");
        }
        sb.Append("}}");
        return sb.ToString();
    }

    private static string F(float v) => v.ToString("0.#######", CultureInfo.InvariantCulture);

    private static float[]? Floats(JsonElement bone, string key)
    {
        if (!bone.TryGetProperty(key, out var v) || v.GetString() is not { } text) return null;
        var parts = text.Split(',');
        var result = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i])) return null;
        return result;
    }

    private static Vector3? Vec(JsonElement bone, string key)
        => Floats(bone, key) is [var x, var y, var z] ? new Vector3(x, y, z) : null;

    private static Quaternion? Quat(JsonElement bone, string key)
        => Floats(bone, key) is [var x, var y, var z, var w] ? Quaternion.Normalize(new Quaternion(x, y, z, w)) : null;
}
