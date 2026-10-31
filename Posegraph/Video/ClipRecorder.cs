using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Posegraph.Backend;
using Posegraph.UI;

namespace Posegraph.Video;

/// <summary>
/// 把片段录成视频，逐帧离线录：每一帧先摆到第 t 秒（ClipPlayer.RenderAt），等姿势套上、镜头视线收敛，再截游戏画面，
/// 按原视频的宽高比从画面中间裁出来，一帧一帧送给后端编码成 mp4（backend video.FrameWriter）。
/// 不掉帧、每帧镜头都到位，代价是录的时候比实时慢。
/// 截图方式同构图窗口的「ReShade 兼容」：不勾时拿 ImGui 绘制之前的游戏画面（没有 ReShade 效果）；
/// 勾上时拷屏幕上的最终画面，录制期间藏起插件窗口（游戏窗口不能被别的窗口挡住）。两种都在录制期间藏起游戏界面，录完恢复。
/// 普通方式整段录制只建一张自动更新的视口贴图，每帧读回它：每帧新建 / 销毁视口贴图时 NVIDIA 驱动线程崩过（2026-10-30）。
/// </summary>
public sealed class ClipRecorder(IFramework framework, ITextureProvider textures, ITextureReadbackProvider readback, IGameGui gameGui)
{
    private const int MaxSettleTicks = 20;      // 镜头大跳时视线约 10 帧收敛
    private const float SettledDeg = 0.05f;
    private const int DxgiB8G8R8A8 = 87;        // DXGI_FORMAT_B8G8R8A8_UNORM
    private static readonly int[] BgraFormats = [87, 91];   // B8G8R8A8_UNORM / _SRGB：直接读
    private static readonly int[] RgbaFormats = [28, 29];   // R8G8B8A8_UNORM / _SRGB：直接读，后端按 rgba 换

    private CancellationTokenSource? _cts;

    public bool Running { get; private set; }

    /// <summary>ReShade 兼容录制期间为 true：插件窗口都不画（拷的是屏幕上的最终画面）。</summary>
    public bool HideUi { get; private set; }

    public int Done { get; private set; }
    public int Total { get; private set; }

    public void Cancel() => _cts?.Cancel();

    /// <summary>
    /// 录 player 当前片段（全长）到 path。viewportId / viewport：ImGui 主视口（游戏画面），在绘制线程读好传进来。
    /// music：配音乐的原视频 / 音频文件（null 不配），从片段起点（原视频时间）开始截。
    /// 取消时抛 OperationCanceledException（没录完的文件会删掉）；录完把播放器摆回原来的时刻。
    /// </summary>
    public async Task<RenderResult> RecordAsync(BackendClient client, ClipPlayer player, string path, double fps, bool reshade,
                                                uint viewportId, Vector2 viewport, string? music = null)
    {
        string? startWarning = null;
        var clip = player.Clip ?? throw new InvalidOperationException(Loc.T("还没有片段：识别一段视频，或者「打开片段…」"));
        var aspect = clip.ImageSize is [var w, var h, ..] && w > 0 && h > 0 ? (float)w / h : viewport.X / viewport.Y;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Running = true;
        Done = 0;
        Total = (int)Math.Floor(clip.Duration * fps + 1e-6) + 1;
        var resume = player.Time;
        var hidGameUi = false;
        string? renderId = null;
        IDalamudTextureWrap? live = null;
        try
        {
            if (!reshade)
            {
                var (min, size) = CompositionWindow.FitRect(viewport, aspect);
                live = await textures.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
                {
                    ViewportId = viewportId, TakeBeforeImGuiRender = true, KeepTransparency = false, AutoUpdate = true,
                    Uv0 = min / viewport, Uv1 = (min + size) / viewport,
                }, "Posegraph video frames", ct);
            }
            if (!gameGui.GameUiHidden)
            {
                await framework.RunOnFrameworkThread(() => CompositionWindow.SetGameUiVisible(false));
                hidGameUi = true;
            }
            HideUi = reshade;
            var window = Process.GetCurrentProcess().MainWindowHandle;
            for (var i = 0; i < Total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = Math.Min(i / fps, clip.Duration);
                await framework.RunOnFrameworkThread(() => player.RenderAt(t));
                var ready = false;
                for (var k = 0; k < MaxSettleTicks && !ready; k++)
                {
                    await framework.DelayTicks(1);
                    ready = player.AppliedTime == player.Time && player.ViewError < SettledDeg;
                }
                if (player.AppliedTime != player.Time)
                    throw new InvalidOperationException(player.Error ?? Loc.T("姿势没套上（角色还在吗？）"));
                // 摆好的这一帧要先画出来；ReShade 兼容还要经过 ReShade 和系统合成才到屏幕上
                await framework.DelayTicks(reshade ? 4 : 1);
                var frame = live is null ? CaptureScreen(window, aspect) : await ReadAsync(live, ct);
                if (renderId is null)
                {
                    var info = await client.StartRenderAsync(path, frame.Width, frame.Height, fps, music, clip.Start);
                    renderId = info.RenderId;
                    startWarning = info.Warning;
                }
                await client.SendRenderFrameAsync(renderId, frame.Pixels, frame.Width, frame.Height, frame.Order);
                Done = i + 1;
            }
            ct.ThrowIfCancellationRequested();
            var result = await client.FinishRenderAsync(renderId!);
            renderId = null;
            return string.IsNullOrEmpty(startWarning) ? result : result with { Warning = startWarning };
        }
        finally
        {
            live?.Dispose();
            HideUi = false;
            if (hidGameUi) await framework.RunOnFrameworkThread(() => CompositionWindow.SetGameUiVisible(true));
            if (renderId is not null)
            {
                try { await client.CancelRenderAsync(renderId); }
                catch { /* 后端没了就算了 */ }
            }
            await framework.RunOnFrameworkThread(() => player.Seek(resume));
            Running = false;
        }
    }

    /// <summary>读回自动更新的视口贴图（已按 aspect 裁好）。B8G8R8A8 / R8G8B8A8 直接读，其他格式（HDR 等）才转成 B8G8R8A8。</summary>
    private async Task<(byte[] Pixels, int Width, int Height, string Order)> ReadAsync(IDalamudTextureWrap live, CancellationToken ct)
    {
        var (spec, raw) = await readback.GetRawImageAsync(live, default, true, ct);
        if (BgraFormats.Contains(spec.DxgiFormat) || RgbaFormats.Contains(spec.DxgiFormat))
            return Crop(raw, spec.Pitch, 0, 0, spec.Width, spec.Height, BgraFormats.Contains(spec.DxgiFormat) ? "bgra" : "rgba");
        (spec, raw) = await readback.GetRawImageAsync(live, new TextureModificationArgs { DxgiFormat = DxgiB8G8R8A8 }, true, ct);
        return Crop(raw, spec.Pitch, 0, 0, spec.Width, spec.Height, "bgra");
    }

    /// <summary>屏幕上的游戏窗口（含 ReShade），裁出 aspect 比例的中间部分；返回 BGRA（宽高裁成偶数）。</summary>
    private static (byte[] Pixels, int Width, int Height, string Order) CaptureScreen(nint window, float aspect)
    {
        var shot = ScreenCapture.Capture(window);
        var (min, size) = CompositionWindow.FitRect(new Vector2(shot.Width, shot.Height), aspect);
        var x = Math.Clamp((int)MathF.Round(min.X), 0, shot.Width);
        var y = Math.Clamp((int)MathF.Round(min.Y), 0, shot.Height);
        return Crop(shot.Pixels, shot.Width * 4, x, y, Math.Min((int)MathF.Round(size.X), shot.Width - x),
                    Math.Min((int)MathF.Round(size.Y), shot.Height - y), "bgra");
    }

    /// <summary>从每行 pitch 字节、每像素 4 字节的图里拷出 (x, y) 起的一块，宽高向下取偶数（H.264 要求）。</summary>
    private static (byte[] Pixels, int Width, int Height, string Order) Crop(byte[] source, int pitch, int x, int y, int width, int height, string order)
    {
        width &= ~1;
        height &= ~1;
        var output = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(source, (y + row) * pitch + x * 4, output, row * width * 4, width * 4);
        return (output, width, height, order);
    }
}
