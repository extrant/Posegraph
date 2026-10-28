using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.DragDrop;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Posegraph.Library;

namespace Posegraph.UI;

/// <summary>
/// 图库窗口：浏览自带图库与用户添加的文件夹，导入（多选、文件夹、拖放、剪切板）、分类（文件夹 + 标签 + 收藏）、
/// 重命名 / 移动 / 删除到回收站，批量识别（结果按内容缓存），存到 Brio 姿势库。
/// 设计参考 Brio 的资料库（来源 + 网格 + 搜索标签），代码独立实现。
/// </summary>
public sealed class LibraryWindow : Window, IDisposable
{
    public const string ExternalDragLabel = "PosegraphImage";
    public const string InternalDragLabel = "PosegraphLibraryItems";

    private static readonly Vector4 Green = PosegraphTheme.Green;
    private static readonly Vector4 Yellow = PosegraphTheme.Yellow;
    private static readonly Vector4 Red = PosegraphTheme.Red;
    private static readonly Vector4 Grey = PosegraphTheme.Grey;
    private static readonly Vector4 Blue = PosegraphTheme.Blue;

    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly ImageLibrary _library;
    private readonly RecognitionService _recognition;
    private readonly ThumbnailCache _thumbs;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, string? First)> _folderInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITextureProvider _textures;
    private readonly ITextureReadbackProvider _readback;
    private readonly IDragDropManager _dragDrop;
    private readonly Action<string> _openInMain;
    private readonly Func<string, Task<string>> _saveToBrio;
    private readonly Func<bool> _backendOnline;
    private readonly AuxViewsMenu _auxMenu;
    private readonly FileDialogManager _fileDialog = new();

    private string _folder;
    private volatile FolderListing? _listing;
    private volatile string[]? _searchResults;
    private Task? _scanTask;
    private string _search = "";
    private bool _favoritesOnly;
    private volatile bool _refresh = true;

    private readonly List<string> _selection = new();
    private string? _anchor;
    private string? _contextPath;   // 右键点的那一项（辅助视角挂在它上面）
    private volatile string[]? _pendingSelection;   // 后台导入完成后要选中的新图片（下一帧在绘制线程里应用）

    // 弹窗
    private List<string> _targets = new();
    private string _input = "";
    private string? _pendingPopup;
    private string[] _moveFolders = [];

    private string _status = "";
    private Vector4 _statusColor = Grey;

    public LibraryWindow(Configuration config, IDalamudPluginInterface pi, ImageLibrary library, RecognitionService recognition,
                         ITextureProvider textures, ITextureReadbackProvider readback, IDragDropManager dragDrop, IPluginLog log,
                         Action<string> openInMain, Func<string, Task<string>> saveToBrio, Func<bool> backendOnline,
                         AuxViewsMenu auxMenu)
        : base(Loc.T("Posegraph 图库") + "###PosegraphLibrary")
    {
        _config = config;
        _pi = pi;
        _library = library;
        _recognition = recognition;
        _textures = textures;
        _readback = readback;
        _dragDrop = dragDrop;
        _thumbs = new ThumbnailCache(textures, log, (int)(256 * ImGuiHelpers.GlobalScale));   // 和最大格子一样大，高分屏也不糊
        _openInMain = openInMain;
        _saveToBrio = saveToBrio;
        _backendOnline = backendOnline;
        _auxMenu = auxMenu;
        _folder = Directory.Exists(config.LibraryFolder) && library.SourceRootOf(config.LibraryFolder, config.LibrarySources) is not null
            ? config.LibraryFolder : library.Root;
        Size = new Vector2(760, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(480, 320), MaximumSize = new Vector2(4000, 4000) };
        recognition.Completed += (_, _) => _refresh = true;
    }

    public void Dispose()
    {
        _thumbs.Dispose();
        _library.Save();
    }

    public override void OnClose() => _library.Save();

    public override void PreDraw() => PosegraphTheme.Push();

    public override void PostDraw() => PosegraphTheme.Pop();

    /// <summary>图库内容在别处变了（主窗口存入了新图），下一帧重新列目录。</summary>
    public void Refresh() => _refresh = true;

    private void SetStatus(string text, Vector4? color = null)
    {
        _status = text;
        _statusColor = color ?? Grey;
    }

    // ------------------------------------------------------------------ 数据

    private void Navigate(string folder)
    {
        if (!Directory.Exists(folder)) return;
        _folder = folder;
        _selection.Clear();
        _anchor = null;
        _search = "";
        _searchResults = null;
        _config.LibraryFolder = folder;
        _pi.SavePluginConfig(_config);
        _refresh = true;
    }

    private void Rescan()
    {
        if (!_refresh || _scanTask is { IsCompleted: false }) return;
        _refresh = false;
        var folder = _folder;
        var query = _search;
        var favorites = _favoritesOnly;
        _scanTask = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(folder)) folder = _library.Root;
                var listing = ImageLibrary.List(folder);
                foreach (var sub in listing.Folders)
                    _folderInfo[sub] = ImageLibrary.FolderSummary(sub);
                _listing = listing;
                _searchResults = query.Trim().Length > 0 || favorites ? _library.Search(folder, query, favorites) : null;
            }
            catch (Exception ex)
            {
                SetStatus(Loc.F("读取文件夹失败：{0}", ex.Message), Red);
            }
        });
    }

    /// <summary>当前网格里的条目：搜索时只有图片，否则先文件夹后图片。</summary>
    private string[] Entries()
    {
        if (_searchResults is { } found) return found;
        if (_listing is not { } l || !string.Equals(l.Path, _folder, StringComparison.OrdinalIgnoreCase)) return [];
        return [.. l.Folders, .. l.Images];
    }

    private string CurrentRoot => _library.SourceRootOf(_folder, _config.LibrarySources) ?? _library.Root;

    // ------------------------------------------------------------------ 绘制

    /// <summary>系统拖动正悬停在图库窗口上（上一帧）：拖动提示据此显示"导入到图库"而不是"导入并识别"。</summary>
    public bool ExternalDragHovered { get; private set; }

    /// <summary>正在从图库拖出的图片（拆分模式的角色列表接收拖放时读取）。</summary>
    public string[] DraggedImages => _selection.Where(File.Exists).ToArray();

    /// <summary>当前文件夹名（拖动提示用）。</summary>
    public string FolderName => Path.GetFileName(_folder.TrimEnd('\\'));

    public override void Draw()
    {
        Rescan();
        if (Interlocked.Exchange(ref _pendingSelection, null) is { Length: > 0 } imported)
        {
            _selection.Clear();
            _selection.AddRange(imported);
        }
        DrawExternalDrop();
        DrawToolbar();
        var footer = ImGui.GetFrameHeightWithSpacing();
        using (var body = ImRaii.Child("##body", new Vector2(0, -footer)))
        {
            if (body)
            {
                using (var left = ImRaii.Child("##sources", new Vector2(170 * ImGuiHelpers.GlobalScale, 0), true))
                    if (left)
                    {
                        DrawSources();
                        ExternalDropZone("##sourcesDrop");
                    }
                ImGui.SameLine();
                using (var grid = ImRaii.Child("##grid", Vector2.Zero, false))
                    if (grid)
                    {
                        DrawGrid();
                        ExternalDropZone("##gridDrop");
                    }
            }
        }
        DrawFooter();
        DrawPopups();
        _fileDialog.Draw();
    }

    private static bool IconButton(string id, FontAwesomeIcon icon, string tip, bool enabled = true)
    {
        bool clicked;
        using (ImRaii.Disabled(!enabled))
            clicked = ImGuiComponents.IconButton(id, icon);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tip);
        return clicked && enabled;
    }

    private void DrawToolbar()
    {
        // 面包屑：来源根目录 → 当前文件夹
        var root = CurrentRoot;
        if (IconButton("##up", FontAwesomeIcon.ArrowUp, Loc.T("上一级"), !string.Equals(_folder.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            Navigate(Path.GetDirectoryName(_folder.TrimEnd('\\'))!);
        ImGui.SameLine();
        var parts = new List<string> { root };
        var rel = Path.GetRelativePath(root, _folder);
        if (rel != ".")
            foreach (var p in rel.Split('\\')) parts.Add(Path.Combine(parts[^1], p));
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) { ImGui.SameLine(0, 2); ImGui.TextDisabled("/"); ImGui.SameLine(0, 2); }
            var label = i == 0 ? SourceName(root) : Path.GetFileName(parts[i]);
            if (ImGui.SmallButton($"{label}##crumb{i}")) Navigate(parts[i]);
            MoveDropTarget(parts[i]);
        }

        ImGui.SameLine();
        var searchWidth = 180 * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - searchWidth - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X));
        ImGui.SetNextItemWidth(searchWidth);
        if (ImGui.InputTextWithHint("##search", Loc.T("搜索文件名，#标签"), ref _search, 128)) _refresh = true;
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Yellow, _favoritesOnly))
            if (IconButton("##fav", FontAwesomeIcon.Star, Loc.T("只看收藏（含子文件夹）")))
            {
                _favoritesOnly = !_favoritesOnly;
                _refresh = true;
            }

        if (IconButton("##importFiles", FontAwesomeIcon.FileImport, Loc.T("导入图片（复制到当前文件夹，可多选）"))) ImportFilesDialog();
        ImGui.SameLine();
        if (IconButton("##importFolder", FontAwesomeIcon.FolderPlus, Loc.F("导入文件夹（按原结构复制其中的图片，最多 {0} 张）", ImageLibrary.MaxImportFiles))) ImportFolderDialog();
        ImGui.SameLine();
        if (IconButton("##clipboard", FontAwesomeIcon.Paste, Loc.T("粘贴剪切板里的图片"), _textures.HasClipboardImage())) PasteClipboard();
        ImGui.SameLine();
        if (IconButton("##newFolder", FontAwesomeIcon.FolderOpen, Loc.T("新建文件夹"))) OpenPopup("newFolder", [], Loc.T("新文件夹"));
        ImGui.SameLine();
        if (IconButton("##refresh", FontAwesomeIcon.Sync, Loc.T("刷新（也会重试加载失败的预览图）")))
        {
            _thumbs.RetryFailed();
            _refresh = true;
        }
        ImGui.SameLine();
        if (IconButton("##explorer", FontAwesomeIcon.ExternalLinkAlt, Loc.T("在资源管理器中打开当前文件夹"))) ShowInExplorer(_folder, false);
        ImGui.SameLine();
        var images = _selection.Where(File.Exists).ToList();
        if (IconButton("##recognize", FontAwesomeIcon.Running, Loc.F("识别选中的 {0} 张（结果会缓存，下次直接用）", images.Count), images.Count > 0))
            Recognize(images);
        if (_recognition.Pending > 0 || _recognition.Busy)
        {
            ImGui.SameLine();
            if (IconButton("##cancelQueue", FontAwesomeIcon.Stop, Loc.T("停止批量识别（正在识别的那张会做完）"))) _recognition.CancelPending();
        }
        ImGui.SameLine();
        var size = _config.LibraryThumbSize;
        ImGui.SetNextItemWidth(Math.Min(140 * ImGuiHelpers.GlobalScale, Math.Max(60, ImGui.GetContentRegionAvail().X)));
        if (ImGui.SliderFloat("##thumb", ref size, 64, 256, Loc.T("缩略图 %.0f")))
            _config.LibraryThumbSize = size;
        if (ImGui.IsItemDeactivatedAfterEdit()) _pi.SavePluginConfig(_config);
    }

    private string SourceName(string root)
        => string.Equals(root, _library.Root, StringComparison.OrdinalIgnoreCase)
            ? Loc.T("Posegraph 图库")
            : _config.LibrarySources.FirstOrDefault(s => string.Equals(s.Path, root, StringComparison.OrdinalIgnoreCase))?.Name ?? Path.GetFileName(root);

    private void DrawSources()
    {
        ImGui.TextDisabled(Loc.T("来源"));
        if (ImGui.Selectable(Loc.T("Posegraph 图库") + "##root", string.Equals(CurrentRoot, _library.Root, StringComparison.OrdinalIgnoreCase)))
            Navigate(_library.Root);
        MoveDropTarget(_library.Root);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(_library.Root);

        LibrarySource? remove = null;
        foreach (var s in _config.LibrarySources)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Grey, !s.Enabled || !Directory.Exists(s.Path)))
                if (ImGui.Selectable($"{s.Name}##{s.Path}", string.Equals(CurrentRoot, s.Path, StringComparison.OrdinalIgnoreCase)) && s.Enabled)
                    Navigate(s.Path);
            MoveDropTarget(s.Path);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Directory.Exists(s.Path) ? s.Path : Loc.F("{0}（文件夹不存在）", s.Path));
            if (ImGui.BeginPopupContextItem($"##src{s.Path}"))
            {
                if (ImGui.MenuItem(s.Enabled ? Loc.T("停用") : Loc.T("启用")))
                {
                    s.Enabled = !s.Enabled;
                    _pi.SavePluginConfig(_config);
                }
                if (ImGui.MenuItem(Loc.T("移除来源（不删除文件）"))) remove = s;
                ImGui.EndPopup();
            }
        }
        if (remove is not null)
        {
            _config.LibrarySources.Remove(remove);
            _pi.SavePluginConfig(_config);
            if (string.Equals(CurrentRoot, remove.Path, StringComparison.OrdinalIgnoreCase)) Navigate(_library.Root);
        }
        if (ImGui.SmallButton(Loc.T("+ 添加文件夹")))
            _fileDialog.OpenFolderDialog(Loc.T("添加文件夹作为图库来源（不复制文件）"), (ok, path) =>
            {
                if (!ok || !Directory.Exists(path)) return;
                if (_config.LibrarySources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;
                _config.LibrarySources.Add(new LibrarySource { Name = Path.GetFileName(path.TrimEnd('\\')), Path = path });
                _pi.SavePluginConfig(_config);
                Navigate(path);
            }, null, true);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("把已有的图片文件夹加进来浏览和识别，文件留在原处"));

        ImGui.Separator();
        ImGui.TextDisabled(Loc.T("标签"));
        foreach (var tag in _library.AllTags().Take(40))
            if (ImGui.Selectable($"#{tag}##tag{tag}", _search == "#" + tag))
            {
                _search = _search == "#" + tag ? "" : "#" + tag;
                _refresh = true;
            }
    }

    // ---------------- 网格

    private void DrawGrid()
    {
        var entries = Entries();
        if (entries.Length == 0)
        {
            ImGui.TextDisabled(_searchResults is not null ? Loc.T("没有符合条件的图片")
                : _listing is null ? Loc.T("读取中…") : Loc.T("这个文件夹是空的：用上方按钮导入图片，或把图片 / 文件夹拖到这里"));
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var tile = _config.LibraryThumbSize * scale;
        var spacing = ImGui.GetStyle().ItemSpacing;
        var labelH = ImGui.GetTextLineHeight();
        var cellW = tile + spacing.X;
        var cellH = tile + labelH + spacing.Y * 2;
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing.X) / cellW));
        var rows = (entries.Length + columns - 1) / columns;

        // 只画可见的行，其余用占位撑开滚动区域（几千张图也不卡）
        var scrollY = ImGui.GetScrollY();
        var viewH = ImGui.GetWindowHeight();
        var firstRow = Math.Max(0, (int)(scrollY / cellH) - 1);
        var lastRow = Math.Min(rows - 1, (int)((scrollY + viewH) / cellH) + 1);
        var startY = ImGui.GetCursorPosY();
        for (var row = firstRow; row <= lastRow; row++)
            for (var col = 0; col < columns; col++)
            {
                var i = row * columns + col;
                if (i >= entries.Length) break;
                ImGui.SetCursorPos(new Vector2(col * cellW, startY + row * cellH));
                DrawTile(entries, i, tile);
            }
        ImGui.SetCursorPos(new Vector2(0, startY + rows * cellH));
        ImGui.Dummy(new Vector2(1, 1));

        // 点空白处取消选择
        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered())
            _selection.Clear();
        // 右键菜单必须和 OpenPopup 在同一个窗口（这个子窗口）里
        if (ImGui.BeginPopup("##libctx"))
        {
            DrawContextMenu();
            ImGui.EndPopup();
        }
    }

    private void DrawTile(string[] entries, int index, float tile)
    {
        var path = entries[index];
        var isFolder = Directory.Exists(path);
        var selected = _selection.Contains(path, StringComparer.OrdinalIgnoreCase);
        var pos = ImGui.GetCursorScreenPos();
        var labelH = ImGui.GetTextLineHeight();
        var dl = ImGui.GetWindowDrawList();

        ImGui.InvisibleButton($"##tile{path}", new Vector2(tile, tile + labelH + 2));
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) Click(entries, path);
        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            if (isFolder) Navigate(path);
            else _openInMain(path);
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _contextPath = path;
            if (!selected)
            {
                _selection.Clear();
                _selection.Add(path);
            }
            ImGui.OpenPopup("##libctx");
        }
        if (ImGui.BeginDragDropSource())
        {
            if (!selected)
            {
                _selection.Clear();
                _selection.Add(path);
            }
            ImGui.SetDragDropPayload(InternalDragLabel, ReadOnlySpan<byte>.Empty, ImGuiCond.None);
            ImGui.TextUnformatted(Loc.F("移动 {0} 项（图片也可以拖到角色列表里绑定）", _selection.Count));
            ImGui.EndDragDropSource();
        }
        if (isFolder) MoveDropTarget(path);

        var max = pos + new Vector2(tile, tile);
        var bg = selected ? ImGui.GetColorU32(ImGuiCol.HeaderActive) : hovered ? ImGui.GetColorU32(ImGuiCol.HeaderHovered) : ImGui.GetColorU32(ImGuiCol.FrameBg);
        dl.AddRectFilled(pos, max, bg, 4);
        if (isFolder) DrawFolder(dl, path, pos, tile);
        else
        {
            var wrap = _thumbs.Get(path, out var failed);
            if (wrap is not null)
            {
                var fit = Math.Min((tile - 6) / wrap.Width, (tile - 6) / wrap.Height);
                var sz = new Vector2(wrap.Width, wrap.Height) * fit;
                var tl = pos + (new Vector2(tile, tile) - sz) / 2;
                dl.AddImage(wrap.Handle, tl, tl + sz);
            }
            else
            {
                // 正文颜色：在悬停 / 选中的背景上也看得见
                var text = failed ? Loc.T("无法预览") : Loc.T("加载中…");
                var sz = ImGui.CalcTextSize(text);
                dl.AddText(pos + (new Vector2(tile, tile) - sz) / 2, ImGui.GetColorU32(failed ? Red : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]), text);
            }
            DrawBadges(dl, path, pos, tile);
        }

        var name = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var label = Ellipsize(name, tile);
        dl.AddText(pos + new Vector2((tile - ImGui.CalcTextSize(label).X) / 2, tile + 1), ImGui.GetColorU32(ImGuiCol.Text), label);
        if (hovered && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGui.SetTooltip(Tooltip(path, isFolder));
    }

    /// <summary>
    /// 文件夹：用矢量图形画（字体图标放大会糊），任何大小都清晰。
    /// 后片（带标签页）→ 文件夹里第一张图（白边照片，裁切铺满、上沿露出来）→ 半透明前片，右上角是图片数量。
    /// </summary>
    private void DrawFolder(ImDrawListPtr dl, string path, Vector2 pos, float tile)
    {
        var margin = tile * 0.1f;
        var w = tile - 2 * margin;
        var h = w * 0.74f;
        var x0 = pos.X + margin;
        var y0 = pos.Y + (tile - h) / 2 + tile * 0.02f;
        var r = Math.Max(2f, tile * 0.045f);
        var tabH = h * 0.13f;
        var back = ImGui.GetColorU32(new Vector4(0.80f, 0.56f, 0.18f, 1));
        var front = ImGui.GetColorU32(new Vector4(0.98f, 0.76f, 0.30f, 0.94f));
        var shadow = ImGui.GetColorU32(new Vector4(0, 0, 0, 0.28f));

        // 阴影、后片与标签页
        dl.AddRectFilled(new Vector2(x0 + 2, y0 + tabH + 4), new Vector2(x0 + w + 2, y0 + h + 4), shadow, r);
        dl.AddRectFilled(new Vector2(x0, y0), new Vector2(x0 + w * 0.42f, y0 + tabH + r * 2), back, r);
        dl.AddRectFilled(new Vector2(x0, y0 + tabH), new Vector2(x0 + w, y0 + h), back, r);

        // 夹在里面的照片（没有图片时是一张白纸）
        _folderInfo.TryGetValue(path, out var info);
        var paperMin = new Vector2(x0 + w * 0.09f, y0 + tabH * 0.55f);
        var paperMax = new Vector2(x0 + w * 0.91f, y0 + h * 0.8f);
        var border = Math.Max(2f, tile * 0.018f);
        dl.AddRectFilled(paperMin, paperMax, ImGui.GetColorU32(new Vector4(0.97f, 0.97f, 0.97f, 1)), r * 0.6f);
        if (info.First is { } first && _thumbs.Get(first, out _) is { } wrap)
        {
            var min = paperMin + new Vector2(border, border);
            var max = paperMax - new Vector2(border, border);
            var (uv0, uv1) = CoverUv(wrap.Width, wrap.Height, max - min);
            dl.AddImageRounded(wrap.Handle, min, max, uv0, uv1, 0xFFFFFFFF, r * 0.4f);
        }

        // 前片：比后片矮一截，挡住照片下半部分
        var frontTop = y0 + h * 0.38f;
        dl.AddRectFilled(new Vector2(x0, frontTop), new Vector2(x0 + w, y0 + h), front, r);
        dl.AddLine(new Vector2(x0 + r, frontTop + 1.5f), new Vector2(x0 + w - r, frontTop + 1.5f),
                   ImGui.GetColorU32(new Vector4(1, 0.93f, 0.7f, 0.8f)), Math.Max(1f, tile * 0.01f));

        if (info.Count > 0)
        {
            var text = info.Count >= 1000 ? "999+" : info.Count.ToString();
            var sz = ImGui.CalcTextSize(text);
            var tr = pos + new Vector2(tile - 5, 5);
            dl.AddRectFilled(tr - new Vector2(sz.X + 8, 0), tr + new Vector2(0, sz.Y + 2), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.55f)), 6);
            dl.AddText(tr - new Vector2(sz.X + 4, -1), ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), text);
        }
    }

    /// <summary>图片按 area 的宽高比居中裁切铺满（不变形）时的 UV。</summary>
    private static (Vector2 Uv0, Vector2 Uv1) CoverUv(float width, float height, Vector2 area)
    {
        var image = width / height;
        var target = area.X / area.Y;
        if (image > target)
        {
            var u = target / image;
            return (new Vector2((1 - u) / 2, 0), new Vector2((1 + u) / 2, 1));
        }
        var v = image / target;
        return (new Vector2(0, (1 - v) / 2), new Vector2(1, (1 + v) / 2));
    }

    private void DrawBadges(ImDrawListPtr dl, string path, Vector2 pos, float tile)
    {
        var meta = _library.Meta(path);
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (meta?.Favorite == true)
                dl.AddText(pos + new Vector2(4, 3), ImGui.GetColorU32(Yellow), FontAwesomeIcon.Star.ToIconString());
            var (icon, color) = StatusIcon(path);
            var right = tile - 4;
            if (icon is { } ic)
            {
                var s = ic.ToIconString();
                right -= ImGui.CalcTextSize(s).X;
                dl.AddText(pos + new Vector2(right, 3), ImGui.GetColorU32(color), s);
                right -= 4;
            }
            // 在 3D 页保存过修正
            var sha = _library.KnownHash(path);
            if (sha is not null && _library.HasCorrection(sha))
            {
                var s = FontAwesomeIcon.SlidersH.ToIconString();
                right -= ImGui.CalcTextSize(s).X;
                dl.AddText(pos + new Vector2(right, 3), ImGui.GetColorU32(Blue), s);
                right -= 4;
            }
            // 挂了辅助视角：图标下面写张数
            if (sha is not null && _library.AuxCount(sha) is var aux and > 0)
            {
                var s = FontAwesomeIcon.Clone.ToIconString();
                right -= ImGui.CalcTextSize(s).X;
                dl.AddText(pos + new Vector2(right, 3), ImGui.GetColorU32(Blue), s);
                var h = ImGui.GetTextLineHeight();
                using (ImRaii.PushFont(UiBuilder.DefaultFont))
                    dl.AddText(pos + new Vector2(right + 2, 3 + h), ImGui.GetColorU32(Blue), aux.ToString());
            }
        }
    }

    /// <summary>右上角的识别状态：已识别（有缓存）/ 排队 / 识别中 / 失败。</summary>
    private (FontAwesomeIcon? Icon, Vector4 Color) StatusIcon(string path)
    {
        var status = _recognition.Status(path);
        if (status?.State == RecognitionState.Running) return (FontAwesomeIcon.Spinner, Yellow);
        if (status?.State == RecognitionState.Queued) return (FontAwesomeIcon.Clock, Grey);
        if (status?.State == RecognitionState.Failed) return (FontAwesomeIcon.ExclamationTriangle, Red);
        return _library.KnownHash(path) is { } sha && _library.HasCache(sha) ? (FontAwesomeIcon.CheckCircle, Green) : (null, Grey);
    }

    private string Tooltip(string path, bool isFolder)
    {
        if (isFolder) return Path.GetFileName(path) + Loc.T("\n双击打开，可以把图片拖进来");
        var text = Path.GetFileName(path);
        var meta = _library.Meta(path);
        if (meta is { Tags.Count: > 0 }) text += "\n#" + string.Join("  #", meta.Tags);
        var status = _recognition.Status(path);
        text += status?.State switch
        {
            RecognitionState.Running => Loc.T("\n识别中…"),
            RecognitionState.Queued => Loc.T("\n排队等待识别"),
            RecognitionState.Failed => Loc.F("\n识别失败：{0}", status.Error ?? ""),
            _ => StatusIcon(path).Icon is not null ? Loc.T("\n已识别（有缓存，打开不用重新识别）") : "",
        };
        if (_library.KnownHash(path) is { } sha && _library.HasCorrection(sha)) text += Loc.T("\n有保存的修正（打开时自动恢复）");
        if (_library.KnownHash(path) is { } h && _library.AuxCount(h) is var aux and > 0)
            text += Loc.F("\n挂了 {0} 张辅助视角图（右键「辅助视角」管理）", aux);
        return text + Loc.T("\n双击打开到主窗口，右键更多操作");
    }

    private static string Ellipsize(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        for (var n = text.Length - 1; n > 0; n--)
        {
            var s = text[..n] + "…";
            if (ImGui.CalcTextSize(s).X <= width) return s;
        }
        return "…";
    }

    private void Click(string[] entries, string path)
    {
        var io = ImGui.GetIO();
        if (io.KeyShift && _anchor is not null)
        {
            var a = Array.FindIndex(entries, e => string.Equals(e, _anchor, StringComparison.OrdinalIgnoreCase));
            var b = Array.IndexOf(entries, path);
            if (a >= 0 && b >= 0)
            {
                if (!io.KeyCtrl) _selection.Clear();
                for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                    if (!_selection.Contains(entries[i], StringComparer.OrdinalIgnoreCase)) _selection.Add(entries[i]);
                return;
            }
        }
        if (io.KeyCtrl)
        {
            // Ctrl：切换这一项的选中状态
            if (_selection.RemoveAll(s => string.Equals(s, path, StringComparison.OrdinalIgnoreCase)) == 0)
                _selection.Add(path);
        }
        else
        {
            _selection.Clear();
            _selection.Add(path);
        }
        _anchor = path;
    }

    /// <summary>把图库里拖着的条目放进 folder。</summary>
    private void MoveDropTarget(string folder)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        var payload = ImGui.AcceptDragDropPayload(InternalDragLabel, ImGuiDragDropFlags.None);
        if (!payload.IsNull) MoveTo(_selection.ToList(), folder);
        ImGui.EndDragDropTarget();
    }

    /// <summary>
    /// 从系统拖进来的图片 / 文件夹：复制到当前文件夹（不识别）。整个窗口都能接收：
    /// 在最前面放一个铺满窗口的隐形按钮作拖放目标，之后的控件照常画在它上面。
    /// </summary>
    /// <summary>
    /// 子区域（文件夹树、图片网格）里也放一个接收系统拖放的隐形按钮，盖住整个子区域：
    /// 子窗口挡在主窗口的拖放目标上面，只在主窗口放的话拖到图片网格上松手没反应。只在拖动时才画，平时不挡点击。
    /// </summary>
    private void ExternalDropZone(string id)
    {
        if (!_dragDrop.IsDragging) return;
        var start = ImGui.GetCursorPos();
        ImGui.SetCursorScreenPos(ImGui.GetWindowPos());
        ImGui.InvisibleButton(id, Vector2.Max(ImGui.GetWindowSize(), new Vector2(1, 1)));
        if (_dragDrop.CreateImGuiTarget(ExternalDragLabel, out var files, out var dirs))
            Import([.. files, .. dirs]);
        ImGui.SetCursorPos(start);
    }

    private void DrawExternalDrop()
    {
        ExternalDragHovered = false;
        if (!_dragDrop.IsDragging) return;
        ExternalDragHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var start = ImGui.GetCursorPos();
        ImGui.InvisibleButton("##externalDrop", Vector2.Max(ImGui.GetContentRegionAvail(), new Vector2(1, 1)));
        if (_dragDrop.CreateImGuiTarget(ExternalDragLabel, out var files, out var dirs))
            Import([.. files, .. dirs]);
        ImGui.SetCursorPos(start);
    }

    // ---------------- 底栏

    private void DrawFooter()
    {
        if (_recognition.TakeNotice() is { } notice) SetStatus(notice, Yellow);   // 批量并行识别（实验）的提示
        var text = _selection.Count > 0 ? Loc.F("已选 {0} 项", _selection.Count) : Loc.F("{0} 项", Entries().Length);
        if (_recognition.Pending > 0 || _recognition.Busy)
            text += Loc.F("　批量识别：剩 {0} 张", _recognition.Pending + (_recognition.Busy ? 1 : 0));
        ImGui.TextDisabled(text);
        if (_status.Length > 0)
        {
            ImGui.SameLine();
            UiText.Fit(_status, _statusColor);
        }
    }

    private void DrawContextMenu()
    {
        var items = _selection.ToList();
        var images = items.Where(File.Exists).ToList();
        var single = items.Count == 1 ? items[0] : null;

        if (single is not null && File.Exists(single) && ImGui.MenuItem(Loc.T("打开到主窗口"))) _openInMain(single);
        if (single is not null && Directory.Exists(single) && ImGui.MenuItem(Loc.T("打开文件夹"))) Navigate(single);
        if (images.Count > 0 && ImGui.MenuItem(Loc.F("识别（{0} 张）", images.Count))) Recognize(images);
        // 辅助视角挂在右键点的那张图上；同时选中的其他图可以移过来
        var main = _contextPath is not null && File.Exists(_contextPath) ? _contextPath : single is not null && File.Exists(single) ? single : null;
        if (main is not null)
        {
            var count = _library.KnownHash(main) is { } mainSha ? _library.AuxCount(mainSha) : 0;
            if (ImGui.BeginMenu(count > 0 ? Loc.F("辅助视角（{0}）", count) : Loc.T("辅助视角")))
            {
                _auxMenu.Draw(main, images, (text, color) =>
                {
                    SetStatus(text, color);
                    _refresh = true;
                });
                ImGui.EndMenu();
            }
        }
        ImGui.Separator();
        if (images.Count > 0)
        {
            var allFav = images.All(p => _library.Meta(p)?.Favorite == true);
            if (ImGui.MenuItem(allFav ? Loc.T("取消收藏") : Loc.T("收藏")))
            {
                foreach (var p in images) _library.SetFavorite(p, !allFav);
                _refresh = _favoritesOnly;
            }
            if (ImGui.MenuItem(Loc.T("编辑标签…")))
                OpenPopup("tags", images, string.Join(", ", images.Count == 1 ? _library.Meta(images[0])?.Tags ?? [] : CommonTags(images)));
        }
        if (single is not null && ImGui.MenuItem(Loc.T("重命名…")))
            OpenPopup("rename", [single], Directory.Exists(single) ? Path.GetFileName(single) : Path.GetFileNameWithoutExtension(single));
        if (items.Count > 0 && ImGui.MenuItem(Loc.T("移动到…")))
        {
            _moveFolders = FoldersUnder(CurrentRoot);
            OpenPopup("move", items, "");
        }
        if (single is not null && ImGui.MenuItem(Loc.T("在资源管理器中显示"))) ShowInExplorer(single, true);
        ImGui.Separator();
        if (images.Count > 0 && ImGui.MenuItem(Loc.F("存到 Brio 姿势库（{0} 张）", images.Count))) SaveToBrio(images);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.F("用每张图保存的修正（没有就用默认值）和当前种族计算姿势，保存到 {0}，在 Brio 资料库里直接能用。没识别过的会先识别", BrioPoseLibrary.Folder));
        ImGui.Separator();
        if (items.Count > 0 && ImGui.MenuItem(Loc.F("删除到回收站（{0} 项）", items.Count))) OpenPopup("delete", items, "");
    }

    private string[] CommonTags(List<string> images)
        => images.Select(p => (IEnumerable<string>)(_library.Meta(p)?.Tags ?? []))
                 .Aggregate((a, b) => a.Intersect(b)).ToArray();

    /// <summary>来源下的所有文件夹（"移动到"的候选，最多 300 个）。</summary>
    private static string[] FoldersUnder(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        try
        {
            return [root, .. Directory.EnumerateDirectories(root, "*", options)
                                      .Where(d => !d.Split('\\').Any(p => p.StartsWith('.')))
                                      .Take(300).OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch
        {
            return [root];
        }
    }

    // ---------------- 弹窗

    private void OpenPopup(string kind, List<string> targets, string input)
    {
        _targets = targets;
        _input = input;
        _pendingPopup = kind;
    }

    private void DrawPopups()
    {
        if (_pendingPopup is { } open)
        {
            ImGui.OpenPopup($"##{open}");
            _pendingPopup = null;
        }
        TextPopup("newFolder", Loc.T("新建文件夹"), Loc.T("名称"), name =>
        {
            Navigate(_library.NewFolder(_folder, name));
            _selection.Clear();
        });
        TextPopup("rename", Loc.T("重命名"), Loc.T("新名称"), name =>
        {
            var from = _targets[0];
            var to = _library.Rename(from, name);
            _thumbs.Forget(from);
            _recognition.Rename(from, to);
            _selection.Clear();
            _selection.Add(to);
            _refresh = true;
        });
        TextPopup("tags", Loc.T("编辑标签"), Loc.T("标签（用逗号分隔）"), text =>
        {
            var tags = text.Split(',', '，').Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).ToList();
            // 多选时：保留各自独有的标签，共同标签按输入替换
            var common = _targets.Count == 1 ? [] : CommonTags(_targets);
            foreach (var p in _targets)
                _library.SetTags(p, (_library.Meta(p)?.Tags ?? []).Where(t => _targets.Count > 1 && !common.Contains(t)).Concat(tags));
            _refresh = true;
        }, DrawTagSuggestions);

        if (ImGui.BeginPopupModal("##move", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(Loc.F("把 {0} 项移动到：", _targets.Count));
            using (var list = ImRaii.Child("##folders", new Vector2(320 * ImGuiHelpers.GlobalScale, 240 * ImGuiHelpers.GlobalScale), true))
                if (list)
                    foreach (var f in _moveFolders)
                    {
                        var rel = Path.GetRelativePath(CurrentRoot, f);
                        if (ImGui.Selectable(rel == "." ? SourceName(CurrentRoot) : rel))
                        {
                            MoveTo(_targets, f);
                            ImGui.CloseCurrentPopup();
                        }
                    }
            if (ImGui.Button(Loc.T("取消"))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("##delete", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(Loc.F("把 {0} 项删除到回收站？（可以从回收站找回）", _targets.Count));
            foreach (var t in _targets.Take(8)) ImGui.BulletText(Path.GetFileName(t));
            if (_targets.Count > 8) ImGui.TextDisabled("…");
            if (ImGui.Button(Loc.T("删除")))
            {
                try
                {
                    _library.Delete(_targets);
                    foreach (var t in _targets) _thumbs.Forget(t);
                    SetStatus(Loc.F("已删除 {0} 项（在回收站）", _targets.Count), Green);
                }
                catch (Exception ex)
                {
                    SetStatus(ex.Message, Red);
                }
                _selection.Clear();
                _refresh = true;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("取消"))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void TextPopup(string id, string title, string hint, Action<string> confirm, Action? extra = null)
    {
        if (!ImGui.BeginPopupModal($"##{id}", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted(title);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        var enter = ImGui.InputTextWithHint("##input", hint, ref _input, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        extra?.Invoke();
        if (ImGui.Button(Loc.T("确定")) || enter)
        {
            try
            {
                confirm(_input.Trim());
                ImGui.CloseCurrentPopup();
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, Red);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("取消"))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawTagSuggestions()
    {
        var current = _input.Split(',', '，').Select(t => t.Trim()).ToHashSet();
        var shown = 0;
        foreach (var tag in _library.AllTags().Where(t => !current.Contains(t)).Take(20))
        {
            if (shown++ > 0) ImGui.SameLine();
            if (ImGui.GetCursorPosX() + ImGui.CalcTextSize(tag).X > 320 * ImGuiHelpers.GlobalScale) ImGui.NewLine();
            if (ImGui.SmallButton($"{tag}##sug")) _input = string.Join(", ", current.Where(t => t.Length > 0).Append(tag));
        }
    }

    // ---------------- 操作

    private void MoveTo(List<string> items, string folder)
    {
        var errors = _library.Move(items, folder);
        foreach (var item in items)
        {
            _thumbs.Forget(item);
            _recognition.Rename(item, Path.Combine(folder, Path.GetFileName(item)));
        }
        SetStatus(errors.Count == 0 ? Loc.F("已移动 {0} 项", items.Count) : string.Join("；", errors), errors.Count == 0 ? Green : Red);
        _selection.Clear();
        _refresh = true;
    }

    private void Recognize(List<string> images)
    {
        if (!_backendOnline())
        {
            SetStatus(Loc.T("后端离线：先在主窗口启动后端"), Red);
            return;
        }
        _recognition.Enqueue(images);
        SetStatus(Loc.F("已加入识别队列：{0} 张", images.Count), Yellow);
    }

    public void Import(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        var folder = _folder;
        var root = CurrentRoot;
        SetStatus(Loc.T("导入中（查重）…"), Yellow);
        _ = Task.Run(() =>
        {
            try
            {
                var r = _library.Import(paths, folder, root);
                var attached = _library.AttachNumbered(r.Added);
                var text = Loc.F("已导入 {0} 张", r.Added.Count - attached);
                if (attached > 0) text += Loc.F("，{0} 张按文件名（主图名-1）挂为辅助视角", attached);
                if (r.Duplicates.Count > 0) text += Loc.F("，{0} 张图库里已有（已跳过，已选中原图）", r.Duplicates.Count);
                if (r.OverLimit > 0) text += Loc.F("，超过上限跳过 {0} 张", r.OverLimit);
                SetStatus(text, r.Duplicates.Count > 0 || r.OverLimit > 0 ? Yellow : Green);
                _pendingSelection = r.Selection.Where(p => string.Equals(Path.GetDirectoryName(p), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToArray();
                _refresh = true;
            }
            catch (Exception ex)
            {
                SetStatus(Loc.F("导入失败：{0}", ex.Message), Red);
            }
        });
    }

    private void ImportFilesDialog()
    {
        var dir = Directory.Exists(_config.LastImageDir) ? _config.LastImageDir : null;
        _fileDialog.OpenFileDialog(Loc.T("导入图片"), Loc.T("图片") + "{.jpg,.jpeg,.png,.webp,.bmp,.gif,.tif,.tiff,.avif}", (ok, paths) =>
        {
            if (!ok || paths.Count == 0) return;
            _config.LastImageDir = Path.GetDirectoryName(paths[0]) ?? "";
            _pi.SavePluginConfig(_config);
            Import(paths);
        }, 0, dir, true);
    }

    private void ImportFolderDialog()
        => _fileDialog.OpenFolderDialog(Loc.T("导入文件夹"), (ok, path) =>
        {
            if (ok && Directory.Exists(path)) Import([path]);
        }, Directory.Exists(_config.LastImageDir) ? _config.LastImageDir : null, true);

    private void PasteClipboard()
    {
        var folder = _folder;
        var root = CurrentRoot;
        _ = Task.Run(async () =>
        {
            try
            {
                var png = await ClipboardImage.ReadPngAsync(_textures, _readback);
                if (png is null)
                {
                    SetStatus(Loc.T("剪切板里没有图片"), Yellow);
                    return;
                }
                var path = _library.SaveClipboard(png, folder, root, out var duplicate);
                SetStatus(duplicate ? Loc.F("图库里已有这张图：{0}", Path.GetRelativePath(root, path)) : Loc.F("已保存 {0}", Path.GetFileName(path)),
                          duplicate ? Yellow : Green);
                _pendingSelection = [path];
                _refresh = true;
            }
            catch (Exception ex)
            {
                SetStatus(Loc.F("读取剪切板失败：{0}", ex.Message), Red);
            }
        });
    }

    private void SaveToBrio(List<string> images)
    {
        _ = Task.Run(async () =>
        {
            var saved = 0;
            await using var batch = _recognition.Batch();   // 整批只加载一次模型
            foreach (var image in images)
            {
                try
                {
                    SetStatus(Loc.F("正在存到 Brio 姿势库：{0}", Path.GetFileName(image)), Yellow);
                    await _saveToBrio(image);
                    saved++;
                }
                catch (Exception ex)
                {
                    SetStatus(Loc.F("{0} 保存失败：{1}", Path.GetFileName(image), ex.Message), Red);
                    return;
                }
            }
            SetStatus(Loc.F("已存到 Brio 姿势库 {0} 个（{1}）", saved, BrioPoseLibrary.Folder), Green);
            _refresh = true;
        });
    }

    private static void ShowInExplorer(string path, bool select)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"");
        }
        catch
        {
            // 打不开资源管理器不影响使用
        }
    }
}
