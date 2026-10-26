using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.DragDrop;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Posegraph.Backend;
using Posegraph.Library;

namespace Posegraph.UI;

/// <summary>
/// 主界面：状态栏 → 选图识别 → 图像叠加 / 3D 火柴人 / 调整 / 提示 / 设置 → 应用、镜头、还原、导出。
/// 识别只做一次（后端缓存），调参只重跑重定向；拖动滑杆时只刷新预览，松手才应用到角色（Brio 撤销历史保持干净）。
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.35f, 0.85f, 0.4f, 1);
    private static readonly Vector4 Yellow = new(1f, 0.8f, 0.25f, 1);
    private static readonly Vector4 Red = new(1f, 0.35f, 0.3f, 1);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1);
    private static readonly Vector4 Orange = new(1f, 0.55f, 0f, 1);
    private static readonly Vector4 MhrGreen = new(0.31f, 0.86f, 0.31f, 1);


    // 静态表里的界面文字存中文原文，使用时再 Loc.T 翻译（运行时可切换语言）

    private static readonly (string Key, string Label, string Tip)[] AmplitudeGroups =
    [
        ("spine", "脊柱", "腰/背/胸三节的弯曲扭转。脊柱用绑定朝向对齐后动作可能偏保守，可适当放大"),
        ("neck", "颈", "颈部转动。插画头颈比例夸张，识别容易估大，建议 0.7"),
        ("head", "头", "头部相对颈的转动。插画建议 0.7"),
        ("clavicle", "锁骨", "耸肩/含胸。过大时斜方肌会隆起、肩部出尖角"),
        ("wrist", "手腕", "手腕弯曲扭转"),
    ];

    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly IClientState _clientState;
    private readonly ITextureProvider _textures;
    private readonly IDragDropManager _dragDrop;
    private readonly BrioBridge _brio;
    private readonly PoseApplier _applier;
    private readonly BridgeServer _server;
    private readonly BackendClient _client;
    private readonly BackendProcess _process;
    private readonly ITextureReadbackProvider _readback;
    public ImageLibrary Library { get; }
    public RecognitionService Recognition { get; }

    /// <summary>打开 / 关闭图库窗口（由 Plugin 接上）。</summary>
    public Action? ToggleLibrary { get; set; }
    private readonly SystemProbe _probe = new();
    private readonly BackendUpdater _updater = new();

    // 后端版本信息（读磁盘，2 秒刷新一次）
    private BackendUpdater.VersionInfo? _installedBackend;
    private int? _packageRuntime;
    private bool _backendOutdated;
    private DateTime _lastVersionCheck = DateTime.MinValue;
    private readonly FileDialogManager _fileDialog = new();

    // 后端状态
    private Health? _health;
    private string? _healthError;
    private DateTime _lastHealth = DateTime.MinValue;
    private Task? _healthTask;

    // 会话
    private SessionInfo? _session;
    private string? _imagePath;
    private string? _imageSha1;      // 当前图的内容哈希：会话丢了（后端重启）时据此从识别缓存恢复
    private bool _correctionLoaded;  // 当前参数来自这张图保存的修正（不写回预设）
    private IDalamudTextureWrap? _texture;
    private IDalamudTextureWrap? _newTexture;
    private int _person;
    private RetargetParams _params;
    private bool _paramsDirty;       // 参数可能变了：松手后与已保存的比较，不同就写入配置
    private string _savedParams = "";
    private string _preset;
    private volatile RetargetResult? _result;
    private volatile bool _recognizing;
    private readonly CancellationTokenSource _clipboardLifetime = new();
    private Task<byte[]?>? _clipboardTask;

    // 重定向请求合并：忙时只记"还需要再算一次"
    private volatile bool _rtBusy, _rtDirty, _rtPush;

    // 角色
    private int? _actor;
    private (int Index, string Name)[] _actors = [];
    private DateTime _lastActors = DateTime.MinValue;
    private string? _reference;
    private string? _detectedRace;      // 从角色外貌数据读出的骨架编号
    private string? _raceOverride;      // 手动指定，null 为自动
    private string? _referenceSignature;   // 缓存的参考姿势属于哪个角色身份（PoseApplier.ActorIdentity）
    private string? _currentSignature;     // 当前角色身份，每秒刷新
    private string? _pendingSignature;     // 检测到变化后等它稳定一秒（模型重建完）再重算
    private int? _pushedIndex;             // 应用过姿势的角色：外貌变化重算后自动再应用
    private string? _pushStatus;           // 下一次应用成功时显示的状态（代替"已应用到角色"）
    private readonly Dictionary<string, string> _backups = new();   // 身份 → 第一次应用前的姿势
    private bool _resetPending;                                     // 换了种族：下次应用前先清除 Brio 残留姿势
    private readonly HashSet<string> _cleanSignatures = new();      // 已清除过 Brio 姿势的身份：骨长不符也信游戏值
    private string? _actorKey;                                      // 上面这些状态属于哪个角色（ActorIdentity.Key）
    private bool? _actorIvcs;                                       // 当前角色是否加载了 IVCS 骨骼，没读到游戏姿势时为 null
    private readonly Dictionary<string, ActorState> _actorStates = new();   // 本次 GPose 里切换过的角色各自的状态

    /// <summary>切走时记下的角色状态，切回来时恢复。Race：上次看到的骨架编号，切回来时不同说明在别处换过种族。</summary>
    private sealed record ActorState(string? Race, string? RaceOverride, bool ResetPending);

    // 3D 视图
    private float _yaw, _pitch = 0.15f, _zoom = 1f;
    private bool _dragged;
    private string? _selected;

    private string _statusText = Loc.T("就绪");
    private Vector4 _statusColor = Grey;
    private int? _selectTab;

    public MainWindow(Configuration config, IDalamudPluginInterface pi, IClientState clientState, ITextureProvider textures,
                      ITextureReadbackProvider readback, IDragDropManager dragDrop, BrioBridge brio, PoseApplier applier,
                      BridgeServer server)
        : base("Posegraph###PosegraphMain")
    {
        _config = config;
        _pi = pi;
        _clientState = clientState;
        _textures = textures;
        _readback = readback;
        _dragDrop = dragDrop;
        _brio = brio;
        _applier = applier;
        _server = server;
        _client = new BackendClient(() => _config.BackendPort);
        Library = new ImageLibrary(pi.GetPluginConfigDirectory());
        Recognition = new RecognitionService(Library, _client);
        _process = new BackendProcess();
        _preset = config.DefaultPreset;
        _params = LoadParams(_preset);
        _savedParams = ParamsSnapshot(_params);

        Size = new Vector2(420, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(330, 260), MaximumSize = new Vector2(4000, 4000) };

        if (_config.AutoStartBackend && BackendProcess.Validate(_config) is null)
            _ = Task.Run(async () =>
            {
                try { await _client.HealthAsync(); }   // 已有后端在跑（比如手动启动的）就不再启动
                catch { _process.Start(_config); }
            });
    }

    public void OpenSettings()
    {
        IsOpen = true;
        _selectTab = 4;
    }

    public void Dispose()
    {
        _clipboardLifetime.Cancel();
        if (_clipboardTask is { } clipboard)
            _ = clipboard.ContinueWith(task =>
            {
                _ = task.Exception;
                _clipboardLifetime.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else _clipboardLifetime.Dispose();
        _clipboardTask = null;
        if (_config.StopBackendOnExit)
            _process.Stop();
        _process.Dispose();
        Recognition.Dispose();
        Library.Save();
        _client.Dispose();
        _updater.Dispose();
        _texture?.Dispose();
        _newTexture?.Dispose();
    }

    private void SetStatus(string text, Vector4? color = null)
    {
        _statusText = text;
        _statusColor = color ?? Grey;
    }

    private void SetError(string text) => SetStatus(text, Red);

    // ------------------------------------------------------------------ 状态

    public bool BackendOnline => _health is not null;

    private bool ModelReady => _health?.Model.Loaded == true;

    private void RefreshBackendVersion()
    {
        if (DateTime.UtcNow - _lastVersionCheck < TimeSpan.FromSeconds(2) || string.IsNullOrEmpty(_config.BackendDir)) return;
        _lastVersionCheck = DateTime.UtcNow;
        _installedBackend = BackendUpdater.Installed(_config.BackendDir);
        _packageRuntime = BackendUpdater.PackageRuntime(_config.BackendDir);
        _backendOutdated = _packageRuntime is not null && BackendUpdater.NeedsUpdate(_config.BackendDir);
    }

    private void PollHealth()
    {
        _probe.Tick();
        RefreshBackendVersion();
        if (_healthTask is { IsCompleted: false } || DateTime.UtcNow - _lastHealth < TimeSpan.FromSeconds(2)) return;
        _lastHealth = DateTime.UtcNow;
        _healthTask = Task.Run(async () =>
        {
            try
            {
                _health = await _client.HealthAsync();
                _healthError = null;
            }
            catch (Exception ex)
            {
                _health = null;
                _healthError = ex.Message;
            }
        });
    }

    private void RefreshActors()
    {
        if (DateTime.UtcNow - _lastActors < TimeSpan.FromSeconds(1)) return;
        _lastActors = DateTime.UtcNow;
        if (!_clientState.IsGPosing)
        {
            _actors = [];
            if (_actorKey is not null || _actorStates.Count > 0) ForgetActors();
            return;
        }
        _actors = _brio.GetActors().Where(Races.IsPoseable).Select(a => ((int)a.ObjectIndex, a.Name.TextValue)).ToArray();

        var id = _applier.Identify(_actor);
        if (id is null) return;
        // 换了一个角色（GPose 目标换人、下拉框选了别人）：换成那个角色自己的状态，不当作换种族
        if (id.Key != _actorKey)
        {
            SwitchActor(id);
            return;
        }
        // 同一个角色外貌变了（Glamourer 换种族 / 体型）：丢弃参考姿势，重新计算
        if (_reference is not null && _referenceSignature != id.Signature)
        {
            // 模型重建中，或刚变化：等下一秒身份不再变化再读游戏骨长
            if (!id.ModelReady || _pendingSignature != id.Signature)
            {
                _pendingSignature = id.Signature;
                return;
            }
            _pendingSignature = null;
            _currentSignature = id.Signature;
            var raceChanged = id.Race != _detectedRace;
            if (raceChanged) _resetPending = true;   // 旧骨架的姿势增量会叠加到新骨架上
            _reference = null;
            _detectedRace = id.Race;
            if (_session is not null)
            {
                var push = _pushedIndex == id.Index;
                var message = raceChanged ? Loc.F("检测到角色种族变化（{0}），已重新计算", Races.Label(id.Race))
                                          : Loc.T("检测到角色外貌变化，已重新计算");
                if (push) _pushStatus = message + Loc.T("并应用");
                else SetStatus(message, Yellow);
                RequestRetarget(push);
            }
            return;
        }
        _pendingSignature = null;
        _currentSignature = id.Signature;
        if (_reference is null)
            _detectedRace = id.Race;
    }

    /// <summary>
    /// 切换到另一个角色：记下当前角色的状态，换成新角色在本次 GPose 里上次的状态。
    /// 不清除新角色的 Brio 姿势——除非切走期间它在别处换过种族（骨架编号与上次看到的不同）。
    /// </summary>
    private void SwitchActor(PoseApplier.ActorIdentity id)
    {
        SaveActorState();
        var switched = _actorKey is not null;
        _actorKey = id.Key;
        var state = _actorStates.GetValueOrDefault(id.Key);
        var raceChanged = state?.Race is { } old && id.ModelReady && old != id.Race;
        // 进 GPose 前（还没有角色时）手动指定的种族留给第一个角色
        if (switched || state is not null) _raceOverride = state?.RaceOverride;
        _resetPending = state?.ResetPending == true || raceChanged;
        _detectedRace = id.Race;
        _reference = null;
        _referenceSignature = null;
        _pendingSignature = null;
        _currentSignature = id.Signature;
        CheckIvcs(id.Key);
        if (_session is null) return;
        if (raceChanged) SetStatus(Loc.F("检测到角色种族变化（{0}），已重新计算", Races.Label(id.Race)), Yellow);
        else if (switched) SetStatus(Loc.F("已切换到 {0}，已重新计算", ActorName(id.Index)));
        RequestRetarget(false);
    }

    /// <summary>读一次角色的游戏姿势，判断是否加载了 IVCS 骨骼（不用等识别图片，选中角色就能在状态栏看到）。</summary>
    private void CheckIvcs(string key)
    {
        _actorIvcs = null;
        _ = Run(async () =>
        {
            if ((await _applier.IdentifyAsync(_actor))?.Key != key) return;
            var pose = await _applier.GetPoseAsync(_actor);
            if (pose is not null && _actorKey == key) _actorIvcs = HasIvcs(pose);
        });
    }

    /// <summary>Brio 读回的姿势含骨架里的全部骨骼，有 iv_ 骨骼就是装了 IVCS（与后端的判断一致）。</summary>
    private static bool HasIvcs(string pose) => pose.Contains("\"iv_", StringComparison.Ordinal);

    private void SaveActorState()
    {
        if (_actorKey is not null)
            _actorStates[_actorKey] = new ActorState(_detectedRace, _raceOverride, _resetPending);
    }

    /// <summary>退出 GPose：角色都没了，下次进 GPose 会重新分配 ObjectIndex，按角色记的状态全部作废。</summary>
    private void ForgetActors()
    {
        _actorStates.Clear();
        _backups.Clear();
        _cleanSignatures.Clear();
        _actorKey = null;
        _reference = null;
        _referenceSignature = null;
        _pendingSignature = null;
        _currentSignature = null;
        _pushedIndex = null;
        _resetPending = false;
        _actorIvcs = null;
    }

    private string ActorName(int index) => _actors.FirstOrDefault(a => a.Index == index).Name ?? $"#{index}";

    private static void StatusIcon(FontAwesomeIcon icon, Vector4 color, string tip)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color, icon.ToIconString());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        ImGui.SameLine();
    }

    private static bool IconButton(string id, FontAwesomeIcon icon, string tip, bool enabled = true)
    {
        bool clicked;
        using (ImRaii.Disabled(!enabled))
            clicked = ImGuiComponents.IconButton(id, icon);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tip);
        return clicked && enabled;
    }

    private void DrawStatusBar()
    {
        // Brio
        var brio = _pi.InstalledPlugins.FirstOrDefault(p => p.InternalName == "Brio");
        var brioIpc = brio is { IsLoaded: true } && _brio.IsAvailable();
        if (brio is null) StatusIcon(FontAwesomeIcon.TimesCircle, Red, Loc.T("Brio：未安装"));
        else if (!brio.IsLoaded) StatusIcon(FontAwesomeIcon.PauseCircle, Yellow, Loc.F("Brio {0}：已安装但未启用", brio.Version));
        else StatusIcon(FontAwesomeIcon.CheckCircle, brioIpc ? Green : Yellow,
                        brioIpc ? Loc.F("Brio {0}：可用", brio.Version) : Loc.F("Brio {0}：已启用，IPC 暂不可用", brio.Version));

        // GPose
        StatusIcon(FontAwesomeIcon.Camera, _clientState.IsGPosing ? Green : Grey,
                   _clientState.IsGPosing ? Loc.T("GPose：已进入") : Loc.T("GPose：未进入（只能预览和导出，不能应用到角色）"));

        // IVCS（选中角色时读一次游戏姿势判断，之后每次读参考姿势时更新）
        if (_clientState.IsGPosing && _actorIvcs is { } ivcs)
            StatusIcon(FontAwesomeIcon.HandPaper, ivcs ? Green : Yellow,
                       ivcs ? Loc.T("IVCS：已加载") : Loc.T("IVCS：未检测到（手指第三节的弯曲已合并到第二节；装上 IVCS 手指会更准）"));

        // 后端
        var (icon, color, tip) = BackendState();
        StatusIcon(icon, color, tip);

        // 显存 / 内存
        var vramOk = ModelReady || _probe.VramFreeMB >= SystemProbe.RequiredVramMB || _config.BackendDevice == "cpu";
        var vramText = _probe.VramFreeMB is { } free
            ? Loc.F("{0}\n显存可用 {1:0.0} / {2:0.0} GB", _probe.GpuName, free / 1024f, _probe.VramTotalMB / 1024f)
            : _probe.GpuError ?? Loc.T("读取中…");
        vramText += ModelReady ? Loc.F("\n模型已加载（{0}）", DeviceLabel(_health?.Model.Device))
            : _config.BackendDevice == "cpu" ? Loc.T("\n识别设备：CPU（不占显存）")
            : Loc.F("\nGPU 识别约需 {0:0.0} GB", SystemProbe.RequiredVramMB / 1024f) + (vramOk ? ""
                : _config.BackendDevice == "auto" ? Loc.T("，当前不足：将自动改用 CPU（约 10 秒/张）")
                : Loc.T("，当前不足：显存溢出到共享内存后识别会极慢，游戏也可能崩溃。建议在设置里改为“自动”或“CPU”"));
        StatusIcon(FontAwesomeIcon.Microchip, _probe.VramFreeMB is null ? Grey : vramOk ? Green : Yellow, vramText);
        var ramOk = ModelReady || _probe.RamAvailMB >= SystemProbe.RequiredRamMB;
        StatusIcon(FontAwesomeIcon.Memory, _probe.RamTotalMB == 0 ? Grey : ramOk ? Green : Yellow,
                   Loc.F("内存可用 {0:0.0} / {1:0.0} GB", _probe.RamAvailMB / 1024f, _probe.RamTotalMB / 1024f) +
                   (ramOk ? "" : Loc.F("\n加载模型约需 {0:0.0} GB，当前不足", SystemProbe.RequiredRamMB / 1024f)));

        // 右侧：后端开关、模型加载/释放、设置
        var right = ImGui.GetContentRegionMax().X - 3 * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right));
        if (BackendOnline || _process.IsRunning)
        {
            if (IconButton("##stopBackend", FontAwesomeIcon.PowerOff, Loc.T("停止后端（释放显存与内存）")))
                StopBackend();
        }
        else
        {
            var problem = BackendProcess.Validate(_config);
            var resources = _probe.RamAvailMB < SystemProbe.RequiredRamMB ? Loc.T("\n注意：当前可用内存低于建议值") : "";
            if (IconButton("##startBackend", FontAwesomeIcon.Plug, problem ?? Loc.T("启动识别后端") + resources))
            {
                if (problem is null) StartBackend();
                else
                {
                    SetError(problem);
                    _selectTab = 4;
                }
            }
        }
        ImGui.SameLine();
        if (ModelReady)
        {
            if (IconButton("##unload", FontAwesomeIcon.Eject, Loc.T("释放模型显存（下次识别会重新加载）")))
                _ = Run(async () => { await _client.UnloadModelAsync(); SetStatus(Loc.T("已释放模型")); });
        }
        else if (IconButton("##load", FontAwesomeIcon.Download, Loc.T("预加载模型"), BackendOnline && _health?.Model.Loading != true))
            _ = Run(async () => { await _client.LoadModelAsync(); SetStatus(Loc.T("模型加载中…")); });
        ImGui.SameLine();
        if (IconButton("##settings", FontAwesomeIcon.Cog, Loc.T("设置")))
            _selectTab = 4;
    }

    private (FontAwesomeIcon, Vector4, string) BackendState()
    {
        if (_health is { } h)
        {
            if (h.Model.Error is { } err) return (FontAwesomeIcon.Server, Red, Loc.F("后端在线，模型加载失败：{0}", err));
            if (_backendOutdated)
                return (FontAwesomeIcon.Server, Yellow, Loc.F("后端在线 v{0}：有新版本 {1}，到设置页更新后端", h.Version, BackendUpdater.PluginTag));
            if (h.Model.Loading) return (FontAwesomeIcon.Server, Yellow, Loc.T("后端在线，模型加载中…"));
            var release = _config.ReleaseAfter switch { 0 => Loc.T("识别后立即释放"), < 0 => Loc.T("常驻"), var s => Loc.F("空闲 {0} 秒后释放", s) };
            return (FontAwesomeIcon.Server, Green, Loc.F("后端在线 v{0}\n", h.Version) + (h.Model.Loaded
                ? Loc.F("模型已加载：{0} {1}（{2}）", DeviceLabel(h.Model.Device), h.Model.Precision, h.Model.DeviceReason)
                : Loc.T("模型未加载（识别时自动加载）")) + Loc.F("\n设备设置：{0}，{1}", DeviceLabel(_config.BackendDevice), release));
        }
        if (_process.IsRunning) return (FontAwesomeIcon.Server, Yellow, Loc.T("后端启动中…"));
        var exit = _process.ExitCode is { } code ? Loc.F("\n上次启动的后端已退出（代码 {0}），见设置页日志", code) : "";
        return (FontAwesomeIcon.Server, Grey, Loc.T("后端离线") + exit);
    }

    private static string DeviceLabel(string? device) => device switch
    {
        "cuda" => "GPU",
        "cpu" => "CPU",
        "auto" => Loc.T("自动"),
        _ => device ?? "",
    };

    /// <summary>一键包用户：下载与插件同版本的后端源码替换 backend\src（见 BackendUpdater）。</summary>
    private void UpdateBackend()
    {
        _ = Task.Run(async () =>
        {
            var error = await _updater.UpdateAsync(_config.BackendDir, _config.UpdateProxy, StopBackendAndWaitAsync, StartAndCheckAsync);
            _lastVersionCheck = DateTime.MinValue;
            if (error is null) SetStatus(Loc.F("后端已更新到 {0}", BackendUpdater.PluginTag), Green);
            else SetError(error);
        });
    }

    private async Task StopBackendAndWaitAsync()
    {
        if (_process.IsRunning) _process.Stop();
        else
        {
            try { await _client.ShutdownAsync(); } catch { }
        }
        for (var i = 0; i < 20; i++)
        {
            try { await _client.HealthAsync(); }
            catch { break; }
            await Task.Delay(500);
        }
        _health = null;
    }

    /// <summary>启动后端并等它响应；version 非空时要求版本一致（确认运行的是新代码）。</summary>
    private async Task<bool> StartAndCheckAsync(string version)
    {
        if (BackendProcess.Validate(_config) is not null) return false;
        _process.Start(_config);
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(1000);
            try
            {
                if (await _client.HealthAsync() is { } h && (version.Length == 0 || h.Version == version))
                {
                    _health = h;
                    return true;
                }
            }
            catch { }
            if (!_process.IsRunning) return false;
        }
        return false;
    }

    private void StartBackend()
    {
        try
        {
            _process.Start(_config);
            SetStatus(Loc.T("后端启动中…"));
        }
        catch (Exception ex)
        {
            SetError(Loc.F("后端启动失败：{0}", ex.Message));
        }
    }

    private void StopBackend()
    {
        if (_process.IsRunning) _process.Stop();
        else _ = Run(_client.ShutdownAsync);
        _health = null;
        SetStatus(Loc.T("后端已停止"));
    }

    private Task Run(Func<Task> action) => Task.Run(async () =>
    {
        try { await action(); }
        catch (Exception ex) { SetError(ex.Message); }
    });

    // ------------------------------------------------------------------ 识别与重定向

    /// <summary>图库双击 / 拖入主窗口：打开一张图（识别过的从缓存恢复，不重跑模型）。</summary>
    public void OpenImage(string path)
    {
        IsOpen = true;
        _selectTab = 0;
        Recognize(path);
    }

    private void RecognizeClipboard()
    {
        if (_recognizing || _clipboardTask is not null) return;
        if (!BackendOnline)
        {
            SetError(Loc.T("后端离线：先点右上角电源按钮启动"));
            return;
        }
        SetStatus(Loc.T("正在读取剪切板图像…"), Yellow);
        _clipboardTask = ClipboardImage.ReadPngAsync(_textures, _readback, _clipboardLifetime.Token);
    }

    private void PollClipboard()
    {
        if (_clipboardTask is not { IsCompleted: true } task) return;
        _clipboardTask = null;
        try
        {
            if (task.GetAwaiter().GetResult() is not { } png)
            {
                SetError(Loc.T("剪切板里没有图片"));
                return;
            }
            // Share the library's content deduplication and refresh its existing view.
            var path = Library.SaveClipboard(png, Library.Root, Library.Root, out var duplicate);
            LibraryChanged?.Invoke();
            _selectTab = 0;
            Recognize(path, note: duplicate ? "；" + Loc.F("图库里已有这张图：{0}", Path.GetRelativePath(Library.Root, path)) : null);
        }
        catch (OperationCanceledException) when (_clipboardLifetime.IsCancellationRequested) { }
        catch (Exception ex) { SetError(Loc.F("读取剪切板失败：{0}", ex.Message)); }
    }

    /// <summary>force：忽略缓存重新识别（「重新识别」按钮）。</summary>
    /// <summary>note：识别完成时附在状态后面的提示。</summary>
    private void Recognize(string path, bool force = false, string? note = null)
    {
        if (_recognizing || _clipboardTask is not null) return;
        if (!BackendOnline)
        {
            SetError(Loc.T("后端离线：先点右上角电源按钮启动"));
            return;
        }
        _recognizing = true;
        _imagePath = path;
        var cached = !force && Library.KnownHash(path) is { } known && Library.HasCache(known);
        SetStatus(cached ? Loc.T("读取识别缓存…") : ModelReady ? Loc.T("识别中…") : Loc.T("加载模型并识别中（约 10~20 秒）…"), Yellow);
        _ = Task.Run(async () =>
        {
            try
            {
                var old = _session;
                var (info, sha1) = await Recognition.OpenAsync(path, force);
                var png = await _client.SessionImageAsync(info.SessionId);
                var tex = await _textures.CreateFromImageAsync(png);
                Interlocked.Exchange(ref _newTexture, tex)?.Dispose();
                _session = info;
                _imageSha1 = sha1;
                _person = 0;
                _result = null;
                // 这张图在 3D 页保存过修正就恢复；否则用预设（手动偏移只对当前图有意义，不带过来）
                var fix = Library.ReadCorrection(sha1);
                if (fix is not null) _preset = fix.Preset;
                _params = fix?.Params.Clone() ?? LoadParams(_preset);
                _correctionLoaded = fix is not null;
                _selected = null;
                if (old is not null) _ = _client.DeleteSessionAsync(old.SessionId).ContinueWith(_ => { });
                SetStatus((info.Device is null
                              ? Loc.T("已从识别缓存读取")
                              : Loc.F("识别完成（{0} {1:0.0} 秒）", DeviceLabel(info.Device), info.InferenceMs / 1000f))
                          + (fix is not null ? Loc.T("，已恢复保存的修正") : "")
                          + (InLibrary ? "" : Loc.T("；未存入图库（工具栏的文件夹 + 按钮可存入）")) + (note ?? ""), Green);
                RequestRetarget(_config.PushAfterRecognize);
            }
            catch (Exception ex)
            {
                SetError(Loc.F("识别失败：{0}", ex.Message));
            }
            finally
            {
                _recognizing = false;
            }
        });
    }

    /// <summary>预设的值：有保存过的用保存的，否则用内置预设。</summary>
    private RetargetParams LoadParams(string preset)
    {
        if (!_config.SavedParams.TryGetValue(preset, out var saved)) return RetargetParams.Preset(preset);
        var p = saved.Clone();
        p.Offsets.Clear();
        return p;
    }

    private static string ParamsSnapshot(RetargetParams p)
    {
        var c = p.Clone();
        c.Offsets.Clear();
        return System.Text.Json.JsonSerializer.Serialize(c);
    }

    /// <summary>调整过的值按预设保存。拖动滑杆时不写，松手后再写。用的是图片自己保存的修正时不写（那是这张图专用的）。</summary>
    private void PersistParams()
    {
        if (!_paramsDirty || ImGui.IsAnyItemActive()) return;
        _paramsDirty = false;
        if (_correctionLoaded) return;
        var snapshot = ParamsSnapshot(_params);
        if (snapshot == _savedParams) return;
        var saved = _params.Clone();
        saved.Offsets.Clear();
        _config.SavedParams[_preset] = saved;
        _pi.SavePluginConfig(_config);
        _savedParams = snapshot;
    }

    private void RequestRetarget(bool push)
    {
        _paramsDirty = true;
        if (_session is null) return;
        _rtPush |= push;
        if (_rtBusy)
        {
            _rtDirty = true;
            return;
        }
        _rtBusy = true;
        _ = Task.Run(RetargetLoop);
    }

    private async Task RetargetLoop()
    {
        var restored = false;
        try
        {
            do
            {
                _rtDirty = false;
                var push = _rtPush;
                _rtPush = false;
                var session = _session!;
                if (push && _resetPending)
                {
                    _resetPending = false;
                    await ClearBrioPoseAsync();
                }
                var reference = await ReferenceAsync();
                var trust = _referenceSignature is { } sig && _cleanSignatures.Contains(sig);
                RetargetResult? result = null;
                try
                {
                    result = await _client.RetargetAsync(session.SessionId, _person, _params.Clone(), reference,
                                                         _raceOverride ?? _detectedRace, trust);
                }
                catch (BackendException ex) when (ex.StatusCode == 404 && !restored && _imageSha1 is not null)
                {
                }
                if (result is null)
                {
                    // 会话没了（后端重启过，或被批量识别挤出）：从识别缓存恢复后再算
                    restored = true;
                    if (await Recognition.RestoreAsync(_imageSha1!, session.Name) is not { } info)
                        throw new InvalidOperationException(Loc.T("会话已失效且没有识别缓存，请重新识别"));
                    if (session == _session) _session = info;
                    _rtPush |= push;
                    _rtDirty = true;
                    continue;
                }
                if (session != _session) continue;
                // 游戏骨长不对（换种族前残留的 Brio 姿势）：先清除再算一次；清除后仍不符就是 Customize+ / 改体型 MOD
                if (push && result.ReferenceStale && !trust && await ClearBrioPoseAsync())
                {
                    _rtPush = true;
                    _rtDirty = true;
                    continue;
                }
                _result = result;
                if (push) await PushAsync(result.Pose);
            } while (_rtDirty || _rtPush);
        }
        catch (Exception ex)
        {
            SetError(Loc.F("重定向失败：{0}", ex.Message));
        }
        finally
        {
            _rtBusy = false;
        }
    }

    /// <summary>清除 Brio 姿势栈后重新读取参考姿势；这个身份之后的骨长按游戏实际值计算。</summary>
    private async Task<bool> ClearBrioPoseAsync()
    {
        if (!await _applier.ResetPoseAsync(_actor)) return false;
        _reference = null;
        if (await _applier.IdentifyAsync(_actor) is { } id)
        {
            _cleanSignatures.Add(id.Signature);
            _backups.Remove(id.Signature);   // 清除前读到的是残留的错误姿势，不能拿来还原
        }
        _pushStatus ??= Loc.T("已清除换种族前残留的 Brio 姿势并重新应用");
        return true;
    }

    /// <summary>游戏内角色当前姿势：骨长/缩放按游戏实际值（种族、Customize+）。每次计算都重新读取，每个身份的第一份留作还原备份。</summary>
    private async Task<string?> ReferenceAsync()
    {
        var id = await _applier.IdentifyAsync(_actor);
        if (id is null) return null;
        _detectedRace = id.Race;
        // 不缓存：用户在 Brio 里重置 / 手动改了姿势后身份不变，缓存的旧姿势会让骨长一直是错的。
        // 读回自己上次推送的姿势没问题（骨长不变，n_root 下的离地高度后端不取游戏值）
        var pose = await _applier.GetPoseAsync(_actor);
        if (pose is null) return null;
        _reference = pose;
        _referenceSignature = id.Signature;
        if (id.Key == _actorKey) _actorIvcs = HasIvcs(pose);   // 换了外貌（种族）后 IVCS 可能不同，跟着更新
        _backups.TryAdd(id.Signature, pose);
        return pose;
    }

    private async Task PushAsync(string pose)
    {
        if (!_clientState.IsGPosing)
        {
            SetStatus(Loc.T("不在 GPose：只更新了预览"), Yellow);
            return;
        }
        var status = _pushStatus ?? Loc.T("已应用到角色");
        _pushStatus = null;
        var error = await _applier.ApplyAsync(_actor, pose);
        if (error is null)
        {
            _pushedIndex = (await _applier.IdentifyAsync(_actor))?.Index;
            SetStatus(status, Green);
        }
        else SetError(error);
    }

    private void ApplyNow()
    {
        // 结果是按残留姿势算的、或刚换过种族：重新计算（会先清除 Brio 残留姿势）
        if (_result is { ReferenceStale: false } r && !_resetPending) _ = Run(() => PushAsync(r.Pose));
        else RequestRetarget(true);
    }

    private void Restore()
    {
        if (_currentSignature is null || !_backups.TryGetValue(_currentSignature, out var backup)) return;
        _ = Run(async () =>
        {
            var error = await _applier.ApplyAsync(_actor, backup);
            if (error is null) SetStatus(Loc.T("已还原为第一次应用前的姿势"), Green);
            else SetError(error);
        });
    }

    private void ReplicateCamera()
    {
        if (_session is null) return;
        if (!_server.IsRunning)
        {
            SetError(Loc.T("复刻镜头需要 Posegraph HTTP 服务（/posegraph on）"));
            return;
        }
        SetStatus(Loc.T("复刻镜头中（约 10 秒，画面会跳动）…"), Yellow);
        _ = Run(async () =>
        {
            var r = await _client.CameraAsync(_session.SessionId, _person, _params.Clone(), _actor, _raceOverride ?? _detectedRace);
            SetStatus(Loc.F("镜头已复刻：机位误差 {0:0.0}cm，构图偏差 {1:0.0}%", r.PositionErrorCm, r.FramingErrorPct), Green);
        });
    }

    private void Export()
    {
        if (_result is not { } r) return;
        var name = _session?.Name ?? "pose";
        var dir = Directory.Exists(_config.ExportDir) ? _config.ExportDir : null;
        _fileDialog.SaveFileDialog(Loc.T("导出 Brio 姿势"), ".pose", name, ".pose", (ok, path) =>
        {
            if (!ok) return;
            try
            {
                File.WriteAllText(path, r.Pose);
                _config.ExportDir = Path.GetDirectoryName(path) ?? "";
                _pi.SavePluginConfig(_config);
                SetStatus(Loc.F("已导出 {0}", Path.GetFileName(path)), Green);
            }
            catch (Exception ex)
            {
                SetError(Loc.F("导出失败：{0}", ex.Message));
            }
        }, dir, true);
    }

    /// <summary>当前结果存到 Brio 姿势库（骨长按游戏里的角色，和应用到角色的一样）。</summary>
    private void SaveCurrentToBrio()
    {
        if (_result is not { } r || _imagePath is not { } image) return;
        _ = Run(async () =>
        {
            var preview = await TextureImages.ThumbnailPngAsync(_textures, _readback, await File.ReadAllBytesAsync(image), 256);
            var path = BrioPoseLibrary.Save(r.Pose, Path.GetFileNameWithoutExtension(image), Library.Meta(image)?.Tags ?? [],
                                            preview, Path.GetFileName(image));
            SetStatus(Loc.F("已存到 Brio 姿势库：{0}", Path.GetFileName(path)), Green);
        });
    }

    /// <summary>
    /// 图库里批量存到 Brio 姿势库：用当前的调整参数（不含手动偏移）和种族计算，不读游戏姿势（骨长用标准骨长）。
    /// 没识别过的先识别。返回保存的路径。
    /// </summary>
    public async Task<string> SaveToBrioAsync(string image)
    {
        var (info, _) = await Recognition.OpenAsync(image);
        try
        {
            var p = _params.Clone();
            p.Offsets.Clear();
            var r = await _client.RetargetAsync(info.SessionId, 0, p, null, _raceOverride ?? _detectedRace);
            var preview = await TextureImages.ThumbnailPngAsync(_textures, _readback, await File.ReadAllBytesAsync(image), 256);
            return BrioPoseLibrary.Save(r.Pose, Path.GetFileNameWithoutExtension(image), Library.Meta(image)?.Tags ?? [],
                                        preview, Path.GetFileName(image));
        }
        finally
        {
            await _client.DeleteSessionAsync(info.SessionId);
        }
    }

    /// <summary>拖到主窗口的图片 / 文件夹：复制进图库当前文件夹，打开第一张，其余加入批量识别。</summary>
    private void OpenDropped(IReadOnlyCollection<string> files, IReadOnlyCollection<string> dirs)
    {
        var images = files.Where(ImageLibrary.IsImage).ToList();
        if (images.Count == 0)
        {
            SetError(dirs.Count > 0 ? Loc.T("文件夹请拖到图库窗口里导入") : Loc.T("不支持的文件格式"));
            return;
        }
        Recognize(images[0], note: images.Count > 1 || dirs.Count > 0 ? Loc.T("；只打开了第一张，多张图片或文件夹请拖到图库窗口") : null);
    }

    /// <summary>当前图片在图库（自带图库或某个来源）里。</summary>
    private bool InLibrary => _imagePath is { } p && Library.SourceRootOf(p, _config.LibrarySources) is not null;

    /// <summary>用户在 3D / 调整页改过：有手动偏移，或参数与预设保存值不同。</summary>
    private bool HasUserCorrection()
        => _params.Offsets.Any(kv => kv.Value.Any(v => v != 0)) || ParamsSnapshot(_params) != ParamsSnapshot(LoadParams(_preset));

    /// <summary>图库内容变了（存入了新图），由 Plugin 接到图库窗口的刷新。</summary>
    public Action? LibraryChanged { get; set; }

    /// <summary>
    /// 把当前图片存进图库当前文件夹（按内容查重，已有就不再复制），同时把 3D / 调整页的修正存进它的识别缓存，
    /// 下次从图库打开自动恢复。
    /// </summary>
    private void SaveCurrentToLibrary()
    {
        if (_imagePath is not { } image || _imageSha1 is not { } sha1) return;
        var folder = Directory.Exists(_config.LibraryFolder) ? _config.LibraryFolder : Library.Root;
        var root = Library.SourceRootOf(folder, _config.LibrarySources) ?? Library.Root;
        var correction = HasUserCorrection() ? new SavedCorrection(_preset, _params.Clone(), DateTime.Now) : null;
        _ = Run(async () =>
        {
            var r = await Task.Run(() => Library.Import([image], folder, root));
            var saved = r.Added.FirstOrDefault() ?? r.Duplicates.Select(d => d.Existing).FirstOrDefault();
            if (saved is null)
            {
                SetError(Loc.T("存入图库失败"));
                return;
            }
            if (correction is not null)
            {
                Library.WriteCorrection(sha1, correction);
                _correctionLoaded = true;
            }
            if (_imagePath == image) _imagePath = saved;
            var rel = Path.GetRelativePath(root, saved);
            var fix = correction is not null ? Loc.T("（含 3D 修正）") : "";
            SetStatus(r.Added.Count > 0 ? Loc.F("已存入图库：{0}{1}", rel, fix) : Loc.F("图库里已有这张图：{0}{1}", rel, fix), Green);
            LibraryChanged?.Invoke();
        });
    }

    // ------------------------------------------------------------------ 绘制

    public override void Draw()
    {
        PollClipboard();
        if (Interlocked.Exchange(ref _newTexture, null) is { } tex)
        {
            _texture?.Dispose();
            _texture = tex;
        }
        PollHealth();
        RefreshActors();
        PersistParams();
        DragSource();

        DrawStatusBar();
        DrawToolbar();

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing();
        if (ImGui.BeginTabBar("##tabs"))
        {
            Tab(0, Loc.T("图像"), DrawImageTab, footer);
            Tab(1, "3D", DrawStickTab, footer);
            Tab(2, Loc.T("调整"), DrawTuningTab, footer);
            var warnings = _result?.Warnings.Length ?? 0;
            Tab(3, warnings > 0 ? Loc.F("提示 ({0})###warn", warnings) : Loc.T("提示###warn"), DrawWarningsTab, footer);
            Tab(4, Loc.T("设置"), DrawSettingsTab, footer);
            _selectTab = null;
            ImGui.EndTabBar();
        }
        DrawFooter();
        _fileDialog.Draw();
    }

    private void Tab(int index, string label, Action draw, float footer)
    {
        if (!ImGui.BeginTabItem(label, _selectTab == index ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None)) return;
        using (var child = ImRaii.Child($"##tab{index}", new Vector2(0, -footer)))
            if (child) draw();
        ImGui.EndTabItem();
    }

    private void DrawToolbar()
    {
        if (IconButton("##open", FontAwesomeIcon.Images, Loc.T("图库：导入、分类、批量识别（双击图片打开到这里），也可以把图片拖到图像页")))
            ToggleLibrary?.Invoke();
        ImGui.SameLine();
        if (IconButton("##clipboard", FontAwesomeIcon.Clipboard, Loc.T("识别剪切板中的图像"), !_recognizing && _clipboardTask is null))
            RecognizeClipboard();
        ImGui.SameLine();
        if (IconButton("##again", FontAwesomeIcon.Redo, Loc.T("重新识别当前图片（不用识别缓存）"),
                       _imagePath is not null && !_recognizing && _clipboardTask is null))
            Recognize(_imagePath!, true);
        ImGui.SameLine();
        var canSave = _session is not null && _imageSha1 is not null && !InLibrary && !_recognizing;
        using (ImRaii.PushColor(ImGuiCol.Text, Yellow, canSave))
            if (IconButton("##saveLib", FontAwesomeIcon.FolderPlus,
                           InLibrary ? Loc.T("这张图已在图库里（修正在 3D 页保存）")
                                     : Loc.T("存入图库：复制到图库当前文件夹（按内容查重），并带上 3D / 调整页的修正"), canSave))
                SaveCurrentToLibrary();
        ImGui.SameLine();

        // 角色
        var label = _actor is { } actor ? ActorName(actor) : Loc.T("GPose 目标");
        ImGui.SetNextItemWidth(ComboWidth(label, 140));
        if (ImGui.BeginCombo("##actor", label))
        {
            if (ImGui.Selectable(Loc.T("GPose 目标"), _actor is null)) SelectActor(null);
            foreach (var (index, name) in _actors)
                if (ImGui.Selectable($"{name}##{index}", _actor == index)) SelectActor(index);
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("要摆姿势的角色"));
        ImGui.SameLine();

        // 多人（预留）：目前整图按一个人识别
        var humans = _session?.Humans.Length ?? 0;
        using (ImRaii.Disabled(humans <= 1))
        {
            var personLabel = humans <= 1 ? Loc.T("1 人") : Loc.F("第 {0} 人", _person + 1);
            ImGui.SetNextItemWidth(ComboWidth(personLabel, 70));
            if (ImGui.BeginCombo("##person", personLabel))
            {
                for (var i = 0; i < humans; i++)
                    if (ImGui.Selectable(Loc.F("第 {0} 人", i + 1), _person == i))
                    {
                        _person = i;
                        RequestRetarget(false);
                    }
                ImGui.EndCombo();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("图中人物（多人识别预留，目前整图按一个人处理）"));
        ImGui.SameLine();
        DrawRaceCombo();
        ImGui.SameLine();
        IconButton("##video", FontAwesomeIcon.Film, Loc.T("视频逐帧（预留）"), false);
        if (_recognizing || _rtBusy)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, _recognizing ? Loc.T("识别中…") : Loc.T("计算中…"));
        }
    }

    /// <summary>下拉框宽度：放得下当前文字（加箭头和内边距），不小于 min，也不超过剩余宽度。</summary>
    private static float ComboWidth(string text, float min)
    {
        var style = ImGui.GetStyle();
        var want = ImGui.CalcTextSize(text).X + ImGui.GetFrameHeight() + style.FramePadding.X * 2 + 4;
        return Math.Min(Math.Max(min * ImGuiHelpers.GlobalScale, want), Math.Max(60, ImGui.GetContentRegionAvail().X));
    }

    /// <summary>下一帧刷新角色时按新角色切换状态并重新计算（见 SwitchActor）。</summary>
    private void SelectActor(int? index)
    {
        _actor = index;
        _lastActors = DateTime.MinValue;
    }

    /// <summary>种族骨架：默认按角色外貌自动识别，可手动指定（换种族 MOD、识别不到时）。</summary>
    private void DrawRaceCombo()
    {
        var used = _result?.Race;
        var label = _raceOverride is not null ? Races.Label(_raceOverride)
            : used is not null ? used.Name : _detectedRace is not null ? Races.Label(_detectedRace) : Loc.T("种族");
        var warn = used is { Warnings.Length: > 0 };
        var raceLabel = warn ? label + " (!)" : label;
        ImGui.SetNextItemWidth(ComboWidth(raceLabel, 130));
        if (ImGui.BeginCombo("##race", raceLabel))
        {
            if (ImGui.Selectable(Loc.F("自动（{0}）", (_detectedRace is null ? Loc.T("按骨长推断") : Races.Label(_detectedRace))), _raceOverride is null))
                SelectRace(null);
            string? group = null;
            foreach (var r in Races.All)
            {
                if (r.Group != group)
                {
                    ImGui.TextDisabled(Loc.T(r.Group));
                    group = r.Group;
                }
                if (ImGui.Selectable(Loc.F("  {0}{1}##{2}", Loc.T(r.Name), !r.Supported ? Loc.T("（未适配）") : "", r.Code), _raceOverride == r.Code))
                    SelectRace(r.Code);
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
        {
            var source = used?.Source switch
            {
                "plugin" => _raceOverride is null ? Loc.T("按角色外貌识别") : Loc.T("手动指定"),
                "guess" => Loc.T("按游戏内骨长推断"),
                "default" => Loc.T("未能识别，使用默认"),
                _ => Loc.T("尚未计算"),
            };
            var tip = used is null ? Loc.T("种族骨架（应用姿势前自动识别）")
                : Loc.F("骨架：{0}（{1}，{2}），{3}", used.Name, used.Code, used.Group, source) + (used.Warnings.Length > 0 ? "\n" + string.Join("\n", used.Warnings) : "");
            ImGui.SetTooltip(tip);
        }
    }

    private void SelectRace(string? code)
    {
        _raceOverride = code;
        RequestRetarget(_config.PushOnRelease);
    }

    private void DrawFooter()
    {
        var hasResult = _result is not null;
        if (IconButton("##apply", FontAwesomeIcon.Play, Loc.T("应用到角色"), hasResult && _clientState.IsGPosing))
            ApplyNow();
        ImGui.SameLine();
        if (IconButton("##camera", FontAwesomeIcon.Video, Loc.T("复刻照片镜头（会改动 GPose 相机）"), _session is not null && _clientState.IsGPosing))
            ReplicateCamera();
        ImGui.SameLine();
        if (IconButton("##restore", FontAwesomeIcon.Undo, Loc.T("还原为第一次应用前的姿势"), _currentSignature is not null && _backups.ContainsKey(_currentSignature) && _clientState.IsGPosing))
            Restore();
        ImGui.SameLine();
        if (IconButton("##export", FontAwesomeIcon.Save, Loc.T("导出 Brio 姿势文件（.pose）"), hasResult))
            ImGui.OpenPopup("##exportMenu");
        if (ImGui.BeginPopup("##exportMenu"))
        {
            if (ImGui.MenuItem(Loc.T("导出 .pose 文件…"))) Export();
            if (ImGui.MenuItem(Loc.T("存到 Brio 姿势库"), _imagePath is not null)) SaveCurrentToBrio();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.F("带预览图和标签保存到 {0}，在 Brio 资料库里直接能用", BrioPoseLibrary.Folder));
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        var autoPush = _config.PushOnRelease;
        if (ImGui.Checkbox("##autopush", ref autoPush))
        {
            _config.PushOnRelease = autoPush;
            _pi.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("松开滑杆时自动应用到角色"));

        ImGui.TextColored(_statusColor, _statusText);
        if (ImGui.IsItemHovered() && _statusText.Length > 30) ImGui.SetTooltip(_statusText);
    }

    // ---------------- 图像页

    private void DrawImageTab()
    {
        var showMhr = _config.ShowMhrOverlay;
        var showFf = _config.ShowFfxivOverlay;
        var toggled = ImGui.Checkbox(Loc.T("识别骨架（绿）"), ref showMhr);
        ImGui.SameLine();
        toggled |= ImGui.Checkbox(Loc.T("FF14 骨架（橙）"), ref showFf);
        if (toggled)
        {
            _config.ShowMhrOverlay = showMhr;
            _config.ShowFfxivOverlay = showFf;
            _pi.SavePluginConfig(_config);
        }

        var avail = ImGui.GetContentRegionAvail();
        if (_texture is null || _session is null)
        {
            ImGui.Button(_recognizing ? Loc.T("识别中…") : Loc.T("点左上角图库按钮选图\n或把图片 / 文件夹拖到这里"), new Vector2(avail.X, Math.Max(80, avail.Y)));
            DropTarget();
            return;
        }
        var imgW = _session.ImageSize[0];
        var imgH = _session.ImageSize[1];
        var scale = Math.Min(avail.X / imgW, avail.Y / imgH);
        var size = new Vector2(imgW, imgH) * scale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail.X - size.X) / 2);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Image(_texture.Handle, size);
        // Image 没有 ID，不能直接作拖放目标；在同一位置放一个隐形按钮接收
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##imageDrop", size);
        DropTarget();

        if (_result?.Overlay is not { } ov) return;
        var dl = ImGui.GetWindowDrawList();
        if (showMhr) DrawSegments(dl, ov.Mhr, origin, scale, MhrGreen);
        if (showFf) DrawSegments(dl, ov.Ffxiv, origin, scale, Orange);
    }

    /// <summary>左侧实线，右侧细线加端点（检查左右是否颠倒），与 preview_overlay.py 一致。</summary>
    private static void DrawSegments(ImDrawListPtr dl, Segment[] segments, Vector2 origin, float scale, Vector4 color)
    {
        var col = ImGui.GetColorU32(color);
        foreach (var s in segments)
        {
            var a = origin + new Vector2(s.A[0], s.A[1]) * scale;
            var b = origin + new Vector2(s.B[0], s.B[1]) * scale;
            var right = s.Side == "r";
            dl.AddLine(a, b, col, right ? 1.5f : 2.5f);
            if (right) dl.AddCircleFilled(b, 2.5f, col);
        }
    }

    private const string DragLabel = LibraryWindow.ExternalDragLabel;

    private static bool IsImage(string path) => ImageLibrary.IsImage(path);

    /// <summary>
    /// 外部文件拖放分两步：每帧用 CreateImGuiSource 把系统拖动转成 ImGui 拖放负载，再由目标区域用 CreateImGuiTarget 接收。
    /// 拖着图片进入窗口时自动切到图像页。
    /// </summary>
    /// <summary>
    /// 外部文件拖放分两步：Plugin 每帧用 CreateImGuiSource 把系统拖动转成 ImGui 拖放负载（见 Plugin.DrawDragSource），
    /// 再由目标区域用 CreateImGuiTarget 接收。拖着图片进入窗口时自动切到图像页。
    /// </summary>
    private void DragSource()
    {
        if (_dragDrop.IsDragging && (_dragDrop.Files.Any(IsImage) || _dragDrop.Directories.Count > 0))
            _selectTab = 0;
    }

    private void DropTarget()
    {
        if (_dragDrop.CreateImGuiTarget(DragLabel, out var files, out var dirs))
            OpenDropped(files, dirs);
    }

    // ---------------- 3D 火柴人

    private static readonly Dictionary<string, string> JointNames = new()
    {
        ["j_kosi"] = "腰", ["j_sebo_a"] = "腰椎", ["j_sebo_b"] = "上背", ["j_sebo_c"] = "胸", ["j_kubi"] = "颈", ["j_kao"] = "头",
        ["j_sako"] = "锁骨", ["j_ude_a"] = "肩（上臂）", ["j_ude_b"] = "肘（前臂）", ["j_te"] = "手腕", ["j_naka_a"] = "手",
        ["j_asi_a"] = "髋（大腿）", ["j_asi_c"] = "膝（小腿）", ["j_asi_d"] = "脚踝", ["j_asi_e"] = "脚尖",
    };

    private static string JointLabel(string bone)
    {
        var side = bone.EndsWith("_l") ? Loc.T("左") : bone.EndsWith("_r") ? Loc.T("右") : "";
        var key = side.Length > 0 ? bone[..^2] : bone;
        return side + (JointNames.TryGetValue(key, out var n) ? Loc.T(n) : bone);
    }

    /// <summary>把当前的修正（预设、幅度、手动关节偏移等调整参数）存进这张图的识别缓存，下次打开这张图自动恢复。</summary>
    private void DrawCorrectionBar()
    {
        var sha1 = _imageSha1;
        var saved = sha1 is not null && Library.HasCorrection(sha1);
        if (IconButton("##saveFix", FontAwesomeIcon.Save, Loc.T("保存修正：把当前的关节偏移和调整参数存进这张图的识别缓存，下次打开自动恢复"), sha1 is not null))
        {
            Library.WriteCorrection(sha1!, new SavedCorrection(_preset, _params.Clone(), DateTime.Now));
            _correctionLoaded = true;
            SetStatus(Loc.T("已保存这张图的修正"), Green);
        }
        ImGui.SameLine();
        if (IconButton("##clearFix", FontAwesomeIcon.Trash, Loc.T("删除这张图保存的修正（当前调整不变）"), saved))
        {
            Library.DeleteCorrection(sha1!);
            _correctionLoaded = false;
            SetStatus(Loc.T("已删除这张图保存的修正"), Yellow);
        }
        ImGui.SameLine();
        var offsets = _params.Offsets.Count(kv => kv.Value.Any(v => v != 0));
        ImGui.TextDisabled(saved ? Loc.F("这张图有保存的修正（手动偏移 {0} 处）", offsets) : Loc.F("未保存（手动偏移 {0} 处）", offsets));
    }

    private void DrawStickTab()
    {
        if (_result?.Stick is not { } stick)
        {
            ImGui.TextDisabled(Loc.T("识别后在这里查看 FF14 骨架（拖动旋转、滚轮缩放、点选关节微调）"));
            return;
        }
        DrawCorrectionBar();
        var avail = ImGui.GetContentRegionAvail();
        var panel = _selected is null ? ImGui.GetTextLineHeightWithSpacing() : ImGui.GetFrameHeightWithSpacing() * 4 + 4;
        var size = new Vector2(avail.X, Math.Max(140, avail.Y - panel));
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##stick", size);
        var io = ImGui.GetIO();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3))
        {
            _yaw += io.MouseDelta.X * 0.01f;
            _pitch = Math.Clamp(_pitch + io.MouseDelta.Y * 0.01f, -1.5f, 1.5f);
            _dragged = true;
        }
        if (ImGui.IsItemHovered() && io.MouseWheel != 0)
            _zoom = Math.Clamp(_zoom * (1 + io.MouseWheel * 0.1f), 0.3f, 5f);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("拖动旋转，滚轮缩放，点击关节选中；双击复位视角"));
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            (_yaw, _pitch, _zoom) = (0, 0.15f, 1);

        var joints = stick.Joints.ToDictionary(j => j.Name);
        var center = joints.TryGetValue("j_kosi", out var hip) ? ToVec(hip.Pos) : Vector3.Zero;
        var scale = size.Y / 2.1f * _zoom;
        var mid = origin + size / 2;
        Vector2 Project(Vector3 p)
        {
            p -= center;
            var (sy, cy) = MathF.SinCos(_yaw);
            var (sp, cp) = MathF.SinCos(_pitch);
            var x = p.X * cy + p.Z * sy;
            var z = -p.X * sy + p.Z * cy;
            var y = p.Y * cp - z * sp;
            // 从角色正前方看：模型 +x（角色左侧）在屏幕右侧
            return mid + new Vector2(x, -y) * scale;
        }

        if (ImGui.IsItemDeactivated())
        {
            if (!_dragged)
            {
                var mouse = ImGui.GetMousePos();
                var hit = stick.Joints.Select(j => (j.Name, D: Vector2.Distance(Project(ToVec(j.Pos)), mouse)))
                    .Where(t => t.D < 10).OrderBy(t => t.D).Select(t => t.Name).FirstOrDefault();
                _selected = hit;
            }
            _dragged = false;
        }

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(origin, origin + size, true);
        dl.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 0.85f)), 4);

        // 地面网格与朝向
        var floor = stick.Joints.Min(j => j.Pos[1]);
        var grid = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.12f));
        for (var i = -3; i <= 3; i++)
        {
            var t = i * 0.25f;
            dl.AddLine(Project(new Vector3(center.X + t, floor, center.Z - 0.75f)), Project(new Vector3(center.X + t, floor, center.Z + 0.75f)), grid);
            dl.AddLine(Project(new Vector3(center.X - 0.75f, floor, center.Z + t)), Project(new Vector3(center.X + 0.75f, floor, center.Z + t)), grid);
        }
        var front = Project(new Vector3(center.X, floor, center.Z + 0.6f));
        dl.AddLine(Project(new Vector3(center.X, floor, center.Z)), front, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.35f)), 2);
        dl.AddText(front, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.5f)), Loc.T("前"));

        if (_config.ShowMhrInStick)
        {
            var mc = ImGui.GetColorU32(MhrGreen with { W = 0.6f });
            foreach (var s in stick.Mhr)
                dl.AddLine(Project(new Vector3(s.A[0], s.A[1], s.A[2])), Project(new Vector3(s.B[0], s.B[1], s.B[2])), mc, 1.5f);
        }

        var severity = _result.Severity;
        foreach (var j in stick.Joints)
        {
            if (j.Parent is null || !joints.TryGetValue(j.Parent, out var parent)) continue;
            var color = j.Name.EndsWith("_l") ? Orange : j.Name.EndsWith("_r") ? new Vector4(0.35f, 0.65f, 1f, 1) : new Vector4(0.9f, 0.9f, 0.9f, 1);
            dl.AddLine(Project(ToVec(parent.Pos)), Project(ToVec(j.Pos)), ImGui.GetColorU32(color), 3);
        }
        foreach (var j in stick.Joints)
        {
            var p = Project(ToVec(j.Pos));
            var level = severity.GetValueOrDefault(j.Name);
            var color = level == "bad" ? Red : level == "warn" ? Yellow : new Vector4(0.85f, 0.85f, 0.85f, 1);
            var radius = level is null ? 3f : 5f;
            if (_params.Offsets.TryGetValue(j.Name, out var off) && off.Any(v => v != 0))
                dl.AddCircle(p, radius + 3, ImGui.GetColorU32(new Vector4(0.7f, 0.5f, 1f, 1)), 0, 1.5f);
            dl.AddCircleFilled(p, radius, ImGui.GetColorU32(color));
            if (j.Name == _selected)
                dl.AddCircle(p, radius + 5, ImGui.GetColorU32(new Vector4(0.3f, 0.9f, 1f, 1)), 0, 2);
        }
        dl.PopClipRect();

        DrawJointPanel();
    }

    private static Vector3 ToVec(float[] p) => new(p[0], p[1], p[2]);

    private void DrawJointPanel()
    {
        if (_selected is not { } bone)
        {
            var mhr = _config.ShowMhrInStick;
            if (ImGui.Checkbox(Loc.T("叠加识别骨架"), ref mhr))
            {
                _config.ShowMhrInStick = mhr;
                _pi.SavePluginConfig(_config);
            }
            ImGui.SameLine();
            ImGui.TextDisabled(Loc.T("黄/红点：超出游戏常见范围"));
            return;
        }
        ImGui.Text($"{JointLabel(bone)}  ");
        ImGui.SameLine();
        ImGui.TextDisabled(bone);
        ImGui.SameLine();
        if (IconButton("##resetJoint", FontAwesomeIcon.UndoAlt, Loc.T("清除这个关节的手动偏移"), _params.Offsets.ContainsKey(bone)))
        {
            _params.Offsets.Remove(bone);
            RequestRetarget(_config.PushOnRelease);
        }
        ImGui.SameLine();
        if (IconButton("##deselect", FontAwesomeIcon.Times, Loc.T("取消选中")))
            _selected = null;
        foreach (var w in _result?.Warnings.Where(w => w.Bone == bone) ?? [])
        {
            ImGui.SameLine();
            ImGui.TextColored(w.Level == "bad" ? Red : Yellow, $"{w.Value:0}°/{w.Limit:0}°");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(w.Message);
        }

        var off = _params.Offsets.TryGetValue(bone, out var v) ? (float[])v.Clone() : new float[3];
        string[] axes = [Loc.T("前后俯仰（绕左右轴）"), Loc.T("水平转（绕竖直轴）"), Loc.T("侧倾（绕前后轴）")];
        var width = (ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X) / 3;
        for (var i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine();
            ImGui.SetNextItemWidth(width);
            var changed = ImGui.SliderFloat($"##off{i}", ref off[i], -90, 90, "%.0f°");
            ItemTip(Loc.F("{0}：绕这个关节转动它下面的整段肢体（模型坐标轴）。右键归零", axes[i]));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                off[i] = 0;
                changed = true;
            }
            if (changed)
            {
                _params.Offsets[bone] = off;
                RequestRetarget(false);
            }
            if (ImGui.IsItemDeactivatedAfterEdit() || (changed && ImGui.IsItemClicked(ImGuiMouseButton.Right)))
                RequestRetarget(_config.PushOnRelease);
        }
    }

    private static void ItemTip(string tip)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    // ---------------- 调整页

    /// <summary>拖动中只刷新预览，松手才应用到角色；右键恢复默认值。</summary>
    private void Slider(string label, ref float value, float min, float max, string format, float def, string tip)
    {
        ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 90));
        var changed = ImGui.SliderFloat(label, ref value, min, max, format);
        ItemTip(tip + Loc.F("\n右键恢复默认（{0:0.##}）", def));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            value = def;
            RequestRetarget(_config.PushOnRelease);
            return;
        }
        if (changed) RequestRetarget(false);
        if (ImGui.IsItemDeactivatedAfterEdit()) RequestRetarget(_config.PushOnRelease);
    }

    private void Toggle(string label, ref bool value, string tip)
    {
        if (ImGui.Checkbox(label, ref value)) RequestRetarget(_config.PushOnRelease);
        ItemTip(tip);
    }

    private void DrawTuningTab()
    {
        ImGui.SetNextItemWidth(110);
        var presetLabel = _preset == "illustration" ? Loc.T("插画") : Loc.T("照片");
        if (ImGui.BeginCombo("##preset", presetLabel))
        {
            foreach (var (key, name) in new[] { ("photo", Loc.T("照片")), ("illustration", Loc.T("插画")) })
                if (ImGui.Selectable(name, _preset == key))
                {
                    _preset = key;
                    var offsets = _params.Offsets;
                    _params = LoadParams(key);
                    _params.Offsets = offsets;
                    _savedParams = ParamsSnapshot(_params);
                    _config.DefaultPreset = key;
                    _pi.SavePluginConfig(_config);
                    RequestRetarget(_config.PushOnRelease);
                }
            ImGui.EndCombo();
        }
        ItemTip(Loc.T("预设：插画会把颈/头幅度设为 0.7（游戏实测合适）") + "\n" + Loc.T("调整过的值按预设自动保存，点右边的按钮恢复默认"));
        ImGui.SameLine();
        if (IconButton("##resetAll", FontAwesomeIcon.UndoAlt, Loc.T("全部恢复预设值（含手动偏移）")))
        {
            _params = RetargetParams.Preset(_preset);
            _correctionLoaded = false;
            if (_config.SavedParams.Remove(_preset)) _pi.SavePluginConfig(_config);
            _savedParams = ParamsSnapshot(_params);
            RequestRetarget(_config.PushOnRelease);
        }

        if (ImGui.CollapsingHeader(Loc.T("幅度"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var (key, label, tip) in AmplitudeGroups)
            {
                var v = _params.Amp(key);
                var before = v;
                Slider($"{Loc.T(label)}##amp{key}", ref v, 0, 1.5f, "%.2f", RetargetParams.Preset(_preset).Amp(key),
                       Loc.T(tip) + Loc.T("\n1 = 照片原样，<1 收敛，>1 夸张"));
                if (v != before) _params.Amplitude[key] = v;
            }
        }
        if (ImGui.CollapsingHeader(Loc.T("手部末端补偿")))
        {
            var p = _params;
            var s = p.HandStrength;
            Slider(Loc.T("强度##hand"), ref s, 0, 1, "%.2f", 1, Loc.T("手靠近头/胸/髋时，用 IK 保持手与身体部位的相对位置（两套骨架比例不同，只复制角度会错位）。0 关闭"));
            p.HandStrength = s;
            var near = p.HandNear * 100;
            Slider(Loc.T("完全生效##near"), ref near, 5, 60, "%.0f cm", 20, Loc.T("手腕距部位小于这个距离时完全按部位相对位置放置"));
            p.HandNear = Math.Min(near, p.HandFar * 100 - 1) / 100;
            var far = p.HandFar * 100;
            Slider(Loc.T("不再生效##far"), ref far, 10, 80, "%.0f cm", 40, Loc.T("手腕距部位大于这个距离时只复制角度"));
            p.HandFar = Math.Max(far, p.HandNear * 100 + 1) / 100;
        }
        if (ImGui.CollapsingHeader(Loc.T("站姿")))
        {
            var upright = _params.Upright;
            Toggle(Loc.T("重力校正"), ref upright, Loc.T("照片相机俯仰/侧倾会让人整体歪斜：双脚着地时把重心摆到支撑脚正上方。半身照（脚不在画面内）自动跳过"));
            _params.Upright = upright;
            using (ImRaii.Disabled(!upright))
            {
                var max = _params.UprightMaxDeg;
                Slider(Loc.T("校正上限##upmax"), ref max, 0, 45, "%.0f°", 30, Loc.T("倾斜超过这个角度视为有意的姿势（如倾身），不校正"));
                _params.UprightMaxDeg = max;
            }
            var ground = _params.Ground;
            Toggle(Loc.T("自动贴地"), ref ground, Loc.T("把最低的关节放到地面（跪坐/躺下不悬空）。跳跃、腾空的图请关闭"));
            _params.Ground = ground;
        }
        if (ImGui.CollapsingHeader(Loc.T("辅助骨")))
        {
            var h = _params.HelperStrength;
            Slider(Loc.T("联动强度##helper"), ref h, 0, 1.5f, "%.2f", 1, Loc.T("肘/腕/肩/膝辅助骨按游戏动画采样的联动规则转动，防止关节处蒙皮变形。1 为游戏拟合值"));
            _params.HelperStrength = h;
        }
        if (_result is { } r)
        {
            ImGui.Separator();
            ImGui.TextDisabled(Loc.F("重定向 {0:0} ms  补偿 {1}", r.ElapsedMs, Compact(r.Compensation)));
        }
    }

    private static string Compact(System.Text.Json.JsonElement e)
    {
        var parts = new List<string>();
        if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
        foreach (var p in e.EnumerateObject())
        {
            var text = p.Name switch
            {
                "l" or "r" => Loc.F("{0}手 {1:0}cm", (p.Name == "l" ? Loc.T("左") : Loc.T("右")), p.Value.GetProperty("shift_cm").GetDouble()),
                "upright_deg" => Loc.F("重力 {0:0.0}°", p.Value.GetDouble()),
                "ground_drop_cm" => Loc.F("贴地 {0:0}cm", p.Value.GetDouble()),
                _ => null,
            };
            if (text is not null) parts.Add(text);
        }
        return string.Join(Loc.T("，"), parts);
    }

    // ---------------- 提示页

    private void DrawWarningsTab()
    {
        if (_result is not { } r)
        {
            ImGui.TextDisabled(Loc.T("识别后在这里显示合理性检查"));
            return;
        }
        foreach (var w in r.Race?.Warnings ?? [])
            ImGui.TextColored(Yellow, w);
        if (r.Warnings.Length == 0)
        {
            ImGui.TextColored(Green, Loc.T("没有发现超出游戏常见范围的关节"));
            return;
        }
        for (var i = 0; i < r.Warnings.Length; i++)
        {
            var w = r.Warnings[i];
            var color = w.Level == "bad" ? Red : w.Level == "warn" ? Yellow : Grey;
            StatusIcon(w.Level == "info" ? FontAwesomeIcon.InfoCircle : FontAwesomeIcon.ExclamationTriangle, color,
                       Loc.F("{0}：{1:0.#}（阈值 {2:0.#}）", w.Bone, w.Value, w.Limit));
            ImGui.TextWrapped(Loc.F("{0}：{1}", JointLabel(w.Bone), w.Message));
            ItemTip(Loc.T("点击在 3D 页查看这个关节"));
            if (ImGui.IsItemClicked())
            {
                _selected = w.Bone;
                _selectTab = 1;
            }
            if (w.Group is { } group && w.Suggest is { } suggest && Math.Abs(_params.Amp(group) - suggest) > 0.01f &&
                _params.Amp(group) > suggest)
            {
                var name = Loc.T(AmplitudeGroups.FirstOrDefault(g => g.Key == group).Label ?? group);
                if (ImGui.SmallButton(Loc.F("把{0}幅度设为 {1:0.0#}##sug{2}", name, suggest, i)))
                {
                    _params.Amplitude[group] = suggest;
                    RequestRetarget(_config.PushOnRelease);
                }
            }
            ImGui.Spacing();
        }
    }

    // ---------------- 设置页

    private void DrawSettingsTab()
    {
        var changed = false;
        ImGui.SetNextItemWidth(200);
        var langLabel = Loc.Languages.FirstOrDefault(l => l.Key == _config.Language).Label ?? _config.Language;
        if (ImGui.BeginCombo(Loc.T("语言 / Language"), _config.Language == "auto" ? Loc.T(langLabel) : langLabel))
        {
            foreach (var (key, label) in Loc.Languages)
                if (ImGui.Selectable(key == "auto" ? Loc.T(label) : label, _config.Language == key))
                {
                    _config.Language = key;
                    Loc.Apply(key, _clientState, _pi);
                    changed = true;
                }
            ImGui.EndCombo();
        }
        ItemTip(Loc.T("自动：游戏客户端是中文时用中文，否则看 Dalamud 界面语言，都不是中文则用英文"));

        ImGui.Separator();
        ImGui.TextDisabled(Loc.T("识别后端（backend）"));
        if (ImGui.Button(Loc.T("选择一键包目录…")))
            _fileDialog.OpenFolderDialog(Loc.T("选择一键包解压后的目录"), (ok, path) =>
            {
                if (!ok) return;
                if (!BackendProcess.TryUsePackage(_config, path))
                {
                    SetError(Loc.F("不是 Posegraph 一键包目录（缺少 python\\python.exe 或 backend）：{0}", path));
                    return;
                }
                _pi.SavePluginConfig(_config);
                SetStatus(Loc.T("已设置一键包路径"), Green);
            }, null, true);
        ItemTip(Loc.T("一键包解压后的目录（含 python 与 backend 两个文件夹），会自动填好下面两项"));
        var python = _config.BackendPython;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - 60);
        if (ImGui.InputText("python##py", ref python, 512)) { _config.BackendPython = python.Trim('"'); changed = true; }
        ItemTip(Loc.T(@"后端的 python.exe：一键包里是 <解压目录>\python\python.exe，源码安装是 backend\.venv\Scripts\python.exe"));
        ImGui.SameLine();
        if (IconButton("##browsePy", FontAwesomeIcon.FolderOpen, Loc.T("浏览")))
            _fileDialog.OpenFileDialog(Loc.T("选择 python.exe"), "python{.exe}", (ok, paths) =>
            {
                if (!ok || paths.Count == 0) return;
                _config.BackendPython = paths[0];
                // 常见布局：backend\.venv\Scripts\python.exe
                var guess = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(paths[0])!, "..", ".."));
                if (string.IsNullOrEmpty(_config.BackendDir) && File.Exists(Path.Combine(guess, "src", "posegraph", "app.py")))
                    _config.BackendDir = guess;
                _pi.SavePluginConfig(_config);
            }, 1, null, true);

        var dir = _config.BackendDir;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - 60);
        if (ImGui.InputText(Loc.T("目录##dir"), ref dir, 512)) { _config.BackendDir = dir.Trim('"'); changed = true; }
        ItemTip(Loc.T("backend 目录（含 src\\posegraph）"));
        ImGui.SameLine();
        if (IconButton("##browseDir", FontAwesomeIcon.FolderOpen, Loc.T("浏览")))
            _fileDialog.OpenFolderDialog(Loc.T("选择 backend 目录"), (ok, path) =>
            {
                if (!ok) return;
                _config.BackendDir = path;
                _pi.SavePluginConfig(_config);
            }, null, true);

        var problem = BackendProcess.Validate(_config);
        if (problem is null) ImGui.TextColored(Green, Loc.T("路径有效"));
        else ImGui.TextColored(Yellow, problem);

        changed |= DrawUpdateSection();

        var port = _config.BackendPort;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt(Loc.T("后端端口"), ref port, 0)) { _config.BackendPort = Math.Clamp(port, 1024, 65535); changed = true; }

        changed |= Check(Loc.T("插件加载时自动启动后端"), _config.AutoStartBackend, v => _config.AutoStartBackend = v);
        var modelChanged = DrawDeviceSettings();
        using (ImRaii.Disabled(_config.ReleaseAfter == 0))
            changed |= Check(Loc.T("启动后预加载模型"), _config.PreloadModel, v => _config.PreloadModel = v,
                             Loc.T("首次识别不用等加载；“识别后立即释放”时无效"));
        if (modelChanged && BackendOnline)
            _ = Run(async () => { await _client.ConfigureAsync(_config); SetStatus(Loc.T("已更新识别设备设置（下次加载生效）")); });
        changed |= modelChanged;
        changed |= Check(Loc.T("退出时关闭插件启动的后端"), _config.StopBackendOnExit, v => _config.StopBackendOnExit = v);

        ImGui.Separator();
        ImGui.TextDisabled(Loc.T("操作"));
        changed |= Check(Loc.T("松开滑杆时自动应用到角色"), _config.PushOnRelease, v => _config.PushOnRelease = v);
        changed |= Check(Loc.T("识别完成后立即应用到角色"), _config.PushAfterRecognize, v => _config.PushAfterRecognize = v);
        changed |= Check(Loc.T("进入 GPose 且 Brio 就绪时自动打开界面"), _config.AutoOpenInGpose, v => _config.AutoOpenInGpose = v,
                         Loc.T("自动打开的界面在退出 GPose 时自动关闭；用指令或插件列表打开的不会关闭"));
        changed |= Check(Loc.T("游戏界面隐藏时仍显示"), _config.ShowWhenUiHidden, v =>
        {
            _config.ShowWhenUiHidden = v;
            Plugin.ApplyUiHide(_pi, _config);
        }, Loc.T("关闭时与游戏界面一起隐藏（如 GPose 里隐藏界面截图）。界面不显示时可以打开试试"));

        ImGui.Separator();
        ImGui.TextDisabled(Loc.F("Posegraph HTTP（127.0.0.1:{0}，复刻镜头需要）：{1}", _config.Port, (_server.IsRunning ? Loc.T("运行中") : Loc.T("已停止"))));
        ImGui.SameLine();
        if (ImGui.SmallButton(_server.IsRunning ? Loc.T("停止") : Loc.T("启动")))
        {
            if (_server.IsRunning) _server.Stop();
            else
            {
                try { _server.Start(_config.Port); }
                catch (Exception ex) { SetError(Loc.F("端口 {0} 启动失败：{1}", _config.Port, ex.Message)); }
            }
        }

        if (changed) _pi.SavePluginConfig(_config);

        if (ImGui.CollapsingHeader(Loc.T("后端日志")))
        {
            using var child = ImRaii.Child("##log", new Vector2(0, 160), true);
            foreach (var line in _process.Log)
                ImGui.TextUnformatted(line);
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY()) ImGui.SetScrollHereY(1);
        }
    }

    /// <summary>设置页的后端更新区域。返回配置是否有改动。</summary>
    private bool DrawUpdateSection()
    {
        var changed = false;
        ImGui.Text(Loc.F("后端版本 {0}，插件 {1}", _installedBackend?.Version ?? Loc.T("未知"), BackendUpdater.PluginTag));
        ImGui.SameLine();
        if (_packageRuntime is null)
            ImGui.TextDisabled(Loc.T("（源码安装：请用 git 更新到同版本）"));
        else if (_updater.Busy)
            ImGui.TextColored(Yellow, _updater.Status ?? Loc.T("更新中…"));
        else if (_backendOutdated)
        {
            if (IconButton("##updateBackend", FontAwesomeIcon.CloudDownloadAlt,
                           Loc.T("从 GitHub 下载同版本的后端源码（不到 1MB），只替换 backend\\src，Python、依赖和模型不动。会先停止后端，更新后自动启动")))
                UpdateBackend();
            ImGui.SameLine();
            ImGui.TextColored(Yellow, Loc.F("可更新到 {0}", BackendUpdater.PluginTag));
        }
        else
            ImGui.TextColored(Green, Loc.T("已是最新"));

        if (_packageRuntime is not null)
        {
            var proxy = _config.UpdateProxy;
            ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 140));
            if (ImGui.InputText(Loc.T("下载加速前缀##proxy"), ref proxy, 256))
            {
                _config.UpdateProxy = proxy.Trim();
                changed = true;
            }
            ItemTip(Loc.T("连不上 GitHub 时可以填加速前缀（如 https://gh-proxy.org/），会加在下载地址前面；留空为直连"));
        }
        return changed;
    }

    private static readonly (string Key, string Label, string Tip)[] Devices =
    [
        ("auto", "自动", "可用显存足够（约 3GB）时用 GPU，否则用 CPU"),
        ("cuda", "GPU", "总是用显卡（约 1.5 秒/张）。显存紧张时可能拖慢甚至挤崩游戏"),
        ("cpu", "CPU", "不占显存（约 10 秒/张，取决于 CPU），内存约需 4GB"),
    ];

    private static readonly (int Seconds, string Label)[] ReleasePolicies =
        [(0, "识别后立即释放"), (60, "空闲 1 分钟后释放"), (300, "空闲 5 分钟后释放"), (-1, "常驻（不释放）")];

    /// <summary>识别设备与释放策略。返回是否有改动。</summary>
    private bool DrawDeviceSettings()
    {
        var changed = false;
        ImGui.Text(Loc.T("识别设备"));
        foreach (var (key, label, tip) in Devices)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton(Loc.T(label), _config.BackendDevice == key))
            {
                _config.BackendDevice = key;
                changed = true;
            }
            ItemTip(Loc.T(tip));
        }
        ImGui.SetNextItemWidth(180);
        var current = ReleasePolicies.FirstOrDefault(p => p.Seconds == _config.ReleaseAfter).Label is { } known
            ? Loc.T(known) : Loc.F("空闲 {0} 秒后释放", _config.ReleaseAfter);
        if (ImGui.BeginCombo(Loc.T("模型释放"), current))
        {
            foreach (var (seconds, label) in ReleasePolicies)
                if (ImGui.Selectable(Loc.T(label), _config.ReleaseAfter == seconds))
                {
                    _config.ReleaseAfter = seconds;
                    changed = true;
                }
            ImGui.EndCombo();
        }
        ItemTip(Loc.T("释放后显存/内存归还给游戏，下次识别重新加载（约 5~10 秒）。\nGPU 模式用过一次后，CUDA 运行时会常驻少量显存，直到后端退出"));
        using (ImRaii.Disabled(_config.BackendDevice == "cpu"))
            changed |= Check(Loc.T("GPU 模式下 MHR 放 CPU"), _config.MhrOnCpu, v => _config.MhrOnCpu = v, Loc.T("显存约省 750MB，结果一致，每张约慢 0.5 秒"));
        return changed;
    }

    private static bool Check(string label, bool value, Action<bool> set, string? tip = null)
    {
        var v = value;
        var changed = ImGui.Checkbox(label, ref v);
        if (tip is not null) ItemTip(tip);
        if (changed) set(v);
        return changed;
    }
}
