using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Posegraph.Backend;

/// <summary>识别后端（backend）的 HTTP 客户端。接口见 backend/src/posegraph/app.py。</summary>
public sealed partial class BackendClient(Func<int> port) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // 识别首次要加载模型，可能超过半分钟
    private readonly HttpClient _http = new(new LanguageHandler()) { Timeout = TimeSpan.FromSeconds(180) };

    /// <summary>每个请求带上界面语言，后端返回的提示 / 错误信息按它翻译。
    /// 后端只在本机：不走系统代理（加速器 / 代理软件会改写请求体，后端收到的图片无法解码、连接被重置）。</summary>
    private sealed class LanguageHandler() : DelegatingHandler(new HttpClientHandler { UseProxy = false })
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
        new Dictionary<string, object> { ["device"] = c.BackendDevice, ["release_after"] = c.ReleaseAfter, ["mhr_on_cpu"] = c.MhrOnCpu, ["use_depth"] = c.UseDepth, ["fast"] = c.FastRecognition });

    /// <summary>hold：同一批后面还有图要识别，这张识别完后端先不按「立即释放」卸载模型（整批结束再 UnloadModelAsync）。
    /// parallel：批量队列的请求，后端可以共享模型并行识别（实验，见 BackendClient.Parallel.cs）。</summary>
    /// masks：合并模式的人物遮罩（标签 k + 1 为第 k 个框的人，见 MaskData），和图片一起用 multipart 发。</summary>
    public async Task<SessionInfo> CreateSessionAsync(byte[] image, string name, bool hold = false, bool parallel = false,
                                                     string? boxes = null, MaskData? masks = null)
    {
        var query = $"/sessions?name={Uri.EscapeDataString(name)}{(hold ? "&hold=true" : "")}{(parallel ? "&parallel=true" : "")}"
                    + (boxes is null ? "" : "&boxes=" + Uri.EscapeDataString(boxes));
        HttpContent content;
        if (masks is null)
        {
            content = new ByteArrayContent(image);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        else
        {
            query += $"&mask_width={masks.Width}&mask_height={masks.Height}";
            content = new MultipartFormDataContent
            {
                { new ByteArrayContent(image), "image", "image" },
                { new ByteArrayContent(Compress(masks.Labels)), "masks", "masks" },
            };
        }
        using (content)
        {
            using var resp = await _http.PostAsync(Url(query), content);
            return (await ReadAsync<SessionInfo>(resp))!;
        }
    }

    /// <summary>合并会话里每个人的遮罩（识别网格投影到照片上，近的挡住远的），给遮罩编辑器当初稿。
    /// render 为 false 时只要照片（标签全是 0），从空白开始涂人。</summary>
    /// <summary>会话里第 person 个人的面部表情和置信度。</summary>
    public async Task<FaceResult> FaceAsync(string sid, int person)
    {
        using var resp = await _http.GetAsync(Url($"/sessions/{sid}/face?person={person}"));
        return (await ReadAsync<FaceResult>(resp))!;
    }

    /// <summary>会话里第 person 个人的照片同款灯光（第一次问约几秒，之后读缓存）。</summary>
    public async Task<PhotoLighting> LightingAsync(string sid, int person)
    {
        using var resp = await _http.GetAsync(Url($"/sessions/{sid}/lighting?person={person}"));
        return (await ReadAsync<PhotoLighting>(resp))!;
    }

    public async Task<MaskData> MasksAsync(string sid, bool render = true)
    {
        using var resp = await _http.GetAsync(Url($"/sessions/{sid}/masks{(render ? "" : "?render=false")}"));
        var r = (await ReadAsync<JsonElement>(resp))!;
        return new MaskData(r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(), r.GetProperty("count").GetInt32(),
                            Decompress(r.GetProperty("labels").GetBytesFromBase64()), Decompress(r.GetProperty("image").GetBytesFromBase64()));
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var z = new System.IO.Compression.ZLibStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>合并模式：找出图里的人（不跑姿势模型）。kind：auto / photo（真人）/ anime（插画）。</summary>
    public async Task<DetectResult> DetectAsync(byte[] image, string kind = "auto")
    {
        using var content = new ByteArrayContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PostAsync(Url($"/detect?kind={Uri.EscapeDataString(kind)}"), content);
        return (await ReadAsync<DetectResult>(resp))!;
    }

    /// <summary>人物框 → /sessions 的 boxes 参数「x1,y1,x2,y2;…」（原图像素，一位小数）。</summary>
    public static string FormatBoxes(IEnumerable<float[]> boxes)
        => string.Join(";", boxes.Select(b => string.Join(",", b.Select(v => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))));

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

    /// <summary>onion：合并模式 3D 页的洋葱皮，要一起算出骨架的其他人（会话里的序号和各自的调整值）。</summary>
    public async Task<RetargetResult> RetargetAsync(string sid, int person, RetargetParams p, string? reference, string? race,
                                                    bool trustReference = false, bool useAux = true,
                                                    IEnumerable<(int Person, RetargetParams Params)>? onion = null)
    {
        var others = (onion ?? []).Select(o => new { person = o.Person, @params = o.Params }).ToArray();
        using var resp = await _http.PostAsJsonAsync(Url($"/sessions/{sid}/retarget"),
            new { person, @params = p, reference, race, overlay = true, stick = true, trust_reference = trustReference, use_aux = useAux,
                  onion = others }, Json);
        return (await ReadAsync<RetargetResult>(resp))!;
    }

    /// <summary>把 Brio 里对姿势的改动换成关节偏移：p、reference 用推送那份姿势时的，edited 是现在从 Brio 读回的姿势。</summary>
    public async Task<BrioEditsResult> BrioEditsAsync(string sid, int person, RetargetParams p, string? reference, string? race,
                                                      bool trustReference, bool useAux, string edited)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/sessions/{sid}/brio-edits"),
            new { person, @params = p, reference, race, overlay = false, stick = false, trust_reference = trustReference, use_aux = useAux, edited }, Json);
        return (await ReadAsync<BrioEditsResult>(resp))!;
    }

    public async Task<CameraResult> CameraAsync(string sid, int person, RetargetParams p, int? actor, string? race, bool useAux = true)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/sessions/{sid}/camera"), new { person, @params = p, actor, race, use_aux = useAux }, Json);
        return (await ReadAsync<CameraResult>(resp))!;
    }

    /// <summary>合并模式复刻站位：每个人（会话里的序号、应用时的参数与游戏姿势）的角色应在的世界位置与朝向。</summary>
    public async Task<PlacementResult> PlacementAsync(string sid, int @base, IEnumerable<PlacementInput> people)
    {
        var body = new
        {
            @base,
            people = people.Select(p => new
            {
                person = p.Person, @params = p.Params, reference = p.Reference, race = p.Race, trust_reference = p.Trust,
                model = p.Model is { } m ? new { position = m.Position, rotation = m.Rotation, scale = m.Scale } : null,
            }),
        };
        using var resp = await _http.PostAsJsonAsync(Url($"/sessions/{sid}/placement"), body, Json);
        return (await ReadAsync<PlacementResult>(resp))!;
    }

    /// <summary>给会话挂上辅助视角（各辅助图的会话，取第 0 人）；空数组为去掉。</summary>
    public async Task<AuxView[]> SetViewsAsync(string sid, IEnumerable<string> aux)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/sessions/{sid}/views"), new { aux = aux.ToArray() }, Json);
        return (await ReadAsync<ViewsResult>(resp))!.Views;
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
