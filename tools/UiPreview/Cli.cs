using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Posegraph;
using Posegraph.UI;
using SkiaSharp;

namespace UiPreview;

/// <summary>
/// 用法：UiPreview [场景…] [--out 目录] [--lang zh|en] [--scale 1.0] [--demo 示例图目录]
/// 不给场景时列出全部场景。每个场景输出一张 PNG（按窗口裁切，带一点边距）。
/// </summary>
public static class Cli
{
    public static int Run(string[] args, string dalamudDir)
    {
        NativeLibrary.SetDllImportResolver(typeof(Dalamud.Bindings.ImGui.ImGui).Assembly, (lib, _, _) =>
            NativeLibrary.Load(Path.Combine(dalamudDir, lib.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? lib : lib + ".dll")));

        var scenes = new List<string>();
        var outDir = Path.Combine(Environment.CurrentDirectory, "shots");
        var lang = "zh";
        var scale = 1f;
        string? demo = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = args[++i]; break;
                case "--lang": lang = args[++i]; break;
                case "--scale": scale = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--demo": demo = args[++i]; break;
                default: scenes.Add(args[i]); break;
            }
        }
        if (scenes.Count == 0)
        {
            Console.WriteLine("场景：" + string.Join(" ", Scenes.All.Keys));
            return 0;
        }
        Directory.CreateDirectory(outDir);
        Console.Error.WriteLine("[cli] 初始化 ImGui");
        using var host = new Host(new Vector2(1600, 1000), scale);
        Tutorial.Register(Scenes.All);
        MergeScenes.Register(Scenes.All);
        var app = new App(host, lang, demo);
        foreach (var name in scenes.SelectMany(s => s == "all" ? Scenes.All.Keys.Where(k => !k.StartsWith("bench-")).ToList() : [s]))
        {
            if (!Scenes.All.TryGetValue(name, out var scene))
            {
                Console.Error.WriteLine($"没有场景 {name}");
                return 1;
            }
            var path = Path.Combine(outDir, name + ".png");
            scene(app, path);
            Console.WriteLine(path);
        }
        return 0;
    }
}

/// <summary>插件对象（真实的窗口类，服务是替身）。</summary>
public sealed class App
{
    public Host Host { get; }
    public Configuration Config { get; }
    public MainWindow Main { get; }
    public LibraryWindow Library { get; }
    public SplitWindow Split { get; }
    public MaskEditorWindow Masks { get; }
    public UpdateWindow Update { get; }
    public bool Gposing { get; set; }

    /// <summary>假的 GPose 角色列表（见 PluginPatches）；null 为不在 GPose。</summary>
    public static (int Index, string Name)[]? FakeActors { get; set; }

    /// <summary>设置插件对象的私有字段（造出识别后、有状态文字等画面）。</summary>
    public static void Set(object target, string field, object? value)
        => target.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                 .SetValue(target, value);

    public static T Get<T>(object target, string field)
        => (T)target.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(target)!;

    /// <summary>主窗口切到第几个标签页（0 图像、1 3D、2 调整、3 提示、4 设置），下一帧生效。</summary>
    public void SelectTab(int index)
        => typeof(MainWindow).GetField("_selectTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                             .SetValue(Main, index);

    public App(Host host, string lang, string? demoSamples = null)
    {
        Host = host;
        Console.Error.WriteLine("[app] 创建服务");
        Config = new Configuration { AutoStartBackend = false, AutoStartServer = false, Language = lang };
        var pi = Services.PluginInterface(() => [Services.Plugin("Brio", "0.6.1.0")]);
        var clientState = Stub.Create<IClientState>(s => s.On("get_IsGPosing", _ => Gposing));
        Loc.Apply(lang, clientState, pi);
        var framework = Services.Framework();
        var targets = Stub.Create<ITargetManager>();
        var log = Stub.Create<IPluginLog>();
        var brio = new BrioBridge(pi);
        var textures = Services.Textures(host.Renderer);
        var readback = Stub.Create<ITextureReadbackProvider>();
        var dragDrop = Stub.Create<Dalamud.Interface.DragDrop.IDragDropManager>();
        Console.Error.WriteLine("[app] 创建主窗口");
        Main = new MainWindow(Config, pi, clientState, textures, readback, dragDrop, brio,
                              new PoseApplier(framework, clientState, targets, brio),
                              new BridgeServer(framework, clientState, targets, brio, log));
        if (demoSamples is not null)
        {
            Demo.Enabled = true;
            Demo.Setup(this, demoSamples);
        }
        var aux = new AuxViewsMenu(Config, Main.Library, Main.Recognition, textures, readback, log);
        Main.AuxMenu = aux;
        Library = new LibraryWindow(Config, pi, Main.Library, Main.Recognition, textures, readback, dragDrop, log,
                                    Main.OpenImage, Main.SaveToBrioAsync, () => Main.BackendOnline, aux);
        Split = new SplitWindow(Config, pi, Main, Library, Main.Library, aux, textures, dragDrop, log);
        Main.ToggleLibrary = Library.Toggle;
        Main.ToggleSplit = Split.Toggle;
        Main.MergeListOpen = () => Split.IsOpen && Config.SplitMerge;   // 与 Plugin 一样接上合并模式
        Main.ShowPeopleList = Split.ShowMerge;
        Masks = new MaskEditorWindow(Main, textures);
        Main.MaskEditor = Masks;
        Update = new UpdateWindow(Config, pi, textures);
        Lighting = new Posegraph.Lighting.LightingService(framework, clientState, new PoseApplier(framework, clientState, targets, brio), Config);
        LightingWindow = new LightingWindow(Lighting, Config, pi, () => null, () => Gposing);
        Main.ToggleLighting = LightingWindow.Toggle;
        Composition = new CompositionWindow(Config, pi, framework, clientState, textures, readback, () => [535, 758], () => null,
                                    () => [new Posegraph.Backend.Segment([150, 120], [180, 400], "c"),
                                           new Posegraph.Backend.Segment([180, 400], [120, 700], "l"),
                                           new Posegraph.Backend.Segment([180, 400], [330, 690], "r")]);
        Main.ToggleComposition = Composition.Toggle;
    }

    public CompositionWindow Composition { get; }

    public Posegraph.Lighting.LightingService Lighting { get; }
    public LightingWindow LightingWindow { get; }

    /// <summary>画 frames 帧，最后一帧按 windows 的矩形（并集，加 margin）裁切保存。</summary>
    public void Shot(string path, Action draw, string[] windows, int frames = 4, float margin = 16)
    {
        Dalamud.Bindings.ImGui.ImDrawDataPtr data = default;
        for (var i = 0; i < frames; i++)
        {
            Console.Error.WriteLine($"[shot] 第 {i} 帧");
            data = Host.Frame(draw);
        }
        using var full = Host.Snapshot(data, new SKColor(38, 44, 52));
        var rects = windows.Select(Host.WindowRect).OfType<(Vector2 Pos, Vector2 Size)>().ToList();
        var crop = rects.Count == 0
            ? new SKRectI(0, 0, full.Width, full.Height)
            : SKRectI.Round(new SKRect(rects.Min(r => r.Pos.X) - margin, rects.Min(r => r.Pos.Y) - margin,
                                       rects.Max(r => r.Pos.X + r.Size.X) + margin, rects.Max(r => r.Pos.Y + r.Size.Y) + margin));
        crop.Intersect(new SKRectI(0, 0, full.Width, full.Height));
        using var image = full.Subset(crop);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, png.ToArray());
    }
}
