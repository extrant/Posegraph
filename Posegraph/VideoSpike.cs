using System.Diagnostics;
using System.Numerics;
using System.Runtime.Loader;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace Posegraph;

/// <summary>
/// 视频功能第 0 步的临时原型（验证完删掉）。经 BridgeServer 的 /spike/* 调用：
///   POST /spike/poses   逐帧套姿势：{actor?, fps, loops, everyTick, poses:[Brio pose JSON 对象]}，统计每次 IPC 套姿势的耗时
///   POST /spike/camera  逐帧写 GPose 镜头：{seconds, yawSpeed（度/秒）, jump（距离突变）}，比较写入值和实际镜头位置的滞后
///   POST /spike/brio    反射写进 Brio 时间轴：{actor?, fps, poses:[...]}，每帧套姿势后用 Brio 自己的「Capture Pose」记关键帧
/// </summary>
public sealed class VideoSpike(IFramework framework, IClientState clientState, ITargetManager targetManager, BrioBridge brio)
{
    private IGameObject? Actor(int? index) => index is { } i ? brio.GetActors().FirstOrDefault(a => a.ObjectIndex == i) : targetManager.GPoseTarget;

    /// <summary>录制测试用：Plugin 接到视频窗口的 SpikeRecordAsync。</summary>
    public static Func<JsonElement, Task<object>>? Record { get; set; }

    public async Task<object> PosesAsync(JsonElement body)
    {
        int? actorIndex = body.TryGetProperty("actor", out var a) ? a.GetInt32() : null;
        var fps = body.TryGetProperty("fps", out var f) ? f.GetDouble() : 10;
        var loops = body.TryGetProperty("loops", out var l) ? l.GetInt32() : 2;
        var everyTick = body.TryGetProperty("everyTick", out var e) && e.GetBoolean();
        var clearStacks = body.TryGetProperty("clear", out var cl) && cl.GetBoolean();
        var direct = new BrioDirectPose();
        var useDirect = body.TryGetProperty("direct", out var dr) && dr.GetBoolean();
        var cleared = 0;
        var guard = body.TryGetProperty("guard", out var gd) && gd.GetBoolean();
        var skipped = 0;
        var trace = body.TryGetProperty("trace", out var tr) && tr.GetBoolean();
        var hand = new List<Vector3>();
        var poses = body.GetProperty("poses").EnumerateArray().Select(p => p.GetRawText()).ToArray();
        if (poses.Length == 0) return new { error = "no poses" };
        var poseBones = new HashSet<string>(JsonDocument.Parse(poses[0]).RootElement.GetProperty("Bones").EnumerateObject().Select(o => o.Name));

        var ready = await framework.RunOnFrameworkThread(() =>
        {
            if (!clientState.IsGPosing || Actor(actorIndex) is not { } actor) return false;
            if (body.TryGetProperty("reset", out var rs) && rs.GetBoolean()) brio.ResetPoseKeepTransform(actor);
            brio.Freeze(actor);
            return true;
        });
        if (!ready) return new { error = "not in gpose / no actor" };
        await framework.DelayTicks(5);

        var applyMs = new List<double>();
        var tickMs = new List<double>();
        var failures = 0;
        var clock = Stopwatch.StartNew();
        var lastTick = 0.0;
        var lastIndex = -1;
        var total = poses.Length * loops;
        var done = new TaskCompletionSource();
        void Tick(IFramework _)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            if (trace && Actor(actorIndex) is { } traced && brio.GetPose(traced) is { } json)
            {
                var v = JsonDocument.Parse(json).RootElement.GetProperty("Bones").GetProperty("j_te_l").GetProperty("Position").GetString()!
                    .Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                hand.Add(new Vector3(v[0], v[1], v[2]));
            }
            tickMs.Add(now - lastTick);
            lastTick = now;
            var step = (int)(clock.Elapsed.TotalSeconds * fps);
            if (step >= total)
            {
                framework.Update -= Tick;
                done.TrySetResult();
                return;
            }
            if (!everyTick && step == lastIndex) return;
            lastIndex = step;
            if (Actor(actorIndex) is not { } actor) return;
            var t = Stopwatch.StartNew();
            if (useDirect)
            {
                var r = direct.Apply(actor, poses[step % poses.Length], poseBones);
                if (r is false) { skipped++; lastIndex = -1; return; }
                if (r is null) failures++; else cleared++;
            }
            else if (!brio.ApplyPose(actor, poses[step % poses.Length])) failures++;
            applyMs.Add(t.Elapsed.TotalMilliseconds);
        }
        framework.Update += Tick;
        await done.Task;
        return new
        {
            calls = applyMs.Count,
            failures,
            cleared,
            skipped,
            problem = direct.Problem,
            handJumps = hand.Count < 2 ? null : (object)new
            {
                ticks = hand.Count,
                maxCm = hand.Zip(hand.Skip(1), (a, b) => (b - a).Length() * 100).Max(),
                p95Cm = Percentile(hand.Zip(hand.Skip(1), (a, b) => (double)(b - a).Length() * 100).ToList(), 0.95),
                over5Cm = hand.Zip(hand.Skip(1), (a, b) => (b - a).Length() * 100).Count(d => d > 5),
            },
            applyAvgMs = applyMs.Average(),
            applyMaxMs = applyMs.Max(),
            applyP95Ms = Percentile(applyMs, 0.95),
            tickAvgMs = tickMs.Skip(1).Average(),
            tickMaxMs = tickMs.Skip(1).Max(),
        };
    }

    public async Task<object> CameraAsync(JsonElement body)
    {
        var seconds = body.TryGetProperty("seconds", out var s) ? s.GetDouble() : 4;
        var yawSpeed = (body.TryGetProperty("yawSpeed", out var y) ? y.GetDouble() : 45) * Math.PI / 180;
        var jump = body.TryGetProperty("jump", out var j) ? j.GetSingle() : 0f;
        var snap = body.TryGetProperty("snap", out var sn) && sn.GetBoolean();

        var start = await framework.RunOnFrameworkThread(() => GposeCamera.Read() is null || targetManager.GPoseTarget is null
            ? ((float, float, Vector3)?)null
            : (GposeCamera.ReadAngle().X, GposeCamera.ReadDistance(), targetManager.GPoseTarget.Position));
        if (start is not { } st) return new { error = "not in gpose / no target" };
        var (yaw0, dist0, pivot) = st;

        var samples = new List<object>();
        var lagDeg = new List<double>();
        var clock = Stopwatch.StartNew();
        var done = new TaskCompletionSource();
        var jumped = false;
        var jumpTick = -1;
        var reachedTick = -1;
        var tick = 0;
        void Tick(IFramework _)
        {
            tick++;
            var t = clock.Elapsed.TotalSeconds;
            if (t > seconds)
            {
                framework.Update -= Tick;
                done.TrySetResult();
                return;
            }
            // 先读这一帧实际画出来的位置（上一帧写入的结果），再写下一帧
            var pose = GposeCamera.Pose();
            var field = GposeCamera.ReadAngle();
            var dist = GposeCamera.ReadDistance();
            if (pose is { } p)
            {
                var d = p.Position - pivot;
                var actualRadius = new Vector2(d.X, d.Z).Length();
                samples.Add(new { tick, t = Math.Round(t, 3), fieldYaw = field.X, fieldDist = dist, camX = p.Position.X, camZ = p.Position.Z, radius = actualRadius });
                if (jump != 0 && jumped && reachedTick < 0 && Math.Abs(Vector3.Distance(p.Position, pivot) - (dist0 + jump)) < 0.3f) reachedTick = tick;
            }
            var commanded = yaw0 + (float)(yawSpeed * t);
            var request = new CameraRequest { Angle = [commanded, GposeCamera.ReadAngle().Y] };
            if (jump != 0 && !jumped && t > seconds / 2)
            {
                request.Distance = dist0 + jump;
                jumped = true;
                jumpTick = tick;
            }
            GposeCamera.Write(request);
            if (snap && request.Distance is { } nd) GposeCamera.SnapDistance(nd);
            lagDeg.Add((commanded - field.X) * 180 / Math.PI);
        }
        await framework.RunOnFrameworkThread(() => framework.Update += Tick);
        await done.Task;
        await framework.RunOnFrameworkThread(() => GposeCamera.Write(new CameraRequest { Angle = [yaw0, GposeCamera.ReadAngle().Y], Distance = dist0 }));
        return new
        {
            ticks = tick,
            fieldLagDegAvg = lagDeg.Skip(2).DefaultIfEmpty().Average(),
            jumpTick,
            reachedTick,
            samples = samples.Where((_, i) => i % 6 == 0).ToArray(),
        };
    }

    /// <summary>GposeCamera.Place 精度：以当前镜头为基准挪位置、转视线、改视角，各摆一次，过 2 帧读回误差。</summary>
    public async Task<object> PlaceTestAsync(JsonElement body)
    {
        var start = await framework.RunOnFrameworkThread(() => GposeCamera.Pose());
        if (start is not { } s0) return new { error = "not in gpose" };
        var cases = new (Vector3 Offset, float Yaw, float Pitch, float Roll, float FovDeg)[]
        {
            (Vector3.Zero, 0, 0, 0, 0), (new(0.5f, 0, 0), 0, 0, 0, 0), (new(0, 0.4f, 0), 0, 0, 0, 0), (new(0, 0, 0.8f), 0, 0, 0, 0),
            (Vector3.Zero, 15, 0, 0, 0), (Vector3.Zero, 0, 10, 0, 0), (Vector3.Zero, 0, -15, 0, 0), (Vector3.Zero, 0, 0, 8, 0),
            (Vector3.Zero, 0, 0, 0, -10), (new(1.0f, -0.3f, 0.5f), -20, 12, 4, 6), (new(-1.5f, 0.6f, -0.6f), 35, -20, -6, -8),
        };
        var results = new List<object>();
        foreach (var c in cases)
        {
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, c.Yaw * MathF.PI / 180);
            var forward = Vector3.Transform(s0.Forward, yaw);
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            forward = Vector3.Transform(forward, Quaternion.CreateFromAxisAngle(right, c.Pitch * MathF.PI / 180));
            var up = Vector3.Normalize(Vector3.Cross(right, forward));
            up = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(forward, c.Roll * MathF.PI / 180));
            var position = s0.Position + c.Offset;
            var fov = s0.FovY + c.FovDeg * MathF.PI / 180;
            var steps = new List<float>();
            for (var k = 0; k < 12; k++)
            {
                steps.Add(await framework.RunOnFrameworkThread(() =>
                {
                    var err = GposeCamera.SteerView(forward, up);
                    GposeCamera.PlacePosition(position, fov);
                    return err;
                }));
                await framework.DelayTicks(1);
            }
            await framework.DelayTicks(1);
            var got = await framework.RunOnFrameworkThread(() => GposeCamera.Pose());
            if (got is not { } g) continue;
            var deg = (Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) * 180 / MathF.PI;
            var want = GposeCamera.ViewAngles(forward, up);
            var have = GposeCamera.ViewAngles(g.Forward, g.Up);
            results.Add(new
            {
                c.Offset, c.Yaw, c.Pitch, c.Roll, c.FovDeg,
                posErrCm = (g.Position - position).Length() * 100,
                fwdErrDeg = deg(g.Forward, forward),
                upErrDeg = deg(g.Up, up),
                pitchErrDeg = (have.Pitch - want.Pitch) * 180 / MathF.PI,
                fovErrDeg = (g.FovY - fov) * 180 / MathF.PI,
                steps,
            });
        }
        for (var k = 0; k < 12; k++)
        {
            await framework.RunOnFrameworkThread(() => { GposeCamera.SteerView(s0.Forward, s0.Up); GposeCamera.PlacePosition(s0.Position, s0.FovY); });
            await framework.DelayTicks(1);
        }
        return new { results };
    }

    /// <summary>改 zoom 后连续若干帧导出相机结构，找被游戏平滑的那个字段。</summary>
    public async Task<object> DumpAsync(JsonElement body)
    {
        var delta = body.TryGetProperty("zoom", out var z) ? z.GetSingle() : 0.3f;
        var snap = body.TryGetProperty("snap", out var sn) && sn.GetBoolean();
        var pan = body.TryGetProperty("pan", out var pn) ? pn.GetSingle() : 0f;
        Vector2 pan0 = default;
        var ticks = body.TryGetProperty("ticks", out var t) ? t.GetInt32() : 40;
        var frames = new List<float[]>();
        var done = new TaskCompletionSource();
        var n = 0;
        float zoom0 = 0;
        void Tick(IFramework _)
        {
            if (n == 2)
            {
                zoom0 = GposeCamera.Projection()?.Zoom ?? 0;
                if (snap) GposeCamera.SnapZoom(zoom0 + delta);
                else GposeCamera.SetZoom(zoom0 + delta);
                if (pan != 0)
                {
                    pan0 = GposeCamera.ReadPan();
                    GposeCamera.Write(new CameraRequest { Pan = [pan0.X + pan, pan0.Y] });
                }
            }
            var dump = GposeCamera.DumpFloats(0x400);
            var proj = GposeCamera.Projection();
            var pose = GposeCamera.Pose();
            dump[0] = proj?.FovY ?? 0;   // 借前几个位置放观测值（相机结构开头是虚表指针，没用）
            dump[1] = pose is { } pp ? MathF.Atan2(pp.Forward.X, pp.Forward.Z) : 0;
            frames.Add(dump);
            if (++n >= ticks)
            {
                framework.Update -= Tick;
                done.TrySetResult();
            }
        }
        await framework.RunOnFrameworkThread(() => framework.Update += Tick);
        await done.Task;
        await framework.RunOnFrameworkThread(() =>
        {
            GposeCamera.SetZoom(zoom0);
            if (pan != 0) GposeCamera.Write(new CameraRequest { Pan = [pan0.X, pan0.Y] });
        });
        return new { frames };
    }

    public async Task<object> BrioTimelineAsync(JsonElement body)
    {
        int? actorIndex = body.TryGetProperty("actor", out var a) ? a.GetInt32() : null;
        var fps = body.TryGetProperty("fps", out var f) ? f.GetDouble() : 10;
        var poses = body.TryGetProperty("poses", out var ps) ? ps.EnumerateArray().Select(p => p.GetRawText()).ToArray() : [];

        var asm = AssemblyLoadContext.All.SelectMany(c => c.Assemblies)
            .FirstOrDefault(x => x.GetName().Name == "Brio" && x.GetType("Brio.Brio") is not null);
        if (asm is null) return new { error = "Brio assembly not found" };
        var brioType = asm.GetType("Brio.Brio")!;
        var timelineType = asm.GetType("Brio.Services.Timeline.TimelineService");
        if (timelineType is null) return new { error = "TimelineService type not found" };
        var tryGet = brioType.GetMethod("TryGetService")!.MakeGenericMethod(timelineType);
        object?[] args = [null];
        if (!(bool)tryGet.Invoke(null, args)! || args[0] is not { } timeline) return new { error = "TryGetService failed" };

        if (body.TryGetProperty("camera", out var wantCamera) && wantCamera.GetBoolean())
        {
            var managerType = asm.GetType("Brio.Game.Camera.VirtualCameraManager")!;
            object?[] margs = [null];
            brioType.GetMethod("TryGetService")!.MakeGenericMethod(managerType).Invoke(null, margs);
            var cameraType = asm.GetType("Brio.Entities.Camera.CameraType")!;
            var created = await framework.RunOnFrameworkThread(() =>
                managerType.GetMethod("CreateCamera")!.Invoke(margs[0], [Enum.Parse(cameraType, "Free"), false, false, null]));
            await framework.DelayTicks(3);
            var cameraHosts = await framework.RunOnFrameworkThread(() =>
                ((System.Collections.IEnumerable)timelineType.GetProperty("AvailableHosts")!.GetValue(timeline)!).Cast<object>()
                    .Where(h => h.GetType().Name == "CameraTimelineCapability")
                    .Select(h => new
                    {
                        name = (string)h.GetType().GetProperty("Name")!.GetValue(h)!,
                        channels = ((System.Collections.IEnumerable)h.GetType().GetProperty("CaptureChannels")!.GetValue(h)!).Cast<object>()
                            .Select(c => (string)c.GetType().GetProperty("Name")!.GetValue(c)!).ToArray(),
                    }).ToArray());
            return new { created = created?.ToString(), cameraHosts };
        }

        var hosts = await framework.RunOnFrameworkThread(() =>
        {
            var available = ((System.Collections.IEnumerable)timelineType.GetProperty("AvailableHosts")!.GetValue(timeline)!).Cast<object>();
            var active = ((System.Collections.IEnumerable)timelineType.GetProperty("ActiveHosts")!.GetValue(timeline)!).Cast<object>();
            return available.Concat(active).Distinct().ToList();
        });
        string HostName(object h) => (string)h.GetType().GetProperty("Name")!.GetValue(h)!;
        var hostInfo = hosts.Select(h => new
        {
            name = HostName(h),
            type = h.GetType().Name,
            channels = ((System.Collections.IEnumerable)h.GetType().GetProperty("CaptureChannels")!.GetValue(h)!).Cast<object>()
                .Select(c => (string)c.GetType().GetProperty("Name")!.GetValue(c)!).ToArray(),
        }).ToArray();
        if (poses.Length == 0) return new { hosts = hostInfo };

        var actorName = await framework.RunOnFrameworkThread(() =>
        {
            if (Actor(actorIndex) is not { } actor) return null;
            brio.Freeze(actor);
            return actor.Name.TextValue;
        });
        var host = hosts.FirstOrDefault(h => h.GetType().Name == "ActorTimelineCapability" && (actorName is null || HostName(h).Contains(actorName)))
                   ?? hosts.FirstOrDefault(h => h.GetType().Name == "ActorTimelineCapability");
        if (host is null) return new { error = "no actor timeline host", hosts = hostInfo };
        // 通道名会被 Brio 本地化（中文是「捕获姿势」），按位置取：位置、旋转、缩放、全部、自动、捕获姿势、模型变换
        var capturePose = ((System.Collections.IEnumerable)host.GetType().GetProperty("CaptureChannels")!.GetValue(host)!).Cast<object>().ElementAt(5);
        var capture = (Action<int>)capturePose.GetType().GetProperty("Capture")!.GetValue(capturePose)!;
        await framework.RunOnFrameworkThread(() => timelineType.GetMethod("AddToTimeline")!.Invoke(timeline, [host]));
        await framework.DelayTicks(5);

        var playbackFps = 60.0;
        var clock = Stopwatch.StartNew();
        for (var k = 0; k < poses.Length; k++)
        {
            var frame = (int)Math.Round(k / fps * playbackFps);
            await framework.RunOnFrameworkThread(() => { if (Actor(actorIndex) is { } actor) brio.ApplyPose(actor, poses[k]); });
            await framework.DelayTicks(2);   // 等 Brio 把姿势写进骨骼（LastRawTransform 下一帧才更新）
            await framework.RunOnFrameworkThread(() => capture(frame));
        }
        var tracks = await framework.RunOnFrameworkThread(() =>
        {
            timelineType.GetProperty("FrameMax")!.SetValue(timeline, (int)Math.Round((poses.Length - 1) / fps * playbackFps));
            return ((System.Collections.ICollection)host.GetType().GetProperty("Tracks")!.GetValue(host)!).Count;
        });
        return new { host = HostName(host), tracks, keyframes = poses.Length, seconds = clock.Elapsed.TotalSeconds, hosts = hostInfo };
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[(int)Math.Clamp(Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }
}
