using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Posegraph.Lighting;

namespace Posegraph.UI;

/// <summary>
/// 灯光窗口：上面是方案（点一下就给当前角色打好光），中间是整体亮度 / 冷暖 / 灯距，
/// 然后是独立的 3D 布光视图（拖灯、拖对准点、光锥和光照预览球，见 LightingScene），
/// 下面「逐盏调整」给熟悉打光的人：每盏灯的方位、仰角、距离、颜色、光斑、灯头偏角都能改，可独显 / 关掉，改好可存成自己的方案。
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
    private readonly LightingScene _scene;
    private string _status = "";
    private Vector4 _statusColor = Grey;
    private bool _busy;
    private string _saveName = "";
    private bool _openSelected;   // 下一帧把选中的灯在「逐盏调整」里展开

    public LightingWindow(LightingService service, Configuration config, IDalamudPluginInterface pi, Func<int?> actor, Func<bool> inGpose)
        : base(Loc.T("Posegraph 灯光") + "###PosegraphLighting")
    {
        _service = service;
        _config = config;
        _pi = pi;
        _actor = actor;
        _inGpose = inGpose;
        _scene = new LightingScene(service, config);
        Size = new Vector2(540, 900);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 360), MaximumSize = new Vector2(4000, 4000) };
    }

    public override void PreDraw() => PosegraphTheme.Push();

    public override void PostDraw() => PosegraphTheme.Pop();

    public override void Draw()
    {
        if (!_inGpose())
        {
            ImGui.TextColored(Yellow, Loc.T("进入 GPose 后才能打光"));
            return;
        }
        DrawRigs();
        ImGui.Separator();
        DrawTweaks();
        DrawScene();
        DrawLights();
        if (_status.Length > 0) ImGui.TextColored(_statusColor, _status);
    }

    private void DrawRigs()
    {
        ImGui.TextDisabled(Loc.T("点一个方案，给主窗口当前的角色打光（灯跟着脸和镜头摆）"));
        var width = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(2, (int)(width / (130 * ImGuiHelpers.GlobalScale)));
        var buttonW = (width - ImGui.GetStyle().ItemSpacing.X * (columns - 1)) / columns;
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
        using (ImRaii.Disabled(_busy || _service.Active is null))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, Loc.T("重新摆")))
                Run(() => _service.ReplaceAsync(_actor()), Loc.T("已按角色现在的位置重新摆"));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Loc.T("角色挪动、换了姿势后点这里：重新读脸的位置和朝向"));
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Trash, Loc.T("关掉所有灯")))
            {
                _service.Clear();
                SetStatus(Loc.T("已关掉 Posegraph 打的灯"), Grey);
            }
        }
        ImGui.SameLine();
        var follow = _config.LightFollowCamera;
        if (ImGui.Checkbox(Loc.T("跟随镜头"), ref follow))
        {
            _config.LightFollowCamera = follow;
            _pi.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("转镜头时灯跟着转，始终从镜头这一侧照亮脸"));
    }

    private void DrawTweaks()
    {
        var t = _service.Tweaks;
        var changed = false;
        var brightness = t.Brightness;
        var warmth = t.Warmth;
        var distance = t.Distance;
        changed |= Slider(Loc.T("整体亮度"), ref brightness, 0.1f, 3f, "%.2f");
        changed |= Slider(Loc.T("冷暖"), ref warmth, -2500f, 2500f, warmth >= 0 ? "+%.0f K" : "%.0f K", Loc.T("负值更暖、正值更冷（加在每盏灯的色温上）"));
        changed |= Slider(Loc.T("灯距"), ref distance, 0.5f, 2.5f, "×%.2f", Loc.T("所有灯离脸的距离倍数：近了光更硬更集中"));
        if (!changed) return;
        _service.Tweaks = new RigTweaks(brightness, warmth, distance);
        _service.Refresh();
    }

    private void DrawLights()
    {
        if (!ImGui.CollapsingHeader(Loc.T("逐盏调整"))) return;
        if (_service.Lights.Count == 0)
        {
            ImGui.TextDisabled(Loc.T("先在上面选一个方案"));
            return;
        }
        var changed = false;
        for (var i = 0; i < _service.Lights.Count; i++)
        {
            var l = _service.Lights[i];
            using var id = ImRaii.PushId(i);
            var selected = _service.SelectedLight == i;
            if (selected && _openSelected) ImGui.SetNextItemOpen(true);
            var flags = ImGuiTreeNodeFlags.AllowItemOverlap | (selected ? ImGuiTreeNodeFlags.Selected : 0);
            var open = ImGui.TreeNodeEx(Loc.T(l.Name) + "###light", flags);
            if (ImGui.IsItemClicked()) _service.SelectedLight = i;
            changed |= DrawSoloMute(i);
            if (!open) continue;
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
            var c = false;
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            c |= ImGui.Combo(Loc.T("种类"), ref kind, [Loc.T("点光"), Loc.T("聚光"), Loc.T("面光")], 3);
            ImGui.SameLine();
            c |= ImGui.Checkbox(Loc.T("投影"), ref shadows);
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
                c |= Slider(Loc.T("灯头左右"), ref aimYaw, -90f, 90f, "%.0f°", Loc.T("灯头偏离「对准脸」的角度（3D 视图里拖菱形点也是改这个）。右键归零"));
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) { aimYaw = 0; c = true; }
                c |= Slider(Loc.T("灯头上下"), ref aimPitch, -90f, 90f, "%.0f°", Loc.T("正值往上偏。右键归零"));
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) { aimPitch = 0; c = true; }
            }
            if (c)
            {
                _service.Lights[i] = l with
                {
                    Kind = (LightKind)(kind + (int)LightKind.Point), Azimuth = az, Elevation = el, Distance = dist, Kelvin = kelvin,
                    Power = power, SpotAngle = angle, Softness = soft, Shadows = shadows, AimYaw = aimYaw, AimPitch = aimPitch,
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
                SetStatus(Loc.F("已存成「{0}」：换地图、换站位都能直接用", _saveName.Trim()), Green);
                _saveName = "";
            }
    }

    /// <summary>3D 布光：视角按钮、专业选项，下面是 3D 视图（LightingScene）和每盏灯的快捷条。</summary>
    private void DrawScene()
    {
        if (!ImGui.CollapsingHeader(Loc.T("3D 布光"), ImGuiTreeNodeFlags.DefaultOpen)) return;
        var subject = _service.Subject;
        using (ImRaii.Disabled(subject is null))
        {
            if (ImGui.SmallButton(Loc.T("镜头视角")) && _service.CameraPose is { } pose) _scene.LookFrom(-pose.Forward);
            Tip(Loc.T("从游戏镜头的方向看（和画面里看到的一致）"));
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("正面")) && subject is { } s1) _scene.LookFrom(s1.Forward);
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("侧面")) && subject is { } s2) _scene.LookFrom(Vector3.Cross(Vector3.UnitY, s2.Forward));
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("顶视")) && subject is { } s3) _scene.LookFrom(Vector3.UnitY * 10 + s3.Forward * 0.01f);
            Tip(Loc.T("从正上方看：最容易看清每盏灯的方位"));
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("全部框住"))) _scene.FrameAll();
            Tip(Loc.T("把所有灯和镜头都框进视图"));
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("全部对准脸")))
            {
                for (var i = 0; i < _service.Lights.Count; i++) _service.Lights[i] = _service.Lights[i] with { AimYaw = 0, AimPitch = 0 };
                _service.Refresh();
            }
            Tip(Loc.T("清掉所有灯的灯头偏角，让聚光、面光都正对脸"));
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("专业选项") + "##proLight")) ImGui.OpenPopup("##lightPro");
        if (ImGui.BeginPopup("##lightPro"))
        {
            var save = false;
            save |= Check(Loc.T("聚光锥 / 面光方向"), _config.LightShowCones, v => _config.LightShowCones = v);
            save |= Check(Loc.T("柔和边（近似）"), _config.LightShowSoftEdge, v => _config.LightShowSoftEdge = v,
                          Loc.T("光斑角度外再加上边缘柔和的范围（按游戏参数的近似画法）"));
            save |= Check(Loc.T("游戏镜头"), _config.LightShowCamera, v => _config.LightShowCamera = v);
            save |= Check(Loc.T("选中灯的轨道"), _config.LightShowOrbit, v => _config.LightShowOrbit = v,
                          Loc.T("以脸为中心、过这盏灯的圈：默认拖灯时灯沿这个球面走"));
            save |= Check(Loc.T("光照预览球和光比"), _config.LightShowPreview, v => _config.LightShowPreview = v,
                          Loc.T("从镜头看一颗头大小的白球被这套灯照成的样子，下面是左右脸的光比（近似，不是游戏渲染）"));
            ImGui.Separator();
            save |= Check(Loc.T("自由移动"), _config.LightFreeMove, v => _config.LightFreeMove = v,
                          Loc.T("拖灯时在屏幕平面上自由移动（会改距离）；关掉时沿以脸为中心的球面走，距离不变"));
            save |= Check(Loc.T("角度吸附"), _config.LightSnap, v => _config.LightSnap = v,
                          Loc.T("拖灯时方位 / 仰角按 15° 吸附，灯头偏角按 5° 吸附"));
            if (save) _pi.SavePluginConfig(_config);
            ImGui.EndPopup();
        }
        // 用满剩下的高度（留出灯按钮一行和「逐盏调整」标题），展开逐盏调整后窗口滚动
        var height = Math.Max(260 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 3);
        _scene.Draw(new Vector2(ImGui.GetContentRegionAvail().X, height));
        DrawLightChips();
    }

    /// <summary>3D 视图下面一排：每盏灯一个按钮（点了选中并展开下面的参数），旁边独显 / 关掉。</summary>
    private void DrawLightChips()
    {
        var changed = false;
        for (var i = 0; i < _service.Lights.Count; i++)
        {
            using var id = ImRaii.PushId(1000 + i);
            if (i > 0) ImGui.SameLine();
            var selected = _service.SelectedLight == i;
            using (ImRaii.PushColor(ImGuiCol.Button, PosegraphTheme.Accent with { W = 0.6f }, selected))
            using (ImRaii.PushColor(ImGuiCol.Text, Grey, !_service.IsLit(i)))
                if (ImGui.SmallButton(Loc.T(_service.Lights[i].Name)))
                {
                    _service.SelectedLight = selected ? null : i;
                    _openSelected = !selected;
                }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _service.Solo = _service.Solo == i ? null : i;
                changed = true;
            }
            Tip(Loc.T("点击选中（下面展开参数）；右键独显这盏灯"));
        }
        if (_service.Solo is not null || _service.Muted.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("全部点亮")))
            {
                _service.Solo = null;
                _service.Muted.Clear();
                changed = true;
            }
        }
        if (changed) _service.Refresh();
    }

    /// <summary>灯名右边：独显（只亮这盏）、关掉 / 打开。返回是否有改动。</summary>
    private bool DrawSoloMute(int i)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var button = ImGui.GetFrameHeight() + 8 * ImGuiHelpers.GlobalScale;   // 图标按钮比行高略宽
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - button * 2 - spacing);
        var changed = false;
        var solo = _service.Solo == i;
        using (ImRaii.PushColor(ImGuiCol.Button, PosegraphTheme.Accent with { W = 0.7f }, solo))
            if (ImGuiComponents.IconButton("##solo", FontAwesomeIcon.Crosshairs))
            {
                _service.Solo = solo ? null : i;
                changed = true;
            }
        Tip(solo ? Loc.T("取消独显") : Loc.T("独显：只亮这一盏，看它单独的效果"));
        ImGui.SameLine();
        var muted = _service.Muted.Contains(i);
        if (ImGuiComponents.IconButton("##mute", muted ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye))
        {
            if (!_service.Muted.Remove(i)) _service.Muted.Add(i);
            changed = true;
        }
        Tip(muted ? Loc.T("打开这盏灯") : Loc.T("关掉这盏灯（方案里保留，存方案时照常保存）"));
        return changed;
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

    private void Apply(LightRig rig) => Run(() => _service.ApplyAsync(rig, _actor()), Loc.F("已打上「{0}」", Loc.T(rig.Name)));

    private void Run(Func<Task<string?>> action, string ok)
    {
        _busy = true;
        Task.Run(async () =>
        {
            try
            {
                var error = await action();
                SetStatus(error ?? ok, error is null ? Green : Yellow);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, Yellow);
            }
            finally { _busy = false; }
        });
    }

    private void SetStatus(string text, Vector4 color) => (_status, _statusColor) = (text, color);

    private static bool Slider(string label, ref float value, float min, float max, string format, string? tip = null)
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(label).X - ImGui.GetStyle().ItemInnerSpacing.X * 2);
        var changed = ImGui.SliderFloat(label, ref value, min, max, format);
        if (tip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        return changed;
    }
}
