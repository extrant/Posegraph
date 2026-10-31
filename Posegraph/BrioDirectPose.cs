using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Game.ClientState.Objects.Types;

namespace Posegraph;

/// <summary>
/// 逐帧套姿势（视频播放）用：反射调 Brio 内部，照 Brio 自己时间轴的做法当帧写骨骼。不走 IPC LoadPoseFromJson 的原因：
/// - IPC 导入先暂停动画、4 帧后才真正排进骨骼更新。先清旧增量再导入的话，中间几帧露出原动画（人物一闪一闪）；
///   不清的话每次导入给每根骨骼新加一层增量，一秒几十层，播久了越来越卡。
/// - 4 帧里接连导入的几次都按同一个旧姿势算增量，叠起来过冲；脸部骨架（沿用上一帧结果）会越漂越远（实测几秒脸偏出几米）。
/// 这里每次：还有没执行的导入就跳过这一帧；否则清掉要写的骨骼的旧增量（各分部骨架里同名的一起清，别的骨骼如用户调的表情、头发不动），
/// 再直接 SkeletonPosingCapability.ImportSkeletonPose，同一次骨骼更新里生效。也不记撤销（同 Brio 时间轴播放）。
/// Brio 内部类型对不上（版本变了）时返回 null，调用方改走 IPC。必须在游戏主线程调用。
/// </summary>
public sealed class BrioDirectPose
{
    private Func<IGameObject, object?>? _skeletonOf;
    private PropertyInfo? _poseInfo;
    private PropertyInfo? _pending;
    private MethodInfo? _clear;
    private MethodInfo? _import;
    private MethodInfo? _sanitize;
    private MethodInfo? _deserialize;
    private Type? _poseFileType;
    private Type? _idType;
    private object? _options;
    private bool _resolved;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<HashSet<string>, Delegate> _predicates = new();   // 多人同场：每人一组骨骼

    /// <summary>上一次用不了的原因（排查用）。</summary>
    public string? Problem { get; private set; }

    /// <summary>Brio 重载后旧的类型和服务就失效了：换片段时调一次，下次重新找。</summary>
    public void Invalidate() => _resolved = false;

    /// <summary>套姿势：true 已排进这一帧的骨骼更新，false 上一次还没执行（这一帧先不套），null 用不了（改走 IPC）。
    /// bones：这份姿势里的骨骼名（套之前清它们的旧增量）。</summary>
    public bool? Apply(IGameObject actor, string poseJson, HashSet<string> bones)
    {
        try
        {
            if (!_resolved) Resolve();
            if (_skeletonOf is null || _poseInfo is null || _pending is null || _clear is null || _import is null || _poseFileType is null
                || _idType is null || _options is null)
            {
                Problem ??= "Brio internals not found";
                return null;
            }
            if (_skeletonOf(actor) is not { } skeleton)
            {
                Problem = "no skeleton capability for actor";
                return null;
            }
            if (_pending.GetValue(skeleton) is true) return false;
            if (_deserialize?.Invoke(null, [poseJson]) is not { } pose) return null;
            _sanitize?.Invoke(pose, null);
            if (_poseInfo.GetValue(skeleton) is { } poseInfo) _clear.Invoke(poseInfo, [Predicate(bones)]);
            _import.Invoke(skeleton, [pose, _options, false]);
            return true;
        }
        catch (Exception ex)
        {
            Problem = (ex.InnerException ?? ex).ToString();
            _skeletonOf = null;   // 内部结构对不上：这个片段不再试，Invalidate 后重找
            return null;
        }
    }

    /// <summary>id => bones.Contains(id.BoneName)，同一组骨骼只编译一次。</summary>
    private Delegate Predicate(HashSet<string> bones)
    {
        if (_predicates.TryGetValue(bones, out var cached)) return cached;
        var id = Expression.Parameter(_idType!, "id");
        var body = Expression.Call(Expression.Constant(bones), typeof(HashSet<string>).GetMethod(nameof(HashSet<string>.Contains))!,
                                   Expression.Property(id, "BoneName"));
        var predicate = Expression.Lambda(typeof(Predicate<>).MakeGenericType(_idType!), body, id).Compile();
        _predicates.AddOrUpdate(bones, predicate);
        return predicate;
    }

    private void Resolve()
    {
        _resolved = true;
        _skeletonOf = null;
        Problem = null;
        var asm = AssemblyLoadContext.All.SelectMany(c => c.Assemblies)
            .LastOrDefault(x => x.GetName().Name == "Brio" && x.GetType("Brio.Brio") is not null);
        var managerType = asm?.GetType("Brio.Entities.EntityManager");
        var entityIdType = asm?.GetType("Brio.Entities.Core.EntityId");
        var entityType = asm?.GetType("Brio.Entities.Core.Entity");
        var skeletonType = asm?.GetType("Brio.Capabilities.Posing.SkeletonPosingCapability");
        var poseInfoType = asm?.GetType("Brio.Game.Posing.PoseInfo");
        var serviceType = asm?.GetType("Brio.Game.Posing.PosingService");
        var poseDataType = asm?.GetType("Brio.Files.PoseData");
        _poseFileType = asm?.GetType("Brio.Files.PoseFile");
        _idType = asm?.GetType("Brio.Game.Posing.BonePoseInfoId");
        if (asm is null || managerType is null || entityIdType is null || entityType is null || skeletonType is null || poseInfoType is null
            || serviceType is null || poseDataType is null || _poseFileType is null || _idType is null)
        {
            Problem = "Brio types not found";
            return;
        }

        var tryGetService = asm.GetType("Brio.Brio")!.GetMethod("TryGetService");
        object?[] manager = [null], posing = [null];
        if (tryGetService?.MakeGenericMethod(managerType).Invoke(null, manager) is not true || manager[0] is null
            || tryGetService.MakeGenericMethod(serviceType).Invoke(null, posing) is not true || posing[0] is null)
        {
            Problem = "Brio services not found";
            return;
        }
        _options = serviceType.GetProperty("DefaultIPCImporterOptions")?.GetValue(posing[0]);
        var newId = entityIdType.GetConstructor([typeof(IGameObject)]);
        var tryGetEntity = managerType.GetMethod("TryGetEntity", [entityIdType, entityType.MakeByRefType()]);
        var tryGetCapability = entityType.GetMethods().FirstOrDefault(m => m.Name == "TryGetCapability" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3)
            ?.MakeGenericMethod(skeletonType);
        _poseInfo = skeletonType.GetProperty("PoseInfo");
        _pending = skeletonType.GetProperty("HasTransitiveActions");
        _import = skeletonType.GetMethod("ImportSkeletonPose");
        _sanitize = poseDataType.GetMethod("SanitizeBoneNames");
        // Brio 自己的 JSON 设置（向量、四元数写成 "x, y, z" 字符串）
        _deserialize = asm.GetType("Brio.Core.JsonSerializer")?.GetMethod("Deserialize", [typeof(string)])?.MakeGenericMethod(_poseFileType);
        _clear = poseInfoType.GetMethod("Clear");
        if (newId is null || tryGetEntity is null || tryGetCapability is null || _options is null)
        {
            Problem = $"members missing: id {newId is not null}, entity {tryGetEntity is not null}, capability {tryGetCapability is not null}, options {_options is not null}";
            return;
        }

        var service = manager[0];
        _skeletonOf = actor =>
        {
            object?[] entity = [newId.Invoke([actor]), null];
            if (tryGetEntity.Invoke(service, entity) is not true || entity[1] is null) return null;
            object?[] capability = [null, false, false];
            return tryGetCapability.Invoke(entity[1], capability) is true ? capability[0] : null;
        };
    }
}
