using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Posegraph.UI;

/// <summary>
/// ImGui 里的简易 3D 视图（透视投影，世界 y 朝上）：左键拖动绕目标转、右键 / 中键拖动平移、滚轮缩放、双击复位。
/// 用法：Begin → 调用方做点选 / 拖动（拖自己的东西时把 Grab 设为 true，视角就不跟着转）→ 画东西 → End。
/// </summary>
public sealed class Scene3D
{
    public float Yaw;
    public float Pitch = 0.25f;
    public float Distance = 3f;
    public Vector3 Target;
    public float FovY = 40f * MathF.PI / 180f;

    /// <summary>这一帧调用方占用了左键拖动（拖灯、拖环），视角不转。</summary>
    public bool Grab;

    /// <summary>这一帧调用方用掉了滚轮（比如悬停在灯上改距离）。</summary>
    public bool WheelUsed;

    private readonly float _defaultYaw;
    private readonly float _defaultPitch;
    private readonly float _defaultDistance;
    private Matrix4x4 _viewProj;

    public Scene3D(float yaw = 0f, float pitch = 0.25f, float distance = 3f)
    {
        (Yaw, Pitch, Distance) = (yaw, pitch, distance);
        (_defaultYaw, _defaultPitch, _defaultDistance) = (yaw, pitch, distance);
    }

    public Vector2 Origin { get; private set; }
    public Vector2 Size { get; private set; }
    public Vector3 Eye { get; private set; }
    public Vector3 Forward { get; private set; }
    public Vector3 Right { get; private set; }
    public Vector3 Up { get; private set; }
    public ImDrawListPtr DrawList { get; private set; }

    /// <summary>这一帧是不是有拖动（转视角、平移或调用方的拖动），松开时据此区分「点击」和「拖动」。</summary>
    public bool Dragged { get; private set; }

    public bool Hovered { get; private set; }
    public bool Active { get; private set; }
    public bool Activated { get; private set; }
    public bool Deactivated { get; private set; }

    /// <summary>默认视角（双击、按钮复位）。target 为视图中心。</summary>
    public void Reset()
    {
        (Yaw, Pitch, Distance) = (_defaultYaw, _defaultPitch, _defaultDistance);
    }

    /// <summary>从某个方向看（世界方向，从目标指向眼睛）。</summary>
    public void LookFrom(Vector3 direction)
    {
        var d = Vector3.Normalize(direction);
        Yaw = MathF.Atan2(d.X, d.Z);
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(d.Y, -1f, 1f)), -1.55f, 1.55f);
    }

    public void Begin(string id, Vector2 size)
    {
        Size = size;
        Origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        Hovered = ImGui.IsItemHovered();
        Active = ImGui.IsItemActive();
        Activated = ImGui.IsItemActivated();
        Deactivated = ImGui.IsItemDeactivated();
        if (Activated) Dragged = false;
        Grab = false;
        WheelUsed = false;
        UpdateMatrices();
        DrawList = ImGui.GetWindowDrawList();
        DrawList.PushClipRect(Origin, Origin + Size, true);
        DrawList.AddRectFilled(Origin, Origin + Size, ImGui.GetColorU32(new Vector4(0.07f, 0.075f, 0.09f, 0.95f)), 4);
    }

    public void End()
    {
        var io = ImGui.GetIO();
        if (Active && !Grab && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3))
        {
            Yaw -= io.MouseDelta.X * 0.01f;
            Pitch = Math.Clamp(Pitch + io.MouseDelta.Y * 0.01f, -1.55f, 1.55f);
            Dragged = true;
        }
        if (Active && (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 3) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 3)))
        {
            var k = 2 * Distance * MathF.Tan(FovY / 2) / Size.Y;
            Target += (-Right * io.MouseDelta.X + Up * io.MouseDelta.Y) * k;
            Dragged = true;
        }
        if (Grab) Dragged = true;
        if (Hovered && !WheelUsed && io.MouseWheel != 0)
            Distance = Math.Clamp(Distance * (1 - io.MouseWheel * 0.1f), 0.3f, 40f);
        if (Hovered && !Grab && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) Reset();
        DrawList.PopClipRect();
    }

    private void UpdateMatrices()
    {
        var (sy, cy) = MathF.SinCos(Yaw);
        var (sp, cp) = MathF.SinCos(Pitch);
        var back = new Vector3(sy * cp, sp, cy * cp);
        Eye = Target + back * Distance;
        Forward = -back;
        Right = Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
        Up = Vector3.Cross(Right, Forward);
        var view = Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(FovY, Math.Max(Size.X, 1) / Math.Max(Size.Y, 1), 0.02f, 500f);
        _viewProj = view * proj;
    }

    /// <summary>世界点 → 屏幕；在眼睛后面（或太近）时为 null。</summary>
    public Vector2? Project(Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), _viewProj);
        if (c.W < 0.02f) return null;
        return Origin + Size / 2 + new Vector2(c.X / c.W, -c.Y / c.W) * Size / 2;
    }

    /// <summary>离眼睛的深度（沿视线，米）。越小越靠前。</summary>
    public float Depth(Vector3 p) => Vector3.Dot(p - Eye, Forward);

    /// <summary>世界里 length 米在 p 处大约占多少像素。</summary>
    public float PixelsPerMeter(Vector3 p)
        => Size.Y / (2 * MathF.Max(Depth(p), 0.02f) * MathF.Tan(FovY / 2));

    /// <summary>屏幕点对应的视线（从眼睛出发，单位方向）。</summary>
    public (Vector3 Origin, Vector3 Dir) Ray(Vector2 screen)
    {
        var ndc = (screen - Origin - Size / 2) / (Size / 2);
        var t = MathF.Tan(FovY / 2);
        var dir = Forward + Right * (ndc.X * t * Size.X / Size.Y) - Up * (ndc.Y * t);
        return (Eye, Vector3.Normalize(dir));
    }

    // ---------------- 画图

    public void Line(Vector3 a, Vector3 b, Vector4 color, float thickness = 1.5f)
    {
        if (Project(a) is { } pa && Project(b) is { } pb)
            DrawList.AddLine(pa, pb, ImGui.GetColorU32(color), thickness);
    }

    public void DashedLine(Vector3 a, Vector3 b, Vector4 color, float thickness = 1.2f, int dashes = 12)
    {
        for (var i = 0; i < dashes; i++)
        {
            var t0 = i / (float)dashes;
            var t1 = (i + 0.5f) / dashes;
            Line(Vector3.Lerp(a, b, t0), Vector3.Lerp(a, b, t1), color, thickness);
        }
    }

    /// <summary>以 c 为圆心、法线 n、半径 r（米）的圆。</summary>
    public void Circle(Vector3 c, Vector3 n, float r, Vector4 color, float thickness = 1.5f, int segments = 40)
    {
        var (u, v) = Basis(n);
        Vector3? prev = null;
        for (var k = 0; k <= segments; k++)
        {
            var (s, co) = MathF.SinCos(k * 2 * MathF.PI / segments);
            var p = c + r * (co * u + s * v);
            if (prev is { } q) Line(q, p, color, thickness);
            prev = p;
        }
    }

    /// <summary>屏幕上的实心点（大小按像素，不随距离变）。</summary>
    public void Dot(Vector3 p, float radius, Vector4 color)
    {
        if (Project(p) is { } s) DrawList.AddCircleFilled(s, radius, ImGui.GetColorU32(color));
    }

    public void Text(Vector3 p, string text, Vector4 color, Vector2 offset = default)
    {
        if (Project(p) is { } s) DrawList.AddText(s + offset, ImGui.GetColorU32(color), text);
    }

    /// <summary>地面网格：以 center 为中心、边长 2×extent、间隔 step 的方格（y = center.Y）。</summary>
    public void Grid(Vector3 center, float extent, float step, Vector4 color)
    {
        var n = (int)MathF.Round(extent / step);
        for (var i = -n; i <= n; i++)
        {
            var t = i * step;
            Line(center + new Vector3(t, 0, -extent), center + new Vector3(t, 0, extent), color, 1);
            Line(center + new Vector3(-extent, 0, t), center + new Vector3(extent, 0, t), color, 1);
        }
    }

    /// <summary>垂直于 n 的两根单位轴。</summary>
    public static (Vector3 U, Vector3 V) Basis(Vector3 n)
    {
        n = Vector3.Normalize(n);
        var u = Vector3.Cross(MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, n);
        u = Vector3.Normalize(u);
        return (u, Vector3.Cross(n, u));
    }

    /// <summary>屏幕点到线段的距离。</summary>
    public static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }
}
