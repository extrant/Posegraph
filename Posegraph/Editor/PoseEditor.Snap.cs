using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Posegraph.Idle;

namespace Posegraph.Editor;

/// <summary>
/// 姿势编辑器的手部吸附：进入编辑时在后台读入自己和 GPose 里其他角色的模型网格；
/// 拖手 / 手指开始时建一次表面（自己的身体按当时的姿势蒙皮、去掉正在拖的那条胳膊；别人按他们现在的姿势），
/// 解算时手掌贴表面、手指顺着弯、钻进去的退出来（HandContact）。P 键在手贴着表面时把手钉在表面上：
/// 贴的是自己的身体就钉在那块表面跟着动的骨骼上（转胸、弯腰时手跟着走），贴的是别人就钉在原地。
/// </summary>
public sealed partial class PoseEditor
{
    private const float SurfaceRange = 1.2f;   // 只要离这条胳膊的肩膀这么近的表面（手够不着的不要，建表面快一些）

    /// <summary>手部吸附开关（面板里的勾选框；拖动时按住 Alt 临时反转）。</summary>
    public bool Snap { get; set; }

    private volatile List<BodyMesh.Part>? _selfParts;            // 自己的模型（后台读完前为 null）
    private volatile List<Surface.Piece>? _otherPieces;          // 别人的身体表面（已经蒙皮、换到自己的模型空间）
    private Dictionary<int, float> _chestLines = [];             // 别人的胸线（换到自己模型空间的高度），「贴到部位」分胸 / 腹 / 肩用
    // 别人的表面按角色分开存，带「位置 + 几根关键骨骼」的签名：开始拖手 / 选部位时签名变了（挪过、改过姿势）就重新蒙皮
    private Dictionary<nint, (int Index, Vector3 Signature, List<Surface.Piece> Pieces)> _others = [];
    private volatile Dictionary<string, HandShape>? _hands;      // 自己两只手的形状（手套 / 手模型里的手部顶点）
    private volatile string? _meshStatus;
    private int _meshGeneration;                                 // 重新进入编辑后，之前没读完的后台任务作废
    private readonly Dictionary<string, Contact> _contacts = [];  // 每只手最后贴着的表面
    private readonly Dictionary<string, PoseNodes.Pin> _surfacePins = [];   // 钉在表面上的手（相对骨骼，或别人身上的固定位置）

    /// <summary>进入编辑时调用（主线程）：记下要读的模型、别人的姿势和位置，后台读。</summary>
    private void LoadMeshes(IGameObject self)
    {
        var generation = ++_meshGeneration;
        _selfParts = null;
        _otherPieces = null;
        _hands = null;
        var structure = _pose;
        _contacts.Clear();
        _surfacePins.Clear();
        _meshStatus = Loc.T("正在读入身体网格…");
        var selfFiles = BodyMesh.ModelFiles(self);
        if (!ModelMatrix(self, out var selfMatrix) || !Matrix4x4.Invert(selfMatrix, out var worldToSelf))
        {
            _meshStatus = Loc.T("读不到角色的位置");
            return;
        }
        // 别人：模型、骨架、现在的姿势、模型 → 自己模型空间（都要在主线程读）
        var others = new List<(int Actor, List<(string, uint)> Files, PoseRig Rig, EditPose Pose, Matrix4x4 ToSelf)>();
        var chestLines = new Dictionary<int, float>();
        _chestLines = chestLines;
        var addresses = new Dictionary<int, (nint Address, Vector3 Signature)>();
        var index = 1;
        foreach (var other in brio.GetActors())
        {
            if (other.Address == self.Address) continue;
            if (ReadRig(other) is not { } rig || brio.GetPose(other) is not { } json || EditPose.Parse(json, rig.Parents) is not { } pose
                || !ModelMatrix(other, out var m)) continue;
            // 别人身上也可能有 Customize+：按渲染姿势补上
            var (otherFix, _) = SkinFix(pose, ReadRenderPose(other));
            var toSelf = m * worldToSelf;
            // 别人的胸线：他的 j_sebo_b、j_sebo_c 中点换到自己的模型空间（以前别人只按骨骼分部位，男性的胸肌会被归成腹、胸肌外侧归成肩）
            if (pose.Has("j_sebo_b") && pose.Has("j_sebo_c"))
                chestLines[index] = Vector3.Transform((pose.Position("j_sebo_b") + pose.Position("j_sebo_c")) / 2f, toSelf).Y;
            others.Add((index, BodyMesh.ModelFiles(other), otherFix.Count > 0 ? rig.WithSkinFix(otherFix) : rig, pose, toSelf));
            addresses[index] = (other.Address, Signature(pose, toSelf));
            index++;
        }
        _ = Task.Run(() =>
        {
            // 低模代理网格（游戏里看不见的影子 / 占位模型）不当身体表面
            var parts = selfFiles.Select(f => BodyMesh.Load(data, f.Path, f.Attributes, log)).OfType<BodyMesh.Part>().Where(p => !BodyMesh.IsProxy(p)).ToList();
            var byActor = new Dictionary<nint, (int, Vector3, List<Surface.Piece>)>();
            foreach (var o in others)
                byActor[addresses[o.Actor].Address] = (o.Actor, addresses[o.Actor].Signature, SkinOther(o.Files, o.Pose, o.Rig, o.ToSelf, o.Actor));
            var pieces = byActor.Values.SelectMany(v => v.Item3).ToList();
            var hands = new Dictionary<string, HandShape>();
            if (structure is not null)
                foreach (var side in new[] { "l", "r" })
                {
                    if (HandShape.From(parts, structure, side) is { } hand) hands[side] = hand;
                    if (HandShape.Arm(parts, structure, side) is { } arm) hands["arm_" + side] = arm;
                }
            if (generation != _meshGeneration) return;
            _hands = hands;
            _selfParts = parts;
            _otherPieces = pieces;
            _others = byActor;
            var triangles = parts.Sum(p => p.Triangles.Length / 3) + pieces.Sum(p => p.Triangles.Length / 3);
            _meshStatus = parts.Count == 0 ? Loc.T("读不到身体网格，手部吸附用不了")
                          : Loc.F("身体网格：自己 {0} 个模型，另外 {1} 个角色，共 {2} 个三角形", parts.Count, others.Count, triangles);
        });
    }

    /// <summary>别人的模型按他的姿势蒙皮、换到自己的模型空间（低模代理网格不要）。</summary>
    private List<Surface.Piece> SkinOther(List<(string Path, uint Attributes)> files, EditPose pose, PoseRig rig, Matrix4x4 toSelf, int actor)
    {
        var pieces = new List<Surface.Piece>();
        foreach (var f in files)
            if (BodyMesh.Load(data, f.Path, f.Attributes, log) is { } part && !BodyMesh.IsProxy(part)
                && BodyMesh.Skin(part, pose, rig, toSelf, actor) is { } piece)
                pieces.Add(piece);
        return pieces;
    }

    /// <summary>别人现在在哪、什么姿势的指纹：腰、头、双手换到自己模型空间后加起来（挪动或改姿势都会变）。</summary>
    private static Vector3 Signature(EditPose pose, Matrix4x4 toSelf)
    {
        var sum = Vector3.Zero;
        foreach (var bone in new[] { "j_kosi", "j_kao", "j_te_l", "j_te_r", "j_asi_d_l", "j_asi_d_r" })
            if (pose.Has(bone)) sum += Vector3.Transform(pose.Position(bone), toSelf);
        return sum;
    }

    /// <summary>
    /// 开始拖手 / 选部位时（主线程）：别人挪过或改过姿势（指纹变了超过 5mm）就按现在的样子重新蒙皮——
    /// 以前别人的表面只在进编辑时读一次，之后挪动第二个角色，手就会贴到他原来的位置上。模型文件有缓存，只重做蒙皮。
    /// </summary>
    private void RefreshOthers(IGameObject self)
    {
        if (_otherPieces is null || !ModelMatrix(self, out var selfMatrix) || !Matrix4x4.Invert(selfMatrix, out var worldToSelf)) return;
        var changed = false;
        var next = new Dictionary<nint, (int Index, Vector3 Signature, List<Surface.Piece> Pieces)>(_others);
        foreach (var other in brio.GetActors())
        {
            if (other.Address == self.Address || !next.TryGetValue(other.Address, out var known)) continue;
            if (ReadRig(other) is not { } rig || brio.GetPose(other) is not { } json || EditPose.Parse(json, rig.Parents) is not { } pose
                || !ModelMatrix(other, out var m)) continue;
            var toSelf = m * worldToSelf;
            var signature = Signature(pose, toSelf);
            if (Vector3.Distance(signature, known.Signature) < 0.005f) continue;
            var (fix, _) = SkinFix(pose, ReadRenderPose(other));
            next[other.Address] = (known.Index, signature, SkinOther(BodyMesh.ModelFiles(other), pose, fix.Count > 0 ? rig.WithSkinFix(fix) : rig, toSelf, known.Index));
            if (pose.Has("j_sebo_b") && pose.Has("j_sebo_c"))
                _chestLines[known.Index] = Vector3.Transform((pose.Position("j_sebo_b") + pose.Position("j_sebo_c")) / 2f, toSelf).Y;
            changed = true;
        }
        if (!changed) return;
        _others = next;
        _otherPieces = next.Values.SelectMany(v => v.Pieces).ToList();
    }

    /// <summary>
    /// 这次操作用的表面：拖手或手指、并且网格读完了才建。自己的身体按 start 蒙皮、去掉这条胳膊（手不会吸到自己身上）；
    /// 只要肩膀 1.2 米以内的三角形。
    /// </summary>
    private Surface? BuildSurface(Node node, EditPose start)
    {
        if (node.Kind is not (NodeKind.Limb or NodeKind.Finger) || node.Side == "" || _rig is not { } rig
            || _selfParts is not { } selfParts || _otherPieces is not { } others) return null;
        if (node.Kind == NodeKind.Limb && !node.Bone.StartsWith("j_te", StringComparison.Ordinal)) return null;
        var arm = start.Subtree($"j_ude_a_{node.Side}");
        var shoulder = start.Has($"j_ude_a_{node.Side}") ? start.Position($"j_ude_a_{node.Side}") : Vector3.Zero;
        Surface.Piece Near(Surface.Piece piece) => piece with
        {
            Keep = t => (piece.Keep?.Invoke(t) ?? true) && Vector3.DistanceSquared(piece.Positions[piece.Triangles[t * 3]], shoulder) < SurfaceRange * SurfaceRange,
        };
        var pieces = selfParts.Select(p => BodyMesh.Skin(p, start, rig, Matrix4x4.Identity, 0, arm)).OfType<Surface.Piece>().Select(Near)
                              .Concat(others.Select(Near));
        var surface = Surface.Build(pieces);
        return surface.TriangleCount > 0 ? surface : null;
    }

    /// <summary>P 键：手贴着表面时钉在表面上，否则钉在原地；已经钉住的取消。</summary>
    private void TogglePinAt(Node node)
    {
        if (node.Kind != NodeKind.Limb) return;
        lock (_gate)
        {
            if (_pinned.Remove(node.Id))
            {
                _surfacePins.Remove(node.Id);
                return;
            }
            _pinned.Add(node.Id);
            if (node.Bone.StartsWith("j_te", StringComparison.Ordinal) && _contacts.TryGetValue(node.Id, out var contact) && _pose is { } pose)
                _surfacePins[node.Id] = SurfacePin(pose, node, contact);
        }
    }

    /// <summary>钉在表面上：贴的是自己的身体就记成相对那块表面所跟的骨骼，贴的是别人就记成原地。</summary>
    private static PoseNodes.Pin SurfacePin(EditPose pose, Node hand, Contact contact)
    {
        var end = hand.Chain[2];
        if (contact.Actor == 0 && pose.Has(contact.Bone))
        {
            var inverse = Quaternion.Conjugate(pose.Rotation(contact.Bone));
            return new PoseNodes.Pin(Vector3.Transform(pose.Position(end) - pose.Position(contact.Bone), inverse),
                                     Quaternion.Normalize(inverse * pose.Rotation(end)), contact.Bone);
        }
        return new PoseNodes.Pin(pose.Position(end), pose.Rotation(end));
    }

    /// <summary>操作结束：钉在表面上的手被直接拖过时，钉到新贴着的地方（不再贴着表面就变成钉在原地）。</summary>
    private void RepinAfter(Node node)
    {
        if (!_surfacePins.ContainsKey(node.Id) || _pose is not { } pose) return;
        if (_contacts.TryGetValue(node.Id, out var contact)) _surfacePins[node.Id] = SurfacePin(pose, node, contact);
        else _surfacePins.Remove(node.Id);
    }

    /// <summary>
    /// 游戏渲染用的身体骨架姿势（模型空间，含 Customize+ 之类插件在动画之后改的缩放 / 位移 / 旋转）。必须在主线程调用。
    /// 只读身体骨架（分部骨架 0）；读不到时 null。
    /// </summary>
    private static unsafe Dictionary<string, BoneTransform>? ReadRenderPose(IGameObject actor)
    {
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
        if (go == null) return null;
        var draw = (CharacterBase*)go->DrawObject;
        if (draw == null || draw->Skeleton == null || draw->Skeleton->PartialSkeletonCount < 1) return null;
        var ps = &draw->Skeleton->PartialSkeletons[0];
        var resource = ps->SkeletonResourceHandle;
        var havokPose = ps->GetHavokPose(0);
        if (resource == null || resource->HavokSkeleton == null || havokPose == null) return null;
        var names = Idle.IdleBaker.RigOf(resource->HavokSkeleton).Names;
        var model = havokPose->GetSyncedPoseModelSpace();
        if (model == null || model->Length < names.Length) return null;
        var result = new Dictionary<string, BoneTransform>();
        for (var i = 0; i < names.Length; i++)
            if (names[i] is { Length: > 0 } name)
                result[name] = Idle.IdleBaker.FromHk(model->Data[i]);
        return result;
    }

    /// <summary>
    /// 每根骨骼的蒙皮修正：渲染姿势 = 修正 × Brio 姿势（行向量约定，修正在骨骼自己的坐标系里）。
    /// 两边几乎一样的骨骼不记（没装 Customize+ 时一根都没有）。返回修正和其中最大的位置差（米，日志用）。
    /// </summary>
    private static (Dictionary<string, Matrix4x4> Fix, float MaxOffset) SkinFix(EditPose brioPose, Dictionary<string, BoneTransform>? render)
    {
        var fix = new Dictionary<string, Matrix4x4>();
        var max = 0f;
        if (render is null) return (fix, 0f);
        static Matrix4x4 M(BoneTransform t) => Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(t.Rotation)) * Matrix4x4.CreateTranslation(t.Translation);
        foreach (var name in brioPose.Names)
        {
            if (!render.TryGetValue(name, out var g) || !Matrix4x4.Invert(M(brioPose[name]), out var inverse)) continue;
            var d = M(g) * inverse;
            var offset = Vector3.Distance(g.Translation, brioPose[name].Translation);
            var scaleDiff = Vector3.Distance(g.Scale, brioPose[name].Scale);
            var turn = 1f - MathF.Abs(Quaternion.Dot(Quaternion.Normalize(g.Rotation), Quaternion.Normalize(brioPose[name].Rotation)));
            if (offset < 0.0005f && scaleDiff < 0.002f && turn < 1e-6f) continue;
            fix[name] = d;
            max = MathF.Max(max, offset);
        }
        return (fix, max);
    }

    /// <summary>角色模型 → 世界（缩放含身高系数）。必须在主线程调用。</summary>
    private static unsafe bool ModelMatrix(IGameObject actor, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.Identity;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
        if (go == null) return false;
        var draw = (CharacterBase*)go->DrawObject;
        if (draw == null) return false;
        var obj = &draw->DrawObject.Object;
        var height = *(float*)((byte*)draw + 0x2A0) * *(float*)((byte*)draw + 0x2A4);
        if (!(height > 0.05f && height < 20f)) height = 1f;
        var scale = new Vector3(obj->Scale.X, obj->Scale.Y, obj->Scale.Z) * height;
        var rotation = Quaternion.Normalize(new Quaternion(obj->Rotation.X, obj->Rotation.Y, obj->Rotation.Z, obj->Rotation.W));
        matrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation)
                 * Matrix4x4.CreateTranslation(new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z));
        return true;
    }
}
