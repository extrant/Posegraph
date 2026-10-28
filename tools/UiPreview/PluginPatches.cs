using HarmonyLib;
using Posegraph;
using Posegraph.UI;

namespace UiPreview;

/// <summary>
/// 插件里读游戏内存 / Brio IPC 的入口换成假数据（角色列表），让 GPose 相关的界面也能离线截图。
/// 由 App.FakeActors 控制：为 null 时不干预。
/// </summary>
[HarmonyPatch(typeof(MainWindow), nameof(MainWindow.Actors), MethodType.Getter)]
internal static class ActorsPatch
{
    private static bool Prefix(ref (int Index, string Name)[] __result)
    {
        if (App.FakeActors is not { } actors) return true;
        __result = actors;
        return false;
    }
}

[HarmonyPatch(typeof(MainWindow), nameof(MainWindow.InGpose), MethodType.Getter)]
internal static class InGposePatch
{
    private static bool Prefix(ref bool __result)
    {
        if (App.FakeActors is null) return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(MainWindow), "RefreshActors")]
internal static class RefreshActorsPatch
{
    private static bool Prefix() => App.FakeActors is null;   // 用假列表时不读 Brio
}

[HarmonyPatch(typeof(MainWindow), nameof(MainWindow.Identify))]
internal static class IdentifyPatch
{
    private static bool Prefix(int index, ref PoseApplier.ActorIdentity? __result)
    {
        if (App.FakeActors is not { } actors) return true;
        var name = actors.FirstOrDefault(a => a.Index == index).Name;
        __result = new PoseApplier.ActorIdentity(index, $"{index}:{name}", $"{index}:{name}:c0101", "c0101", true);
        return false;
    }
}

// 选了具体角色时主窗口会异步读这个角色的身份和游戏姿势（要读游戏内存）：离线时当作读不到姿势，按标准骨长计算
[HarmonyPatch(typeof(PoseApplier), nameof(PoseApplier.IdentifyAsync))]
internal static class IdentifyAsyncPatch
{
    private static bool Prefix(int? actor, ref Task<PoseApplier.ActorIdentity?> __result)
    {
        if (App.FakeActors is not { } actors) return true;
        var name = actors.FirstOrDefault(a => a.Index == actor).Name;
        __result = Task.FromResult<PoseApplier.ActorIdentity?>(actor is { } index
            ? new PoseApplier.ActorIdentity(index, $"{index}:{name}", $"{index}:{name}:c0101", "c0101", true)
            : null);
        return false;
    }
}

[HarmonyPatch(typeof(PoseApplier), nameof(PoseApplier.GetPoseAsync))]
internal static class GetPoseAsyncPatch
{
    private static bool Prefix(ref Task<string?> __result)
    {
        if (App.FakeActors is null) return true;
        __result = Task.FromResult<string?>(null);
        return false;
    }
}
