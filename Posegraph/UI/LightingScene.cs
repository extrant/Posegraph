using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Posegraph.Lighting;

namespace Posegraph.UI;

/// <summary>
/// 灯光窗口里的独立 3D 视图（不在游戏画面里操作）：画角色骨架、GPose 镜头、每盏灯和它的光锥，
/// 拖灯（默认沿以脸为中心的球面走，保持距离）、拖对准点（转灯头）、悬停滚轮改距离；
/// 右上角是光照预览球（从镜头看一颗头大小的球被这套灯照成什么样）和左右脸的光比。
/// 灯的位置照样存成相对脸和镜头的方位 / 仰角 / 距离，跟随镜头、存方案都不受影响。
/// </summary>
public sealed class LightingScene(LightingService service, Configuration config)
{
    private static readonly Vector4 White = new(1, 1, 1, 0.9f);
    private static readonly Vector4 Faint = new(1, 1, 1, 0.25f);
    private static readonly Vector4 BodyColor = new(0.75f, 0.78f, 0.82f, 0.85f);
    private static readonly Vector4 CameraColor = new(0.55f, 0.85f, 1f, 0.9f);
    private static readonly Vector4 Highlight = new(0.3f, 0.95f, 1f, 1f);

    private readonly Scene3D _view = new(0.7f, 0.3f, 3.2f);
    private Vector3? _lastFace;
    private DragTarget _drag;
    private int _dragLight;
    private Vector3 _dragAnchor;    // 自由移动：拖动开始时灯的位置（在过它、垂直视线的平面上移动）

    private enum DragTarget { None, Light, Aim }

    public void LookFrom(Vector3 direction) => _view.LookFrom(direction);

    /// <summary>下一帧把所有灯和镜头框进视图。</summary>
    public void FrameAll() => _frameAll = true;

    private bool _frameAll = true;

    public void Draw(Vector2 size)
    {
        if (service.Subject is not { } subject || service.Lights.Count == 0)
        {
            ImGui.TextDisabled(Loc.T("套用一个方案后，这里显示 3D 布光（读到角色的脸才能摆）"));
            return;
        }
        // 换了人 / 重新读脸后把视图中心放回脸上
        if (_lastFace is not { } last || Vector3.Distance(last, subject.Center) > 0.3f)
        {
            _lastFace = subject.Center;
            _frameAll = true;
        }
        var camera = service.Camera;
        var states = LightRigs.Solve(service.Lights, subject, camera, service.Tweaks, service.Time);
        if (_frameAll)
        {
            // 视图中心放在脸上，距离让最远的灯 / 镜头也在画面里
            _frameAll = false;
            _view.Target = subject.Center;
            var far = states.Select(s => Vector3.Distance(s.Position, subject.Center)).Append(1f)
                            .Concat(config.LightShowCamera && service.CameraPose is { } cp ? [Vector3.Distance(cp.Position, subject.Center)] : []).Max();
            _view.Distance = Math.Clamp(far * 1.15f / MathF.Tan(_view.FovY / 2), 1.5f, 40f);
        }
        _view.Begin("##lightScene", size);
        HandleInput(subject, camera, states);

        var feet = service.Body.Count > 0 ? service.Body.Min(b => MathF.Min(b.A.Y, b.B.Y)) : subject.Face.Y - 1.58f * subject.Scale;
        _view.Grid(new Vector3(subject.Face.X, feet, subject.Face.Z), 3f, 0.5f, new Vector4(1, 1, 1, 0.08f));
        DrawSubject(subject);
        if (subject.DistanceScale > 1)   // 照半身、全身时画出要照的范围
            _view.Circle(subject.Center, _view.Forward, subject.Reach, Faint with { W = 0.18f }, 1f, 48);
        if (config.LightShowCamera && service.CameraPose is { } pose) DrawCamera(pose, subject);
        if (config.LightShowOrbit && service.SelectedLight is { } sel && sel < states.Count) DrawOrbit(subject, states[sel].Position);
        for (var i = 0; i < states.Count; i++) DrawLight(i, states[i], subject);
        if (config.LightShowPreview) DrawPreviewSphere(subject, states);
        _view.End();
        if (_view.Hovered && _drag == DragTarget.None)
            ImGui.SetTooltip(HoverLight(states) is { } h
                ? Loc.F("{0}：拖动移动（按住 Shift 只改距离），滚轮改距离；拖菱形点转灯头", Loc.T(service.Lights[h].Name))
                : Loc.T("左键拖动转视角，右键 / 中键平移，滚轮缩放，双击复位；点灯选中"));
    }

    // ---------------- 交互

    private void HandleInput(Subject subject, Vector3? camera, List<LightState> states)
    {
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();
        if (_view.Activated)
        {
            if (AimHandleHit(states) is { } a)
                (_drag, _dragLight) = (DragTarget.Aim, a);
            else if (HoverLight(states) is { } l)
            {
                (_drag, _dragLight, _dragAnchor) = (DragTarget.Light, l, states[l].Position);
                service.SelectedLight = l;
            }
        }
        if (_drag != DragTarget.None && _view.Active && _dragLight < states.Count)
        {
            _view.Grab = true;
            if (io.MouseDelta != Vector2.Zero)
            {
                if (_drag == DragTarget.Light) DragLight(subject, camera, states[_dragLight], mouse, io);
                else DragAim(subject, states[_dragLight], mouse);
            }
        }
        // 悬停在灯上滚轮：改距离（不缩放视图）
        if (_view.Hovered && io.MouseWheel != 0 && _drag == DragTarget.None && HoverLight(states) is { } w)
        {
            var l = service.Lights[w];
            service.Lights[w] = l with { Distance = Math.Clamp(l.Distance * (1 - io.MouseWheel * 0.06f), 0.2f, 8f) };
            service.Refresh();
            _view.WheelUsed = true;
        }
        if (_view.Deactivated)
        {
            if (!_view.Dragged) service.SelectedLight = HoverLight(states);
            _drag = DragTarget.None;
        }
    }

    private int? HoverLight(List<LightState> states)
    {
        var mouse = ImGui.GetMousePos();
        int? best = null;
        var bestD = 12f;
        for (var i = 0; i < states.Count; i++)
            if (_view.Project(states[i].Position) is { } p && Vector2.Distance(p, mouse) is var d && d < bestD)
                (best, bestD) = (i, d);
        return best;
    }

    /// <summary>选中灯的对准点（菱形）：灯轴上离灯「灯到脸的距离」那一点。只有聚光 / 面光有。</summary>
    private Vector3? AimPoint(int i, LightState s, Subject subject)
        => s.Kind == LightKind.Point ? null : s.Position + Vector3.Transform(Vector3.UnitZ, s.Rotation) * Vector3.Distance(s.Position, subject.Center);

    private int? AimHandleHit(List<LightState> states)
    {
        if (service.SelectedLight is not { } i || i >= states.Count || service.Subject is not { } subject) return null;
        return AimPoint(i, states[i], subject) is { } a && _view.Project(a) is { } p && Vector2.Distance(p, ImGui.GetMousePos()) < 10 ? i : null;
    }

    private void DragLight(Subject subject, Vector3? camera, LightState state, Vector2 mouse, ImGuiIOPtr io)
    {
        var light = service.Lights[_dragLight];
        if (io.KeyShift)
        {
            // 只改距离：上下拖
            service.Lights[_dragLight] = light with { Distance = Math.Clamp(light.Distance * (1 + io.MouseDelta.Y * 0.01f), 0.2f, 8f) };
            service.Refresh();
            return;
        }
        var (origin, dir) = _view.Ray(mouse);
        Vector3 target;
        if (config.LightFreeMove)
        {
            // 在过拖动起点、垂直视线的平面上移动
            var t = Vector3.Dot(_dragAnchor - origin, _view.Forward) / MathF.Max(Vector3.Dot(dir, _view.Forward), 1e-4f);
            target = origin + dir * t;
        }
        else
        {
            // 沿以脸为中心的球面走（距离不变）；视线没碰到球就取视线上离球心最近的点再推到球面
            var radius = Vector3.Distance(state.Position, subject.Center);
            var oc = origin - subject.Center;
            var b = Vector3.Dot(oc, dir);
            var disc = b * b - (oc.LengthSquared() - radius * radius);
            var hit = disc >= 0 ? origin + dir * (-b - MathF.Sqrt(disc)) : origin + dir * -b;
            target = subject.Center + Vector3.Normalize(hit - subject.Center) * radius;
        }
        var (az, el, dist) = LightRigs.Place(target, subject, camera, service.Tweaks);
        if (config.LightSnap)
        {
            az = MathF.Round(az / 15f) * 15f;
            el = MathF.Round(el / 15f) * 15f;
        }
        service.Lights[_dragLight] = light with
        {
            Azimuth = az, Elevation = Math.Clamp(el, -85f, 85f),
            Distance = config.LightFreeMove ? Math.Clamp(dist, 0.2f, 8f) : light.Distance,
        };
        service.Refresh();
    }

    private void DragAim(Subject subject, LightState state, Vector2 mouse)
    {
        // 对准点在过脸、垂直于「灯 → 脸」的平面上移动
        var toFace = subject.Center - state.Position;
        var n = Vector3.Normalize(toFace);
        var (origin, dir) = _view.Ray(mouse);
        var denom = Vector3.Dot(dir, n);
        if (MathF.Abs(denom) < 1e-4f) return;
        var hit = origin + dir * (Vector3.Dot(subject.Center - origin, n) / denom);
        var (yaw, pitch) = LightRigs.AimOf(toFace, hit - state.Position);
        if (config.LightSnap)
        {
            yaw = MathF.Round(yaw / 5f) * 5f;
            pitch = MathF.Round(pitch / 5f) * 5f;
        }
        service.Lights[_dragLight] = service.Lights[_dragLight] with { AimYaw = yaw, AimPitch = pitch };
        service.Refresh();
    }

    // ---------------- 画

    private void DrawSubject(Subject subject)
    {
        foreach (var (a, b) in service.Body) _view.Line(a, b, BodyColor, 2.5f);
        var head = 0.11f * subject.Scale;
        _view.Circle(subject.Face - subject.Forward * 0.04f * subject.Scale, _view.Forward, head, BodyColor, 2f);
        _view.Line(subject.Face, subject.Face + subject.Forward * 0.25f * subject.Scale, new Vector4(1f, 0.8f, 0.3f, 0.9f), 2f);
        _view.Text(subject.Face + subject.Forward * 0.27f * subject.Scale, Loc.T("脸朝向"), new Vector4(1f, 0.8f, 0.3f, 0.8f), new Vector2(4, -6));
    }

    private void DrawCamera(CameraPose pose, Subject subject)
    {
        var right = Vector3.Normalize(Vector3.Cross(pose.Forward, pose.Up));
        var up = Vector3.Cross(right, pose.Forward);
        var depth = 0.35f;
        var h = MathF.Tan(pose.FovY / 2) * depth;
        var w = h * pose.Aspect;
        var c = pose.Position + pose.Forward * depth;
        Vector3[] corners = [c + right * w + up * h, c - right * w + up * h, c - right * w - up * h, c + right * w - up * h];
        for (var k = 0; k < 4; k++)
        {
            _view.Line(pose.Position, corners[k], CameraColor, 1.5f);
            _view.Line(corners[k], corners[(k + 1) % 4], CameraColor, 1.5f);
        }
        _view.Line(c + up * h, c + up * h * 1.4f, CameraColor, 2f);   // 小三角：画面上方
        _view.DashedLine(pose.Position, subject.Center, CameraColor with { W = 0.3f });
        _view.Text(pose.Position, Loc.T("镜头"), CameraColor, new Vector2(6, -16));
    }

    /// <summary>选中灯的轨道：以脸为中心、过这盏灯的水平圈和竖直圈（拖灯时灯沿这个球面走）。</summary>
    private void DrawOrbit(Subject subject, Vector3 light)
    {
        var offset = light - subject.Center;
        var radius = offset.Length();
        var level = subject.Center with { Y = light.Y };
        var ring = MathF.Sqrt(MathF.Max(radius * radius - offset.Y * offset.Y, 0));
        _view.Circle(level, Vector3.UnitY, ring, Faint, 1f, 64);
        var flat = new Vector3(offset.X, 0, offset.Z);
        if (flat.LengthSquared() > 1e-6f)
            _view.Circle(subject.Center, Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitY)), radius, Faint with { W = 0.15f }, 1f, 64);
    }

    private void DrawLight(int i, LightState s, Subject subject)
    {
        var spec = service.Lights[i];
        var lit = service.IsLit(i);
        var selected = service.SelectedLight == i;
        var color = lit ? DisplayColor(s.Color) : new Vector4(0.45f, 0.45f, 0.45f, 0.6f);
        var forward = Vector3.Transform(Vector3.UnitZ, s.Rotation);
        var toFace = Vector3.Distance(s.Position, subject.Center);
        var line = color with { W = selected ? 0.9f : 0.45f };

        if (s.Kind != LightKind.Point && config.LightShowCones)
        {
            var length = MathF.Min(toFace, 3f);
            var half = MathF.Min(s.SpotAngle / 2, 85f) * MathF.PI / 180f;
            var tip = s.Position + forward * length;
            if (s.Kind == LightKind.Spot)
            {
                var r = length * MathF.Tan(half);
                Cone(s.Position, forward, length, r, line, selected ? 1.8f : 1.2f);
                if (config.LightShowSoftEdge && s.Softness > 0)
                {
                    var soft = MathF.Min(s.SpotAngle / 2 + s.Softness, 88f) * MathF.PI / 180f;
                    _view.Circle(tip, forward, length * MathF.Tan(soft), line with { W = line.W * 0.4f }, 1f);
                }
            }
            else
            {
                // 面光：灯面画成一块方板，再画光的去向
                var (u, v) = Scene3D.Basis(forward);
                var e = 0.15f;
                Vector3[] q = [s.Position + (u + v) * e, s.Position + (u - v) * e, s.Position + (-u - v) * e, s.Position + (-u + v) * e];
                for (var k = 0; k < 4; k++) _view.Line(q[k], q[(k + 1) % 4], line, 1.5f);
                _view.DashedLine(s.Position, tip, line);
            }
        }
        else if (s.Kind == LightKind.Point)
            _view.DashedLine(s.Position, subject.Center, line with { W = line.W * 0.6f }, 1f, 8);

        if (_view.Project(s.Position) is { } p)
        {
            var dl = _view.DrawList;
            dl.AddCircleFilled(p, selected ? 8 : 6.5f, ImGui.GetColorU32(color with { W = 1 }));
            dl.AddCircle(p, selected ? 11 : 8, ImGui.GetColorU32(selected ? Highlight : White with { W = 0.5f }), 0, selected ? 2 : 1);
            dl.AddText(p + new Vector2(10, -8), ImGui.GetColorU32(lit ? White : Faint), Loc.T(spec.Name));
        }
        if (selected && AimPoint(i, s, subject) is { } aim && _view.Project(aim) is { } ap)
        {
            var d = 6f;
            var c = ImGui.GetColorU32(Highlight);
            _view.DrawList.AddQuadFilled(ap + new Vector2(0, -d), ap + new Vector2(d, 0), ap + new Vector2(0, d), ap + new Vector2(-d, 0), c);
            _view.DashedLine(s.Position, aim, Highlight with { W = 0.5f }, 1f, 10);
        }
    }

    private void Cone(Vector3 apex, Vector3 dir, float length, float radius, Vector4 color, float thickness)
    {
        var (u, v) = Scene3D.Basis(dir);
        var c = apex + dir * length;
        _view.Circle(c, dir, radius, color, thickness);
        for (var k = 0; k < 4; k++)
        {
            var (s, co) = MathF.SinCos(k * MathF.PI / 2);
            _view.Line(apex, c + (u * co + v * s) * radius, color, thickness * 0.8f);
        }
    }

    /// <summary>界面上显示的灯色：只看色相（亮度另算），太暗的提到能看清。</summary>
    private static Vector4 DisplayColor(Vector3 color)
    {
        var max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        var c = max > 1e-4f ? color / max : Vector3.One;
        return new Vector4(Gamma(c.X), Gamma(c.Y), Gamma(c.Z), 1);
    }

    private static float Gamma(float linear) => MathF.Pow(Math.Clamp(linear, 0, 1), 1 / 2.2f);

    // ---------------- 光照预览球

    /// <summary>
    /// 右上角：从游戏镜头看，一颗头大小、放在脸位置的白球被这套灯照成的样子（漫反射 + 聚光锥 + 距离衰减的近似），
    /// 下面是左右半边脸的光比（亮 : 暗，和档数）。用来看光的角度和主补光关系，不是游戏渲染。
    /// </summary>
    private void DrawPreviewSphere(Subject subject, List<LightState> states)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = 104 * scale;
        var pad = 8 * scale;
        var center = _view.Origin + new Vector2(_view.Size.X - size / 2 - pad, size / 2 + pad);
        var dl = _view.DrawList;
        dl.AddRectFilled(center - new Vector2(size / 2 + 4, size / 2 + 4), center + new Vector2(size / 2 + 4, size / 2 + 22 * scale),
                         ImGui.GetColorU32(new Vector4(0, 0, 0, 0.55f)), 6);
        // 镜头方向：有 GPose 镜头用镜头，否则用视图
        var (fwd, up) = service.CameraPose is { } pose ? (pose.Forward, pose.Up) : (_view.Forward, _view.Up);
        var right = Vector3.Normalize(Vector3.Cross(fwd, up));
        up = Vector3.Cross(right, fwd);
        var r = 0.11f * subject.Scale;
        const int n = 32;
        var cell = size / n;
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var sx = (x + 0.5f) / n * 2 - 1;
                var sy = 1 - (y + 0.5f) / n * 2;
                var d2 = sx * sx + sy * sy;
                if (d2 > 1) continue;
                var normal = right * sx + up * sy - fwd * MathF.Sqrt(1 - d2);
                var c = Shade(subject.Face + normal * r, normal, states);
                var tl = center + new Vector2(x * cell - size / 2, y * cell - size / 2);
                dl.AddRectFilled(tl, tl + new Vector2(cell + 0.6f), ImGui.GetColorU32(new Vector4(Tone(c.X), Tone(c.Y), Tone(c.Z), 1)));
            }
        dl.AddCircle(center, size / 2, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.25f)), 48, 1);
        // 光比：镜头看过去左右两侧脸颊（稍偏前）的亮度
        var left = Luma(Shade(subject.Face + Vector3.Normalize(-right - fwd * 0.6f) * r, Vector3.Normalize(-right - fwd * 0.6f), states));
        var rightL = Luma(Shade(subject.Face + Vector3.Normalize(right - fwd * 0.6f) * r, Vector3.Normalize(right - fwd * 0.6f), states));
        var hi = MathF.Max(left, rightL);
        var lo = MathF.Max(MathF.Min(left, rightL), hi / 64f);
        var text = hi <= 1e-5f ? Loc.T("没有光") : Loc.F("光比 {0:0.0}:1（{1:0.0} 档）", hi / lo, MathF.Log2(hi / lo));
        var ts = ImGui.CalcTextSize(text);
        dl.AddText(center + new Vector2(-ts.X / 2, size / 2 + 3 * scale), ImGui.GetColorU32(White), text);
    }

    /// <summary>球面上一点的受光（线性 RGB，主光正对时约 1）。</summary>
    private Vector3 Shade(Vector3 point, Vector3 normal, List<LightState> states)
    {
        var sum = new Vector3(0.015f);
        for (var i = 0; i < states.Count; i++)
        {
            if (!service.IsLit(i)) continue;
            var s = states[i];
            var spec = service.Lights[i];
            var toLight = s.Position - point;
            var dist = toLight.Length();
            var l = toLight / dist;
            var lambert = Vector3.Dot(normal, l);
            if (lambert <= 0) continue;
            var att = 1f;
            if (s.Kind != LightKind.Point)
            {
                var axis = Vector3.Transform(Vector3.UnitZ, s.Rotation);
                var angle = MathF.Acos(Math.Clamp(Vector3.Dot(-l, axis), -1, 1)) * 180f / MathF.PI;
                var half = s.Kind == LightKind.Spot ? s.SpotAngle / 2 : 75f;
                var soft = MathF.Max(s.Kind == LightKind.Spot ? s.Softness : 15f, 1f);
                att = Math.Clamp((half + soft - angle) / soft, 0, 1);
            }
            // 距离衰减：按方案里这盏灯的距离归一（方案距离处为 1），Falloff 越大衰减越快
            var reference = spec.Distance * service.Subject!.Value.Scale * service.Tweaks.Distance;
            var falloff = MathF.Pow(reference / MathF.Max(dist, 0.05f), 2 * s.Falloff);
            sum += s.Color / LightRigs.BaseBrightness * (lambert * att * falloff);
        }
        return sum;
    }

    private static float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>线性亮度 → 显示值（主光正对约 0.75，超出的截断），再做 gamma。</summary>
    private static float Tone(float v) => MathF.Pow(MathF.Min(v * 0.75f, 1f), 1 / 2.2f);
}
