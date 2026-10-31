using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Posegraph.Library;

namespace Posegraph.Lighting;

/// <summary>
/// 一键布光：选一套方案，按当前角色的脸和 GPose 相机算出每盏灯摆哪，生成游戏灯。
/// 「跟随镜头」开着时相机动了灯跟着转；「跟随角色」开着时 Posegraph 给这个角色换了姿势、或角色被挪动 / 转身后自动重新读脸再摆：
/// 主光那一侧沿用手动套用时定的（脸转过正面也不左右翻），人的位置、朝向在 FollowSeconds 内平滑过渡过去（视频播放时不跳）。
/// 打光范围（脸 / 半身 / 全身）默认看游戏镜头拍到多少自动换，灯围着那一块的中心摆。有动态灯效的灯每帧更新。主窗口的图在图库里存过灯光时，给角色套这张图的姿势时一并恢复。
/// 退出 GPose、插件卸载时清掉所有灯。
/// </summary>
public sealed partial class LightingService : IDisposable
{
    private const int MovePollTicks = 30;          // 每隔这么多帧看一次角色挪没挪
    private const float MoveThreshold = 0.01f;     // 挪了 1 厘米以上
    private const float TurnThresholdDeg = 1f;     // 或转了 1° 以上就重新摆
    private const int FramingPollTicks = 15;       // 自动打光范围：每隔这么多帧看一次镜头拍到哪
    private const float ScreenMargin = 0.03f;      // 离画面边缘这么近（画面的比例）以内不算拍到
    private const float FollowSeconds = 0.4f;      // 跟随角色时灯平滑过渡的时间常数

    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly PoseApplier _applier;
    private readonly Configuration _config;
    private readonly GameLights _lights = new();
    private readonly List<(nint Handle, LightKind Kind)> _spawned = [];
    private readonly IGameGui _gameGui;
    private readonly Stopwatch _clock = Stopwatch.StartNew();   // 动态灯效的时间
    private Subject? _subject;
    private Subject? _target;            // 跟随角色读到的新位置：_subject 每帧往它靠
    private float? _side;                // 主光那一侧：手动套用时定，跟随时沿用
    private List<(Vector3 A, Vector3 B)> _body = [];
    private Vector3? _lastCamera;
    private int _tick;
    private int? _actor;                 // 打光的角色（同主窗口的选法，null 为 GPose 目标）
    private int? _actorIndex;            // 读脸时角色的对象序号：盯它挪没挪
    private Vector3 _modelPosition;
    private Quaternion _modelRotation;
    private bool _following;             // 正在因为角色动了重新摆
    private string? _lastImage;          // 上次套姿势时主窗口的图（换了图才从图库恢复灯光）

    public LightingService(IFramework framework, IClientState clientState, PoseApplier applier, Configuration config, IGameGui gameGui)
    {
        _framework = framework;
        _clientState = clientState;
        _applier = applier;
        _config = config;
        _gameGui = gameGui;
        _framework.Update += OnUpdate;
        _applier.Applied += OnPoseApplied;
    }

    /// <summary>当前套用的方案（灯可在界面里逐盏改，改的是这份拷贝）；null 为没有灯。</summary>
    public LightRig? Active { get; private set; }

    public List<LightSpec> Lights { get; private set; } = [];

    public RigTweaks Tweaks { get; set; } = RigTweaks.Default;

    public bool HasLights => _spawned.Count > 0;

    public string? Error { get; private set; }

    /// <summary>3D 视图里选中的灯（Lights 的下标）。</summary>
    public int? SelectedLight { get; set; }

    /// <summary>独显：只亮这一盏（其他灯保留但不发光）；Muted：关掉的灯。都不改方案本身，存方案时照常保存。</summary>
    public int? Solo { get; set; }

    public HashSet<int> Muted { get; } = [];

    /// <summary>读到的人，已按打光范围定好灯的中心（3D 视图、拖灯反算用）。</summary>
    public Subject? Subject => _subject is { } s ? LightRigs.Framed(s, Framing) : null;

    /// <summary>现在的打光范围：设置里选了就是它，「自动」时看游戏镜头拍到多少。</summary>
    public LightFraming Framing { get; private set; }

    /// <summary>读脸时顺带记下的身体骨架（世界坐标线段），3D 视图里画人。</summary>
    public IReadOnlyList<(Vector3 A, Vector3 B)> Body => _body;

    /// <summary>GPose 相机（每帧更新）。摆灯时用它的位置；3D 视图里画镜头。</summary>
    public CameraPose? CameraPose { get; private set; }

    /// <summary>灯摆的时候用的镜头位置：跟随镜头时是当前镜头，否则是套用 / 重新摆时的镜头。</summary>
    public Vector3? Camera => _config.LightFollowCamera ? CameraPose?.Position ?? _lastCamera : _lastCamera;

    /// <summary>动态灯效的时间（秒）。</summary>
    public float Time => (float)_clock.Elapsed.TotalSeconds;

    /// <summary>主窗口当前的图（图库哈希）和它存的灯光；没有为 null。由 Plugin 接上。</summary>
    public Func<(string Image, SavedLighting? Saved)?>? CurrentImage { get; set; }

    /// <summary>自动做的事（跟随角色、从图库恢复）的结果，给灯光窗口的标题栏显示。</summary>
    public event Action<string, bool>? Notice;

    /// <summary>这盏灯现在是否发光（独显 / 关掉之后）。</summary>
    public bool IsLit(int i) => Solo is { } s ? s == i : !Muted.Contains(i);

    /// <summary>所有方案：内置 + 自己存的。</summary>
    public IEnumerable<LightRig> Rigs => LightRigs.Builtin.Concat(CustomRigs);

    public IEnumerable<LightRig> CustomRigs => _config.LightRigs.Select(json =>
    {
        try { return JsonSerializer.Deserialize<LightRig>(json); }
        catch (JsonException) { return null; }
    }).OfType<LightRig>();

    /// <summary>读角色的脸，套用方案。actor 同主窗口（null 为 GPose 目标）。返回失败原因，成功为 null。
    /// follow：跟随角色的自动重摆——主光那一侧沿用之前的，人的位置平滑过渡过去。</summary>
    public async Task<string?> ApplyAsync(LightRig rig, int? actor, bool follow = false)
    {
        var read = await ReadSubjectAsync(actor);
        if (read is not { } subject) return Error = Loc.T("读不到角色的脸（不在 GPose、没选角色或 Brio 不可用）");
        if (!follow || _side is null)
            _side = await _framework.RunOnFrameworkThread(() => LightRigs.SideOf(subject, GposeCamera.WorldPosition()));
        subject = subject with { Side = _side };
        if (follow && _subject is not null && Active?.Id == rig.Id && Lights.Count == rig.Lights.Length)
        {
            _target = subject;   // 平滑过渡：OnUpdate 每帧靠过去
            _actor = actor;
            return Error = null;
        }
        _subject = subject;
        _target = null;
        _actor = actor;
        Framing = await _framework.RunOnFrameworkThread(() => ResolveFraming(subject));
        if (Active?.Id != rig.Id || Lights.Count != rig.Lights.Length)
        {
            SelectedLight = Solo = null;
            Muted.Clear();
        }
        Active = rig;
        Lights = [.. rig.Lights];
        await _framework.RunOnFrameworkThread(Rebuild);
        return Error = null;
    }

    /// <summary>角色动过后重新读脸，再摆一次。</summary>
    public async Task<string?> ReplaceAsync(int? actor, bool follow = false)
        => Active is { } rig ? await ApplyAsync(rig with { Lights = [.. Lights] }, actor, follow) : null;

    /// <summary>界面改了某盏灯或全局微调：按已读到的脸重摆（不重新读角色）。</summary>
    public void Refresh() => _framework.RunOnFrameworkThread(Rebuild);

    /// <summary>照片配色：按照片给现在的灯换颜色（见 LightRigs.WithPhotoColors）。返回换了颜色的盏数和加上的盏数。</summary>
    public (int Changed, int Added) ApplyPhotoColors(Backend.PhotoLighting photo)
    {
        var before = Lights;
        Lights = LightRigs.WithPhotoColors(before, photo);
        var changed = before.Where((l, i) => !LightRigs.SameColor(l, Lights[i])).Count();
        if (Lights.Count != before.Count) Solo = null;
        Refresh();
        return (changed, Lights.Count - before.Count);
    }

    public void Clear() => _framework.RunOnFrameworkThread(() =>
    {
        _lights.DestroyAll();
        _spawned.Clear();
        Active = null;
        Lights = [];
        SelectedLight = Solo = null;
        Muted.Clear();
    });

    /// <summary>把当前的灯（含逐盏改过的）存成自己的方案。</summary>
    public void SaveCustom(string name)
    {
        var rig = new LightRig($"custom-{DateTime.Now:yyyyMMddHHmmss}", name, Loc.T("自己存的方案"), [.. Lights], Custom: true);
        _config.LightRigs.Add(JsonSerializer.Serialize(rig));
    }

    public void DeleteCustom(LightRig rig)
        => _config.LightRigs.RemoveAll(json => json.Contains($"\"{rig.Id}\"", StringComparison.Ordinal));

    /// <summary>现在的灯（含逐盏改过的、整体亮度这些微调），存进图库用。没有灯时为 null。</summary>
    public SavedLighting? Snapshot()
        => Active is { } rig ? new SavedLighting(rig with { Lights = [.. Lights] }, Tweaks, DateTime.Now) : null;

    /// <summary>设置改了打光范围：马上按新的范围重摆。</summary>
    public void RefreshFraming() => _framework.RunOnFrameworkThread(() =>
    {
        if (_subject is { } s) Framing = ResolveFraming(s);
        Rebuild();
    });

    /// <summary>
    /// 打光范围：设置里选了就用选的；「自动」时看脚、腰有没有在游戏画面里——脚拍到了是全身，腰拍到了是半身，否则照脸。
    /// 必须在主线程调用。
    /// </summary>
    private LightFraming ResolveFraming(Subject s)
    {
        switch (_config.LightFraming)
        {
            case "face": return LightFraming.Face;
            case "half": return LightFraming.Half;
            case "full": return LightFraming.Full;
        }
        if (s.Feet is { } feet && OnScreen(feet)) return LightFraming.Full;
        if (s.Pelvis is { } pelvis && OnScreen(pelvis)) return LightFraming.Half;
        return LightFraming.Face;
    }

    private bool OnScreen(Vector3 world)
    {
        if (!_gameGui.WorldToScreen(world, out var screen)) return false;
        var vp = ImGuiHelpers.MainViewport;
        var uv = (screen - vp.Pos) / vp.Size;
        return uv.X > ScreenMargin && uv.X < 1 - ScreenMargin && uv.Y > ScreenMargin && uv.Y < 1 - ScreenMargin;
    }

    private void Rebuild()
    {
        if (Subject is not { } subject || !_clientState.IsGPosing) return;
        var camera = GposeCamera.WorldPosition();
        _lastCamera = camera;
        var states = LightRigs.Solve(Lights, subject, camera, Tweaks, Time);
        for (var i = 0; i < states.Count; i++)
            if (!IsLit(i)) states[i] = states[i] with { Color = Vector3.Zero };
        // 种类或数量变了就重建，否则原地改（避免闪）
        if (states.Count != _spawned.Count || states.Where((s, i) => s.Kind != _spawned[i].Kind).Any())
        {
            _lights.DestroyAll();
            _spawned.Clear();
            foreach (var s in states)
            {
                var handle = _lights.Spawn(s);
                if (handle == 0)
                {
                    Error = Loc.T("游戏没能生成灯");
                    continue;
                }
                _spawned.Add((handle, s.Kind));
            }
            return;
        }
        for (var i = 0; i < states.Count; i++) _lights.Update(_spawned[i].Handle, states[i]);
    }

    private void OnUpdate(IFramework framework)
    {
        CameraPose = _clientState.IsGPosing ? GposeCamera.Pose() : null;
        if (_spawned.Count == 0) return;
        if (!_clientState.IsGPosing)
        {
            // 退出 GPose：游戏会切场景，灯必须在那之前清掉
            _lights.DestroyAll();
            _spawned.Clear();
            Active = null;
            SelectedLight = Solo = null;
            Muted.Clear();
            return;
        }
        _tick++;
        if (_config.LightFollowSubject && _tick % MovePollTicks == 0 && !_following && _actorIndex is { } index)
            _ = CheckMovedAsync(index);
        if (_target is { } target && _subject is { } current)
        {
            var k = 1f - MathF.Exp(-(float)framework.UpdateDelta.TotalSeconds / FollowSeconds);
            var next = current.Toward(target, k);
            var done = Vector3.Distance(next.Face, target.Face) < 0.002f && Vector3.Dot(next.Forward, target.Forward) > 0.9999f;
            _subject = done ? target : next;
            if (done) _target = null;
            Rebuild();
            return;
        }
        if (_config.LightFraming == "auto" && _tick % FramingPollTicks == 0 && _subject is { } raw)
        {
            var framing = ResolveFraming(raw);
            if (framing != Framing)
            {
                Framing = framing;
                Rebuild();
            }
        }
        if (Lights.Any(l => l.Effect != LightEffect.None))
        {
            Rebuild();
            return;
        }
        if (!_config.LightFollowCamera || _tick % 6 != 0) return;
        if (GposeCamera.WorldPosition() is { } cam && (_lastCamera is not { } last || Vector3.Distance(cam, last) > 0.02f))
            Rebuild();
    }

    /// <summary>
    /// Posegraph 给某个角色套了姿势：主窗口的图换过且图库里存了灯光 → 恢复那套灯；否则打着光的正是这个角色 → 按新姿势重新摆。
    /// </summary>
    private void OnPoseApplied(int? actor) => _ = Task.Run(async () =>
    {
        try
        {
            if (!_clientState.IsGPosing) return;
            var current = CurrentImage?.Invoke();
            var newImage = current is { } c && c.Image != _lastImage;
            if (current is { } cur) _lastImage = cur.Image;
            if (newImage && _config.LightRestoreFromLibrary && current?.Saved is { } saved)
            {
                Tweaks = saved.Tweaks;
                var error = await ApplyAsync(saved.Rig, actor);
                Notice?.Invoke(error ?? Loc.F("已按图库里这张图存的灯光打光（{0}）", Loc.T(saved.Rig.Name)), error is null);
                return;
            }
            if (Active is null || !_config.LightFollowSubject || !await SameActorAsync(actor)) return;
            await FollowAsync();
        }
        catch (Exception ex)
        {
            Notice?.Invoke(ex.Message, false);
        }
    });

    private async Task<bool> SameActorAsync(int? actor)
        => actor == _actor || (await _applier.IdentifyAsync(actor))?.Index is { } a && a == _actorIndex;

    private async Task FollowAsync()
    {
        if (_following) return;
        _following = true;
        try
        {
            var error = await ReplaceAsync(_actor, follow: true);
            if (error is not null) Notice?.Invoke(error, false);
        }
        finally { _following = false; }
    }

    /// <summary>角色被挪动或转身（Brio 移动、换站位）：位置或朝向变了就重新读脸再摆。</summary>
    private async Task CheckMovedAsync(int index)
    {
        var model = await _applier.GetTransformAsync(index);
        if (model is null || _following) return;
        var position = new Vector3(model.Position[0], model.Position[1], model.Position[2]);
        var rotation = new Quaternion(model.Rotation[0], model.Rotation[1], model.Rotation[2], model.Rotation[3]);
        var turned = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(rotation, _modelRotation)), 0f, 1f)) * 180f / MathF.PI;
        if (Vector3.Distance(position, _modelPosition) < MoveThreshold && turned < TurnThresholdDeg) return;
        await FollowAsync();
    }

    /// <summary>
    /// 读角色的脸：Brio 姿势里两只眼睛的中点当脸的位置，头顶方向 = 脖子 → 头，
    /// 脸朝向 = 头顶 × 角色右手方向（再用鼻子校正正负）；体型比例 = 眼睛离脚底的高度 / 人族中原的眼高。
    /// </summary>
    private async Task<Subject?> ReadSubjectAsync(int? actor)
    {
        if (await _applier.IdentifyAsync(actor) is not { } id) return null;
        var poseJson = await _applier.GetPoseAsync(actor);
        var model = await _applier.GetTransformAsync(id.Index);
        if (poseJson is null || model is null) return null;
        Dictionary<string, Vector3> bones;
        try
        {
            using var doc = JsonDocument.Parse(poseJson);
            bones = doc.RootElement.GetProperty("Bones").EnumerateObject()
                       .Where(b => b.Value.TryGetProperty("Position", out _))
                       .ToDictionary(b => b.Name, b => ParseVector(b.Value.GetProperty("Position").GetString()));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
        if (!bones.TryGetValue("j_kao", out var head) || !bones.TryGetValue("j_kubi", out var neck)) return null;
        var position = new Vector3(model.Position[0], model.Position[1], model.Position[2]);
        var rotation = new Quaternion(model.Rotation[0], model.Rotation[1], model.Rotation[2], model.Rotation[3]);
        var scale = new Vector3(model.Scale[0], model.Scale[1], model.Scale[2]);
        Vector3 World(Vector3 p) => position + Vector3.Transform(p * scale, rotation);
        _actorIndex = id.Index;
        _modelPosition = position;
        _modelRotation = rotation;

        var hasEyes = bones.TryGetValue("j_f_eye_l", out var eyeL) & bones.TryGetValue("j_f_eye_r", out var eyeR);
        var faceModel = hasEyes ? (eyeL + eyeR) / 2 : head + Vector3.Normalize(head - neck) * 0.08f;
        var face = World(faceModel);
        var up = Vector3.Normalize(World(head) - World(neck));
        Vector3 forward;
        if (hasEyes)
        {
            var right = World(eyeR) - World(eyeL);
            forward = Vector3.Normalize(Vector3.Cross(up, right));
            if (bones.TryGetValue("j_f_uhana", out var nose) && Vector3.Dot(forward, World(nose) - face) < 0) forward = -forward;
        }
        else forward = Vector3.Transform(Vector3.UnitZ, rotation);
        var eyeHeight = faceModel.Y * scale.Y;
        _body = [.. BodyBones.Where(b => bones.ContainsKey(b.Child) && bones.ContainsKey(b.Parent))
                             .Select(b => (World(bones[b.Parent]), World(bones[b.Child])))];
        // 脚：脚踝、脚尖里最低的那个（站着、坐着、躺着都是离地最近的一点）
        var feet = FeetBones.Where(bones.ContainsKey).Select(b => World(bones[b])).OrderBy(p => p.Y).Cast<Vector3?>().FirstOrDefault();
        return new Subject(face, forward, up, Math.Clamp(eyeHeight / 1.58f, 0.4f, 1.5f))
        {
            Pelvis = bones.TryGetValue("j_kosi", out var pelvis) ? World(pelvis) : null,
            Feet = feet,
        };
    }

    private static readonly string[] FeetBones = ["j_asi_d_l", "j_asi_d_r", "j_asi_e_l", "j_asi_e_r"];

    /// <summary>3D 视图里画的身体骨架（子 → 父）。</summary>
    private static readonly (string Child, string Parent)[] BodyBones =
    [
        ("j_sebo_a", "j_kosi"), ("j_sebo_b", "j_sebo_a"), ("j_sebo_c", "j_sebo_b"), ("j_kubi", "j_sebo_c"), ("j_kao", "j_kubi"),
        ("j_sako_l", "j_sebo_c"), ("j_ude_a_l", "j_sako_l"), ("j_ude_b_l", "j_ude_a_l"), ("j_te_l", "j_ude_b_l"),
        ("j_sako_r", "j_sebo_c"), ("j_ude_a_r", "j_sako_r"), ("j_ude_b_r", "j_ude_a_r"), ("j_te_r", "j_ude_b_r"),
        ("j_asi_a_l", "j_kosi"), ("j_asi_c_l", "j_asi_a_l"), ("j_asi_d_l", "j_asi_c_l"), ("j_asi_e_l", "j_asi_d_l"),
        ("j_asi_a_r", "j_kosi"), ("j_asi_c_r", "j_asi_a_r"), ("j_asi_d_r", "j_asi_c_r"), ("j_asi_e_r", "j_asi_d_r"),
    ];

    private static Vector3 ParseVector(string? s)
    {
        var p = (s ?? "").Split(',');
        return new Vector3(float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                           float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                           float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _applier.Applied -= OnPoseApplied;
        // 卸载插件可能不在主线程：同步回主线程清灯
        _framework.RunOnFrameworkThread(() =>
        {
            _lights.DestroyAll();
            _spawned.Clear();
        }).Wait(TimeSpan.FromSeconds(2));
    }
}
