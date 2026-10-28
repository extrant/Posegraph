using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using HarmonyLib;
using SkiaSharp;

namespace UiPreview;

/// <summary>
/// 离线的 ImGui 宿主：建上下文、装字体（与 Dalamud 相同的 Noto Sans CJK 与 FontAwesome）、套 Dalamud 默认样式，
/// 按帧调用窗口的 Draw，最后渲染成图片。Dalamud 里依赖游戏的静态成员（字体、界面缩放）用 Harmony 换成这里的。
/// </summary>
public sealed unsafe class Host : IDisposable
{
    public static Host Current { get; private set; } = null!;

    public SkiaRenderer Renderer { get; } = new();
    public ImFontPtr DefaultFont { get; private set; }
    public ImFontPtr IconFont { get; private set; }
    public ImFontPtr MonoFont { get; private set; }
    public float Scale { get; }
    public Vector2 DisplaySize { get; }

    // 输入：场景脚本设定鼠标位置与按键，下一帧生效
    public Vector2 MousePos { get; set; } = new(-1000, -1000);
    public bool[] MouseDown { get; } = new bool[5];
    public float MouseWheel { get; set; }

    private readonly List<nint> _allocations = new();
    private readonly HashSet<Window> _opened = new();

    public Host(Vector2 displaySize, float scale = 1f)
    {
        Current = this;
        Scale = scale;
        DisplaySize = displaySize;
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.DisplaySize = displaySize;
        io.DeltaTime = 1 / 60f;
        io.IniFilename = null;
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        Console.Error.WriteLine("[host] 字体");
        LoadFonts(scale);
        Console.Error.WriteLine("[host] Harmony");
        new Harmony("posegraph.uipreview").PatchAll(typeof(Host).Assembly);
        Console.Error.WriteLine("[host] 样式");
        Dalamud.Interface.Style.StyleModelV1.DalamudStandard.Apply();
        Console.Error.WriteLine("[host] 完成");
        ImGui.GetStyle().ScaleAllSizes(scale);
    }

    private ushort* Ranges(params ushort[] ranges)
    {
        var p = Marshal.AllocHGlobal(ranges.Length * 2);
        _allocations.Add(p);
        for (var i = 0; i < ranges.Length; i++) ((ushort*)p)[i] = ranges[i];
        return (ushort*)p;
    }

    private void LoadFonts(float scale)
    {
        var assets = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"XIVLauncherCN\dalamudAssets\dev\UIRes");
        var fonts = ImGui.GetIO().Fonts;
        // Dalamud 默认 12pt（16px）。不能读 UiBuilder.DefaultFontSizePx：它取游戏里的服务配置，离线时会一直等
        var size = 16f * scale;
        DefaultFont = fonts.AddFontFromFileTTF(Path.Combine(assets, "NotoSansCJKjp-Medium.otf"), size, ImFontConfigPtr.Null, fonts.GetGlyphRangesChineseFull());
        IconFont = fonts.AddFontFromFileTTF(Path.Combine(assets, "FontAwesomeFreeSolid.otf"), size, ImFontConfigPtr.Null, Ranges(0xE000, 0xF8FF, 0));
        MonoFont = fonts.AddFontFromFileTTF(Path.Combine(assets, "Inconsolata-Regular.ttf"), size, ImFontConfigPtr.Null, fonts.GetGlyphRangesDefault());
        fonts.Build();
        for (var i = 0; i < fonts.Textures.Size; i++)
        {
            byte* px;
            int w, h;
            ImGui.GetTexDataAsRGBA32(fonts, i, &px, &w, &h);
            var image = SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), (nint)px, w * 4);
            fonts.Textures.Data[i].TexID = new ImTextureID(Renderer.Register(image));
        }
        ImGui.GetIO().FontDefault = DefaultFont;
    }

    /// <summary>画一帧：draw 里调用窗口。返回这一帧的绘制数据（已 Render）。</summary>
    public ImDrawDataPtr Frame(Action draw)
    {
        var io = ImGui.GetIO();
        io.MousePos = MousePos;
        for (var i = 0; i < MouseDown.Length; i++) io.MouseDown[i] = MouseDown[i];
        io.MouseWheel = MouseWheel;
        MouseWheel = 0;
        ImGui.NewFrame();
        draw();
        ImGui.Render();
        return ImGui.GetDrawData();
    }

    /// <summary>按 WindowSystem 的方式画一个 Dalamud 窗口（PreDraw → Begin → Draw → End → PostDraw）。</summary>
    public static void DrawWindow(Window window)
    {
        if (!window.IsOpen) return;
        if (Current._opened.Add(window)) window.OnOpen();
        window.PreDraw();
        if (window.Position is { } pos) ImGui.SetNextWindowPos(pos, window.PositionCondition);
        if (window.Size is { } size) ImGui.SetNextWindowSize(size * Current.Scale, window.SizeCondition);
        if (window.SizeConstraints is { } c) ImGui.SetNextWindowSizeConstraints(c.MinimumSize * Current.Scale, c.MaximumSize * Current.Scale);
        if (window.BgAlpha is { } alpha) ImGui.SetNextWindowBgAlpha(alpha);
        var open = true;
        if (ImGui.Begin(window.WindowName, ref open, window.Flags))
            window.Draw();
        ImGui.End();
        window.PostDraw();
    }

    /// <summary>当前帧 ImGui 里某个窗口的屏幕矩形（裁图用）。</summary>
    public static (Vector2 Pos, Vector2 Size)? WindowRect(string name)
    {
        var w = ImGuiP.FindWindowByName(name);
        return w.IsNull ? null : (w.Pos, w.Size);
    }

    public SKImage Snapshot(ImDrawDataPtr data, SKColor background)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)DisplaySize.X, (int)DisplaySize.Y, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(background);
        Renderer.Render(data, surface.Canvas);
        return surface.Snapshot();
    }

    public void Dispose()
    {
        ImGui.DestroyContext();
        foreach (var p in _allocations) Marshal.FreeHGlobal(p);
    }
}

// ---------------------------------------------------------------- Dalamud 静态成员的替换

[HarmonyPatch(typeof(Dalamud.Interface.UiBuilder), nameof(Dalamud.Interface.UiBuilder.DefaultFont), MethodType.Getter)]
internal static class DefaultFontPatch
{
    private static bool Prefix(ref ImFontPtr __result)
    {
        __result = Host.Current.DefaultFont;
        return false;
    }
}

[HarmonyPatch(typeof(Dalamud.Interface.UiBuilder), nameof(Dalamud.Interface.UiBuilder.IconFont), MethodType.Getter)]
internal static class IconFontPatch
{
    private static bool Prefix(ref ImFontPtr __result)
    {
        __result = Host.Current.IconFont;
        return false;
    }
}

[HarmonyPatch(typeof(Dalamud.Interface.UiBuilder), nameof(Dalamud.Interface.UiBuilder.IconFontFixedWidth), MethodType.Getter)]
internal static class IconFontFixedPatch
{
    private static bool Prefix(ref ImFontPtr __result)
    {
        __result = Host.Current.IconFont;
        return false;
    }
}

[HarmonyPatch(typeof(Dalamud.Interface.UiBuilder), nameof(Dalamud.Interface.UiBuilder.MonoFont), MethodType.Getter)]
internal static class MonoFontPatch
{
    private static bool Prefix(ref ImFontPtr __result)
    {
        __result = Host.Current.MonoFont;
        return false;
    }
}

[HarmonyPatch(typeof(Dalamud.Interface.Utility.ImGuiHelpers), nameof(Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale), MethodType.Getter)]
internal static class GlobalScalePatch
{
    private static bool Prefix(ref float __result)
    {
        __result = Host.Current.Scale;
        return false;
    }
}

[HarmonyPatch(typeof(Dalamud.Interface.Utility.ImGuiHelpers), nameof(Dalamud.Interface.Utility.ImGuiHelpers.GlobalScaleSafe), MethodType.Getter)]
internal static class GlobalScaleSafePatch
{
    private static bool Prefix(ref float __result)
    {
        __result = Host.Current.Scale;
        return false;
    }
}
