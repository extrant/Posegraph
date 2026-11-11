using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Playback;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Object;
using FFXIVClientStructs.Havok.Common.Base.System.IO.OStream;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;

namespace Posegraph.Idle;

/// <summary>Feet：身体踩在地上（上下都调）；NoSinking：可以浮着但不陷进地里；None：不调。</summary>
public enum Grounding { None, Feet, NoSinking }

/// <summary>
/// 身体放在哪：VanillaBody = 骨盆放在原版动画的位置（坐、躺）；Offset = 用户手动的偏移（模型空间，米：x 左右、y 上下、z 前后）；
/// RotationDeg = 用户手动的整体旋转（度，绕骨盆：x 前后倾、y 转身、z 侧倾），站姿转完再贴地。
/// </summary>
public readonly record struct BodyPlacement(Grounding Grounding, bool VanillaBody, Vector3 Offset, Vector3 RotationDeg = default);

/// <summary>
/// 预览：原版循环逐帧的局部姿势（Frames[帧][骨骼]），和我们的姿势（已放好身体位置，不含用户偏移；姿势里没有的骨骼为 null）。
/// 界面按当前的分区强度、整体旋转实时算出每一帧（和烘焙同一个公式），画侧视 / 正视动画。
/// Planted：腿部骨骼；Grounded：站姿（转完要重新贴地）。
/// </summary>
public sealed record IdlePreview(string[] Names, short[] Parents, BoneTransform[][] Frames, BoneTransform?[] Pose, float Fps, bool[] Planted, bool Grounded)
{
    /// <summary>原版第 f 帧的模型空间骨骼位置（米，角色朝 +z）。</summary>
    public Vector3[] Vanilla(int f, float amplify = 1f)
    {
        if (amplify == 1f) return [.. IdleMath.ToModel(Frames[f], Parents).Select(t => t.Translation)];
        var locals = new BoneTransform[Names.Length];
        for (var i = 0; i < locals.Length; i++) locals[i] = IdleMath.Bake(Frames[0][i], Frames[0][i], Frames[f][i], false, amplify);
        return [.. IdleMath.ToModel(locals, Parents).Select(t => t.Translation)];
    }

    /// <summary>生成后第 f 帧的模型空间骨骼位置；amplify 只放大预览（看清小幅度的呼吸）；rotationDeg：整体旋转（同烘焙）。</summary>
    public Vector3[] Ours(int f, IdleBreath breath, float amplify, Vector3 rotationDeg)
    {
        var pose = rotationDeg == Vector3.Zero ? Pose : Rotated(rotationDeg);
        var locals = new BoneTransform[Names.Length];
        for (var b = 0; b < locals.Length; b++)
        {
            var reference = Frames[0][b];
            locals[b] = Names[b] is "n_root" or "n_throw"
                ? reference
                : IdleMath.Bake(pose[b] ?? reference, reference, Frames[f][b], Names[b] == "n_hara" && pose[b] is not null, breath.Weight(Names[b]) * amplify);
        }
        return [.. IdleMath.ToModel(locals, Parents).Select(t => t.Translation)];
    }

    // 绕骨盆转，站姿再按转完的姿势重新贴地（和 IdleBaker 先转再放身体的结果一样：贴地的位移和身体高度是线性的）
    private BoneTransform?[] Rotated(Vector3 rotationDeg)
    {
        var pose = (BoneTransform?[])Pose.Clone();
        var body = Array.IndexOf(Names, "n_hara");
        if (body < 0) return pose;
        var model = IdleMath.ToModel(Frames[0], Parents);
        var parent = Parents[body] >= 0 ? model[Parents[body]].Rotation : Quaternion.Identity;
        var rotated = IdleMath.RotateInModel(pose[body] ?? Frames[0][body], parent, IdleMath.BodyRotation(rotationDeg));
        pose[body] = rotated;
        if (Grounded)
        {
            var locals = new BoneTransform[Names.Length];
            for (var b = 0; b < locals.Length; b++)
                locals[b] = pose[b] is { } p && Names[b] is not ("n_root" or "n_throw")
                    ? Frames[0][b] with { Rotation = p.Rotation, Translation = b == body ? p.Translation : Frames[0][b].Translation }
                    : Frames[0][b];
            var shift = IdleMath.GroundShift(locals, Frames[0], Parents, Planted);
            pose[body] = rotated with { Translation = rotated.Translation + Vector3.Transform(new Vector3(0f, shift, 0f), Quaternion.Inverse(parent)) };
        }
        return pose;
    }
}

/// <summary>
/// 把姿势烘焙进原版 idle 的 pap：全程用游戏自带的 Havok（VFXEditor 的做法），不需要 Havok SDK。
/// 循环：第 0 帧是姿势，每帧再叠上原版相对第 0 帧的增量（保留呼吸起伏）。
/// 过渡（_start）：原版过渡照常播，同时把修正逐渐加进去，最后一帧落在姿势上。
/// 只改旋转（加上定身体位置的 n_hara 的平移），骨长用这个种族原版的。移植自 ReAnimate（MIT，Copyright (c) 2026 Nyaughty），
/// 区别：姿势是后端按这个种族骨架重定向好的模型空间姿势（不读角色当前姿势），骨架用这个种族的原版 sklb。必须在游戏主线程调用。
/// </summary>
public static unsafe class IdleBaker
{
    private const float Fps = 30f;

    // n_root 会挪动整个角色；n_throw 会弄坏姿势（Brio 也跳过它）
    internal static readonly string[] SkipBones = ["n_root", "n_throw"];

    // 原版所有姿态（站、坐、坐地、打盹）都只靠 n_hara 的平移定身体位置，别的骨骼的平移都是固定骨长
    internal const string BodyBone = "n_hara";

    // 腿（j_asi_a_* 及以下）的末端踩在地上：贴地时和骨盆一起算
    private static readonly string[] PlantedRoots = ["j_asi_a_l", "j_asi_a_r"];

    internal sealed record Rig(string?[] Names, short[] Parents, int[] Depths, bool[] Planted, BoneTransform[] Reference);

    /// <summary>动画引用的骨骼比这个骨架多（被别的模组改过的 idle 要的骨架不对）：硬采样会写坏内存。</summary>
    public sealed class SkeletonMismatchException() : Exception("animation needs bones this skeleton does not have");

    /// <summary>pose：这个种族骨架下的模型空间姿势（Brio 姿势的 Bones）。loopFrame0：循环烘焙时输出第 0 帧的局部姿势，过渡烘焙时传入当终点。</summary>
    public static byte[] Bake(byte[] papBytes, byte[] sklbBytes, IReadOnlyDictionary<string, BoneTransform> pose, bool asStart, BodyPlacement place, IdleBreath breath,
                              string tempDir, nint interleavedVtbl, Dictionary<string, BoneTransform> loopFrame0)
    {
        var pap = PapFile.Read(papBytes);
        if (pap.HavokData.Length <= 8) throw new InvalidDataException("pap has no havok animation data");
        var allocs = new List<nint>();
        hkResource* papRes = null, sklbRes = null;
        try
        {
            var anims = LoadAnimations(pap.HavokData, allocs, out papRes);
            var skelAnims = LoadAnimations(PapFile.SklbHavok(sklbBytes), allocs, out sklbRes);
            var skeleton = skelAnims->Skeletons.Length == 0 ? null : skelAnims->Skeletons[0].ptr;
            if (skeleton == null) throw new InvalidDataException("sklb contains no skeleton");

            var rig = RigOf(skeleton);
            var locals = asStart ? loopFrame0 : RotateBody(LocalPose(rig, pose), rig, place);
            for (var i = 0; i < anims->Bindings.Length; i++)
                BakeAnimation(anims, i, skeleton, locals, rig, asStart, place, breath, interleavedVtbl, allocs, loopFrame0);

            var havok = Save(LoadRoot(papRes), tempDir);
            // 时间轴部分的 4 字节对齐保持原样
            var pad = ((pap.HavokData.Length % 4) - (havok.Length % 4) + 4) % 4;
            if (pad != 0) Array.Resize(ref havok, havok.Length + pad);
            pap.HavokData = havok;
            return pap.Write();
        }
        finally
        {
            if (papRes != null) ((hkReferencedObject*)papRes)->RemoveReference();
            if (sklbRes != null) ((hkReferencedObject*)sklbRes)->RemoveReference();
            foreach (var p in allocs) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>用户的整体旋转：绕骨盆（n_hara）在模型空间里转（父骨骼朝向按绑定姿势）。姿势里没有 n_hara 时按绑定姿势补上。</summary>
    private static Dictionary<string, BoneTransform> RotateBody(Dictionary<string, BoneTransform> locals, Rig rig, in BodyPlacement place)
    {
        if (place.RotationDeg == Vector3.Zero) return locals;
        var body = Array.IndexOf(rig.Names, BodyBone);
        if (body < 0) return locals;
        var p = rig.Parents[body];
        var parent = p >= 0 ? IdleMath.ToModel(rig.Reference, rig.Parents)[p].Rotation : Quaternion.Identity;
        var local = locals.TryGetValue(BodyBone, out var l) ? l : rig.Reference[body];
        locals[BodyBone] = IdleMath.RotateInModel(local, parent, IdleMath.BodyRotation(place.RotationDeg));
        return locals;
    }

    /// <summary>模型空间姿势 → 相对父骨骼。姿势里没有的骨骼按骨架的绑定姿势接在父骨骼上（只用来算子骨骼，不进动画）。</summary>
    internal static Dictionary<string, BoneTransform> LocalPose(Rig rig, IReadOnlyDictionary<string, BoneTransform> pose)
    {
        var n = rig.Names.Length;
        var model = new BoneTransform[n];
        var locals = new Dictionary<string, BoneTransform>(StringComparer.Ordinal);
        for (var b = 0; b < n; b++)
        {
            var p = rig.Parents[b];
            var parent = p >= 0 && p < b ? model[p] : BoneTransform.Identity;
            if (rig.Names[b] is { } name && pose.TryGetValue(name, out var m))
            {
                model[b] = m;
                locals[name] = IdleMath.Relative(parent, m);
            }
            else
            {
                model[b] = IdleMath.Compose(parent, rig.Reference[b]);
            }
        }
        return locals;
    }

    private static void BakeAnimation(hkaAnimationContainer* anims, int index, hkaSkeleton* skeleton, Dictionary<string, BoneTransform> pose, Rig rig,
                                      bool asStart, BodyPlacement place, IdleBreath breath, nint vtbl, List<nint> allocs, Dictionary<string, BoneTransform> loopFrame0)
    {
        var binding = anims->Bindings[index].ptr;
        var anim = binding == null ? null : binding->Animation.ptr;
        if (anim == null) return;
        // 叠加层（idle.pap 里的受击抖动 cbna_add_dmg_f）存的是增量，烤进绝对值会被游戏叠两次
        if (binding->BlendHint.Storage != 0) return;

        var numBones = skeleton->Bones.Length;
        // 轨道指向骨架外的骨骼时 sampleAndCombineAnimations 会写坏内存（VFXEditor 的教训），采样前一定要查
        for (var t = 0; t < binding->TransformTrackToBoneIndices.Length; t++)
            if (binding->TransformTrackToBoneIndices[t] >= numBones) throw new SkeletonMismatchException();
        if (binding->TransformTrackToBoneIndices.Length == 0 && anim->NumberOfTransformTracks > numBones) throw new SkeletonMismatchException();

        var trackCount = anim->NumberOfTransformTracks;
        var floatTrackCount = anim->NumberOfFloatTracks;
        var duration = anim->Duration;
        var frames = Math.Max(1, (int)MathF.Round(duration * Fps) + 1);
        var frameTime = frames > 1 ? duration / (frames - 1) : 0f;

        // 逐帧采样原版，结果按骨骼排（已按绑定换算）
        var animated = (hkaAnimatedSkeleton*)Alloc(sizeof(hkaAnimatedSkeleton), allocs);
        var control = (hkaAnimationControl*)Alloc(sizeof(hkaAnimationControl), allocs);
        control->Ctor1(binding);
        animated->Ctor1(skeleton);
        animated->addAnimationControl(control);
        control->Weight = 1f;
        var numFloatSlots = skeleton->FloatSlots.Length;
        var floatStride = Math.Max(1, numFloatSlots);
        var boneBuf = (hkQsTransformf*)Alloc(numBones * sizeof(hkQsTransformf), allocs);
        var floatBuf = (float*)Alloc(floatStride * sizeof(float), allocs);
        var samples = new hkQsTransformf[frames * numBones];
        var floatSamples = floatTrackCount > 0 ? new float[frames * floatStride] : null;
        for (var f = 0; f < frames; f++)
        {
            control->LocalTime = f * frameTime;
            animated->sampleAndCombineAnimations(boneBuf, floatBuf);
            for (var b = 0; b < numBones; b++) samples[f * numBones + b] = boneBuf[b];
            if (floatSamples is not null)
                for (var s = 0; s < numFloatSlots; s++) floatSamples[f * floatStride + s] = floatBuf[s];
        }
        animated->removeAnimationControl(control);
        var reference = new hkQsTransformf[numBones];
        Array.Copy(samples, reference, numBones);   // 参考帧 = 第 0 帧

        // 轨道 → 骨骼（映射为空表示第 i 条轨道驱动第 i 根骨骼）
        var mapLength = binding->TransformTrackToBoneIndices.Length;
        var trackBone = new short[trackCount];
        for (var t = 0; t < trackCount; t++)
            trackBone[t] = mapLength == 0 ? (short)t : t < mapLength ? binding->TransformTrackToBoneIndices[t] : (short)-1;

        // 哪些轨道换成姿势，以及它们的层级深度范围（过渡缓动用）
        var bakeFor = new BoneTransform?[trackCount];
        int minDepth = int.MaxValue, maxDepth = int.MinValue;
        for (var t = 0; t < trackCount; t++)
        {
            var bone = trackBone[t];
            var name = bone >= 0 ? rig.Names[bone] : null;
            if (name is null || SkipBones.Contains(name) || !pose.TryGetValue(name, out var target)) continue;
            bakeFor[t] = target;
            if (rig.Planted[bone]) continue;
            minDepth = Math.Min(minDepth, rig.Depths[bone]);
            maxDepth = Math.Max(maxDepth, rig.Depths[bone]);
        }
        if (!asStart)
        {
            var bodyTrack = Array.FindIndex(trackBone, b => b >= 0 && rig.Names[b] == BodyBone);
            if (bodyTrack >= 0)
            {
                // 姿势里没有 n_hara 时用原版的（旋转照原版），这样骨盆位置和偏移仍然生效
                var body = bakeFor[bodyTrack] ?? FromHk(reference[trackBone[bodyTrack]]);
                bakeFor[bodyTrack] = body with { Translation = PlaceBody(pose, rig, reference, trackBone[bodyTrack], place) };
            }
        }
        if (minDepth == int.MaxValue) (minDepth, maxDepth) = (0, 0);
        var ease = new IdleMath.StaggeredEase(frames, minDepth, maxDepth);
        if (!asStart) loopFrame0.Clear();

        var transforms = (hkQsTransformf*)Alloc(frames * trackCount * sizeof(hkQsTransformf), allocs);
        for (var t = 0; t < trackCount; t++)
        {
            var bone = trackBone[t];
            if (bone < 0)
            {
                for (var f = 0; f < frames; f++) transforms[f * trackCount + t] = ToHk(BoneTransform.Identity);
                continue;
            }
            if (bakeFor[t] is not { } target)
            {
                // 姿势里没有的骨骼（头发、尾巴、裙摆等）：过渡照原版；循环也照原版，只是幅度按分区强度
                var w = asStart || rig.Names[bone] is "n_root" or "n_throw" ? 1f : breath.Weight(rig.Names[bone]);
                if (w >= 1f)
                    for (var f = 0; f < frames; f++) transforms[f * trackCount + t] = samples[f * numBones + bone];
                else
                    WriteLoopTrack(transforms, t, trackCount, frames, samples, reference, numBones, bone, FromHk(reference[bone]), false, w);
                continue;
            }
            var name = rig.Names[bone]!;
            var withTranslation = name == BodyBone;
            if (asStart)
                WriteStartTrack(transforms, t, trackCount, frames, samples, numBones, bone, target, withTranslation, ease,
                                rig.Planted[bone] ? minDepth : rig.Depths[bone]);
            else
                loopFrame0[name] = WriteLoopTrack(transforms, t, trackCount, frames, samples, reference, numBones, bone, target, withTranslation, breath.Weight(name));
        }

        // 浮点轨道原样保留（idle 里很少有）
        float* floats = null;
        if (floatTrackCount > 0 && floatSamples is not null)
        {
            floats = (float*)Alloc(frames * floatTrackCount * sizeof(float), allocs);
            var explicitFloats = binding->FloatTrackToFloatSlotIndices.Length > 0;
            for (var ft = 0; ft < floatTrackCount; ft++)
            {
                var slot = explicitFloats ? binding->FloatTrackToFloatSlotIndices[ft] : (short)ft;
                for (var f = 0; f < frames; f++)
                    floats[f * floatTrackCount + ft] = slot >= 0 && slot < numFloatSlots ? floatSamples[f * floatStride + slot] : 0f;
            }
        }

        // 用游戏的非压缩交错动画虚表建新的动画对象
        var newAnim = (InterleavedAnimation*)Alloc(sizeof(InterleavedAnimation), allocs);
        *newAnim = default;
        ((BaseObject*)newAnim)->Vfptr = (void*)vtbl;
        ((hkReferencedObject*)newAnim)->MemSizeAndRefCount = 0;   // 不是 Havok 堆上的，Havok 不会去释放
        newAnim->Animation.Type = hkaAnimation.AnimationType.InterleavedAnimation;
        newAnim->Animation.Duration = duration;
        newAnim->Animation.NumberOfTransformTracks = trackCount;
        newAnim->Animation.NumberOfFloatTracks = floatTrackCount;
        newAnim->Animation.ExtractedMotion = anim->ExtractedMotion;
        // 注释轨道是调度用的时间标记（小动作、表情时机），原样带过去
        newAnim->Animation.AnnotationTracks = anim->AnnotationTracks;
        newAnim->Transforms = MakeArray(transforms, frames * trackCount);
        newAnim->Floats = floats == null ? MakeArray<float>(null, 0) : MakeArray(floats, frames * floatTrackCount);

        // 只换指针，旧的样条动画仍归资源所有
        binding->Animation.ptr = (hkaAnimation*)newAnim;
        anims->Animations.Data[index].ptr = (hkaAnimation*)newAnim;
    }

    /// <summary>原版这一帧的局部姿势换上我们的旋转（骨长不变），n_hara 的平移用 body。</summary>
    private static BoneTransform[] PoseLocals(Dictionary<string, BoneTransform> pose, Rig rig, hkQsTransformf[] reference, Vector3 body)
    {
        var n = rig.Names.Length;
        var locals = new BoneTransform[n];
        for (var b = 0; b < n; b++)
        {
            var vanilla = FromHk(reference[b]);
            var name = rig.Names[b];
            locals[b] = name is not null && !SkipBones.Contains(name) && pose.TryGetValue(name, out var p)
                ? vanilla with { Rotation = p.Rotation, Translation = name == BodyBone ? body : vanilla.Translation }
                : vanilla;
        }
        return locals;
    }

    /// <summary>
    /// 身体（n_hara）放在哪，局部平移：
    /// - 站立（Feet）：用姿势自己的位置，再上下挪到最低的腿部骨骼踩在原版的高度；
    /// - 坐、躺（VanillaBody）：骨盆放在原版动画里的位置（游戏按它把人放到椅子上、地上），照片里的位置不准；
    /// 最后加上用户的偏移（模型空间，米）。
    /// </summary>
    private static Vector3 PlaceBody(Dictionary<string, BoneTransform> pose, Rig rig, hkQsTransformf[] reference, int bone, in BodyPlacement place)
    {
        var vanilla = FromHk(reference[bone]).Translation;
        var body = place.VanillaBody || !pose.TryGetValue(BodyBone, out var p) ? vanilla : p.Translation;
        if (!place.VanillaBody && place.Grounding != Grounding.None)
        {
            var refLocals = new BoneTransform[rig.Names.Length];
            for (var b = 0; b < refLocals.Length; b++) refLocals[b] = FromHk(reference[b]);
            var shift = IdleMath.GroundShift(PoseLocals(pose, rig, reference, body), refLocals, rig.Parents, rig.Planted);
            if (place.Grounding == Grounding.Feet || shift > 0f) body += ToParent(pose, rig, reference, bone, body, new Vector3(0f, shift, 0f));
        }
        if (place.Offset != Vector3.Zero) body += ToParent(pose, rig, reference, bone, body, place.Offset);
        return body;
    }

    // 模型空间的位移换到 n_hara 父骨骼的坐标系里
    private static Vector3 ToParent(Dictionary<string, BoneTransform> pose, Rig rig, hkQsTransformf[] reference, int bone, Vector3 body, Vector3 v)
    {
        var parent = rig.Parents[bone];
        if (parent < 0) return v;
        var pm = IdleMath.ToModel(PoseLocals(pose, rig, reference, body), rig.Parents)[parent];
        return Vector3.Transform(v, Quaternion.Inverse(pm.Rotation)) / pm.Scale;
    }

    /// <summary>
    /// 位置预览：原版循环第 0 帧和我们的姿势（不含用户偏移）的模型空间骨骼位置，界面画侧视 / 正视图让用户对齐。
    /// 必须在游戏主线程调用。
    /// </summary>
    public static IdlePreview Preview(byte[] papBytes, byte[] sklbBytes, IReadOnlyDictionary<string, BoneTransform> pose, in BodyPlacement place)
    {
        var pap = PapFile.Read(papBytes);
        var allocs = new List<nint>();
        hkResource* papRes = null, sklbRes = null;
        try
        {
            var anims = LoadAnimations(pap.HavokData, allocs, out papRes);
            var skelAnims = LoadAnimations(PapFile.SklbHavok(sklbBytes), allocs, out sklbRes);
            var skeleton = skelAnims->Skeletons.Length == 0 ? null : skelAnims->Skeletons[0].ptr;
            if (skeleton == null) throw new InvalidDataException("sklb contains no skeleton");
            var rig = RigOf(skeleton);
            var locals = RotateBody(LocalPose(rig, pose), rig, place);
            hkaAnimationBinding* binding = null;
            for (var i = 0; i < anims->Bindings.Length && binding == null; i++)
                if (anims->Bindings[i].ptr is var b && b != null && b->Animation.ptr != null && b->BlendHint.Storage == 0) binding = b;
            if (binding == null) throw new InvalidDataException("pap has no animation");
            var numBones = skeleton->Bones.Length;
            for (var t = 0; t < binding->TransformTrackToBoneIndices.Length; t++)
                if (binding->TransformTrackToBoneIndices[t] >= numBones) throw new SkeletonMismatchException();

            var animated = (hkaAnimatedSkeleton*)Alloc(sizeof(hkaAnimatedSkeleton), allocs);
            var control = (hkaAnimationControl*)Alloc(sizeof(hkaAnimationControl), allocs);
            control->Ctor1(binding);
            animated->Ctor1(skeleton);
            animated->addAnimationControl(control);
            control->Weight = 1f;
            var duration = binding->Animation.ptr->Duration;
            var frames = Math.Max(1, (int)MathF.Round(duration * Fps) + 1);
            var frameTime = frames > 1 ? duration / (frames - 1) : 0f;
            var boneBuf = (hkQsTransformf*)Alloc(numBones * sizeof(hkQsTransformf), allocs);
            var floatBuf = (float*)Alloc(Math.Max(1, skeleton->FloatSlots.Length) * sizeof(float), allocs);
            var sampled = new BoneTransform[frames][];
            var reference = new hkQsTransformf[numBones];
            for (var f = 0; f < frames; f++)
            {
                control->LocalTime = f * frameTime;
                animated->sampleAndCombineAnimations(boneBuf, floatBuf);
                sampled[f] = new BoneTransform[numBones];
                for (var b = 0; b < numBones; b++)
                {
                    sampled[f][b] = FromHk(boneBuf[b]);
                    if (f == 0) reference[b] = boneBuf[b];
                }
            }
            animated->removeAnimationControl(control);

            var bodyBone = Array.IndexOf(rig.Names, BodyBone);
            var body = bodyBone >= 0 ? PlaceBody(locals, rig, reference, bodyBone, place with { Offset = Vector3.Zero }) : Vector3.Zero;
            var ours = new BoneTransform?[numBones];
            for (var b = 0; b < numBones; b++)
                if (rig.Names[b] is { } name && locals.TryGetValue(name, out var p))
                    ours[b] = name == BodyBone ? p with { Translation = body } : p;
            if (bodyBone >= 0 && ours[bodyBone] is null) ours[bodyBone] = sampled[0][bodyBone] with { Translation = body };
            return new IdlePreview([.. rig.Names.Select(n => n ?? "")], rig.Parents, sampled, ours, Fps, rig.Planted,
                                   place.Grounding == Grounding.Feet && !place.VanillaBody);
        }
        finally
        {
            if (papRes != null) ((hkReferencedObject*)papRes)->RemoveReference();
            if (sklbRes != null) ((hkReferencedObject*)sklbRes)->RemoveReference();
            foreach (var p in allocs) Marshal.FreeHGlobal(p);
        }
    }

    private static BoneTransform WriteLoopTrack(hkQsTransformf* transforms, int t, int tracks, int frames, hkQsTransformf[] samples, hkQsTransformf[] reference,
                                                int numBones, short bone, in BoneTransform target, bool withTranslation, float weight)
    {
        var vanillaRef = FromHk(reference[bone]);
        var previous = default(Quaternion);
        var frame0 = default(BoneTransform);
        for (var f = 0; f < frames; f++)
        {
            var baked = IdleMath.Bake(target, vanillaRef, FromHk(samples[f * numBones + bone]), withTranslation, weight);
            baked = baked with { Rotation = f == 0 ? baked.Rotation : IdleMath.AlignSign(previous, baked.Rotation) };
            if (f == 0) frame0 = baked;
            previous = baked.Rotation;
            transforms[f * tracks + t] = ToHk(baked);
        }
        return frame0;
    }

    // 原版过渡的每一帧，修正按这根骨骼自己在原版里动的进度加进去（原版动它时才动一次）；原版不动的骨骼按从根到梢的缓动
    private static void WriteStartTrack(hkQsTransformf* transforms, int t, int tracks, int frames, hkQsTransformf[] samples, int numBones, short bone,
                                        in BoneTransform target, bool withTranslation, in IdleMath.StaggeredEase ease, int depth)
    {
        var final = FromHk(samples[(frames - 1) * numBones + bone]);
        var correction = Quaternion.Normalize(Quaternion.Inverse(final.Rotation) * IdleMath.AlignSign(final.Rotation, target.Rotation));
        var shift = withTranslation ? target.Translation - final.Translation : Vector3.Zero;
        var track = new Quaternion[frames];
        for (var f = 0; f < frames; f++) track[f] = FromHk(samples[f * numBones + bone]).Rotation;
        var progress = IdleMath.ArcProgress(track);
        var previous = default(Quaternion);
        for (var f = 0; f < frames; f++)
        {
            var vanilla = FromHk(samples[f * numBones + bone]);
            var w = progress is not null ? progress[f] : ease.Weight(depth, f);
            var rot = Quaternion.Normalize(vanilla.Rotation * Quaternion.Slerp(Quaternion.Identity, correction, w));
            rot = f == 0 ? rot : IdleMath.AlignSign(previous, rot);
            previous = rot;
            transforms[f * tracks + t] = ToHk(vanilla with { Rotation = rot, Translation = vanilla.Translation + shift * w });
        }
    }

    /// <summary>骨骼名、父骨骼、层级深度、是不是腿、绑定姿势（Havok 骨架父骨骼在前，一遍就能算出深度）。</summary>
    internal static Rig RigOf(hkaSkeleton* skeleton)
    {
        var n = skeleton->Bones.Length;
        var names = new string?[n];
        var parents = new short[n];
        var depths = new int[n];
        var planted = new bool[n];
        var reference = new BoneTransform[n];
        for (short i = 0; i < n; i++)
        {
            var p = i < skeleton->ParentIndices.Length ? skeleton->ParentIndices[i] : (short)-1;
            parents[i] = p;
            depths[i] = p >= 0 && p < i ? depths[p] + 1 : 0;
            names[i] = skeleton->Bones[i].Name.String;
            planted[i] = (p >= 0 && p < i && planted[p]) || (names[i] is { } nm && PlantedRoots.Contains(nm));
            reference[i] = i < skeleton->ReferencePose.Length ? FromHk(skeleton->ReferencePose[i]) : BoneTransform.Identity;
        }
        return new Rig(names, parents, depths, planted, reference);
    }

    internal static hkaAnimationContainer* LoadAnimations(byte[] havok, List<nint> allocs, out hkResource* resource)
    {
        var buffer = Alloc(havok.Length, allocs);
        Marshal.Copy(havok, 0, buffer, havok.Length);
        var options = stackalloc hkSerializeUtil.LoadOptions[1];
        options->TypeInfoRegistry = hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry();
        options->ClassNameRegistry = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry();
        options->Flags = new hkFlags<hkSerializeUtil.LoadOptionBits, int> { Storage = (int)hkSerializeUtil.LoadOptionBits.Default };
        resource = hkSerializeUtil.LoadFromBuffer((void*)buffer, havok.Length, null, options);
        if (resource == null) throw new InvalidDataException("the game's havok runtime could not read the data");
        var anims = (hkaAnimationContainer*)LoadRoot(resource)->findObjectByName("hkaAnimationContainer", null);
        if (anims == null) throw new InvalidDataException("havok data has no animation container");
        return anims;
    }

    internal static hkRootLevelContainer* LoadRoot(hkResource* resource)
    {
        var root = (hkRootLevelContainer*)resource->GetContentsPointer("hkRootLevelContainer", hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry());
        if (root == null) throw new InvalidDataException("havok data has no hkRootLevelContainer");
        return root;
    }

    internal static byte[] Save(hkRootLevelContainer* container, string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, $"havok_{Environment.CurrentManagedThreadId}.hkx");
        var klass = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry()->GetClassByName("hkRootLevelContainer");
        if (klass == null) throw new InvalidOperationException("hkRootLevelContainer class not found in the game's registry");
        var stream = stackalloc hkOstream[1];
        stream->Ctor(path);
        try
        {
            var result = stackalloc hkResult[1];
            var options = new hkSerializeUtil.SaveOptions { Flags = new() { Storage = (int)hkSerializeUtil.SaveOptionBits.Default } };
            hkSerializeUtil.Save(result, container, klass, stream->StreamWriter.ptr, options);
            if (*(int*)result != 0) throw new InvalidOperationException("hkSerializeUtil.Save failed");
        }
        finally
        {
            stream->Dtor();
        }
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        if (bytes.Length <= 8) throw new InvalidOperationException("havok save produced an empty file");
        return bytes;
    }

    internal static nint Alloc(int size, List<nint> allocs)
    {
        var p = Marshal.AllocHGlobal(Math.Max(1, size));
        allocs.Add(p);
        return p;
    }

    // DontDeallocate：Havok 的析构和序列化不会去释放我们自己分配的内存
    internal static hkArray<T> MakeArray<T>(T* data, int count) where T : unmanaged => new()
    {
        Data = data,
        Length = count,
        CapacityAndFlags = count | unchecked((int)hkArray<T>.hkArrayFlags.DontDeallocate),
    };

    internal static BoneTransform FromHk(in hkQsTransformf t) => new(
        new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z),
        new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W),
        new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z));

    internal static hkQsTransformf ToHk(in BoneTransform t) => new()
    {
        Translation = new() { X = t.Translation.X, Y = t.Translation.Y, Z = t.Translation.Z, W = 0f },
        Rotation = new() { X = t.Rotation.X, Y = t.Rotation.Y, Z = t.Rotation.Z, W = t.Rotation.W },
        Scale = new() { X = t.Scale.X, Y = t.Scale.Y, Z = t.Scale.Z, W = 1f },
    };

    /// <summary>hkaInterleavedUncompressedAnimation（FFXIVClientStructs 没有）：hkaAnimation 基类后面跟变换和浮点两个数组（hk2014 的布局）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterleavedAnimation
    {
        public hkaAnimation Animation;
        public hkArray<hkQsTransformf> Transforms;
        public hkArray<float> Floats;
    }

    /// <summary>Havok 对象的第一个字段是虚表指针。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    internal struct BaseObject
    {
        [FieldOffset(0)] public void* Vfptr;
    }
}
