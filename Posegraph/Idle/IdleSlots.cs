using Dalamud.Plugin.Services;

namespace Posegraph.Idle;

/// <summary>待机动作的类型（同游戏 /cpose 能切的几类）。</summary>
public enum IdleFamily { Stand, Sit, GroundSit, Doze }

/// <summary>某个种族某个槽位实际用的文件：GamePath 是游戏去读的路径（没有自己文件的种族读上级种族的），FileRace 是那个文件属于的种族。</summary>
public sealed record IdleTarget(string GamePath, string FileRace);

/// <summary>
/// 类型 + 槽位 → 游戏播放的循环动画文件（2026-11-04 用 Lumina 离线核对国服客户端，同 ReAnimate 的表）：
/// 槽位 0 是这一类的基础文件（站立 resident/idle、椅子 emote/sit、坐地 emote/jmn、打盹 emote/doze），
/// 1~N 是 /cpose 切换的 emote/&lt;前缀&gt;poseNN_loop（站立无前缀、椅子 s_、坐地 j_、打盹 l_），切进去时先播同名 _start 过渡。
/// 不是每个种族都有自己的文件：没有的读上级种族的路径（拉拉女 → 拉拉男，女性 → 中原女，男性和中原女 → 中原男）。
/// </summary>
public static class IdleSlots
{
    public const int MaxSlots = 8;

    public static readonly IdleFamily[] Families = [IdleFamily.Stand, IdleFamily.Sit, IdleFamily.GroundSit, IdleFamily.Doze];

    public static string Label(IdleFamily family) => family switch
    {
        IdleFamily.Stand => Loc.T("站立"),
        IdleFamily.Sit => Loc.T("坐椅子"),
        IdleFamily.GroundSit => Loc.T("坐地上"),
        _ => Loc.T("打盹"),
    };

    public static string SlotLabel(IdleFamily family, int slot)
        => slot == 0 ? $"{Label(family)} {Loc.T("默认")}" : $"{Label(family)} {slot}";

    /// <summary>模组里的组名、选项名用的固定中文名（不随界面语言变，换语言后再转仍覆盖同一个选项）。</summary>
    public static string ModLabel(IdleFamily family, int slot)
    {
        var name = family switch { IdleFamily.Stand => "站立", IdleFamily.Sit => "坐椅子", IdleFamily.GroundSit => "坐地上", _ => "打盹" };
        return slot == 0 ? $"{name} 默认" : $"{name} {slot}";
    }

    private static string BaseFile(IdleFamily family) => family switch
    {
        IdleFamily.Stand => "resident/idle",
        IdleFamily.Sit => "emote/sit",
        IdleFamily.GroundSit => "emote/jmn",
        _ => "emote/doze",
    };

    private static string Prefix(IdleFamily family) => family switch
    {
        IdleFamily.Stand => "",
        IdleFamily.Sit => "s_",
        IdleFamily.GroundSit => "j_",
        _ => "l_",
    };

    private static string LoopFile(IdleFamily family, int slot) => slot == 0 ? BaseFile(family) : $"emote/{Prefix(family)}pose{slot:D2}_loop";

    public static IdleTarget? Loop(IDataManager data, IdleFamily family, int slot, string race) => Probe(data, LoopFile(family, slot), race);

    /// <summary>切进这个槽位时播的过渡；槽位 0 没有（走游戏自己的姿态切换）。</summary>
    public static IdleTarget? Start(IDataManager data, IdleFamily family, int slot, string race)
        => slot == 0 ? null : Probe(data, $"emote/{Prefix(family)}pose{slot:D2}_start", race);

    /// <summary>这个种族这一类有哪些槽位。</summary>
    public static List<int> Available(IDataManager data, IdleFamily family, string race)
        => [.. Enumerable.Range(0, MaxSlots).Where(s => Loop(data, family, s, race) is not null)];

    public static string SklbPath(string race) => $"chara/human/{race}/skeleton/base/b0001/skl_{race}b0001.sklb";

    private static IdleTarget? Probe(IDataManager data, string file, string race)
    {
        foreach (var r in FallbackChain(race))
        {
            var path = $"chara/human/{r}/animation/a0001/bt_common/{file}.pap";
            if (data.FileExists(path)) return new IdleTarget(path, r);
        }
        return null;
    }

    /// <summary>游戏的种族继承链：自己 → 上级 → … → 中原男。</summary>
    public static IEnumerable<string> FallbackChain(string race)
    {
        yield return race;
        var id = int.Parse(race[1..]);
        while (id != 101)
        {
            id = id switch
            {
                1201 => 1101,
                201 => 101,
                _ when id / 100 % 2 == 0 => 201,
                _ => 101,
            };
            yield return $"c{id:D4}";
        }
    }
}
