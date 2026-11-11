using System.Globalization;
using Dalamud.Interface;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Posegraph.Editor;

/// <summary>姿势编辑器的叠加层和操作（鼠标、键盘、HUD）以及主窗口里的选项面板。</summary>
public sealed partial class PoseEditor
{
    private enum Mode { Drag, Grab, Rotate }

    /// <summary>一次操作。Draw 改输入（目标、旋转、确认 / 取消），Update 解算；都在 _gate 里。</summary>
    private sealed class Op(Node node, Mode mode, EditPose start, Vector3 startPoint, Vector2 startScreen, Vector2 mouse)
    {
        public Node Node { get; } = node;
        public Mode Mode { get; set; } = mode;
        public EditPose Start { get; } = start;
        public Vector3 StartPoint { get; } = startPoint;       // 节点开始时的位置（模型空间）
        public Vector2 StartScreen { get; } = startScreen;     // 节点开始时在屏幕上的位置
        public Vector2 Cursor { get; set; } = startScreen;     // 虚拟光标：节点跟着它走（按住 Shift 时只走鼠标位移的 1/10）
        public Vector2 LastMouse { get; set; } = mouse;
        public float Angle { get; set; }                       // R：累计转角（弧度）
        public int Axis { get; set; } = -1;                    // 锁定的轴 0/1/2，-1 不锁
        public bool Local { get; set; }                        // 锁的是骨骼自身的轴
        public string Numeric { get; set; } = "";
        public Vector3? Target { get; set; }
        public Quaternion? Rotation { get; set; }
        public bool Dirty { get; set; }
        public bool Confirmed { get; set; }
        public bool Cancelled { get; set; }
        public HashSet<string> Write { get; } = [];
        public Dictionary<string, PoseNodes.Pin>? Pins { get; set; }
        public Surface? Surface { get; set; }                  // 手部吸附用的表面（操作开始时建）
        public bool Alt { get; set; }                          // 按着 Alt：吸附临时反转
        public bool QuickSolved { get; set; }                  // 上一次是按快速版解的（松手时要完整再算）
        public bool ForceSnap { get; set; }                    // 「贴到部位」：不管吸附开关都贴，并且只贴选中的部位
        public Surface? Region { get; set; }                   // 「贴到部位」选中的部位（手掌只往这里贴；防穿模还是看全身）
        public Vector3? Focus { get; set; }                    // 「贴到部位」点到的那一点（手掌以它为中心）
        public int Calibration { get; set; }
        public int CalibrationFrames { get; set; }
        public Vector2 RotateFrom { get; set; }               // R：开始转时鼠标在哪（扇形从这个方向画起）
        public bool FromPanel { get; set; }                   // 面板里拖 / 输入 X Y Z 角度（鼠标、键盘不归它管）
        public Vector3? PanelEuler { get; set; }              // 面板里正在改的 X Y Z（度）
    }

    private const float PickRadius = 16f;     // 像素：鼠标离节点这么近算点中
    private const float DragThreshold = 4f;   // 按下后挪这么多像素才算拖动（否则只是选中）

    private string? _selected;                // 选中的节点 id
    private (string Id, Vector2 Mouse)? _press;   // 在节点上按下了左键、还没拖
    private bool _blocked;                    // 上一帧鼠标在别的窗口上（节点被窗口挡住时不接管鼠标）

    private Node? Selected => _selected is null ? null : _nodes.FirstOrDefault(n => n.Id == _selected);

    /// <summary>每帧（UiBuilder.Draw）：画节点和骨架线，处理鼠标键盘。</summary>
    public void Draw()
    {
        if (_pose is not { } pose || !clientState.IsGPosing || _rig is not { } rig) return;
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        _viewProjection = GposeCamera.ViewProjection();
        Op? op;
        lock (_gate) op = _op;
        var selected = Selected;
        var visible = _nodes.Where(n => Visible(n, selected)).ToList();

        var screen = new Dictionary<string, Vector2>();
        foreach (var n in visible)
            if (ToScreen(Shown(pose, n.Bone, PoseNodes.Point(pose, n, _tips, _gaze)), out var s)) screen[n.Id] = s;

        Node? nearest = null;
        var best = PickRadius;
        foreach (var n in visible)
            if (screen.TryGetValue(n.Id, out var s) && Vector2.Distance(s, mouse) is var d && d < best) (best, nearest) = (d, n);

        // 全屏透明叠加层：在节点上、按着节点、操作中时接管鼠标（游戏不会跟着转镜头），否则点击穿透给游戏。
        // 这里的开关决定的是下一帧 ImGui 把鼠标算给谁；「被别的窗口挡住」用上一帧在窗口里量到的结果（同一帧里量的才准）
        var capture = op is { FromPanel: false } || _press is not null || _pick is not null || (nearest is not null && !_blocked);
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.Pos);
        ImGui.SetNextWindowSize(vp.Size);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoDocking;
        if (!capture) flags |= ImGuiWindowFlags.NoInputs;
        ImGui.Begin("##posegraphPoseEditor", flags);
        // 本帧鼠标是不是在别的窗口上（或者正拖着别的窗口里的滑条）：鼠标落在某个窗口上、但不是这个叠加层
        var otherWindow = ImGui.IsAnyItemActive() || (ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && !ImGui.IsWindowHovered());
        _blocked = op is null && _press is null && otherWindow;
        var hovered = op is null && !otherWindow ? nearest : null;

        // 选中节点或操作中：这几个键归编辑器（Update 里从游戏的按键表清掉，游戏收不到 G / R / Esc……；游戏或插件的输入框开着时不抢）
        var keys = !_textInput && !io.WantTextInput && (selected is not null || op is not null || _pick is not null);
        _keys.Poll();
        // 刚用 Esc 取消选中时键还按着：继续挡到松开，不然游戏会在下一帧收到 Esc 退出 GPose
        _grab = keys ? (op is not null ? 2 : 1)
                : _grab > 0 && EditorKeys.SelectionKeys.Concat(EditorKeys.OperationKeys).Any(_keys.Down) ? _grab : 0;

        if (op is { FromPanel: true }) { }   // 面板里改角度：由 DrawPanel 推进
        else if (op is not null) HandleOp(op, pose, mouse, io, keys);
        else if (_pick is not null) HandlePick(pose, mouse, keys, otherWindow);
        else HandleIdle(pose, hovered, screen, mouse, io, keys, selected);

        DrawPick(ImGui.GetWindowDrawList());
        if (op is null && _pick is null) DrawHeat(ImGui.GetWindowDrawList(), selected);
        DrawSkeleton(ImGui.GetWindowDrawList(), pose, rig, visible, screen, hovered, op);
        if (op is not null) DrawGuides(ImGui.GetWindowDrawList(), op, pose);
        if (op is { FromPanel: false }) DrawHud(op, pose, mouse);
        ImGui.End();
    }

    private bool Visible(Node n, Node? selected) => n.Kind switch
    {
        NodeKind.Finger => ShowAllFingers || (selected is { } s && (s.Id == n.Hand || s.Hand == n.Hand)),
        NodeKind.Gaze => _gaze is not null && selected is { Id: "head" or "gaze" },
        _ => true,
    };

    // ------------------------------------------------------------------ 没在操作时：选中、开始拖、快捷键

    private void HandleIdle(EditPose pose, Node? hovered, Dictionary<string, Vector2> screen, Vector2 mouse, ImGuiIOPtr io, bool keys, Node? selected)
    {
        if (hovered is not null && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _selected = hovered.Id;
            _press = (hovered.Id, mouse);
        }
        if (_press is { } press)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _press = null;
            else if (Vector2.Distance(mouse, press.Mouse) > DragThreshold && _nodes.FirstOrDefault(n => n.Id == press.Id) is { } node
                     && screen.TryGetValue(node.Id, out var s))
            {
                _press = null;
                BeginOp(node, Mode.Drag, pose, s, press.Mouse);
            }
        }
        if (!keys) return;
        if (Ctrl(io) && _keys.Pressed(EditorKeys.Z)) { if (Shift(io)) Redo(); else Undo(); return; }
        if (Ctrl(io) && _keys.Pressed(EditorKeys.Y)) { Redo(); return; }
        if (selected is null) return;
        if (_keys.Pressed(EditorKeys.Escape)) { _selected = null; return; }
        if (_keys.Pressed(EditorKeys.P)) TogglePinAt(selected);
        if (_keys.Pressed(EditorKeys.M)) MirrorSelected(selected);
        if (!screen.TryGetValue(selected.Id, out var at)) return;
        if (_keys.Pressed(EditorKeys.G)) BeginOp(selected, Mode.Grab, pose, at, mouse);
        else if (_keys.Pressed(EditorKeys.R) && selected.Rotate.Length > 0) BeginOp(selected, Mode.Rotate, pose, at, mouse);
    }

    private void BeginOp(Node node, Mode mode, EditPose pose, Vector2 nodeScreen, Vector2 mouse)
    {
        // 拖动 / G：节点跟着鼠标的位移走（按在节点边上也不会跳）；R：鼠标绕节点转多少就转多少
        var op = new Op(node, mode, pose, PoseNodes.Point(pose, node, _tips, _gaze), nodeScreen, mouse);
        if (mode == Mode.Rotate) op.Cursor = op.RotateFrom = mouse;
        _heat = null;
        lock (_gate) _op = op;
    }

    // ------------------------------------------------------------------ 操作中：鼠标、轴、数字、确认 / 取消

    private void HandleOp(Op op, EditPose pose, Vector2 mouse, ImGuiIOPtr io, bool keys)
    {
        lock (_gate)
        {
            if (op.Confirmed || op.Cancelled) return;
            var scale = Shift(io) ? 0.1f : 1f;
            op.Alt = io.KeyAlt || _keys.Down(EditorKeys.Alt);
            var delta = mouse - op.LastMouse;
            op.LastMouse = mouse;
            if (op.Mode == Mode.Rotate)
            {
                var a = op.Cursor - op.StartScreen;
                var b = mouse - op.StartScreen;
                if (a.LengthSquared() > 25f && b.LengthSquared() > 25f)
                    op.Angle += MathF.Atan2(a.X * b.Y - a.Y * b.X, Vector2.Dot(a, b)) * scale;
                op.Cursor = mouse;
            }
            else op.Cursor += delta * scale;

            if (keys)
            {
                // G / R 互换；X / Y / Z 锁轴（同一个键第二次是骨骼自身的轴，第三次取消）；数字直接输入
                if (op.Mode != Mode.Drag && _keys.Pressed(EditorKeys.G) && op.Mode != Mode.Grab) Switch(op, Mode.Grab, mouse);
                if (op.Mode != Mode.Drag && _keys.Pressed(EditorKeys.R) && op.Mode != Mode.Rotate && op.Node.Rotate.Length > 0) Switch(op, Mode.Rotate, mouse);
                for (var axis = 0; axis < 3; axis++)
                    if (_keys.Pressed(axis switch { 0 => EditorKeys.X, 1 => EditorKeys.Y, _ => EditorKeys.Z }))
                    {
                        if (op.Axis != axis) (op.Axis, op.Local) = (axis, false);
                        else if (!op.Local) op.Local = true;
                        else op.Axis = -1;
                    }
                TypeNumber(op);
                if (_keys.Pressed(EditorKeys.Enter)) op.Confirmed = true;
                if (_keys.Pressed(EditorKeys.Escape)) op.Cancelled = true;
            }
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) op.Cancelled = true;
            if (op.Mode == Mode.Drag ? !ImGui.IsMouseDown(ImGuiMouseButton.Left) : ImGui.IsMouseClicked(ImGuiMouseButton.Left)) op.Confirmed = true;
            if (op.Cancelled) return;

            if (op.Mode == Mode.Rotate) op.Rotation = RotationOf(op, pose);
            else op.Target = TargetOf(op);
            op.Dirty = true;
        }
    }

    private static void Switch(Op op, Mode mode, Vector2 mouse)
    {
        op.Mode = mode;
        op.Angle = 0;
        op.Cursor = mode == Mode.Rotate ? mouse : op.StartScreen;
        op.RotateFrom = mouse;
        op.Numeric = "";
        op.Target = null;
        op.Rotation = null;
    }

    private void TypeNumber(Op op)
    {
        if (op.Mode == Mode.Drag) return;
        for (var d = 0; d < 10; d++)
            if (_keys.Pressed('0' + d) || _keys.Pressed(0x60 + d)) op.Numeric += (char)('0' + d);
        if ((_keys.Pressed(EditorKeys.Period) || _keys.Pressed(EditorKeys.NumpadDecimal)) && !op.Numeric.Contains('.')) op.Numeric += ".";
        if (_keys.Pressed(EditorKeys.Minus) || _keys.Pressed(EditorKeys.NumpadMinus))
            op.Numeric = op.Numeric.StartsWith('-') ? op.Numeric[1..] : "-" + op.Numeric;
        if (_keys.Pressed(EditorKeys.Backspace) && op.Numeric.Length > 0) op.Numeric = op.Numeric[..^1];
    }

    private static float? NumericValue(Op op)
        => float.TryParse(op.Numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>锁定的轴（模型空间）：全局是角色自己的左右 / 上下 / 前后，局部是骨骼自身的轴。</summary>
    private static Vector3 AxisOf(Op op, EditPose pose)
    {
        var unit = op.Axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
        if (!op.Local) return unit;
        var bone = op.Mode == Mode.Rotate && op.Node.Rotate.Length > 0 ? op.Node.Rotate[^1] : op.Node.Bone;
        return pose.Has(bone) ? Vector3.Normalize(Vector3.Transform(unit, op.Start.Rotation(bone))) : unit;
    }

    /// <summary>G / 自由拖：节点的新位置（模型空间）。不锁轴时在正对镜头的平面里走，锁轴时沿轴走；输了数字就按厘米沿轴走。</summary>
    private Vector3? TargetOf(Op op)
    {
        var p0 = op.StartPoint;
        if (op.Axis >= 0)
        {
            var axis = AxisOf(op, op.Start);
            if (NumericValue(op) is { } cm) return p0 + axis * (cm / 100f / Scale());
            // 沿轴：找轴上屏幕位置离虚拟光标最近的点（屏幕对轴向位移的变化率现量现算，两步牛顿）
            var t = 0f;
            for (var i = 0; i < 3; i++)
            {
                if (!ToScreen(p0 + axis * t, out var s0) || !ToScreen(p0 + axis * (t + 0.01f), out var s1)) return null;
                var j = (s1 - s0) / 0.01f;
                if (j.LengthSquared() < 1e-4f) return null;
                t += Vector2.Dot(op.Cursor - s0, j) / j.LengthSquared();
            }
            return p0 + axis * t;
        }
        // 正对镜头的平面（镜头的右、上两个方向），两步牛顿
        if (GposeCamera.Pose() is not { } cam) return null;
        var world = World(p0);
        var right = Vector3.Normalize(Vector3.Cross(cam.Forward, cam.Up));
        var up = Vector3.Normalize(cam.Up);
        var step = MathF.Max(0.01f, 0.02f * Vector3.Distance(world, cam.Position));
        for (var iteration = 0; iteration < 3; iteration++)
        {
            if (!Project(world, out var s0) || !Project(world + right * step, out var sr)
                || !Project(world + up * step, out var su)) return null;
            var jr = (sr - s0) / step;
            var ju = (su - s0) / step;
            var det = jr.X * ju.Y - ju.X * jr.Y;
            if (MathF.Abs(det) < 1e-3f) return null;
            var d = op.Cursor - s0;
            world += right * ((d.X * ju.Y - ju.X * d.Y) / det) + up * ((jr.X * d.Y - d.X * jr.Y) / det);
        }
        return ModelOf(world);
    }

    /// <summary>R：模型空间里的转动。不锁轴时绕视线转；鼠标绕节点转多少，骨骼在屏幕上就转多少；输了数字就按度数转。</summary>
    private Quaternion? RotationOf(Op op, EditPose pose)
    {
        Vector3 axis;
        if (op.Axis >= 0) axis = AxisOf(op, op.Start);
        else if (GposeCamera.Pose() is { } cam) axis = Vector3.Normalize(Vector3.Transform(cam.Forward, Quaternion.Conjugate(_modelRot)));
        else return null;
        if (NumericValue(op) is { } deg) return Quaternion.CreateFromAxisAngle(axis, deg * MathF.PI / 180f);
        // 正方向在屏幕上是顺时针还是逆时针：转一个小角度看投影怎么动（不用管坐标系手性）
        var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX)) * 0.1f;
        var c = op.StartPoint;
        if (!ToScreen(c, out var sc) || !ToScreen(c + u, out var s1)
            || !ToScreen(c + Vector3.Transform(u, Quaternion.CreateFromAxisAngle(axis, 0.05f)), out var s2)) return null;
        var a = s1 - sc;
        var b = s2 - sc;
        var sign = a.X * b.Y - a.Y * b.X >= 0 ? 1f : -1f;
        return Quaternion.CreateFromAxisAngle(axis, op.Angle * sign);
    }

    // ------------------------------------------------------------------ 画

    private static readonly (string A, string B)[] Bones =
    [
        ("j_kosi", "j_sebo_b"), ("j_sebo_b", "j_sebo_c"), ("j_sebo_c", "j_kubi"), ("j_kubi", "j_kao"),
        ("j_sebo_c", "j_sako_l"), ("j_sako_l", "j_ude_a_l"), ("j_ude_a_l", "j_ude_b_l"), ("j_ude_b_l", "j_te_l"),
        ("j_sebo_c", "j_sako_r"), ("j_sako_r", "j_ude_a_r"), ("j_ude_a_r", "j_ude_b_r"), ("j_ude_b_r", "j_te_r"),
        ("j_kosi", "j_asi_a_l"), ("j_asi_a_l", "j_asi_c_l"), ("j_asi_c_l", "j_asi_d_l"), ("j_asi_d_l", "j_asi_e_l"),
        ("j_kosi", "j_asi_a_r"), ("j_asi_a_r", "j_asi_c_r"), ("j_asi_c_r", "j_asi_d_r"), ("j_asi_d_r", "j_asi_e_r"),
    ];

    /// <summary>每个节点看哪几项限位（取最严重的上色）。</summary>
    private static readonly Dictionary<string, string[]> NodeLimits = new()
    {
        ["waist"] = ["j_sebo_a"], ["chest"] = ["j_sebo_b", "j_sebo_c"], ["head"] = ["j_kubi", "j_kao"],
        ["shoulder_l"] = ["j_sako_l"], ["shoulder_r"] = ["j_sako_r"], ["elbow_l"] = ["j_ude_b_l", "j_ude_a_l"], ["elbow_r"] = ["j_ude_b_r", "j_ude_a_r"],
        ["hand_l"] = ["j_te_l"], ["hand_r"] = ["j_te_r"], ["knee_l"] = ["j_asi_c_l"], ["knee_r"] = ["j_asi_c_r"],
    };

    private void DrawSkeleton(ImDrawListPtr dl, EditPose pose, PoseRig rig, List<Node> visible, Dictionary<string, Vector2> screen, Node? hovered, Op? op)
    {
        var line = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.4f));
        foreach (var (a, b) in Bones)
            if (pose.Has(a) && pose.Has(b) && ToScreen(Joint(pose, a), out var sa) && ToScreen(Joint(pose, b), out var sb))
                dl.AddLine(sa, sb, line, 1.5f);
        foreach (var n in visible)
        {
            if (!screen.TryGetValue(n.Id, out var tip)) continue;
            if (n.Kind == NodeKind.Finger)
            {
                // 手 → 各节 → 指尖
                var prev = pose.Has($"j_te_{n.Side}") && ToScreen(Joint(pose, $"j_te_{n.Side}"), out var hs) ? hs : (Vector2?)null;
                foreach (var seg in n.Chain)
                    if (ToScreen(Joint(pose, seg), out var ss))
                    {
                        if (prev is { } pv) dl.AddLine(pv, ss, line, 1f);
                        prev = ss;
                    }
                if (prev is { } last) dl.AddLine(last, tip, line, 1f);
            }
            else if (n.TipFrom is not null && ToScreen(Joint(pose, n.Bone), out var bs)) dl.AddLine(bs, tip, line, 1.5f);
            else if (n.Kind == NodeKind.Gaze)
                foreach (var eye in PoseNodes.Eyes)
                    if (ToScreen(Joint(pose, eye), out var es)) dl.AddLine(es, tip, ImGui.GetColorU32(new Vector4(1f, 0.5f, 0.8f, 0.5f)), 1f);
        }

        foreach (var n in visible)
        {
            if (!screen.TryGetValue(n.Id, out var s)) continue;
            var color = n.Kind switch
            {
                NodeKind.Limb => new Vector4(0.25f, 0.8f, 1f, 1f),
                NodeKind.Pole => new Vector4(0.6f, 0.9f, 0.4f, 1f),
                NodeKind.Finger => new Vector4(0.5f, 0.95f, 0.9f, 1f),
                NodeKind.Gaze => new Vector4(1f, 0.5f, 0.8f, 1f),
                _ when n.Side != "" => new Vector4(0.75f, 0.6f, 1f, 1f),
                _ => new Vector4(0.95f, 0.95f, 0.95f, 1f),
            };
            if (NodeLimits.TryGetValue(n.Id, out var bones))
            {
                var level = bones.SelectMany(b => PoseOps.Limits.Where(l => l.Bone == b))
                                 .Select(l => PoseOps.Level(PoseOps.Measure(pose, rig, l), l.Degrees)).DefaultIfEmpty(0).Max();
                if (level == 1) color = new Vector4(1f, 0.85f, 0.2f, 1f);
                else if (level == 2) color = new Vector4(1f, 0.3f, 0.25f, 1f);
            }
            var active = n == hovered || op?.Node == n;
            var radius = (n.Kind == NodeKind.Finger ? 4.5f : 6.5f) + (active ? 2f : 0f);
            dl.AddCircleFilled(s, radius, ImGui.GetColorU32(color with { W = active ? 0.95f : 0.8f }));
            dl.AddCircle(s, radius, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f)), 0, 1.5f);
            if (n.Id == _selected) dl.AddCircle(s, radius + 3.5f, ImGui.GetColorU32(new Vector4(1f, 0.6f, 0.1f, 1f)), 0, 2.5f);
            if (_pinned.Contains(n.Id))
            {
                var r = radius + 6f;
                bool onSurface;
                lock (_gate) onSurface = _surfacePins.ContainsKey(n.Id);
                if (onSurface)
                    // 钉在表面上：菱形
                    dl.AddQuad(s + new Vector2(0, -r), s + new Vector2(r, 0), s + new Vector2(0, r), s + new Vector2(-r, 0),
                               ImGui.GetColorU32(new Vector4(0.3f, 1f, 0.6f, 0.95f)), 2f);
                else
                    dl.AddRect(s - new Vector2(r), s + new Vector2(r), ImGui.GetColorU32(new Vector4(1f, 0.4f, 0.4f, 0.9f)), 0f, ImDrawFlags.None, 1.5f);
            }
            // 手贴着表面：在贴着的地方画个小圈
            bool touching;
            Contact contact;
            lock (_gate) touching = _contacts.TryGetValue(n.Id, out contact);
            if (touching && ToScreen(contact.Point, out var cs))
            {
                dl.AddCircle(cs, 5f, ImGui.GetColorU32(new Vector4(0.3f, 1f, 0.6f, 0.9f)), 0, 2f);
                dl.AddLine(cs, s, ImGui.GetColorU32(new Vector4(0.3f, 1f, 0.6f, 0.5f)), 1f);
            }
            if (active || n.Id == _selected) dl.AddText(s + new Vector2(radius + 5f, -8f), ImGui.GetColorU32(Vector4.One), Loc.T(n.Label));
        }

    }

    private void DrawHud(Op op, EditPose pose, Vector2 mouse)
    {
        var mode = op.Mode switch { Mode.Grab => Loc.T("移动"), Mode.Rotate => Loc.T("旋转"), _ => Loc.T("拖动") };
        var axis = op.Axis < 0 ? (op.Mode == Mode.Rotate ? Loc.T("绕视线") : Loc.T("自由"))
                   : $"{"XYZ"[op.Axis]} {(op.Local ? Loc.T("（骨骼自身）") : Loc.T("（全局）"))}";
        var value = op.Numeric.Length > 0 ? op.Numeric + (op.Mode == Mode.Rotate ? "°" : " cm")
                    : op.Mode == Mode.Rotate && op.Rotation is { } q ? $"{SignedAngle(q, RotationAxis(op)) * 180f / MathF.PI:0.0}°"
                    : Moves(op) ? $"{MovedCm(op, pose).Length():0.0} cm" : "";
        var title = $"{Loc.T(op.Node.Label)} · {mode} · {axis} {value}";
        var hint = op.Mode == Mode.Drag ? Loc.T("松开确定，右键取消；Shift 微调") : Loc.T("左键 / 回车确定，右键 / Esc 取消；X Y Z 锁轴，可直接输入数字；Shift 微调");

        // 第二行：X / Y / Z 各改了多少（移动按厘米，转动按面板里那三个角度）
        string[]? parts = null;
        if (Moves(op))
        {
            var cm = MovedCm(op, pose);
            parts = [$"X {cm.X:+0.0;-0.0;0.0} cm", $"Y {cm.Y:+0.0;-0.0;0.0} cm", $"Z {cm.Z:+0.0;-0.0;0.0} cm"];
        }
        else if (EulerBones(op.Start, op.Node) is { } bones && pose.Has(bones.End))
        {
            var d = Wrap(ToEuler(LocalOf(pose, bones)) - ToEuler(LocalOf(op.Start, bones)));
            parts = [$"X {d.X:+0.0;-0.0;0.0}°", $"Y {d.Y:+0.0;-0.0;0.0}°", $"Z {d.Z:+0.0;-0.0;0.0}°"];
        }
        var dl = ImGui.GetWindowDrawList();
        var at = mouse + new Vector2(18f, 18f);
        var lineH = ImGui.GetTextLineHeightWithSpacing();
        var partW = parts is null ? 0f : parts.Max(x => ImGui.CalcTextSize(x).X) + 14f;
        var width = MathF.Max(MathF.Max(ImGui.CalcTextSize(title).X, ImGui.CalcTextSize(hint).X), partW * 3);
        var rows = parts is null ? 2 : 3;
        dl.AddRectFilled(at - new Vector2(6f, 4f), at + new Vector2(width + 6f, lineH * rows), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.65f)), 4f);
        dl.AddText(at, ImGui.GetColorU32(Vector4.One), title);
        var y = at.Y + lineH;
        if (parts is not null)
        {
            for (var i = 0; i < 3; i++)
                dl.AddText(new Vector2(at.X + partW * i, y), ImGui.GetColorU32(AxisColors[i]), parts[i]);
            y += lineH;
        }
        dl.AddText(new Vector2(at.X, y), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.7f)), hint);
    }

    // ------------------------------------------------------------------ 方向提示、转了多少、移了多少

    private static readonly Vector4[] AxisColors = [new(1f, 0.3f, 0.3f, 1f), new(0.35f, 1f, 0.35f, 1f), new(0.35f, 0.6f, 1f, 1f)];

    private static string[] GlobalAxisNames => [Loc.T("角色的左右"), Loc.T("角色的上下"), Loc.T("角色的前后")];

    /// <summary>这次操作算「移动」：G，或者直接拖手脚 / 骨盆 / 视线 / 手指（其余直接拖是转向，按角度显示）。</summary>
    private static bool Moves(Op op) => !op.FromPanel && op.Mode != Mode.Rotate
                                        && (op.Mode == Mode.Grab || op.Node.Kind is NodeKind.Limb or NodeKind.Pelvis or NodeKind.Gaze or NodeKind.Finger);

    /// <summary>节点从起点挪了多少厘米：锁了骨骼自身的轴时按骨骼的三根轴分，否则按角色的左右 / 上下 / 前后分。</summary>
    private Vector3 MovedCm(Op op, EditPose pose)
    {
        var delta = (PoseNodes.Point(pose, op.Node, _tips, _gaze) - op.StartPoint) * 100f * Scale();
        if (!(op.Axis >= 0 && op.Local)) return delta;
        var frame = Frame(op);
        return new Vector3(Vector3.Dot(delta, frame[0]), Vector3.Dot(delta, frame[1]), Vector3.Dot(delta, frame[2]));
    }

    /// <summary>锁轴用的那一组轴（模型空间）：全局是角色的 X Y Z，局部是骨骼开始时的三根轴（和 AxisOf 一致）。</summary>
    private static Vector3[] Frame(Op op)
    {
        if (!op.Local) return [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        var bone = op.Mode == Mode.Rotate && op.Node.Rotate.Length > 0 ? op.Node.Rotate[^1] : op.Node.Bone;
        var q = op.Start.Has(bone) ? op.Start.Rotation(bone) : Quaternion.Identity;
        return [Vector3.Normalize(Vector3.Transform(Vector3.UnitX, q)), Vector3.Normalize(Vector3.Transform(Vector3.UnitY, q)),
                Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, q))];
    }

    /// <summary>R 绕的轴（模型空间）：锁了轴就是那根轴，否则是视线。</summary>
    private Vector3 RotationAxis(Op op)
    {
        if (op.Axis >= 0) return AxisOf(op, op.Start);
        return GposeCamera.Pose() is { } cam ? Vector3.Normalize(Vector3.Transform(cam.Forward, Quaternion.Conjugate(_modelRot))) : Vector3.UnitZ;
    }

    /// <summary>q 绕 axis 转了多少（弧度，带正负）。</summary>
    private static float SignedAngle(Quaternion q, Vector3 axis)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0) q = Quaternion.Negate(q);
        return 2f * MathF.Atan2(Vector3.Dot(new Vector3(q.X, q.Y, q.Z), axis), q.W);
    }

    private static void Dashed(ImDrawListPtr dl, Vector2 a, Vector2 b, uint color, float thickness, float dash = 10f, float gap = 6f)
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
        if (Vector2.DistanceSquared(from, to) < 1e-4f) return;
        var d = Vector2.Normalize(to - from);
        var n = new Vector2(-d.Y, d.X);
        dl.AddTriangleFilled(to, to - d * head + n * head * 0.5f, to - d * head - n * head * 0.5f, color);
    }

    private static void Tag(ImDrawListPtr dl, Vector2 at, string text, Vector4 color)
    {
        var size = ImGui.CalcTextSize(text);
        dl.AddRectFilled(at - new Vector2(4f, 2f), at + size + new Vector2(4f, 2f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.7f)), 3f);
        dl.AddText(at, ImGui.GetColorU32(color), text);
    }

    /// <summary>
    /// 操作中的辅助图：锁了轴时画方向提示（贯穿画面的轴线：全局实线、骨骼自身虚线；节点旁这组轴的三个方向，锁住的那根带箭头）；
    /// R 时画一圈，扇形填充转过的角度并写上度数；移动时从起点到现在画虚线、写厘米。
    /// </summary>
    private void DrawGuides(ImDrawListPtr dl, Op op, EditPose pose)
    {
        if (op.FromPanel) return;
        var pivotBone = op.Mode == Mode.Rotate && op.Node.Rotate.Length > 0 ? op.Node.Rotate[0] : op.Node.Bone;
        var origin = op.Mode == Mode.Rotate && op.Start.Has(pivotBone) ? Joint(op.Start, pivotBone) : Shown(op.Start, op.Node.Bone, op.StartPoint);
        if (!ToScreen(origin, out var c0)) return;
        var white = new Vector4(1f, 1f, 1f, 0.9f);

        // 锁轴：方向提示
        if (op.Axis >= 0)
        {
            var frame = Frame(op);
            var axis = frame[op.Axis];
            var color = AxisColors[op.Axis];
            if (ToScreen(origin + axis * 0.05f, out var c1) && (c1 - c0).LengthSquared() > 1e-4f)
            {
                var dir = Vector2.Normalize(c1 - c0);
                var far = ImGui.GetMainViewport().Size.Length();
                var shadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.7f));
                if (op.Local)
                {
                    Dashed(dl, c0 - dir * far, c0 + dir * far, shadow, 5f);
                    Dashed(dl, c0 - dir * far, c0 + dir * far, ImGui.GetColorU32(color), 2.5f);
                }
                else
                {
                    dl.AddLine(c0 - dir * far, c0 + dir * far, shadow, 5f);
                    dl.AddLine(c0 - dir * far, c0 + dir * far, ImGui.GetColorU32(color), 2.5f);
                }
            }
            else dl.AddCircle(c0, 14f, ImGui.GetColorU32(color), 0, 3f);   // 轴正对着镜头：画成一个圈

            // 节点旁：这一组轴的三个方向（屏幕上统一长度，看得清朝哪边），锁住的粗、带箭头和「+」；骨骼自身的另两根画虚线
            for (var i = 0; i < 3; i++)
            {
                if (!ToScreen(origin + frame[i] * 0.05f, out var e) || (e - c0).LengthSquared() < 1e-4f) continue;
                var d = Vector2.Normalize(e - c0);
                var locked = i == op.Axis;
                var length = locked ? 70f : 42f;
                var col = ImGui.GetColorU32(AxisColors[i] with { W = locked ? 1f : 0.6f });
                if (op.Local && !locked) Dashed(dl, c0, c0 + d * length, col, 1.5f, 5f, 4f);
                else Arrow(dl, c0, c0 + d * length, col, locked ? 3f : 1.5f, locked ? 11f : 7f);
                Tag(dl, c0 + d * (length + 12f) - new Vector2(6f, 8f), (locked ? "+" : "") + "XYZ"[i], AxisColors[i]);
            }
            var label = op.Local ? Loc.F("{0} · 骨骼自身的轴（再按一次 {0} 取消锁定）", "XYZ"[op.Axis])
                                 : Loc.F("{0} · 全局：{1}（再按一次 {0} 换成骨骼自身的轴）", "XYZ"[op.Axis], GlobalAxisNames[op.Axis]);
            Tag(dl, c0 + new Vector2(18f, 38f), label, color);
        }

        // R：圈 + 扇形填充 + 度数
        if (op.Mode == Mode.Rotate && op.Rotation is { } rotation)
        {
            var axis = RotationAxis(op);
            var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var v = Vector3.Cross(axis, u);
            // 半径：屏幕上约 70 像素
            float Pixels(Vector3 w) => ToScreen(origin + w * 0.05f, out var s) ? Vector2.Distance(s, c0) / 0.05f : 0f;
            var perUnit = MathF.Max(Pixels(u), Pixels(v));
            if (perUnit > 1e-3f)
            {
                var r = Math.Clamp(70f / perUnit, 0.02f, 1f);
                var color = op.Axis >= 0 ? AxisColors[op.Axis] : white;
                const int segments = 64;
                var ring = new Vector2[segments + 1];
                var ok = true;
                for (var k = 0; k <= segments && ok; k++)
                {
                    var (sin, cos) = MathF.SinCos(k * 2f * MathF.PI / segments);
                    ok = ToScreen(origin + (u * cos + v * sin) * r, out ring[k]);
                }
                if (ok)
                {
                    for (var k = 0; k < segments; k++) dl.AddLine(ring[k], ring[k + 1], ImGui.GetColorU32(color with { W = 0.55f }), 2f);
                    // 扇形从开始转时鼠标所在的方向画起
                    var want = op.RotateFrom - c0;
                    var start = 0;
                    if (want.LengthSquared() > 1f)
                    {
                        var best = float.MinValue;
                        var w = Vector2.Normalize(want);
                        for (var k = 0; k < segments; k++)
                        {
                            var rk = ring[k] - c0;
                            if (rk.LengthSquared() < 1e-4f) continue;
                            var dot = Vector2.Dot(Vector2.Normalize(rk), w);
                            if (dot > best) (best, start) = (dot, k);
                        }
                    }
                    var (s0, c) = MathF.SinCos(start * 2f * MathF.PI / segments);
                    var from = u * c + v * s0;
                    var angle = SignedAngle(rotation, axis);
                    var steps = Math.Max(2, (int)(MathF.Abs(angle) / 0.06f));
                    var fill = ImGui.GetColorU32(color with { W = 0.28f });
                    var edge = ImGui.GetColorU32(color);
                    var first = ring[start];
                    var prev = first;
                    for (var k = 1; k <= steps; k++)
                    {
                        if (!ToScreen(origin + Vector3.Transform(from, Quaternion.CreateFromAxisAngle(axis, angle * k / steps)) * r, out var p)) break;
                        dl.AddTriangleFilled(c0, prev, p, fill);
                        dl.AddLine(prev, p, edge, 3f);
                        prev = p;
                    }
                    dl.AddLine(c0, first, ImGui.GetColorU32(color with { W = 0.7f }), 1.5f);
                    dl.AddLine(c0, prev, edge, 2f);
                    var mid = Vector3.Transform(from, Quaternion.CreateFromAxisAngle(axis, angle / 2f));
                    if (ToScreen(origin + mid * r * 1.4f, out var labelAt))
                    {
                        var text = $"{angle * 180f / MathF.PI:0.0}°";
                        Tag(dl, labelAt - ImGui.CalcTextSize(text) / 2f, text, Vector4.One);
                    }
                }
            }
        }

        // 移动：起点 → 现在，虚线 + 厘米
        if (Moves(op) && ToScreen(Shown(pose, op.Node.Bone, PoseNodes.Point(pose, op.Node, _tips, _gaze)), out var now)
            && Vector2.Distance(now, c0) > 3f)
        {
            dl.AddCircle(c0, 6f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.7f)), 0, 1.5f);
            Dashed(dl, c0, now, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f)), 4f);
            Dashed(dl, c0, now, ImGui.GetColorU32(white), 2f);
            Tag(dl, (c0 + now) / 2f + new Vector2(10f, 4f), $"{MovedCm(op, pose).Length():0.0} cm", Vector4.One);
        }
    }

    // ------------------------------------------------------------------ X Y Z 角度（面板）

    /// <summary>面板里显示角度用哪两根骨骼：末节 End 相对第一节的父骨骼 Frame（腰、胸这种分几节转的，看的是几节合起来）。</summary>
    private static (string? Frame, string End)? EulerBones(EditPose pose, Node n)
    {
        if (n.Rotate.Length == 0 || n.Kind == NodeKind.Gaze || !n.Rotate.All(pose.Has)) return null;
        return (pose.Parent(n.Rotate[0]), n.Rotate[^1]);
    }

    private static Quaternion LocalOf(EditPose pose, (string? Frame, string End) b)
        => b.Frame is { } f && pose.Has(f) ? Quaternion.Normalize(Quaternion.Conjugate(pose.Rotation(f)) * pose.Rotation(b.End)) : pose.Rotation(b.End);

    /// <summary>四元数 → X Y Z 角度（度）：先绕 X、再绕 Y、再绕 Z（都是父骨骼的轴）。</summary>
    private static Vector3 ToEuler(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var x = MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
        var y = MathF.Asin(Math.Clamp(2f * (q.W * q.Y - q.Z * q.X), -1f, 1f));
        var z = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));
        return new Vector3(x, y, z) * (180f / MathF.PI);
    }

    private static Quaternion FromEuler(Vector3 degrees)
    {
        var r = degrees * (MathF.PI / 180f);
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, r.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Y)
                                    * Quaternion.CreateFromAxisAngle(Vector3.UnitX, r.X));
    }

    private static float Wrap(float degrees) => degrees - 360f * MathF.Round(degrees / 360f);

    private static Vector3 Wrap(Vector3 d) => new(Wrap(d.X), Wrap(d.Y), Wrap(d.Z));

    /// <summary>选中节点的 X Y Z 角度：显示现在的值；按住左右拖或 Ctrl+单击输入就改（和 R 一样走一次操作，能撤销）。</summary>
    private void DrawRotationFields(EditPose pose, Node selected)
    {
        if (EulerBones(pose, selected) is not { } bones) return;
        Op? op;
        lock (_gate) op = _op;
        var mine = op is { FromPanel: true } && op.Node == selected ? op : null;
        var busy = op is not null && mine is null;
        var values = mine?.PanelEuler ?? ToEuler(LocalOf(pose, bones));
        var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        bool changed = false, active = false;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("旋转"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("这根骨骼现在的角度（度，相对它的上一级骨骼；先绕 X、再绕 Y、再绕 Z）。按住数字左右拖调整，Ctrl+单击直接输入"));
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(busy))
            for (var i = 0; i < 3; i++)
            {
                ImGui.SameLine(0, i == 0 ? 8f * scale : 10f * scale);
                ImGui.TextColored(AxisColors[i], "XYZ"[i].ToString());
                ImGui.SameLine(0, 3f * scale);
                ImGui.SetNextItemWidth(78f * scale);
                var v = i == 0 ? values.X : i == 1 ? values.Y : values.Z;
                if (ImGui.DragFloat($"##rot{i}", ref v, 0.5f, 0f, 0f, "%.1f°"))
                {
                    if (i == 0) values.X = v;
                    else if (i == 1) values.Y = v;
                    else values.Z = v;
                    changed = true;
                }
                active |= ImGui.IsItemActive();
            }
        if (busy) return;
        lock (_gate)
        {
            if (mine is null && (active || changed) && _op is null)
            {
                mine = new Op(selected, Mode.Rotate, pose, PoseNodes.Point(pose, selected, _tips, _gaze), Vector2.Zero, Vector2.Zero)
                       { FromPanel = true, PanelEuler = values };
                _heat = null;
                _op = mine;
            }
            if (mine is null || mine.Confirmed || mine.Cancelled) return;
            if (changed)
            {
                mine.PanelEuler = values;
                var frame = bones.Frame is { } f && mine.Start.Has(f) ? mine.Start.Rotation(f) : Quaternion.Identity;
                mine.Rotation = Quaternion.Normalize(frame * FromEuler(values) * Quaternion.Conjugate(mine.Start.Rotation(bones.End)));
                mine.Dirty = true;
            }
            if (!active) mine.Confirmed = true;
        }
    }

    // ------------------------------------------------------------------ 主窗口里的面板

    /// <summary>3D 页顶部「编辑中」下面的选项和说明（主窗口调用）。窗口窄、英文长的时候控件自动换行，不会被右边裁掉。</summary>
    public void DrawPanel()
    {
        if (_pose is not { } pose) return;
        var selected = Selected;
        UI.UiText.Wrapped(Status ?? (selected is null ? Loc.T("单击角色身上的点选中；直接拖也行") : Loc.F("选中：{0}", Loc.T(selected.Label))),
                          new Vector4(1f, 0.8f, 0.3f, 1f), ImGui.GetContentRegionAvail().X);

        var symmetric = Symmetric;
        if (ImGui.Checkbox(Loc.T("对称编辑"), ref symmetric)) Symmetric = symmetric;
        UI.UiText.SameLineIfFits(UI.UiText.CheckboxWidth(Loc.T("限制在正常范围内")));
        var clamp = ClampLimits;
        if (ImGui.Checkbox(Loc.T("限制在正常范围内"), ref clamp)) ClampLimits = clamp;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("超出游戏动画常见范围的关节点会变黄 / 变红；勾上后直接限制住"));
        UI.UiText.SameLineIfFits(UI.UiText.CheckboxWidth(Loc.T("一直显示手指")));
        var fingers = ShowAllFingers;
        if (ImGui.Checkbox(Loc.T("一直显示手指"), ref fingers)) ShowAllFingers = fingers;

        var snap = Snap;
        if (ImGui.Checkbox(Loc.T("手部吸附"), ref snap)) Snap = snap;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("拖手靠近身体（自己的或 GPose 里别人的）时手掌贴上去、手指顺着表面弯，手和手指不会钻进身体。拖动时按住 Alt 临时反转"));
        var sliderW = 160f * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        UI.UiText.SameLineIfFits(sliderW);
        ImGui.SetNextItemWidth(sliderW);
        var fit = Fit;
        string[] fitNames = [Loc.T("松"), Loc.T("自然"), Loc.T("贴紧"), Loc.T("压紧")];
        if (ImGui.SliderFloat("##fit", ref fit, 0f, 3f, Loc.F("贴合度：{0}", fitNames[(int)MathF.Round(Math.Clamp(fit, 0f, 3f))]))) Fit = fit;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("手贴得多紧：松（留一点缝）→ 自然 → 贴紧 → 压紧（手掌、指肚压进皮肤一点，像按住了肉）。臀、胸、腹、大腿这些软的地方会自动多压一点"));
        if (selected is { Kind: NodeKind.Limb } hand && hand.Bone.StartsWith("j_te", StringComparison.Ordinal))
        {
            var label = _pick is null ? Loc.T("贴到部位…") : Loc.T("取消选部位");
            UI.UiText.SameLineIfFits(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight() + 8f);
            using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(_selfParts is null))
                if (ImGui.Button(label)) TogglePick(hand);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Loc.T("实验功能。点了以后鼠标移到身上（自己的或别人的），会高亮光标下的部位；点一下，这只手就自动贴过去。Esc 或右键取消。\n先把手大致摆到那附近、掌心朝着那里再贴，结果最自然"));
            // 实验功能：黄色感叹号
            ImGui.SameLine(0, 4f);
            using (Dalamud.Interface.Utility.Raii.ImRaii.PushFont(Dalamud.Interface.UiBuilder.IconFont))
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle.ToIconString());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("「贴到部位」是实验功能，还在打磨：够不着或要拧着才够得着时不会动（状态栏说明原因），不满意就 Ctrl+Z 撤销"));
        }
        if (_meshStatus is { } meshStatus)
            UI.UiText.Wrapped(meshStatus, new Vector4(0.6f, 0.62f, 0.65f, 1f), ImGui.GetContentRegionAvail().X);

        bool canUndo, canRedo;
        lock (_gate) (canUndo, canRedo) = (_undo.Count > 0, _redo.Count > 0);
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!canUndo))
            if (ImGui.Button(Loc.T("撤销"))) Undo();
        ImGui.SameLine();
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!canRedo))
            if (ImGui.Button(Loc.T("重做"))) Redo();
        if (selected is { Kind: NodeKind.Limb })
        {
            var pin = _pinned.Contains(selected.Id) ? Loc.T("取消钉住") : Loc.T("钉住");
            UI.UiText.SameLineIfFits(ImGui.CalcTextSize(pin).X + ImGui.GetStyle().FramePadding.X * 2);
            if (ImGui.Button(pin)) TogglePinAt(selected);
        }
        if (selected?.MirrorGroup is not null)
        {
            UI.UiText.SameLineIfFits(ImGui.CalcTextSize(Loc.T("镜像到另一侧")).X + ImGui.GetStyle().FramePadding.X * 2);
            if (ImGui.Button(Loc.T("镜像到另一侧"))) MirrorSelected(selected);
        }
        if (selected is not null) DrawRotationFields(pose, selected);

        if (ImGui.CollapsingHeader(Loc.T("操作说明")))
        {
            ImGui.TextWrapped(Loc.T("单击选中节点；直接拖动是自由模式：手脚用 IK，肘膝调朝向，腰胸头肩转向鼠标，骨盆平移整个人。"));
            ImGui.TextWrapped(Loc.T("选中后按 G 移动、R 旋转，移动鼠标调整；按 X / Y / Z 锁定轴（第一次是角色的左右 / 上下 / 前后，再按一次是骨骼自身的轴，第三次取消），可直接输入数字（厘米 / 度）。左键或回车确定，右键或 Esc 取消，按住 Shift 微调。"));
            ImGui.TextWrapped(Loc.T("锁轴时画方向提示：全局轴是实线、骨骼自身的轴是虚线，箭头指着正方向；旋转时扇形填充转过的角度，移动时虚线连着起点、写着厘米；鼠标旁显示 X / Y / Z 各改了多少。"));
            ImGui.TextWrapped(Loc.T("P 钉住选中的手 / 脚（方框标记，动身体时它不动；双脚默认钉住），M 把选中这一侧镜像到另一侧，Ctrl+Z / Ctrl+Y 撤销 / 重做，Esc 取消选中。"));
            ImGui.TextWrapped(Loc.T("选中手时会显示手指的点：拖指尖往掌心是握拳，往外是伸直。选中头时会显示视线点。"));
            ImGui.TextWrapped(Loc.T("手部吸附：勾上后拖手靠近身体，手掌会贴上去（绿色小圈是贴着的地方），手指顺着表面弯；按住 Alt 临时反转。手贴着表面时按 P 是钉在表面上（菱形标记）：贴的是自己的身体，之后转胸、弯腰手会跟着走。"));
        }
    }

    // ------------------------------------------------------------------ 工具

    private bool Shift(ImGuiIOPtr io) => io.KeyShift || _keys.Down(EditorKeys.Shift);

    private bool Ctrl(ImGuiIOPtr io) => io.KeyCtrl || _keys.Down(EditorKeys.Ctrl);

    private float Scale() => MathF.Max(1e-4f, (MathF.Abs(_modelScale.X) + MathF.Abs(_modelScale.Y) + MathF.Abs(_modelScale.Z)) / 3f);

    private Vector3 World(Vector3 model) => _modelPos + Vector3.Transform(model * _modelScale, _modelRot);

    private Vector3 ModelOf(Vector3 world)
    {
        var s = _modelScale;
        var safe = new Vector3(MathF.Abs(s.X) < 1e-6f ? 1f : s.X, MathF.Abs(s.Y) < 1e-6f ? 1f : s.Y, MathF.Abs(s.Z) < 1e-6f ? 1f : s.Z);
        return Vector3.Transform(world - _modelPos, Quaternion.Conjugate(_modelRot)) / safe;
    }

    private bool ToScreen(Vector3 model, out Vector2 screen) => Project(World(model), out screen);

    /// <summary>
    /// 挂在 bone 上的点在游戏里实际画在哪：有蒙皮修正（Customize+ 改过这根骨骼）时，按「修正 × 姿势」换算，
    /// 不然节点、骨架线会和游戏里看到的关节差几厘米。拖动按鼠标位移算，不受影响。
    /// </summary>
    private Vector3 Shown(EditPose pose, string bone, Vector3 point)
    {
        if (_rig is not { } rig || !pose.Has(bone) || !rig.SkinFixOf(bone, out var fix)) return point;
        var t = pose[bone];
        var m = Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(t.Rotation)) * Matrix4x4.CreateTranslation(t.Translation);
        return Matrix4x4.Invert(m, out var inverse) ? Vector3.Transform(point, inverse * fix * m) : point;
    }

    /// <summary>关节在游戏里实际画在哪（见 Shown）。</summary>
    private Vector3 Joint(EditPose pose, string bone) => Shown(pose, bone, pose.Position(bone));

    private Matrix4x4? _viewProjection;   // 这一帧镜头的视图 × 投影（Draw 开头取一次）

    /// <summary>世界 → 屏幕：用镜头自己的矩阵（不带抗锯齿抖动，点不会晃）；取不到时退回 Dalamud 的。</summary>
    private unsafe bool Project(Vector3 world, out Vector2 screen)
    {
        var device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (_viewProjection is not { } m || device == null) return gameGui.WorldToScreen(world, out screen);
        var p = Vector4.Transform(new Vector4(world, 1f), m);
        if (p.W < 1e-6f)
        {
            screen = default;
            return false;
        }
        var origin = ImGui.GetMainViewport().Pos;
        screen = new Vector2(origin.X + (p.X / p.W + 1f) * device->Width * 0.5f, origin.Y + (1f - p.Y / p.W) * device->Height * 0.5f);
        return true;
    }
}
