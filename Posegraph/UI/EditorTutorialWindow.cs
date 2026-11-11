using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace Posegraph.UI;

/// <summary>
/// 姿势编辑器（游戏内编辑）的教程：分页，每页一张示意图（ImGui 现画，跟着时间动）和几条说明。
/// 第一次用时自动弹出，要按住 5 秒才能关（看完了再关）；之后从「教程」按钮打开，随时能关。
/// </summary>
public sealed class EditorTutorialWindow : Window
{
    private const float HoldSeconds = 5f;

    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private bool _firstTime;
    private int _page;
    private float _hold;

    // 编辑器里节点的颜色（和 PoseEditor 画的一样）
    private static readonly Vector4 LimbColor = new(0.25f, 0.8f, 1f, 1f);
    private static readonly Vector4 PoleColor = new(0.6f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 AimColor = new(0.95f, 0.95f, 0.95f, 1f);
    private static readonly Vector4 SideColor = new(0.75f, 0.6f, 1f, 1f);
    private static readonly Vector4 SelectColor = new(1f, 0.6f, 0.1f, 1f);
    private static readonly Vector4 ContactColor = new(0.3f, 1f, 0.6f, 1f);
    private static readonly Vector4[] AxisColors = [new(1f, 0.3f, 0.3f, 1f), new(0.35f, 1f, 0.35f, 1f), new(0.35f, 0.6f, 1f, 1f)];

    private sealed record Page(string Title, string[] Lines, Action<ImDrawListPtr, Vector2, Vector2, float> Picture);

    public EditorTutorialWindow(Configuration config, IDalamudPluginInterface pi)
        : base(Loc.T("姿势编辑器教程") + "###PosegraphEditorTutorial",
               ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoDocking)
    {
        _config = config;
        _pi = pi;
        Size = new Vector2(680, 620);
        SizeCondition = ImGuiCond.Always;
    }

    /// <summary>每次画的时候现取（切换语言后跟着变）。</summary>
    private static Page[] Pages =>
    [
        new(Loc.T("开始编辑"),
            [
                Loc.T("进 GPose，在主窗口 3D 页点「在游戏里编辑」，角色身上会出现一组圆点（控制点）。"),
                Loc.T("蓝色是手和脚（拖它整条胳膊 / 腿跟着走），绿色是肘和膝（调弯的朝向），白色是骨盆、腰、胸、头，紫色是肩和脚尖。"),
                Loc.T("点变黄 / 变红表示关节超出了游戏动画常见的范围；勾上「限制在正常范围内」会直接限住。"),
                Loc.T("改完点「完成」，改动会存进这张图的修正；点「取消」放弃这次的全部改动。"),
            ], DrawNodes),
        new(Loc.T("选中和直接拖"),
            [
                Loc.T("单击圆点选中它（外面多一圈橙色）。按住直接拖就是自由模式：手脚用 IK 跟着鼠标，肘膝调朝向，腰胸头转向鼠标，骨盆平移整个人。"),
                Loc.T("选中手时会显示五根手指的点：指尖往掌心拖是握拳，往外拖是伸直。选中头时会显示视线点。"),
                Loc.T("拖动时按住 Shift 微调（鼠标只走 1/10），右键取消这一下。"),
            ], DrawDrag),
        new(Loc.T("G 移动、R 旋转"),
            [
                Loc.T("选中后按 G 移动、按 R 旋转，然后移动鼠标；左键或回车确定，右键或 Esc 取消。"),
                Loc.T("移动时从起点到现在的位置画一条虚线，旁边写移动了多少厘米；旋转时节点周围画一个圈，扇形填充的部分就是转过的角度，旁边写度数。"),
                Loc.T("可以直接敲数字：移动是厘米，旋转是度（例如 R、Z、9、0 就是绕 Z 转 90°）。按住 Shift 微调。"),
            ], DrawMoveRotate),
        new(Loc.T("X / Y / Z 锁轴：全局还是骨骼自身"),
            [
                Loc.T("G 或 R 的时候按 X / Y / Z 锁住一根轴，这时才会画出方向提示：一条贯穿画面的辅助线、箭头指着正方向，旁边写着是哪种轴。"),
                Loc.T("第一次按是全局轴（实线）：X 是角色的左右，Y 是上下，Z 是前后，和骨骼怎么歪都没关系。"),
                Loc.T("同一个键再按一次是骨骼自身的轴（虚线）：跟着这根骨骼的朝向走，比如沿着前臂拧手腕。第三次取消锁定。"),
                Loc.T("节点旁边会画出这一组轴的三个方向（红 X、绿 Y、蓝 Z），锁住的那根粗一些。"),
            ], DrawAxes),
        new(Loc.T("看角度、输入角度"),
            [
                Loc.T("选中节点后，面板里显示这根骨骼现在的 X / Y / Z 角度（相对它的上一级骨骼）。按住数字左右拖可以调，Ctrl+单击可以直接输入。"),
                Loc.T("旋转的时候鼠标旁边还会显示这一下 X / Y / Z 各改了多少。"),
            ], DrawAngles),
        new(Loc.T("钉住、镜像、撤销"),
            [
                Loc.T("P 钉住选中的手 / 脚（方框标记）：之后动身体它不动。双脚默认钉住，弯腰、蹲下时脚不会离地。"),
                Loc.T("M 把选中这一侧（胳膊或腿）镜像到另一侧；勾「对称编辑」时两边一起动。"),
                Loc.T("Ctrl+Z 撤销，Ctrl+Y 重做；Esc 取消选中。"),
            ], DrawPins),
        new(Loc.T("手部吸附和「贴到部位」（实验）"),
            [
                Loc.T("勾上「手部吸附」后，拖手靠近身体（自己的或 GPose 里别人的），手掌会贴上去、手指顺着表面弯，绿色小圈是贴着的地方。拖动时按住 Alt 临时反转。"),
                Loc.T("「贴到部位…」是实验功能（按钮旁有黄色感叹号）：选中一只手，点它，再点身上想摸的地方，手会自己贴过去。"),
                Loc.T("推荐的用法：先把手大致摆到那附近、掌心朝着那里，再点「贴到部位」。它只做小修正，尽量保持你摆的姿势；够不着或要拧着才够得着时不会动，状态栏会说明原因（比如换另一只手）。"),
                Loc.T("不满意就 Ctrl+Z 撤销。"),
            ], DrawSnap),
    ];

    /// <summary>打开教程。firstTime：第一次用，要按住 5 秒才能关。</summary>
    public void Show(bool firstTime)
    {
        _firstTime = firstTime || (_firstTime && IsOpen);
        _page = 0;
        _hold = 0;
        ShowCloseButton = !_firstTime;
        RespectCloseHotkey = !_firstTime;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        PosegraphTheme.Push();
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    public override void PostDraw() => PosegraphTheme.Pop();

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var pages = Pages;
        _page = Math.Clamp(_page, 0, pages.Length - 1);
        var page = pages[_page];

        // 页码点（可以点）+ 标题
        var dl = ImGui.GetWindowDrawList();
        for (var i = 0; i < pages.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 4 * scale);
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"##page{i}", new Vector2(12, ImGui.GetTextLineHeight()) * new Vector2(scale, 1))) _page = i;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(pages[i].Title);
            dl.AddCircleFilled(pos + new Vector2(6 * scale, ImGui.GetTextLineHeight() / 2), 4 * scale,
                               ImGui.GetColorU32(i == _page ? PosegraphTheme.Accent : PosegraphTheme.Grey with { W = 0.5f }));
        }
        ImGui.SameLine(0, 12 * scale);
        ImGui.TextColored(PosegraphTheme.Accent, $"{_page + 1} / {pages.Length}   {page.Title}");
        ImGui.Spacing();

        var footer = ImGui.GetFrameHeight() * 1.4f + ImGui.GetStyle().ItemSpacing.Y * 2;
        using (var child = ImRaii.Child("##tutorial", new Vector2(0, -footer), false))
        {
            if (child)
            {
                var width = ImGui.GetContentRegionAvail().X;
                var origin = ImGui.GetCursorScreenPos();
                var size = new Vector2(width, 260 * scale);
                var cdl = ImGui.GetWindowDrawList();
                cdl.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(0.08f, 0.09f, 0.11f, 1f)), 6 * scale);
                cdl.PushClipRect(origin, origin + size, true);
                page.Picture(cdl, origin, size, scale);
                cdl.PopClipRect();
                ImGui.Dummy(size);
                ImGui.Spacing();
                foreach (var line in page.Lines)
                {
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.PushTextWrapPos(0);
                    ImGui.TextUnformatted(UiText.CjkWrap(line));
                    ImGui.PopTextWrapPos();
                }
            }
        }

        // 翻页 + 关闭
        var button = new Vector2(100 * scale, ImGui.GetFrameHeight() * 1.4f);
        using (ImRaii.Disabled(_page == 0))
            if (ImGui.Button(Loc.T("上一页"), button)) _page--;
        ImGui.SameLine();
        using (ImRaii.Disabled(_page >= pages.Length - 1))
            if (ImGui.Button(Loc.T("下一页"), button)) _page++;
        ImGui.SameLine();
        var closeW = 240 * scale;
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - closeW);
        if (_firstTime)
        {
            if (HoldButton(Loc.T("按住 5 秒关闭（我看完了）"), new Vector2(closeW, button.Y)))
            {
                _config.EditorTutorialSeen = true;
                _pi.SavePluginConfig(_config);
                _firstTime = false;
                IsOpen = false;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("第一次用请先看完：按住这个按钮 5 秒关闭。以后可以在 3D 页点「教程」再看"));
        }
        else if (ImGui.Button(Loc.T("关闭"), new Vector2(closeW, button.Y))) IsOpen = false;
    }

    private bool HoldButton(string label, Vector2 size)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Button(label, size);
        _hold = ImGui.IsItemActive() ? _hold + ImGui.GetIO().DeltaTime : 0;
        if (_hold > 0)
        {
            var dl = ImGui.GetWindowDrawList();
            var fill = Math.Min(1, _hold / HoldSeconds);
            dl.AddRectFilled(pos, pos + new Vector2(size.X * fill, size.Y), ImGui.GetColorU32(PosegraphTheme.Accent with { W = 0.45f }),
                             ImGui.GetStyle().FrameRounding);
            var left = $"{Math.Max(0, HoldSeconds - _hold):0.0}s";
            dl.AddText(pos + new Vector2(size.X - ImGui.CalcTextSize(left).X - 8, (size.Y - ImGui.GetTextLineHeight()) / 2),
                       ImGui.GetColorU32(Vector4.One), left);
        }
        if (_hold < HoldSeconds) return false;
        _hold = 0;
        return true;
    }

    // ------------------------------------------------------------------ 示意图

    private static float Time => (float)ImGui.GetTime();

    private static uint U32(Vector4 c) => ImGui.GetColorU32(c);

    /// <summary>站着的小人（面朝屏幕）：关节位置以 center 为原点、unit 为一个单位（约半个身高）。</summary>
    private sealed class Figure
    {
        public readonly Dictionary<string, Vector2> P = new()
        {
            ["head"] = new(0, -1.62f), ["neck"] = new(0, -1.36f), ["chest"] = new(0, -1.08f), ["waist"] = new(0, -0.66f), ["pelvis"] = new(0, -0.36f),
            ["shoulder_r"] = new(-0.32f, -1.24f), ["elbow_r"] = new(-0.44f, -0.8f), ["hand_r"] = new(-0.5f, -0.36f),
            ["shoulder_l"] = new(0.32f, -1.24f), ["elbow_l"] = new(0.44f, -0.8f), ["hand_l"] = new(0.5f, -0.36f),
            ["hip_r"] = new(-0.17f, -0.32f), ["knee_r"] = new(-0.2f, 0.28f), ["foot_r"] = new(-0.22f, 0.86f), ["toe_r"] = new(-0.34f, 0.92f),
            ["hip_l"] = new(0.17f, -0.32f), ["knee_l"] = new(0.2f, 0.28f), ["foot_l"] = new(0.22f, 0.86f), ["toe_l"] = new(0.34f, 0.92f),
        };

        /// <summary>把手放到 target（两段 IK）；down 为真时肘往下垂，否则往外弯。</summary>
        public void Reach(string side, Vector2 target, bool down = false)
        {
            var s = P["shoulder_" + side];
            var a = Vector2.Distance(s, P["elbow_" + side]);
            var b = Vector2.Distance(P["elbow_" + side], P["hand_" + side]);
            var d = target - s;
            var len = Math.Clamp(d.Length(), 1e-3f, a + b - 1e-3f);
            var dir = Vector2.Normalize(d);
            var cos = Math.Clamp((a * a + len * len - b * b) / (2 * a * len), -1f, 1f);
            var angle = MathF.Acos(cos);
            Vector2 Turn(float t)
            {
                var (sin, co) = MathF.SinCos(t);
                return new Vector2(dir.X * co - dir.Y * sin, dir.X * sin + dir.Y * co);
            }
            var e1 = Turn(angle);
            var e2 = Turn(-angle);
            var outward = side == "r" ? -1f : 1f;
            var e = down ? (e1.Y > e2.Y ? e1 : e2) : (e1.X * outward > e2.X * outward ? e1 : e2);
            P["elbow_" + side] = s + e * a;
            P["hand_" + side] = s + dir * len;
        }

        public Vector2 At(string name, Vector2 center, float unit) => center + P[name] * unit;

        private static readonly (string A, string B)[] Lines =
        [
            ("neck", "chest"), ("chest", "waist"), ("waist", "pelvis"),
            ("chest", "shoulder_r"), ("shoulder_r", "elbow_r"), ("elbow_r", "hand_r"),
            ("chest", "shoulder_l"), ("shoulder_l", "elbow_l"), ("elbow_l", "hand_l"),
            ("pelvis", "hip_r"), ("hip_r", "knee_r"), ("knee_r", "foot_r"), ("foot_r", "toe_r"),
            ("pelvis", "hip_l"), ("hip_l", "knee_l"), ("knee_l", "foot_l"), ("foot_l", "toe_l"),
        ];

        private static Vector4 ColorOf(string name) => name switch
        {
            _ when name.StartsWith("hand") || name.StartsWith("foot") => LimbColor,
            _ when name.StartsWith("elbow") || name.StartsWith("knee") => PoleColor,
            _ when name.StartsWith("shoulder") || name.StartsWith("toe") => SideColor,
            _ => AimColor,
        };

        private static readonly string[] Nodes =
            ["pelvis", "waist", "chest", "head", "shoulder_r", "shoulder_l", "elbow_r", "elbow_l", "hand_r", "hand_l", "knee_r", "knee_l", "foot_r", "foot_l", "toe_r", "toe_l"];

        public void Draw(ImDrawListPtr dl, Vector2 center, float unit, float scale, float alpha = 1f, string? selected = null)
        {
            var line = U32(new Vector4(1f, 1f, 1f, 0.55f * alpha));
            foreach (var (a, b) in Lines) dl.AddLine(At(a, center, unit), At(b, center, unit), line, 2f * scale);
            dl.AddCircle(At("head", center, unit), 0.2f * unit, line, 0, 2f * scale);
            dl.AddLine(At("neck", center, unit), At("head", center, unit) + new Vector2(0, 0.2f * unit), line, 2f * scale);
            foreach (var n in Nodes)
            {
                var p = At(n, center, unit);
                var r = 5f * scale;
                dl.AddCircleFilled(p, r, U32(ColorOf(n) with { W = 0.85f * alpha }));
                dl.AddCircle(p, r, U32(new Vector4(0, 0, 0, 0.85f * alpha)), 0, 1.5f * scale);
                if (n == selected) dl.AddCircle(p, r + 3.5f * scale, U32(SelectColor), 0, 2.5f * scale);
            }
        }
    }

    private static void Label(ImDrawListPtr dl, Vector2 at, string text, Vector4 color, bool box = true)
    {
        var size = ImGui.CalcTextSize(text);
        if (box) dl.AddRectFilled(at - new Vector2(4, 2), at + size + new Vector2(4, 2), U32(new Vector4(0, 0, 0, 0.7f)), 3f);
        dl.AddText(at, U32(color), text);
    }

    private static float KeyCap(ImDrawListPtr dl, Vector2 at, string key, float scale)
    {
        var size = ImGui.CalcTextSize(key);
        var box = new Vector2(Math.Max(size.X + 12 * scale, 26 * scale), 26 * scale);
        dl.AddRectFilled(at, at + box, U32(new Vector4(1f, 1f, 1f, 0.22f)), 5 * scale);
        dl.AddRect(at, at + box, U32(new Vector4(1f, 1f, 1f, 0.7f)), 5 * scale, ImDrawFlags.None, 1.5f * scale);
        dl.AddText(at + (box - size) / 2, U32(Vector4.One), key);
        return box.X;
    }

    private static void Dashed(ImDrawListPtr dl, Vector2 a, Vector2 b, uint color, float thickness, float dash = 7f, float gap = 5f)
    {
        var length = Vector2.Distance(a, b);
        if (length < 1e-3f) return;
        var dir = (b - a) / length;
        for (var t = 0f; t < length; t += dash + gap)
            dl.AddLine(a + dir * t, a + dir * MathF.Min(t + dash, length), color, thickness);
    }

    private static void Arrow(ImDrawListPtr dl, Vector2 from, Vector2 to, uint color, float thickness, float head)
    {
        dl.AddLine(from, to, color, thickness);
        if (Vector2.Distance(from, to) < 1e-3f) return;
        var d = Vector2.Normalize(to - from);
        var n = new Vector2(-d.Y, d.X);
        dl.AddTriangleFilled(to, to - d * head + n * head * 0.5f, to - d * head - n * head * 0.5f, color);
    }

    /// <summary>鼠标箭头。</summary>
    private static void Cursor(ImDrawListPtr dl, Vector2 tip, float scale)
    {
        Vector2 P(float x, float y) => tip + new Vector2(x, y) * scale;
        var pts = new[] { P(0, 0), P(0, 17), P(4.5f, 13), P(8, 20), P(10.5f, 19), P(7.2f, 12.2f), P(12.5f, 12.2f) };
        dl.AddTriangleFilled(pts[0], pts[1], pts[6], U32(Vector4.One));
        dl.AddTriangleFilled(pts[2], pts[3], pts[4], U32(Vector4.One));
        dl.AddTriangleFilled(pts[2], pts[4], pts[5], U32(Vector4.One));
        for (var i = 0; i < pts.Length; i++) dl.AddLine(pts[i], pts[(i + 1) % pts.Length], U32(new Vector4(0, 0, 0, 1)), 1.2f);
    }

    /// <summary>扇形填充（旋转了多少）：从 start 弧度转 sweep 弧度，屏幕坐标 y 向下。</summary>
    private static void Sector(ImDrawListPtr dl, Vector2 c, float r, float start, float sweep, Vector4 color, float scale)
    {
        dl.AddCircle(c, r, U32(color with { W = 0.6f }), 48, 2f * scale);
        var steps = Math.Max(2, (int)(MathF.Abs(sweep) / 0.08f));
        var prev = c + new Vector2(MathF.Cos(start), MathF.Sin(start)) * r;
        for (var i = 1; i <= steps; i++)
        {
            var a = start + sweep * i / steps;
            var p = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            dl.AddTriangleFilled(c, prev, p, U32(color with { W = 0.3f }));
            dl.AddLine(prev, p, U32(color), 3f * scale);
            prev = p;
        }
        dl.AddLine(c, c + new Vector2(MathF.Cos(start), MathF.Sin(start)) * r, U32(color with { W = 0.8f }), 1.5f * scale);
        dl.AddLine(c, prev, U32(color), 2f * scale);
    }

    private static void Divider(ImDrawListPtr dl, Vector2 o, Vector2 size, float scale)
        => dl.AddLine(o + new Vector2(size.X / 2, 10 * scale), o + new Vector2(size.X / 2, size.Y - 10 * scale), U32(new Vector4(1, 1, 1, 0.1f)), 1f);

    private static void DrawNodes(ImDrawListPtr dl, Vector2 o, Vector2 size, float scale)
    {
        var unit = size.Y / 2.9f;
        var center = o + new Vector2(size.X * 0.25f, size.Y * 0.62f);
        new Figure().Draw(dl, center, unit, scale);
        var x = o.X + size.X * 0.48f;
        var y = o.Y + 30 * scale;
        foreach (var (color, text) in new[]
                 {
                     (LimbColor, Loc.T("手、脚：拖它整条胳膊 / 腿跟着走（IK）")), (PoleColor, Loc.T("肘、膝：调弯曲的朝向")),
                     (AimColor, Loc.T("骨盆、腰、胸、头：转向 / 平移")), (SideColor, Loc.T("肩、脚尖")),
                     (new Vector4(1f, 0.85f, 0.2f, 1f), Loc.T("黄 / 红：超出常见范围")),
                 })
        {
            dl.AddCircleFilled(new Vector2(x, y + 8 * scale), 6 * scale, U32(color));
            dl.AddText(new Vector2(x + 14 * scale, y), U32(Vector4.One with { W = 0.9f }), text);
            y += 38 * scale;
        }
    }

    private static void DrawDrag(ImDrawListPtr dl, Vector2 o, Vector2 size, float scale)
    {
        var unit = size.Y / 2.9f;
        var center = o + new Vector2(size.X * 0.3f, size.Y * 0.62f);
        var phase = (MathF.Sin(Time * 1.6f) + 1f) / 2f;
        new Figure().Draw(dl, center, unit, scale, 0.25f);
        var figure = new Figure();
        var start = figure.P["hand_r"];
        figure.Reach("r", Vector2.Lerp(start, new Vector2(-0.78f, -1.3f), phase));
        figure.Draw(dl, center, unit, scale, 1f, "hand_r");
        var hand = figure.At("hand_r", center, unit);
        Dashed(dl, center + start * unit, hand, U32(new Vector4(1, 1, 1, 0.5f)), 1.5f * scale);
        Cursor(dl, hand + new Vector2(3, 3) * scale, scale);
        var x = o.X + size.X * 0.55f;
        Label(dl, new Vector2(x, o.Y + 40 * scale), Loc.T("按住直接拖 = 自由模式"), Vector4.One);
        Label(dl, new Vector2(x, o.Y + 72 * scale), Loc.T("手跟着鼠标，胳膊自动弯"), PosegraphTheme.Grey);
        Label(dl, new Vector2(x, o.Y + 104 * scale), Loc.T("Shift：微调　右键：取消"), PosegraphTheme.Grey);
    }

    private static void DrawMoveRotate(ImDrawListPtr dl, Vector2 o, Vector2 size, float scale)
    {
        var half = size.X / 2;
        var phase = (MathF.Sin(Time * 1.4f) + 1f) / 2f;
        // 左：G 移动
        var w = KeyCap(dl, o + new Vector2(14, 14) * scale, "G", scale);
        Label(dl, o + new Vector2(22 * scale + w, 18 * scale), Loc.T("移动"), Vector4.One, false);
        var a = o + new Vector2(half * 0.25f, size.Y * 0.68f);
        var b = a + new Vector2(half * 0.45f, -size.Y * 0.32f) * phase;
        dl.AddCircle(a, 6 * scale, U32(new Vector4(1, 1, 1, 0.6f)), 0, 1.5f * scale);
        Dashed(dl, a, b, U32(new Vector4(1, 1, 1, 0.85f)), 2f * scale);
        dl.AddCircleFilled(b, 7 * scale, U32(LimbColor));
        dl.AddCircle(b, 10.5f * scale, U32(SelectColor), 0, 2.5f * scale);
        var cm = 25f * phase;
        Label(dl, (a + b) / 2 + new Vector2(12, 6) * scale, $"{cm:0.0} cm", Vector4.One);
        var parts = new[] { cm * 0.8f, cm * 0.6f, 0f };
        for (var i = 0; i < 3; i++)
            dl.AddText(o + new Vector2(half * 0.08f + i * 74 * scale, size.Y - 32 * scale), U32(AxisColors[i]), $"{"XYZ"[i]} {parts[i]:+0.0;-0.0}");

        // 右：R 旋转
        Divider(dl, o, size, scale);
        w = KeyCap(dl, o + new Vector2(half + 14 * scale, 14 * scale), "R", scale);
        Label(dl, o + new Vector2(half + 22 * scale + w, 18 * scale), Loc.T("旋转"), Vector4.One, false);
        var c = o + new Vector2(half * 1.5f, size.Y * 0.56f);
        var r = 64 * scale;
        var sweep = (0.2f + 1.1f * phase) * MathF.PI / 2;
        var from = -MathF.PI * 0.85f;
        Sector(dl, c, r, from, sweep, AxisColors[2], scale);
        var bone = c + new Vector2(MathF.Cos(from + sweep), MathF.Sin(from + sweep)) * r * 0.9f;
        dl.AddLine(c, bone, U32(Vector4.One), 3f * scale);
        dl.AddCircleFilled(c, 7 * scale, U32(LimbColor));
        dl.AddCircle(c, 10.5f * scale, U32(SelectColor), 0, 2.5f * scale);
        var mid = from + sweep / 2;
        Label(dl, c + new Vector2(MathF.Cos(mid), MathF.Sin(mid)) * (r + 24 * scale) - new Vector2(20, 8) * scale,
              $"{sweep * 180f / MathF.PI:0.0}°", Vector4.One);
    }

    private static void DrawAxes(ImDrawListPtr dl, Vector2 o, Vector2 size, float scale)
    {
        var half = size.X / 2;
        var red = AxisColors[0];
        // 左：全局 X（实线，水平）
        var w = KeyCap(dl, o + new Vector2(14, 14) * scale, "X", scale);
        Label(dl, o + new Vector2(22 * scale + w, 18 * scale), Loc.T("按一次：全局"), Vector4.One, false);
        var shoulder = o + new Vector2(half * 0.15f, size.Y * 0.42f);
        var elbow = shoulder + new Vector2(half * 0.25f, size.Y * 0.2f);
        var hand = elbow + new Vector2(half * 0.3f, -size.Y * 0.18f);
        dl.PushClipRect(o, o + new Vector2(half, size.Y), true);
        dl.AddLine(new Vector2(o.X, hand.Y), new Vector2(o.X + half, hand.Y), U32(new Vector4(0, 0, 0, 0.7f)), 5f * scale);
        dl.AddLine(new Vector2(o.X, hand.Y), new Vector2(o.X + half, hand.Y), U32(red), 2.5f * scale);
        Arm(dl, shoulder, elbow, hand, scale);
        Triad(dl, hand, [new Vector2(1, 0), new Vector2(0, -1), Vector2.Normalize(new Vector2(-0.6f, 0.5f))], 0, 50 * scale, scale, false);
        Label(dl, new Vector2(o.X + 12 * scale, o.Y + size.Y - 34 * scale), Loc.T("X · 全局（角色的左右）"), red);
        dl.PopClipRect();

        // 右：骨骼自身 X（虚线，沿前臂）
        Divider(dl, o, size, scale);
        var x = o.X + half + 14 * scale;
        x += KeyCap(dl, new Vector2(x, o.Y + 14 * scale), "X", scale) + 4 * scale;
        x += KeyCap(dl, new Vector2(x, o.Y + 14 * scale), "X", scale) + 8 * scale;
        Label(dl, new Vector2(x, o.Y + 18 * scale), Loc.T("再按一次：骨骼自身"), Vector4.One, false);
        var s2 = o + new Vector2(half * 1.15f, size.Y * 0.42f);
        var e2 = s2 + new Vector2(half * 0.25f, size.Y * 0.2f);
        var h2 = e2 + new Vector2(half * 0.3f, -size.Y * 0.18f);
        var along = Vector2.Normalize(h2 - e2);
        dl.PushClipRect(o + new Vector2(half, 0), o + size, true);
        Dashed(dl, h2 - along * size.X, h2 + along * size.X, U32(new Vector4(0, 0, 0, 0.7f)), 5f * scale, 10f, 6f);
        Dashed(dl, h2 - along * size.X, h2 + along * size.X, U32(red), 2.5f * scale, 10f, 6f);
        Arm(dl, s2, e2, h2, scale);
        var up = new Vector2(along.Y, -along.X);
        Triad(dl, h2, [along, up, Vector2.Normalize(-along * 0.4f + new Vector2(-0.3f, 0.6f))], 0, 50 * scale, scale, true);
        Label(dl, new Vector2(o.X + half + 12 * scale, o.Y + size.Y - 34 * scale), Loc.T("X · 骨骼自身（沿着前臂）"), red);
        dl.PopClipRect();
    }

    private static void Arm(ImDrawListPtr dl, Vector2 shoulder, Vector2 elbow, Vector2 hand, float scale)
    {
        dl.AddLine(shoulder, elbow, U32(new Vector4(1, 1, 1, 0.6f)), 2.5f * scale);
        dl.AddLine(elbow, hand, U32(new Vector4(1, 1, 1, 0.6f)), 2.5f * scale);
        dl.AddCircleFilled(shoulder, 5 * scale, U32(SideColor));
        dl.AddCircleFilled(elbow, 5 * scale, U32(PoleColor));
        dl.AddCircleFilled(hand, 6.5f * scale, U32(LimbColor));
        dl.AddCircle(hand, 10 * scale, U32(SelectColor), 0, 2.5f * scale);
    }

    /// <summary>一组轴的三个方向（红 X、绿 Y、蓝 Z），锁住的那根粗、带箭头和「+X」；骨骼自身的轴画虚线。</summary>
    private static void Triad(ImDrawListPtr dl, Vector2 at, Vector2[] dirs, int locked, float length, float scale, bool dashed)
    {
        for (var i = 0; i < 3; i++)
        {
            var color = AxisColors[i] with { W = i == locked ? 1f : 0.6f };
            var to = at + dirs[i] * (i == locked ? length : length * 0.6f);
            if (dashed && i != locked) Dashed(dl, at, to, U32(color), 1.5f * scale, 4f, 3f);
            else Arrow(dl, at, to, U32(color), (i == locked ? 3f : 1.5f) * scale, (i == locked ? 10f : 6f) * scale);
            dl.AddText(to + dirs[i] * 7 * scale - new Vector2(5, 7) * scale, U32(color), (i == locked ? "+" : "") + "XYZ"[i]);
        }
    }

    private static void DrawAngles(ImDrawListPtr dl, Vector2 o, Vector2 size, float scale)
    {
        var phase = (MathF.Sin(Time * 1.2f) + 1f) / 2f;
        // 面板里的 X / Y / Z
        float[] values = [12.0f, -3.5f, 18f + 40f * phase];
        var x = o.X + 24 * scale;
        var y = o.Y + 26 * scale;
        Label(dl, new Vector2(x, y), Loc.T("面板：选中右手"), Vector4.One, false);
        y += 30 * scale;
        dl.AddText(new Vector2(x, y + 5 * scale), U32(Vector4.One), Loc.T("旋转"));
        var fx = x + ImGui.CalcTextSize(Loc.T("旋转")).X + 10 * scale;
        for (var i = 0; i < 3; i++)
        {
            var box = new Vector2(fx + i * 104 * scale, y);
            dl.AddText(box + new Vector2(0, 5 * scale), U32(AxisColors[i]), "XYZ"[i].ToString());
            var f = box + new Vector2(14 * scale, 0);
            dl.AddRectFilled(f, f + new Vector2(80, 26) * scale, U32(new Vector4(1, 1, 1, i == 2 ? 0.24f : 0.12f)), 4 * scale);
            dl.AddText(f + new Vector2(10, 5) * scale, U32(Vector4.One), $"{values[i]:0.0}°");
        }
        Label(dl, new Vector2(x, y + 36 * scale), Loc.T("按住左右拖调整，Ctrl+单击输入"), PosegraphTheme.Grey, false);

        // 旋转中鼠标旁的读数
        var mouse = o + new Vector2(size.X * 0.42f, size.Y * 0.56f);
        Cursor(dl, mouse, scale);
        var at = mouse + new Vector2(20, 20) * scale;
        var line1 = Loc.T("右手 · 旋转 · Z 全局") + $" {40f * phase:0.0}°";
        var width = Math.Max(ImGui.CalcTextSize(line1).X, 260 * scale);
        dl.AddRectFilled(at - new Vector2(6, 4) * scale, at + new Vector2(width + 6 * scale, ImGui.GetTextLineHeight() * 2 + 10 * scale),
                         U32(new Vector4(0, 0, 0, 0.65f)), 4 * scale);
        dl.AddText(at, U32(Vector4.One), line1);
        var y2 = at.Y + ImGui.GetTextLineHeight() + 4 * scale;
        float[] deltas = [0f, 0f, 40f * phase];
        for (var i = 0; i < 3; i++)
            dl.AddText(new Vector2(at.X + i * 86 * scale, y2), U32(AxisColors[i]), $"{"XYZ"[i]} {deltas[i]:+0.0;-0.0}°");
    }

    private static void DrawPins(ImDrawListPtr dl, Vector2 o, Vector2 size, float scale)
    {
        var unit = size.Y / 2.9f;
        var center = o + new Vector2(size.X * 0.25f, size.Y * 0.62f);
        var phase = (MathF.Sin(Time * 1.3f) + 1f) / 2f;
        var figure = new Figure();
        // 蹲一点：上半身往下，钉住的脚不动
        var drop = 0.25f * phase;
        foreach (var k in figure.P.Keys.ToList())
            if (!k.StartsWith("foot") && !k.StartsWith("toe") && !k.StartsWith("knee")) figure.P[k] += new Vector2(0, drop);
        figure.P["knee_r"] += new Vector2(-0.12f * phase, drop * 0.5f);
        figure.P["knee_l"] += new Vector2(0.12f * phase, drop * 0.5f);
        figure.Draw(dl, center, unit, scale);
        foreach (var foot in new[] { "foot_r", "foot_l" })
        {
            var p = figure.At(foot, center, unit);
            var r = 11 * scale;
            dl.AddRect(p - new Vector2(r), p + new Vector2(r), U32(new Vector4(1f, 0.4f, 0.4f, 0.9f)), 0, ImDrawFlags.None, 1.5f * scale);
        }
        var x = o.X + size.X * 0.5f;
        var y = o.Y + 24 * scale;
        foreach (var (key, text) in new[] { ("P", Loc.T("钉住 / 取消钉住")), ("M", Loc.T("镜像到另一侧")), ("Ctrl+Z", Loc.T("撤销")), ("Ctrl+Y", Loc.T("重做")), ("Esc", Loc.T("取消选中")) })
        {
            KeyCap(dl, new Vector2(x, y), key, scale);
            dl.AddText(new Vector2(x + 76 * scale, y + 5 * scale), U32(Vector4.One with { W = 0.9f }), text);
            y += 42 * scale;
        }
    }

    private static void DrawSnap(ImDrawListPtr dl, Vector2 o, Vector2 size, float scale)
    {
        var unit = size.Y / 2.9f;
        var me = o + new Vector2(size.X * 0.2f, size.Y * 0.62f);
        var other = me + new Vector2(1.3f * unit, 0);
        new Figure().Draw(dl, other, unit, scale, 0.35f);
        // 自己的左手（画面右侧，靠近对方）：先大致摆到对方肩膀附近，再贴上去
        var phase = Time * 0.5f % 2f;
        var touchScreen = other + new Vector2(-0.3f, -1.18f) * unit;
        var touch = (touchScreen - me) / unit;
        var rough = touch + new Vector2(-0.1f, 0.12f);
        var figure = new Figure();
        var rest = figure.P["hand_l"];
        var target = phase < 1f ? Vector2.Lerp(rest, rough, Math.Min(1f, phase * 1.6f))
                                : Vector2.Lerp(rough, touch, Math.Min(1f, (phase - 1f) * 3f));
        figure.Reach("l", target, down: true);
        figure.Draw(dl, me, unit, scale, 1f, "hand_l");
        if (phase >= 1.35f)
        {
            dl.AddCircle(touchScreen, 5 * scale, U32(ContactColor), 0, 2f * scale);
            dl.AddLine(touchScreen, figure.At("hand_l", me, unit), U32(ContactColor with { W = 0.5f }), 1f);
        }
        var x = o.X + size.X * 0.56f;
        var y = o.Y + 24 * scale;
        Label(dl, new Vector2(x, y), phase < 1f ? Loc.T("1. 先大致摆过去") : Loc.T("2. 贴到部位 → 点这里"), Vector4.One);
        Label(dl, new Vector2(x, y + 32 * scale), Loc.T("只做小修正，保持你摆的姿势"), PosegraphTheme.Grey, false);
        Label(dl, new Vector2(x, y + 60 * scale), "!  " + Loc.T("实验功能"), PosegraphTheme.Yellow, false);
    }
}
