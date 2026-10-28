using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace Posegraph.Lighting;

/// <summary>
/// 一键布光：选一套方案，按当前角色的脸和 GPose 相机算出每盏灯摆哪，生成游戏灯。
/// 「跟随镜头」开着时相机动了灯跟着转（人物的脸位置在套用时读一次，角色动了再点一次「重新摆」）。
/// 退出 GPose、插件卸载时清掉所有灯。
/// </summary>
public sealed class LightingService : IDisposable
{
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly PoseApplier _applier;
    private readonly Configuration _config;
    private readonly GameLights _lights = new();
    private readonly List<(nint Handle, LightKind Kind)> _spawned = [];
    private Subject? _subject;
    private List<(Vector3 A, Vector3 B)> _body = [];
    private Vector3? _lastCamera;
    private int _tick;

    public LightingService(IFramework framework, IClientState clientState, PoseApplier applier, Configuration config)
    {
        _framework = framework;
        _clientState = clientState;
        _applier = applier;
        _config = config;
        _framework.Update += OnUpdate;
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

    /// <summary>读到的脸（3D 视图的中心、拖灯反算用）。</summary>
    public Subject? Subject => _subject;

    /// <summary>读脸时顺带记下的身体骨架（世界坐标线段），3D 视图里画人。</summary>
    public IReadOnlyList<(Vector3 A, Vector3 B)> Body => _body;

    /// <summary>GPose 相机（每帧更新）。摆灯时用它的位置；3D 视图里画镜头。</summary>
    public CameraPose? CameraPose { get; private set; }

    /// <summary>灯摆的时候用的镜头位置：跟随镜头时是当前镜头，否则是套用 / 重新摆时的镜头。</summary>
    public Vector3? Camera => _config.LightFollowCamera ? CameraPose?.Position ?? _lastCamera : _lastCamera;

    /// <summary>这盏灯现在是否发光（独显 / 关掉之后）。</summary>
    public bool IsLit(int i) => Solo is { } s ? s == i : !Muted.Contains(i);

    /// <summary>所有方案：内置 + 自己存的。</summary>
    public IEnumerable<LightRig> Rigs => LightRigs.Builtin.Concat(CustomRigs);

    public IEnumerable<LightRig> CustomRigs => _config.LightRigs.Select(json =>
    {
        try { return JsonSerializer.Deserialize<LightRig>(json); }
        catch (JsonException) { return null; }
    }).OfType<LightRig>();

    /// <summary>读角色的脸，套用方案。actor 同主窗口（null 为 GPose 目标）。返回失败原因，成功为 null。</summary>
    public async Task<string?> ApplyAsync(LightRig rig, int? actor)
    {
        var subject = await ReadSubjectAsync(actor);
        if (subject is null) return Error = Loc.T("读不到角色的脸（不在 GPose、没选角色或 Brio 不可用）");
        _subject = subject;
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
    public async Task<string?> ReplaceAsync(int? actor)
        => Active is { } rig ? await ApplyAsync(rig with { Lights = [.. Lights] }, actor) : null;

    /// <summary>界面改了某盏灯或全局微调：按已读到的脸重摆（不重新读角色）。</summary>
    public void Refresh() => _framework.RunOnFrameworkThread(Rebuild);

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

    private void Rebuild()
    {
        if (_subject is not { } subject || !_clientState.IsGPosing) return;
        var camera = GposeCamera.WorldPosition();
        _lastCamera = camera;
        var states = LightRigs.Solve(Lights, subject, camera, Tweaks);
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
        if (!_config.LightFollowCamera || ++_tick % 6 != 0) return;
        if (GposeCamera.WorldPosition() is { } cam && (_lastCamera is not { } last || Vector3.Distance(cam, last) > 0.02f))
            Rebuild();
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
        return new Subject(face, forward, up, Math.Clamp(eyeHeight / 1.58f, 0.4f, 1.5f));
    }

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
        // 卸载插件可能不在主线程：同步回主线程清灯
        _framework.RunOnFrameworkThread(() =>
        {
            _lights.DestroyAll();
            _spawned.Clear();
        }).Wait(TimeSpan.FromSeconds(2));
    }
}
