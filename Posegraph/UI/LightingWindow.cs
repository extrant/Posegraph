using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Posegraph.Backend;
using Posegraph.Library;
using Posegraph.Lighting;

namespace Posegraph.UI;

/// <summary>
/// 灯光窗口：上面是方案（点一下就给当前角色打好光），中间是整体亮度 / 冷暖 / 灯距 / 补光，
/// 3D 布光（拖灯、拖对准点、光锥和光照预览球）在贴着右边的 LightingSceneWindow 里，点「3D 布光」展开 / 收起；
/// 方案最前面是「照片同款」：按主窗口照片里人物脸上的明暗估出主光，照着摆（后端 lighting.py）；「照片配色」只按照片换颜色。
/// 方案下面是跟随角色、自动调亮度这些开关，和把灯光存进主窗口当前图片（图库）。
/// 「照谁」：单人 / 一组（一套灯罩住勾选的人）/ 每人一套 / 自动（近的合一套、远的各一套），人散开的大场景也都照得到。
/// 下面「逐盏调整」给熟悉打光的人：每盏灯的方位、仰角、距离、颜色、光斑、灯头偏角都能改，可独显 / 关掉，改好可存成自己的方案；
/// 还能加灯（主光、轮廓光、头发光这些预设，加进正在调的那一套）、删灯，单盏 / 整套恢复成方案原样。
/// 灯光窗口和 3D 布光共用一份撤销记录：改参数、拖灯、加删灯、换方案都能撤销 / 重做（Ctrl+Z / Ctrl+Y）。
/// </summary>
public sealed class LightingWindow : Window
{
    private static readonly Vector4 Green = PosegraphTheme.Green;
    private static readonly Vector4 Yellow = PosegraphTheme.Yellow;
    private static readonly Vector4 Grey = PosegraphTheme.Grey;

    private readonly LightingService _service;
    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly Func<int?> _actor;
    private readonly Func<bool> _inGpose;
    private readonly Func<bool> _hasPhoto;
    private readonly Func<Task<PhotoLighting?>> _photoLighting;
    private readonly Func<string?> _imageSha1;
    private readonly ImageLibrary _library;
    private readonly Func<(int Index, string Name)[]> _actors;
    private string _status = "";
    private Vector4 _statusColor = Grey;
    private PhotoLighting? _photo;        // 最近一次从照片估的光（画照片颜色色块用）和它对应的图
    private string? _photoImage;
    private bool _busy;
    private string? _ok;                  // 动作自己给的成功提示（盖过 Run 的默认提示）
    private string _saveName = "";
    private bool _openSelected;   // 下一帧把选中的灯在「逐盏调整」里展开

    public LightingWindow(LightingService service, Configuration config, IDalamudPluginInterface pi, Func<int?> actor, Func<bool> inGpose,
                          Func<bool> hasPhoto, Func<Task<PhotoLighting?>> photoLighting, Func<string?> imageSha1, ImageLibrary library,
                          Func<(int Index, string Name)[]> actors)
        : base(Loc.T("Posegraph 灯光") + "###PosegraphLighting")
    {
        _service = service;
        _config = config;
        _pi = pi;
        _actor = actor;
        _inGpose = inGpose;
        _hasPhoto = hasPhoto;
        _photoLighting = photoLighting;
        _imageSha1 = imageSha1;
        _library = library;
        _actors = actors;
        _service.Notice += (text, ok) => SetStatus(text, ok ? Green : Yellow);
        Size = new Vector2(540, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 360), MaximumSize = new Vector2(4000, 4000) };
    }

    public override void PreDraw()
    {
        // 提示放在标题栏：不占窗口里的位置，出现、消失时下面的按钮不会跳
        var title = Loc.T("Posegraph 灯光");
        if (_status.Length > 0)
            title += "  ·  " + (_statusColor == Yellow ? Loc.T("注意：") : "") + _status.Replace('\n', ' ');
        WindowName = title + "###PosegraphLighting";
        PosegraphTheme.Push();
    }

    public override void PostDraw() => PosegraphTheme.Pop();

    /// <summary>3D 布光窗口（Plugin 接上），点「3D 布光」展开 / 收起。</summary>
    public Window? SceneWindow { get; set; }

    /// <summary>窗口的位置和大小（3D 布光窗口贴在它右边）。</summary>
    public Vector2 WindowPos { get; private set; }

    public Vector2 WindowSize { get; private set; }

    /// <summary>选中第 i 盏灯，并在「逐盏调整」里展开它（3D 布光窗口点灯按钮用）；null 为取消选中。</summary>
    public void OpenLight(int? i)
    {
        _service.SelectedLight = i;
        _openSelected = i is not null;
    }

    public override void Draw()
    {
        WindowPos = ImGui.GetWindowPos();
        WindowSize = ImGui.GetWindowSize();
        if (!_inGpose())
        {
            ImGui.TextColored(Yellow, Loc.T("进入 GPose 后才能打光"));
            return;
        }
        HandleShortcuts();
        DrawRigs();
        ImGui.Separator();
        DrawTweaks();
        DrawLights();
    }

    /// <summary>窗口有焦点（不在输入框里打字）时 Ctrl+Z 撤销、Ctrl+Y / Ctrl+Shift+Z 重做。3D 布光窗口也调。</summary>
    public void HandleShortcuts()
    {
        var io = ImGui.GetIO();
        if (!io.KeyCtrl || io.WantTextInput || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Z, false))
        {
            if (io.KeyShift) Redo();
            else Undo();
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Y, false)) Redo();
    }

    /// <summary>撤销、重做两个按钮（灯光窗口和 3D 布光窗口都有）。</summary>
    public void DrawUndoRedo()
    {
        using (ImRaii.Disabled(!_service.CanUndo || _busy))
            if (ImGuiComponents.IconButton("##lightUndo", FontAwesomeIcon.Undo)) Undo();
        Tip(Loc.T("撤销（Ctrl+Z）"));
        ImGui.SameLine();
        using (ImRaii.Disabled(!_service.CanRedo || _busy))
            if (ImGuiComponents.IconButton("##lightRedo", FontAwesomeIcon.Redo)) Redo();
        Tip(Loc.T("重做（Ctrl+Y）"));
    }

    private void Undo()
    {
        if (_busy || !_service.CanUndo) return;
        _service.Undo();
        SetStatus(Loc.T("已撤销"), Grey);
    }

    private void Redo()
    {
        if (_busy || !_service.CanRedo) return;
        _service.Redo();
        SetStatus(Loc.T("已重做"), Grey);
    }

    private void DrawRigs()
    {
        ImGui.TextDisabled(Loc.T("点一个方案，给主窗口当前的角色打光（灯跟着人和镜头摆）"));
        var width = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(2, (int)(width / (130 * ImGuiHelpers.GlobalScale)));
        var buttonW = (width - ImGui.GetStyle().ItemSpacing.X * (columns - 1)) / columns;
        DrawPhotoRig(width);
        var i = 0;
        foreach (var rig in _service.Rigs)
        {
            if (i++ % columns != 0) ImGui.SameLine();
            var active = _service.Active?.Id == rig.Id;
            using (ImRaii.PushColor(ImGuiCol.Button, PosegraphTheme.Accent with { W = 0.55f }, active))
            using (ImRaii.Disabled(_busy))
                if (ImGui.Button(Loc.T(rig.Name) + "##rig" + rig.Id, new Vector2(buttonW, 0)))
                    Apply(rig);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(rig.Custom ? Loc.T("自己存的方案；右键删除") : Loc.T(rig.Description));
            if (rig.Custom && ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _service.DeleteCustom(rig);
                _pi.SavePluginConfig(_config);
            }
        }
        DrawUndoRedo();
        ImGui.SameLine();
        using (ImRaii.Disabled(_busy || _service.Active is null))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.SyncAlt, Loc.T("重新摆")))
                Run(() => _service.ReplaceAsync(_actor()), Loc.T("已按角色现在的位置重新摆"));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Loc.T("角色挪动、换了姿势后点这里：重新读脸的位置和朝向"));
            UiText.SameLineIfFits(UiText.IconButtonWidth(Loc.T("关掉所有灯")));
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Trash, Loc.T("关掉所有灯")))
            {
                _service.Checkpoint();
                _service.Clear();
                SetStatus(Loc.T("已关掉 Posegraph 打的灯"), Grey);
            }
        }
        if (SceneWindow is { } scene)
        {
            UiText.SameLineIfFits(UiText.IconButtonWidth(Loc.T("3D 布光")));
            using (ImRaii.PushColor(ImGuiCol.Button, PosegraphTheme.Accent with { W = 0.55f }, scene.IsOpen))
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Cube, Loc.T("3D 布光")))
                    scene.IsOpen = !scene.IsOpen;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("展开 / 收起 3D 布光窗口：贴在这个窗口右边，宽度可以拖"));
        }
        UiText.SameLineIfFits(UiText.CheckboxWidth(Loc.T("跟随镜头")));
        var follow = _config.LightFollowCamera;
        if (ImGui.Checkbox(Loc.T("跟随镜头"), ref follow))
        {
            _config.LightFollowCamera = follow;
            _pi.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("转镜头时灯跟着转，始终从镜头这一侧照亮脸"));
        UiText.SameLineIfFits(110 * ImGuiHelpers.GlobalScale + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(Loc.T("打光范围")).X);
        DrawFraming();
        UiText.SameLineIfFits(UiText.CheckboxWidth(Loc.T("跟随角色")));
        if (Check(Loc.T("跟随角色"), _config.LightFollowSubject, v => _config.LightFollowSubject = v,
                  Loc.T("Posegraph 给这个角色换了姿势、或角色被挪动 / 转身后，自动重新读脸再摆（在 Brio 里手动掰骨骼不算，掰完点「重新摆」）")))
            _pi.SavePluginConfig(_config);
        DrawPeople();
        DrawLibraryRow();
    }

    /// <summary>第 g 套的名字：「第 N 套：角色名…」，单独调过的加标记。</summary>
    public string GroupLabel(int g)
    {
        var names = _actors().ToDictionary(a => a.Index, a => a.Name);
        var members = g < _service.GroupMembers.Count ? _service.GroupMembers[g] : [];
        var who = string.Join("、", members.Select(i => names.GetValueOrDefault(i, "?")));
        return Loc.F("第 {0} 套：{1}", g + 1, who) + (_service.IsOwn(g) ? Loc.T("（单独调过）") : "");
    }

    /// <summary>
    /// 多人打光时选调哪一套：所有套一起调，或者单独调某一套（其他套不变）；单独调过的可以恢复成和其他套一样。
    /// 3D 布光窗口顶上和「逐盏调整」上面都有。只有一套灯时不显示。
    /// </summary>
    public void DrawGroupPicker()
    {
        var count = _service.Groups.Count;
        if (count < 2) return;
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("调哪一套"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Min(260 * scale, Math.Max(120 * scale, ImGui.GetContentRegionAvail().X * 0.6f)));
        var current = _service.SelectedGroup is { } g ? GroupLabel(g) : Loc.T("所有套一起调");
        if (ImGui.BeginCombo("##lightGroup", current))
        {
            if (ImGui.Selectable(Loc.T("所有套一起调"), _service.SelectedGroup is null)) Select(null);
            for (var i = 0; i < count; i++)
                if (ImGui.Selectable(GroupLabel(i) + "##group" + i, _service.SelectedGroup == i)) Select(i);
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("所有套一起调：改的灯所有人都跟着变（单独调过的那几套除外）。\n"
                                   + "选某一套：只改这一套，别的套不变。在 3D 布光里点别的那套的灯或圈也能选中它"));
        if (_service.SelectedGroup is { } selected && _service.IsOwn(selected))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("恢复成和其他套一样")))
            {
                _service.Checkpoint();
                _service.ResetGroup(selected);
                SetStatus(Loc.F("第 {0} 套已恢复成和其他套一样", selected + 1), Grey);
            }
        }

        void Select(int? group)
        {
            _service.SelectGroup(group);
            _service.Refresh();
        }
    }

    private static readonly (string Key, string Label, string Tip)[] PeopleModes =
    [
        ("single", "单人", "只照主窗口选的角色"),
        ("group", "一组", "勾选的人当成一组：一套灯拉远、光斑放宽罩住所有人，光线统一。人离得很远（十几米）时会偏暗、偏平"),
        ("each", "每人一套", "每个勾选的人身上各摆一套同样的灯（主光都在画面同一侧），人散得再开都照得到；人多时灯多，可能掉帧"),
        ("auto", "自动", "离得近（约 2.5 米内）的人合用一套灯，离得远的各自另起一套"),
    ];

    /// <summary>照谁：单人 / 一组 / 每人一套 / 自动；不是单人时列出 GPose 角色勾选（默认全选，主窗口选的角色总是照）。改了马上重摆。</summary>
    private void DrawPeople()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var changed = false;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("照谁"));
        foreach (var (key, label, tip) in PeopleModes)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton(Loc.T(label) + "##people" + key, _config.LightPeople == key) && _config.LightPeople != key)
            {
                _config.LightPeople = key;
                _pi.SavePluginConfig(_config);
                changed = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T(tip));
        }
        if (_config.LightPeople != "single")
        {
            var primary = _actor();
            var first = true;
            foreach (var (index, name) in _actors())
            {
                var label = index == primary ? Loc.F("{0}（主）", name) : name;
                if (!first) UiText.SameLineIfFits(UiText.CheckboxWidth(label));
                first = false;
                var on = index == primary || !_service.Excluded.Contains(index);
                using (ImRaii.Disabled(index == primary))
                    if (ImGui.Checkbox(label + "##light" + index, ref on))
                    {
                        if (on) _service.Excluded.Remove(index);
                        else _service.Excluded.Add(index);
                        changed = true;
                    }
                if (index == primary && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Loc.T("主窗口选的角色总是照（灯的方向按他定）"));
            }
            if (_service.Active is not null && _service.PeopleCount > 0)
            {
                var info = Loc.F("照 {0} 个人，{1} 套灯", _service.PeopleCount, _service.Groups.Count - _service.DroppedGroups);
                if (_service.DroppedGroups > 0) ImGui.TextColored(Yellow, info + Loc.F("；灯太多，离镜头远的 {0} 组没打（可以改用「一组」或「自动」）", _service.DroppedGroups));
                else ImGui.TextDisabled(info);
            }
        }
        if (changed && _service.Active is not null)
            Run(() => _service.ReplaceAsync(_actor()), Loc.T("已按新的「照谁」重新摆"));
    }

    private static readonly string[] FramingKeys = ["auto", "face", "half", "full"];

    /// <summary>打光范围：自动（看游戏镜头拍到多少）/ 脸 / 半身 / 全身。</summary>
    private void DrawFraming()
    {
        string Name(LightFraming f) => f switch
        {
            LightFraming.Half => Loc.T("半身"),
            LightFraming.Full => Loc.T("全身"),
            _ => Loc.T("脸"),
        };
        var current = Math.Max(0, Array.IndexOf(FramingKeys, _config.LightFraming));
        string[] labels = [Loc.F("自动（{0}）", Name(_service.Framing)), Loc.T("脸"), Loc.T("半身"), Loc.T("全身")];
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo(Loc.T("打光范围"), ref current, labels, labels.Length))
        {
            _config.LightFraming = FramingKeys[current];
            _pi.SavePluginConfig(_config);
            _service.RefreshFraming();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("灯围着哪一块摆、照多大一片：脸（头像、特写）、半身（腰以上）、全身。\n"
                                   + "自动：看游戏镜头拍到多少——拍到脚是全身，拍到腰是半身，否则照脸。\n"
                                   + "范围大时灯会离远一些、光斑放宽，整个人都照得到"));
    }

    /// <summary>把现在的灯存进主窗口当前的图（图库）：以后给角色套这张图的姿势时一并恢复。</summary>
    private void DrawLibraryRow()
    {
        var sha1 = _imageSha1();
        var saved = sha1 is not null && _library.HasLighting(sha1);
        using (ImRaii.Disabled(sha1 is null || _service.Active is null))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, saved ? Loc.T("更新这张图的灯光") : Loc.T("存进这张图")))
            {
                if (_service.Snapshot() is { } snapshot)
                {
                    _library.WriteLighting(sha1!, snapshot);
                    SetStatus(Loc.T("已把灯光存进主窗口当前的图：以后套这张图的姿势时一并恢复"), Green);
                }
            }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(sha1 is null ? Loc.T("主窗口先打开一张图库里的图")
                             : Loc.T("把现在的灯（逐盏改过的、整体亮度都算）存进主窗口当前的图。\n"
                                     + "灯的位置相对脸和镜头，换站位、换地图都能直接用"));
        ImGui.SameLine();
        using (ImRaii.Disabled(!saved))
            if (ImGuiComponents.IconButton("##deleteLight", FontAwesomeIcon.Trash))
            {
                _library.DeleteLighting(sha1!);
                SetStatus(Loc.T("已删除这张图存的灯光"), Grey);
            }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("删除这张图存的灯光（现在的灯不变）"));
        ImGui.SameLine();
        if (Check(Loc.T("套姿势时恢复"), _config.LightRestoreFromLibrary, v => _config.LightRestoreFromLibrary = v,
                  Loc.T("给角色套主窗口的图时，这张图存过灯光就一并打上")))
            _pi.SavePluginConfig(_config);
        if (saved)
        {
            ImGui.SameLine();
            ImGui.TextColored(Green, Loc.T("这张图存了灯光"));
        }
    }

    private void DrawTweaks()
    {
        var t = _service.Tweaks;
        var d = RigTweaks.Default;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Loc.T("整体调整（右键滑杆单独恢复默认）"));
        ImGui.SameLine();
        using (ImRaii.Disabled(t == d))
            if (ImGui.SmallButton(Loc.T("恢复默认") + "##tweaksReset"))
            {
                _service.Checkpoint();
                _service.Tweaks = d;
                _service.Refresh();
            }
        Tip(Loc.T("整体亮度、冷暖、灯距、补光全部回到默认（不动每盏灯自己的参数）"));
        var brightness = t.Brightness;
        var warmth = t.Warmth;
        var distance = t.Distance;
        var fill = t.Fill;
        var changed = false;
        changed |= Slider(Loc.T("整体亮度"), ref brightness, 0.1f, 3f, "%.2f", reset: d.Brightness);
        changed |= Slider(Loc.T("冷暖"), ref warmth, -2500f, 2500f, warmth >= 0 ? "+%.0f K" : "%.0f K", Loc.T("负值更暖、正值更冷（加在每盏灯的色温上）"), d.Warmth);
        changed |= Slider(Loc.T("灯距"), ref distance, 0.5f, 2.5f, "×%.2f", Loc.T("所有灯离脸的距离倍数：近了光更硬更集中"), d.Distance);
        changed |= Slider(Loc.T("补光##fillScale"), ref fill, 0f, 1.5f, "×%.2f",
                          Loc.T("补光（主光以外、在脸前面的灯）的亮度倍数：小了光比更大、更有立体感；场景本身已经很亮时调小"), d.Fill);
        if (!changed) return;
        _service.Tweaks = t with { Brightness = brightness, Warmth = warmth, Distance = distance, Fill = fill };
        _service.Refresh();
    }

    private void DrawLights()
    {
        if (_openSelected) ImGui.SetNextItemOpen(true);   // 3D 布光窗口点了灯：把「逐盏调整」也展开
        if (!ImGui.CollapsingHeader(Loc.T("逐盏调整"))) return;
        if (_service.Active is null)
        {
            ImGui.TextDisabled(Loc.T("先在上面选一个方案，或者从空白开始加灯："));
            DrawAddLight();
            return;
        }
        DrawGroupPicker();
        DrawAddLight();
        ImGui.SameLine();
        using (ImRaii.Disabled(!_service.IsRigModified))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.UndoAlt, Loc.T("整套恢复原样")))
            {
                _service.Checkpoint();
                _service.ResetRig();
                SetStatus(Loc.T("已恢复成刚套方案时的样子"), Grey);
            }
        Tip(_service.IsManual
                ? Loc.T("这些灯是从空白开始加的，没有原样可回；先在下面存成我的方案，之后就能恢复成存的样子")
                : Loc.T("所有灯恢复成刚套方案时的样子：逐盏改的参数、加删的灯、单独调过的套、关掉的灯都去掉（整体亮度这些不动）"));
        var changed = false;
        for (var i = 0; i < _service.Lights.Count; i++)
        {
            var l = _service.Lights[i];
            using var id = ImRaii.PushId(i);
            var selected = _service.SelectedLight == i;
            if (selected && _openSelected) ImGui.SetNextItemOpen(true);
            var flags = ImGuiTreeNodeFlags.AllowItemOverlap | (selected ? ImGuiTreeNodeFlags.Selected : 0);
            var open = ImGui.TreeNodeEx(LightRigs.DisplayName(l.Name) + (_service.IsModified(i) ? " *" : "") + "###light", flags);
            if (ImGui.IsItemClicked()) _service.SelectedLight = i;
            var action = DrawLightButtons(i);
            if (action == LightAction.Removed)
            {
                if (open) ImGui.TreePop();
                break;   // 下标变了，下一帧再画
            }
            changed |= action == LightAction.Changed;
            if (!open) continue;
            l = _service.Lights[i];   // 可能刚恢复过原样
            var az = l.Azimuth;
            var el = l.Elevation;
            var dist = l.Distance;
            var kelvin = l.Kelvin;
            var power = l.Power;
            var angle = l.SpotAngle;
            var soft = l.Softness;
            var shadows = l.Shadows;
            var aimYaw = l.AimYaw;
            var aimPitch = l.AimPitch;
            var kind = (int)l.Kind - (int)LightKind.Point;
            var effect = (int)l.Effect;
            var speed = l.EffectSpeed;
            var amount = l.EffectAmount;
            var c = false;
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            c |= Discrete(ImGui.Combo(Loc.T("种类"), ref kind, [Loc.T("点光"), Loc.T("聚光"), Loc.T("面光")], 3));
            ImGui.SameLine();
            c |= Discrete(ImGui.Checkbox(Loc.T("投影"), ref shadows));
            c |= Slider(Loc.T("方位"), ref az, -180f, 180f, "%.0f°", Loc.T("0 = 从镜头这边照，正值偏向脸转过去的一侧，±90 = 正侧面，±180 = 正后方"));
            c |= Slider(Loc.T("仰角"), ref el, -60f, 85f, "%.0f°", Loc.T("正值从上往下照，负值从下往上"));
            c |= Slider(Loc.T("距离"), ref dist, 0.2f, 8f, "%.2f m");
            c |= Slider(Loc.T("亮度"), ref power, 0f, 3f, "%.2f");
            if (l.Rgb is null)
                c |= Slider(Loc.T("色温"), ref kelvin, 1500f, 12000f, "%.0f K");
            if (kind != 0)
            {
                c |= Slider(Loc.T("光斑角度"), ref angle, 5f, 120f, "%.0f°");
                c |= Slider(Loc.T("边缘柔和"), ref soft, 0f, 60f, "%.0f°");
                c |= Slider(Loc.T("灯头左右"), ref aimYaw, -90f, 90f, "%.0f°", Loc.T("灯头偏离「对准脸」的角度（3D 视图里拖菱形点也是改这个）。右键归零"), 0f);
                c |= Slider(Loc.T("灯头上下"), ref aimPitch, -90f, 90f, "%.0f°", Loc.T("正值往上偏。右键归零"), 0f);
            }
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            c |= Discrete(ImGui.Combo(Loc.T("动态"), ref effect, [Loc.T("无"), Loc.T("闪烁"), Loc.T("呼吸"), Loc.T("环绕"), Loc.T("变色")], 5));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("闪烁：烛火、坏灯管那样不规则地跳；呼吸：亮暗缓慢起伏；环绕：绕着脸转圈；变色：色相循环（霓虹）"));
            if (effect != 0)
            {
                c |= Slider(Loc.T("快慢"), ref speed, 0.1f, 5f, "×%.1f");
                c |= Slider(Loc.T("幅度"), ref amount, 0f, 1f, "%.2f",
                            Loc.T("闪烁、呼吸：暗下去多少；环绕：转多快；变色：颜色多鲜艳"));
            }
            if (c)
            {
                _service.Lights[i] = l with
                {
                    Kind = (LightKind)(kind + (int)LightKind.Point), Azimuth = az, Elevation = el, Distance = dist, Kelvin = kelvin,
                    Power = power, SpotAngle = angle, Softness = soft, Shadows = shadows, AimYaw = aimYaw, AimPitch = aimPitch,
                    Effect = (LightEffect)effect, EffectSpeed = speed, EffectAmount = amount,
                };
                _service.SelectedLight = i;
                changed = true;
            }
            ImGui.TreePop();
        }
        if (changed) _service.Refresh();
        _openSelected = false;
        ImGui.Spacing();
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##saveName", Loc.T("方案名字"), ref _saveName, 40);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_saveName)))
            if (ImGui.Button(Loc.T("存成我的方案")))
            {
                _service.SaveCustom(_saveName.Trim());
                _pi.SavePluginConfig(_config);
                SetStatus(Loc.F("已存成「{0}」：换地图、换站位都能直接用；「整套恢复原样」之后回到存的这一份", _saveName.Trim()), Green);
                _saveName = "";
            }
    }

    /// <summary>点一下就改完的控件（下拉框、勾选框）：改了先记一步撤销。</summary>
    private bool Discrete(bool changed)
    {
        if (changed) _service.Checkpoint();
        return changed;
    }

    /// <summary>
    /// 「添加灯」按钮和它的菜单（主光、轮廓光、头发光……），灯加进正在调的那一套；还没打光时从空白开始。
    /// 3D 布光窗口灯按钮那一排也有一个。
    /// </summary>
    public void DrawAddLight()
    {
        using (ImRaii.Disabled(_busy))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, Loc.T("添加灯")))
                ImGui.OpenPopup("##addLight");
        Tip(_service.Groups.Count > 1
                ? _service.SelectedGroup is { } g ? Loc.F("加一盏灯到第 {0} 套（只这一套有）", g + 1) : Loc.T("加一盏灯到所有套（单独调过的那几套除外）")
                : Loc.T("加一盏灯：主光、补光、轮廓光、头发光这些常用的灯，加好再逐盏调"));
        if (!ImGui.BeginPopup("##addLight")) return;
        foreach (var preset in LightRigs.Presets)
        {
            if (ImGui.MenuItem(Loc.T(preset.Name)))
            {
                _service.Checkpoint();
                var name = Loc.T(preset.Name);
                Run(() => _service.AddLightAsync(preset, _actor()), Loc.F("已加上「{0}」，在下面逐盏调整里改参数", name));
                _openSelected = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T(preset.Description));
        }
        ImGui.EndPopup();
    }

    private enum LightAction
    {
        None,
        Changed,
        Removed,
    }

    /// <summary>灯名右边：恢复原样、删掉、独显（只亮这盏）、关掉 / 打开。</summary>
    private LightAction DrawLightButtons(int i)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var button = ImGui.GetFrameHeight() + 8 * ImGuiHelpers.GlobalScale;   // 图标按钮比行高略宽
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - button * 4 - spacing * 3);
        var action = LightAction.None;
        using (ImRaii.Disabled(!_service.IsModified(i)))
            if (ImGuiComponents.IconButton("##reset", FontAwesomeIcon.UndoAlt))
            {
                _service.Checkpoint();
                _service.ResetLight(i);
                action = LightAction.Changed;
            }
        Tip(Loc.T("这盏灯恢复原样（方案里的样子，或刚加进来时的样子）"));
        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##remove", FontAwesomeIcon.Trash))
        {
            _service.Checkpoint();
            _service.RemoveLight(i);
            return LightAction.Removed;
        }
        Tip(Loc.T("删掉这盏灯（可以撤销）"));
        ImGui.SameLine();
        var solo = _service.Solo == i;
        using (ImRaii.PushColor(ImGuiCol.Button, PosegraphTheme.Accent with { W = 0.7f }, solo))
            if (ImGuiComponents.IconButton("##solo", FontAwesomeIcon.Crosshairs))
            {
                _service.Solo = solo ? null : i;
                action = LightAction.Changed;
            }
        Tip(solo ? Loc.T("取消独显") : Loc.T("独显：只亮这一盏，看它单独的效果"));
        ImGui.SameLine();
        var muted = _service.Muted.Contains(i);
        if (ImGuiComponents.IconButton("##mute", muted ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye))
        {
            _service.Checkpoint();
            if (!_service.Muted.Remove(i)) _service.Muted.Add(i);
            action = LightAction.Changed;
        }
        Tip(muted ? Loc.T("打开这盏灯") : Loc.T("关掉这盏灯（方案里保留，存方案时照常保存）"));
        return action;
    }

    private static bool Check(string label, bool value, Action<bool> set, string? tip = null)
    {
        var v = value;
        var changed = ImGui.Checkbox(label, ref v);
        if (tip is not null) Tip(tip);
        if (changed) set(v);
        return changed;
    }

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }

    private void DrawPhotoRig(float width)
    {
        var active = _service.Active?.Id == LightRigs.PhotoRigId;
        var hasPhoto = _hasPhoto();
        var colorsW = MathF.Min(width * 0.35f, 140 * ImGuiHelpers.GlobalScale);
        using (ImRaii.PushColor(ImGuiCol.Button, PosegraphTheme.Accent with { W = 0.55f }, active))
        using (ImRaii.Disabled(_busy || !hasPhoto))
            if (ImGui.Button(Loc.T("照片同款") + "##rigPhoto", new Vector2(width - colorsW - ImGui.GetStyle().ItemSpacing.X, 0)))
            {
                _service.Checkpoint();
                Run(ApplyPhotoAsync, Loc.F("已打上「{0}」", Loc.T("照片同款")));
            }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var tip = Loc.T("按主窗口照片里人物脸上的明暗，估出主光从哪来、软硬、冷暖和光比，照着摆灯。\n"
                            + "没有脸（没拍到头、背对、脸太小）时改看身上露出的皮肤；皮肤也少时按衣服的明暗强制估，只当个大概。色温只分得出冷暖。第一次点要算几秒");
            ImGui.SetTooltip(!hasPhoto ? Loc.T("主窗口先识别一张照片") : active ? tip + "\n\n" + _service.Active!.Description : tip);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_busy || !hasPhoto || _service.Active is null))
            if (ImGui.Button(Loc.T("照片配色") + "##rigColors", new Vector2(colorsW, 0)))
            {
                _service.Checkpoint();
                Run(ApplyPhotoColorsAsync, Loc.T("已按照片换了灯的颜色"));
            }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!hasPhoto ? Loc.T("主窗口先识别一张照片")
                             : Loc.T("灯的位置不动，只按照片换颜色：主光用照片主光的颜色，补光用照片里环境光的颜色，\n"
                                     + "背后的灯用照片背景的颜色；照片背景不暗时再加一盏照向身后的背景光。先选一套方案"));
        DrawPhotoSwatches();
    }

    /// <summary>照片里取到的颜色（主光、补光、背景），点过「照片同款」或「照片配色」后显示，看得出取到了什么颜色。</summary>
    private void DrawPhotoSwatches()
    {
        if (_photo is not { Found: true } photo || _photoImage != _imageSha1()) return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Loc.T("照片的颜色："));
        var size = new Vector2(ImGui.GetFrameHeight());
        void Swatch(string name, PhotoColor? color)
        {
            if (color is null) return;
            ImGui.SameLine();
            var rgb = color.Rgb is { Length: 3 } c ? new Vector3(c[0], c[1], c[2]) : LightRigs.KelvinToRgb(color.Kelvin);
            var shown = new Vector4(Gamma(rgb.X), Gamma(rgb.Y), Gamma(rgb.Z), 1);
            ImGui.ColorButton("##sw" + name, shown, ImGuiColorEditFlags.NoTooltip, size);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(color.Tinted ? Loc.F("{0}：彩色", Loc.T(name)) : Loc.F("{0}：约 {1:0} K", Loc.T(name), color.Kelvin));
            ImGui.SameLine();
            ImGui.TextDisabled(Loc.T(name));
        }
        Swatch("主光", photo.Lights is { Length: > 0 } l ? l[0].Color : null);
        Swatch("补光", LightRigs.FillColor(photo));
        Swatch("照片背景", photo.Background);
    }

    private static float Gamma(float linear) => MathF.Pow(Math.Clamp(linear, 0f, 1f), 1 / 2.2f);

    private async Task<string?> ApplyPhotoColorsAsync()
    {
        var photo = await FetchPhotoAsync();
        if (photo is null) return Loc.T("主窗口先识别一张照片");
        if (!photo.Found) return Loc.F("照片里估不出光：{0}", photo.Reason);
        var (changed, added) = _service.ApplyPhotoColors(photo);
        if (changed == 0 && added == 0)
            return Loc.T("颜色没变：现在的灯已经是照片的颜色（「照片同款」本来就带照片配色）");
        _ok = added > 0 ? Loc.F("已按照片换了 {0} 盏灯的颜色，加了背景光", changed) : Loc.F("已按照片换了 {0} 盏灯的颜色", changed);
        return null;
    }

    /// <summary>从主窗口的照片估光，记下来画颜色色块。</summary>
    private async Task<PhotoLighting?> FetchPhotoAsync()
    {
        var image = _imageSha1();
        var photo = await _photoLighting();
        (_photo, _photoImage) = (photo, image);
        return photo;
    }

    private async Task<string?> ApplyPhotoAsync()
    {
        var photo = await FetchPhotoAsync();
        if (photo is null) return Loc.T("主窗口先识别一张照片");
        if (!photo.Found) return Loc.F("照片里估不出光：{0}", photo.Reason);
        return await _service.ApplyAsync(LightRigs.FromPhoto(photo), _actor());
    }

    private void Apply(LightRig rig)
    {
        _service.Checkpoint();
        Run(() => _service.ApplyAsync(rig, _actor()), Loc.F("已打上「{0}」", Loc.T(rig.Name)));
    }

    private void Run(Func<Task<string?>> action, string ok)
    {
        _busy = true;
        Task.Run(async () =>
        {
            try
            {
                _ok = null;
                var error = await action();
                SetStatus(error ?? _ok ?? ok, error is null ? Green : Yellow);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, Yellow);
            }
            finally { _busy = false; }
        });
    }

    private void SetStatus(string text, Vector4 color) => (_status, _statusColor) = (text, color);

    /// <summary>滑杆：开始拖的那一下记一步撤销；给了 reset 时右键恢复成它。</summary>
    private bool Slider(string label, ref float value, float min, float max, string format, string? tip = null, float? reset = null)
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(label).X - ImGui.GetStyle().ItemInnerSpacing.X * 2);
        var changed = ImGui.SliderFloat(label, ref value, min, max, format);
        if (ImGui.IsItemActivated()) _service.Checkpoint();
        if (reset is { } r && ImGui.IsItemClicked(ImGuiMouseButton.Right) && value != r)
        {
            _service.Checkpoint();
            value = r;
            changed = true;
        }
        if (tip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        return changed;
    }
}
