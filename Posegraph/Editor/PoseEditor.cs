using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.UI;
using Posegraph.Idle;

namespace Posegraph.Editor;

/// <summary>
/// 姿势编辑器（实验，只编进 Debug 版）：在 GPose 里把关键节点画在角色身上，直接拖或用 G / R（像 Blender）改姿势，每帧写进游戏。
/// 交互参考 AnimChar Tools（只拖控制点）和 Blender（单击选中、G 移动、R 旋转、X/Y/Z 锁轴、数字输入、Shift 微调）；
/// IK 链条照 Brio 的设置（手：上臂→前臂→手，肘绕前臂局部 z 轴；腿：大腿→小腿→脚，跳过 j_asi_b），代码全部自己写。
///
/// 线程：Draw（界面绘制）只读姿势、处理鼠标键盘、记下操作参数；解算和读写游戏都在 Update（游戏主线程）里做。
/// 写姿势走 BrioDirectPose，只写局部旋转真正变了的骨骼、只写旋转（骨长保持游戏里的实际值），平移整个人时根骨骼连位置一起写。
/// 每次操作结束后从 Brio 重读一次姿势，节点和游戏里的骨骼对齐。
/// 编辑完不需要单独存：主窗口原有的「检测到在 Brio 里改了骨骼 → 收进修正」会把改动收进这张图的修正。
/// </summary>
public sealed partial class PoseEditor(IFramework framework, IClientState clientState, IGameGui gameGui, IKeyState keyState, IDataManager data,
                                        IPluginLog log, PoseApplier applier, BrioBridge brio)
{
    private const int ResyncDelayFrames = 3;   // 写进去之后隔几帧再从 Brio 读回来（等 Brio 执行完）
    private const int UndoLimit = 100;

    private readonly BrioDirectPose _direct = new();
    private readonly EditorKeys _keys = new(keyState);
    private volatile int _grab;         // 键盘归编辑器：0 不抢，1 选中了节点，2 操作中（Draw 定，Update 清游戏的按键表）
    private readonly object _gate = new();
    private readonly ConcurrentQueue<Action<IGameObject>> _actions = new();   // 界面上的操作（撤销、镜像……），在主线程里执行

    private int? _actor;
    private PoseRig? _rig;
    private List<Node> _nodes = [];
    private Dictionary<string, Vector3> _tips = [];
    // 后台吸附解算：同一时间只有一个；_solveOp 是它属于的那次操作（操作取消、换了就丢掉结果）
    private Task<(EditPose Pose, List<(Vector3 Point, float Signed)>? Heat)>? _solveTask;
    private object? _solveOp;
    private bool _solveQuick;
    private Dictionary<string, Contact> _solveContacts = [];
    private EditPose? _pose;            // 当前姿势（Update 整个换掉，Draw 只读）
    private EditPose? _original;        // 进入编辑前的姿势（取消时还原）
    private readonly HashSet<string> _touched = [];   // 写过的骨骼（取消时只还原这些）
    private readonly HashSet<string> _pinned = [];    // 钉住的手脚（节点 id）
    private readonly List<EditPose> _undo = [];
    private readonly List<EditPose> _redo = [];
    private readonly Dictionary<string, Quaternion> _partialFix = [];   // 脸部骨架骨骼写入时的校正（见 CalibrateGaze）
    private Vector3? _gaze;             // 视线点（模型空间）
    private Vector3 _modelPos;
    private Quaternion _modelRot = Quaternion.Identity;
    private Vector3 _modelScale = Vector3.One;

    private Op? _op;                    // 正在进行的操作（Draw 建、Update 解；_gate 保护字段）
    private EditPose? _writePose;       // 待写进游戏的姿势和骨骼
    private HashSet<string>? _writeBones;
    private bool _writeRaw;             // 写的时候不加脸部校正（校准用）
    private int _resync = -1;           // >0：倒数几帧后从 Brio 重读姿势
    private volatile bool _textInput;   // 游戏的输入框（聊天等）正在输入：不抢键盘

    public bool Active => _pose is not null;

    /// <summary>出错或提示（界面显示）；正常为 null。</summary>
    public string? Status { get; private set; }

    public bool Symmetric { get; set; }
    public bool ClampLimits { get; set; }
    public bool ShowAllFingers { get; set; }

    /// <summary>进入编辑：读角色当前姿势和骨架。返回错误信息，成功为 null。</summary>
    public Task<string?> StartAsync(int? actor) => framework.RunOnFrameworkThread(() =>
    {
        if (!clientState.IsGPosing) return Loc.T("进 GPose 后才能在游戏里编辑");
        if (applier.Resolve(actor) is not { } a) return Loc.T("找不到角色");
        if (brio.GetPose(a) is not { } json) return Loc.T("读不到角色的姿势（需要 Brio）");
        if (ReadRig(a) is not { } rig) return Loc.T("读不到角色的骨架");
        if (EditPose.Parse(json, rig.Parents) is not { } pose) return Loc.T("读不到角色的姿势（需要 Brio）");
        // Customize+ 这类插件在动画之后改骨骼（只改骨骼自己），Brio 读到的姿势里没有：按游戏渲染用的姿势逐骨补上蒙皮修正，
        // 不然手部吸附算出来的身体、手和游戏里画出来的差几厘米
        var (skinFix, maxOffset) = SkinFix(pose, ReadRenderPose(a));
        if (skinFix.Count > 0) rig = rig.WithSkinFix(skinFix);
        log.Debug("Posegraph editor: skin fix on {Count} bones, max offset {Offset:0.0}cm", skinFix.Count, maxOffset * 100f);
        var nodes = PoseNodes.Build(pose);
        if (!nodes.Any(n => n.Kind == NodeKind.Limb)) return Loc.T("角色骨架缺少手臂骨骼");
        if (!ReadModel(a)) return Loc.T("读不到角色的位置");
        lock (_gate)
        {
            _actor = actor;
            _rig = rig;
            _nodes = nodes;
            _tips = PoseNodes.Tips(pose, nodes);
            _original = pose;
            _pose = pose;
            _touched.Clear();
            _pinned.Clear();
            _pinned.UnionWith(nodes.Where(n => n.Kind == NodeKind.Limb && n.Id.StartsWith("foot_", StringComparison.Ordinal)).Select(n => n.Id));
            _undo.Clear();
            _redo.Clear();
            _partialFix.Clear();
            _op = null;
            _writePose = null;
            _writeBones = null;
            _resync = -1;
            _selected = null;
            _press = null;
            Status = null;
            Gaze = null;
            ResetGaze(pose);
            LoadMeshes(a);
        }
        _direct.Invalidate();
        return null;
    });

    /// <summary>
    /// 上次编辑结束时的视线：拖过视线时为两只眼球相对头部的旋转（四元数 xyzw），存进修正用（RetargetParams.Gaze）；没拖过为 null。
    /// </summary>
    public Dictionary<string, float[]>? Gaze { get; private set; }

    /// <summary>完成：姿势留在游戏里，退出编辑（还没写进去的最后一下照样写完）。</summary>
    public void Finish()
    {
        lock (_gate)
        {
            Gaze = _pose is { } pose && PoseNodes.Eyes.Any(_touched.Contains) && pose.Has(PoseNodes.Head)
                ? PoseNodes.Eyes.Where(pose.Has).ToDictionary(e => e, e =>
                {
                    var q = Quaternion.Normalize(Quaternion.Conjugate(pose.Rotation(PoseNodes.Head)) * pose.Rotation(e));
                    return new[] { q.X, q.Y, q.Z, q.W };
                })
                : null;
            _pose = null;
            _op = null;
            _selected = null;
            _press = null;
        }
    }

    /// <summary>取消：还原进入编辑前的姿势，退出编辑。</summary>
    public void Cancel()
    {
        EditPose? original;
        int? actor;
        HashSet<string> touched;
        Dictionary<string, Quaternion> fix;
        lock (_gate)
        {
            original = _original;
            actor = _actor;
            touched = [.. _touched];
            fix = new Dictionary<string, Quaternion>(_partialFix);
            Gaze = null;
            _writePose = null;
            _writeBones = null;
            _pose = null;
            _op = null;
            _selected = null;
            _press = null;
        }
        if (original is null || touched.Count == 0) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (applier.Resolve(actor) is not { } a) return;
            // 改过的骨骼清掉增量后按原姿势整套（位置、旋转）写回
            if (_direct.Apply(a, original.ToJson(touched, fix), touched) is null) brio.ApplyPose(a, original.ToJson());
        });
    }

    // ------------------------------------------------------------------ 游戏主线程

    /// <summary>每帧（Framework.Update）：执行界面上的操作、解算、把姿势写进游戏。</summary>
    public void Update(IFramework _)
    {
        if (_pose is null)
        {
            // 刚点了「完成」：还没写进去的最后一下写完
            if (_writePose is not null && clientState.IsGPosing && applier.Resolve(_actor) is { } last) Write(last);
            return;
        }
        if (!clientState.IsGPosing)
        {
            Finish();
            return;
        }
        if (applier.Resolve(_actor) is not { } actor)
        {
            Status = Loc.T("找不到角色");
            return;
        }
        ReadModel(actor);
        _textInput = TextInputActive();
        if (_grab >= 1 && !_textInput) _keys.Block(EditorKeys.SelectionKeys);
        if (_grab == 2 && !_textInput) _keys.Block(EditorKeys.OperationKeys);

        while (_actions.TryDequeue(out var action)) action(actor);
        StepOp(actor);
        if (Write(actor)) return;

        // 操作结束、写完后从 Brio 读回来：游戏里的骨长（体型、Customize+）和我们算的可能差一点，节点以游戏为准
        if (_resync > 0 && --_resync == 0)
            lock (_gate)
                if (_op is null && _rig is { } rig && brio.GetPose(actor) is { } json && EditPose.Parse(json, rig.Parents) is { } fresh
                    && _nodes.All(n => fresh.Has(n.Bone)))
                {
                    _pose = fresh;
                    ResetGaze(fresh);
                }
    }

    /// <summary>推进当前操作：视线先校准，然后按 Draw 记下的目标解算，结束时记撤销。</summary>
    private void StepOp(IGameObject actor)
    {
        lock (_gate)
        {
            if (_op is not { } op || _rig is not { } rig) return;
            if (op.Pins is null)
            {
                // 操作开始：钉住的手脚记下此刻的位置和朝向
                op.Pins = _pinned.Select(id => _nodes.FirstOrDefault(n => n.Id == id)).OfType<Node>()
                                 .ToDictionary(n => n.Id, n => _surfacePins.TryGetValue(n.Id, out var surfacePin)
                                                   ? surfacePin
                                                   : new PoseNodes.Pin(op.Start.Position(n.Chain[2]), op.Start.Rotation(n.Chain[2])));
                if (op.Surface is null)
                {
                    RefreshOthers(actor);   // 别人挪过 / 改过姿势就重新蒙皮
                    op.Surface = BuildSurface(op.Node, op.Start);
                }
            }
            if (op.Cancelled)
            {
                Commit(op.Start, op.Write);
                _op = null;
                _resync = ResyncDelayFrames;
                return;
            }
            if (op.Node.Kind == NodeKind.Gaze && !CalibrateGaze(actor, op)) return;
            // 手部吸附：拖动中每帧用快速版（手指不合拢），松手时再完整算一次
            var surface = op.ForceSnap || Snap != op.Alt ? op.Surface : null;
            // 吸附要几十到上百毫秒：放到后台线程算，主线程只提交算好的结果（游戏不卡）；同一时间只算一个，算完再按最新的目标算下一个
            if (_solveTask is { IsCompleted: true } done)
            {
                var (solvedOp, solvedQuick, contacts) = (_solveOp, _solveQuick, _solveContacts);
                _solveTask = null;
                _solveOp = null;
                if (ReferenceEquals(solvedOp, op))
                {
                    if (done.IsCompletedSuccessfully)
                    {
                        var (pose, heat) = done.Result;
                        foreach (var (id, c) in contacts) _contacts[id] = c;
                        if (!contacts.ContainsKey(op.Node.Id)) _contacts.Remove(op.Node.Id);
                        if (!solvedQuick) _heat = heat is null ? null : (op.Node.Id, heat);
                        // 贴得不理想（够不着、手掌贴不平、胳膊会压进身体）时在状态栏说明原因
                        if (!solvedQuick && HandContact.LastIssue is { } issue) Status = Loc.T(issue);
                        op.QuickSolved = solvedQuick;
                        pose.CollectChanged(op.Start, op.Write, RootSet);
                        Commit(pose, op.Write);
                    }
                    else
                    {
                        log.Error(done.Exception?.GetBaseException(), "hand snap solve failed");
                        op.QuickSolved = false;
                    }
                }
            }
            var final = op.Confirmed && surface is not null && op.QuickSolved;
            if ((op.Dirty || final) && _solveTask is null)
            {
                op.Dirty = false;
                var quick = surface is not null && !op.Confirmed;
                if (surface is null)
                {
                    var options = new PoseNodes.Options(ClampLimits, Symmetric, null, _contacts, _hands, false, Fit, op.Region, op.Focus);
                    var pose = PoseNodes.Solve(op.Start, rig, op.Node, _tips, options, op.Pins, _nodes, op.Target, op.Rotation);
                    op.QuickSolved = false;
                    pose.CollectChanged(op.Start, op.Write, RootSet);
                    Commit(pose, op.Write);
                    if (op.Node.Kind == NodeKind.Gaze && op.Target is { } g) _gaze = g;
                }
                else
                {
                    var contacts = new Dictionary<string, Contact>(_contacts);
                    var options = new PoseNodes.Options(ClampLimits, Symmetric, surface, contacts, _hands, quick, Fit, op.Region, op.Focus);
                    var (start, node, tips, pins, nodes, target, rotation, hands) = (op.Start, op.Node, _tips, op.Pins, _nodes, op.Target, op.Rotation, _hands);
                    _solveOp = op;
                    _solveQuick = quick;
                    _solveContacts = contacts;
                    _solveTask = Task.Run(() =>
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var pose = PoseNodes.Solve(start, rig, node, tips, options, pins, nodes, target, rotation);
                        var solved = watch.Elapsed.TotalMilliseconds;
                        var heat = quick ? null : Heat(node, pose, surface, rig, hands);
                        if (!quick)
                            log.Debug("Posegraph snap: solve {Solve:0}ms heat {Heat:0}ms, surface {Tris} tris, hand {Hand} verts, arm {Arm} verts",
                                      solved, watch.Elapsed.TotalMilliseconds - solved, surface.TriangleCount,
                                      hands?.GetValueOrDefault(node.Side)?.Count ?? 0, hands?.GetValueOrDefault("arm_" + node.Side)?.Count ?? 0);
                        return (pose, heat);
                    });
                }
            }
            // 松手：后台还在算、或者最后那次完整解算还没做完时先等着
            if (op.Confirmed && (_solveTask is not null || op.Dirty || (surface is not null && op.QuickSolved))) return;
            if (op.Confirmed)
            {
                if (op.Write.Count > 0) PushUndo(op.Start);
                RepinAfter(op.Node);
                _op = null;
                _resync = ResyncDelayFrames;
            }
        }
    }

    private static readonly HashSet<string> RootSet = [PoseNodes.Root];

    /// <summary>换成 pose，并把 bones 排进写入（Update 里写）。</summary>
    private void Commit(EditPose pose, HashSet<string> bones)
    {
        _pose = pose;
        _writePose = pose;
        _writeBones = bones;
        _touched.UnionWith(bones);
    }

    /// <summary>直接换成 pose（撤销、镜像……）：局部旋转变了的骨骼都写。</summary>
    private void Replace(EditPose pose)
    {
        if (_pose is not { } current) return;
        var bones = new HashSet<string>();
        pose.CollectChanged(current, bones, RootSet);
        if (bones.Count == 0) return;
        Commit(pose, bones);
        _resync = ResyncDelayFrames;
    }

    private void PushUndo(EditPose before)
    {
        _undo.Add(before);
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>把排着的姿势写进游戏。返回 true 表示这一帧还在等 Brio。</summary>
    private bool Write(IGameObject actor)
    {
        EditPose? pose;
        HashSet<string>? bones;
        bool raw;
        lock (_gate) (pose, bones, raw) = (_writePose, _writeBones, _writeRaw);
        if (pose is null || bones is null) return false;
        var fix = raw ? null : _partialFix;
        string? full = null;
        IEnumerable<string> rotated = bones;
        if (bones.Contains(PoseNodes.Root))
        {
            // 平移、转整个人：根骨骼连位置一起写，其余只写旋转
            full = pose.ToJson([PoseNodes.Root], fix);
            rotated = bones.Where(b => b != PoseNodes.Root);
        }
        switch (_direct.Apply(actor, pose.ToJson(rotated, fix), bones, rotationOnly: true, full))
        {
            case true:
                lock (_gate)
                    if (ReferenceEquals(_writePose, pose))
                    {
                        _writePose = null;
                        _writeBones = null;
                        _writeRaw = false;
                    }
                Status = null;
                return false;
            case false:
                return true;   // Brio 上一次的还没执行：下一帧再写
            default:
                // Brio 内部接口对不上：操作中不能每帧走 IPC（会一闪一闪、越叠越卡），操作结束后整套写一次
                Status = Loc.T("Brio 版本对不上，松手后才会更新到角色身上");
                lock (_gate)
                    if (_op is null)
                    {
                        brio.ApplyPose(actor, pose.ToJson());
                        _writePose = null;
                        _writeBones = null;
                    }
                return false;
        }
    }

    /// <summary>
    /// 眼睛在脸部骨架里。Brio 套脸部骨骼时按「接回头部之前」的姿势算增量，接回头部后会多出一个转动 T（头被改过时不为单位旋转），
    /// 所以按原值写进去，眼睛会转到别处。拖视线前先按当前值写一次、读回实际结果，量出 T，之后写入时先乘上 T 的逆。
    /// 返回 true 表示已校准，可以解算。
    /// </summary>
    private bool CalibrateGaze(IGameObject actor, Op op)
    {
        if (op.Calibration == 2) return true;
        if (_pose is not { } pose) return false;
        var eyes = PoseNodes.Eyes.Where(pose.Has).ToHashSet();
        if (op.Calibration == 0)
        {
            _writePose = pose;
            _writeBones = eyes;
            _writeRaw = true;
            _touched.UnionWith(eyes);
            op.Calibration = 1;
            op.CalibrationFrames = ResyncDelayFrames + 1;
            return false;
        }
        if (_writePose is not null || --op.CalibrationFrames > 0) return false;
        if (_rig is { } rig && brio.GetPose(actor) is { } json && EditPose.Parse(json, rig.Parents) is { } actual)
            foreach (var eye in eyes.Where(actual.Has))
                _partialFix[eye] = Quaternion.Normalize(pose.Rotation(eye) * Quaternion.Conjugate(actual.Rotation(eye)));
        op.Calibration = 2;
        return true;
    }

    /// <summary>视线点：两眼中点往眼睛正前方 0.6 米。</summary>
    private void ResetGaze(EditPose pose)
    {
        if (_rig is not { } rig || !PoseNodes.Eyes.All(pose.Has))
        {
            _gaze = null;
            return;
        }
        var mid = PoseNodes.Eyes.Aggregate(Vector3.Zero, (s, e) => s + pose.Position(e)) / PoseNodes.Eyes.Length;
        var forward = PoseNodes.Eyes.Aggregate(Vector3.Zero, (s, e) => s + PoseOps.Forward(pose, rig, e));
        _gaze = mid + (forward.LengthSquared() > 1e-8f ? Vector3.Normalize(forward) : Vector3.UnitZ) * 0.6f;
    }

    // ------------------------------------------------------------------ 界面上的离散操作（排到主线程执行）

    private void Undo() => _actions.Enqueue(_ =>
    {
        lock (_gate)
        {
            if (_op is not null || _undo.Count == 0 || _pose is not { } current) return;
            var before = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(current);
            Replace(before);
        }
    });

    private void Redo() => _actions.Enqueue(_ =>
    {
        lock (_gate)
        {
            if (_op is not null || _redo.Count == 0 || _pose is not { } current) return;
            var after = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(current);
            Replace(after);
        }
    });

    private void MirrorSelected(Node node) => _actions.Enqueue(_ =>
    {
        lock (_gate)
        {
            if (_op is not null || _pose is not { } current || _rig is not { } rig || node.MirrorGroup is null) return;
            PushUndo(current);
            Replace(PoseNodes.MirrorSide(current, rig, node));
        }
    });

    // ------------------------------------------------------------------ 读游戏

    /// <summary>
    /// 角色模型 → 世界：直接读游戏里的模型变换，缩放要乘上身高系数（和 Brio 画骨骼时一样；Brio IPC 给的缩放里没有身高，
    /// 只用它的话身高越偏离默认值，节点偏得越多）。
    /// </summary>
    private unsafe bool ReadModel(IGameObject actor)
    {
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
        if (go == null) return false;
        var draw = (CharacterBase*)go->DrawObject;
        if (draw == null) return false;
        var obj = &draw->DrawObject.Object;
        _modelPos = new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
        _modelRot = Quaternion.Normalize(new Quaternion(obj->Rotation.X, obj->Rotation.Y, obj->Rotation.Z, obj->Rotation.W));
        // 身高系数：CharacterBase 0x2A0 和 0x2A4 两个 float 相乘（偏移照 Brio 的 BrioCharacterBase）
        var height = *(float*)((byte*)draw + 0x2A0) * *(float*)((byte*)draw + 0x2A4);
        if (!(height > 0.05f && height < 20f)) height = 1f;
        _modelScale = new Vector3(obj->Scale.X, obj->Scale.Y, obj->Scale.Z) * height;
        return true;
    }

    private static unsafe bool TextInputActive()
    {
        var module = RaptureAtkModule.Instance();
        return module != null && module->AtkModule.IsTextInputActive();
    }

    /// <summary>
    /// 骨架：父子关系和绑定姿势（游戏 Havok 骨架：身体、脸、头发等分部骨架；分部骨架的根接到它连着的身体骨骼上，同名骨骼以身体的为准）。
    /// 必须在主线程调用。
    /// </summary>
    private static unsafe PoseRig? ReadRig(IGameObject actor)
    {
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
        var draw = (CharacterBase*)go->DrawObject;
        if (draw == null || draw->Skeleton == null) return null;
        var skeleton = draw->Skeleton;
        var parents = new Dictionary<string, string>();
        var bind = new Dictionary<string, Quaternion>();
        var bindMatrices = new Dictionary<string, Matrix4x4>();
        var partial = new HashSet<string>();
        string?[] body = [];
        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var ps = &skeleton->PartialSkeletons[p];
            var resource = ps->SkeletonResourceHandle;
            if (resource == null || resource->HavokSkeleton == null) continue;
            var rig = IdleBaker.RigOf(resource->HavokSkeleton);
            var names = rig.Names;
            var n = names.Length;
            if (p == 0) body = names;
            // 分部骨架自己的绑定姿势（模型空间）：旋转，以及完整的变换矩阵（行向量约定：子 = 局部 × 父）
            var model = new Quaternion[n];
            var modelMatrix = new Matrix4x4[n];
            for (var i = 0; i < n; i++)
            {
                var parent = rig.Parents[i];
                var r = rig.Reference[i];
                model[i] = Quaternion.Normalize(parent >= 0 && parent < i ? model[parent] * r.Rotation : r.Rotation);
                var local = Matrix4x4.CreateScale(r.Scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(r.Rotation)) * Matrix4x4.CreateTranslation(r.Translation);
                modelMatrix[i] = parent >= 0 && parent < i ? local * modelMatrix[parent] : local;
            }
            var attach = p > 0 && ps->ConnectedParentBoneIndex >= 0 && ps->ConnectedParentBoneIndex < body.Length
                ? body[ps->ConnectedParentBoneIndex] : null;
            // 接到身体上：分部骨架的根和它连着的身体骨骼重合
            var correction = Quaternion.Identity;
            var correctionMatrix = Matrix4x4.Identity;
            if (attach is not null && bind.TryGetValue(attach, out var attachBind))
                for (var i = 0; i < n; i++)
                    if (rig.Parents[i] < 0)
                    {
                        correction = Quaternion.Normalize(attachBind * Quaternion.Conjugate(model[i]));
                        if (bindMatrices.TryGetValue(attach, out var attachMatrix) && Matrix4x4.Invert(modelMatrix[i], out var rootInverse))
                            correctionMatrix = rootInverse * attachMatrix;
                        break;
                    }
            for (var i = 0; i < n; i++)
            {
                if (names[i] is not { Length: > 0 } name || bind.ContainsKey(name)) continue;
                bind[name] = Quaternion.Normalize(correction * model[i]);
                bindMatrices[name] = modelMatrix[i] * correctionMatrix;
                if (p > 0) partial.Add(name);
                var parent = rig.Parents[i];
                var parentName = parent >= 0 && parent < n ? names[parent] : attach;
                if (parentName is { Length: > 0 } pn && pn != name) parents[name] = pn;
            }
        }
        return parents.Count == 0 ? null : new PoseRig(parents, bind, partial, bindMatrices);
    }
}
