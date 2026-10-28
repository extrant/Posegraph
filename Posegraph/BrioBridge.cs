using Brio.API;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;

namespace Posegraph;

/// <summary>
/// Brio IPC 的薄封装。所有方法必须在游戏主线程调用（由 BridgeServer 负责切换）。
/// </summary>
public sealed class BrioBridge(IDalamudPluginInterface pi)
{
    private readonly ApiVersion _apiVersion = new(pi);
    private readonly IsAvailable _isAvailable = new(pi);
    private readonly GetAllActors _getAllActors = new(pi);
    private readonly GetPoseAsJson _getPoseAsJson = new(pi);
    private readonly LoadPoseFromJson _loadPoseFromJson = new(pi);
    private readonly FreezeActor _freezeActor = new(pi);
    private readonly ResetPose _resetPose = new(pi);
    private readonly GetModelTransform _getModelTransform = new(pi);
    private readonly SetModelTransform _setModelTransform = new(pi);

    public (int Breaking, int Feature)? TryGetApiVersion()
    {
        try { return _apiVersion.Invoke(); }
        catch { return null; }
    }

    public bool IsAvailable()
    {
        try { return _isAvailable.Invoke(); }
        catch { return false; }
    }

    public IGameObject[] GetActors() => _getAllActors.Invoke() ?? [];

    public string? GetPose(IGameObject actor) => _getPoseAsJson.Invoke(actor);

    public bool ApplyPose(IGameObject actor, string poseJson) => _loadPoseFromJson.Invoke(actor, poseJson);

    /// <summary>
    /// 冻结动画。Brio 导入姿势时会暂停并重置时间轴、导入后恢复原速度，
    /// 姿势以增量叠加在动画帧上，不冻结的话待机动画会继续播放导致姿势漂移。
    /// </summary>
    public bool Freeze(IGameObject actor) => _freezeActor.Invoke(actor);

    /// <summary>
    /// 清除 Brio 的姿势栈（每根骨骼"目标 - 当时姿势"的增量，每帧叠加，换模型后仍按骨骼名保留）。
    /// Brio 的重置会顺带重置模型位置，这里先记下再恢复，角色不会跳回原位。
    /// </summary>
    public bool ResetPoseKeepTransform(IGameObject actor)
    {
        var (position, rotation, scale) = _getModelTransform.Invoke(actor);
        if (!_resetPose.Invoke(actor, false)) return false;
        if (position is not null || rotation is not null || scale is not null)
            _setModelTransform.Invoke(actor, position, rotation, scale, false);
        return true;
    }

    /// <summary>角色现在的模型位置、旋转、缩放（合并模式复刻站位读这个，和 SetModelTransform 一致）。</summary>
    public (System.Numerics.Vector3? Position, System.Numerics.Quaternion? Rotation, System.Numerics.Vector3? Scale) GetModelTransform(IGameObject actor)
        => _getModelTransform.Invoke(actor);

    /// <summary>移动角色（合并模式复刻站位）：世界坐标与旋转，缩放不变。</summary>
    public bool SetModelTransform(IGameObject actor, System.Numerics.Vector3 position, System.Numerics.Quaternion rotation)
        => _setModelTransform.Invoke(actor, position, rotation, null, false);
}
