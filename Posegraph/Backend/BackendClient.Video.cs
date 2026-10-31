using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Posegraph.Backend;

/// <summary>视频逐帧识别的接口（backend/src/posegraph/video.py）。</summary>
public sealed partial class BackendClient
{
    /// <summary>上传视频（大文件，单独放宽超时）。</summary>
    public async Task<VideoInfo> UploadVideoAsync(string path, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var resp = await _http.PostAsync(Url($"/videos?name={Uri.EscapeDataString(Path.GetFileName(path))}"), content, cts.Token);
        return (await ReadAsync<VideoInfo>(resp))!;
    }

    public async Task<VideoInfo> VideoAsync(string videoId)
    {
        using var resp = await _http.GetAsync(Url($"/videos/{videoId}"));
        return (await ReadAsync<VideoInfo>(resp))!;
    }

    public async Task<byte[]> VideoFrameAsync(string videoId, double t, int maxSide = 960)
    {
        using var resp = await _http.GetAsync(Url($"/videos/{videoId}/frame?t={Inv(t)}&max_side={maxSide}"));
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public async Task<DetectResult> VideoDetectAsync(string videoId, double t, string kind = "auto")
    {
        using var resp = await _http.PostAsync(Url($"/videos/{videoId}/detect?t={Inv(t)}&kind={Uri.EscapeDataString(kind)}"), null);
        return (await ReadAsync<DetectResult>(resp))!;
    }

    public async Task<VideoJobStatus> StartVideoJobAsync(string videoId, VideoJobRequest request)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/videos/{videoId}/jobs"), request, Json);
        return (await ReadAsync<VideoJobStatus>(resp))!;
    }

    public async Task<VideoJobStatus> VideoJobAsync(string jobId)
    {
        using var resp = await _http.GetAsync(Url($"/jobs/{jobId}"));
        return (await ReadAsync<VideoJobStatus>(resp))!;
    }

    public Task CancelVideoJobAsync(string jobId) => PostAsync($"/jobs/{jobId}/cancel", null);

    /// <summary>片段结果原文（JSON），直接存成片段文件。</summary>
    public async Task<string> VideoJobResultAsync(string jobId)
    {
        using var resp = await _http.GetAsync(Url($"/jobs/{jobId}/result"));
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>识别的原始结果（npz，和种族无关），存在片段旁边，换种族时传回来重算。</summary>
    public async Task<byte[]> VideoJobRawAsync(string jobId)
    {
        using var resp = await _http.GetAsync(Url($"/jobs/{jobId}/raw"));
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public async Task<RawInfo> UploadRawAsync(byte[] data)
    {
        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PostAsync(Url("/raws"), content);
        return (await ReadAsync<RawInfo>(resp))!;
    }

    /// <summary>按新的种族 / 骨长 / 表情权重重算片段（不重新识别），返回和识别结果一样格式的 JSON。</summary>
    public async Task<string> RebuildClipAsync(string rawId, ClipRebuildRequest request)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/raws/{rawId}/clip"), request, Json);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>只重算第 first~last 帧的骨骼（改了时间轴修正后），返回这几帧和骨骼列表。</summary>
    public async Task<ClipFrames> RebuildFramesAsync(string rawId, ClipRebuildRequest request, int first, int last)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/raws/{rawId}/clip"), request with { Frames = [first, last] }, Json);
        return (await ReadAsync<ClipFrames>(resp))!;
    }

    /// <summary>暂停在第 frame 帧时用户在 Brio 里改了骨骼：pose 是读回的姿势、shown 是播放器写的那一帧 → 这一帧的修正（delta）。</summary>
    public async Task<EditBones> CaptureEditAsync(string rawId, ClipRebuildRequest request, int person, int frame, string pose, string shown)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/raws/{rawId}/edits/capture"),
            new EditFrameRequest(request.People, request.Smooth, person, frame, pose, shown, null), Json);
        return (await ReadAsync<EditBones>(resp))!;
    }

    /// <summary>复制骨骼姿势：第 frame 帧（带上已有修正）这组骨骼（edits.GROUPS）相对父骨骼的朝向。</summary>
    public async Task<EditBones> PoseEditAsync(string rawId, ClipRebuildRequest request, int person, int frame, string group)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/raws/{rawId}/edits/pose"),
            new EditFrameRequest(request.People, request.Smooth, person, frame, null, null, group), Json);
        return (await ReadAsync<EditBones>(resp))!;
    }

    /// <summary>播放时放的音乐：path（原视频或音频文件）从 start 到 end 秒（原视频时间），按速度变速、按音量缩放的 WAV。</summary>
    public async Task<byte[]> AudioWavAsync(string path, double start, double end, float speed, float volume)
    {
        using var resp = await _http.PostAsJsonAsync(Url("/audio/wav"), new { path, start, end, speed, volume }, Json);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    /// <summary>开始录制：后端把之后送来的逐帧画面编码成 path（mp4，宽高会裁成偶数）。</summary>
    /// <param name="music">配音乐：原视频（或音频）文件的完整路径，null 不配；没有音轨时后端找同名的音频文件。</param>
    /// <param name="musicStart">音乐从原视频的第几秒起（片段的起点）。</param>
    public async Task<RenderInfo> StartRenderAsync(string path, int width, int height, double fps, string? music = null, double musicStart = 0)
    {
        using var resp = await _http.PostAsJsonAsync(Url("/renders"), new { path, width, height, fps, music, music_start = musicStart }, Json);
        return (await ReadAsync<RenderInfo>(resp))!;
    }

    /// <summary>一帧原始像素（每像素 4 字节，order 为 bgra 或 rgba）。</summary>
    public async Task SendRenderFrameAsync(string renderId, byte[] pixels, int width, int height, string order = "bgra")
    {
        using var content = new ByteArrayContent(pixels);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PostAsync(Url($"/renders/{renderId}/frames?width={width}&height={height}&order={order}"), content);
        await EnsureSuccessAsync(resp);
    }

    public async Task<RenderResult> FinishRenderAsync(string renderId)
    {
        using var resp = await _http.PostAsync(Url($"/renders/{renderId}/finish"), null);
        return (await ReadAsync<RenderResult>(resp))!;
    }

    /// <summary>取消录制，删掉没录完的文件。</summary>
    public async Task CancelRenderAsync(string renderId)
    {
        using var resp = await _http.DeleteAsync(Url($"/renders/{renderId}"));
    }

    private static string Inv(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record RawInfo(string RawId, int Frames, int People, int[] Faces);

public sealed record ClipRebuildPerson(
    [property: System.Text.Json.Serialization.JsonPropertyName("race")] string? Race,
    [property: System.Text.Json.Serialization.JsonPropertyName("reference")] string? Reference,
    [property: System.Text.Json.Serialization.JsonPropertyName("params")] RetargetParams Params)
{
    /// <summary>这个人的时间轴修正（Clip.Edits 里 Person 是他的）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("edits")] public List<Posegraph.Video.ClipKey> Edits { get; init; } = [];
}

public sealed record ClipRebuildRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("people")] ClipRebuildPerson[] People,
    [property: System.Text.Json.Serialization.JsonPropertyName("smooth")] double Smooth)
{
    /// <summary>[first, last]：只重算这几帧（RebuildFramesAsync 设）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("frames")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int[]? Frames { get; init; }
}

public sealed record EditFrameRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("people")] ClipRebuildPerson[] People,
    [property: System.Text.Json.Serialization.JsonPropertyName("smooth")] double Smooth,
    [property: System.Text.Json.Serialization.JsonPropertyName("person")] int Person,
    [property: System.Text.Json.Serialization.JsonPropertyName("frame")] int Frame,
    [property: System.Text.Json.Serialization.JsonPropertyName("pose")] string? Pose,
    [property: System.Text.Json.Serialization.JsonPropertyName("shown")] string? Shown,
    [property: System.Text.Json.Serialization.JsonPropertyName("group")] string? Group);

/// <summary>只重算一段的结果：Range = [first, last]，People[p] 的骨骼列表和这几帧（每帧 骨骼数 × 10）。</summary>
public sealed record ClipFrames(int[] Range, ClipFramesPerson[] People);

public sealed record ClipFramesPerson(string[] Bones, float[][] Frames);

/// <summary>收进来的修正 / 复制的骨骼朝向：骨骼名 → 四元数 xyzw；Changed：改了哪些骨骼。</summary>
public sealed record EditBones(Dictionary<string, float[]> Bones, string[]? Changed);

public sealed record RenderInfo(string RenderId, int Width, int Height, string Codec, string? Music = null, string? Warning = null);

/// <summary>MusicSeconds：配上的音乐秒数（0 = 没配）；Warning：没配上的原因（开始录制、合成音乐时的提示）。</summary>
public sealed record RenderResult(string Path, int Frames, string Codec, double MusicSeconds = 0, string? Warning = null);

public sealed record VideoInfo(string VideoId, string Name, double Fps, int Frames, double Duration, int Width, int Height, double[]? Cuts);

public sealed record VideoJobPerson(
    [property: System.Text.Json.Serialization.JsonPropertyName("box")] float[] Box,
    [property: System.Text.Json.Serialization.JsonPropertyName("race")] string? Race,
    [property: System.Text.Json.Serialization.JsonPropertyName("reference")] string? Reference,
    [property: System.Text.Json.Serialization.JsonPropertyName("params")] RetargetParams Params);

public sealed record VideoJobRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("start")] double Start,
    [property: System.Text.Json.Serialization.JsonPropertyName("end")] double End,
    [property: System.Text.Json.Serialization.JsonPropertyName("fps")] double Fps,
    [property: System.Text.Json.Serialization.JsonPropertyName("seed_time")] double SeedTime,
    [property: System.Text.Json.Serialization.JsonPropertyName("smooth")] double Smooth,
    [property: System.Text.Json.Serialization.JsonPropertyName("people")] VideoJobPerson[] People);

/// <summary>state：queued / detecting / estimating / retargeting / done / error / cancelled。</summary>
public sealed record VideoJobStatus(string JobId, string VideoId, string State, int Done, int Total, string? Error, double Elapsed)
{
    public bool Finished => State is "done" or "error" or "cancelled";
}
