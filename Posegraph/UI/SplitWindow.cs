using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.DragDrop;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Posegraph.Library;

namespace Posegraph.UI;

/// <summary>
/// 角色列表，两种模式（顶部切换）：
/// - 拆分：GPose 里的每个角色绑一张图（从图库拖进来），一起应用；某个角色要细调时「编辑」切到主窗口。
///   每张图用它自己保存的修正（3D 页保存），挂了辅助视角就融合。绑定按角色身份记，只在本次 GPose 内有效（角色编号每次进 GPose 会变）。
/// - 合并：主窗口当前那张图里的多个人，每人选一个角色（MainWindow.Merge.cs）；人物框在主窗口图像页上改。
/// 默认贴在主窗口右边。
/// </summary>
public sealed class SplitWindow : Window, IDisposable
{
    private static readonly Vector4 Green = PosegraphTheme.Green;
    private static readonly Vector4 Yellow = PosegraphTheme.Yellow;
    private static readonly Vector4 Red = PosegraphTheme.Red;
    private static readonly Vector4 Grey = PosegraphTheme.Grey;
    private static readonly Vector4 Blue = PosegraphTheme.Blue;

    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly MainWindow _main;
    private readonly LibraryWindow _libraryWindow;
    private readonly ImageLibrary _library;
    private readonly AuxViewsMenu _auxMenu;
    private readonly IDragDropManager _dragDrop;
    private readonly ThumbnailCache _thumbs;

    private readonly Dictionary<string, string> _bindings = new();                       // 角色身份 → 图片
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Text, Vector4 Color)> _rowStatus = new();   // 角色身份 → 上次应用结果（后台写）
    private readonly Dictionary<int, string> _keys = new();                                // ObjectIndex → 角色身份（每秒刷新）
    private DateTime _lastKeys = DateTime.MinValue;
    private bool _wasGposing;
    private volatile bool _applying;
    private string _status = "";
    private Vector4 _statusColor = Grey;
    private string? _contextKey;

    public SplitWindow(Configuration config, IDalamudPluginInterface pi, MainWindow main, LibraryWindow libraryWindow,
                       ImageLibrary library, AuxViewsMenu auxMenu, ITextureProvider textures, IDragDropManager dragDrop, IPluginLog log)
        : base(Loc.T("Posegraph 角色列表") + "###PosegraphSplit")
    {
        _config = config;
        _pi = pi;
        _main = main;
        _libraryWindow = libraryWindow;
        _library = library;
        _auxMenu = auxMenu;
        _dragDrop = dragDrop;
        _thumbs = new ThumbnailCache(textures, log, (int)(64 * ImGuiHelpers.GlobalScale));
        Size = new Vector2(340, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(280, 200), MaximumSize = new Vector2(2000, 4000) };
    }

    public void Dispose() => _thumbs.Dispose();

    private void SetStatus(string text, Vector4 color)
    {
        _status = text;
        _statusColor = color;
    }

    public override void PreDraw()
    {
        // 贴在主窗口右边；主窗口没打开（拿不到位置）时不强制
        if (_config.SplitDock && _main.IsOpen && _main.WindowSize.X > 0)
        {
            Position = _main.WindowPos + new Vector2(_main.WindowSize.X + 4, 0);
            PositionCondition = ImGuiCond.Always;
        }
        else
        {
            Position = null;
        }
        PosegraphTheme.Push();
    }

    public override void PostDraw() => PosegraphTheme.Pop();

    private void RefreshKeys()
    {
        if (_main.InGpose != _wasGposing)
        {
            // 进出 GPose：角色编号会重新分配，绑定作废
            _wasGposing = _main.InGpose;
            _bindings.Clear();
            _rowStatus.Clear();
            _keys.Clear();
            foreach (var p in _main.People) p.Actor = null;
        }
        if (DateTime.UtcNow - _lastKeys < TimeSpan.FromSeconds(1)) return;
        _lastKeys = DateTime.UtcNow;
        _keys.Clear();
        foreach (var (index, _) in _main.Actors)
            if (_main.Identify(index) is { } id) _keys[index] = id.Key;
    }

    /// <summary>合并模式（一张图多人）还在打磨，这一版先关掉：界面上置灰、不能切过去，存着的设置也当拆分模式。</summary>
    public const bool MergeAvailable = false;

    /// <summary>当前是不是合并模式（关掉时一律是拆分）。</summary>
    public bool MergeMode => MergeAvailable && _config.SplitMerge;

    public override void Draw()
    {
        RefreshKeys();
        DrawHeader();
        ImGui.Separator();
        if (MergeMode)
        {
            DrawMerge();
            return;
        }
        var footer = ImGui.GetFrameHeightWithSpacing() * 2;
        using (var list = ImRaii.Child("##actors", new Vector2(0, -footer)))
            if (list) DrawActors();
        DrawFooter();
    }

    private void DrawHeader()
    {
        var merge = MergeMode;
        if (ImGui.RadioButton(Loc.T("拆分"), !merge)) SetMode(false);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("每个角色一张图"));
        ImGui.SameLine();
        using (ImRaii.Disabled(!MergeAvailable))
            if (ImGui.RadioButton(Loc.T("合并"), merge)) SetMode(true);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(MergeAvailable ? Loc.T("一张图里的多个人分给各个角色") : Loc.T("一张图里的多个人分给各个角色（还在打磨，下个版本开放）"));
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(merge
            ? Loc.T("主窗口当前那张图里有多个人时用：\n1. 「找人」自动框出图里的人（主窗口图像页上可以拖边角调整、空白处拖出新框、右键删除）\n2. 勾选要识别的人，点「识别」\n3. 给每个人选一个角色，点「全部应用」\n要细调某个人：点「编辑」，主窗口切到这个人（各人的调整值分开保存）。\n角色只在这次 GPose 里有效。")
            : Loc.T("从图库把图拖到角色左边的格子里绑定，每个角色一张图。\n「全部应用」用每张图保存的修正（3D 页保存），没有就用当前预设；挂了辅助视角的自动融合。\n要细调某个角色：点「编辑」切到主窗口，调好后在 3D 页保存修正。\n绑定只在这次 GPose 里有效。"));
        // 放得下就和模式切换同一行，否则另起一行（英文较长）
        var dockLabel = Loc.T("贴在主窗口右边");
        var dockWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(dockLabel).X;
        ImGui.SameLine();
        if (dockWidth > ImGui.GetContentRegionAvail().X) ImGui.NewLine();
        var dock = _config.SplitDock;
        if (ImGui.Checkbox(dockLabel, ref dock))
        {
            _config.SplitDock = dock;
            _pi.SavePluginConfig(_config);
        }
    }

    private void SetMode(bool merge)
    {
        _config.SplitMerge = merge;
        _pi.SavePluginConfig(_config);
    }

    /// <summary>主窗口找到 2 个以上的人时：打开并切到合并页。</summary>
    public void ShowMerge()
    {
        if (!MergeAvailable) return;
        if (!_config.SplitMerge) SetMode(true);
        IsOpen = true;
    }

    // ------------------------------------------------------------------ 合并模式

    private static readonly (string Kind, string Label)[] Detectors = [("auto", "自动"), ("photo", "真人"), ("anime", "插画")];

    private void DrawMerge()
    {
        var image = _main.CurrentImage;
        if (image is null)
        {
            ImGui.TextDisabled(Loc.T("先在主窗口打开一张图"));
            return;
        }
        var people = _main.People;
        var busy = _main.Drafting || _main.Recognizing || _main.MergeApplying;

        // 涂人 / 自动初稿（找人 → 识别 → 投影成涂抹）
        using (ImRaii.Disabled(busy || _main.LoadingPaint || !_main.BackendOnline))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.PaintBrush, Loc.T("涂人"))) _main.StartPainting();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!_main.BackendOnline ? Loc.T("后端离线：先在主窗口启动后端")
                : Loc.T("打开涂人窗口：每个人一个图层，涂上大致范围（看得见的部分 + 被挡住的部分），涂过的范围就是人物框"));
        ImGui.SameLine();
        var current = Detectors.FirstOrDefault(d => d.Kind == _config.MergeDetector).Label ?? "自动";
        ImGui.SetNextItemWidth(70 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##detector", Loc.T(current)))
        {
            foreach (var (k, label) in Detectors)
                if (ImGui.Selectable(Loc.T(label), _config.MergeDetector == k))
                {
                    _config.MergeDetector = k;
                    _pi.SavePluginConfig(_config);
                }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("自动初稿找人用的模型：自动（先按真人找，找不到再按插画找）/ 真人照片 / 插画"));
        ImGui.SameLine();
        using (ImRaii.Disabled(busy || !_main.BackendOnline || !_main.MergeSupported || !_main.DetectorsReady))
            if (ImGui.Button(_main.Drafting ? Loc.T("生成中…") : Loc.T("自动初稿")))
            {
                if (_main.Paint is { } painted && painted.PixelCounts().Any(c => c > 0)) ImGui.OpenPopup("##draftConfirm");
                else _main.AutoDraft();
            }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!_main.BackendOnline ? Loc.T("后端离线：先在主窗口启动后端")
                : !_main.MergeSupported ? Loc.T("后端版本过旧，不支持合并模式：在设置页更新后端，然后重启后端")
                : !_main.DetectorsReady ? Loc.T("后端没装人物检测组件，不能自动初稿：请在涂人窗口里自己涂")
                : Loc.T("自动找人并识别，把识别结果投影成涂抹（替换现在涂的），再在涂人窗口里改错的地方"));
        if (ImGui.BeginPopup("##draftConfirm"))
        {
            ImGui.TextUnformatted(Loc.T("自动初稿会替换现在涂的内容，继续吗？"));
            if (ImGui.Button(Loc.T("替换")))
            {
                _main.AutoDraft();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("取消"))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        UiText.Fit(Path.GetFileNameWithoutExtension(image), Grey);
        if (_main.BackendOnline && !_main.MergeSupported)
            using (ImRaii.PushColor(ImGuiCol.Text, Yellow))
                ImGui.TextWrapped(Loc.T("后端版本过旧，不支持合并模式：在设置页更新后端，然后重启后端"));

        var footer = ImGui.GetFrameHeightWithSpacing() * 2;
        using (var list = ImRaii.Child("##people", new Vector2(0, -footer)))
        {
            if (list)
            {
                if (people.Count == 0)
                    ImGui.TextWrapped(Loc.T("还没有涂人：点「涂人」给每个人涂上大致范围，或者点「自动初稿」先自动生成再改"));
                for (var i = 0; i < people.Count; i++)
                    using (ImRaii.PushId(i))
                        DrawPerson(i, people[i], busy);
            }
        }
        DrawMergeFooter(people, busy);
    }

    private void DrawPerson(int index, MainWindow.MergePerson person, bool busy)
    {
        // 编号（与图像页上的框同色）
        var color = MainWindow.PersonColor(index);
        var label = (index + 1).ToString();
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetFrameHeight());
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + size, ImGui.GetColorU32(person.Checked ? color : color with { W = 0.4f }), 4);
        dl.AddText(pos + (size - ImGui.CalcTextSize(label)) / 2, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 1)), label);
        ImGui.Dummy(size);
        ImGui.SameLine();

        var check = person.Checked;
        using (ImRaii.Disabled(busy))
            if (ImGui.Checkbox("##check", ref check))
            {
                person.Checked = check;
                person.Status = null;
            }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("识别并应用这个人（不勾选的跳过）"));
        ImGui.SameLine();
        // 复刻站位的基准：它的角色不动，其他人围着它摆
        using (ImRaii.Disabled(!_config.MergePlacement))
            if (ImGui.RadioButton("##anchor", _main.Anchor == person)) _main.Anchor = person;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("站位基准：复刻站位时这个人的角色不动，其他人按照片里的相对位置摆在它周围"));
        ImGui.SameLine();

        // 角色
        var actorName = person.Actor is { } a ? _main.Actors.FirstOrDefault(x => x.Index == a).Name : null;
        var edit = person.Human is not null ? ImGui.CalcTextSize(Loc.T("编辑")).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X : 0;
        var width = Math.Max(60, ImGui.GetContentRegionAvail().X - edit);
        ImGui.SetNextItemWidth(width);
        using (ImRaii.Disabled(busy || !_main.InGpose))
            if (ImGui.BeginCombo("##actor", UiText.ComboPreview(actorName ?? Loc.T("未指定角色"), width)))
            {
                if (ImGui.Selectable(Loc.T("未指定角色"), person.Actor is null)) _main.SetPersonActor(person, null);
                foreach (var (i, name) in _main.Actors)
                    if (ImGui.Selectable($"{name}##{i}", person.Actor == i)) _main.SetPersonActor(person, i);
                ImGui.EndCombo();
            }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(_main.InGpose ? Loc.T("这个人的姿势应用到哪个角色") : Loc.T("进入 GPose 后选择角色"));
        if (person.Human is not null)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(busy))
                if (ImGui.Button(Loc.T("编辑"))) _main.EditPerson(person);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("主窗口切到这个人细调（调整值各人分开保存）"));
        }

        // 第二行：来源或状态
        ImGui.Dummy(new Vector2(size.X, 0));
        ImGui.SameLine();
        var painted = _main.Paint is { } doc && index < doc.Count && doc.Box(index, _main.SessionImageSize ?? [doc.Width, doc.Height]) is not null;
        if (person.Status is { } st) UiText.Fit(st.Text, st.Color);
        else if (!painted) UiText.Fit(Loc.T("还没涂"), Yellow);
        else if (person.Checked && person.Human is null) UiText.Fit(Loc.T("涂好了，未识别"), Yellow);
        else UiText.Fit(person.Human is null ? Loc.T("不识别") : Loc.T("已识别"), Grey);
        ImGui.Separator();
    }

    private void DrawMergeFooter(IReadOnlyList<MainWindow.MergePerson> people, bool busy)
    {
        var chosen = people.Count(p => p.Checked);
        using (ImRaii.Disabled(chosen == 0 || busy || !_main.BackendOnline || !_main.MergeSupported))
            if (ImGui.Button(_main.Recognizing ? Loc.T("识别中…") : Loc.F("识别（{0} 人）", chosen)))
                _main.RecognizePeople();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("按涂抹把勾选的人一起识别（同一张图、同一个镜头）；同样的涂抹识别过的直接读缓存"));
        ImGui.SameLine();
        var ready = people.Count(p => p.Checked && p.Human is not null && p.Actor is not null);
        using (ImRaii.Disabled(ready == 0 || busy || !_main.MergeSessionActive || !_main.InGpose))
            if (ImGui.Button(_main.MergeApplying ? Loc.T("应用中…") : Loc.F("全部应用（{0} 人）", ready)))
                _main.ApplyPeople();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("每个人用自己的调整值应用到选定的角色（勾选、识别过、选了角色的才应用）"));

        ImGui.SameLine();
        var place = _config.MergePlacement;
        if (ImGui.Checkbox(Loc.T("复刻站位"), ref place))
        {
            _config.MergePlacement = place;
            _pi.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("「全部应用」后按照片摆好各角色的位置和朝向：选中「站位基准」的人的角色不动，其他人摆在它周围；照片里看得见地面时按地面摆正、大家踩在同一高度"));

        using (ImRaii.Disabled(busy || !_main.MergeSessionActive || !_main.InGpose || _main.Anchor is null))
            if (ImGuiComponents.IconButton("##mergeCamera", FontAwesomeIcon.Video)) _main.ReplicateMergeCamera();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("按站位基准的人复刻照片镜头（复刻过站位的话，所有人都在画面里对应的位置）"));
        ImGui.SameLine();
        if (_main.PeopleChanged)
            UiText.Fit(Loc.T("涂抹改过了：点「识别」更新"), Yellow);
        else if (people.Count(p => p.Checked && p.Human is not null) > ready && _main.InGpose)
            UiText.Fit(Loc.T("还有人没选角色"), Grey);
    }

    private void DrawActors()
    {
        if (!_main.InGpose)
        {
            ImGui.TextDisabled(Loc.T("进入 GPose 后列出角色"));
            return;
        }
        if (_main.Actors.Length == 0)
        {
            ImGui.TextDisabled(Loc.T("没有可摆姿势的角色（Brio 里新建或选中角色）"));
            return;
        }
        foreach (var (index, name) in _main.Actors)
        {
            if (!_keys.TryGetValue(index, out var key)) continue;
            using (ImRaii.PushId(key))
                DrawRow(index, name, key);
        }
    }

    private void DrawRow(int index, string name, string key)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var slot = 56 * scale;
        var image = _bindings.GetValueOrDefault(key);
        if (image is not null && !File.Exists(image))
        {
            _bindings.Remove(key);   // 图被删了或改名了
            image = null;
        }

        // 左边的图片格子：拖放目标，右键菜单
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##slot", new Vector2(slot, slot));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(slot, slot), ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), 4);
        if (image is not null && _thumbs.Get(image, out _) is { } wrap)
        {
            var fit = Math.Min((slot - 4) / wrap.Width, (slot - 4) / wrap.Height);
            var sz = new Vector2(wrap.Width, wrap.Height) * fit;
            var tl = pos + (new Vector2(slot, slot) - sz) / 2;
            dl.AddImage(wrap.Handle, tl, tl + sz);
        }
        else if (image is null)
        {
            var plus = "+";
            var sz = ImGui.CalcTextSize(plus);
            dl.AddText(pos + (new Vector2(slot, slot) - sz) / 2, ImGui.GetColorU32(ImGuiCol.TextDisabled), plus);
        }
        if (hovered && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGui.SetTooltip(image is null ? Loc.T("从图库把图拖到这里绑定，右键更多") : Path.GetFileName(image) + Loc.T("\n双击在主窗口编辑，右键更多"));
        if (hovered && image is not null && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) _main.Edit(index, image);
        AcceptDrop(key);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _contextKey = key;
            ImGui.OpenPopup("##slotMenu");
        }
        if (ImGui.BeginPopup("##slotMenu"))
        {
            DrawSlotMenu(index, key, image);
            ImGui.EndPopup();
        }

        // 右边：名字、图片、状态、按钮
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            UiText.Fit(name, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            if (image is null) ImGui.TextDisabled(Loc.T("未绑定图片"));
            else
            {
                var aux = _library.KnownHash(image) is { } sha ? _library.AuxCount(sha) : 0;
                UiText.Fit(Path.GetFileNameWithoutExtension(image) + (aux > 0 ? Loc.F("　辅助视角 {0}", aux) : ""), ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            }
            if (_rowStatus.TryGetValue(key, out var st)) UiText.Fit(st.Text, st.Color);
            using (ImRaii.Disabled(image is null || _applying))
            {
                if (ImGui.SmallButton(Loc.T("编辑"))) _main.Edit(index, image!);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Loc.T("切到主窗口编辑这个角色（3D 页保存的修正，「全部应用」时也会用）"));
                ImGui.SameLine();
                if (ImGui.SmallButton(Loc.T("应用"))) ApplyAll([(index, key, image!)]);
            }
        }
        ImGui.Separator();
    }

    private void DrawSlotMenu(int index, string key, string? image)
    {
        if (_main.CurrentImage is { } current && ImGui.MenuItem(Loc.F("用主窗口当前的图（{0}）", Path.GetFileNameWithoutExtension(current))))
            Bind(key, current);
        if (image is not null)
        {
            if (ImGui.MenuItem(Loc.T("在主窗口编辑"))) _main.Edit(index, image);
            if (ImGui.BeginMenu(Loc.T("辅助视角")))
            {
                _auxMenu.Draw(image, [], SetStatus);
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem(Loc.T("解除绑定")))
            {
                _bindings.Remove(key);
                _rowStatus.TryRemove(key, out _);
            }
        }
    }

    /// <summary>从图库拖来的图（取第一张），或从系统拖进来的图片文件。</summary>
    private void AcceptDrop(string key)
    {
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload(LibraryWindow.InternalDragLabel, ImGuiDragDropFlags.None);
            if (!payload.IsNull && _libraryWindow.DraggedImages.FirstOrDefault() is { } path) Bind(key, path);
            ImGui.EndDragDropTarget();
        }
        if (_dragDrop.CreateImGuiTarget(LibraryWindow.ExternalDragLabel, out var files, out _)
            && files.FirstOrDefault(ImageLibrary.IsImage) is { } file)
            Bind(key, file);
    }

    private void Bind(string key, string image)
    {
        _bindings[key] = image;
        _rowStatus.TryRemove(key, out _);
    }

    private void DrawFooter()
    {
        var rows = _main.Actors.Where(a => _keys.ContainsKey(a.Index))
                        .Select(a => (a.Index, Key: _keys[a.Index], Image: _bindings.GetValueOrDefault(_keys[a.Index])))
                        .Where(r => r.Image is not null).Select(r => (r.Index, r.Key, r.Image!)).ToList();
        using (ImRaii.Disabled(rows.Count == 0 || _applying || !_main.InGpose || !_main.BackendOnline))
            if (ImGui.Button(_applying ? Loc.T("应用中…") : Loc.F("全部应用（{0} 个角色）", rows.Count)))
                ApplyAll(rows);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(_main.BackendOnline ? Loc.T("按顺序逐个识别（有缓存的不重跑）并应用") : Loc.T("后端离线：先在主窗口启动后端"));
        UiText.Fit(_status, _statusColor);
    }

    private void ApplyAll(List<(int Index, string Key, string Image)> rows)
    {
        if (_applying) return;
        _applying = true;
        _ = Task.Run(async () =>
        {
            var ok = 0;
            try
            {
                await using var batch = _main.Recognition.Batch();   // 所有角色的图（含辅助视角）只加载一次模型
                foreach (var (index, key, image) in rows)
                {
                    _rowStatus[key] = (Loc.T("识别并应用中…"), Yellow);
                    SetStatus(Loc.F("正在应用 {0}…", Path.GetFileNameWithoutExtension(image)), Yellow);
                    try
                    {
                        var error = await _main.ApplyToActorAsync(index, image);
                        _rowStatus[key] = error is null ? (Loc.T("已应用"), Green) : (error, Red);
                        if (error is null) ok++;
                    }
                    catch (Exception ex)
                    {
                        _rowStatus[key] = (ex.Message, Red);
                    }
                }
                SetStatus(ok == rows.Count ? Loc.F("已应用 {0} 个角色", ok) : Loc.F("已应用 {0} 个，{1} 个失败", ok, rows.Count - ok),
                          ok == rows.Count ? Green : Red);
            }
            finally
            {
                _applying = false;
            }
        });
    }
}
