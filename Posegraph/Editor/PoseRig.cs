using System.Numerics;

namespace Posegraph.Editor;

/// <summary>
/// 编辑器用的骨架信息：父子关系、绑定姿势（模型空间旋转）、哪些骨骼属于脸 / 头发等分部骨架。
/// 绑定姿势用来算左右镜像（两侧骨骼的局部坐标轴约定不同）和关节限位（相对绑定姿势转了多少）。
/// 游戏里从 Havok 骨架读（PoseEditor.ReadRig），离线测试从后端骨架 JSON 建。
/// </summary>
public sealed class PoseRig(Dictionary<string, string> parents, Dictionary<string, Quaternion> bind, HashSet<string> partial,
                            Dictionary<string, Matrix4x4>? bindMatrices = null)
{
    /// <summary>
    /// 绑定姿势下骨骼的模型空间变换（缩放·旋转·平移）的逆：模型文件里的顶点是绑定姿势，蒙皮 = 顶点 × 逆绑定 × 当前变换。
    /// 有蒙皮修正（WithSkinFix）时再乘上修正：蒙皮 = 顶点 × 逆绑定 × 修正 × 当前变换。
    /// </summary>
    public bool InverseBind(string bone, out Matrix4x4 inverse)
    {
        if (!_inverseBind.TryGetValue(bone, out inverse)) return false;
        if (_skinFix is not null && _skinFix.TryGetValue(bone, out var fix)) inverse *= fix;
        return true;
    }

    private Dictionary<string, Matrix4x4>? _skinFix;

    /// <summary>这根骨骼的蒙皮修正（没有时 false）。</summary>
    public bool SkinFixOf(string bone, out Matrix4x4 fix)
    {
        fix = Matrix4x4.Identity;
        return _skinFix is not null && _skinFix.TryGetValue(bone, out fix);
    }

    /// <summary>
    /// 跟着 bone 动的一个点在游戏里实际画在哪（有蒙皮修正时：点 × 当前变换的逆 × 修正 × 当前变换；没有修正就是原样）。
    /// 关节本身就传 pose.Position(bone)。Customize+ 缩放过的角色手的网格能比 Brio 读到的手骨偏 2–3cm，掌心等按网格算的量都要用这个
    /// </summary>
    public Vector3 Rendered(EditPose pose, string bone, Vector3 point)
    {
        if (!pose.Has(bone) || !SkinFixOf(bone, out var fix)) return point;
        var t = pose[bone];
        var m = Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(t.Rotation)) * Matrix4x4.CreateTranslation(t.Translation);
        return Matrix4x4.Invert(m, out var inverse) ? Vector3.Transform(point, inverse * fix * m) : point;
    }

    /// <summary>
    /// 带蒙皮修正的副本。修正是骨骼自己坐标系里的一个变换（渲染时的变换 = 修正 × Brio 读到的变换）：
    /// Customize+ 这类插件在动画之后才改骨骼的缩放 / 位移 / 旋转（只改这根骨骼自己），Brio 读到的姿势里没有，
    /// 不补上的话算出来的身体、手比游戏里画出来的细、位置也偏几厘米。
    /// </summary>
    public PoseRig WithSkinFix(Dictionary<string, Matrix4x4> fix) => new(parents, bind, partial, bindMatrices) { _skinFix = fix };

    /// <summary>绑定姿势下 bone 的位置在 parent 的局部坐标里（手指根在手腕坐标里的方向之类）；没有记录时 null。</summary>
    public Vector3? BindOffset(string bone, string parent)
        => bindMatrices is not null && bindMatrices.TryGetValue(bone, out var m) && _inverseBind.TryGetValue(parent, out var inverseParent)
            ? Vector3.Transform(m.Translation, inverseParent)
            : null;

    private readonly Dictionary<string, Matrix4x4> _inverseBind = (bindMatrices ?? [])
        .Select(kv => (kv.Key, Ok: Matrix4x4.Invert(kv.Value, out var inv), inv))
        .Where(x => x.Ok).ToDictionary(x => x.Key, x => x.inv);

    public IReadOnlyDictionary<string, string> Parents => parents;

    /// <summary>不在身体骨架里的骨骼（脸、头发、尾巴……）：Brio 写这些骨骼时有额外的偏差，见 PoseEditor 里的校准。</summary>
    public IReadOnlySet<string> Partial => partial;

    public bool HasBind(string bone) => bind.ContainsKey(bone);

    /// <summary>绑定姿势下的模型空间旋转（没有记录时为单位旋转）。</summary>
    public Quaternion Bind(string bone) => bind.TryGetValue(bone, out var q) ? q : Quaternion.Identity;

    /// <summary>绑定姿势下相对父骨骼的旋转。</summary>
    public Quaternion BindLocal(string bone)
        => parents.TryGetValue(bone, out var p) ? Quaternion.Normalize(Quaternion.Conjugate(Bind(p)) * Bind(bone)) : Bind(bone);

    /// <summary>左右对应的骨骼名（j_te_l ↔ j_te_r）；不分左右的返回 null。</summary>
    public static string? Mirror(string bone)
        => bone.EndsWith("_l", StringComparison.Ordinal) ? bone[..^2] + "_r"
           : bone.EndsWith("_r", StringComparison.Ordinal) ? bone[..^2] + "_l"
           : null;
}
