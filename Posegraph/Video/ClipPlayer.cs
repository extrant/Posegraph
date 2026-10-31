using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace Posegraph.Video;

/// <summary>镜头模式：跟拍（角色原地、镜头按每帧构图移动）/ 走动（角色按视频在场景里走，镜头见 WalkCamera）。</summary>
public enum ClipCameraMode
{
    Follow,
    Walk,
}

/// <summary>走动模式的机位：原运镜（原视频的镜头怎么动就怎么动）/ 固定机位 / 平滑跟拍 / 定点摇镜。</summary>
public enum WalkCamera
{
    Original,
    Fixed,
    Follow,
    Pan,
}

/// <summary>原运镜时原视频拍不到人的那几段：照原视频 / 换成平滑跟拍 / 换成定点摇镜（前后渐变）。</summary>
public enum HiddenCamera
{
    Original,
    Follow,
    Pan,
}

/// <summary>
/// 片段播放：每个游戏帧按时间在相邻两帧之间插值，整份套姿势，镜头用 GposeCamera 当帧摆好。
/// 姿势经 BrioDirectPose 当帧写进 Brio（IPC 导入要隔 4 帧才生效，逐帧套会闪、会叠层、脸会漂，见那里的说明）；Brio 版本对不上时退回 IPC。
/// 基准 = 角色现在的模型变换（片段里的镜头、走动都是相对角色的）：跟拍模式镜头 = 基准变换 × 每帧镜头；
/// 走动模式：片段有走位和镜头轨道（新版后端）时，角色 = 基准变换 × 每帧走位，镜头 = 基准变换 × 选中的轨道（原运镜拍不到人的段落
/// 按 HiddenCamera 换成自动镜头）；旧片段镜头固定在第一帧的位置，角色的模型变换 = 固定镜头 × 每帧镜头的逆
/// （只取水平位移和转向，高度不变，免得跟着识别误差上下飘）。
/// 没被走动模式挪过时每次都重读基准（换了场景、在 Brio 里挪了角色都跟着走）；挪过之后基准不变，停止 / 换角色 / 换模式时放回去。
/// 角色不见了（退出 GPose、换场景）就清掉基准、停下。都在游戏主线程上执行（framework.Update）。
/// </summary>
public sealed class ClipPlayer : IDisposable
{
    private readonly IFramework _framework;
    private readonly BrioBridge _brio;
    private readonly Func<int, IGameObject?> _resolve;
    private readonly BrioDirectPose _direct = new();
    private HashSet<string> _bones = [];
    private (Vector3 Position, Quaternion Rotation, float Scale)? _base;
    private bool _moved;          // 走动模式挪过角色（停止时要放回去）
    private bool _frozen;         // 已经冻结过这个角色的动画
    private bool _dirty = true;
    private int _settle;          // 停下来后还要继续拧视线的帧数（视线是逐帧闭环修正的，跳转后要几帧才收敛）
    private (Vector3 Forward, Vector3 Up)? _goalView;
    private List<Extra> _extras = [];

    /// <summary>配角（片段里第 2、3… 个人）：游戏角色、骨骼名、放回去用的原位置、是否已冻结 / 挪过。</summary>
    private sealed class Extra(int person, int actor, HashSet<string> bones)
    {
        public int Person { get; } = person;
        public int Actor { get; } = actor;
        public HashSet<string> Bones { get; } = bones;
        public (Vector3 Position, Quaternion Rotation)? Original;
        public bool Frozen;
    }

    public ClipPlayer(IFramework framework, BrioBridge brio, Func<int, IGameObject?> resolve)
    {
        _framework = framework;
        _brio = brio;
        _resolve = resolve;
        _framework.Update += Tick;
    }

    public Clip? Clip { get; private set; }
    public int? Actor { get; private set; }

    /// <summary>片段里每个人对应的游戏角色（第一个是主角 = Actor；没配的为 null）。</summary>
    public IReadOnlyList<int?> Actors { get; private set; } = [];
    public double Time { get; private set; }
    public bool Playing { get; private set; }
    public bool Loop { get; set; } = true;
    public float Speed { get; set; } = 1f;
    public bool DriveCamera { get; set; } = true;
    public ClipCameraMode Mode { get; private set; } = ClipCameraMode.Follow;

    private WalkCamera _walkCamera = WalkCamera.Original;
    private HiddenCamera _hiddenCamera = HiddenCamera.Follow;

    private ClipView _view = ClipView.Default;
    private ClipFraming? _framing;
    private Clip? _framingClip;
    private Vector3? _autoPivot;   // 这一帧是自动镜头时的中心点（跟踪点，第一帧角色的模型系）

    /// <summary>镜头整体调整（片段里存的那份；改了马上生效）。</summary>
    public ClipView View
    {
        get => _view;
        set { _view = value; _dirty = true; }
    }

    public WalkCamera WalkCamera
    {
        get => _walkCamera;
        set { _walkCamera = value; _dirty = true; }
    }

    public HiddenCamera HiddenCamera
    {
        get => _hiddenCamera;
        set { _hiddenCamera = value; _dirty = true; }
    }
    public string? Error { get; private set; }

    /// <summary>最近一次套上的姿势是第几秒的（录制时等它等于要录的时刻）。</summary>
    public double? AppliedTime { get; private set; }

    /// <summary>镜头视线离目标还差多少度（逐帧闭环修正，跳转后几帧才收敛）；不控制镜头时为 0。</summary>
    public float ViewError { get; private set; }

    /// <summary>
    /// 播放时的时钟：返回片段第几秒（在放音乐时 = 声卡实际放到的位置），null 时按游戏帧时间往前走。
    /// 有音乐时画面跟着音乐走，音乐本身连续放、不跳（追画面会一卡一跳、重叠）。
    /// </summary>
    public Func<double?>? Clock { get; set; }

    /// <summary>时间被跳过去了（拖进度、停止、循环回到开头、换片段、开始播放）：音乐要重新定位。</summary>
    public event Action? Jumped;

    /// <summary>录制用：停下来，摆到第 t 秒（下一帧套姿势、摆镜头）。</summary>
    public void RenderAt(double t)
    {
        if (Clip is null) return;
        Playing = false;
        Time = Math.Clamp(t, 0, Clip.Duration);
        AppliedTime = null;
        _dirty = true;
        Jumped?.Invoke();
    }

    /// <summary>换片段或角色：先把上一个角色放回原位，下次套姿势时重新记基准。</summary>
    public void Load(Clip? clip, int? actor) => Load(clip, [actor]);

    /// <summary>多人片段：actors[i] 是片段里第 i 个人对应的游戏角色（第一个是主角，镜头、走位按他；配角按站位摆在他旁边）。</summary>
    public void Load(Clip? clip, IReadOnlyList<int?> actors)
    {
        _framework.RunOnFrameworkThread(() =>
        {
            RestoreBase();
            Clip = clip;
            Actor = actors.Count > 0 ? actors[0] : null;
            Actors = [.. actors];
            _extras = [];
            for (var p = 1; clip is not null && p < Math.Min(actors.Count, clip.People.Length); p++)
                if (actors[p] is { } a && a != Actor && _extras.All(e => e.Actor != a) && clip.People[p].Offset.Length > 0)
                    _extras.Add(new Extra(p, a, new HashSet<string>(clip.People[p].Bones, StringComparer.Ordinal)));
            _bones = clip is { People.Length: > 0 } c ? new HashSet<string>(c.People[0].Bones, StringComparer.Ordinal) : [];
            _direct.Invalidate();
            Time = 0;
            Playing = false;
            _base = null;
            _framing = null;
            _view = clip?.View ?? ClipView.Default;
            _frozen = false;
            _dirty = true;
        });
    }

    public void SetActor(int? actor)
    {
        if (actor == Actor) return;
        Load(Clip, [actor, .. Actors.Skip(1)]);
    }

    public void SetActors(IReadOnlyList<int?> actors)
    {
        if (actors.SequenceEqual(Actors)) return;
        Load(Clip, actors);
    }

    public void SetMode(ClipCameraMode mode)
    {
        if (mode == Mode) return;
        _framework.RunOnFrameworkThread(() =>
        {
            RestoreBase();
            Mode = mode;
            _dirty = true;
        });
    }

    public void Play()
    {
        if (Clip is null) return;
        if (Time >= Clip.Duration) Time = 0;
        Playing = true;
        Jumped?.Invoke();
    }

    public void Pause() => Playing = false;

    /// <summary>停止：回到开头，角色放回原位。</summary>
    public void Stop()
    {
        _framework.RunOnFrameworkThread(() =>
        {
            Playing = false;
            Time = 0;
            _dirty = true;
            RestoreBase();
            Jumped?.Invoke();
        });
    }

    public void Seek(double t)
    {
        if (Clip is null) return;
        Time = Math.Clamp(t, 0, Clip.Duration);
        _dirty = true;
        Jumped?.Invoke();
    }

    /// <summary>当前时刻的姿势 JSON（存进 Brio 姿势库用）。</summary>
    public string? CurrentPose() => Clip is { People.Length: > 0 } c ? c.People[0].PoseJson(c, Time) : null;

    private void Tick(IFramework framework)
    {
        if (Clip is not { } clip || Actor is not { } index) return;
        if (_base is not null && _resolve(index) is null)
        {
            // 角色不见了（退出 GPose、换场景）：旧坐标作废，回来后按角色那时的位置重新算
            _base = null;
            _moved = _frozen = false;
            Playing = false;
            _dirty = true;
            return;
        }
        if (Playing)
        {
            Time = Clock?.Invoke() ?? Time + framework.UpdateDelta.TotalSeconds * Speed;
            if (Time > clip.Duration)
            {
                if (Loop && clip.Duration > 0) Time %= clip.Duration;
                else
                {
                    Time = clip.Duration;
                    Playing = false;
                }
                Jumped?.Invoke();
            }
            _dirty = true;
        }
        if (_dirty) Apply();
        else if (_settle > 0 && DriveCamera && _goalView is { } view)
        {
            _settle--;
            ViewError = GposeCamera.SteerView(view.Forward, view.Up);
            if (ViewError < 0.05f) _settle = 0;
        }
    }

    private void Apply()
    {
        _dirty = false;
        if (Clip is not { People.Length: > 0 } clip || Actor is not { } index || _resolve(index) is not { } actor)
        {
            Error = Clip is null ? null : Loc.T("找不到角色");
            return;
        }
        if (_base is null || !_moved)
        {
            var (pos, rot, scale) = _brio.GetModelTransform(actor);
            _base = (pos ?? actor.Position, rot ?? Quaternion.CreateFromAxisAngle(Vector3.UnitY, actor.Rotation), scale?.X ?? 1f);
        }
        if (!_frozen)
        {
            _brio.Freeze(actor);
            _frozen = true;
        }
        var applied = ApplyPose(actor, clip.People[0].PoseJson(clip, Time), _bones);
        foreach (var extra in _extras)
        {
            if (_resolve(extra.Actor) is not { } other) continue;
            if (!extra.Frozen)
            {
                var (pos, rot, _) = _brio.GetModelTransform(other);
                extra.Original ??= (pos ?? other.Position, rot ?? Quaternion.CreateFromAxisAngle(Vector3.UnitY, other.Rotation));
                _brio.Freeze(other);
                extra.Frozen = true;
            }
            applied &= ApplyPose(other, clip.People[extra.Person].PoseJson(clip, Time), extra.Bones);
        }
        AppliedTime = applied ? Time : null;
        if (clip.CameraAt(Time) is not { } cam) return;
        var (p0, q0, s) = _base.Value;
        Vector3 position, forward, up;
        var (actorP, actorQ) = (p0, q0);
        _autoPivot = null;
        if (Mode == ClipCameraMode.Walk && clip.HasWalkTracks)
        {
            var root = clip.RootAt(Time);
            var p = p0 + Vector3.Transform(new Vector3(root.X, 0, root.Z) * s, q0);
            p.Y = p0.Y;
            var q = Quaternion.Normalize(q0 * Quaternion.CreateFromAxisAngle(Vector3.UnitY, root.W));
            _brio.SetModelTransform(actor, p, q);
            (actorP, actorQ) = (p, q);
            _moved = true;
            cam = WalkView(clip);
            position = p0 + Vector3.Transform(cam.Position * s, q0);
            forward = Vector3.Transform(cam.Forward, q0);
            up = Vector3.Transform(cam.Up, q0);
        }
        else if (Mode == ClipCameraMode.Walk)
        {
            var first = clip.Camera[0];
            position = p0 + Vector3.Transform(first.Position * s, q0);
            forward = Vector3.Transform(first.Forward, q0);
            up = Vector3.Transform(first.Up, q0);
            // 角色的变换 M 要让 M × 这一帧的镜头 = 固定镜头：转向取两帧镜头朝向之差的水平分量
            var turn = Basis(first) * Quaternion.Conjugate(Basis(cam));
            var heading = Vector3.Transform(Vector3.UnitZ, turn);
            var q = Quaternion.Normalize(q0 * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(heading.X, heading.Z)));
            var p = position - Vector3.Transform(cam.Position * s, q);
            p.Y = p0.Y;
            _brio.SetModelTransform(actor, p, q);
            (actorP, actorQ) = (p, q);
            _moved = true;
        }
        else
        {
            position = p0 + Vector3.Transform(cam.Position * s, q0);
            forward = Vector3.Transform(cam.Forward, q0);
            up = Vector3.Transform(cam.Up, q0);
        }
        PlaceExtras(clip, actorP, actorQ, s);
        var fov = cam.FovY;
        if (View.Moves)
        {
            // 中心点：自动镜头用跟踪点；其他用镜头正前方、角色胸口所在深度的那一点
            Vector3 pivot;
            if (_autoPivot is { } ap) pivot = p0 + Vector3.Transform(ap * s, q0);
            else
            {
                var chest = actorP + Vector3.Transform(clip.People[0].BoneAt(clip, Time, "j_sebo_c") * s, actorQ);
                var axis = Vector3.Normalize(forward);
                pivot = position + axis * MathF.Max(0.5f, Vector3.Dot(chest - position, axis));
            }
            View.Apply(ref position, ref forward, ref up, ref fov, pivot, Vector3.Transform(Vector3.UnitY, q0));
        }
        if (DriveCamera) PlaceCamera(clip, position, forward, up, fov);
        else ViewError = 0;
    }

    /// <summary>套一个角色的姿势：当帧写进 Brio（上一次还没执行就下一帧再套），用不了时退回 IPC。返回这一帧套上没有。</summary>
    private bool ApplyPose(IGameObject actor, string pose, HashSet<string> bones)
    {
        switch (_direct.Apply(actor, pose, bones))
        {
            case true:
                Error = null;
                return true;
            case false:
                _dirty = true;   // 上一次还没执行：姿势下一帧再套，镜头照常跟
                return false;
            default:
                Error = _brio.ApplyPose(actor, pose) ? null : Loc.T("Brio 应用姿势失败");
                return Error is null;
        }
    }

    /// <summary>配角摆到主角这一帧的位置 × 站位（同一块地面：高度跟主角一样）。</summary>
    private void PlaceExtras(Clip clip, Vector3 mainPosition, Quaternion mainRotation, float scale)
    {
        foreach (var extra in _extras)
        {
            if (_resolve(extra.Actor) is not { } other || clip.People[extra.Person].OffsetAt(clip, Time) is not { } o) continue;
            var p = mainPosition + Vector3.Transform(new Vector3(o.X, 0, o.Z) * scale, mainRotation);
            p.Y = mainPosition.Y;
            _brio.SetModelTransform(other, p, Quaternion.Normalize(mainRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, o.W)));
        }
    }

    /// <summary>摆镜头：位置、视角当帧到位；视线按上一帧实际画出来的朝向闭环修正（GposeCamera.SteerView）。
    /// 照片比游戏画面宽时左右对齐（同照片镜头复刻）。</summary>
    private void PlaceCamera(Clip clip, Vector3 position, Vector3 forward, Vector3 up, float fovYDeg)
    {
        var fov = fovYDeg * MathF.PI / 180f;
        if (GposeCamera.Projection() is { } proj && clip.ImageSize is [var w, var h, ..] && h > 0)
            fov = 2 * MathF.Atan(MathF.Tan(fov / 2) * MathF.Max(1f, (float)w / h / proj.Aspect));
        ViewError = GposeCamera.SteerView(forward, up);
        GposeCamera.PlacePosition(position, fov);
        _goalView = (forward, up);
        _settle = 30;
    }

    /// <summary>走动模式这一刻的镜头（第一帧角色的模型系）：按 WalkCamera 选轨道；原运镜拍不到人时按 HiddenCamera 渐变到自动镜头。</summary>
    private ClipCamera WalkView(Clip clip)
    {
        var original = clip.TrackAt("original", Time)!.Value;
        switch (WalkCamera)
        {
            case WalkCamera.Fixed:
                return clip.TrackAt("original", 0)!.Value;
            case WalkCamera.Follow:
                return AutoCamera(clip, "follow", true) ?? original;
            case WalkCamera.Pan:
                return AutoCamera(clip, "pan", true) ?? original;
        }
        if (HiddenCamera == HiddenCamera.Original) return original;
        var seen = clip.VisibleAt(Time);
        if (seen >= 1f) return original;
        var auto = AutoCamera(clip, HiddenCamera == HiddenCamera.Pan ? "pan" : "follow", false);
        return auto is { } a ? ClipCamera.Lerp(a, original, seen) : original;
    }

    /// <summary>
    /// 自动镜头（第一帧角色的模型系）：后端的轨道（按胸口算的）改成对准跟踪点——跟拍的机位跟着跟踪点平移，摇镜只改朝向；
    /// 全身入画时跟拍退到能装下整个人的距离，摇镜放宽视角。pivot：是否把跟踪点当这一帧调整镜头的中心点。
    /// </summary>
    private ClipCamera? AutoCamera(Clip clip, string track, bool pivot)
    {
        if (clip.TrackAt(track, Time) is not { } cam) return null;
        if (_framing is null || _framingClip != clip || _framing.Target != View.Target)
        {
            _framing = ClipFraming.Build(clip, View.Target);
            _framingClip = clip;
        }
        var f = _framing;
        var aim = f.AimAt(clip, Time);
        var position = cam.Position;
        var fov = cam.FovY;
        if (track == "follow") position += aim - f.ChestAt(clip, Time);
        if (View.FitBody)
        {
            var center = f.CenterAt(clip, Time);
            // 镜头对准跟踪点，不一定是全身中心：要装下的半径再加上两者的距离
            var radius = f.RadiusAt(clip, Time) * ClipFraming.FitMargin + Vector3.Distance(center, aim);
            var dist = MathF.Max(0.3f, Vector3.Distance(position, center));
            if (track == "pan")
                fov = MathF.Max(fov, 2 * MathF.Atan(MathF.Min(0.99f, radius / dist)) * 180f / MathF.PI);
            else
            {
                var need = radius / MathF.Tan(fov * MathF.PI / 360f);
                if (dist < need) position = center + Vector3.Normalize(position - center) * need;
            }
        }
        var forward = Vector3.Normalize(aim - position);
        var up = Vector3.Normalize(Vector3.UnitY - forward * forward.Y);
        if (pivot) _autoPivot = aim;
        return new ClipCamera(position, forward, up, fov);
    }

    /// <summary>镜头的朝向（局部 x 右、y 上、z 后 → 模型系）。</summary>
    private static Quaternion Basis(ClipCamera c)
    {
        var back = -Vector3.Normalize(c.Forward);
        var right = Vector3.Normalize(Vector3.Cross(c.Up, back));
        var up = Vector3.Cross(back, right);
        var m = new Matrix4x4(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, back.X, back.Y, back.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    private void RestoreBase()
    {
        foreach (var extra in _extras)
        {
            if (extra.Original is { } o && _resolve(extra.Actor) is { } other) _brio.SetModelTransform(other, o.Position, o.Rotation);
            extra.Original = null;
            extra.Frozen = false;
        }
        if (_moved && _base is { } b && Actor is { } index && _resolve(index) is { } actor)
            _brio.SetModelTransform(actor, b.Position, b.Rotation);
        _moved = false;
    }

    public void Dispose()
    {
        _framework.Update -= Tick;
        _framework.RunOnFrameworkThread(RestoreBase);
    }
}
