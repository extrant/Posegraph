using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace Posegraph.UI;

/// <summary>
/// 合并模式的涂人窗口，像 PS 的图层：每个人一个图层（颜色、编号和角色列表一致），涂抹分两层——
/// 「看得见」涂这个人在照片里看得见的部分（作为遮罩交给识别模型），「被挡住」涂被别人或东西挡住的身体大概在哪
/// （斜线显示，只用来放大这个人的范围）。涂过的范围就是人物框。
/// 画笔把像素划给当前图层、橡皮擦只擦当前图层、魔棒按照片颜色选一片相连的区域加进 / 移出当前图层。
/// 画布：滚轮缩放、中键或按住空格拖动平移；画笔模式右键拖动是橡皮擦，魔棒右键是移出。涂抹由 MainWindow 持有（MainWindow.Paint）。
/// </summary>
public sealed class MaskEditorWindow : Window, IDisposable
{
    private enum Tool { Brush, Eraser, Wand }

    private static readonly Vector4 Grey = PosegraphTheme.Grey;
    private static readonly Vector4 Yellow = PosegraphTheme.Yellow;

    private readonly MainWindow _main;
    private readonly ITextureProvider _textures;
    private readonly List<IDalamudTextureWrap> _retired = new();
    private MaskDocument? _doc;                 // 上一帧画的是哪份涂抹（换了就重建纹理、适应窗口）
    private IDalamudTextureWrap? _photo, _overlay;
    private Task<IDalamudTextureWrap>? _photoTask, _overlayTask;
    private MaskDocument? _photoDoc;            // _photoTask 是给哪份涂抹建的
    private (int Version, int Count, int Layer, float Opacity, string Hidden) _overlayState = (-1, 0, -1, 0, "");
    private DateTime _overlayAt = DateTime.MinValue;

    private Tool _tool = Tool.Brush;
    private bool _hiddenPlane;                  // 涂的是「被挡住的范围」这一层
    private int _layer;
    private bool[] _hidden = new bool[MaskDocument.MaxPeople];   // 图层只是不显示
    private float _opacity = 0.5f;
    private float _brush = 16;                  // 笔头半径（标签图像素）
    private int _tolerance = 24;                // 魔棒容差（RGB 各通道最大差）
    private float _zoom = 1;
    private Vector2 _pan;
    private bool _fit = true;
    private Vector2? _last;                     // 正在画的这一笔上一个点（标签图像素）
    private bool _erasing;

    public MaskEditorWindow(MainWindow main, ITextureProvider textures)
        : base(Loc.T("Posegraph 涂人") + "###PosegraphMasks")
    {
        _main = main;
        _textures = textures;
        Size = new Vector2(900, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 420), MaximumSize = new Vector2(4000, 4000) };
        BgAlpha = 1f;   // 常盖在主窗口上面，背景不透明，免得透出底下的字
    }

    public override void OnClose() => _main.SavePaint();

    public override void PreDraw() => PosegraphTheme.Push();

    public override void PostDraw() => PosegraphTheme.Pop();

    public override void Draw()
    {
        foreach (var t in _retired) t.Dispose();   // 上一帧换下来的纹理：这一帧已经不画了，可以释放
        _retired.Clear();
        var doc = _main.Paint;
        if (!ReferenceEquals(doc, _doc))
        {
            _doc = doc;
            Retire(ref _photo);
            Retire(ref _overlay);
            _overlayState = (-1, 0, -1, 0, "");
            _hidden = new bool[MaskDocument.MaxPeople];
            _layer = 0;
            _fit = true;
        }
        if (doc is null)
        {
            ImGui.TextDisabled(_main.LoadingPaint ? Loc.T("正在打开…") : Loc.T("在主窗口打开一张图，然后在角色列表的「合并」页点「涂人」"));
            return;
        }
        _layer = Math.Clamp(_layer, 0, Math.Max(0, doc.Count - 1));
        HandleShortcuts(doc);
        DrawToolbar(doc);
        var scale = ImGuiHelpers.GlobalScale;
        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing() + 6 * scale;
        using (var left = ImRaii.Child("##layers", new Vector2(200 * scale, -footer), true))
            if (left) DrawLayers(doc);
        ImGui.SameLine();
        using (var canvas = ImRaii.Child("##canvas", new Vector2(0, -footer), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            if (canvas) DrawCanvas(doc);
        DrawFooter(doc);
    }

    // ------------------------------------------------------------------ 工具栏、图层、底栏

    private void DrawToolbar(MaskDocument doc)
    {
        ToolButton(Tool.Brush, FontAwesomeIcon.PaintBrush, Loc.T("画笔（B）：把涂到的像素划给当前图层；右键拖动是橡皮擦"));
        ImGui.SameLine();
        ToolButton(Tool.Eraser, FontAwesomeIcon.Eraser, Loc.T("橡皮擦（E）：只擦当前图层"));
        ImGui.SameLine();
        ToolButton(Tool.Wand, FontAwesomeIcon.Magic, Loc.T("魔棒（W）：左键把颜色相近的一片相连区域加进当前图层，右键移出"));
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Grey, Loc.T("涂："));
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.T("看得见"), !_hiddenPlane)) _hiddenPlane = false;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("这个人在照片里看得见的部分（H 切换）：大致涂到就行，看得见的手脚要涂到"));
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.T("被挡住"), _hiddenPlane)) _hiddenPlane = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("被别人或东西挡住的身体大概在哪（H 切换，斜线显示）：比如沙发后面的腿。只用来放大这个人的范围，模型才不会把腿折起来"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (_tool == Tool.Wand)
        {
            ImGui.SliderInt("##tolerance", ref _tolerance, 1, 128, Loc.T("容差 %d"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("颜色相差多少以内算同一片（越大选得越多）"));
        }
        else
        {
            ImGui.SliderFloat("##brush", ref _brush, 1, 80, Loc.T("大小 %.0f"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("笔头半径（[ 和 ] 调整）"));
        }
        ImGui.SameLine();
        if (IconButton("##undo", FontAwesomeIcon.Undo, Loc.T("撤销（Ctrl+Z）"), doc.CanUndo)) doc.Undo();
        ImGui.SameLine();
        if (IconButton("##redo", FontAwesomeIcon.Redo, Loc.T("重做（Ctrl+Y）"), doc.CanRedo)) doc.Redo();
        ImGui.SameLine();
        if (IconButton("##fit", FontAwesomeIcon.Expand, Loc.T("适应窗口"))) _fit = true;
    }

    private void ToolButton(Tool tool, FontAwesomeIcon icon, string tip)
    {
        // 选中的工具换成蓝色（主题的按钮都是橙色，深一点看不出来）
        using (ImRaii.PushColor(ImGuiCol.Button, PosegraphTheme.Blue, _tool == tool))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, PosegraphTheme.Blue, _tool == tool))
            if (ImGuiComponents.IconButton($"##tool{tool}", icon)) _tool = tool;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    private static bool IconButton(string id, FontAwesomeIcon icon, string tip, bool enabled = true)
    {
        bool clicked;
        using (ImRaii.Disabled(!enabled))
            clicked = ImGuiComponents.IconButton(id, icon);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tip);
        return clicked && enabled;
    }

    private void DrawLayers(MaskDocument doc)
    {
        ImGui.TextColored(Grey, Loc.T("人物（每人一个图层）"));
        var counts = doc.PixelCounts();
        var dl = ImGui.GetWindowDrawList();
        var busy = _main.Recognizing || _main.MergeApplying;
        for (var k = 0; k < doc.Count; k++)
        {
            using var id = ImRaii.PushId(k);
            if (ImGuiComponents.IconButton("##eye", _hidden[k] ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye)) _hidden[k] = !_hidden[k];
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("显示 / 隐藏这个图层（只影响显示）"));
            ImGui.SameLine();
            var swatch = ImGui.GetFrameHeight();
            var at = ImGui.GetCursorScreenPos();
            dl.AddRectFilled(at + new Vector2(0, swatch * 0.2f), at + new Vector2(swatch * 0.6f, swatch * 0.8f), ImGui.GetColorU32(MainWindow.PersonColor(k)), 3);
            ImGui.Dummy(new Vector2(swatch * 0.6f, swatch));
            ImGui.SameLine();
            var remove = swatch + ImGui.GetStyle().ItemSpacing.X;
            if (ImGui.Selectable(Loc.F("{0} 号  {1:0.0}%", k + 1, 100f * counts[k] / (doc.Width * doc.Height)), k == _layer,
                                 ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - remove, swatch)))
                _layer = k;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.F("当前图层：画笔、魔棒都作用在它上面（数字键 {0}）", k + 1));
            ImGui.SameLine();
            if (IconButton("##remove", FontAwesomeIcon.Trash, Loc.T("删掉这个人（涂抹一起删，不能撤销；后面的人编号往前挪）"), !busy))
                _main.RemovePersonAt(k);
        }
        using (ImRaii.Disabled(busy || doc.Count >= MaskDocument.MaxPeople))
            if (ImGui.Button(Loc.T("+ 添加人物"), new Vector2(-1, 0)))
            {
                _main.AddPerson();
                _layer = doc.Count - 1;
            }
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.SliderFloat("##opacity", ref _opacity, 0.1f, 0.9f, Loc.T("不透明度 %.1f"));
        if (ImGui.Button(_hiddenPlane ? Loc.T("清空当前图层（被挡住）") : Loc.T("清空当前图层（看得见）"), new Vector2(-1, 0))) doc.Clear(_layer, _hiddenPlane);
        using (ImRaii.Disabled(!_main.MergeSessionActive || _main.LoadingPaint || busy))
            if (ImGui.Button(Loc.T("按识别结果重新生成"), new Vector2(-1, 0))) _main.RegeneratePaint();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("「看得见」换成上次识别结果投影到照片上的样子（「被挡住」不动，可以撤销）；识别过才能用"));
        ImGui.Spacing();
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Grey, Loc.T("不用涂得很准，大致范围就行；椅子、背景不属于任何人。滚轮缩放，中键或按住空格拖动平移"));
        ImGui.PopTextWrapPos();
    }

    private void DrawFooter(MaskDocument doc)
    {
        ImGui.Spacing();
        var counts = doc.PixelCounts();
        var empty = Enumerable.Range(0, doc.Count).Count(k => counts[k] == 0);
        if (empty > 0)
            UiText.Fit(Loc.F("还有 {0} 个人没涂（没涂的人不识别）", empty), Yellow);
        else if (_main.PeopleChanged)
            UiText.Fit(Loc.T("涂抹改过了：点「识别」更新"), Yellow);
        else
            UiText.Fit(Loc.T("框是每个人涂过的范围（看得见 + 被挡住）；被挡住的腿往哪伸，就用「被挡住」往那边涂"), Grey);
        var people = _main.People;
        var ready = Enumerable.Range(0, Math.Min(doc.Count, people.Count)).Count(k => counts[k] > 0 && people[k].Checked);
        using (ImRaii.Disabled(_main.Recognizing || !_main.BackendOnline || ready == 0))
            if (ImGui.Button(_main.Recognizing ? Loc.T("识别中…") : Loc.F("识别（{0} 人）", ready)))
                _main.RecognizePeople();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("按涂抹识别勾选的人（同样的涂抹识别过的直接读缓存）"));
    }

    // ------------------------------------------------------------------ 画布

    private void DrawCanvas(MaskDocument doc)
    {
        EnsureTextures(doc);
        var io = ImGui.GetIO();
        var avail = Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.One);
        var area = ImGui.GetCursorScreenPos();
        var docSize = new Vector2(doc.Width, doc.Height);
        var fitZoom = MathF.Min(avail.X / doc.Width, avail.Y / doc.Height);
        if (_fit)
        {
            _zoom = fitZoom;
            _pan = (avail - docSize * _zoom) / 2;
            _fit = false;
        }
        ImGui.InvisibleButton("##paint", avail, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var mouse = io.MousePos;
        var panning = ImGui.IsKeyDown(ImGuiKey.Space) || ImGui.IsMouseDown(ImGuiMouseButton.Middle);

        if (hovered && io.MouseWheel != 0)
        {
            // 以鼠标为中心缩放
            var under = (mouse - area - _pan) / _zoom;
            _zoom = Math.Clamp(_zoom * (1 + io.MouseWheel * 0.15f), fitZoom * 0.5f, 40f);
            _pan = mouse - area - under * _zoom;
        }
        if (active && panning && (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0)))
            _pan += io.MouseDelta;

        var origin = area + _pan;
        var at = (mouse - origin) / _zoom;   // 鼠标在标签图上的位置
        var canPaint = doc.Count > 0 && !_main.Recognizing;
        if (canPaint && !panning && _tool is Tool.Brush or Tool.Eraser)
        {
            var left = active && ImGui.IsMouseDown(ImGuiMouseButton.Left);
            var right = active && ImGui.IsMouseDown(ImGuiMouseButton.Right) && _tool == Tool.Brush;
            if (left || right)
            {
                var erase = _tool == Tool.Eraser || right;
                if (_last is null || erase != _erasing)
                {
                    doc.BeginStep();
                    _last = at;
                    _erasing = erase;
                }
                doc.Stroke(_last.Value, at, _brush, _layer, erase, _hiddenPlane);
                _last = at;
            }
            else _last = null;
        }
        else _last = null;
        if (canPaint && !panning && _tool == Tool.Wand && hovered)
        {
            var add = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            if (add || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                doc.Apply(doc.Wand((int)at.X, (int)at.Y, _tolerance), _layer, remove: !add, _hiddenPlane);
        }

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(area, area + avail, true);
        dl.AddRectFilled(area, area + avail, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 1)));
        var size = docSize * _zoom;
        if (_photo is not null) dl.AddImage(_photo.Handle, origin, origin + size);
        if (_overlay is not null) dl.AddImage(_overlay.Handle, origin, origin + size);
        DrawBoxes(doc, dl, origin);
        if (hovered && !panning)
        {
            var color = doc.Count > 0 ? MainWindow.PersonColor(_layer) : new Vector4(1, 1, 1, 1);
            if (_tool == Tool.Wand)
            {
                var c = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.9f));
                var r = 7 * ImGuiHelpers.GlobalScale;
                dl.AddLine(mouse - new Vector2(r, 0), mouse + new Vector2(r, 0), c);
                dl.AddLine(mouse - new Vector2(0, r), mouse + new Vector2(0, r), c);
            }
            else
            {
                dl.AddCircle(mouse, _brush * _zoom, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.8f)), 48, 2.5f);
                dl.AddCircle(mouse, _brush * _zoom, ImGui.GetColorU32(_tool == Tool.Eraser ? new Vector4(1, 1, 1, 1) : color), 48, 1.2f);
            }
            ImGui.SetMouseCursor(ImGuiMouseCursor.None);
        }
        else if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        dl.PopClipRect();
    }

    /// <summary>每个人的范围（交给识别模型的人物框）。</summary>
    private void DrawBoxes(MaskDocument doc, ImDrawListPtr dl, Vector2 origin)
    {
        if (_main.SessionImageSize is not { } image) return;
        var s = _zoom * doc.Width / image[0];
        for (var k = 0; k < doc.Count; k++)
        {
            if (_hidden[k] || doc.Box(k, image) is not { } box) continue;
            dl.AddRect(origin + new Vector2(box[0], box[1]) * s, origin + new Vector2(box[2], box[3]) * s,
                       ImGui.GetColorU32(MainWindow.PersonColor(k)), 0, ImDrawFlags.None, k == _layer ? 2f : 1f);
        }
    }

    private void HandleShortcuts(MaskDocument doc)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.GetIO().WantTextInput) return;
        var io = ImGui.GetIO();
        if (io.KeyCtrl)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Z)) { if (io.KeyShift) doc.Redo(); else doc.Undo(); }
            if (ImGui.IsKeyPressed(ImGuiKey.Y)) doc.Redo();
            return;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.B)) _tool = Tool.Brush;
        if (ImGui.IsKeyPressed(ImGuiKey.E)) _tool = Tool.Eraser;
        if (ImGui.IsKeyPressed(ImGuiKey.W)) _tool = Tool.Wand;
        if (ImGui.IsKeyPressed(ImGuiKey.H)) _hiddenPlane = !_hiddenPlane;
        if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) _brush = Math.Max(1, _brush / 1.25f);
        if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) _brush = Math.Min(80, _brush * 1.25f);
        for (var k = 0; k < Math.Min(doc.Count, 9); k++)
            if (ImGui.IsKeyPressed(ImGuiKey.Key1 + k)) _layer = k;
    }

    // ------------------------------------------------------------------ 纹理

    /// <summary>纹理在后台建，建好的下一帧换上（换下来的旧纹理再下一帧释放）。</summary>
    private void EnsureTextures(MaskDocument doc)
    {
        if (_photoTask is { IsCompleted: true } photo)
        {
            _photoTask = null;
            if (photo.IsCompletedSuccessfully && _photoDoc == doc) _photo = photo.Result;
            else if (photo.IsCompletedSuccessfully) _retired.Add(photo.Result);
        }
        if (_overlayTask is { IsCompleted: true } overlay)
        {
            _overlayTask = null;
            if (overlay.IsCompletedSuccessfully)
            {
                Retire(ref _overlay);
                _overlay = overlay.Result;
            }
        }
        if (_photo is null && _photoTask is null)
        {
            var rgba = new byte[doc.Width * doc.Height * 4];
            for (int i = 0, j = 0; i < doc.Rgb.Length; i += 3, j += 4)
            {
                rgba[j] = doc.Rgb[i];
                rgba[j + 1] = doc.Rgb[i + 1];
                rgba[j + 2] = doc.Rgb[i + 2];
                rgba[j + 3] = 255;
            }
            _photoDoc = doc;
            _photoTask = _textures.CreateFromRawAsync(RawImageSpecification.Rgba32(doc.Width, doc.Height), rgba, "Posegraph mask photo");
        }
        var state = (doc.Version, doc.Count, _layer, _opacity, string.Concat(_hidden.Select(h => h ? '1' : '0')));
        // 上一张还没建好时不排新的；画的时候最多每 50ms 更新一次叠加层（整张重新上传）
        if (state == _overlayState || _overlayTask is not null
            || (_last is not null && DateTime.UtcNow - _overlayAt < TimeSpan.FromMilliseconds(50))) return;
        var colors = new uint[doc.Count + 1];
        var stripes = new uint[doc.Count + 1];
        for (var k = 0; k < doc.Count; k++)
        {
            if (_hidden[k]) continue;
            var c = MainWindow.PersonColor(k);
            uint Rgba(float alpha) => (uint)(c.X * 255) | (uint)(c.Y * 255) << 8 | (uint)(c.Z * 255) << 16 | (uint)(alpha * 255) << 24;
            colors[k + 1] = Rgba(k == _layer ? _opacity : _opacity * 0.8f);
            stripes[k + 1] = Rgba(Math.Min(1, _opacity + 0.3f));
        }
        var pixels = new byte[doc.Width * doc.Height * 4];
        var visible = doc.Visible;
        var hiddenPlane = doc.Hidden;
        for (var i = 0; i < visible.Length; i++)
        {
            var v = visible[i];
            var h = hiddenPlane[i];
            var color = v > 0 && v <= doc.Count ? colors[v] : 0u;
            // 被挡住的范围：斜线（盖在看得见的颜色上面）
            if (h > 0 && h <= doc.Count && stripes[h] != 0 && ((i % doc.Width) + (i / doc.Width)) % 10 < 3) color = stripes[h];
            if (color != 0) BitConverter.TryWriteBytes(pixels.AsSpan(i * 4, 4), color);
        }
        _overlayTask = _textures.CreateFromRawAsync(RawImageSpecification.Rgba32(doc.Width, doc.Height), pixels, "Posegraph mask overlay");
        _overlayState = state;
        _overlayAt = DateTime.UtcNow;
    }

    private void Retire(ref IDalamudTextureWrap? texture)
    {
        if (texture is not null) _retired.Add(texture);
        texture = null;
    }

    public void Dispose()
    {
        foreach (var t in _retired) t.Dispose();
        _retired.Clear();
        _photo?.Dispose();
        _overlay?.Dispose();
        foreach (var task in new[] { _photoTask, _overlayTask })
            task?.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); });
    }
}
