using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.DragDrop;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Posegraph.UI;

namespace Posegraph;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/posegraph";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _commands;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly BridgeServer _server;
    private readonly WindowSystem _windows = new("Posegraph");
    private readonly MainWindow _main;
    private readonly LibraryWindow _library;
    private readonly AuxViewsMenu _auxMenu;
    private readonly SplitWindow _split;
    private readonly MaskEditorWindow _masks;
    private readonly UpdateWindow _update;
    private bool _changelogChecked;   // 本次加载后第一次打开主窗口时检查一次是否要弹更新日志
    private readonly IDragDropManager _dragDrop;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly BrioBridge _brio;
    private bool _wasGposing;
    private bool _autoOpenDone;    // 本次 GPose 已处理过自动打开（手动关掉后不再弹出）
    private readonly Lighting.LightingService _lighting;
    private readonly LightingWindow _lightingWindow;
    private readonly CompositionWindow _composition;
    private bool _openedByGpose;
    private bool _shotRequested;
    private readonly IDataManager _data;
    private bool _faceDumping;   // 当前窗口是进入 GPose 时自动打开的：退出 GPose 时自动关闭

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commands,
        IChatGui chat,
        IFramework framework,
        IClientState clientState,
        ITargetManager targetManager,
        ITextureProvider textures,
        ITextureReadbackProvider readback,
        IDragDropManager dragDrop,
        IGameGui gameGui,
        IDataManager data,
        IPluginLog log)
    {
        _data = data;
        _pi = pi;
        _commands = commands;
        _chat = chat;
        _log = log;
        _config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        Loc.Apply(_config.Language, clientState, pi);

        var brio = new BrioBridge(pi);
        _server = new BridgeServer(framework, clientState, targetManager, brio, log);
        if (_config.AutoStartServer)
            TryStart();

        var applier = new PoseApplier(framework, clientState, targetManager, brio);
        _main = new MainWindow(_config, pi, clientState, textures, readback, dragDrop, brio, applier, _server);
        _windows.AddWindow(_main);
        _auxMenu = new AuxViewsMenu(_config, _main.Library, _main.Recognition, textures, readback, log);
        _main.AuxMenu = _auxMenu;
        _library = new LibraryWindow(_config, pi, _main.Library, _main.Recognition, textures, readback, dragDrop, log,
                                     _main.OpenImage, _main.SaveToBrioAsync, () => _main.BackendOnline, _auxMenu);
        _windows.AddWindow(_library);
        _split = new SplitWindow(_config, pi, _main, _library, _main.Library, _auxMenu, textures, dragDrop, log);
        _windows.AddWindow(_split);
        _main.ToggleSplit = _split.Toggle;
        _main.MergeListOpen = () => _split.IsOpen && _split.MergeMode;
        _main.ShowPeopleList = _split.ShowMerge;
        _masks = new MaskEditorWindow(_main, textures);
        _windows.AddWindow(_masks);
        _main.MaskEditor = _masks;
        _update = new UpdateWindow(_config, pi, textures);
        _windows.AddWindow(_update);
        _main.ShowChangelog = () => _update.IsOpen = true;
        _main.ToggleLibrary = _library.Toggle;
        _lighting = new Lighting.LightingService(framework, clientState, applier, _config, gameGui);
        _lighting.CurrentImage = () => _main.CurrentImageSha1 is { } sha ? (sha, _main.Library.ReadLighting(sha)) : null;
        _lightingWindow = new LightingWindow(_lighting, _config, pi, () => _main.SelectedActor, () => clientState.IsGPosing,
                                             () => _main.HasPhoto, _main.PhotoLightingAsync, () => _main.CurrentImageSha1, _main.Library);
        _windows.AddWindow(_lightingWindow);
        var lightingScene = new LightingSceneWindow(_lighting, _config, pi, _lightingWindow);
        _windows.AddWindow(lightingScene);
        _lightingWindow.SceneWindow = lightingScene;
        _main.ToggleLighting = _lightingWindow.Toggle;
        _composition = new CompositionWindow(_config, pi, framework, clientState, textures, readback, () => _main.PhotoSize,
                                             () => _main.PhotoTexture, () => _main.PhotoSkeleton);
        _windows.AddWindow(_composition);
        _main.ToggleComposition = _composition.Toggle;
        _main.DumpFaceData = DumpFaceAnimations;
        _main.LibraryDragged = () => _library.DraggedImages;
        _main.Closed = () => _library.IsOpen = _split.IsOpen = _masks.IsOpen = _lightingWindow.IsOpen = _composition.IsOpen = false;
        // 消息里可能有花括号（后端返回的 JSON），按参数传，不当成日志模板
        _main.Recognition.Log = (message, ex) =>
        {
            if (ex is null) log.Warning("{Message}", message);
            else log.Warning(ex, "{Message}", message);
        };
        _main.LibraryChanged = _library.Refresh;
        _dragDrop = dragDrop;
        _framework = framework;
        _clientState = clientState;
        _brio = brio;
        // 本插件只在 GPose 里有用：不受 Dalamud"GPose 中隐藏插件界面"设置影响（与 Brio 默认一致）
        pi.UiBuilder.DisableGposeUiHide = true;
        ApplyUiHide(pi, _config);
        framework.Update += OnFrameworkUpdate;
        pi.UiBuilder.Draw += DrawDragSource;
        pi.UiBuilder.Draw += _composition.DrawOverlay;
        pi.UiBuilder.Draw += DrawPendingShot;
        pi.UiBuilder.Draw += _windows.Draw;
        pi.UiBuilder.Draw += _auxMenu.DrawDialogs;
        pi.UiBuilder.OpenMainUi += ToggleManually;
        pi.UiBuilder.OpenConfigUi += _main.OpenSettings;

        _commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = Loc.T("打开主界面；on / off / status：开关或查看本地 HTTP 服务；shot：截图（可绑到宏）"),
        });
    }

    /// <summary>游戏界面隐藏（含 GPose 里隐藏界面）时是否仍显示，默认跟随游戏界面隐藏。</summary>
    public static void ApplyUiHide(IDalamudPluginInterface pi, Configuration config)
    {
        pi.UiBuilder.DisableAutomaticUiHide = config.ShowWhenUiHidden;
        pi.UiBuilder.DisableUserUiHide = config.ShowWhenUiHidden;
    }

    /// <summary>指令 / 插件列表按钮打开：算手动打开，退出 GPose 时不自动关闭。</summary>
    private void ToggleManually()
    {
        _openedByGpose = false;
        _main.Toggle();
    }

    private void TryStart()
    {
        try { _server.Start(_config.Port); }
        catch (Exception ex)
        {
            _log.Error(ex, Loc.T("Posegraph HTTP 启动失败"));
            _chat.PrintError(Loc.F("[Posegraph] 端口 {0} 启动失败：{1}", _config.Port, ex.Message));
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":
                ToggleManually();
                return;
            case "on":
                TryStart();
                break;
            case "off":
                _server.Stop();
                break;
            case "shot":
                _shotRequested = true;   // 截图要读 ImGui 视口：放到下一次绘制里做
                return;
            case "facedump":
                DumpFaceAnimations();
                return;
        }
        _chat.Print(_server.IsRunning
            ? Loc.F("[Posegraph] 运行中：http://127.0.0.1:{0}/", _server.Port)
            : Loc.T("[Posegraph] 已停止"));
    }

    /// <summary>进入 GPose 且 Brio 就绪时自动打开主界面（每次进入 GPose 只打开一次，设置里可关）；
    /// 自动打开的窗口在退出 GPose 时关闭，手动打开的保持不动。</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_changelogChecked && _main.IsOpen)
        {
            _changelogChecked = true;
            _update.ShowIfUpdated();
        }
        var gposing = _clientState.IsGPosing;
        if (gposing != _wasGposing)
        {
            if (!gposing && _openedByGpose) _main.IsOpen = false;
            _openedByGpose = false;
            _autoOpenDone = false;
            _wasGposing = gposing;
        }
        if (!gposing || _autoOpenDone || !_config.AutoOpenInGpose) return;
        if (!_brio.IsAvailable()) return;   // Brio 刚进 GPose 时可能还没就绪，下一帧再看
        _autoOpenDone = true;
        if (_main.IsOpen) return;           // 进 GPose 前就手动打开着：仍算手动
        _main.IsOpen = true;
        _openedByGpose = true;
    }

    /// <summary>开发用：导出游戏自带的面部表情动画（见 FaceAnimationDump），不需要进 GPose。</summary>
    private void DumpFaceAnimations()
    {
        if (_faceDumping) return;
        _faceDumping = true;
        _chat.Print(Loc.T("[Posegraph] 开始导出面部表情动画，约需一两分钟，期间游戏可能会短暂卡顿"));
        _ = Task.Run(async () =>
        {
            try
            {
                await FaceAnimationDump.RunAsync(_data, _framework, message => _log.Info("{Message}", message));
                _chat.Print(Loc.F("[Posegraph] 面部表情动画已导出到 {0}", FaceAnimationDump.OutputDir));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "facedump");
                _chat.PrintError(Loc.F("[Posegraph] 导出失败：{0}", ex.Message));
            }
            finally { _faceDumping = false; }
        });
    }

    private void DrawPendingShot()
    {
        if (!_shotRequested) return;
        _shotRequested = false;
        if (_clientState.IsGPosing) _composition.Shoot();
        else _chat.PrintError(Loc.T("[Posegraph] 进入 GPose 后才能截图"));
    }

    /// <summary>
    /// 把系统里拖动的图片 / 文件夹转成 ImGui 拖放负载（每帧一次，主窗口和图库窗口都用 CreateImGuiTarget 接收）。
    /// </summary>
    private void DrawDragSource()
        => _dragDrop.CreateImGuiSource(LibraryWindow.ExternalDragLabel,
            m => m.Files.Any(Library.ImageLibrary.IsImage) || m.Directories.Count > 0,
            m =>
            {
                var images = m.Files.Count(Library.ImageLibrary.IsImage);
                var what = m.Directories.Count > 0 ? Loc.F("{0} 张图片和 {1} 个文件夹", images, m.Directories.Count)
                         : images == 1 ? Path.GetFileName(m.Files.First(Library.ImageLibrary.IsImage))
                         : Loc.F("{0} 张图片", images);
                // 图库窗口：查重后复制进当前文件夹；主窗口：直接识别第一张，不存入图库（识别后由用户决定是否存入）
                ImGui.TextUnformatted(_library.IsOpen && _library.ExternalDragHovered
                    ? Loc.F("松开以导入到图库「{0}」：{1}", _library.FolderName, what)
                    : Loc.F("松开以识别（不存入图库）：{0}", what));
                return true;
            });

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _lighting.Dispose();
        _commands.RemoveHandler(Command);
        _pi.UiBuilder.Draw -= _windows.Draw;
        _pi.UiBuilder.Draw -= _auxMenu.DrawDialogs;
        _pi.UiBuilder.Draw -= DrawDragSource;
        _pi.UiBuilder.Draw -= _composition.DrawOverlay;
        _pi.UiBuilder.Draw -= DrawPendingShot;
        _pi.UiBuilder.OpenMainUi -= ToggleManually;
        _pi.UiBuilder.OpenConfigUi -= _main.OpenSettings;
        _windows.RemoveAllWindows();
        _library.Dispose();
        _split.Dispose();
        _masks.Dispose();
        _update.Dispose();
        _auxMenu.Dispose();
        _main.Dispose();
        _server.Dispose();
        _pi.SavePluginConfig(_config);
    }
}
