using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace Posegraph;

/// <summary>
/// 种族骨架编号 cXX01（与后端 posegraph/retarget/races.py 一致）。XX：中原男 01 / 女 02、高地 03/04、精灵 05/06、猫魅 07/08、
/// 鲁加 09/10、拉拉菲尔 11/12、敖龙 13/14、硌狮 15/16、维埃拉 17/18。
/// </summary>
public static class Races
{
    /// <summary>Validated：游戏内实测过；Supported：已适配（女性骨架 2026-10-26 离线核对后已适配，待游戏内复测）。</summary>
    public sealed record Race(string Code, string Name, string Group, bool Validated, bool Supported = true);

    public static readonly Race[] All =
    [
        new("c0101", "人族中原 男", "中原系男", true),
        new("c0301", "人族高地 男", "中原系男", true),
        new("c0701", "猫魅 男", "中原系男", true),
        new("c1301", "敖龙 男", "中原系男", true),
        new("c1701", "维埃拉 男", "中原系男", true),
        new("c0501", "精灵 男", "精灵男", true),
        new("c0901", "鲁加 男", "鲁加系男", false),
        new("c1501", "硌狮 男", "鲁加系男", false),
        new("c1101", "拉拉菲尔 男", "拉拉菲尔", false),
        new("c0201", "人族中原 女", "中原系女", false),
        new("c0401", "人族高地 女", "中原系女", false),
        new("c1801", "维埃拉 女", "中原系女", false),
        new("c0601", "精灵 女", "精灵系女", false),
        new("c1001", "鲁加 女", "精灵系女", false),
        new("c1601", "硌狮 女", "精灵系女", false),
        new("c0801", "猫魅 女", "猫魅系女", false),
        new("c1401", "敖龙 女", "猫魅系女", false),
        new("c1201", "拉拉菲尔 女", "拉拉菲尔", false),
    ];

    public static Race? Find(string? code) => All.FirstOrDefault(r => r.Code == code);

    public static string Label(string? code) => Find(code) is { } r ? Loc.T(r.Name) : code ?? Loc.T("未知");

    /// <summary>角色当前实际加载的骨架编号（人形绘制对象的 RaceSexId，如 101 → c0101）；能反映换种族 MOD。读不到为 null。必须在主线程调用。</summary>
    public static unsafe string? FromModel(IGameObject? obj)
    {
        if (obj is null || obj.Address == 0) return null;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        var draw = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)go->DrawObject;
        if (draw == null || draw->GetModelType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human) return null;
        var id = ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)draw)->RaceSexId;
        return id > 0 ? $"c{id:0000}" : null;
    }

    /// <summary>
    /// 能不能摆姿势：人形模型（玩家、Brio 新建的角色、雇员等人形 NPC）。宠物、坐骑、怪物、道具等骨架套不上，排除。
    /// 模型还没加载（换种族重建中）时只按对象类型判断，免得列表里的角色闪一下就没了。必须在主线程调用。
    /// </summary>
    public static unsafe bool IsPoseable(IGameObject? obj)
    {
        if (obj is null || obj.Address == 0) return false;
        if (obj.ObjectKind is ObjectKind.Companion or ObjectKind.Mount or ObjectKind.Ornament) return false;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        var draw = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)go->DrawObject;
        if (draw == null) return obj is ICharacter;
        return draw->GetModelType() == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human;
    }

    /// <summary>按角色外貌数据（种族、部族、性别）得到骨架编号；非人形模型（坐骑、怪物等）返回 null。必须在主线程调用。</summary>
    public static string? FromCharacter(IGameObject? obj)
    {
        if (obj is not ICharacter ch) return null;
        var c = ch.Customize;
        if (c.Length <= (int)CustomizeIndex.Tribe) return null;
        int baseId = c[(int)CustomizeIndex.Race] switch
        {
            1 => c[(int)CustomizeIndex.Tribe] == 2 ? 3 : 1,   // 人族：部族 1 中原、2 高地
            2 => 5,
            3 => 11,
            4 => 7,
            5 => 9,
            6 => 13,
            7 => 15,
            8 => 17,
            _ => 0,
        };
        if (baseId == 0) return null;
        return $"c{baseId + c[(int)CustomizeIndex.Gender]:00}01";
    }
}
