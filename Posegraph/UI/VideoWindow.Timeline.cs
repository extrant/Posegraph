using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Posegraph.Backend;
using Posegraph.Video;

namespace Posegraph.UI;

/// <summary>
/// 时间轴修正：在某些时间点改几根骨骼（关键帧，ClipKey），相邻帧按影响范围过渡；后端生成片段时施加（辅助骨照样联动）。
/// 改骨骼用 Brio：暂停后在 Brio 里拖骨骼，这里隔一会读回角色姿势，和播放器写的那一帧比，有改动就提示「收进修正」。
/// 复制粘贴：复制某个关键帧的改动量（跟着原动作走），或某一帧一组骨骼的姿势（原样贴上，比如一整只手的手势），
/// 贴到当前帧或一个区间。改完只重算受影响的那几帧（后端 frames），拼回片段文件。
/// 重算要用角色的参考姿势（骨长）：识别 / 整段重算时记下来（EditContext）；打开的旧片段第一次修正时先整段重算一次。
/// </summary>
public sealed partial class VideoWindow
{
    private const float EditMinDeg = 1f;
    private const double DefaultEase = 0.5;
    private static readonly TimeSpan BrioPollInterval = TimeSpan.FromSeconds(1.5);

    // 「复制骨骼姿势」的骨骼组（后端 edits.GROUPS）
    private static readonly (string Id, string Label)[] PoseGroups =
    [
        ("hand_l", "左手"), ("hand_r", "右手"), ("hands", "双手"), ("arm_l", "左臂"), ("arm_r", "右臂"), ("head", "头颈"), ("body", "全身"),
    ];

    /// <summary>重算这个片段用的请求（种族、参考姿势、参数）和原始数据；RawId 是上传到后端后的编号（后端重启后要重新上传）。</summary>
    private sealed class EditContext(string clipPath, string rawPath, ClipRebuildRequest request)
    {
        public string ClipPath { get; } = clipPath;
        public string RawPath { get; } = rawPath;
        public ClipRebuildRequest Request { get; } = request;
        public string? RawId { get; set; }
    }

    private EditContext? _editContext;
    private ClipKey? _selectedKey;
    private ClipClipboard? _clipboard;
    private double? _rangeIn, _rangeOut;
    private int _editPerson;
    private int _poseGroup = 2;
    private volatile bool _editBusy;
    private readonly Stack<List<ClipKey>> _undo = new();
    private double? _dragTime;              // 正在拖的关键帧的新时间

    // Brio 里的改动：读回的姿势、当时播放器写的那一帧、那一帧的时间
    private string? _brioEdited, _brioShown;
    private double _brioTime;
    private DateTime _brioPolledAt;
    private volatile bool _brioPolling;

    /// <summary>识别 / 整段重算完记下重算参数，之后改时间轴修正只重算受影响的帧。</summary>
    private void RememberEditContext(string clipPath, string? rawPath, ClipRebuildRequest request, string? rawId = null)
    {
        _editContext = rawPath is null ? null : new EditContext(clipPath, rawPath, request) { RawId = rawId };
        _undo.Clear();
        _selectedKey = null;
    }

    private static int FrameAt(Clip clip, double t) => Math.Clamp((int)Math.Round(t * clip.Fps), 0, Math.Max(0, clip.FrameCount - 1));

    private static double Snap(Clip clip, double t) => FrameAt(clip, t) / clip.Fps;

    // ------------------------------------------------------------------ 界面

    private void DrawTimeline(Clip clip)
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (!ImGui.CollapsingHeader(Loc.T("时间轴修正") + (clip.Edits.Count > 0 ? Loc.F("（{0} 个关键帧）", clip.Edits.Count) : "") + "###timeline"))
            return;
        if (clip.RawPath is null)
        {
            ImGui.TextDisabled(Loc.T("这个片段没有识别原始数据（旧版本识别的），不能修正；重新识别一次就可以"));
            return;
        }
        if (_selectedKey is not null && !clip.Edits.Contains(_selectedKey)) _selectedKey = null;
        if (clip.People.Length > 1)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Loc.T("修正的人"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120 * scale);
            _editPerson = Math.Clamp(_editPerson, 0, clip.People.Length - 1);
            if (ImGui.BeginCombo("##editPerson", PersonLabel(_editPerson)))
            {
                for (var p = 0; p < clip.People.Length; p++)
                    if (ImGui.Selectable(PersonLabel(p), p == _editPerson))
                    {
                        _editPerson = p;
                        _brioEdited = null;
                    }
                ImGui.EndCombo();
            }
        }

        using var busy = ImRaii.Disabled(_editBusy || _rebuilding || _recorder.Running);
        DrawTrack(clip);
        DrawBrioBanner(clip);
        DrawEditButtons(clip);
        if (_selectedKey is { } key) DrawKeyDetail(clip, key);
        else if (clip.Edits.Count == 0)
            ImGui.TextDisabled(Loc.T("暂停后在 Brio 里拖骨骼，这里会提示「收进修正」；也可以复制别处的修正粘贴过来"));
    }

    private static string PersonLabel(int p) => p == 0 ? Loc.T("主角") : Loc.F("第 {0} 个人", p + 1);

    /// <summary>关键帧轨道：菱形是关键帧（选中的高亮），色条是影响范围，竖线是播放位置，括号是粘贴区间。
    /// 点空白处跳到那一帧，点菱形选中并跳过去，拖菱形挪时间，右键菱形有菜单。</summary>
    private void DrawTrack(Clip clip)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(50, ImGui.GetContentRegionAvail().X);
        var height = 24 * scale;
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##editTrack", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList();
        var max = min + new Vector2(width, height);
        var duration = Math.Max(clip.Duration, 1e-6);
        float X(double t) => min.X + (float)(Math.Clamp(t / duration, 0, 1) * width);
        double T(float x) => Snap(clip, (x - min.X) / width * duration);
        var mid = (min.Y + max.Y) / 2;

        dl.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.06f)), 3 * scale);
        var keys = clip.Edits.Where(k => k.Person == _editPerson).ToList();
        foreach (var k in keys)
        {
            var (s, e) = k.Span(clip.Edits, clip.Duration);
            var color = k == _selectedKey ? new Vector4(0.45f, 0.75f, 1f, 0.35f) : new Vector4(0.6f, 0.6f, 0.9f, 0.18f);
            dl.AddRectFilled(new Vector2(X(s), mid - 4 * scale), new Vector2(Math.Max(X(e), X(s) + 1), mid + 4 * scale), ImGui.GetColorU32(color));
        }
        if (_rangeIn is { } a) dl.AddLine(new Vector2(X(a), min.Y), new Vector2(X(a), max.Y), ImGui.GetColorU32(Green), 2 * scale);
        if (_rangeOut is { } b) dl.AddLine(new Vector2(X(b), min.Y), new Vector2(X(b), max.Y), ImGui.GetColorU32(Green), 2 * scale);
        var px = X(_player.Time);
        dl.AddLine(new Vector2(px, min.Y), new Vector2(px, max.Y), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.8f)), 1.5f * scale);

        ClipKey? under = null;
        var mouse = ImGui.GetMousePos();
        foreach (var k in keys)
        {
            var x = k == _selectedKey && _dragTime is { } dt ? X(dt) : X(k.Time);
            var d = (k == _selectedKey ? 7 : 5.5f) * scale;
            var c = new Vector2(x, mid);
            var color = k == _selectedKey ? new Vector4(0.45f, 0.75f, 1f, 1f) : k.Kind == "pose" ? Green : new Vector4(0.85f, 0.85f, 1f, 1f);
            dl.AddQuadFilled(c + new Vector2(0, -d), c + new Vector2(d, 0), c + new Vector2(0, d), c + new Vector2(-d, 0), ImGui.GetColorU32(color));
            if (k.Hold) dl.AddLine(c + new Vector2(d, 0), c + new Vector2(d + 6 * scale, 0), ImGui.GetColorU32(color), 2 * scale);
            if (hovered && Math.Abs(mouse.X - x) <= 7 * scale) under = k;
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _selectedKey = under;
            _player.Pause();
            _player.Seek(under?.Time ?? T(mouse.X));
        }
        if (active && _selectedKey is not null && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3 * scale))
            _dragTime = T(mouse.X);
        if (!active && _dragTime is { } moved && _selectedKey is { } dragged)
        {
            _dragTime = null;
            if (Math.Abs(moved - dragged.Time) > 1e-6) EditKey(dragged, k => k.Time = moved, Loc.T("已挪动关键帧"));
        }
        if (under is not null && ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _selectedKey = under;
            ImGui.OpenPopup("##keyMenu");
        }
        if (hovered && under is not null)
            ImGui.SetTooltip(Loc.F("{0:0.00} 秒：{1} 根骨骼（{2}）", under.Time, under.Bones.Count,
                                   under.Kind == "pose" ? Loc.T("贴上的姿势") : Loc.T("改动量")));
        if (ImGui.BeginPopup("##keyMenu"))
        {
            if (_selectedKey is { } k)
            {
                if (ImGui.MenuItem(Loc.T("复制"))) CopyKey(k);
                if (ImGui.MenuItem(Loc.T("删除"))) DeleteKey(k);
            }
            ImGui.EndPopup();
        }
    }

    private void DrawBrioBanner(Clip clip)
    {
        if (_brioEdited is null) return;
        ImGui.TextColored(Yellow, Loc.F("Brio 里改了第 {0:0.00} 秒的姿势", _brioTime));
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("收进修正"))) _ = Task.Run(() => AbsorbBrioAsync());
        Tip(Loc.T("把 Brio 里改的骨骼收成这一帧的关键帧：前后按影响范围过渡，换种族重算、录制时都带着"));
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("还原")))
        {
            _brioEdited = null;
            _player.Seek(_player.Time);
        }
        Tip(Loc.T("不要 Brio 里的改动，重新套上片段这一帧"));
    }

    private void DrawEditButtons(Clip clip)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using (ImRaii.Disabled(_selectedKey is null))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Copy, Loc.T("复制修正")) && _selectedKey is { } k)
                CopyKey(k);
        Tip(Loc.T("复制选中关键帧的改动（相对原动作的改动量）：贴到别处时跟着那里原本的动作走"));
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Clone, Loc.T("复制姿势")))
            _ = Task.Run(() => CopyPoseAsync(clip));
        Tip(Loc.T("复制当前帧这组骨骼的样子（比如一整只手的手势）：贴到别处时原样贴上，不管那里原本是什么动作"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70 * scale);
        if (ImGui.BeginCombo("##poseGroup", Loc.T(PoseGroups[_poseGroup].Label)))
        {
            for (var i = 0; i < PoseGroups.Length; i++)
                if (ImGui.Selectable(Loc.T(PoseGroups[i].Label), i == _poseGroup)) _poseGroup = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_undo.Count == 0))
            if (ImGuiComponents.IconButton("##editUndo", FontAwesomeIcon.Undo)) Undo();
        Tip(Loc.T("撤销上一次修正"));

        using (ImRaii.Disabled(_clipboard is null))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Paste, Loc.T("粘贴到当前帧"))) PasteAt(clip);
            ImGui.SameLine();
            using (ImRaii.Disabled(_rangeIn is null || _rangeOut is null))
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowsAltH, Loc.T("粘贴到区间"))) PasteRange(clip);
            Tip(Loc.T("从入点到出点一直保持复制的修正，出点后按影响范围渐出"));
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("入点"))) _rangeIn = Snap(clip, _player.Time);
        Tip(Loc.T("把当前帧设为粘贴区间的开头（右键清除）"));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _rangeIn = null;
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("出点"))) _rangeOut = Snap(clip, _player.Time);
        Tip(Loc.T("把当前帧设为粘贴区间的结尾（右键清除）"));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _rangeOut = null;
        if (_rangeIn is { } a && _rangeOut is { } b && b < a) (_rangeIn, _rangeOut) = (b, a);
        if (_clipboard is { } c) ImGui.TextDisabled(Loc.F("剪贴板：{0}", c.Label));
    }

    private void DrawKeyDetail(Clip clip, ClipKey key)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.TextUnformatted(Loc.F("关键帧 {0:0.00} 秒（{1}，{2} 根骨骼）", key.Time,
                                    key.Kind == "pose" ? Loc.T("贴上的姿势") : Loc.T("改动量"), key.Bones.Count));
        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##deleteKey", FontAwesomeIcon.Trash)) DeleteKey(key);
        Tip(Loc.T("删除这个关键帧"));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("影响"));
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.T("渐入渐出"), !key.Hold) && key.Hold) EditKey(key, k => k.Mode = "ease", Loc.T("已改影响范围"));
        Tip(Loc.T("前后各「过渡」秒：离关键帧近的一段完全按修正，再慢慢退回原动作"));
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.T("保持到下一个"), key.Hold) && !key.Hold) EditKey(key, k => k.Mode = "hold", Loc.T("已改影响范围"));
        Tip(Loc.T("一直保持到这些骨骼的下一个关键帧（没有就到结尾），之前按「过渡」秒渐入"));
        ImGui.SameLine();
        var ease = (float)key.Ease;
        ImGui.SetNextItemWidth(120 * scale);
        ImGui.SliderFloat(Loc.T("过渡") + "##ease", ref ease, 0f, 3f, Loc.T("%.2f 秒"));
        if (ImGui.IsItemDeactivatedAfterEdit()) EditKey(key, k => k.Ease = ease, Loc.T("已改过渡时间"));
        else if (ImGui.IsItemActive()) key.Ease = ease;   // 拖的时候轨道上的色条跟着变，松手再重算
        if (!ImGui.TreeNode(Loc.T("改了哪些骨骼") + "###keyBones")) return;
        foreach (var bone in key.Bones.Keys.OrderBy(b => b).ToList())
        {
            using var id = ImRaii.PushId(bone);
            if (ImGuiComponents.IconButton("##removeBone", FontAwesomeIcon.Times))
                EditKey(key, k => k.Bones.Remove(bone), Loc.F("已去掉 {0} 的修正", bone));
            ImGui.SameLine();
            ImGui.TextUnformatted(bone);
        }
        ImGui.TreePop();
    }

    // ------------------------------------------------------------------ 操作

    private void CopyKey(ClipKey key)
    {
        _clipboard = new ClipClipboard(key.Kind, key.Bones.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone()),
                                       Loc.F("{0:0.00} 秒的{1}（{2} 根骨骼）", key.Time, key.Kind == "pose" ? Loc.T("姿势") : Loc.T("改动"), key.Bones.Count));
        SetStatus(Loc.T("已复制修正：拖到别的帧点「粘贴到当前帧」，或设好入点出点「粘贴到区间」"), Green);
    }

    private async Task CopyPoseAsync(Clip clip)
    {
        if (_editBusy) return;
        _editBusy = true;
        try
        {
            if (await EnsureEditContextAsync() is not { } ctx || _player.Clip is not { } now) return;
            var frame = FrameAt(now, _player.Time);
            var (id, label) = PoseGroups[_poseGroup];
            var result = await WithRawAsync(ctx, raw => _main.Client.PoseEditAsync(raw, RequestFor(ctx, now.Edits), _editPerson, frame, id));
            if (result.Bones.Count == 0)
            {
                SetStatus(Loc.T("这组骨骼这一帧没有可复制的"), Yellow);
                return;
            }
            _clipboard = new ClipClipboard("pose", result.Bones, Loc.F("{0:0.00} 秒的{1}姿势（{2} 根骨骼）", frame / now.Fps, Loc.T(label), result.Bones.Count));
            SetStatus(Loc.T("已复制姿势：拖到别的帧点「粘贴到当前帧」，或设好入点出点「粘贴到区间」"), Green);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("复制失败：{0}", EditError(ex)), Yellow);
        }
        finally { _editBusy = false; }
    }

    private void PasteAt(Clip clip)
    {
        if (_clipboard is not { } c) return;
        var edits = CloneEdits(clip.Edits);
        var key = Place(edits, new ClipKey { Person = _editPerson, Time = Snap(clip, _player.Time), Kind = c.Kind, Bones = CloneBones(c.Bones) });
        _ = Task.Run(() => CommitAsync(edits, Loc.T("已粘贴修正"), key.Time));
    }

    private void PasteRange(Clip clip)
    {
        if (_clipboard is not { } c || _rangeIn is not { } a || _rangeOut is not { } b) return;
        var edits = CloneEdits(clip.Edits);
        // 区间里有的同骨骼关键帧先去掉，再在入点放「保持」、出点放「渐出」
        foreach (var k in edits.Where(k => k.Person == _editPerson && k.Time > a + 1e-6 && k.Time < b - 1e-6).ToList())
        {
            foreach (var bone in c.Bones.Keys) k.Bones.Remove(bone);
            if (k.Bones.Count == 0) edits.Remove(k);
        }
        Place(edits, new ClipKey { Person = _editPerson, Time = a, Kind = c.Kind, Mode = "hold", Ease = DefaultEase, Bones = CloneBones(c.Bones) });
        if (b > a + 1e-6)
            Place(edits, new ClipKey { Person = _editPerson, Time = b, Kind = c.Kind, Ease = DefaultEase, Bones = CloneBones(c.Bones) });
        _ = Task.Run(() => CommitAsync(edits, Loc.F("已粘贴到 {0:0.00}~{1:0.00} 秒", a, b), a));
    }

    private void DeleteKey(ClipKey key)
    {
        if (_player.Clip is not { } clip) return;
        var edits = CloneEdits(clip.Edits, out var map);
        edits.Remove(map[key]);
        _selectedKey = null;
        _ = Task.Run(() => CommitAsync(edits, Loc.T("已删除关键帧"), null));
    }

    /// <summary>改一个关键帧（挪时间、影响范围、去掉骨骼）：在副本上改，重算后换上；去光了骨骼就删掉。</summary>
    private void EditKey(ClipKey key, Action<ClipKey> change, string done)
    {
        if (_player.Clip is not { } clip) return;
        var edits = CloneEdits(clip.Edits, out var map);
        var copy = map[key];
        change(copy);
        if (copy.Bones.Count == 0) edits.Remove(copy);
        _ = Task.Run(() => CommitAsync(edits, done, copy.Bones.Count > 0 ? copy.Time : null));
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var edits = _undo.Pop();
        _ = Task.Run(() => CommitAsync(edits, Loc.T("已撤销"), null, undoable: false));
    }

    /// <summary>
    /// 放一个关键帧：同一个人同一帧已有同类的关键帧就并进去（新的骨骼覆盖），不同类的去掉重叠的骨骼（一帧一根骨骼只有一个修正）。
    /// 返回最后放进去的关键帧。
    /// </summary>
    private static ClipKey Place(List<ClipKey> edits, ClipKey key)
    {
        foreach (var k in edits.Where(k => k.Person == key.Person && Math.Abs(k.Time - key.Time) < 1e-6).ToList())
        {
            if (k.Kind == key.Kind)
            {
                foreach (var (bone, q) in key.Bones) k.Bones[bone] = q;
                k.Mode = key.Mode;
                k.Ease = key.Ease;
                return k;
            }
            foreach (var bone in key.Bones.Keys) k.Bones.Remove(bone);
            if (k.Bones.Count == 0) edits.Remove(k);
        }
        edits.Add(key);
        return key;
    }

    private static List<ClipKey> CloneEdits(List<ClipKey> edits) => CloneEdits(edits, out _);

    private static List<ClipKey> CloneEdits(List<ClipKey> edits, out Dictionary<ClipKey, ClipKey> map)
    {
        map = edits.ToDictionary(k => k, k => k.Clone());
        var m = map;
        return edits.Select(k => m[k]).ToList();
    }

    private static Dictionary<string, float[]> CloneBones(Dictionary<string, float[]> bones) =>
        bones.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone());

    // ------------------------------------------------------------------ Brio 里的改动

    /// <summary>每帧调用：暂停着、片段能修正时，隔一会读回角色姿势和播放器写的那一帧比，有改动就显示「收进修正」。</summary>
    private void PollBrioEdits()
    {
        if (_player.Clip is not { RawPath: not null } clip || _player.Playing || _player.AppliedTime is not { } time
            || _brioPolling || _editBusy || _rebuilding || _recorder.Running || !_main.InGpose) return;
        if (_brioEdited is not null && Math.Abs(_brioTime - time) > 1e-6) _brioEdited = null;   // 换了帧：之前的改动已被冲掉
        if (DateTime.UtcNow - _brioPolledAt < BrioPollInterval) return;
        if (_editPerson >= clip.People.Length || ActorFor(_editPerson) is not { } actor) return;
        _brioPolledAt = DateTime.UtcNow;
        _brioPolling = true;
        var shown = clip.People[_editPerson].PoseJson(clip, time);
        var bones = clip.People[_editPerson].Bones;
        _ = Task.Run(async () =>
        {
            try
            {
                var now = await _applier.GetPoseAsync(actor);
                if (now is null || _player.Clip != clip || _player.Playing || _player.AppliedTime != time) return;
                if (Differs(shown, now, bones)) (_brioEdited, _brioShown, _brioTime) = (now, shown, time);
                else if (_brioTime == time) _brioEdited = null;
            }
            catch
            {
                // 读不到就下次再试（换角色、退出 GPose 时常见）
            }
            finally { _brioPolling = false; }
        });
    }

    /// <summary>片段的骨骼里有没有朝向差 1° 以上的（脸部 j_f_ 不算）。</summary>
    private static bool Differs(string shown, string now, string[] bones)
    {
        var a = Rotations(shown);
        var b = Rotations(now);
        var cos = MathF.Cos(EditMinDeg * MathF.PI / 360f);
        return bones.Any(n => !n.StartsWith("j_f_") && a.TryGetValue(n, out var p) && b.TryGetValue(n, out var q)
                              && MathF.Abs(Quaternion.Dot(p, q)) < cos);
    }

    private static Dictionary<string, Quaternion> Rotations(string json)
    {
        var map = new Dictionary<string, Quaternion>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Bones", out var bones)) return map;
        foreach (var bone in bones.EnumerateObject())
        {
            if (!bone.Value.TryGetProperty("Rotation", out var r) || r.GetString() is not { } text) continue;
            var v = text.Split(',').Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : float.NaN).ToArray();
            if (v.Length == 4 && !v.Any(float.IsNaN)) map[bone.Name] = Quaternion.Normalize(new Quaternion(v[0], v[1], v[2], v[3]));
        }
        return map;
    }

    /// <summary>把 Brio 里的改动收成那一帧的关键帧（改动量按原动作算，后端 edits/capture）。</summary>
    private async Task AbsorbBrioAsync()
    {
        if (_editBusy || _brioEdited is not { } pose || _brioShown is not { } shown || _player.Clip is not { } clip) return;
        var person = _editPerson;
        var frame = FrameAt(clip, _brioTime);
        _editBusy = true;
        try
        {
            SetStatus(Loc.T("正在收进 Brio 里的改动…"), Yellow);
            if (await EnsureEditContextAsync() is not { } ctx || _player.Clip is not { } now) return;
            var result = await WithRawAsync(ctx, raw => _main.Client.CaptureEditAsync(raw, RequestFor(ctx, now.Edits), person, frame, pose, shown));
            _brioEdited = null;
            if (result.Bones.Count == 0)
            {
                SetStatus(Loc.T("没有能收进的改动（只改了脸部或辅助骨，或者改动不到 1°）"), Yellow);
                _player.Seek(_player.Time);
                return;
            }
            var edits = CloneEdits(now.Edits);
            var key = Place(edits, new ClipKey { Person = person, Time = frame / now.Fps, Kind = "delta", Ease = DefaultEase, Bones = result.Bones });
            _editBusy = false;
            await CommitAsync(edits, Loc.F("已收进 {0} 根骨骼的修正：{1}", result.Bones.Count, string.Join("、", result.Changed ?? [])), key.Time);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("收进修正失败：{0}", EditError(ex)), Yellow);
        }
        finally { _editBusy = false; }
    }

    // ------------------------------------------------------------------ 重算

    /// <summary>后端没有这个接口（404 Not Found）= 还在跑更新前的后端：提示重启，不显示原始报错。</summary>
    private static string EditError(Exception ex) =>
        ex is BackendException { StatusCode: 404 } && ex.Message.EndsWith("Not Found")
            ? Loc.T("后端还是旧版本，没有时间轴修正：重启后端后再试")
            : ex.Message;

    /// <summary>重算请求里换上这份修正（每个人只带自己的关键帧）。</summary>
    private static ClipRebuildRequest RequestFor(EditContext ctx, List<ClipKey> edits) => RequestWithEdits(ctx.Request, edits);

    private static ClipRebuildRequest RequestWithEdits(ClipRebuildRequest request, List<ClipKey> edits) => request with
    {
        People = request.People.Select((p, i) => p with { Edits = edits.Where(k => k.Person == i).ToList() }).ToArray(),
    };

    /// <summary>
    /// 拿到这个片段的重算参数：识别 / 整段重算后记着；打开的片段第一次修正时先整段重算一次（读角色的骨长，几秒到几十秒）。
    /// 原始数据还没传到后端就传上去。拿不到时返回 null（状态栏写了原因）。
    /// </summary>
    private async Task<EditContext?> EnsureEditContextAsync()
    {
        if (_player.Clip is not { } clip) return null;
        if (clip.RawPath is null)
        {
            SetStatus(Loc.T("这个片段没有识别原始数据，不能修正"), Yellow);
            return null;
        }
        if (_editContext is null || _editContext.ClipPath != clip.Path)
        {
            if (_player.Actor is not { } actor)
            {
                SetStatus(Loc.T("先选一个游戏角色"), Yellow);
                return null;
            }
            SetStatus(Loc.T("第一次修正：先按角色现在的骨长整段重算一次…"), Yellow);
            await RebuildAsync(clip, actor, Loc.T("已整段重算"));
            if (_editContext is null || _player.Clip?.Path != _editContext.ClipPath) return null;
        }
        if (_editContext.RawId is null)
            _editContext.RawId = (await _main.Client.UploadRawAsync(await File.ReadAllBytesAsync(_editContext.RawPath))).RawId;
        return _editContext;
    }

    /// <summary>调后端（用原始数据编号）；失败时重新上传原始数据再试一次（后端重启后编号就没了）。</summary>
    private async Task<T> WithRawAsync<T>(EditContext ctx, Func<string, Task<T>> call)
    {
        try
        {
            return await call(ctx.RawId!);
        }
        catch
        {
            ctx.RawId = (await _main.Client.UploadRawAsync(await File.ReadAllBytesAsync(ctx.RawPath))).RawId;
            return await call(ctx.RawId);
        }
    }

    /// <summary>
    /// 换上新的一份修正：找出变了的关键帧（新旧两份里对不上的），按它们的影响范围只重算那几帧，拼回片段文件和播放器里的片段，
    /// 重新套上当前帧。select：重算完选中这个时间的关键帧。骨骼列表变了（比如表情开关改过）时改走整段重算。
    /// </summary>
    private async Task CommitAsync(List<ClipKey> edits, string done, double? select, bool undoable = true)
    {
        _editBusy = true;
        try
        {
            if (await EnsureEditContextAsync() is not { } ctx || _player.Clip is not { Path: { } path } clip) return;
            edits = edits.OrderBy(k => k.Time).ToList();
            var oldJson = clip.Edits.Select(k => k.ToJson().ToJsonString()).ToHashSet();
            var newJson = edits.Select(k => k.ToJson().ToJsonString()).ToHashSet();
            var changed = clip.Edits.Where(k => !newJson.Contains(k.ToJson().ToJsonString())).Select(k => (k, clip.Edits))
                .Concat(edits.Where(k => !oldJson.Contains(k.ToJson().ToJsonString())).Select(k => (k, edits))).ToList();
            if (changed.Count > 0)
            {
                var start = changed.Min(c => c.k.Span(c.Item2, clip.Duration).Start);
                var end = changed.Max(c => c.k.Span(c.Item2, clip.Duration).End);
                var first = Math.Max(0, (int)Math.Floor(start * clip.Fps));
                var last = Math.Min(clip.FrameCount - 1, (int)Math.Ceiling(end * clip.Fps));
                SetStatus(Loc.F("正在重算第 {0}~{1} 帧…", first, last), Yellow);
                var result = await WithRawAsync(ctx, raw => _main.Client.RebuildFramesAsync(raw, RequestFor(ctx, edits), first, last));
                var people = changed.Select(c => c.k.Person).Distinct().Where(p => p < clip.People.Length && p < result.People.Length).ToList();
                foreach (var p in people)
                {
                    var part = result.People[p];
                    if (!part.Bones.SequenceEqual(clip.People[p].Bones))
                        throw new InvalidOperationException(Loc.T("骨骼列表和片段里的不一样，请整段重算（换一次角色或改一下表情权重）"));
                }
                if (undoable) _undo.Push(CloneEdits(clip.Edits));
                foreach (var p in people)
                {
                    var part = result.People[p];
                    Clip.SaveEdits(path, edits, p, result.Range[0], part.Frames, part.Bones);
                    for (var i = 0; i < part.Frames.Length && result.Range[0] + i < clip.People[p].Frames.Length; i++)
                        clip.People[p].Frames[result.Range[0] + i] = part.Frames[i];
                }
                if (people.Count == 0) Clip.SaveEdits(path, edits);
            }
            clip.Edits = edits;
            _selectedKey = select is { } t ? edits.FirstOrDefault(k => k.Person == _editPerson && Math.Abs(k.Time - t) < 1e-6) : null;
            _brioEdited = null;
            _player.Seek(_player.Time);
            SetStatus(done, Green);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("修正失败：{0}", EditError(ex)), Yellow);
        }
        finally { _editBusy = false; }
    }
}
