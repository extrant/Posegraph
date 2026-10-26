using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Posegraph.Backend;

/// <summary>识别后端（backend）的 HTTP 客户端。接口见 backend/src/posegraph/app.py。</summary>
public sealed class BackendClient(Func<int> port) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // 识别首次要加载模型，可能超过半分钟
    private readonly HttpClient _http = new(new LanguageHandler()) { Timeout = TimeSpan.FromSeconds(180) };

    /// <summary>每个请求带上界面语言，后端返回的提示 / 错误信息按它翻译。</summary>
    private sealed class LanguageHandler() : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.AcceptLanguage.Clear();
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(Loc.Lang));
            return base.SendAsync(request, ct);
        }
    }

    private string Url(string path) => $"http://127.0.0.1:{port()}{path}";

    public async Task<Health?> HealthAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        return await _http.GetFromJsonAsync<Health>(Url("/health"), Json, cts.Token);
    }

    public Task LoadModelAsync() => PostAsync("/model/load", null);

    public Task UnloadModelAsync() => PostAsync("/model/unload", null);

    public Task ShutdownAsync() => PostAsync("/shutdown", null);

    /// <summary>运行时修改设备与释放策略（设备改动在下次加载时生效）。</summary>
    public Task ConfigureAsync(Configuration c) => PostAsync("/model/config",
        new Dictionary<string, object> { ["device"] = c.BackendDevice, ["release_after"] = c.ReleaseAfter, ["mhr_on_cpu"] = c.MhrOnCpu });

    public async Task<SessionInfo> CreateSessionAsync(byte[] image, string name)
    {
        using var content = new ByteArrayContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PostAsync(Url($"/sessions?name={Uri.EscapeDataString(name)}"), content);
        return (await ReadAsync<SessionInfo>(resp))!;
    }

    public Task<byte[]> SessionImageAsync(string sid) => _http.GetByteArrayAsync(Url($"/sessions/{sid}/image"));

    /// <summary>会话存档（npz），存进图库的识别缓存。</summary>
    public async Task<byte[]> SessionArchiveAsync(string sid)
    {
        using var resp = await _http.GetAsync(Url($"/sessions/{sid}/archive"));
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    /// <summary>从识别缓存恢复会话（不加载模型）。</summary>
    public async Task<SessionInfo> RestoreSessionAsync(byte[] archive, string name)
    {
        using var content = new ByteArrayContent(archive);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PostAsync(Url($"/sessions/archive?name={Uri.EscapeDataString(name)}"), content);
        return (await ReadAsync<SessionInfo>(resp))!;
    }

    public async Task DeleteSessionAsync(string sid)
    {
        using var resp = await _http.DeleteAsync(Url($"/sessions/{sid}"));
        await EnsureSuccessAsync(resp);
    }

    public async Task<RetargetResult> RetargetAsync(string sid, int person, RetargetParams p, string? reference, string? race,
                                                    bool trustReference = false)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/sessions/{sid}/retarget"),
            new { person, @params = p, reference, race, overlay = true, stick = true, trust_reference = trustReference }, Json);
        return (await ReadAsync<RetargetResult>(resp))!;
    }

    public async Task<CameraResult> CameraAsync(string sid, int person, RetargetParams p, int? actor, string? race)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/sessions/{sid}/camera"), new { person, @params = p, actor, race }, Json);
        return (await ReadAsync<CameraResult>(resp))!;
    }

    private async Task PostAsync(string path, object? body)
    {
        using var resp = await _http.PostAsJsonAsync(Url(path), body ?? new { }, Json);
        await ReadAsync<JsonElement>(resp);
    }

    /// <summary>非 2xx 时把后端 FastAPI 的 detail 作为异常信息抛出。</summary>
    private static async Task<T?> ReadAsync<T>(HttpResponseMessage resp)
    {
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<T>(Json);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var text = await resp.Content.ReadAsStringAsync();
        string detail = text;
        try { detail = JsonDocument.Parse(text).RootElement.GetProperty("detail").ToString(); } catch { }
        throw new BackendException((int)resp.StatusCode, Loc.F("后端返回 {0}：{1}", (int)resp.StatusCode, detail));
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>后端返回非 2xx。StatusCode 404 = 会话不存在（后端重启或会话被挤出），可从识别缓存恢复。</summary>
public sealed class BackendException(int statusCode, string message) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
}
