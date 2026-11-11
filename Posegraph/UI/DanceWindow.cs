using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Posegraph.Backend;
using Posegraph.Dance;
using Posegraph.Video;

namespace Posegraph.UI;

/// <summary>
/// 视频窗口「导出舞蹈 mod…」：把当前片段（可以只取一段）换进一个循环舞蹈表情，带上原视频的音乐，存进 Penumbra 模组。
/// 音乐挂在表情的动作时间轴上：每循环一圈从头放一次，停跳就停，和动作对齐。
/// </summary>
public sealed class DanceWindow : Window
{
    private static readonly Vector4 Green = PosegraphTheme.Green;
    private static readonly Vector4 Yellow = PosegraphTheme.Yellow;
    private static readonly Vector4 Red = PosegraphTheme.Red;
    private static readonly Vector4 Grey = PosegraphTheme.Grey;

    private static readonly (RootMode Mode, string Label, string Tip)[] RootModes =
    [
        (RootMode.Drift, "去掉漂移", "保留舞步里的小位移，去掉慢慢走远的部分：循环时不会一下子瞬移回起点（推荐）"),
        (RootMode.Keep, "照视频走", "完全按视频里的走位：走得远的舞，每圈结束会瞬移回起点"),
        (RootMode.InPlace, "原地", "身体一直在原地，只保留转身"),
    ];

    private readonly DanceService _dance;
    private readonly Func<BackendClient> _client;
    private Clip? _clip;
    private string? _music;
    private int _danceIndex;
    private float _start, _end;
    private RootMode _root = RootMode.Drift;
    private float _seam = 0.5f;
    private bool _useIntro;
    private float _introEnd;
    private bool _smile = true;
    private bool _withMusic = true;
    private float _volume = 1f;
    private int _offsetMs;
    private string _modName = DanceService.DefaultModName;
    private string _option = "";
    private string _status = "";
    private Vector4 _statusColor = Grey;
    private bool _busy;

    public DanceWindow(DanceService dance, Func<BackendClient> client)
        : base(Loc.T("导出舞蹈 mod") + "###PosegraphDance")
    {
        _dance = dance;
        _client = client;
        Size = new Vector2(560, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>music：音乐来源（原视频或用户选的音频文件），没有为 null。</summary>
    public void Open(Clip clip, string? music)
    {
        if (_clip?.Path != clip.Path)
        {
            _start = 0;
            _end = (float)clip.Duration;
            _introEnd = Math.Min(_end, 8f);
            _option = clip.BaseName.Length > 0 ? clip.BaseName : clip.Name;
        }
        _clip = clip;
        _music = music;
        _withMusic = music is not null;
        _status = "";
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw() => PosegraphTheme.Push();

    public override void PostDraw() => PosegraphTheme.Pop();

    public override void Draw()
    {
        if (_clip is not { } clip) return;
        var s = ImGuiHelpers.GlobalScale;
        ImGui.TextUnformatted(Loc.F("片段：{0}", clip.Name));
        if (clip.Race is not { } race)
        {
            ImGui.TextColored(Yellow, Loc.T("这个片段没记种族，没法导出"));
            return;
        }

        // 舞蹈表情
        var dances = _dance.Dances;
        if (dances.Count == 0)
        {
            ImGui.TextColored(Yellow, Loc.T("没读到游戏里的舞蹈表情"));
            return;
        }
        _danceIndex = Math.Clamp(_danceIndex, 0, dances.Count - 1);
        var slot = dances[_danceIndex];
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("替换"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260 * s);
        using (var combo = ImRaii.Combo("##dance", slot.Label))
            if (combo)
                for (var i = 0; i < dances.Count; i++)
                    if (ImGui.Selectable($"{dances[i].Label}##d{i}", i == _danceIndex)) _danceIndex = i;
        Tip(Loc.T("游戏里做这个表情时跳的就是这段舞（只换循环部分）。建议先选一个平时不用的舞"));
        var target = _dance.Target(slot, race);
        ImGui.SameLine();
        ImGui.TextDisabled(Loc.F("原版 {0:0.0} 秒", _dance.VanillaSeconds(slot)));
        if (target is null) ImGui.TextColored(Yellow, Loc.F("{0} 没有这个表情的动作文件", Races.Label(race)));
        else if (target.FileRace != race)
            ImGui.TextColored(Grey, Loc.F("{0} 没有自己的文件，用的是 {1} 的：会一起改到所有用这个文件的种族", Races.Label(race), Races.Label(target.FileRace)));
        else ImGui.TextColored(Grey, Loc.F("动作按 {0} 的骨架生成（片段识别时的种族）", Races.Label(race)));

        // 范围
        ImGui.Separator();
        var duration = (float)clip.Duration;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloatRange2("##range", ref _start, ref _end, 0.05f, 0f, duration, Loc.T("从 %.2f 秒"), Loc.T("到 %.2f 秒")))
        {
            _start = Math.Clamp(_start, 0f, duration);
            _end = Math.Clamp(_end, _start + 0.5f, duration);
        }
        Tip(Loc.T("只取片段里的一段当循环（拖动或双击输入）"));
        var seconds = Math.Max(0f, _end - _start);
        var frames = (int)Math.Round(seconds * DanceMotion.Fps) + 1;
        ImGui.TextColored(seconds > 300 ? Yellow : Grey, Loc.F("循环长 {0:0.0} 秒；动作文件约 {1:0} MB（不压缩）", seconds, DanceService.EstimateMegabytes(frames)));

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("走位"));
        foreach (var (mode, label, tip) in RootModes)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton(Loc.T(label), _root == mode)) _root = mode;
            Tip(Loc.T(tip));
        }
        // 开场：只有原版带开场动作的舞，游戏才会先放开场 pap
        var introTarget = _dance.IntroTarget(slot, race);
        if (introTarget is null)
        {
            _useIntro = false;
            ImGui.TextDisabled(Loc.T("这个舞原版没有开场动作，游戏不会先放开场：整段都是循环"));
        }
        else
        {
            ImGui.Checkbox(Loc.T("开场只放一次"), ref _useIntro);
            Tip(Loc.T("做表情时先放一遍开场，放完再一直循环后面那段（和原版这个舞一样：开场 → 循环）"));
            if (_useIntro)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(200 * s);
                _introEnd = Math.Clamp(_introEnd, _start + 0.5f, Math.Max(_start + 0.5f, _end - 1f));
                ImGui.SliderFloat("##introEnd", ref _introEnd, _start + 0.5f, Math.Max(_start + 0.6f, _end - 1f), Loc.T("开场到 %.2f 秒"));
                Tip(Loc.T("从「从」到这里是开场，这里到「到」是循环"));
                ImGui.TextColored(Grey, Loc.F("开场 {0:0.0} 秒 + 循环 {1:0.0} 秒", _introEnd - _start, _end - _introEnd));
            }
        }
        ImGui.SetNextItemWidth(200 * s);
        ImGui.SliderFloat(Loc.T("接缝过渡"), ref _seam, 0f, 2f, Loc.T("%.2f 秒"));
        Tip(Loc.T("循环的最后这么多秒慢慢过渡回循环的第一帧，一圈接下一圈时动作不会跳。0 = 不过渡"));
        ImGui.SameLine();
        ImGui.Checkbox(Loc.T("微笑"), ref _smile);
        Tip(Loc.T("跳舞时整段带微笑表情（原版舞里按原舞步切换的表情都去掉了，时机对不上）"));

        // 音乐
        ImGui.Separator();
        using (ImRaii.Disabled(_music is null))
            ImGui.Checkbox(Loc.T("带音乐"), ref _withMusic);
        Tip(Loc.T("把原视频这一段的音乐放进模组：做表情时从头放（有开场时开场、循环各一段），每循环一圈重新放一次，停跳就停。音量跟游戏的音效音量走"));
        if (_music is null)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, Loc.T("没找到音乐（在视频窗口点「放音乐」旁的音符按钮选一个）"));
            _withMusic = false;
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled(Path.GetFileName(_music));
        }
        if (_withMusic)
        {
            ImGui.SetNextItemWidth(160 * s);
            ImGui.SliderFloat(Loc.T("音量"), ref _volume, 0.2f, 2f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160 * s);
            ImGui.SliderInt(Loc.T("音乐偏移"), ref _offsetMs, -1000, 1000, Loc.T("%d 毫秒"));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _offsetMs = 0;
            Tip(Loc.T("正数 = 音乐晚一点放，负数 = 早一点（游戏里听着和动作对不上时微调）。右键归零"));
        }

        // 模组
        ImGui.Separator();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("模组名"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * s);
        ImGui.InputText("##mod", ref _modName, 64);
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.T("选项名"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##option", ref _option, 64);
        Tip(Loc.T("模组里每个表情一组、每段舞一个选项；同名的选项会被覆盖。存完自动只开这一个"));

        ImGui.Spacing();
        var ready = _dance.PenumbraReady;
        using (ImRaii.Disabled(_busy || !ready || target is null || _option.Trim().Length == 0))
            if (ImGui.Button(_busy ? Loc.T("生成中…") : Loc.T("生成并启用"), new Vector2(-1, 0)))
                Export(clip, slot, race);
        if (!ready) ImGui.TextColored(Yellow, Loc.T("没有检测到 Penumbra"));
        if (_status.Length > 0) ImGui.TextColored(_statusColor, _status);

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Grey))
        {
            ImGui.TextWrapped(Loc.T("· 模组会在你角色用的合集里启用。别人做同一个表情时，如果他也用这个合集（比如默认合集），也会跳这段、放这首歌"));
            ImGui.TextWrapped(Loc.T("· 音乐和游戏的背景音乐会同时放，跳之前可以先关掉背景音乐"));
            ImGui.TextWrapped(Loc.T("· 生成后不用重启游戏，重新做一次表情就是新的"));
        }
    }

    private void Export(Clip clip, DanceSlot slot, string race)
    {
        _busy = true;
        SetStatus(Loc.T("生成中…"), Grey);
        var (start, end, root, seam, music, withMusic, volume, offset, modName, option) =
            (_start, _end, _root, _seam, _music, _withMusic, _volume, _offsetMs, _modName, _option.Trim());
        double? introEnd = _useIntro && _dance.IntroTarget(slot, race) is not null ? _introEnd : null;
        var face = _smile ? "cfxf_smile" : null;
        Task.Run(async () =>
        {
            try
            {
                var (intro, motion) = DanceMotion.Split(clip, 0, start, end, root, seam, introEnd);
                var loopStart = introEnd ?? start;
                byte[]? ogg = null, introOgg = null;
                if (withMusic && music is not null)
                {
                    var from = clip.Start + loopStart - offset / 1000.0;
                    ogg = await _client().AudioOggAsync(music, from, from + motion.Seconds, volume);
                    if (intro is not null)
                    {
                        var introFrom = clip.Start + start - offset / 1000.0;
                        introOgg = await _client().AudioOggAsync(music, introFrom, introFrom + intro.Seconds, volume);
                    }
                }
                var source = introEnd is { } ie
                    ? Loc.F("{0}，开场 {1:0.00}~{2:0.00} 秒，循环 {2:0.00}~{3:0.00} 秒", clip.Name, start, ie, end)
                    : Loc.F("{0}，{1:0.00}~{2:0.00} 秒", clip.Name, start, end);
                var (error, mod) = await _dance.ExportAsync(slot, race, motion, ogg, intro, introOgg, face, modName, option, source);
                if (error is not null) SetStatus(error, mod is null ? Red : Yellow);
                else SetStatus(Loc.F("已存进模组「{0}」并启用：在游戏里做 {1} 就是这段舞", mod, slot.Command.Length > 0 ? slot.Command : slot.Name), Green);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.F("生成失败：{0}", ex.Message), Red);
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private void SetStatus(string text, Vector4 color)
    {
        _status = text;
        _statusColor = color;
    }

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }
}
