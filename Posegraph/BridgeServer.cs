using System.Net;
using System.Text;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace Posegraph;

/// <summary>
/// 只监听 127.0.0.1 的 HTTP 服务。接口：
///   GET  /status            插件/Brio/GPose 状态
///   GET  /actors            Brio 管理的 GPose 角色列表
///   GET  /pose?actor=N      取角色当前姿势（Brio pose JSON），N 为 ObjectIndex，省略则用 GPose 目标
///   POST /pose?actor=N      应用姿势，请求体为 Brio pose JSON；默认先冻结动画，freeze=0 可跳过
///   GET  /camera            读取 GPose 相机参数、视图/投影矩阵、帧号（判断相机停稳用），以及 GPose 目标的世界位置与朝向
///   POST /camera            写入相机参数 {distance, angle[2], pan[2], roll, zoom, delimit}
/// </summary>
public sealed class BridgeServer(IFramework framework, IClientState clientState, ITargetManager targetManager, BrioBridge brio, IPluginLog log) : IDisposable
{
    private const int FreezeSettleTicks = 5;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _frame;   // 游戏帧号：后端连读相机时据此判断两次读数是不是同一帧（帧率低时按时间间隔判断会误判「已停稳」）

    public bool IsRunning => _listener?.IsListening == true;
    public int? Port { get; private set; }

    public void Start(int port)
    {
        Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _listener = listener;
        _cts = new CancellationTokenSource();
        Port = port;
        framework.Update += CountFrame;
        _ = Task.Run(() => AcceptLoop(listener, _cts.Token));
        log.Info(Loc.F("Posegraph HTTP 已启动：http://127.0.0.1:{0}/", port));
    }

    public void Stop()
    {
        framework.Update -= CountFrame;
        _cts?.Cancel();
        try { _listener?.Close(); } catch { }
        _listener = null;
        Port = null;
    }

    public void Dispose() => Stop();

    private void CountFrame(IFramework _) => _frame++;

    private async Task AcceptLoop(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        try
        {
            var route = (req.HttpMethod, req.Url!.AbsolutePath.TrimEnd('/'));
            switch (route)
            {
                case ("GET", "/status"):
                    await WriteJson(ctx, 200, await framework.RunOnFrameworkThread(GetStatus));
                    break;
                case ("GET", "/actors"):
                    await WriteJson(ctx, 200, await framework.RunOnFrameworkThread(ListActors));
                    break;
                case ("GET", "/pose"):
                    await HandleGetPose(ctx);
                    break;
                case ("POST", "/pose"):
                    await HandleApplyPose(ctx);
                    break;
                case ("GET", "/camera"):
                    await WriteJson(ctx, 200, await framework.RunOnFrameworkThread(ReadCamera));
                    break;
                case ("POST", "/camera"):
                    await HandleWriteCamera(ctx);
                    break;
                default:
                    await WriteJson(ctx, 404, new { error = "not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, Loc.T("Posegraph 处理请求失败"));
            await WriteJson(ctx, 500, new { error = ex.Message });
        }
    }

    private object ReadCamera()
    {
        if (!clientState.IsGPosing) return new { error = Loc.T("不在 GPose 中") };
        var target = targetManager.GPoseTarget;
        return new
        {
            frame = _frame,
            camera = GposeCamera.Read(),
            target = target is null ? null : new
            {
                objectIndex = target.ObjectIndex,
                position = new[] { target.Position.X, target.Position.Y, target.Position.Z },
                rotation = target.Rotation,
            },
        };
    }

    private async Task HandleWriteCamera(HttpListenerContext ctx)
    {
        CameraRequest? body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = JsonSerializer.Deserialize<CameraRequest>(await reader.ReadToEndAsync(), JsonOpts);
        if (body is null)
        {
            await WriteJson(ctx, 400, new { error = Loc.T("请求体为空") });
            return;
        }
        var (code, payload) = await framework.RunOnFrameworkThread<(int, object)>(() =>
        {
            if (!clientState.IsGPosing) return (409, new { error = Loc.T("不在 GPose 中") });
            return GposeCamera.Write(body) ? (200, new { ok = true }) : (500, new { error = Loc.T("找不到活动相机") });
        });
        await WriteJson(ctx, code, payload);
    }

    private object GetStatus()
    {
        var version = brio.TryGetApiVersion();
        return new
        {
            plugin = "Posegraph",
            version = typeof(BridgeServer).Assembly.GetName().Version?.ToString(),
            brioAvailable = version is not null && brio.IsAvailable(),
            brioApiVersion = version is { } v ? $"{v.Breaking}.{v.Feature}" : null,
            inGPose = clientState.IsGPosing,
            gposeTarget = targetManager.GPoseTarget?.ObjectIndex,
        };
    }

    private object ListActors()
    {
        if (!clientState.IsGPosing) return Array.Empty<object>();
        return brio.GetActors()
            .Select(a => new { objectIndex = a.ObjectIndex, name = a.Name.TextValue })
            .ToArray();
    }

    /// <summary>按 ObjectIndex 找 Brio 角色；未指定时取 GPose 当前目标。必须在主线程调用。</summary>
    private IGameObject? ResolveActor(string? actorParam)
    {
        if (string.IsNullOrEmpty(actorParam))
            return targetManager.GPoseTarget;
        if (!ushort.TryParse(actorParam, out var index))
            return null;
        return brio.GetActors().FirstOrDefault(a => a.ObjectIndex == index);
    }

    private async Task HandleGetPose(HttpListenerContext ctx)
    {
        var actorParam = ctx.Request.QueryString["actor"];
        var (code, body) = await framework.RunOnFrameworkThread(() =>
        {
            if (!clientState.IsGPosing) return (409, Loc.T("{\"error\":\"不在 GPose 中\"}"));
            var actor = ResolveActor(actorParam);
            if (actor is null) return (404, Loc.T("{\"error\":\"找不到角色\"}"));
            var json = brio.GetPose(actor);
            return json is null ? (500, Loc.T("{\"error\":\"Brio 未返回姿势\"}")) : (200, json);
        });
        await WriteRaw(ctx, code, body);
    }

    private async Task HandleApplyPose(HttpListenerContext ctx)
    {
        string poseJson;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            poseJson = await reader.ReadToEndAsync();

        var actorParam = ctx.Request.QueryString["actor"];
        var freeze = ctx.Request.QueryString["freeze"] != "0";

        // 先冻结并等待几帧再导入：Brio 按导入当帧的动画姿势计算增量，
        // 若在动画播放中同帧冻结+导入，最终停住的帧与计算基准帧不同，会残留 1~2° 偏差
        var (errorCode, error, frozen) = await framework.RunOnFrameworkThread<(int, object?, bool)>(() =>
        {
            if (!clientState.IsGPosing) return (409, new { error = Loc.T("不在 GPose 中") }, false);
            var actor = ResolveActor(actorParam);
            if (actor is null) return (404, new { error = Loc.T("找不到角色") }, false);
            return (0, null, freeze && brio.Freeze(actor));
        });
        if (error is not null)
        {
            await WriteJson(ctx, errorCode, error);
            return;
        }
        if (frozen)
            await framework.DelayTicks(FreezeSettleTicks);

        var (code, payload) = await framework.RunOnFrameworkThread<(int, object)>(() =>
        {
            var actor = ResolveActor(actorParam);
            if (actor is null) return (404, new { error = Loc.T("找不到角色") });
            var ok = brio.ApplyPose(actor, poseJson);
            return (ok ? 200 : 500, new { ok, frozen, objectIndex = actor.ObjectIndex });
        });
        await WriteJson(ctx, code, payload);
    }

    private static Task WriteJson(HttpListenerContext ctx, int code, object payload)
        => WriteRaw(ctx, code, JsonSerializer.Serialize(payload, JsonOpts));

    private static async Task WriteRaw(HttpListenerContext ctx, int code, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
}
