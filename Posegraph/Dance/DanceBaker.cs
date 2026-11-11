using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Playback;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Object;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using Posegraph.Idle;
using System.Runtime.InteropServices;

namespace Posegraph.Dance;

/// <summary>
/// 把一整段舞蹈烘焙进原版舞蹈循环的 pap：动画对象整个换成新的非压缩交错动画（时长 = 舞蹈长度），
/// pap 自带的时间轴重建（身体动画时长、表情、音乐，见 TmbEdit.BuildPapTimeline）。和 idle 一样全程用游戏自带的 Havok（见 IdleBaker），必须在游戏主线程调用。
/// 骨骼只换旋转（加上定身体位置的 n_hara 的平移），骨长用这个种族原版的；片段里没有的骨骼（头发、裙摆等）保持原版第 0 帧。
/// </summary>
public static unsafe class DanceBaker
{
    /// <summary>face：整段表情（cfxf_smile 等，null 不加）；sound：音乐的游戏路径（null 不放）；rename：动画改名（开场 pap 从循环 pap 复制来时用）。</summary>
    public static byte[] Bake(byte[] papBytes, byte[] sklbBytes, DanceMotion motion, string tempDir, nint interleavedVtbl,
                              string? face, string? sound, string? rename = null)
    {
        var pap = PapFile.Read(papBytes);
        if (pap.HavokData.Length <= 8) throw new InvalidDataException("pap has no havok animation data");
        if (pap.AnimCount != 1) throw new InvalidDataException($"pap has {pap.AnimCount} animations, expected 1");
        if (rename is not null) pap.RenameAnimation(0, rename);
        var allocs = new List<nint>();
        hkResource* papRes = null, sklbRes = null;
        try
        {
            var anims = IdleBaker.LoadAnimations(pap.HavokData, allocs, out papRes);
            var skelAnims = IdleBaker.LoadAnimations(PapFile.SklbHavok(sklbBytes), allocs, out sklbRes);
            var skeleton = skelAnims->Skeletons.Length == 0 ? null : skelAnims->Skeletons[0].ptr;
            if (skeleton == null) throw new InvalidDataException("sklb contains no skeleton");
            var rig = IdleBaker.RigOf(skeleton);

            // 每帧相对父骨骼的姿势（所有动画共用，只算一次）
            var locals = new Dictionary<string, BoneTransform>[motion.Count];
            for (var f = 0; f < motion.Count; f++) locals[f] = IdleBaker.LocalPose(rig, motion.Pose(f));

            var baked = 0;
            for (var i = 0; i < anims->Bindings.Length; i++)
                if (BakeAnimation(anims, i, skeleton, rig, locals, motion, interleavedVtbl, allocs)) baked++;
            if (baked == 0) throw new InvalidDataException("pap has no animation to replace");

            var havok = IdleBaker.Save(IdleBaker.LoadRoot(papRes), tempDir);
            var pad = ((pap.HavokData.Length % 4) - (havok.Length % 4) + 4) % 4;   // 时间轴部分的 4 字节对齐保持原样
            if (pad != 0) Array.Resize(ref havok, havok.Length + pad);
            pap.HavokData = havok;
            pap.TimelineBytes = TmbEdit.BuildPapTimeline(pap.TimelineBytes, motion.TimelineFrames, pap.AnimationName(0), face, sound);
            return pap.Write();
        }
        finally
        {
            if (papRes != null) ((hkReferencedObject*)papRes)->RemoveReference();
            if (sklbRes != null) ((hkReferencedObject*)sklbRes)->RemoveReference();
            foreach (var p in allocs) Marshal.FreeHGlobal(p);
        }
    }

    private static bool BakeAnimation(hkaAnimationContainer* anims, int index, hkaSkeleton* skeleton, IdleBaker.Rig rig,
                                      Dictionary<string, BoneTransform>[] locals, DanceMotion motion, nint vtbl, List<nint> allocs)
    {
        var binding = anims->Bindings[index].ptr;
        var anim = binding == null ? null : binding->Animation.ptr;
        if (anim == null || binding->BlendHint.Storage != 0) return false;   // 叠加层不动

        var numBones = skeleton->Bones.Length;
        for (var t = 0; t < binding->TransformTrackToBoneIndices.Length; t++)
            if (binding->TransformTrackToBoneIndices[t] >= numBones) throw new IdleBaker.SkeletonMismatchException();
        if (binding->TransformTrackToBoneIndices.Length == 0 && anim->NumberOfTransformTracks > numBones) throw new IdleBaker.SkeletonMismatchException();

        // 原版第 0 帧（骨长、片段里没有的骨骼用它）
        var animated = (hkaAnimatedSkeleton*)IdleBaker.Alloc(sizeof(hkaAnimatedSkeleton), allocs);
        var control = (hkaAnimationControl*)IdleBaker.Alloc(sizeof(hkaAnimationControl), allocs);
        control->Ctor1(binding);
        animated->Ctor1(skeleton);
        animated->addAnimationControl(control);
        control->Weight = 1f;
        control->LocalTime = 0f;
        var numFloatSlots = skeleton->FloatSlots.Length;
        var boneBuf = (hkQsTransformf*)IdleBaker.Alloc(numBones * sizeof(hkQsTransformf), allocs);
        var floatBuf = (float*)IdleBaker.Alloc(Math.Max(1, numFloatSlots) * sizeof(float), allocs);
        animated->sampleAndCombineAnimations(boneBuf, floatBuf);
        animated->removeAnimationControl(control);
        var reference = new BoneTransform[numBones];
        for (var b = 0; b < numBones; b++) reference[b] = IdleBaker.FromHk(boneBuf[b]);

        var trackCount = anim->NumberOfTransformTracks;
        var floatTrackCount = anim->NumberOfFloatTracks;
        var mapLength = binding->TransformTrackToBoneIndices.Length;
        var frames = motion.Count;
        var transforms = (hkQsTransformf*)IdleBaker.Alloc(frames * trackCount * sizeof(hkQsTransformf), allocs);
        for (var t = 0; t < trackCount; t++)
        {
            var bone = mapLength == 0 ? (short)t : t < mapLength ? binding->TransformTrackToBoneIndices[t] : (short)-1;
            if (bone < 0 || bone >= numBones)
            {
                for (var f = 0; f < frames; f++) transforms[f * trackCount + t] = IdleBaker.ToHk(BoneTransform.Identity);
                continue;
            }
            var vanilla = reference[bone];
            var name = rig.Names[bone];
            if (name is null || IdleBaker.SkipBones.Contains(name) || !locals[0].ContainsKey(name))
            {
                var still = IdleBaker.ToHk(vanilla);
                for (var f = 0; f < frames; f++) transforms[f * trackCount + t] = still;
                continue;
            }
            var withTranslation = name == IdleBaker.BodyBone;
            var previous = default(System.Numerics.Quaternion);
            for (var f = 0; f < frames; f++)
            {
                var l = locals[f].TryGetValue(name, out var v) ? v : vanilla;
                var rot = f == 0 ? l.Rotation : IdleMath.AlignSign(previous, l.Rotation);
                previous = rot;
                transforms[f * trackCount + t] = IdleBaker.ToHk(new BoneTransform(withTranslation ? l.Translation : vanilla.Translation, rot, vanilla.Scale));
            }
        }

        // 浮点轨道：保持原版第 0 帧
        float* floats = null;
        if (floatTrackCount > 0)
        {
            floats = (float*)IdleBaker.Alloc(frames * floatTrackCount * sizeof(float), allocs);
            var explicitFloats = binding->FloatTrackToFloatSlotIndices.Length > 0;
            for (var ft = 0; ft < floatTrackCount; ft++)
            {
                var slot = explicitFloats ? binding->FloatTrackToFloatSlotIndices[ft] : (short)ft;
                var value = slot >= 0 && slot < numFloatSlots ? floatBuf[slot] : 0f;
                for (var f = 0; f < frames; f++) floats[f * floatTrackCount + ft] = value;
            }
        }

        var newAnim = (IdleBaker.InterleavedAnimation*)IdleBaker.Alloc(sizeof(IdleBaker.InterleavedAnimation), allocs);
        *newAnim = default;
        ((IdleBaker.BaseObject*)newAnim)->Vfptr = (void*)vtbl;
        ((hkReferencedObject*)newAnim)->MemSizeAndRefCount = 0;   // 不是 Havok 堆上的，Havok 不会去释放
        newAnim->Animation.Type = hkaAnimation.AnimationType.InterleavedAnimation;
        newAnim->Animation.Duration = motion.TimelineFrames / DanceMotion.Fps;
        newAnim->Animation.NumberOfTransformTracks = trackCount;
        newAnim->Animation.NumberOfFloatTracks = floatTrackCount;
        // 不带原版的根运动（时长对不上；身体位置已经在 n_hara 里）；注释轨道（按轨道的名字标记）原样带过去
        newAnim->Animation.AnnotationTracks = anim->AnnotationTracks;
        newAnim->Transforms = IdleBaker.MakeArray(transforms, frames * trackCount);
        newAnim->Floats = floats == null ? IdleBaker.MakeArray<float>(null, 0) : IdleBaker.MakeArray(floats, frames * floatTrackCount);

        binding->Animation.ptr = (hkaAnimation*)newAnim;
        anims->Animations.Data[index].ptr = (hkaAnimation*)newAnim;
        return true;
    }
}
