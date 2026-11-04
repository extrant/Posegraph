using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Posegraph.Idle;

/// <summary>
/// 图片姿势 → idle：每个勾选的种族用后端按它的骨架重定向的姿势（带这张图的修正），烘焙进原版的循环动画（和切进来时的过渡），
/// 写进 Penumbra 模组并在自己的合集里启用。需要 Penumbra。
/// </summary>
public sealed class IdleService
{
    // hkaInterleavedUncompressedAnimation 的虚表：特征码匹配写虚表指针的 mov [rdi], rax，往回 4 字节是前一条 lea 的 rip 相对偏移
    // （VFXEditor、ReAnimate 用的同一个）
    private const string InterleavedVtblSig = "48 89 07 48 8B CD 48 89 77 38";

    private readonly IDataManager _data;
    private readonly IFramework _framework;
    private readonly ISigScanner _sigs;
    private readonly IPluginLog _log;
    private readonly string _tempDir;
    private nint _vtbl;

    public PenumbraIdleMod Penumbra { get; }

    public IdleService(IDalamudPluginInterface pi, IDataManager data, IFramework framework, ISigScanner sigs, IPluginLog log)
    {
        _data = data;
        _framework = framework;
        _sigs = sigs;
        _log = log;
        _tempDir = Path.Combine(pi.GetPluginConfigDirectory(), "idle");
        Penumbra = new PenumbraIdleMod(pi, log);
    }

    private readonly Dictionary<(IdleFamily, string), List<int>> _slots = [];
    private readonly Dictionary<(IdleFamily, int, string), string?> _inherited = [];
    private long _penumbraChecked;
    private bool _penumbraReady;

    /// <summary>这个种族这一类有哪些槽位（查一次记下来，界面每帧问）。</summary>
    public List<int> Slots(IdleFamily family, string race)
    {
        if (!_slots.TryGetValue((family, race), out var slots)) _slots[(family, race)] = slots = IdleSlots.Available(_data, family, race);
        return slots;
    }

    /// <summary>这个种族这个槽位没有自己的文件时，返回实际改到的种族（会一起影响那个种族）。</summary>
    public string? InheritedFrom(IdleFamily family, int slot, string race)
    {
        if (!_inherited.TryGetValue((family, slot, race), out var from))
            _inherited[(family, slot, race)] = from = IdleSlots.Loop(_data, family, slot, race) is { } t && t.FileRace != race ? t.FileRace : null;
        return from;
    }

    /// <summary>Penumbra 能不能用（界面每帧问，3 秒查一次）。</summary>
    public bool PenumbraReady
    {
        get
        {
            if (Environment.TickCount64 - _penumbraChecked > 3000)
            {
                _penumbraChecked = Environment.TickCount64;
                _penumbraReady = Penumbra.Root is not null;
            }
            return _penumbraReady;
        }
    }

    /// <summary>站立：用姿势自己的位置、脚踩地；坐、躺：骨盆放在原版动画的位置（游戏按它把人放到椅子、地上）。都再加上用户偏移。</summary>
    public static BodyPlacement Placement(IdleFamily family, Vector3 offset, Vector3 rotationDeg = default)
        => family == IdleFamily.Stand ? new BodyPlacement(Grounding.Feet, false, offset, rotationDeg) : new BodyPlacement(Grounding.None, true, offset, rotationDeg);

    /// <summary>位置预览（原版第 0 帧和我们的姿势，不含偏移）。失败时返回错误信息。</summary>
    public async Task<(IdlePreview? Preview, string? Error)> PreviewAsync(string poseJson, IdleFamily family, int slot, string race)
    {
        return await _framework.RunOnFrameworkThread(() =>
        {
            if (IdleSlots.Loop(_data, family, slot, race) is not { } loop) return (null, Loc.F("{0} 没有这个槽位", Races.Label(race)));
            try
            {
                var pap = _data.GetFile(loop.GamePath)!.Data;
                var sklb = _data.GetFile(IdleSlots.SklbPath(loop.FileRace))!.Data;
                return (IdleBaker.Preview(pap, sklb, ParsePose(poseJson), Placement(family, Vector3.Zero)), (string?)null);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "idle preview failed for {Race} {Path}", race, loop.GamePath);
                return ((IdlePreview?)null, Loc.F("预览失败：{0}", ex.Message));
            }
        });
    }

    /// <summary>poses：种族 → Brio 姿势 JSON（这个种族骨架下的模型空间）；offset：身体偏移（模型空间，米）；rotationDeg：整体旋转（度）；
    /// breath：各分区叠原版动作的强度。返回失败原因，成功为 null。</summary>
    public async Task<(string? Error, string? Mod)> SaveAsync(IReadOnlyDictionary<string, string> poses, IdleFamily family, int slot, Vector3 offset,
                                                              Vector3 rotationDeg, IdleBreath breath, string modName, string? reuse, bool merge,
                                                              string source)
    {
        return await _framework.RunOnFrameworkThread<(string? Error, string? Mod)>(() =>
        {
            if (Penumbra.Root is not { } root) return (Loc.T("没有检测到 Penumbra：idle 要存成 Penumbra 模组，请先安装并启用 Penumbra"), null);
            var vtbl = Vtable();
            if (vtbl == 0) return (Loc.T("游戏版本变了，找不到生成动画要用的 Havok 函数：等 Posegraph 更新"), null);
            var option = IdleSlots.ModLabel(family, slot);
            var folder = Penumbra.ResolveFolder(root, modName, reuse, merge);
            foreach (var (race, json) in poses)
            {
                var loop = IdleSlots.Loop(_data, family, slot, race);
                if (loop is null) return (Loc.F("{0} 没有「{1}」这个槽位", Races.Label(race), IdleSlots.SlotLabel(family, slot)), null);
                var pose = ParsePose(json);
                var place = Placement(family, offset, rotationDeg);
                var frame0 = new Dictionary<string, BoneTransform>(StringComparer.Ordinal);
                var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    files[loop.GamePath] = Bake(loop, pose, asStart: false, place, breath, vtbl, frame0);
                    if (IdleSlots.Start(_data, family, slot, race) is { } start)
                        files[start.GamePath] = Bake(start, pose, asStart: true, place, breath, vtbl, frame0);
                }
                catch (IdleBaker.SkeletonMismatchException)
                {
                    return (Loc.F("{0} 的原版动画和骨架对不上，没法生成", Races.Label(race)), null);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "idle bake failed for {Race} {Path}", race, loop.GamePath);
                    return (Loc.F("生成 {0} 的 idle 失败：{1}", Races.Label(race), ex.Message), null);
                }
                var group = $"{Races.Find(race)?.Name ?? race} ({race})";
                try
                {
                    Penumbra.Write(root, folder, group, option, source, files);
                    if (Penumbra.Publish(folder, group, option) is { } problem) return (problem, folder);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "writing idle mod failed");
                    return (Loc.F("写 Penumbra 模组失败：{0}", ex.Message), null);
                }
            }
            return ((string?)null, folder);
        });
    }

    // 骨架用文件所属种族的原版 sklb：动画轨道按那个骨架的骨骼序号绑定
    private byte[] Bake(IdleTarget target, Dictionary<string, BoneTransform> pose, bool asStart, BodyPlacement place, IdleBreath breath, nint vtbl,
                        Dictionary<string, BoneTransform> frame0)
    {
        var pap = _data.GetFile(target.GamePath)?.Data ?? throw new FileNotFoundException(target.GamePath);
        var sklb = _data.GetFile(IdleSlots.SklbPath(target.FileRace))?.Data ?? throw new FileNotFoundException(IdleSlots.SklbPath(target.FileRace));
        return IdleBaker.Bake(pap, sklb, pose, asStart, place, breath, _tempDir, vtbl, frame0);
    }

    private unsafe nint Vtable()
    {
        if (_vtbl != 0) return _vtbl;
        try
        {
            var lea = _sigs.ScanText(InterleavedVtblSig) - 4;
            var vtbl = lea + 4 + *(int*)lea;
            if (vtbl != 0 && *(nint*)vtbl != 0) _vtbl = vtbl;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "interleaved animation vtable not found");
        }
        return _vtbl;
    }

    /// <summary>Brio 姿势 JSON 的 Bones（"x, y, z" / "x, y, z, w" 字符串）。</summary>
    private static Dictionary<string, BoneTransform> ParsePose(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, BoneTransform>(StringComparer.Ordinal);
        foreach (var bone in doc.RootElement.GetProperty("Bones").EnumerateObject())
        {
            var p = Floats(bone.Value, "Position", 3);
            var r = Floats(bone.Value, "Rotation", 4);
            var s = Floats(bone.Value, "Scale", 3);
            if (p is null || r is null) continue;
            result[bone.Name] = new BoneTransform(new Vector3(p[0], p[1], p[2]), Quaternion.Normalize(new Quaternion(r[0], r[1], r[2], r[3])),
                                                  s is null ? Vector3.One : new Vector3(s[0], s[1], s[2]));
        }
        return result;
    }

    private static float[]? Floats(JsonElement bone, string key, int count)
    {
        if (!bone.TryGetProperty(key, out var v) || v.GetString() is not { } text) return null;
        var parts = text.Split(',');
        if (parts.Length != count) return null;
        var values = new float[count];
        for (var i = 0; i < count; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return null;
        return values;
    }
}
