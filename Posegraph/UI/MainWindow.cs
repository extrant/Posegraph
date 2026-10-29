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
public sealed partial class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Green = PosegraphTheme.Green;
    private static readonly Vector4 Yellow = PosegraphTheme.Yellow;
    private static readonly Vector4 Red = PosegraphTheme.Red;
    private static readonly Vector4 Grey = PosegraphTheme.Grey;
    private static readonly Vector4 Orange = new(1f, 0.55f, 0f, 1);
    private static readonly Vector4 MhrGreen = new(0.31f, 0.86f, 0.31f, 1);
    private static readonly Vector4 Blue = PosegraphTheme.Blue;


    // 静态表里的界面文字存中文原文，使用时再 Loc.T 翻译（运行时可切换语言）

    private static readonly (string Key, string Label, string Tip)[] AmplitudeGroups =
    [
        ("spine", "脊柱", "腰/背/胸三节的弯曲扭转。脊柱用绑定朝向对齐后动作可能偏保守，可适当放大"),
        ("neck", "颈", "颈部转动。插画头颈比例夸张，识别容易估大，建议 0.7"),
        ("head", "头", "头部相对颈的转动。插画建议 0.7"),
        ("clavicle", "锁骨", "耸肩/含胸。过大时斜方肌会隆起、肩部出尖角"),
        ("wrist", "手腕", "手腕弯曲扭转"),
    ];

    private static readonly (string Key, string Label, string Tip)[] FaceGroups =
    [
        ("eyes", "眼睑", "闭眼、睁大、眯眼"),
        ("brows", "眉毛", "眉头、眉梢的高低和皱眉"),
        ("mouth", "嘴部", "嘴角、嘴唇、脸颊、鼻翼跟着照片动的幅度（歪嘴、单侧嘴角都在这里）"),
        ("jaw", "下巴", "张嘴的幅度"),
        ("gaze", "视线", "眼球转动；侧脸时以看得见的那只眼为准，两眼同向"),
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

    /// <summary>打开 / 关闭灯光窗口（由 Plugin 接上）。</summary>
    public Action? ToggleLighting { get; set; }

    /// <summary>导出游戏表情数据（/posegraph facedump，由 Plugin 接上）。</summary>
    public Action? DumpFaceData { get; set; }

    /// <summary>打开 / 关闭构图与截图窗口（由 Plugin 接上）。</summary>
    public Action? ToggleComposition { get; set; }

    /// <summary>当前照片的尺寸（宽、高），构图框按它的比例；没有照片时为 null。</summary>
    public int[]? PhotoSize => _session?.ImageSize;

    /// <summary>当前照片的预览纹理和识别出的骨架（图片像素坐标），构图窗口叠在游戏画面上当参考。</summary>
    public IDalamudTextureWrap? PhotoTexture => _session is null ? null : _texture;

    public Segment[]? PhotoSkeleton => _session is null ? null : _result?.Overlay?.Mhr;

    /// <summary>当前选中的角色（null 为 GPose 目标），灯光窗口照它打光。</summary>
    public int? SelectedActor => _actor;

    /// <summary>主窗口当前的图在图库里的内容哈希（存灯光、修正用）；没打开图为 null。</summary>
    public string? CurrentImageSha1 => _imageSha1;

    /// <summary>主窗口里有识别好的照片（灯光窗口的「照片同款」要用）。</summary>
    public bool HasPhoto => _session is not null && BackendOnline;

    /// <summary>当前照片里选中那个人的照片同款灯光；没有照片时为 null。</summary>
    public async Task<PhotoLighting?> PhotoLightingAsync()
        => _session is { } s ? await _client.LightingAsync(s.SessionId, _person) : null;

    /// <summary>主窗口关闭时（关闭按钮、指令、退出 GPose 自动关闭）调用：图库、角色列表跟着关（由 Plugin 接上）。</summary>
    public Action? Closed { get; set; }

    /// <summary>辅助视角菜单（由 Plugin 接上，与图库右键共用）。</summary>
    public AuxViewsMenu? AuxMenu { get; set; }
    private readonly SystemProbe _probe = new();
    private readonly BackendUpdater _updater = new();

    // 后端版本信息（读磁盘，2 秒刷新一次）
    private BackendUpdater.VersionInfo? _installedBackend;
    private BackendUpdater.Component[] _missingComponents = [];
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
    private string? _savedFix;       // 这张图保存的修正（FullSnapshot），没有为 null：判断有没有未保存的修改
    private IDalamudTextureWrap? _texture;
    private IDalamudTextureWrap? _newTexture;
    private int _person;
    private RetargetParams _params;
    private string _preset;
    private volatile RetargetResult? _result;
    private volatile bool _recognizing;
    private (string Path, AuxView View)[] _auxViews = [];   // 当前图挂的辅助视角（已融合进会话）
    private bool _useAux = true;                             // 3D 页开关：关掉时只用主图，方便对比

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
    private readonly Scene3D _stickView = new(0f, 0.15f, 3.2f);
    private Vector3? _stickCenter;   // 3D 页上次对准的腰的位置（换图 / 换人时重新对准）
    private int? _ringAxis;   // 正在拖的旋转环（0 绕左右轴、1 绕竖直轴、2 绕前后轴，同关节偏移的三个分量）
    private Vector2 _ringTangent;   // 按下时环上那一点沿正方向转的屏幕方向（像素 / 弧度）
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
        Recognition = new RecognitionService(Library, _client, () => _config.ReleaseAfter == 0)
        {
            Parallelism = () => _config.BatchRecognitionParallelism,
            ToPng = image => TextureImages.ToPngAsync(_textures, _readback, image),
        };
        Library.AuxChanged += OnAuxChanged;
        _process = new BackendProcess();
        _preset = config.DefaultPreset;
        _params = LoadParams(_preset);

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

    public override void PreDraw() => PosegraphTheme.Push();

    public override void PostDraw() => PosegraphTheme.Pop();

    public override void OnClose() => Closed?.Invoke();

    public void OpenSettings()
    {
        IsOpen = true;
        _selectTab = 4;
    }

    public void Dispose()
    {
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
        _missingComponents = _packageRuntime is not null ? BackendUpdater.MissingComponents(_config.BackendDir) : [];
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

    /// <summary>force：忽略缓存重新识别（「重新识别」按钮）。</summary>
    /// <summary>note：识别完成时附在状态后面的提示。</summary>
    private void Recognize(string path, bool force = false, string? note = null)
    {
        if (_recognizing)
        {
            SetStatus(Loc.T("还在识别上一张，识别完再打开"), Yellow);
            return;
        }
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
                await using var batch = Recognition.Batch();   // 主图和辅助视角只加载一次模型
                var old = _session;
                var (info, sha1) = await Recognition.OpenAsync(path, force);
                var png = await _client.SessionImageAsync(info.SessionId);
                var tex = await _textures.CreateFromImageAsync(png);
                Interlocked.Exchange(ref _newTexture, tex)?.Dispose();
                _session = info;
                _imageSha1 = sha1;
                _sessionKey = sha1;
                _mergeSession = false;
                if (_paint is { } paint) foreach (var p in paint.People) p.Human = null;   // 之前的合并会话作废
                _person = 0;
                _result = null;
                // 这张图在 3D 页保存过修正就恢复；否则用预设（手动偏移只对当前图有意义，不带过来）
                var fix = Library.ReadCorrection(sha1);
                if (fix is not null) _preset = fix.Preset;
                _params = fix?.Params.Clone() ?? LoadParams(_preset);
                _savedFix = fix is null ? null : FullSnapshot(fix.Preset, fix.Params);
                _selected = null;
                _useAux = true;
                _auxViews = [];
                if (old is not null) _ = _client.DeleteSessionAsync(old.SessionId).ContinueWith(_ => { });
                var aux = Library.AuxImages(sha1).Length > 0 ? await AttachAuxAsync(info, sha1) : "";
                SetStatus((info.Device is null
                              ? Loc.T("已从识别缓存读取")
                              : Loc.F("识别完成（{0} {1:0.0} 秒）", DeviceLabel(info.Device), info.InferenceMs / 1000f))
                          + (fix is not null ? Loc.T("，已恢复保存的修正") : "") + aux
                          + (InLibrary ? "" : Loc.T("；未存入图库（工具栏的文件夹 + 按钮可存入）")) + (note ?? ""), Green);
                RequestRetarget(_config.PushAfterRecognize);
                // 合并模式：这张图涂过人就读回来（合并页开着时从识别缓存恢复多人结果）；没涂过时顺带找人提示。
                // 不等它：找人在 CPU 上可能要排队，等的话这段时间拖进来的图都打不开
                _ = LoadPaintAsync(path, sha1, info);
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

    /// <summary>
    /// 把当前图挂着的辅助视角融合进会话（没识别过的辅助图先识别）。返回附在状态后面的说明：几张、什么角度、有没有问题。
    /// 辅助图识别失败不影响主图，只在说明里提示。
    /// </summary>
    private async Task<string> AttachAuxAsync(SessionInfo info, string sha1)
    {
        try
        {
            SetStatus(Loc.T("识别并融合辅助视角…"), Yellow);
            var views = await Recognition.AttachAuxAsync(info.SessionId, sha1);
            if (_imageSha1 == sha1) _auxViews = views;
            if (views.Length == 0) return "";
            var text = Loc.F("，已融合 {0} 张辅助视角（{1}）", views.Length,
                             string.Join("、", views.Select(v => AuxViewsMenu.ViewLabel(v.View.View))));
            var warnings = views.Select(v => AuxViewsMenu.Warning(v.View) is { } w ? $"{Path.GetFileNameWithoutExtension(v.Path)}：{w}" : null)
                                .OfType<string>().ToList();
            return warnings.Count > 0 ? text + "；" + string.Join("；", warnings) : text;
        }
        catch (Exception ex)
        {
            return Loc.F("；辅助视角融合失败：{0}", ex.Message);
        }
    }

    /// <summary>图库里给当前图增删了辅助视角：重新融合并重算。</summary>
    private void OnAuxChanged(string sha1)
    {
        if (sha1 != _imageSha1 || _session is not { } session || _recognizing) return;
        _ = Run(async () =>
        {
            var text = await AttachAuxAsync(session, sha1);
            if (session != _session) return;
            SetStatus(_auxViews.Length > 0 ? Loc.T("辅助视角已更新") + text : Loc.T("已去掉辅助视角，只用主图"), Green);
            RequestRetarget(false);
        });
    }

    /// <summary>
    /// 新图片用的值：插件内置预设。不沿用别的图调过的值——每张图不一样，调好的值（调整页的数值 + 3D 页的偏移）
    /// 用「保存修正」存进这张图，打开时恢复。
    /// </summary>
    private static RetargetParams LoadParams(string preset) => RetargetParams.Preset(preset);

    private static string ParamsSnapshot(RetargetParams p)
    {
        var c = p.Clone();
        c.Offsets.Clear();
        return System.Text.Json.JsonSerializer.Serialize(c);
    }

    /// <summary>预设 + 全部参数（含偏移），判断有没有未保存的修改。</summary>
    private static string FullSnapshot(string preset, RetargetParams p) => preset + System.Text.Json.JsonSerializer.Serialize(p);

    /// <summary>当前图有未保存的修改：保存过就和保存的比，没保存过就看是否和默认值不同。</summary>
    private bool HasUnsavedChanges() => _savedFix is null ? HasUserCorrection() : FullSnapshot(_preset, _params) != _savedFix;

    /// <summary>调整页里与预设默认值不同的项数（幅度各组、手部补偿、站姿、辅助骨）。</summary>
    private int ChangedTuning()
    {
        var d = RetargetParams.Preset(_preset);
        var n = AmplitudeGroups.Count(g => Math.Abs(_params.Amp(g.Key) - d.Amp(g.Key)) > 1e-4f);
        if (Math.Abs(_params.HandStrength - d.HandStrength) > 1e-4f) n++;
        if (Math.Abs(_params.HandNear - d.HandNear) > 1e-4f) n++;
        if (Math.Abs(_params.HandFar - d.HandFar) > 1e-4f) n++;
        if (_params.Upright != d.Upright) n++;
        if (Math.Abs(_params.UprightMaxDeg - d.UprightMaxDeg) > 1e-4f) n++;
        if (_params.Ground != d.Ground) n++;
        if (_params.FlatFeet != d.FlatFeet) n++;
        if (Math.Abs(_params.HelperStrength - d.HelperStrength) > 1e-4f) n++;
        if (Math.Abs(_params.FaceHead - d.FaceHead) > 1e-4f) n++;
        n += FaceGroups.Count(g => Math.Abs(_params.FaceGain(g.Key) - 1f) > 1e-4f);
        return n;
    }

    private void RequestRetarget(bool push)
    {
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
                                                         _raceOverride ?? _detectedRace, trust, _useAux, OnionInputs());
                }
                catch (BackendException ex) when (ex.StatusCode == 404 && !restored && _imageSha1 is not null)
                {
                }
                if (result is null)
                {
                    // 会话没了（后端重启过，或被批量识别挤出）：从识别缓存恢复后再算
                    restored = true;
                    if (await Recognition.RestoreAsync(_sessionKey ?? _imageSha1!, session.Name) is not { } info)
                        throw new InvalidOperationException(Loc.T("会话已失效且没有识别缓存，请重新识别"));
                    if (_auxViews.Length > 0 && !_mergeSession) await Recognition.AttachAuxAsync(info.SessionId, _imageSha1!);   // 融合结果在会话里，跟着丢了
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
            RememberPushed(pose);
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
        SetStatus(Loc.T("复刻镜头中…"), Yellow);
        _ = Run(async () =>
        {
            var r = await _client.CameraAsync(_session.SessionId, _person, _params.Clone(), _actor, _raceOverride ?? _detectedRace, _useAux);
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
    /// 图库里批量存到 Brio 姿势库：用每张图保存的修正（没有就用默认值）和当前种族计算，不读游戏姿势（骨长用标准骨长）。
    /// 没识别过的先识别。返回保存的路径。
    /// </summary>
    public async Task<string> SaveToBrioAsync(string image)
    {
        var (info, sha1) = await Recognition.OpenAsync(image);
        try
        {
            if (Library.AuxCount(sha1) > 0) await Recognition.AttachAuxAsync(info.SessionId, sha1);
            var p = Library.ReadCorrection(sha1)?.Params.Clone() ?? LoadParams(_preset);
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

    // ------------------------------------------------------------------ 拆分模式（角色列表窗口调用）

    public (int Index, string Name)[] Actors => _actors;

    public bool InGpose => _clientState.IsGPosing;

    /// <summary>必须在主线程（绘制时）调用。</summary>
    public PoseApplier.ActorIdentity? Identify(int index) => _applier.Identify(index);

    /// <summary>在主窗口里编辑某个角色的图：切到这个角色并打开图片（3D / 调整页的修正照常保存到这张图）。</summary>
    public void Edit(int actor, string image)
    {
        SelectActor(actor);
        OpenImage(image);
    }

    /// <summary>
    /// 把一张图直接算好应用到指定角色（不经过主窗口当前的图）：用这张图保存的修正，没有就用当前预设；挂了辅助视角就融合。
    /// 骨长按这个角色的游戏姿势；像换种族前残留的 Brio 姿势时先清除再算（同单人流程）。返回错误信息，成功为 null。
    /// </summary>
    public async Task<string?> ApplyToActorAsync(int actor, string image)
    {
        var (info, sha1) = await Recognition.OpenAsync(image);
        try
        {
            if (Library.AuxCount(sha1) > 0) await Recognition.AttachAuxAsync(info.SessionId, sha1);
            var fix = Library.ReadCorrection(sha1);
            return await RetargetAndApplyAsync(actor, info.SessionId, 0, fix?.Params.Clone() ?? LoadParams(_preset));
        }
        finally
        {
            _ = _client.DeleteSessionAsync(info.SessionId).ContinueWith(_ => { });
        }
    }

    /// <summary>
    /// 会话里的第 person 个人按参数 p 重定向并应用到角色：骨长按这个角色的游戏姿势；像换种族前残留的 Brio 姿势时先清除再算。
    /// 返回错误信息，成功为 null（拆分模式、合并模式共用）。
    /// </summary>
    private async Task<string?> RetargetAndApplyAsync(int actor, string sid, int person, RetargetParams p)
    {
        if (await _applier.IdentifyAsync(actor) is not { } id) return Loc.T("找不到角色");
        var race = (id.Key == _actorKey ? _raceOverride : _actorStates.GetValueOrDefault(id.Key)?.RaceOverride) ?? id.Race;
        var trust = _cleanSignatures.Contains(id.Signature);
        var reference = await _applier.GetPoseAsync(actor);
        if (reference is not null) _backups.TryAdd(id.Signature, reference);
        var r = await _client.RetargetAsync(sid, person, p, reference, race, trust);
        if (r.ReferenceStale && !trust && await _applier.ResetPoseAsync(actor))
        {
            _cleanSignatures.Add(id.Signature);
            _backups.Remove(id.Signature);
            reference = await _applier.GetPoseAsync(actor);
            r = await _client.RetargetAsync(sid, person, p, reference, race, true);
        }
        return await _applier.ApplyAsync(actor, r.Pose);
    }

    /// <summary>主窗口当前打开的图（角色列表里「用主窗口当前的图」）。</summary>
    public string? CurrentImage => _imagePath is { } p && File.Exists(p) ? p : null;

    /// <summary>主窗口的位置和大小（角色列表贴在它右边）。</summary>
    public Vector2 WindowPos { get; private set; }
    public Vector2 WindowSize { get; private set; }

    /// <summary>打开更新日志窗口（由 Plugin 接上）。</summary>
    public Action? ShowChangelog { get; set; }

    /// <summary>打开 / 关闭角色列表（由 Plugin 接上）。</summary>
    public Action? ToggleSplit { get; set; }

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
                _savedFix = FullSnapshot(correction.Preset, correction.Params);
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
        if (Interlocked.Exchange(ref _newTexture, null) is { } tex)
        {
            _texture?.Dispose();
            _texture = tex;
        }
        WindowPos = ImGui.GetWindowPos();
        WindowSize = ImGui.GetWindowSize();
        PollHealth();
        RefreshActors();
        DragSource();

        DrawStatusBar();
        DrawToolbar();
        PollBrioEdits();
        DrawBrioEditsBanner();

        // 底栏：按钮一行 + 状态文字（按宽度换行，最多占四行）
        var statusH = ImGui.CalcTextSize(_statusText, false, ImGui.GetContentRegionAvail().X).Y;
        var footer = ImGui.GetFrameHeightWithSpacing() + Math.Min(statusH, ImGui.GetTextLineHeight() * 4) + ImGui.GetStyle().ItemSpacing.Y;
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
        if (IconButton("##again", FontAwesomeIcon.Redo, Loc.T("重新识别当前图片（不用识别缓存）"), _imagePath is not null && !_recognizing))
            Recognize(_imagePath!, true);
        ImGui.SameLine();
        var canSave = _session is not null && _imageSha1 is not null && !InLibrary && !_recognizing;
        using (ImRaii.PushColor(ImGuiCol.Text, Yellow, canSave))
            if (IconButton("##saveLib", FontAwesomeIcon.FolderPlus,
                           InLibrary ? Loc.T("这张图已在图库里（修正在 3D 页保存）")
                                     : Loc.T("存入图库：复制到图库当前文件夹（按内容查重），并带上 3D / 调整页的修正"), canSave))
                SaveCurrentToLibrary();
        ImGui.SameLine();
        var auxCount = _imageSha1 is { } sha ? Library.AuxCount(sha) : 0;
        using (ImRaii.PushColor(ImGuiCol.Text, Blue, auxCount > 0 && _useAux))
            if (IconButton("##aux", FontAwesomeIcon.Clone,
                           auxCount > 0 ? Loc.F("辅助视角：{0} 张（同一动作其他角度的图，自动融合补深度）", auxCount)
                                        : Loc.T("辅助视角：给这张图挂上同一动作其他角度的图（最好是侧面），自动融合补深度"),
                           _imagePath is not null && File.Exists(_imagePath) && AuxMenu is not null))
                ImGui.OpenPopup("##auxMenu");
        if (ImGui.BeginPopup("##auxMenu"))
        {
            if (_imagePath is { } path) AuxMenu!.Draw(path, [], (text, color) => SetStatus(text, color));
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        if (IconButton("##split", FontAwesomeIcon.Users, Loc.T("角色列表（拆分模式）：多个角色各绑一张图，一起应用"), ToggleSplit is not null))
            ToggleSplit!();
        ImGui.SameLine();
        if (IconButton("##lighting", FontAwesomeIcon.Lightbulb, Loc.T("灯光：一键给当前角色打光，跟着脸和镜头摆"), ToggleLighting is not null))
            ToggleLighting!();
        ImGui.SameLine();
        if (IconButton("##composition", FontAwesomeIcon.CropAlt, Loc.T("构图与截图：构图框、等效焦距、一键截图"), ToggleComposition is not null))
            ToggleComposition!();
        ImGui.SameLine();
        IconButton("##video", FontAwesomeIcon.Film, Loc.T("视频逐帧（预留）"), false);
        if (_recognizing || _rtBusy)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Yellow, _recognizing ? Loc.T("识别中…") : Loc.T("计算中…"));
        }

        // 第三行：角色、图中人物、种族骨架，按比例分满整行
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var row = ImGui.GetContentRegionAvail().X - spacing * 2;
        var label = _actor is { } actor ? ActorName(actor) : Loc.T("GPose 目标");
        ImGui.SetNextItemWidth(row * 0.42f);
        if (ImGui.BeginCombo("##actor", UiText.ComboPreview(label, row * 0.42f)))
        {
            if (ImGui.Selectable(Loc.T("GPose 目标"), _actor is null)) SelectActor(null);
            foreach (var (index, name) in _actors)
                if (ImGui.Selectable($"{name}##{index}", _actor == index)) SelectActor(index);
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("要摆姿势的角色"));
        ImGui.SameLine();

        // 多人（合并模式）：编号与图像页的人物框、角色列表一致；切换时换成这个人的调整值和对应角色
        var humans = _session?.Humans.Length ?? 0;
        using (ImRaii.Disabled(humans <= 1))
        {
            var personLabel = humans <= 1 ? Loc.T("1 人") : Loc.F("第 {0} 人", PersonNumber(_person));
            ImGui.SetNextItemWidth(row * 0.2f);
            if (ImGui.BeginCombo("##person", UiText.ComboPreview(personLabel, row * 0.2f)))
            {
                for (var i = 0; i < humans; i++)
                    if (ImGui.Selectable(Loc.F("第 {0} 人", PersonNumber(i)), _person == i) && i != _person)
                        SwitchPerson(i);
                ImGui.EndCombo();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(humans <= 1 ? Loc.T("图中人物：一张图有多个人时，在角色列表的「合并」页找人并识别")
                                         : Loc.T("切换编辑图中的第几个人（各人的调整值分开保存，切换时也切到这个人对应的角色）"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        DrawRaceCombo();
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
        if (ImGui.BeginCombo("##race", UiText.ComboPreview(raceLabel, ImGui.CalcItemWidth())))
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

        // 状态可能很长（辅助视角说明、错误信息）：按宽度换行，底栏高度跟着变（见 Draw 里的 footer）
        using (ImRaii.PushColor(ImGuiCol.Text, _statusColor))
            ImGui.TextWrapped(_statusText);
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
        DrawMergeHint();

        var avail = ImGui.GetContentRegionAvail();
        if (_texture is null || _session is null)
        {
            DrawDropZone(new Vector2(avail.X, Math.Max(80, avail.Y)));
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
        DrawPeopleBoxes(origin, scale);

        // 合并页开着、还没按人物框识别：整张图当一个人的骨架没有意义，不画
        if (MergeEditing && !_mergeSession) return;
        if (_result?.Overlay is not { } ov) return;
        var dl = ImGui.GetWindowDrawList();
        if (showMhr) DrawSegments(dl, ov.Mhr, origin, scale, MhrGreen);
        if (showFf) DrawSegments(dl, ov.Ffxiv, origin, scale, Orange);
    }

    /// <summary>还没有图时的拖放区：虚线框 + 图标 + 提示，拖着图片悬停时高亮；点一下打开图库。</summary>
    private void DrawDropZone(Vector2 size)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##dropZone", size);
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (ImGui.IsItemClicked() && !_recognizing) ToggleLibrary?.Invoke();
        DropTarget();

        var scale = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        var dragging = _dragDrop.IsDragging;
        var accent = PosegraphTheme.Accent;
        var line = ImGui.GetColorU32(dragging || hovered ? accent : accent with { W = 0.35f });
        dl.AddRectFilled(pos, pos + size, ImGui.GetColorU32(accent with { W = dragging ? 0.10f : hovered ? 0.06f : 0.03f }), 8 * scale);
        // 虚线边框：沿四条边画短线段
        var dash = 8 * scale;
        var inset = 1.5f * scale;
        var min = pos + new Vector2(inset);
        var max = pos + size - new Vector2(inset);
        for (var x = min.X; x < max.X; x += dash * 2)
        {
            dl.AddLine(new Vector2(x, min.Y), new Vector2(Math.Min(x + dash, max.X), min.Y), line, 1.5f * scale);
            dl.AddLine(new Vector2(x, max.Y), new Vector2(Math.Min(x + dash, max.X), max.Y), line, 1.5f * scale);
        }
        for (var y = min.Y; y < max.Y; y += dash * 2)
        {
            dl.AddLine(new Vector2(min.X, y), new Vector2(min.X, Math.Min(y + dash, max.Y)), line, 1.5f * scale);
            dl.AddLine(new Vector2(max.X, y), new Vector2(max.X, Math.Min(y + dash, max.Y)), line, 1.5f * scale);
        }

        var icon = (_recognizing ? FontAwesomeIcon.Spinner : FontAwesomeIcon.Image).ToIconString();
        var lines = _recognizing
            ? [Loc.T("识别中…")]
            : new[] { Loc.T("把图片拖到这里识别"), Loc.T("或点这里打开图库选图") };
        var lineH = ImGui.GetTextLineHeightWithSpacing();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(icon) * 2;
        var total = iconSize.Y + 10 * scale + lineH * lines.Length;
        var y0 = pos.Y + (size.Y - total) / 2;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            dl.AddText(UiBuilder.IconFont, ImGui.GetFontSize() * 2, new Vector2(pos.X + (size.X - iconSize.X) / 2, y0), line, icon);
        y0 += iconSize.Y + 10 * scale;
        for (var i = 0; i < lines.Length; i++)
        {
            var w = ImGui.CalcTextSize(lines[i]).X;
            dl.AddText(new Vector2(pos.X + (size.X - w) / 2, y0 + i * lineH),
                       ImGui.GetColorU32(i == 0 ? ImGuiCol.Text : ImGuiCol.TextDisabled), lines[i]);
        }
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

    /// <summary>图库里拖出来的图（由 Plugin 接上）。</summary>
    public Func<string[]>? LibraryDragged { get; set; }

    /// <summary>系统拖来的图片 / 文件夹，或从图库拖来的图（打开第一张）。</summary>
    private void DropTarget()
    {
        if (_dragDrop.CreateImGuiTarget(DragLabel, out var files, out var dirs))
            OpenDropped(files, dirs);
        if (!ImGui.BeginDragDropTarget()) return;
        var payload = ImGui.AcceptDragDropPayload(LibraryWindow.InternalDragLabel, ImGuiDragDropFlags.None);
        if (!payload.IsNull && LibraryDragged?.Invoke().FirstOrDefault(ImageLibrary.IsImage) is { } path) OpenImage(path);
        ImGui.EndDragDropTarget();
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
    // 面捕：按（会话，人物）取一次
    private (string Sid, int Person)? _faceKey;
    private FaceResult? _face;
    private string? _faceError;

    /// <summary>
    /// 调整页的「面部表情」：后端用 MediaPipe 找脸得到的表情系数和置信度（置信度过低的不用），
    /// 套用到角色时换算成 FF14 的脸部骨骼（后端 face_rig.py）。
    /// </summary>
    private void DrawFaceSection()
    {
        if (_session is not { } session) return;
        if (!ImGui.CollapsingHeader(Loc.T("面部表情（测试）"))) return;
        var key = (Sid: session.SessionId, Person: _person);
        if (_faceKey != key)
        {
            (_faceKey, _face, _faceError) = (key, null, null);
            _ = Task.Run(async () =>
            {
                try
                {
                    var face = await _client.FaceAsync(key.Sid, key.Person);
                    if (_faceKey == key) _face = face;
                }
                catch (Exception ex)
                {
                    if (_faceKey == key) _faceError = ex.Message;
                }
            });
        }
        if (_faceError is { } error) { ImGui.TextColored(Red, error); return; }
        if (_face is not { } f) { ImGui.TextDisabled(Loc.T("正在找脸…")); return; }
        if (!f.Available) { ImGui.TextColored(Yellow, f.Reason); return; }

        var color = f.Used ? Green : Yellow;
        ImGui.TextColored(color, Loc.F("置信度 {0:0%}", f.Confidence));
        ImGui.SameLine();
        ImGui.TextDisabled(f.Used ? Loc.T("可用") : Loc.F("不用：{0}", f.Reason));
        DrawFaceApply(f.Used);
        DrawFaceHead();
        if (!f.Found) return;
        ImGui.TextDisabled(Loc.F("头部朝向：左右 {0:0}°，上下 {1:0}°", f.YawDeg, f.PitchDeg));
        var width = ImGui.GetContentRegionAvail().X;
        foreach (var (name, value) in f.Blendshapes.OrderByDescending(kv => kv.Value).Take(8))
        {
            ImGui.ProgressBar(value, new Vector2(width, 0), $"{name}  {value:0.00}");
        }
    }

    /// <summary>「套用到角色」：重定向时把照片表情换算成脸部骨骼一起应用；强度；结果（用的骨架 / 没套上的原因）。</summary>
    private void DrawFaceApply(bool usable)
    {
        var apply = _params.Face;
        using (ImRaii.Disabled(!usable && !apply))
            if (ImGui.Checkbox(Loc.T("套用到角色"), ref apply))
            {
                _params.Face = apply;
                RequestRetarget(_config.PushOnRelease);
            }
        ItemTip(Loc.T("把照片里的表情换算成游戏的脸部骨骼，和姿势一起应用。\n以中性脸为底换算，重复应用不会越叠越多；关掉后脸部骨骼不再改动（保持当前的样子）"));
        if (!apply) return;
        ImGui.SameLine();
        var strength = _params.FaceStrength;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(Loc.T("强度")).X - ImGui.GetStyle().ItemInnerSpacing.X * 2);
        if (ImGui.SliderFloat(Loc.T("强度"), ref strength, 0f, 2f, "%.2f")) _params.FaceStrength = strength;
        if (ImGui.IsItemDeactivatedAfterEdit()) RequestRetarget(_config.PushOnRelease);
        ItemTip(Loc.T("所有表情的幅度倍数：照片表情不明显时调大，夸张时调小"));
        // 分项权重默认展开、用强调色：哪一块不像就单独调哪一块，比整体强度好用（用户特别要求突出）
        ImGui.SetNextItemOpen(true, ImGuiCond.FirstUseEver);
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, Blue))
            open = ImGui.TreeNode(Loc.T("分项权重（推荐）：哪一块不像就单独调哪一块") + "###faceGains");
        if (open)
        {
            foreach (var (key, label, tip) in FaceGroups)
            {
                var v = _params.FaceGain(key);
                var before = v;
                Slider($"{Loc.T(label)}##face{key}", ref v, 0, 2, "%.2f", 1, Loc.T(tip) + Loc.T("\n和上面的强度相乘；再大也不会超过游戏里这块能动的范围"));
                if (v != before) _params.FaceGains[key] = v;
            }
            ImGui.TreePop();
        }
        if (_result?.Face is not { } info) return;
        if (info.Applied)
        {
            var top = info.Channels?.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} {kv.Value:0.00}") ?? [];
            ImGui.TextDisabled(Loc.F("已套用（面部骨架 {0}）", info.Skeleton ?? "?") + (top.Any() ? "：" + string.Join("，", top) : Loc.T("：照片是无表情")));
            return;
        }
        ImGui.TextColored(Yellow, info.Reason);
        if (info.NeedsDump && DumpFaceData is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("导出游戏表情数据")))
                DumpFaceData();
            ItemTip(Loc.T("从游戏文件里导出各种族的表情动画（一两分钟，只需一次），导出后点「应用」重新套用"));
        }
    }

    /// <summary>头部朝向跟面捕的脸走多少；不开表情也生效。下面显示脸和识别的头差多少度。</summary>
    private void DrawFaceHead()
    {
        var w = _params.FaceHead;
        Slider(Loc.T("头部跟脸##facehead"), ref w, 0, 1, "%.2f", 0.5f,
               Loc.T("用面捕的脸部朝向修正头的朝向：整身识别时头只占很小一块，面捕专看脸，低头、侧脸的角度更准。\n0 = 只用整身识别的头，1 = 完全按脸；和识别差太多（45° 以上，多半认错了脸）时不改。不开表情也生效"));
        _params.FaceHead = w;
        if (_result?.HeadFromFaceDeg is { } diff && w > 0)
            ImGui.TextDisabled(diff > 45 ? Loc.F("脸和识别的头差 {0:0}°，太多了，没改", diff) : Loc.F("脸和识别的头差 {0:0}°，已按权重修正", diff));
    }

    private void DrawCorrectionBar()
    {
        var sha1 = _imageSha1;
        var saved = sha1 is not null && Library.HasCorrection(sha1);
        var unsaved = HasUnsavedChanges();
        bool save;
        using (ImRaii.PushColor(ImGuiCol.Text, Yellow, unsaved))
            save = IconButton("##saveFix", FontAwesomeIcon.Save,
                              Loc.T("保存修正：把 3D 页的关节偏移和调整页的数值存进这张图，下次打开自动恢复（新图片都从默认值开始）"), sha1 is not null);
        if (save)
        {
            Library.WriteCorrection(sha1!, new SavedCorrection(_preset, _params.Clone(), DateTime.Now));
            _savedFix = FullSnapshot(_preset, _params);
            SetStatus(Loc.T("已保存这张图的修正"), Green);
        }
        ImGui.SameLine();
        if (IconButton("##clearFix", FontAwesomeIcon.Trash, Loc.T("删除这张图保存的修正（当前调整不变）"), saved))
        {
            Library.DeleteCorrection(sha1!);
            _savedFix = null;
            SetStatus(Loc.T("已删除这张图保存的修正"), Yellow);
        }
        ImGui.SameLine();
        var offsets = _params.Offsets.Count(kv => kv.Value.Any(v => v != 0));
        var detail = Loc.F("（偏移 {0} 处，调整 {1} 项）", offsets, ChangedTuning());
        ImGui.AlignTextToFramePadding();
        if (unsaved) Fit((saved ? Loc.T("有未保存的修改") : Loc.T("未保存")) + detail, Yellow);
        else Fit((saved ? Loc.T("已保存修正") : Loc.T("默认值")) + detail, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        if (_auxViews.Length == 0) return;
        if (ImGui.Checkbox(Loc.F("使用辅助视角（{0} 张）", _auxViews.Length), ref _useAux)) RequestRetarget(_config.PushOnRelease);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("主图决定画面里看到的姿势，辅助视角只修正前后深度（主图猜不准的部分）。关掉可以对比效果\n")
                             + string.Join("\n", _auxViews.Select(v => $"{Path.GetFileNameWithoutExtension(v.Path)}："
                                 + Loc.F("{0} {1:+0;-0}°，采纳 {2:0}%", AuxViewsMenu.ViewLabel(v.View.View), v.View.Yaw, v.View.Share * 100)
                                 + (AuxViewsMenu.Warning(v.View) is { } w ? "  " + w : ""))));
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
        // 下方面板：没选关节时是一行勾选框（按控件行高留，按文字行高会被切掉一截）
        var panel = _selected is null ? ImGui.GetFrameHeightWithSpacing() : ImGui.GetFrameHeightWithSpacing() * 4 + 4;
        var size = new Vector2(avail.X, Math.Max(140, avail.Y - panel));
        var io = ImGui.GetIO();
        var joints = stick.Joints.ToDictionary(j => j.Name);
        var center = joints.TryGetValue("j_kosi", out var hip) ? ToVec(hip.Pos) : Vector3.Zero;
        // 换了图 / 人后把视图中心放回腰上（平移过的保留）
        if (_stickCenter is not { } lastCenter || Vector3.Distance(lastCenter, center) > 0.3f)
        {
            _stickView.Target = center;
            _stickCenter = center;
        }
        _stickView.Begin("##stick", size);
        var view = _stickView;
        Vector2 Project(Vector3 p) => view.Project(p) ?? new Vector2(-1e5f);
        // 朝向观察者的深度（正 = 离观察者更近），旋转环背面画淡一些
        float Depth(Vector3 p) => -view.Depth(p);

        // 旋转环（Brio 式）：选中关节上画三个环，按住环拖动就绕那根轴转（改的就是下面三个滑条的偏移）
        var ringJoint = _selected is { } sel && joints.TryGetValue(sel, out var sj) ? ToVec(sj.Pos) : (Vector3?)null;
        var ringRadius = ringJoint is { } rj ? RingPixels * ImGuiHelpers.GlobalScale / view.PixelsPerMeter(rj) : 0f;
        var hoverRing = ringJoint is { } rc && _ringAxis is null && view.Hovered
            ? HitRing(rc, ringRadius, ImGui.GetMousePos(), Project, Depth) : null;
        if (view.Activated && hoverRing is { } ringHit)
            (_ringAxis, _ringTangent) = (ringHit.Axis, ringHit.Tangent);
        if (_ringAxis is { } axis && view.Active && _selected is { } ringBone)
        {
            if (io.MouseDelta != Vector2.Zero)
            {
                var off = _params.Offsets.TryGetValue(ringBone, out var cur) ? (float[])cur.Clone() : new float[3];
                var degrees = Vector2.Dot(io.MouseDelta, _ringTangent) / _ringTangent.LengthSquared() * 180f / MathF.PI;
                off[axis] = Math.Clamp(off[axis] + degrees, -180f, 180f);
                _params.Offsets[ringBone] = off;
                RequestRetarget(false);
            }
            view.Grab = true;
        }
        if (view.Hovered && _ringAxis is null)
            ImGui.SetTooltip(hoverRing is { } h
                ? Loc.F("按住拖动：{0}", RingAxisNames[h.Axis])
                : Loc.T("左键拖动旋转，右键 / 中键拖动平移，滚轮缩放，点击关节选中；双击复位视角\n选中关节后拖它上面的彩色环直接转动"));

        var dl = view.DrawList;

        // 地面网格与朝向
        var floor = stick.Joints.Min(j => j.Pos[1]);
        view.Grid(new Vector3(center.X, floor, center.Z), 0.75f, 0.25f, new Vector4(1, 1, 1, 0.12f));
        var front = new Vector3(center.X, floor, center.Z + 0.6f);
        view.Line(new Vector3(center.X, floor, center.Z), front, new Vector4(1, 1, 1, 0.35f), 2);
        view.Text(front, Loc.T("前"), new Vector4(1, 1, 1, 0.5f));

        // 洋葱皮：其他人的骨架（半透明，颜色和人物列表一致），只看不能选
        if (_mergeSession && _config.ShowOnion && _result.Onion is { } onion)
            foreach (var other in onion)
            {
                var number = PersonNumber(other.Person) - 1;
                var oc = PersonColor(number) with { W = 0.4f };
                var byName = other.Joints.ToDictionary(j => j.Name);
                foreach (var j in other.Joints)
                    if (j.Parent is { } parentName && byName.TryGetValue(parentName, out var op))
                        view.Line(ToVec(op.Pos), ToVec(j.Pos), oc, 2);
                if (byName.TryGetValue("j_kao", out var head))
                    view.Text(ToVec(head.Pos), (number + 1).ToString(), oc, new Vector2(6, -6));
            }

        if (_config.ShowMhrInStick)
            foreach (var s in stick.Mhr)
                view.Line(new Vector3(s.A[0], s.A[1], s.A[2]), new Vector3(s.B[0], s.B[1], s.B[2]), MhrGreen with { W = 0.6f }, 1.5f);

        var severity = _result.Severity;
        foreach (var j in stick.Joints)
        {
            if (j.Parent is null || !joints.TryGetValue(j.Parent, out var parent)) continue;
            var color = j.Name.EndsWith("_l") ? Orange : j.Name.EndsWith("_r") ? new Vector4(0.35f, 0.65f, 1f, 1) : new Vector4(0.9f, 0.9f, 0.9f, 1);
            view.Line(ToVec(parent.Pos), ToVec(j.Pos), color, 3);
        }
        foreach (var j in stick.Joints)
        {
            if (view.Project(ToVec(j.Pos)) is not { } p) continue;
            var level = severity.GetValueOrDefault(j.Name);
            var color = level == "bad" ? Red : level == "warn" ? Yellow : new Vector4(0.85f, 0.85f, 0.85f, 1);
            var radius = level is null ? 3f : 5f;
            if (_params.Offsets.TryGetValue(j.Name, out var off) && off.Any(v => v != 0))
                dl.AddCircle(p, radius + 3, ImGui.GetColorU32(new Vector4(0.7f, 0.5f, 1f, 1)), 0, 1.5f);
            dl.AddCircleFilled(p, radius, ImGui.GetColorU32(color));
            if (j.Name == _selected)
                dl.AddCircle(p, radius + 5, ImGui.GetColorU32(new Vector4(0.3f, 0.9f, 1f, 1)), 0, 2);
        }
        if (ringJoint is { } ring)
            DrawRings(dl, ring, ringRadius, _ringAxis ?? hoverRing?.Axis, Project, Depth);

        if (view.Deactivated)
        {
            if (_ringAxis is not null)
            {
                _ringAxis = null;
                RequestRetarget(_config.PushOnRelease);
            }
            else if (!view.Dragged)
            {
                var mouse = ImGui.GetMousePos();
                _selected = stick.Joints.Select(j => (j.Name, D: Vector2.Distance(Project(ToVec(j.Pos)), mouse)))
                    .Where(t => t.D < 10).OrderBy(t => t.D).Select(t => t.Name).FirstOrDefault();
            }
        }
        view.End();

        DrawJointPanel();
    }

    private static Vector3 ToVec(float[] p) => new(p[0], p[1], p[2]);

    private const float RingPixels = 55f;   // 旋转环在屏幕上的半径
    private const int RingSegments = 48;

    private static readonly Vector4[] RingColors =
        [new(0.95f, 0.35f, 0.35f, 1), new(0.4f, 0.9f, 0.4f, 1), new(0.35f, 0.6f, 1f, 1)];

    private static string[] RingAxisNames => [Loc.T("前后俯仰（绕左右轴）"), Loc.T("水平转（绕竖直轴）"), Loc.T("侧倾（绕前后轴）")];

    /// <summary>绕 axis 号轴（模型系 x / y / z）的环上第 k 个点。</summary>
    private static Vector3 RingPoint(Vector3 c, float r, int axis, int k)
    {
        var (s, co) = MathF.SinCos(k * 2 * MathF.PI / RingSegments);
        return axis switch
        {
            0 => c + r * new Vector3(0, co, s),
            1 => c + r * new Vector3(s, 0, co),
            _ => c + r * new Vector3(co, s, 0),
        };
    }

    private static Vector3 AxisVector(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;

    /// <summary>鼠标离哪个环最近（8 像素内，前半圈优先）；同时给出那一点按右手定则正向转动时在屏幕上的方向（每弧度多少像素）。</summary>
    private static (int Axis, Vector2 Tangent)? HitRing(Vector3 c, float r, Vector2 mouse, Func<Vector3, Vector2> project, Func<Vector3, float> depth)
    {
        (int Axis, Vector2 Tangent)? best = null;
        var bestScore = 8f;
        for (var axis = 0; axis < 3; axis++)
            for (var k = 0; k < RingSegments; k++)
            {
                var p = RingPoint(c, r, axis, k);
                var q = RingPoint(c, r, axis, k + 1);
                var a = project(p);
                var b = project(q);
                var ab = b - a;
                var t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(mouse - a, ab) / ab.LengthSquared(), 0, 1);
                var d = Vector2.Distance(mouse, a + ab * t);
                var score = d + (depth(p) < depth(c) ? 3f : 0f);
                if (score >= bestScore) continue;
                // 正向转动的切线：axis × (p - c)，投到屏幕；步长取小量再按弧度归一
                var tangent3 = Vector3.Cross(AxisVector(axis), p - c);
                var tangent = (project(p + tangent3 * 0.01f) - project(p)) / 0.01f;
                if (tangent.LengthSquared() < 1e-4f) continue;   // 环正好侧对屏幕的那一点拖不动
                bestScore = score;
                best = (axis, tangent);
            }
        return best;
    }

    private static void DrawRings(ImDrawListPtr dl, Vector3 c, float r, int? active, Func<Vector3, Vector2> project, Func<Vector3, float> depth)
    {
        var front = depth(c);
        for (var axis = 0; axis < 3; axis++)
        {
            var color = RingColors[axis];
            var width = active == axis ? 3.5f : 2f;
            for (var k = 0; k < RingSegments; k++)
            {
                var p = RingPoint(c, r, axis, k);
                var q = RingPoint(c, r, axis, k + 1);
                var back = depth((p + q) / 2) < front;
                var alpha = active == axis ? 1f : back ? 0.25f : 0.85f;
                dl.AddLine(project(p), project(q), ImGui.GetColorU32(color with { W = alpha }), width);
            }
        }
    }

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
            if (_mergeSession)
            {
                ImGui.SameLine();
                var onion = _config.ShowOnion;
                if (ImGui.Checkbox(Loc.T("洋葱皮"), ref onion))
                {
                    _config.ShowOnion = onion;
                    _pi.SavePluginConfig(_config);
                    RequestRetarget(false);
                }
                ItemTip(Loc.T("叠加其他人的骨架（半透明），按复刻站位摆在相对位置，方便对着别人调"));
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
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tip);
    }

    // ---------------- 调整页

    /// <summary>拖动中只刷新预览，松手才应用到角色；右键恢复默认值。</summary>
    private void Slider(string label, ref float value, float min, float max, string format, float def, string tip)
    {
        // 右边统一留出调整页最长标签的宽度：所有滑杆右端对齐，标签排成一列（英文标签更长，也不会被裁掉）
        var shown = label.Split("##")[0];
        var labelW = Math.Max(_sliderLabelW, ImGui.CalcTextSize(shown).X + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.SetNextItemWidth(Math.Max(120 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - labelW));
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

    // 调整页滑杆的标签列宽（每帧按当前语言算一次）
    private float _sliderLabelW;
    private static readonly string[] SliderLabels = ["头部跟脸##facehead", "强度##hand", "完全生效##near", "不再生效##far", "校正上限##upmax", "联动强度##helper",
                                                     "俯仰##rootx", "水平转##rooty", "侧倾##rootz", "左右##offx", "上下##offy", "前后##offz"];

    private void DrawTuningTab()
    {
        _sliderLabelW = AmplitudeGroups.Select(g => g.Label).Concat(FaceGroups.Select(g => g.Label)).Concat(SliderLabels)
                                       .Max(l => ImGui.CalcTextSize(Loc.T(l).Split("##")[0]).X) + ImGui.GetStyle().ItemInnerSpacing.X;
        if (_session is not null)
        {
            DrawCorrectionBar();
            ImGui.Separator();
        }
        DrawFaceSection();
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
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
                    _config.DefaultPreset = key;
                    _pi.SavePluginConfig(_config);
                    RequestRetarget(_config.PushOnRelease);
                }
            ImGui.EndCombo();
        }
        ItemTip(Loc.T("预设：插画会把颈/头幅度设为 0.7（游戏实测合适）") + "\n"
                + Loc.T("新图片都从预设默认值开始；这张图调好的值用上方「保存修正」存进这张图，下次打开自动恢复。右边按钮恢复默认"));
        ImGui.SameLine();
        if (IconButton("##resetAll", FontAwesomeIcon.UndoAlt, Loc.T("全部恢复预设值（含手动偏移）")))
        {
            _params = RetargetParams.Preset(_preset);
            RequestRetarget(_config.PushOnRelease);
        }

        if (ImGui.CollapsingHeader(Loc.T("整体旋转 / 位移"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            // 整个角色绕髋部转、整体挪动（模型系）；人歪了、要抬高放低时用，复刻站位不会抵消
            string[] rot = ["俯仰##rootx", "水平转##rooty", "侧倾##rootz"];
            string[] rotTips = ["整个人前后倾（绕左右轴）", "整个人原地转向（绕竖直轴）", "整个人左右歪（绕前后轴）"];
            float[] rotMax = [90, 180, 90];
            for (var i = 0; i < 3; i++)
                Slider(Loc.T(rot[i]), ref _params.RootRotation[i], -rotMax[i], rotMax[i], "%.0f°", 0, Loc.T(rotTips[i]));
            string[] off = ["左右##offx", "上下##offy", "前后##offz"];
            string[] offTips = ["整个人左右挪（角色自己的左右）", "整个人抬高 / 放低（坐在椅子上、抬起来的人）", "整个人前后挪（角色自己的前后）"];
            for (var i = 0; i < 3; i++)
            {
                var cm = _params.RootOffset[i] * 100;
                Slider(Loc.T(off[i]), ref cm, -100, 100, "%.0f cm", 0, Loc.T(offTips[i]));
                _params.RootOffset[i] = cm / 100;
            }
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
            var flat = _params.FlatFeet;
            Toggle(Loc.T("踩地的脚放平"), ref flat, Loc.T("识别出的脚常常脚尖微翘、只有脚跟着地：踩在地上的脚换成游戏里平放的样子（只转脚，不动腿）。\n踮脚、勾脚这种明显故意的不会动"));
            _params.FlatFeet = flat;
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
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.F("更新日志（{0}）", UpdateWindow.Current))) ShowChangelog?.Invoke();

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
        else Wrapped(problem, Yellow);

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
        Wrapped(Loc.F("Posegraph HTTP（127.0.0.1:{0}，复刻镜头需要）：{1}", _config.Port, (_server.IsRunning ? Loc.T("运行中") : Loc.T("已停止"))),
                ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
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

    private static void Fit(string text, Vector4 color) => UiText.Fit(text, color);

    /// <summary>按窗口宽度自动换行的彩色文字（长提示不会被右边裁掉）。</summary>
    private static void Wrapped(string text, Vector4 color)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextWrapped(text);
    }

    /// <summary>设置页的后端更新区域。返回配置是否有改动。</summary>
    private bool DrawUpdateSection()
    {
        var changed = false;
        ImGui.Text(Loc.F("后端版本 {0}，插件 {1}", _installedBackend?.Version ?? Loc.T("未知"), BackendUpdater.PluginTag));
        if (_packageRuntime is null)
            Wrapped(Loc.T("（源码安装：请用 git 更新到同版本）"), ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        else if (_updater.Busy)
            Wrapped(_updater.Status ?? Loc.T("更新中…"), Yellow);
        else if (_backendOutdated)
        {
            if (IconButton("##updateBackend", FontAwesomeIcon.CloudDownloadAlt,
                           Loc.T("从 GitHub 下载同版本的后端源码替换 backend\\src；新功能要的组件（面捕、深度图的 Python 包和模型）缺哪个补哪个，不用重新下载整个一键包。会先停止后端，更新后自动启动")))
                UpdateBackend();
            ImGui.SameLine();
            ImGui.TextColored(Yellow, Loc.F("可更新到 {0}", BackendUpdater.PluginTag));
            if (_missingComponents.Length > 0)
                Wrapped(Loc.F("还缺组件：{0}（共约 {1:0} MB，更新时一起下载）", string.Join("、", _missingComponents.Select(c => c.Label)),
                              _missingComponents.Sum(c => c.Size) / 1048576.0), Yellow);
            else
                Wrapped(Loc.T("这次更新会补齐新功能要的组件（面捕约 64 MB、深度图约 400 MB），已经有的不会重下"), ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
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
        // 下拉框占满到右侧标签前（英文选项更长），放不下时省略
        var releaseW = Math.Max(180 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(Loc.T("模型释放")).X - ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.SetNextItemWidth(releaseW);
        var current = ReleasePolicies.FirstOrDefault(p => p.Seconds == _config.ReleaseAfter).Label is { } known
            ? Loc.T(known) : Loc.F("空闲 {0} 秒后释放", _config.ReleaseAfter);
        if (ImGui.BeginCombo(Loc.T("模型释放"), UiText.ComboPreview(current, releaseW)))
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
        // 快速识别删掉了 MHR 的修正形变权重，剩约 29MB，直接放显卡：这一项不再起作用
        using (ImRaii.Disabled(_config.BackendDevice == "cpu" || _config.FastRecognition))
            changed |= Check(Loc.T("GPU 模式下 MHR 放 CPU"), _config.MhrOnCpu, v => _config.MhrOnCpu = v,
                             _config.FastRecognition ? Loc.T("快速识别开着时 MHR 只有约 29MB，直接放显卡，这一项不起作用")
                                                     : Loc.T("显存约省 750MB，结果一致，每张约慢 0.5 秒"));
        changed |= Check(Loc.T("快速识别"), _config.FastRecognition, v => _config.FastRecognition = v, Loc.T("移植自 Fast SAM 3D Body：左右手合成一批识别，MHR 不算修正形变（只影响网格表面，骨架不变）。\nRTX 4060 笔记本实测：单人每张约 1.3 秒 → 0.6 秒，两人约 1.7 秒 → 0.8 秒；显存多约 0.1GB，内存少约 0.6GB。\n和关掉时的结果差别：关节平均约 3mm、画面上约 3px，肉眼基本看不出。CPU 模式只快约 10%。\n改了要重新加载模型；已识别过的图片读的是缓存"));
        changed |= Check(Loc.T("深度辅助识别"), _config.UseDepth, v => _config.UseDepth = v, Loc.T("用 MoGe 估照片的深度：镜头焦距更准、多人时按前后关系修正遮罩和远近、看得见脚和地面时按地面扶正（斜拍、俯拍更稳）。\n代价（RTX 4070 实测）：单人每张多约 0.5 秒（深度模型本身约 0.05 秒，其余是找地面等计算），「识别后立即释放」时含重新加载约多 2 秒；显存峰值多约 0.75GB，内存多约 2GB。\nCPU 模式深度模型每张约 1 秒。\n已识别过的图片读的是缓存，改了要点「重新识别」才看得到区别"));
        changed |= DrawRecognitionConcurrencySettings();
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
