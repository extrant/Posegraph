using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Posegraph.Editor;

/// <summary>
/// 「贴到部位」和贴合热力图：选中一只手、点「贴到部位…」后，鼠标移到身上（自己的或别人的）会半透明高亮光标下的部位，
/// 点一下这只手就自动贴过去（只往这个部位贴，防穿模还是看全身）。手贴好以后，在选中这只手的时候，
/// 手上的顶点按离身体的远近上色：绿 = 贴着，黄到红 = 压进去，方便看贴得紧不紧。都只在预览时显示。
/// </summary>
public sealed partial class PoseEditor
{
    /// <summary>贴合度：0 松、1 自然、2 贴紧、3 压紧（中间连续）。</summary>
    public float Fit { get; set; } = 1f;

    private sealed class Pick(Node hand, Surface surface)
    {
        public Node Hand { get; } = hand;
        public Surface Surface { get; } = surface;
        public Contact? Hit { get; set; }
        public (int Actor, string Region)? Key { get; set; }
        /// <summary>光标附近要画的三角形（只取朝着镜头的），和离命中点多远（越近越亮）。</summary>
        public List<(Vector3 A, Vector3 B, Vector3 C, float Distance)> Triangles { get; set; } = [];
        public Vector3? DrawnAt { get; set; }
    }

    private const float PreviewRadius = 0.08f;   // 预览只亮光标周围这么大（米）

    private Pick? _pick;
    private (string Hand, List<(Vector3 Point, float Signed)> Points)? _heat;

    /// <summary>面板按钮：开始 / 取消「贴到部位」。</summary>
    private void TogglePick(Node hand)
    {
        if (_pick is not null)
        {
            _pick = null;
            return;
        }
        if (applier.Resolve(_actor) is { } self) RefreshOthers(self);   // 别人挪过 / 改过姿势就重新蒙皮
        if (_pose is not { } pose || BuildSurface(hand, pose) is not { } surface)
        {
            Status = Loc.T("身体网格还没读完，稍等一下再试");
            return;
        }
        _pick = new Pick(hand, surface);
        _heat = null;
    }

    /// <summary>选部位中：射线找光标下的表面，高亮那个部位；左键贴过去，右键 / Esc 取消。</summary>
    private void HandlePick(EditPose pose, Vector2 mouse, bool keys, bool otherWindow)
    {
        if (_pick is not { } pick) return;
        if ((keys && _keys.Pressed(EditorKeys.Escape)) || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _pick = null;
            return;
        }
        if (otherWindow) return;
        var ray = MouseRay(mouse);
        pick.Hit = ray is { } r0 ? pick.Surface.Raycast(r0.Origin, r0.Direction) : null;
        if (pick.Hit is not { } hit || ray is not { } r)
        {
            pick.Key = null;
            pick.Triangles = [];
            pick.DrawnAt = null;
            return;
        }
        // 自己身上躯干正面再按胸线分胸 / 腹；别的角色没有骨架，只按骨骼
        float? Line(int actor) => actor == 0 ? BodyRegions.ChestLine(pose) : _chestLines.TryGetValue(actor, out var other) ? other : null;
        var key = (hit.Actor, BodyRegions.Of(hit.Bone, hit.Point, Line(hit.Actor)));
        // 预览只画光标附近这一块（越近越亮），不画整个部位；背对镜头的三角形不画（不透视到身体另一面）。
        // 光标挪了半厘米以上或换了部位才重新取
        if (pick.Key != key || pick.DrawnAt is not { } at || Vector3.DistanceSquared(at, hit.Point) > 0.005f * 0.005f)
        {
            pick.Key = key;
            pick.DrawnAt = hit.Point;
            pick.Triangles = pick.Surface.Around(hit.Point, PreviewRadius, (a, b, c) => a == key.Actor && BodyRegions.Of(b, c, Line(a)) == key.Item2)
                                 .Where(x => Vector3.Dot(x.Normal, (x.A + x.B + x.C) / 3f - r.Origin) < 0)
                                 .Select(x => (x.A, x.B, x.C, x.Distance)).ToList();
        }
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;

        // 贴过去：手腕目标放在点到的地方外面 3cm（掌心对着那里），选中的部位当贴的地方
        var hand = pick.Hand;
        var palm = HandContact.PalmCenter(pose, hand.Side, _rig) ?? pose.Position(hand.Chain[2]);
        var target = hit.Point + hit.Normal * 0.03f + (pose.Position(hand.Chain[2]) - palm);
        var region = pick.Surface.Subset((a, b, c) => a == key.Actor && BodyRegions.Of(b, c, Line(a)) == key.Item2);
        var op = new Op(hand, Mode.Grab, pose, PoseNodes.Point(pose, hand, _tips, _gaze), mouse, mouse)
        {
            Target = target,
            Surface = pick.Surface,
            Region = region,
            Focus = hit.Point,
            ForceSnap = true,
            Dirty = true,
            Confirmed = true,
        };
        _pick = null;
        lock (_gate) _op = op;
    }

    /// <summary>选部位中：光标附近那一块半透明高亮（中间亮、往外淡），点到的地方画一个亮点。</summary>
    private void DrawPick(ImDrawListPtr dl)
    {
        if (_pick is not { } pick) return;
        foreach (var (a, b, c, distance) in pick.Triangles)
        {
            if (!ToScreen(a, out var sa) || !ToScreen(b, out var sb) || !ToScreen(c, out var sc)) continue;
            var fade = 1f - distance / PreviewRadius;
            dl.AddTriangleFilled(sa, sb, sc, ImGui.GetColorU32(new Vector4(0.3f, 0.85f, 1f, 0.04f + 0.26f * fade * fade)));
        }
        var text = Loc.T("移到身上选部位，左键贴过去，右键 / Esc 取消");
        if (pick.Hit is { } hit && ToScreen(hit.Point, out var hs))
        {
            // 命中点：淡光晕 + 外圈 + 实心亮点
            dl.AddCircleFilled(hs, 14f, ImGui.GetColorU32(new Vector4(0.4f, 1f, 0.7f, 0.18f)));
            dl.AddCircle(hs, 9f, ImGui.GetColorU32(new Vector4(0.4f, 1f, 0.7f, 1f)), 0, 2f);
            dl.AddCircleFilled(hs, 4.5f, ImGui.GetColorU32(new Vector4(0.85f, 1f, 0.9f, 1f)));
            if (pick.Key is { } key) text = Loc.F("贴到：{0}", Loc.T(key.Region)) + "\n" + text;
        }
        var at = ImGui.GetIO().MousePos + new Vector2(18f, 18f);
        var size = ImGui.CalcTextSize(text);
        dl.AddRectFilled(at - new Vector2(6f, 4f), at + size + new Vector2(6f, 4f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.65f)), 4f);
        dl.AddText(at, ImGui.GetColorU32(Vector4.One), text);
    }

    /// <summary>手贴好以后（解算线程里）：手上各顶点离身体多远，选中这只手时画热力图。不是手时 null。</summary>
    private static List<(Vector3 Point, float Signed)>? Heat(Node node, EditPose pose, Surface surface, PoseRig rig, IReadOnlyDictionary<string, HandShape>? hands)
    {
        if (node.Kind != NodeKind.Limb || hands?.GetValueOrDefault(node.Side) is not { } shape) return null;
        var list = new List<(Vector3, float)>();
        foreach (var p in shape.Skin(pose, rig))
            if (surface.Closest(p, 0.012f) is { } c) list.Add((p, c.Signed));
        return list;
    }

    /// <summary>贴合热力图：选中刚贴好的那只手时，手上离身体 1cm 以内的顶点上色（绿 = 贴着，黄到红 = 压进去越深越红）。</summary>
    private void DrawHeat(ImDrawListPtr dl, Node? selected)
    {
        if (_heat is not { } heat || selected?.Id != heat.Hand) return;
        foreach (var (point, signed) in heat.Points)
        {
            if (!ToScreen(point, out var s)) continue;
            var color = signed >= 0
                ? new Vector4(0.3f, 1f, 0.45f, MathF.Max(0.25f, 1f - signed / 0.012f))
                : Vector4.Lerp(new Vector4(1f, 0.9f, 0.2f, 0.95f), new Vector4(1f, 0.2f, 0.15f, 0.95f), Math.Clamp(-signed / 0.008f, 0f, 1f));
            dl.AddCircleFilled(s, 2.5f, ImGui.GetColorU32(color));
        }
    }

    /// <summary>光标处的射线（编辑中角色的模型空间）：从镜头位置出发，穿过光标。</summary>
    private (Vector3 Origin, Vector3 Direction)? MouseRay(Vector2 mouse)
    {
        if (_viewProjection is not { } m || !Matrix4x4.Invert(m, out var inverse) || GposeCamera.Pose() is not { } cam) return null;
        var origin = ImGui.GetMainViewport().Pos;
        var size = ImGui.GetMainViewport().Size;
        var x = (mouse.X - origin.X) / size.X * 2f - 1f;
        var y = 1f - (mouse.Y - origin.Y) / size.Y * 2f;
        Vector3 Unproject(float z)
        {
            var p = Vector4.Transform(new Vector4(x, y, z, 1f), inverse);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }
        var direction = Unproject(0.9f) - Unproject(0.1f);
        if (direction.LengthSquared() < 1e-12f) return null;
        direction = Vector3.Normalize(direction);
        if (Vector3.Dot(direction, Unproject(0.5f) - cam.Position) < 0) direction = -direction;
        var start = ModelOf(cam.Position);
        var dir = ModelOf(cam.Position + direction) - start;
        return dir.LengthSquared() < 1e-12f ? null : (start, Vector3.Normalize(dir));
    }
}
