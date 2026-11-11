using System.Numerics;

namespace Posegraph.Editor;

/// <summary>
/// 身体按骨骼分成的部位（「贴到部位」点选时用；贴合度按部位的软硬自动多压一点）。
/// 名字是界面文字（Loc.T 翻译）；左右分开的部位带「左 / 右」。
/// </summary>
public static class BodyRegions
{
    /// <summary>这根骨骼（表面三角形主要跟的骨骼）属于哪个部位。</summary>
    public static string Of(string bone)
    {
        var side = bone.EndsWith("_l", StringComparison.Ordinal) ? "左" : bone.EndsWith("_r", StringComparison.Ordinal) ? "右" : "";
        string Has(params string[] prefixes) => prefixes.Any(p => bone.StartsWith(p, StringComparison.Ordinal)) ? "y" : "";
        if (Has("j_kao", "j_f_", "j_kami", "j_mimi", "j_ago", "j_ex_h", "j_zera", "n_ear") != "") return "头";
        if (bone == "j_kubi" || bone.StartsWith("n_kubi", StringComparison.Ordinal)) return "脖子";
        if (Has("j_mune", "iv_c_mune") != "") return side + "胸";
        if (bone == "j_sebo_c") return "胸";
        if (Has("j_sako", "n_hkata") != "") return side + "肩";
        if (bone is "j_sebo_a" or "j_sebo_b" || Has("iv_fukubu", "iv_kosi") != "") return "腹";
        if (Has("j_kosi", "iv_shiri", "j_sk_b", "iv_kougan", "iv_inshin") != "") return "臀";
        if (Has("j_asi_a", "j_asi_b", "iv_daitai") != "") return side + "大腿";
        if (Has("j_asi_c", "j_asi_d", "j_asi_e", "iv_asi") != "") return side + "小腿";
        if (Has("j_ude_a", "n_hhiji", "n_hijisoubi") != "") return side + "上臂";
        if (Has("j_ude_b", "n_hte") != "") return side + "前臂";
        if (Has("j_te", "j_oya", "j_hito", "j_naka", "j_kusu", "j_ko", "iv_oya", "iv_hito", "iv_naka", "iv_kusu", "iv_ko") != "") return side + "手";
        if (Has("j_sk_") != "") return "裙摆";
        return bone;
    }

    /// <summary>
    /// 按位置细分：男性模型没有胸部骨骼，胸肌的蒙皮跟脊椎（j_sebo_b、j_sebo_c）走，只按骨骼分不出胸和腹。
    /// 躯干正面在 chestLine（模型空间高度）以上算胸、以下算腹；chestLine 为 null（别的角色，没有骨架）时只按骨骼。
    /// </summary>
    public static string Of(string bone, Vector3 point, float? chestLine)
    {
        var region = Of(bone);
        if (chestLine is { } line && (region is "腹" || (region == "胸" && bone == "j_sebo_c")))
            return point.Y > line ? "胸" : "腹";
        // 胸肌外侧的蒙皮常常跟锁骨（j_sako）走，被归成「肩」——只能往那一小块上贴，手会被别成竖的。胸线以上 12cm 内算胸
        if (chestLine is { } line2 && bone.StartsWith("j_sako", StringComparison.Ordinal) && point.Y < line2 + 0.12f)
            return "胸";
        return region;
    }

    /// <summary>自己的胸线：j_sebo_b 和 j_sebo_c 高度的中点（大约胸肌下沿）。</summary>
    public static float? ChestLine(EditPose pose)
        => pose.Has("j_sebo_b") && pose.Has("j_sebo_c") ? (pose.Position("j_sebo_b").Y + pose.Position("j_sebo_c").Y) / 2f : null;

    /// <summary>部位的软硬：贴合度「压紧」时软的部位多压进去一点（臀、胸、腹、大腿 1，肩、手臂、头 0）。</summary>
    public static float Softness(string bone)
    {
        var region = Of(bone);
        if (region.EndsWith("胸") || region is "臀" or "腹" || region.EndsWith("大腿")) return 1f;
        if (region.EndsWith("小腿") || region.EndsWith("上臂")) return 0.5f;
        return 0f;
    }
}
