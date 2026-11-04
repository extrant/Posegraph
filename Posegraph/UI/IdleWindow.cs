using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Posegraph.Idle;

namespace Posegraph.UI;

/// <summary>
/// 图库右键「转换为 idle…」：选类型（站立 / 坐椅子 / 坐地上 / 打盹）和槽位、要生成的种族（默认当前角色的），
/// 生成后存进用户命名的 Penumbra 模组并在自己的合集里启用。
/// 左栏是设置（放不下时滚动），右栏是跟着窗口变大的动作预览（滚轮缩放、拖动平移）和位置 / 旋转。
/// </summary>
public sealed class IdleWindow : Window
{
    private static readonly Vector4 Green = PosegraphTheme.Green;
    private static readonly Vector4 Yellow = PosegraphTheme.Yellow;
    private static readonly Vector4 Red = PosegraphTheme.Red;
    private static readonly Vector4 Grey = PosegraphTheme.Grey;

    private enum View { Both, Side, Front }

    private readonly IdleService _idle;
    private readonly Func<string?> _currentRace;
    private readonly Func<string, IReadOnlyList<string>, Task<Dictionary<string, string>>> _poses;
    private readonly Func<bool> _backendOnline;
    private string? _image;
    private IdleFamily _family = IdleFamily.Stand;
    private int _slot;
    private readonly HashSet<string> _races = [];
    private string _status = "";
    private Vector4 _statusColor = Grey;
    private bool _busy;
    private (IdleFamily, string)? _slotsKey;
    private List<int> _slots = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _poseCache = new();   // 种族 → 这张图的姿势 JSON
    private IdlePreview? _preview;
    private (IdleFamily, int, string)? _previewKey;
    private bool _previewing;
    private (IdleFamily, int, string)? _failedKey;
    private Vector3 _offsetCm;   // 身体偏移（厘米，模型空间：x 左右、y 上下、z 前后）
    private Vector3 _rotationDeg;   // 整体旋转（度：x 前后倾、y 转身、z 侧倾）
    private readonly IdleBreath _breath = new();   // 各部位跟着原版 idle 动多少（这次打开游戏期间记住）
    private string _modName = PenumbraIdleMod.DefaultName;
    private bool _merge;                                   // 同名时加进已有的模组（默认新建带编号的）
    private (string Image, string Name, string Folder)? _saved;   // 这个窗口刚存过的：同一张图、同一个名字再存就覆盖它
    private bool _playing = true;
    private int _frame;
    private float _amplify = 1f;
    private View _view = View.Both;
    private float _zoom = 1f;      // 预览缩放（滚轮）
    private Vector2 _pan;          // 预览平移（拖动，屏幕像素）

    public IdleWindow(IdleService idle, Func<string?> currentRace, Func<string, IReadOnlyList<string>, Task<Dictionary<string, string>>> poses,
                      Func<bool> backendOnline)
        : base(Loc.T("转换为 idle") + "###PosegraphIdle")
    {
        _idle = idle;
        _currentRace = currentRace;
        _poses = poses;
        _backendOnline = backendOnline;
        Size = new Vector2(900, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 480), MaximumSize = new Vector2(4000, 4000) };
    }

    public void Open(string image)
    {
        // 每次打开都重新算姿势（修正可能改过）；同一张图保留上次的偏移和旋转
        if (_image != image) _offsetCm = _rotationDeg = Vector3.Zero;
        _poseCache.Clear();
        _preview = null;
        _previewKey = _failedKey = null;
        _image = image;
        _races.Clear();
        if (_currentRace() is { } race) _races.Add(race);
        _status = "";
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw() => PosegraphTheme.Push();

    public override void PostDraw() => PosegraphTheme.Pop();

    public override void Draw()
    {
        if (_image is null) return;
        var s = ImGuiHelpers.GlobalScale;
        ImGui.TextUnformatted(Path.GetFileName(_image));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220 * s);
        ImGui.InputText(Loc.T("模组名"), ref _modName, 64);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("名字已经有了就新建「名字 2」「名字 3」…；这张图调完再存会覆盖刚才那个"));
        ImGui.SameLine();
        ImGui.Checkbox(Loc.T("同名时加进已有的模组"), ref _merge);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("勾上后存进同名的 Posegraph 模组里（每个槽位一个选项），不新建"));
        if (!_idle.PenumbraReady) UiText.Fit(Loc.T("没有检测到 Penumbra：idle 要存成 Penumbra 模组，请先安装并启用 Penumbra"), Red);

        var primary = _currentRace() is { } cur && _races.Contains(cur) ? cur : _races.Order().FirstOrDefault() ?? _currentRace() ?? "c0101";
        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;   // 底下一行：生成按钮和状态
        var height = MathF.Max(200 * s, ImGui.GetContentRegionAvail().Y - footer);
        using (var left = ImRaii.Child("##settings", new Vector2(330 * s, height), true))
            if (left) DrawSettings(primary);
        ImGui.SameLine();
        using (var right = ImRaii.Child("##preview", new Vector2(0, height), true))
            if (right) DrawPlacement(primary);

        var ready = !_busy && _races.Count > 0 && _backendOnline() && _idle.PenumbraReady;
        using (ImRaii.Disabled(!ready))
            if (ImGui.Button(Loc.T("生成并启用"))) Save();
        ImGui.SameLine();
        if (!_backendOnline()) UiText.Fit(Loc.T("先启动后端"), Yellow);
        else if (_status.Length > 0) UiText.Fit(_status, _statusColor);
        else UiText.Fit(Loc.T("可以在 Penumbra 里开关、删除；保留原版的呼吸起伏。别人要用 Mare 等同步插件才看得到"), Grey);
    }

    private void DrawSettings(string primary)
    {
        var s = ImGuiHelpers.GlobalScale;
        var wrap = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(160 * s);
        using (var combo = ImRaii.Combo(Loc.T("类型"), IdleSlots.Label(_family)))
            if (combo)
                foreach (var f in IdleSlots.Families)
                    if (ImGui.Selectable(IdleSlots.Label(f), f == _family))
                    {
                        _family = f;
                        _slot = 0;
                    }

        if (_slotsKey != (_family, primary))
        {
            _slotsKey = (_family, primary);
            _slots = _idle.Slots(_family, primary);
            if (!_slots.Contains(_slot)) _slot = _slots.FirstOrDefault();
        }
        ImGui.SetNextItemWidth(160 * s);
        using (var combo = ImRaii.Combo(Loc.T("槽位"), IdleSlots.SlotLabel(_family, _slot)))
            if (combo)
                foreach (var slot in _slots)
                    if (ImGui.Selectable(IdleSlots.SlotLabel(_family, slot), slot == _slot)) _slot = slot;
        UiText.Wrapped(_slot == 0 ? Loc.T("默认：不切 /cpose 时用的那一个") : Loc.F("游戏里用 /cpose 切到第 {0} 个就是它", _slot), Grey, wrap);

        ImGui.Separator();
        ImGui.TextUnformatted(Loc.T("给哪些种族生成"));
        // 一行一个种族，男、女各一列（骨架编号男 cXX01、女 cXX01 + 100）
        if (ImGui.BeginTable("##races", 3, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("##race", ImGuiTableColumnFlags.WidthFixed, 130 * s);
            ImGui.TableSetupColumn(Loc.T("男"), ImGuiTableColumnFlags.WidthFixed, 60 * s);
            ImGui.TableSetupColumn(Loc.T("女"), ImGuiTableColumnFlags.WidthFixed, 60 * s);
            ImGui.TableHeadersRow();
            for (var id = 101; id <= 1701; id += 200)
            {
                var male = $"c{id:D4}";
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(BaseName(male));
                foreach (var code in new[] { male, $"c{id + 100:D4}" })
                {
                    ImGui.TableNextColumn();
                    var on = _races.Contains(code);
                    if (ImGui.Checkbox($"{(code == _currentRace() ? "*" : "")}##{code}", ref on))
                    {
                        if (on) _races.Add(code);
                        else _races.Remove(code);
                    }
                }
            }
            ImGui.EndTable();
        }
        if (_currentRace() is not null) UiText.Wrapped(Loc.T("* 当前角色"), Grey, wrap);
        foreach (var race in _races.Order())
        {
            if (_idle.InheritedFrom(_family, _slot, race) is { } from)
                UiText.Wrapped(Loc.F("{0} 这个槽位没有自己的动画，用的是 {1} 的：会一起改到 {1}", Races.Label(race), Races.Label(from)), Yellow, wrap);
            else if (!_idle.Slots(_family, race).Contains(_slot))
                UiText.Wrapped(Loc.F("{0} 没有这个槽位", Races.Label(race)), Yellow, wrap);
        }

        ImGui.Separator();
        ImGui.TextUnformatted(Loc.T("各部位跟着原版动多少"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("原版 idle 里除了呼吸还有摆手、换重心等小动作。0% 保持姿势不动，100% 跟原版一样。\n四肢、骨盆一动脚容易滑，默认不动"));
        foreach (var region in IdleBreath.Regions)
        {
            var percent = _breath[region] * 100f;
            ImGui.SetNextItemWidth(150 * s);
            if (ImGui.SliderFloat($"{IdleBreath.Label(region)}##{region}", ref percent, 0f, 100f, "%.0f%%")) _breath[region] = percent / 100f;
        }
        if (ImGui.Button(Loc.T("恢复默认") + "##breath")) _breath.Reset();
    }

    /// <summary>
    /// 右栏：动作预览（灰色原版、青色生成后的，按当前强度和位置实时播放），下面是位置偏移和整体旋转。
    /// 预览跟着窗口变大，滚轮缩放、拖动平移、双击复位；可以只看侧视或正视。
    /// </summary>
    private void DrawPlacement(string primary)
    {
        var s = ImGuiHelpers.GlobalScale;
        var key = (_family, _slot, primary);
        var stale = _preview is null || _previewKey != key;
        // 姿势已经算过时，换类型 / 槽位 / 主种族自动刷新预览
        if (stale && !_busy && !_previewing && _failedKey != key && _poseCache.ContainsKey(primary)) StartPreview(primary);
        if (_preview is null || stale)
        {
            using (ImRaii.Disabled(_busy || _previewing || !_backendOnline()))
                if (ImGui.Button(_previewing ? Loc.T("正在计算姿势…") : Loc.T("预览位置"))) StartPreview(primary);
            UiText.Wrapped(Loc.T("先预览，看生成后的身体和原版动作的位置对不对得上"), Grey, ImGui.GetContentRegionAvail().X);
            return;
        }

        // 顶上一行：看哪个视图、播放、放大
        foreach (var (view, label) in new[] { (View.Both, Loc.T("并排")), (View.Side, Loc.T("侧视")), (View.Front, Loc.T("正视")) })
        {
            using (ImRaii.PushColor(ImGuiCol.Button, PosegraphTheme.Accent with { W = 0.55f }, _view == view))
                if (ImGui.Button(label)) _view = view;
            ImGui.SameLine();
        }
        ImGui.Checkbox(Loc.T("播放"), ref _playing);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * s);
        ImGui.SliderFloat(Loc.T("预览放大"), ref _amplify, 1f, 10f, "×%.0f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("只放大预览里的动作幅度，看清小小的呼吸起伏；不影响生成"));

        var preview = _preview;
        var offset = _offsetCm / 100f;
        if (_playing) _frame = (int)(ImGui.GetTime() * preview.Fps) % preview.Frames.Length;
        var vanilla = preview.Vanilla(_frame, _amplify);
        var ours = preview.Ours(_frame, _breath, _amplify, _rotationDeg);
        for (var i = 0; i < ours.Length; i++) ours[i] += offset;
        // 比例：包住原版和生成后第 0 帧的全部骨骼（播放时比例不跳）
        float minY = 0, maxY = 0, half = 0.3f;
        foreach (var p in preview.Vanilla(0).Concat(preview.Ours(0, _breath, 1f, _rotationDeg).Select(v => v + offset)))
        {
            minY = MathF.Min(minY, p.Y);
            maxY = MathF.Max(maxY, p.Y);
            half = MathF.Max(half, MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Z)));
        }

        var controls = ImGui.GetFrameHeightWithSpacing() * 4 + ImGui.GetTextLineHeightWithSpacing();
        var avail = ImGui.GetContentRegionAvail();
        var canvas = new Vector2(avail.X, MathF.Max(160 * s, avail.Y - controls - ImGui.GetTextLineHeightWithSpacing()));
        var views = _view == View.Both ? 2 : 1;
        var size = new Vector2((canvas.X - ImGui.GetStyle().ItemSpacing.X * (views - 1)) / views, canvas.Y);
        var fit = MathF.Min((size.Y - 20 * s) / MathF.Max(0.5f, maxY - minY), (size.X - 20 * s) / (2 * half));
        if (_view != View.Front)
        {
            DrawView("##side", Loc.T("侧视（右边是前）"), size, fit, minY, p => p.Z, preview, vanilla, ours);
            if (_view == View.Both) ImGui.SameLine();
        }
        if (_view != View.Side) DrawView("##front", Loc.T("正视"), size, fit, minY, p => p.X, preview, vanilla, ours);
        UiText.Fit(Loc.T("灰色：原版动作；青色：生成后的。滚轮缩放，拖动平移，双击复位"), Grey);

        // 位置和旋转：两列三行；滑杆宽度给最长的标签留够地方（英文标签更长）
        var labels = new[] { "前后（厘米）", "前后倾（度）", "上下（厘米）", "转身（度）", "左右（厘米）", "侧倾（度）" }
            .Max(l => ImGui.CalcTextSize(Loc.T(l)).X);
        var width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2 - labels - ImGui.GetStyle().ItemInnerSpacing.X * 2;
        if (ImGui.BeginTable("##place", 2, ImGuiTableFlags.SizingStretchSame))
        {
            void Slider(string label, ref float value, float range)
            {
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(MathF.Max(80 * s, width));
                ImGui.SliderFloat(label, ref value, -range, range, "%.0f");
            }
            Slider(Loc.T("前后（厘米）"), ref _offsetCm.Z, 100f);
            Slider(Loc.T("前后倾（度）"), ref _rotationDeg.X, 90f);
            Slider(Loc.T("上下（厘米）"), ref _offsetCm.Y, 100f);
            Slider(Loc.T("转身（度）"), ref _rotationDeg.Y, 180f);
            Slider(Loc.T("左右（厘米）"), ref _offsetCm.X, 100f);
            Slider(Loc.T("侧倾（度）"), ref _rotationDeg.Z, 90f);
            ImGui.EndTable();
        }
        if (ImGui.Button(Loc.T("位置归零")))
        {
            _offsetCm = Vector3.Zero;
            _rotationDeg = Vector3.Zero;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("整个身体绕骨盆转；站立的转完会重新踩到地上"));
    }

    private void DrawView(string id, string title, Vector2 size, float fit, float minY, Func<Vector3, float> horizontal, IdlePreview preview,
                          Vector3[] vanillaPos, Vector3[] oursPos)
    {
        var s = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var center = origin + size / 2;
        // 滚轮缩放（以鼠标为中心）、左 / 右键拖动平移、双击复位
        if (ImGui.IsItemHovered())
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0)
            {
                var k = MathF.Pow(1.15f, wheel);
                var zoom = Math.Clamp(_zoom * k, 0.5f, 12f);
                k = zoom / _zoom;
                var mouse = ImGui.GetMousePos() - center;
                _pan = mouse - (mouse - _pan) * k;
                _zoom = zoom;
            }
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _zoom = 1f;
                _pan = Vector2.Zero;
            }
        }
        if (ImGui.IsItemActive() && (ImGui.IsMouseDragging(ImGuiMouseButton.Left) || ImGui.IsMouseDragging(ImGuiMouseButton.Right)))
            _pan += ImGui.GetIO().MouseDelta;

        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.35f)), 4f);
        Vector2 Project(Vector3 p)
        {
            var fitted = new Vector2(origin.X + size.X / 2 + horizontal(p) * fit, origin.Y + size.Y - 10 * s - (p.Y - minY) * fit);
            return center + (fitted - center) * _zoom + _pan;
        }
        var ground = Project(Vector3.Zero).Y;
        draw.AddLine(new Vector2(origin.X, ground), new Vector2(origin.X + size.X, ground), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.25f)));
        var vanilla = ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.78f, 0.75f));
        var ours = ImGui.GetColorU32(PosegraphTheme.Accent);
        var thick = MathF.Min(4f, MathF.Max(1f, _zoom)) * s;
        for (var i = 0; i < preview.Names.Length; i++)
        {
            var p = preview.Parents[i];
            // n_root 在脚下的原点，连到身体的那根线没有意义；武器、装备挂点、裙摆、扩展骨骼不画（拉出很长的线，看不出身体）
            if (p < 0 || preview.Names[p] == "n_root" || !BodyBone(preview.Names[i])) continue;
            draw.AddLine(Project(vanillaPos[p]), Project(vanillaPos[i]), vanilla, thick);
            draw.AddLine(Project(oursPos[p]), Project(oursPos[i]), ours, 1.5f * thick);
        }
        // 骨盆（j_kosi）标个点，坐姿主要对它
        var pelvis = Array.IndexOf(preview.Names, "j_kosi");
        if (pelvis >= 0)
        {
            draw.AddCircleFilled(Project(vanillaPos[pelvis]), 2f * thick, vanilla);
            draw.AddCircleFilled(Project(oursPos[pelvis]), 2f * thick, ours);
        }
        draw.AddText(origin + new Vector2(6 * s, 4 * s), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.7f)), title);
        draw.PopClipRect();
    }

    /// <summary>预览里画的骨骼：身体本身（j_ 开头、不含武器 j_buki 和裙摆 j_sk_），加上骨盆 n_hara 和尾巴 n_sippo。</summary>
    private static bool BodyBone(string name)
        => name is "n_hara" || name.StartsWith("n_sippo") || (name.StartsWith("j_") && !name.StartsWith("j_buki") && !name.StartsWith("j_sk_"));

    /// <summary>这些种族的姿势：算过的直接用，没算过的找后端算（每张图、每个种族只算一次）。</summary>
    private async Task<Dictionary<string, string>> PosesAsync(string image, IReadOnlyList<string> races)
    {
        var missing = races.Where(r => !_poseCache.ContainsKey(r)).ToList();
        if (missing.Count > 0)
            foreach (var (race, json) in await _poses(image, missing))
                _poseCache[race] = json;
        return races.ToDictionary(r => r, r => _poseCache[r]);
    }

    private void StartPreview(string primary)
    {
        var image = _image!;
        var key = (_family, _slot, primary);
        _previewing = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var poses = await PosesAsync(image, [primary]);
                var (preview, error) = await _idle.PreviewAsync(poses[primary], key.Item1, key.Item2, primary);
                if (image != _image) return;
                _preview = preview;
                _previewKey = key;
                if (error is not null)
                {
                    _failedKey = key;
                    (_status, _statusColor) = (error, Red);
                }
            }
            catch (Exception ex)
            {
                _failedKey = key;   // 失败了不自动重试（避免每帧重来），用户可以再点
                (_status, _statusColor) = (Loc.F("预览失败：{0}", ex.Message), Red);
            }
            finally
            {
                _previewing = false;
            }
        });
    }

    /// <summary>不带性别的种族名（「人族中原 男」→「人族中原」，英文「Midlander Hyur (M)」→「Midlander Hyur」）。</summary>
    private static string BaseName(string code)
        => System.Text.RegularExpressions.Regex.Replace(Races.Label(code), @"\s*(\((M|F)\)|男|女)$", "");

    private void Save()
    {
        var image = _image!;
        var races = _races.Order().ToList();
        var family = _family;
        var slot = _slot;
        var modName = _modName;
        var merge = _merge;
        var reuse = _saved is { } last && last.Image == image && last.Name == modName ? last.Folder : null;
        _busy = true;
        _status = Loc.T("正在计算姿势…");
        _statusColor = Yellow;
        _ = Task.Run(async () =>
        {
            try
            {
                var poses = await PosesAsync(image, races);
                _status = Loc.T("正在生成动画…");
                var (error, mod) = await _idle.SaveAsync(poses, family, slot, _offsetCm / 100f, _rotationDeg, _breath.Clone(), modName, reuse, merge,
                                                         Path.GetFileName(image));
                if (mod is not null) _saved = (image, modName, mod);
                (_status, _statusColor) = error is null
                    ? (Loc.F("已存进模组「{0}」的「{1}」并启用（{2} 个种族），游戏里切到这个姿势就能看到", mod ?? modName, IdleSlots.SlotLabel(family, slot), races.Count), Green)
                    : (error, Red);
            }
            catch (Exception ex)
            {
                (_status, _statusColor) = (Loc.F("转换失败：{0}", ex.Message), Red);
            }
            finally
            {
                _busy = false;
            }
        });
    }
}
