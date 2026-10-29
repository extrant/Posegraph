using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Posegraph.Lighting;

namespace Posegraph.UI;

/// <summary>
/// 3D 布光窗口：竖着贴在灯光窗口右边，高度跟灯光窗口一样，宽度可以拖；在灯光窗口里点「3D 布光」展开 / 收起，
/// 灯光窗口关着时不显示。里面是视角按钮、专业选项、3D 视图（拖灯、拖对准点，见 LightingScene）和每盏灯一个按钮。
/// </summary>
public sealed class LightingSceneWindow : Window
{
    private static readonly Vector4 Grey = PosegraphTheme.Grey;

    private readonly LightingService _service;
    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly LightingWindow _lighting;
    private readonly LightingScene _scene;

    public LightingSceneWindow(LightingService service, Configuration config, IDalamudPluginInterface pi, LightingWindow lighting)
        : base(Loc.T("3D 布光") + "###PosegraphLightingScene", ImGuiWindowFlags.NoCollapse)
    {
        _service = service;
        _config = config;
        _pi = pi;
        _lighting = lighting;
        _scene = new LightingScene(service, config);
        Size = new Vector2(460, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = config.LightSceneOpen;
    }

    public override bool DrawConditions() => _lighting.IsOpen;

    public override void PreDraw()
    {
        // 贴在灯光窗口右边、和它一样高；宽度自己拖
        if (_lighting.WindowSize.X > 0)
        {
            Position = _lighting.WindowPos + new Vector2(_lighting.WindowSize.X + 4, 0);
            PositionCondition = ImGuiCond.Always;
            var height = _lighting.WindowSize.Y;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(300, height), MaximumSize = new Vector2(2400, height) };
        }
        PosegraphTheme.Push();
    }

    public override void PostDraw()
    {
        PosegraphTheme.Pop();
        RaiseTogether();
    }

    /// <summary>
    /// 两个窗口一起前置：点中其中一个，另一个也提到最上面，焦点留在点中的那个。
    /// 等松开鼠标再做——按下的那一帧就换焦点会打断正在拖的灯。灯光窗口先画，这里两个窗口的焦点都已更新。
    /// </summary>
    private void RaiseTogether()
    {
        Window? focused = IsFocused ? this : _lighting.IsFocused ? _lighting : null;
        if (focused is not null && focused != _lastFocused) _raise = focused;
        _lastFocused = focused;
        if (_raise is not { } target || ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        (target == this ? (Window)_lighting : this).BringToFront();
        target.BringToFront();   // 后提的拿到焦点
        _raise = null;
    }

    private Window? _lastFocused;
    private Window? _raise;   // 松开鼠标后要一起前置的（被点中的那个窗口）

    public override void OnOpen() => Remember(true);

    public override void OnClose() => Remember(false);

    private void Remember(bool open)
    {
        if (_config.LightSceneOpen == open) return;
        _config.LightSceneOpen = open;
        _pi.SavePluginConfig(_config);
    }

    public override void Draw()
    {
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
        DrawProOptions();
        // 3D 视图用满剩下的高度，留出下面灯按钮的一行
        var height = Math.Max(200 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 1.5f);
        _scene.Draw(new Vector2(ImGui.GetContentRegionAvail().X, height));
        DrawLightChips();
    }

    private void DrawProOptions()
    {
        if (!ImGui.BeginPopup("##lightPro")) return;
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

    /// <summary>3D 视图下面一排：每盏灯一个按钮（点了选中，并在灯光窗口的「逐盏调整」里展开参数），右键独显。</summary>
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
                    _lighting.OpenLight(selected ? null : i);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _service.Solo = _service.Solo == i ? null : i;
                changed = true;
            }
            Tip(Loc.T("点击选中（灯光窗口里展开参数）；右键独显这盏灯"));
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
}
