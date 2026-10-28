using System.IO.Compression;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Playback;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using System.Runtime.InteropServices;
using Lumina.Excel.Sheets;

namespace Posegraph;

/// <summary>
/// 开发用：把游戏自带的面部表情动画（GPose「表情」那一套 facial/pose/*、眨眼、说话口型）按帧采样导出，
/// 用来做「识别到的表情 → 脸部骨骼」的换算，免去在游戏里一个个摆表情录采样。
///
/// 做法同 VFXEditor（MIT）：从 .sklb / .pap 里取出 Havok 数据，用游戏自带的 Havok 载入，
/// 建 hkaAnimatedSkeleton + hkaAnimationControl，按时间采样每根骨骼的局部变换。必须在游戏主线程调用。
///
/// 文件布局（2026-10-28 用 Lumina 离线查到）：面部骨架 chara/human/cXXXX/skeleton/face/fYYYY/skl_cXXXXfYYYY.sklb，
/// 同一种族几种脸型共用几套骨架（中原男是 f0002/f0003/f0006/f0008）；动画在 chara/human/cXXXX/animation/fYYYY/ 下：
/// resident/face.pap 是常驻合集（眨眼 cfxb_*、基础表情 cfxf_*、口型 cfxl_*），nonresident/&lt;名字&gt;.pap 每个表情一个文件。
/// 输出：文档\Posegraph\FaceDump\cXXXX_fYYYY.json.gz，每个动画每秒 5 帧，只存动过的骨骼。
/// </summary>
public static class FaceAnimationDump
{
    private const float Fps = 5f;

    private static readonly string[] RaceCodes =
    [
        "c0101", "c0201", "c0301", "c0401", "c0501", "c0601", "c0701", "c0801", "c0901", "c1001",
        "c1101", "c1201", "c1301", "c1401", "c1501", "c1601", "c1701", "c1801",
    ];

    public static string OutputDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Posegraph", "FaceDump");

    /// <summary>逐个面部骨架导出，每个骨架一帧（不卡游戏太久）。log 报告进度。</summary>
    public static async Task RunAsync(IDataManager data, IFramework framework, Action<string> log)
    {
        Directory.CreateDirectory(OutputDir);
        // 动画名：facial 时间轴的最后一段（facial/pose/smile → smile），去重
        var names = data.GetExcelSheet<ActionTimeline>()
            .Select(r => r.Key.ToString())
            .Where(k => k.Contains("facial"))
            .Select(k => k.Split('/')[^1])
            .Distinct()
            .ToList();
        var total = 0;
        foreach (var race in RaceCodes)
            foreach (var id in Enumerable.Range(1, 10).Concat(Enumerable.Range(101, 10)).Concat(Enumerable.Range(201, 10)))
            {
                var face = $"f{id:D4}";
                var sklb = $"chara/human/{race}/skeleton/face/{face}/skl_{race}{face}.sklb";
                if (!data.FileExists(sklb)) continue;
                var dir = $"chara/human/{race}/animation/{face}";
                var paps = new List<string> { $"{dir}/resident/face.pap" };
                paps.AddRange(names.Select(n => $"{dir}/nonresident/{n}.pap"));
                paps = paps.Where(data.FileExists).ToList();
                var result = await framework.RunOnFrameworkThread(() => DumpSkeleton(data, race, face, sklb, paps));
                total += result.Animations;
                log($"{race} {face}：{result.Animations} 段动画{(result.Skipped > 0 ? $"，跳过 {result.Skipped} 段（骨架不匹配或读不出）" : "")}");
            }
        log($"完成，共 {total} 段动画，文件在 {OutputDir}");
    }

    private static unsafe (int Animations, int Skipped) DumpSkeleton(IDataManager data, string race, string face, string sklbPath, List<string> paps)
    {
        var skelBytes = Havok(data.GetFile(sklbPath)!.Data, isSklb: true);
        var skelRes = Load(skelBytes);
        if (skelRes.Container == null) return (0, paps.Count);
        var skeleton = skelRes.Container->Skeletons[0].ptr;
        var boneCount = skeleton->Bones.Length;
        var bones = new string[boneCount];
        var parents = new int[boneCount];
        var reference = new float[boneCount][];
        for (var i = 0; i < boneCount; i++)
        {
            bones[i] = skeleton->Bones[i].Name.String ?? $"bone_{i}";
            parents[i] = skeleton->ParentIndices[i];
            reference[i] = Pack(skeleton->ReferencePose[i]);
        }

        var anims = new Dictionary<string, object>();
        var skipped = 0;
        foreach (var pap in paps)
        {
            var file = data.GetFile(pap)!.Data;
            var infos = PapAnimations(file);
            var res = Load(Havok(file, isSklb: false));
            if (res.Container == null)
            {
                skipped += infos.Count;
                continue;
            }
            foreach (var (name, havokIndex) in infos)
            {
                if (havokIndex < 0 || havokIndex >= res.Container->Bindings.Length)
                {
                    skipped++;
                    continue;
                }
                var binding = res.Container->Bindings[havokIndex].ptr;
                var sampled = Sample(skeleton, binding, reference);
                if (sampled is null)
                {
                    skipped++;
                    continue;
                }
                anims[name] = new
                {
                    file = pap,
                    blend = (int)binding->BlendHint.Value,
                    duration = binding->Animation.ptr->Duration,
                    fps = Fps,
                    bones = sampled.Value.Bones,
                    frames = sampled.Value.Frames,
                };
            }
            res.Release();
        }
        skelRes.Release();

        var path = Path.Combine(OutputDir, $"{race}_{face}.json.gz");
        using var stream = new GZipStream(File.Create(path), CompressionLevel.Optimal);
        JsonSerializer.Serialize(stream, new
        {
            race, face, skeleton = sklbPath, bones, parents, reference,
            note = "每个变换 8 个数：位移 xyz、旋转四元数 xyzw、缩放（取 x）；局部空间（相对父骨骼）。frames[帧][动过的骨骼]，骨骼下标见 anims.*.bones",
            anims,
        });
        return (anims.Count, skipped);
    }

    /// <summary>按 Fps 采样一段动画；只保留动过的骨骼。骨架和动画对不上时返回 null（硬采样会写坏内存）。</summary>
    private static unsafe (int[] Bones, float[][][] Frames)? Sample(hkaSkeleton* skeleton, hkaAnimationBinding* binding, float[][] reference)
    {
        var boneCount = skeleton->Bones.Length;
        for (var i = 0; i < binding->TransformTrackToBoneIndices.Length; i++)
            if (binding->TransformTrackToBoneIndices[i] >= boneCount) return null;
        for (var i = 0; i < binding->FloatTrackToFloatSlotIndices.Length; i++)
            if (binding->FloatTrackToFloatSlotIndices[i] >= skeleton->FloatSlots.Length) return null;

        var animated = (hkaAnimatedSkeleton*)NativeMemory.AllocZeroed((nuint)sizeof(hkaAnimatedSkeleton));
        var control = (hkaAnimationControl*)NativeMemory.AllocZeroed((nuint)sizeof(hkaAnimationControl));
        var transforms = (hkQsTransformf*)NativeMemory.Alloc((nuint)(boneCount * sizeof(hkQsTransformf)));
        var floats = (float*)NativeMemory.Alloc((nuint)(Math.Max(1, skeleton->FloatSlots.Length) * sizeof(float)));
        try
        {
            control->Ctor1(binding);
            animated->Ctor1(skeleton);
            animated->addAnimationControl(control);
            var duration = binding->Animation.ptr->Duration;
            var count = Math.Max(1, (int)MathF.Floor(duration * Fps) + 1);
            var frames = new List<float[][]>();
            var moved = new bool[boneCount];
            for (var f = 0; f < count; f++)
            {
                control->LocalTime = MathF.Min(f / Fps, duration);
                animated->sampleAndCombineAnimations(transforms, floats);
                var frame = new float[boneCount][];
                for (var b = 0; b < boneCount; b++)
                {
                    frame[b] = Pack(transforms[b]);
                    if (!moved[b] && Differs(frame[b], reference[b])) moved[b] = true;
                }
                frames.Add(frame);
            }
            var keep = Enumerable.Range(0, boneCount).Where(b => moved[b]).ToArray();
            return (keep, frames.Select(fr => keep.Select(b => fr[b]).ToArray()).ToArray());
        }
        finally
        {
            // 同 VFXEditor：只摘掉控制器再释放内存，不调析构（析构会让 Havok 去释放我们自己分配的控制器）
            animated->removeAnimationControl(control);
            NativeMemory.Free(animated);
            NativeMemory.Free(control);
            NativeMemory.Free(transforms);
            NativeMemory.Free(floats);
        }
    }

    private static bool Differs(float[] a, float[] b)
    {
        for (var i = 0; i < a.Length; i++)
            if (MathF.Abs(a[i] - b[i]) > 1e-4f) return true;
        return false;
    }

    private static float[] Pack(hkQsTransformf t)
        => [R(t.Translation.X), R(t.Translation.Y), R(t.Translation.Z),
            R(t.Rotation.X), R(t.Rotation.Y), R(t.Rotation.Z), R(t.Rotation.W), R(t.Scale.X)];

    private static float R(float v) => MathF.Round(v, 5);

    /// <summary>PAP 头里的动画表：名字（32 字节）、类型、Havok 动画下标、是否面部。格式同 VFXEditor 的 PapFile。</summary>
    private static List<(string Name, int HavokIndex)> PapAnimations(byte[] pap)
    {
        using var r = new BinaryReader(new MemoryStream(pap));
        r.BaseStream.Position = 8;
        var count = r.ReadInt16();
        r.BaseStream.Position = 0x0E;
        var infoOffset = r.ReadInt32();
        r.BaseStream.Position = infoOffset;
        var list = new List<(string, int)>();
        for (var i = 0; i < count; i++)
        {
            var name = System.Text.Encoding.ASCII.GetString(r.ReadBytes(32)).TrimEnd('\0');
            r.ReadInt16();
            var index = r.ReadInt16();
            r.ReadInt32();
            list.Add((name, index));
        }
        return list;
    }

    /// <summary>取出文件里的 Havok 数据。sklb：版本 1300/1301 在 0x0C 处是 4 字节偏移，1200 在 0x0A 处是 2 字节；
    /// pap：头部 魔数 4、版本 4、动画数 2、模型 2、类型 1、变体 1，之后 0x0E 动画表、0x12 Havok 起点、0x16 Havok 终点（TMB 起点）。</summary>
    private static byte[] Havok(byte[] file, bool isSklb)
    {
        if (!isSklb)
        {
            var start = BitConverter.ToInt32(file, 0x12);
            var end = BitConverter.ToInt32(file, 0x16);
            return file[start..end];
        }
        var version = BitConverter.ToUInt32(file, 4);
        var offset = version is 0x31333030 or 0x31333031 ? BitConverter.ToInt32(file, 0x0C)
                   : version == 0x31323030 ? BitConverter.ToInt16(file, 0x0A)
                   : throw new InvalidDataException($"未知的 sklb 版本 {version:X}");
        return file[offset..];
    }

    private readonly unsafe struct Loaded(hkResource* resource, hkaAnimationContainer* container)
    {
        public readonly hkResource* Resource = resource;
        public readonly hkaAnimationContainer* Container = container;

        public void Release()
        {
            if (Resource != null) Resource->RemoveReference();
        }
    }

    private static unsafe Loaded Load(byte[] havok)
    {
        var options = stackalloc hkSerializeUtil.LoadOptions[1];
        options->TypeInfoRegistry = hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry();
        options->ClassNameRegistry = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry();
        options->Flags = new hkFlags<hkSerializeUtil.LoadOptionBits, int> { Storage = (int)hkSerializeUtil.LoadOptionBits.Default };
        hkResource* resource;
        fixed (byte* p = havok)
            resource = hkSerializeUtil.LoadFromBuffer(p, havok.Length, null, options);
        if (resource == null) return default;
        var root = (hkRootLevelContainer*)resource->GetContentsPointer("hkRootLevelContainer", hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry());
        if (root == null) return new Loaded(resource, null);
        return new Loaded(resource, (hkaAnimationContainer*)root->findObjectByName("hkaAnimationContainer", null));
    }
}
