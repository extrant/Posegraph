using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace Posegraph;

/// <summary>插件界面用的姿势读写（HTTP 接口走 BridgeServer，逻辑相同）。</summary>
public sealed class PoseApplier(IFramework framework, IClientState clientState, ITargetManager targetManager, BrioBridge brio)
{
    private const int FreezeSettleTicks = 5;

    /// <summary>从差异很大的姿势切换时，Brio 首次应用会残留最多约 1° 偏差（头颈、手指），隔一会儿再应用一次即收敛。</summary>
    private const int SecondApplyTicks = 30;

    /// <summary>actor 为 ObjectIndex，null 表示 GPose 当前目标。必须在主线程调用。</summary>
    public IGameObject? Resolve(int? actor)
        => actor is null ? targetManager.GPoseTarget : brio.GetActors().FirstOrDefault(a => a.ObjectIndex == actor);

    public Task<string?> GetPoseAsync(int? actor) => framework.RunOnFrameworkThread(() =>
        clientState.IsGPosing && Resolve(actor) is { } a ? brio.GetPose(a) : null);

    /// <summary>
    /// 角色"身份"。Key：是哪个角色（ObjectIndex + 类型 + BaseId + 名字，Glamourer 换外貌不变，Brio 删掉再新建会变）；
    /// Signature：这个角色当前的样子（Key + 骨架编号 + 外貌数据，Glamourer 换种族 / 体型时会变）。
    /// 界面按 Key 区分"换了一个角色"和"同一个角色换了外貌"：只有后者需要丢弃参考姿势、清除 Brio 残留姿势
    /// （旧角色的骨长套到新种族上会把身体拉长或压扁）。
    /// ModelReady：模型已加载（Glamourer 换种族时会重建模型，期间读到的骨长不可靠）。
    /// </summary>
    public sealed record ActorIdentity(int Index, string Key, string Signature, string? Race, bool ModelReady);

    /// <summary>必须在主线程调用。</summary>
    public unsafe ActorIdentity? Identify(int? actor)
    {
        if (!clientState.IsGPosing || Resolve(actor) is not { } a) return null;
        var model = Races.FromModel(a);
        var race = model ?? Races.FromCharacter(a);
        var customize = a is ICharacter ch ? Convert.ToHexString(ch.Customize) : "";
        var key = $"{a.ObjectIndex}:{a.ObjectKind}:{a.BaseId}:{a.Name.TextValue}";
        return new ActorIdentity(a.ObjectIndex, key, $"{key}:{race}:{customize}", race, model is not null);
    }

    public Task<ActorIdentity?> IdentifyAsync(int? actor) => framework.RunOnFrameworkThread(() => Identify(actor));

    /// <summary>清除 Brio 姿势栈（保留模型位置），等几帧让 Brio 读到的骨骼刷新。</summary>
    public async Task<bool> ResetPoseAsync(int? actor)
    {
        var ok = await framework.RunOnFrameworkThread(() =>
            clientState.IsGPosing && Resolve(actor) is { } a && brio.ResetPoseKeepTransform(a));
        if (ok) await framework.DelayTicks(FreezeSettleTicks);
        return ok;
    }

    /// <summary>角色现在的模型变换（合并模式复刻站位）；读不到为 null。</summary>
    public Task<Backend.ModelPose?> GetTransformAsync(int actor) => framework.RunOnFrameworkThread(() =>
    {
        if (!clientState.IsGPosing || Resolve(actor) is not { } a) return null;
        var (position, rotation, scale) = brio.GetModelTransform(a);
        if (position is not { } p || rotation is not { } r) return null;
        var s = scale ?? System.Numerics.Vector3.One;
        return new Backend.ModelPose([p.X, p.Y, p.Z], [r.X, r.Y, r.Z, r.W], [s.X, s.Y, s.Z]);
    });

    /// <summary>把角色移到世界坐标 position、朝向 rotation（合并模式复刻站位），等几帧让 Brio 读到的位置刷新。</summary>
    public async Task<bool> MoveAsync(int actor, System.Numerics.Vector3 position, System.Numerics.Quaternion rotation)
    {
        var ok = await framework.RunOnFrameworkThread(() =>
            clientState.IsGPosing && Resolve(actor) is { } a && brio.SetModelTransform(a, position, rotation));
        if (ok) await framework.DelayTicks(FreezeSettleTicks);
        return ok;
    }

    /// <summary>冻结动画、等几帧再导入（否则待机动画会让姿势漂移），之后再应用一次。返回错误信息，成功为 null。</summary>
    /// <summary>姿势成功套到角色上之后（参数是 ApplyAsync 的 actor）。灯光按新姿势重新摆、从图库恢复灯光用。</summary>
    public event Action<int?>? Applied;

    public async Task<string?> ApplyAsync(int? actor, string poseJson)
    {
        var error = await framework.RunOnFrameworkThread(() =>
        {
            if (!clientState.IsGPosing) return Loc.T("不在 GPose 中");
            var a = Resolve(actor);
            if (a is null) return Loc.T("找不到角色（请在 GPose 中选中目标）");
            brio.Freeze(a);
            return null;
        });
        if (error is not null) return error;
        await framework.DelayTicks(FreezeSettleTicks);
        for (var i = 0; i < 2; i++)
        {
            if (i > 0) await framework.DelayTicks(SecondApplyTicks);
            var ok = await framework.RunOnFrameworkThread(() => Resolve(actor) is { } a && brio.ApplyPose(a, poseJson));
            if (!ok) return Loc.T("Brio 应用姿势失败");
        }
        Applied?.Invoke(actor);
        return null;
    }
}
