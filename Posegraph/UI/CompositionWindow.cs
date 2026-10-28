using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Posegraph.Backend;

namespace Posegraph.UI;

/// <summary>
/// 构图与截图：在游戏画面上画出构图框（照片比例或常用比例，横竖可切），框外压暗，可叠参考线；
/// 显示 / 设置相当于全画幅相机的等效焦距；一键截图只截游戏渲染的画面（不含插件界面），按构图框裁好存成 PNG。
/// 构图框和复刻镜头的约定一致：比游戏画面窄的照片上下对齐，更宽的左右对齐（见后端 camera.game_fov_y）。
/// </summary>
public sealed class CompositionWindow : Window
{
    private static readonly Vector4 Green = PosegraphTheme.Green;
    private static readonly Vector4 Yellow = PosegraphTheme.Yellow;
    private static readonly Vector4 Grey = PosegraphTheme.Grey;

    /// <summary>常用比例（宽:高，横构图写法）。"photo" 为当前照片。</summary>
    private static readonly (string Key, string Label, float W, float H)[] Ratios =
    [
        ("photo", "照片", 0, 0), ("3:2", "3:2", 3, 2), ("4:3", "4:3", 4, 3), ("16:9", "16:9", 16, 9),
        ("5:4", "5:4", 5, 4), ("1:1", "1:1", 1, 1), ("21:9", "21:9", 21, 9), ("2.39:1", "2.39:1", 2.39f, 1),
    ];

    private static readonly string[] Guides = ["无", "三分线", "中心十字", "黄金分割线"];

    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly ITextureProvider _textures;
    private readonly ITextureReadbackProvider _readback;
    private readonly Func<int[]?> _photoSize;
    private readonly Func<IDalamudTextureWrap?> _photoTexture;
    private readonly Func<Segment[]?> _photoSkeleton;
    private string _status = "";
    private Vector4 _statusColor = Grey;
    private bool _shooting;
    private float? _fovOffset;   // 拖焦距滑条期间固定「垂直视角 - Zoom」，避免投影矩阵晚一帧更新造成来回跳

    public CompositionWindow(Configuration config, IDalamudPluginInterface pi, IFramework framework, IClientState clientState,
                             ITextureProvider textures, ITextureReadbackProvider readback, Func<int[]?> photoSize,
                             Func<IDalamudTextureWrap?> photoTexture, Func<Segment[]?> photoSkeleton)
        : base(Loc.T("Posegraph 构图与截图") + "###PosegraphComposition")
    {
        _config = config;
        _pi = pi;
        _framework = framework;
        _clientState = clientState;
        _textures = textures;
        _readback = readback;
        _photoSize = photoSize;
        _photoTexture = photoTexture;
        _photoSkeleton = photoSkeleton;
        Size = new Vector2(400, 0);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.AlwaysAutoResize;
    }

    /// <summary>默认截图文件夹：文档\Posegraph\Screenshots（和图库 文档\Posegraph\Images 放在一起）。</summary>
    public static string DefaultShotDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Posegraph", "Screenshots");

    private string ShotDir => string.IsNullOrWhiteSpace(_config.ScreenshotDir) ? DefaultShotDir : _config.ScreenshotDir;

    public override void PreDraw() => PosegraphTheme.Push();

    public override void PostDraw() => PosegraphTheme.Pop();

    public override void Draw()
    {
        var changed = false;
        var show = _config.FrameShow;
        if (ImGui.Checkbox(Loc.T("显示构图框"), ref show))
        {
            _config.FrameShow = show;
            changed = true;
        }
        Tip(Loc.T("在游戏画面上画出构图框，截图按框裁切。复刻镜头时照片就落在这个框里"));
        ImGui.SameLine();
        using (ImRaii.Disabled(_photoSize() is null))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Image, Loc.T("适配原图")))
            {
                _config.FrameAspect = "photo";
                _config.FrameShow = true;
                changed = true;
            }
        Tip(_photoSize() is null ? Loc.T("主窗口还没有照片") : Loc.T("构图框改成原图的比例和朝向（和复刻镜头后照片在画面里的位置一致）"));

        ImGui.TextUnformatted(Loc.T("比例"));
        var i = 0;
        foreach (var (key, label, _, _) in Ratios)
        {
            if (i++ % 4 != 0) ImGui.SameLine();
            using var active = ImRaii.PushColor(ImGuiCol.Button, PosegraphTheme.Accent with { W = 0.55f }, _config.FrameAspect == key);
            using var disabled = ImRaii.Disabled(key == "photo" && _photoSize() is null);
            if (ImGui.Button(Loc.T(label) + "##ratio" + key, new Vector2(80 * ImGuiHelpers.GlobalScale, 0)))
            {
                _config.FrameAspect = key;
                changed = true;
            }
            if (key == "photo") Tip(_photoSize() is { } s ? Loc.F("当前照片 {0}×{1}", s[0], s[1]) : Loc.T("主窗口还没有照片"));
        }
        using (ImRaii.Disabled(_config.FrameAspect == "photo"))
        {
            var portrait = _config.FramePortrait;
            if (ImGui.RadioButton(Loc.T("横构图"), !portrait)) { _config.FramePortrait = false; changed = true; }
            ImGui.SameLine();
            if (ImGui.RadioButton(Loc.T("竖构图"), portrait)) { _config.FramePortrait = true; changed = true; }
            if (_config.FrameAspect == "photo")
            {
                ImGui.SameLine();
                ImGui.TextDisabled(Loc.T("（照片比例按照片本身）"));
            }
        }

        var dim = _config.FrameDim;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat(Loc.T("框外压暗"), ref dim, 0f, 0.95f, "%.2f"))
        {
            _config.FrameDim = dim;
            changed = true;
        }
        var guide = Math.Clamp(_config.FrameGuides, 0, Guides.Length - 1);
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo(Loc.T("参考线"), ref guide, Guides.Select(Loc.T).ToArray(), Guides.Length))
        {
            _config.FrameGuides = guide;
            changed = true;
        }

        changed |= DrawPhotoReference();
        ImGui.Separator();
        DrawFocal();
        ImGui.Separator();
        DrawShot();
        if (_status.Length > 0) ImGui.TextColored(_statusColor, _status);
        if (changed) _pi.SavePluginConfig(_config);
    }

    /// <summary>原图参考：叠原图、画原图骨架，对着照片摆人和构图。</summary>
    private bool DrawPhotoReference()
    {
        var changed = false;
        using var disabled = ImRaii.Disabled(_photoSize() is null);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("原图参考"));
        Tip(Loc.T("把原图叠在游戏画面上对着摆：位置和复刻镜头后照片所在的区域一致。只画在界面上，截图里没有"));
        ImGui.SameLine();
        var alpha = _config.FramePhotoAlpha;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##photoAlpha", ref alpha, 0f, 0.8f, alpha <= 0 ? Loc.T("不叠原图") : Loc.T("原图 %.2f")))
        {
            _config.FramePhotoAlpha = alpha;
            changed = true;
        }
        Tip(Loc.T("原图的不透明度，0 为不叠。右键关掉"));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _config.FramePhotoAlpha = 0;
            changed = true;
        }
        ImGui.SameLine();
        var skeleton = _config.FramePhotoSkeleton;
        if (ImGui.Checkbox(Loc.T("原图骨架"), ref skeleton))
        {
            _config.FramePhotoSkeleton = skeleton;
            changed = true;
        }
        Tip(Loc.T("画出识别到的原图人物骨架（绿色），对着它调角色和镜头"));
        return changed;
    }

    /// <summary>等效焦距：构图框长边对应全画幅 36mm 时的焦距（和真相机的取景范围一致）。</summary>
    private void DrawFocal()
    {
        if (!_clientState.IsGPosing || GposeCamera.Projection() is not { } proj)
        {
            ImGui.TextDisabled(Loc.T("进入 GPose 后显示 / 设置等效焦距"));
            return;
        }
        var vp = ImGui.GetMainViewport();
        var frame = FrameRect(vp.Size);
        var longRatio = Math.Max(frame.Size.X, frame.Size.Y) / vp.Size.Y;   // 构图框长边 / 游戏画面高度
        var tanHalfV = MathF.Tan(proj.FovY / 2);
        var focal = 18f / (tanHalfV * longRatio);
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var edited = ImGui.SliderFloat(Loc.T("等效焦距"), ref focal, 10f, 300f, "%.0f mm", ImGuiSliderFlags.Logarithmic);
        if (ImGui.IsItemActivated()) _fovOffset = proj.FovY - proj.Zoom;
        if (ImGui.IsItemDeactivated()) _fovOffset = null;
        Tip(Loc.T("相当于全画幅相机（36×24mm）拍这个构图框用的焦距：24 广角、35 / 50 人文、85 人像、135 以上长焦压缩。\n按构图框的长边换算，横竖构图都准；拖动直接改 GPose 视角（游戏下限约 12.6°）"));
        if (edited && focal > 1f)
        {
            var fovY = 2 * MathF.Atan(18f / focal / longRatio);
            var offset = _fovOffset ?? proj.FovY - proj.Zoom;
            _framework.RunOnFrameworkThread(() => GposeCamera.SetZoom(fovY - offset));
        }
        ImGui.SameLine();
        ImGui.TextDisabled(Loc.F("垂直视角 {0:0.0}°", proj.FovY * 180f / MathF.PI));
    }

    private void DrawShot()
    {
        using (ImRaii.Disabled(_shooting || !_clientState.IsGPosing))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Camera, Loc.T("截图")))
                Shoot();
        Tip(Loc.T("只截游戏渲染的画面（不含插件和 Dalamud 的窗口），PNG 无损；显示构图框时按框裁切。\n游戏自己的界面要先隐藏（GPose 里的隐藏界面）"));
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderOpen, Loc.T("打开文件夹")))
        {
            Directory.CreateDirectory(ShotDir);
            Dalamud.Utility.Util.OpenLink(ShotDir);
        }
        var dir = _config.ScreenshotDir;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##shotDir", Loc.F("截图文件夹（留空：{0}）", DefaultShotDir), ref dir, 260))
        {
            _config.ScreenshotDir = dir;
            _pi.SavePluginConfig(_config);
        }
    }

    /// <summary>截游戏画面（ImGui 绘制之前的那一帧），按构图框裁切后存 PNG。必须在绘制线程调用（读 ImGui 视口）。</summary>
    public void Shoot()
    {
        var vp = ImGui.GetMainViewport();
        var args = new ImGuiViewportTextureArgs { ViewportId = vp.ID, TakeBeforeImGuiRender = true, KeepTransparency = false };
        if (_config.FrameShow)
        {
            var frame = FrameRect(vp.Size);
            args.Uv0 = frame.Min / vp.Size;
            args.Uv1 = (frame.Min + frame.Size) / vp.Size;
        }
        var path = Path.Combine(ShotDir, $"Posegraph_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        _shooting = true;
        SetStatus(Loc.T("截图中…"), Yellow);
        Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(ShotDir);
                using var texture = await _textures.CreateFromImGuiViewportAsync(args, "Posegraph screenshot");
                var encoder = _readback.GetSupportedImageEncoderInfos()
                    .FirstOrDefault(c => c.MimeTypes.Contains("image/png", StringComparer.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(Loc.T("未找到 PNG 图像编码器"));
                await _readback.SaveToFileAsync(texture, encoder.ContainerGuid, path, null, true);
                SetStatus(Loc.F("已保存 {0}（{1}×{2}）", Path.GetFileName(path), texture.Width, texture.Height), Green);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.F("截图失败：{0}", ex.Message), Yellow);
            }
            finally { _shooting = false; }
        });
    }

    /// <summary>构图框的宽高比（宽 / 高）；"照片" 但主窗口没有照片时按 3:2。</summary>
    public float Aspect()
    {
        if (_config.FrameAspect == "photo")
            return _photoSize() is [var w, var h, ..] && w > 0 && h > 0 ? (float)w / h : 1.5f;
        var r = Ratios.FirstOrDefault(r => r.Key == _config.FrameAspect);
        var a = r.W > 0 ? r.W / r.H : 1.5f;
        return _config.FramePortrait ? 1f / a : a;
    }

    /// <summary>构图框在游戏画面里的位置（相对视口左上角）：放得下的最大尺寸、居中。</summary>
    private (Vector2 Min, Vector2 Size) FrameRect(Vector2 viewport) => FitRect(viewport, Aspect());

    private static (Vector2 Min, Vector2 Size) FitRect(Vector2 viewport, float aspect)
    {
        var size = aspect < viewport.X / viewport.Y ? new Vector2(viewport.Y * aspect, viewport.Y) : new Vector2(viewport.X, viewport.X / aspect);
        return ((viewport - size) / 2, size);
    }

    /// <summary>每帧画构图框（背景层，在所有窗口下面、游戏画面上面；截图时不会被截进去）。</summary>
    public void DrawOverlay()
    {
        if (!_clientState.IsGPosing) return;
        DrawPhotoOverlay();
        if (!_config.FrameShow) return;
        var vp = ImGui.GetMainViewport();
        var (min, size) = FrameRect(vp.Size);
        min += vp.Pos;
        var max = min + size;
        var dl = ImGui.GetBackgroundDrawList(vp);
        var shade = ImGui.GetColorU32(new Vector4(0, 0, 0, _config.FrameDim));
        var vMin = vp.Pos;
        var vMax = vp.Pos + vp.Size;
        if (_config.FrameDim > 0)
        {
            dl.AddRectFilled(vMin, new Vector2(vMax.X, min.Y), shade);
            dl.AddRectFilled(new Vector2(vMin.X, max.Y), vMax, shade);
            dl.AddRectFilled(new Vector2(vMin.X, min.Y), new Vector2(min.X, max.Y), shade);
            dl.AddRectFilled(new Vector2(max.X, min.Y), new Vector2(vMax.X, max.Y), shade);
        }
        var line = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.85f));
        var faint = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.35f));
        dl.AddRect(min, max, line, 0, ImDrawFlags.None, 1.5f);
        switch (_config.FrameGuides)
        {
            case 1:
                for (var k = 1; k <= 2; k++)
                {
                    var x = min.X + size.X * k / 3;
                    var y = min.Y + size.Y * k / 3;
                    dl.AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), faint);
                    dl.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), faint);
                }
                break;
            case 2:
                var c = min + size / 2;
                var arm = Math.Min(size.X, size.Y) * 0.04f;
                dl.AddLine(c - new Vector2(arm, 0), c + new Vector2(arm, 0), line);
                dl.AddLine(c - new Vector2(0, arm), c + new Vector2(0, arm), line);
                break;
            case 3:   // 黄金分割线（0.382 / 0.618）
                foreach (var t in new[] { 0.382f, 0.618f })
                {
                    dl.AddLine(new Vector2(min.X + size.X * t, min.Y), new Vector2(min.X + size.X * t, max.Y), faint);
                    dl.AddLine(new Vector2(min.X, min.Y + size.Y * t), new Vector2(max.X, min.Y + size.Y * t), faint);
                }
                break;
        }
    }

    /// <summary>原图参考：原图所在区域（和复刻镜头一致：放进游戏画面的最大尺寸、居中）里叠原图 / 骨架；
    /// 构图框不是原图比例时再用虚线标出原图的边。</summary>
    private void DrawPhotoOverlay()
    {
        if (_photoSize() is not [var w, var h, ..] || w <= 0 || h <= 0) return;
        var texture = _photoTexture();
        var bones = _config.FramePhotoSkeleton ? _photoSkeleton() : null;
        var outline = _config.FrameShow && _config.FrameAspect != "photo";
        if ((_config.FramePhotoAlpha <= 0 || texture is null) && bones is null && !outline) return;
        var vp = ImGui.GetMainViewport();
        var (min, size) = FitRect(vp.Size, (float)w / h);
        min += vp.Pos;
        var max = min + size;
        var dl = ImGui.GetBackgroundDrawList(vp);
        if (_config.FramePhotoAlpha > 0 && texture is not null)
            dl.AddImage(texture.Handle, min, max, Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, _config.FramePhotoAlpha)));
        var green = ImGui.GetColorU32(new Vector4(0.35f, 0.95f, 0.45f, 0.9f));
        if (bones is not null)
        {
            var scale = size.X / w;
            foreach (var s in bones)
                dl.AddLine(min + new Vector2(s.A[0], s.A[1]) * scale, min + new Vector2(s.B[0], s.B[1]) * scale, green, 2.5f);
        }
        if (!outline) return;
        const float dash = 10f;
        for (var x = min.X; x < max.X; x += dash * 2)
        {
            dl.AddLine(new Vector2(x, min.Y), new Vector2(Math.Min(x + dash, max.X), min.Y), green, 1.5f);
            dl.AddLine(new Vector2(x, max.Y), new Vector2(Math.Min(x + dash, max.X), max.Y), green, 1.5f);
        }
        for (var y = min.Y; y < max.Y; y += dash * 2)
        {
            dl.AddLine(new Vector2(min.X, y), new Vector2(min.X, Math.Min(y + dash, max.Y)), green, 1.5f);
            dl.AddLine(new Vector2(max.X, y), new Vector2(max.X, Math.Min(y + dash, max.Y)), green, 1.5f);
        }
        dl.AddText(min + new Vector2(6, 4), green, Loc.T("原图"));
    }

    private void SetStatus(string text, Vector4 color) => (_status, _statusColor) = (text, color);

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }
}
