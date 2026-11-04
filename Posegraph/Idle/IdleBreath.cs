namespace Posegraph.Idle;

/// <summary>身体分区：每一块单独调原版 idle 的动作（呼吸起伏、小动作）叠多少。</summary>
public enum BodyRegion { Pelvis, Spine, Chest, Head, Arms, Fingers, Legs, Other }

/// <summary>
/// 各分区叠加原版 idle 增量的强度（0 = 保持姿势不动，1 = 原版幅度）。
/// 默认只让躯干呼吸：原版 idle 里还有重心转移、摆手、看左右等小动作，叠到别的姿势上手会乱摆、脚会滑，所以四肢默认不动；
/// 骨盆一动整个人（连着腿）跟着动，四肢不动时脚会浮起或陷下去，也默认不动。
/// </summary>
public sealed class IdleBreath
{
    public static readonly BodyRegion[] Regions = Enum.GetValues<BodyRegion>();

    private readonly float[] _weights = [0f, 1f, 1f, 0.5f, 0f, 0f, 0f, 1f];

    public float this[BodyRegion region]
    {
        get => _weights[(int)region];
        set => _weights[(int)region] = Math.Clamp(value, 0f, 1f);
    }

    public IdleBreath Clone()
    {
        var copy = new IdleBreath();
        _weights.CopyTo(copy._weights, 0);
        return copy;
    }

    public void Reset() => new IdleBreath()._weights.CopyTo(_weights, 0);

    public float Weight(string? bone) => this[RegionOf(bone)];

    public static string Label(BodyRegion region) => region switch
    {
        BodyRegion.Pelvis => Loc.T("骨盆"),
        BodyRegion.Spine => Loc.T("脊椎"),
        BodyRegion.Chest => Loc.T("胸和肩"),
        BodyRegion.Head => Loc.T("头颈"),
        BodyRegion.Arms => Loc.T("手臂"),
        BodyRegion.Fingers => Loc.T("手指"),
        BodyRegion.Legs => Loc.T("腿"),
        _ => Loc.T("其他（头发、尾巴、裙摆等）"),
    };

    /// <summary>按骨骼名分区（FFXIV 基础骨架的命名）。</summary>
    public static BodyRegion RegionOf(string? bone)
    {
        if (bone is null) return BodyRegion.Other;
        if (bone is "n_hara" or "j_kosi") return BodyRegion.Pelvis;
        if (bone.StartsWith("j_sebo_")) return BodyRegion.Spine;
        if (bone.StartsWith("j_mune") || bone.StartsWith("j_sako") || bone.StartsWith("n_hkata") || bone.StartsWith("n_kataarmor")) return BodyRegion.Chest;
        if (bone is "j_kubi" or "j_kao" or "j_ago") return BodyRegion.Head;
        if (bone.StartsWith("j_oya") || bone.StartsWith("j_hito") || bone.StartsWith("j_naka") || bone.StartsWith("j_kusu") || bone.StartsWith("j_ko_"))
            return BodyRegion.Fingers;
        if (bone.StartsWith("j_ude") || bone.StartsWith("j_te_") || bone.StartsWith("n_hhiji") || bone.StartsWith("n_hijisoubi") || bone.StartsWith("n_hte"))
            return BodyRegion.Arms;
        if (bone.StartsWith("j_asi") || bone.StartsWith("n_hizasoubi")) return BodyRegion.Legs;
        return BodyRegion.Other;
    }
}
