using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Posegraph.Idle;

namespace Posegraph.Dance;

/// <summary>一个可以替换的循环舞蹈表情：Loop = 循环动作的时间轴名（emote/dance02_loop），pap 和 tmb 都按它找。</summary>
public sealed record DanceSlot(uint EmoteId, string Name, string Command, string Loop, string? Start)
{
    public string Label => Command.Length > 0 ? $"{Name}  {Command}" : Name;

    /// <summary>动作时间轴（所有种族共用）。</summary>
    public string TimelinePath => $"chara/action/{Loop}.tmb";

    public string? StartTimelinePath => Start is null ? null : $"chara/action/{Start}.tmb";

    /// <summary>声音文件的游戏路径（游戏里没有这个文件，Penumbra 直接提供）。</summary>
    public string SoundPath => SoundFor(Loop);

    public static string SoundFor(string key) => $"sound/posegraph/{key.Replace('/', '_')}.scd";
}

/// <summary>
/// 片段 → 舞蹈模组：选一个循环舞蹈表情，把动作烘焙进它的循环 pap（按片段的种族；没有自己文件的种族读上级种族的，会一起改到），
/// 改它的动作时间轴（总帧数 = 舞蹈长度，第 0 帧放音乐、动画结束时停），音乐装进 scd，一起写进 Penumbra 模组「Posegraph Dance」：
/// 每个表情一组，每段舞一个选项，存完只开这一个。需要 Penumbra。
/// </summary>
public sealed class DanceService
{
    public const string DefaultModName = "Posegraph Dance";

    /// <summary>装音乐的模板：游戏里一个单声音、单音轨、立体声 Vorbis 的事件音效（不跟着距离衰减）。</summary>
    public const string SoundTemplate = "sound/event/SE_Event_018.scd";

    private readonly IDataManager _data;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IdleService _idle;
    private readonly string _tempDir;
    private List<DanceSlot>? _dances;

    public DanceService(IDalamudPluginInterface pi, IDataManager data, IFramework framework, IPluginLog log, IdleService idle)
    {
        _data = data;
        _framework = framework;
        _log = log;
        _idle = idle;
        _tempDir = Path.Combine(pi.GetPluginConfigDirectory(), "dance");
    }

    public bool PenumbraReady => _idle.PenumbraReady;

    /// <summary>
    /// 循环舞蹈表情（有 _loop 时间轴、名字里带 dance 的，加上小黄莺之舞这类 emote_sp 的舞），按表情编号排。
    /// 同一个循环动作被几个表情共用时只列一次。
    /// </summary>
    public IReadOnlyList<DanceSlot> Dances
    {
        get
        {
            if (_dances is not null) return _dances;
            var list = new List<DanceSlot>();
            var seen = new HashSet<string>();
            foreach (var e in _data.GetExcelSheet<Emote>())
            {
                if (e.ActionTimeline[0].RowId == 0) continue;
                var loop = e.ActionTimeline[0].Value.Key.ToString();
                var command = e.TextCommand.ValueNullable?.Command.ToString() ?? "";
                var isDance = loop.StartsWith("emote/dance", StringComparison.Ordinal) && loop.EndsWith("_loop", StringComparison.Ordinal)
                              || command.Contains("dance", StringComparison.OrdinalIgnoreCase) && loop.EndsWith("_loop", StringComparison.Ordinal);
                if (!isDance || !seen.Add(loop) || !_data.FileExists($"chara/action/{loop}.tmb")) continue;
                var start = e.ActionTimeline[1].RowId == 0 ? null : e.ActionTimeline[1].Value.Key.ToString();
                list.Add(new DanceSlot(e.RowId, e.Name.ToString(), command, loop, string.IsNullOrEmpty(start) ? null : start));
            }
            return _dances = list;
        }
    }

    /// <summary>这个种族的循环动作实际读的文件（没有自己文件时是上级种族的）。</summary>
    public IdleTarget? Target(DanceSlot dance, string race) => Find(dance.Loop, race);

    /// <summary>
    /// 开场动作的文件；没有就是这个舞不支持开场（游戏只给原版带开场动作的舞加载开场 pap：踢踏舞、丰饶之舞、宫廷之舞，
    /// 动作时间轴表里那一列和有没有开场 pap 完全对应，太阳之舞这类的开场时间轴是空的、不读 pap）。
    /// </summary>
    public IdleTarget? IntroTarget(DanceSlot dance, string race) => dance.Start is null ? null : Find(dance.Start, race);

    private IdleTarget? Find(string key, string race)
    {
        foreach (var r in IdleSlots.FallbackChain(race))
        {
            var path = $"chara/human/{r}/animation/a0001/bt_common/{key}.pap";
            if (_data.FileExists(path)) return new IdleTarget(path, r);
        }
        return null;
    }

    /// <summary>原版循环的长度（秒），界面上提示用。</summary>
    public double VanillaSeconds(DanceSlot dance)
        => _data.GetFile(dance.TimelinePath) is { } f ? TmbEdit.Duration(f.Data) / DanceMotion.Fps : 0;

    /// <summary>
    /// 烘焙并写进 Penumbra 模组。ogg：音乐（null 不放音乐）；option：选项名（同名覆盖）。返回失败原因（成功为 null）和模组文件夹。
    /// </summary>
    public async Task<(string? Error, string? Mod)> ExportAsync(DanceSlot dance, string race, DanceMotion motion, byte[]? ogg,
                                                                DanceMotion? intro, byte[]? introOgg, string? face,
                                                                string modName, string option, string source)
    {
        return await _framework.RunOnFrameworkThread<(string? Error, string? Mod)>(() =>
        {
            if (_idle.Penumbra.Root is not { } root) return (Loc.T("没有检测到 Penumbra：舞蹈要存成 Penumbra 模组，请先安装并启用 Penumbra"), null);
            var vtbl = _idle.Vtable();
            if (vtbl == 0) return (Loc.T("游戏版本变了，找不到生成动画要用的 Havok 函数：等 Posegraph 更新"), null);
            if (Target(dance, race) is not { } target) return (Loc.F("{0} 没有「{1}」的动作文件", Races.Label(race), dance.Name), null);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var pap = _data.GetFile(target.GamePath)?.Data ?? throw new FileNotFoundException(target.GamePath);
                var sklb = _data.GetFile(IdleSlots.SklbPath(target.FileRace))?.Data ?? throw new FileNotFoundException(IdleSlots.SklbPath(target.FileRace));
                var template = ogg is null && introOgg is null ? null
                             : _data.GetFile(SoundTemplate)?.Data ?? throw new FileNotFoundException(SoundTemplate);
                // 循环：动作（pap 时间轴里带音乐，停跳就停）+ 动作时间轴的长度
                files[target.GamePath] = DanceBaker.Bake(pap, sklb, motion, _tempDir, vtbl, face, ogg is null ? null : dance.SoundPath);
                var tmb = _data.GetFile(dance.TimelinePath)?.Data ?? throw new FileNotFoundException(dance.TimelinePath);
                files[dance.TimelinePath] = TmbEdit.RewriteActionTimeline(tmb, motion.TimelineFrames, null);
                if (ogg is not null) files[dance.SoundPath] = ScdAudio.ReplaceFirstAudio(template!, ogg);

                // 开场（只放一次，放完游戏接循环）：用这个种族自己的开场 pap 当底，动作时间轴照循环的那个改（时长、动画名）
                if (intro is not null && dance.Start is { } startKey && IntroTarget(dance, race) is { } introTarget)
                {
                    var introPap = _data.GetFile(introTarget.GamePath)?.Data ?? throw new FileNotFoundException(introTarget.GamePath);
                    var introSklb = _data.GetFile(IdleSlots.SklbPath(introTarget.FileRace))?.Data
                                    ?? throw new FileNotFoundException(IdleSlots.SklbPath(introTarget.FileRace));
                    var introSound = introOgg is null ? null : DanceSlot.SoundFor(startKey);
                    files[introTarget.GamePath] = DanceBaker.Bake(introPap, introSklb, intro, _tempDir, vtbl, face, introSound);
                    var startName = PapFile.Read(introPap).AnimationName(0);
                    files[dance.StartTimelinePath!] = TmbEdit.RewriteActionTimeline(tmb, intro.TimelineFrames, null, animName: startName);
                    if (introOgg is not null) files[introSound!] = ScdAudio.ReplaceFirstAudio(template!, introOgg);
                }
            }
            catch (IdleBaker.SkeletonMismatchException)
            {
                return (Loc.F("{0} 的原版动画和骨架对不上，没法生成", Races.Label(race)), null);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "dance bake failed for {Race} {Path}", race, target.GamePath);
                return (Loc.F("生成舞蹈失败：{0}", ex.Message), null);
            }

            var group = dance.Label;
            try
            {
                var folder = _idle.Penumbra.ResolveFolder(root, modName, null, merge: true);
                _idle.Penumbra.Write(root, folder, group, option, source, files,
                                     Loc.T("Posegraph 从视频动作生成的舞蹈：替换循环舞蹈表情的动作，可带音乐。每个表情一组，每段舞一个选项，同一组只开一个。"));
                if (_idle.Penumbra.Publish(folder, group, option, exclusive: true) is { } problem) return (problem, folder);
                _log.Information("dance {Option} saved to {Mod} / {Group}: {Bytes} bytes", option, folder, group, files.Values.Sum(b => b.Length));
                return ((string?)null, folder);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "writing dance mod failed");
                return (Loc.F("写 Penumbra 模组失败：{0}", ex.Message), null);
            }
        });
    }

    /// <summary>pap 大概多大（非压缩：每帧每条轨道 48 字节），提示用。轨道数按 120 估。</summary>
    public static double EstimateMegabytes(int frames) => frames * 120 * 48 / 1024.0 / 1024.0;
}
