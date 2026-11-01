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
/// 照谁（设置 LightPeople）：单人 = 只照主窗口选的角色；一组 = 勾选的人当成一组，一套灯拉远、光斑放宽罩住所有人；
/// 每人一套 = 每个人身上各摆一套（主光都在画面同一侧，灯的范围收紧到主要照自己）；自动 = 离得近的人合用一套、远的另起一套。
/// 多人时可以选中某一套单独调（SelectGroup）：这一套有自己的一份灯，其他套照旧用共用的那份。
/// 人多灯多时最多 MaxLights 盏（离镜头远的组先不打），阴影只给离镜头最近的 MaxShadowGroups 组。
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
    private const int MaxLights = 32;              // 多人打光时最多生成这么多盏灯（游戏能同时渲染多少还没实测，先保守）
    private const int MaxShadowGroups = 2;         // 只有离镜头最近的这么多组开阴影（阴影最费性能）
    public const float AutoGroupMeters = 2.5f;     // 自动分组：脸的水平距离在这以内的人合用一套灯

    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly PoseApplier _applier;
    private readonly Configuration _config;
    private readonly GameLights _lights = new();
    private readonly List<(nint Handle, LightKind Kind)> _spawned = [];
    private readonly IGameGui _gameGui;
    private readonly Stopwatch _clock = Stopwatch.StartNew();   // 动态灯效的时间
    private List<Subject> _people = [];  // 读到的被照的人（第一个是主窗口选的角色）
    private List<Subject>? _targets;     // 跟随角色读到的新位置：_people 每帧往它靠
    private float? _side;                // 主光那一侧：手动套用时定，跟随时沿用
    private List<(Vector3 A, Vector3 B)> _body = [];
    private Vector3? _lastCamera;
    private int _tick;
    private int? _actor;                 // 打光的角色（同主窗口的选法，null 为 GPose 目标）
    private List<int> _indices = [];     // 读脸时各人的对象序号：盯他们挪没挪
    private List<(Vector3 Position, Quaternion Rotation)> _models = [];
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

    /// <summary>
    /// 正在调的灯：选中了某一套就是那一套自己的一份，否则是所有套共用的那份（逐盏调整、3D 布光、照片配色都改它）。
    /// </summary>
    public List<LightSpec> Lights
    {
        get => EditKey is { } key && _overrides.TryGetValue(key, out var own) ? own : _shared;
        private set
        {
            if (EditKey is { } key && _overrides.ContainsKey(key)) _overrides[key] = value;
            else _shared = value;
        }
    }

    private List<LightSpec> _shared = [];                                    // 所有套共用的灯
    private readonly Dictionary<string, List<LightSpec>> _overrides = new();  // 单独调过的套（键：这套照的人，见 GroupKeys）

    /// <summary>选中单独调的那一套（Groups 的下标）；null 为所有套一起调。</summary>
    public int? SelectedGroup { get; private set; }

    private string? EditKey => SelectedGroup is { } g && g < GroupKeys.Count ? GroupKeys[g] : null;

    /// <summary>每一套照的人（对象序号）和据此生成的键（同一批人换了站位还认得出是同一套）。</summary>
    public IReadOnlyList<int[]> GroupMembers { get; private set; } = [];

    public IReadOnlyList<string> GroupKeys { get; private set; } = [];

    /// <summary>第 g 套用的灯：单独调过的用自己的，否则用共用的。</summary>
    public List<LightSpec> LightsFor(int g) => g < GroupKeys.Count && _overrides.TryGetValue(GroupKeys[g], out var own) ? own : _shared;

    public bool IsOwn(int g) => g < GroupKeys.Count && _overrides.ContainsKey(GroupKeys[g]);

    /// <summary>
    /// 选中某一套单独调：第一次选时从共用的那份复制一份给它。回到「所有套一起调」时，单独那份和共用的一样（没改过）就去掉，
    /// 以后改共用的它也跟着变。
    /// </summary>
    public void SelectGroup(int? group)
    {
        if (SelectedGroup is { } old && old < GroupKeys.Count && _overrides.TryGetValue(GroupKeys[old], out var own) && own.SequenceEqual(_shared))
            _overrides.Remove(GroupKeys[old]);
        SelectedGroup = group is { } g && g >= 0 && g < GroupKeys.Count && GroupKeys.Count > 1 ? g : null;
        if (EditKey is { } key && !_overrides.ContainsKey(key)) _overrides[key] = [.. _shared];
        SelectedLight = null;
    }

    /// <summary>第 g 套恢复成和其他套一样（去掉它单独的那份）。</summary>
    public void ResetGroup(int g)
    {
        if (g < GroupKeys.Count && _overrides.Remove(GroupKeys[g]))
        {
            if (SelectedGroup == g) _overrides[GroupKeys[g]] = [.. _shared];   // 还选着就接着单独调，从共用的重新开始
            Refresh();
        }
    }

    public RigTweaks Tweaks { get; set; } = RigTweaks.Default;

    public bool HasLights => _spawned.Count > 0;

    public string? Error { get; private set; }

    /// <summary>3D 视图里选中的灯（Lights 的下标）。</summary>
    public int? SelectedLight { get; set; }

    /// <summary>独显：只亮这一盏（其他灯保留但不发光）；Muted：关掉的灯。都不改方案本身，存方案时照常保存。</summary>
    public int? Solo { get; set; }

    public HashSet<int> Muted { get; } = [];

    /// <summary>正在调的那一组（选中的那套，否则主角那一组；读到的人已按打光范围、分组定好灯的中心）：3D 视图、拖灯反算用。</summary>
    public Subject? Subject => Groups.Count == 0 ? null : Groups[SelectedGroup is { } g && g < Groups.Count ? g : 0];

    /// <summary>现在打光的各组（第一组含主窗口选的角色），每组一套灯。</summary>
    public IReadOnlyList<Subject> Groups { get; private set; } = [];

    /// <summary>被照的人数；灯太多时没打的组数（离镜头远的）。</summary>
    public int PeopleCount => _people.Count;

    public int DroppedGroups { get; private set; }

    /// <summary>勾选要一起照的其他角色（主窗口角色列表的序号，Plugin 接上：列表里没被去掉勾的）。</summary>
    public Func<IReadOnlyList<int>>? Others { get; set; }

    /// <summary>灯光窗口里去掉勾的角色（不照）。</summary>
    public HashSet<int> Excluded { get; } = [];

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
        var read = await ReadPeopleAsync(actor);
        if (read.Count == 0) return Error = Loc.T("读不到角色的脸（不在 GPose、没选角色或 Brio 不可用）");
        if (!follow || _side is null)
            _side = await _framework.RunOnFrameworkThread(() => LightRigs.SideOf(read[0].Subject, GposeCamera.WorldPosition()));
        var people = read.Select(r => r.Subject with { Side = _side }).ToList();
        var samePeople = read.Select(r => r.Index).SequenceEqual(_indices);
        _indices = [.. read.Select(r => r.Index)];
        _models = [.. read.Select(r => (r.Position, r.Rotation))];
        _body = [.. read.SelectMany(r => r.Body)];
        _actor = actor;
        if (follow && samePeople && _people.Count > 0 && Active?.Id == rig.Id && _shared.Count == rig.Lights.Length)
        {
            _targets = people;   // 平滑过渡：OnUpdate 每帧靠过去
            return Error = null;
        }
        _people = people;
        _targets = null;
        Framing = await _framework.RunOnFrameworkThread(() => ResolveFraming(people));
        if (Active?.Id != rig.Id || _shared.Count != rig.Lights.Length)
        {
            SelectedLight = Solo = null;
            Muted.Clear();
        }
        if (Active?.Id != rig.Id)
        {
            _overrides.Clear();
            SelectedGroup = null;
        }
        Active = rig;
        _shared = [.. rig.Lights];
        await _framework.RunOnFrameworkThread(Rebuild);
        return Error = null;
    }

    /// <summary>角色动过后重新读脸，再摆一次。</summary>
    public async Task<string?> ReplaceAsync(int? actor, bool follow = false)
        => Active is { } rig ? await ApplyAsync(rig with { Lights = [.. _shared] }, actor, follow) : null;

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
        _shared = [];
        _overrides.Clear();
        SelectedGroup = null;
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
        => Active is { } rig ? new SavedLighting(rig with { Lights = [.. _shared] }, Tweaks, DateTime.Now) : null;

    /// <summary>设置改了打光范围：马上按新的范围重摆。</summary>
    public void RefreshFraming() => _framework.RunOnFrameworkThread(() =>
    {
        if (_people.Count > 0) Framing = ResolveFraming(_people);
        Rebuild();
    });

    /// <summary>
    /// 打光范围：设置里选了就用选的；「自动」时看脚、腰有没有在游戏画面里——谁的脚拍到了是全身，腰拍到了是半身，否则照脸。
    /// 必须在主线程调用。
    /// </summary>
    private LightFraming ResolveFraming(IReadOnlyList<Subject> people)
    {
        switch (_config.LightFraming)
        {
            case "face": return LightFraming.Face;
            case "half": return LightFraming.Half;
            case "full": return LightFraming.Full;
        }
        if (people.Any(s => s.Feet is { } feet && OnScreen(feet))) return LightFraming.Full;
        if (people.Any(s => s.Pelvis is { } pelvis && OnScreen(pelvis))) return LightFraming.Half;
        return LightFraming.Face;
    }

    /// <summary>按「照谁」把读到的人分组，每组合成一个打光对象（第一组含主窗口选的角色）。</summary>
    private List<Subject> ComputeGroups()
    {
        var people = _people;
        if (people.Count == 0) return [];
        var all = Enumerable.Range(0, people.Count).ToList();
        List<List<int>> sets = _config.LightPeople switch
        {
            "group" => [all],
            "each" => [.. all.Select(i => new List<int> { i })],
            "auto" => LightRigs.Cluster(people, AutoGroupMeters),
            _ => [[0]],
        };
        var indices = _indices;
        GroupMembers = [.. sets.Select(set => set.Where(i => i < indices.Count).Select(i => indices[i]).Order().ToArray())];
        GroupKeys = [.. GroupMembers.Select(m => string.Join(",", m))];
        if (SelectedGroup is { } selected && selected >= GroupKeys.Count) SelectedGroup = null;
        return [.. sets.Select(set => LightRigs.Group([.. set.Select(i => people[i])], Framing))];
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
        var groups = ComputeGroups();
        Groups = groups;
        if (groups.Count == 0 || !_clientState.IsGPosing) return;
        var camera = GposeCamera.WorldPosition();
        _lastCamera = camera;
        // 灯太多时离镜头远的组先不打（主角那一组总是打）；阴影只给离镜头最近的几组
        var keep = Math.Max(1, MaxLights / Math.Max(1, _shared.Count));
        var byDistance = Enumerable.Range(0, groups.Count)
                                   .OrderBy(g => g == 0 ? -1f : camera is { } c ? Vector3.Distance(c, groups[g].Center) : g).ToList();
        DroppedGroups = Math.Max(0, groups.Count - keep);
        var states = new List<LightState>();
        foreach (var g in byDistance.Take(keep).Order())
        {
            var shadows = byDistance.IndexOf(g) < MaxShadowGroups;
            var solved = LightRigs.Solve(LightsFor(g), groups[g], camera, Tweaks, Time, tight: groups.Count > 1);
            for (var i = 0; i < solved.Count; i++)
            {
                if (!IsLit(i)) solved[i] = solved[i] with { Color = Vector3.Zero };
                if (!shadows) solved[i] = solved[i] with { Shadows = false };
            }
            states.AddRange(solved);
        }
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
        if (_config.LightFollowSubject && _tick % MovePollTicks == 0 && !_following && _indices.Count > 0)
            _ = CheckMovedAsync();
        if (_targets is { } targets && targets.Count == _people.Count)
        {
            var k = 1f - MathF.Exp(-(float)framework.UpdateDelta.TotalSeconds / FollowSeconds);
            var next = _people.Zip(targets, (current, target) => current.Toward(target, k)).ToList();
            var done = next.Zip(targets).All(p => Vector3.Distance(p.First.Face, p.Second.Face) < 0.002f
                                                  && Vector3.Dot(p.First.Forward, p.Second.Forward) > 0.9999f);
            _people = done ? targets : next;
            if (done) _targets = null;
            Rebuild();
            return;
        }
        if (_config.LightFraming == "auto" && _tick % FramingPollTicks == 0 && _people.Count > 0)
        {
            var framing = ResolveFraming(_people);
            if (framing != Framing)
            {
                Framing = framing;
                Rebuild();
            }
        }
        if (_shared.Concat(_overrides.Values.SelectMany(l => l)).Any(l => l.Effect != LightEffect.None))
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
        => actor == _actor || (await _applier.IdentifyAsync(actor))?.Index is { } a && _indices.Contains(a);

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

    /// <summary>被照的人里有人被挪动或转身（Brio 移动、换站位）：位置或朝向变了就重新读脸再摆。</summary>
    private async Task CheckMovedAsync()
    {
        var indices = _indices;
        var models = _models;
        for (var i = 0; i < indices.Count && i < models.Count; i++)
        {
            var model = await _applier.GetTransformAsync(indices[i]);
            if (model is null || _following || !ReferenceEquals(indices, _indices)) return;
            var position = new Vector3(model.Position[0], model.Position[1], model.Position[2]);
            var rotation = new Quaternion(model.Rotation[0], model.Rotation[1], model.Rotation[2], model.Rotation[3]);
            var turned = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(rotation, models[i].Rotation)), 0f, 1f)) * 180f / MathF.PI;
            if (Vector3.Distance(position, models[i].Position) < MoveThreshold && turned < TurnThresholdDeg) continue;
            await FollowAsync();
            return;
        }
    }

    /// <summary>读到的一个人：打光对象、对象序号、模型位置朝向（盯挪动用）、身体骨架（3D 视图里画人）。</summary>
    private sealed record Person(Subject Subject, int Index, Vector3 Position, Quaternion Rotation, List<(Vector3 A, Vector3 B)> Body);

    /// <summary>读要照的人：主窗口选的角色在前；不是「单人」时再加上勾选的其他角色（同一个角色只算一次）。主角读不到时返回空。</summary>
    private async Task<List<Person>> ReadPeopleAsync(int? primary)
    {
        var actors = new List<int?> { primary };
        if (_config.LightPeople != "single" && Others?.Invoke() is { } others)
            actors.AddRange(others.Where(i => i != primary).Select(i => (int?)i));
        var result = new List<Person>();
        foreach (var actor in actors)
        {
            var person = await ReadPersonAsync(actor);
            if (person is null)
            {
                if (actor == primary && result.Count == 0) return [];
                continue;
            }
            if (result.All(p => p.Index != person.Index)) result.Add(person);
        }
        return result;
    }

    /// <summary>
    /// 读角色的脸：Brio 姿势里两只眼睛的中点当脸的位置，头顶方向 = 脖子 → 头，
    /// 脸朝向 = 头顶 × 角色右手方向（再用鼻子校正正负）；体型比例 = 眼睛离脚底的高度 / 人族中原的眼高。
    /// </summary>
    private async Task<Person?> ReadPersonAsync(int? actor)
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
        List<(Vector3 A, Vector3 B)> body = [.. BodyBones.Where(b => bones.ContainsKey(b.Child) && bones.ContainsKey(b.Parent))
                                                         .Select(b => (World(bones[b.Parent]), World(bones[b.Child])))];
        // 脚：脚踝、脚尖里最低的那个（站着、坐着、躺着都是离地最近的一点）
        var feet = FeetBones.Where(bones.ContainsKey).Select(b => World(bones[b])).OrderBy(p => p.Y).Cast<Vector3?>().FirstOrDefault();
        var subject = new Subject(face, forward, up, Math.Clamp(eyeHeight / 1.58f, 0.4f, 1.5f))
        {
            Pelvis = bones.TryGetValue("j_kosi", out var pelvis) ? World(pelvis) : null,
            Feet = feet,
        };
        return new Person(subject, id.Index, position, rotation, body);
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
