using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Posegraph.Backend;
using Posegraph.Library;

// 图库批量识别：串行路径不变；并行（实验）只在设置大于 1 时开几路、请求带 parallel；后端不支持 / 停用 / 削减路数时提示一次。
await SerialByDefault();
await ParallelBatch();
await OldBackendFallsBackToSerial();
await BackendNoticesShownOnce();
await ForegroundNeverParallel();
await SameContentInParallel();
await UndecodableImageRetriesAsPng();
await PeopleNeedAMultiPersonBackend();
Console.WriteLine("8 recognition service scenarios passed (real service/library/client; loopback HTTP).");

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static async Task Run(Fixture f, string[] paths)
{
    var done = 0;
    var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnCompleted(string _, bool __)
    {
        if (Interlocked.Increment(ref done) == paths.Length) all.TrySetResult();
    }
    f.Service.Completed += OnCompleted;
    try
    {
        f.Service.Enqueue(paths);
        await all.Task.WaitAsync(TimeSpan.FromSeconds(20));
        while (f.Service.Busy) await Task.Delay(10);
    }
    finally { f.Service.Completed -= OnCompleted; }
}

static async Task SerialByDefault()
{
    using var f = new Fixture(1);
    var paths = f.Images(5);
    await Run(f, paths);
    Check(f.Backend.Configured.IsEmpty, "serial batch must not touch the parallel setting");
    Check(f.Backend.Peak == 1 && f.Backend.Parallel == 0 && f.Backend.Created == 5, "serial batch changed its requests");
    Check(f.Backend.Held == 5 && f.Backend.Unloaded == 1, "batch hold / final unload changed");
    Check(paths.All(p => f.Service.Status(p)?.State == RecognitionState.Done), "serial batch failed");
    Check(f.Service.TakeNotice() is null, "serial batch produced a notice");
    Console.WriteLine("PASS parallelism 1 keeps the original serial batch");
}

static async Task ParallelBatch()
{
    using var f = new Fixture(3);
    var paths = f.Images(9);
    await Run(f, paths);
    Check(f.Backend.Configured.SequenceEqual([3]), "parallelism was not sent once before the batch");
    Check(f.Backend.Parallel == 9 && f.Backend.Held == 9, "batch requests lost the parallel / hold flags");
    Check(f.Backend.Peak is > 1 and <= 3, $"expected 2–3 requests at once, got {f.Backend.Peak}");
    Check(paths.All(p => f.Service.Status(p)?.State == RecognitionState.Done), "parallel batch failed");
    Check(f.Backend.Deleted.Count == 9 && f.Backend.Unloaded == 1, "batch sessions leaked or the model was not released once");
    Check(f.Service.TakeNotice() is null, "a healthy parallel batch produced a notice");
    Console.WriteLine("PASS parallelism 3 runs flagged requests in up to 3 slots");
}

static async Task OldBackendFallsBackToSerial()
{
    using var f = new Fixture(4);
    f.Backend.OldBackend = true;
    await Run(f, f.Images(4));
    Check(f.Backend.Peak == 1 && f.Backend.Parallel == 0, "unconfirmed backend still got parallel requests");
    Check(f.Service.TakeNotice() == "当前后端不支持并行识别，请更新并重启后端", "missing old-backend notice");
    Console.WriteLine("PASS old backend: serial with one notice");
}

static async Task BackendNoticesShownOnce()
{
    using var f = new Fixture(4);
    f.Backend.State = new { requested = 4, effective = 2, note = "可用内存 4000 MB，只够 2 路", disabled = (string?)null };
    await Run(f, f.Images(4));
    Check(f.Service.TakeNotice() == "并行识别按余量只开了 2 路：可用内存 4000 MB，只够 2 路", "missing reduced-slots notice");
    await Run(f, f.Images(4, 10));
    Check(f.Service.TakeNotice() is null, "the same notice was shown twice");
    f.Backend.State = new { requested = 4, effective = (int?)null, note = "", disabled = "UnsupportedSharedRuntime: fingerprint" };
    await Run(f, f.Images(4, 20));
    Check(f.Service.TakeNotice() == "并行识别已停用，改为逐张识别：UnsupportedSharedRuntime: fingerprint", "missing disabled notice");
    Console.WriteLine("PASS backend reductions / fallback are reported once each");
}

static async Task ForegroundNeverParallel()
{
    using var f = new Fixture(4);
    var path = f.Images(1)[0];
    var (info, _) = await f.Service.OpenAsync(path);
    Check(info.SessionId.Length > 0 && f.Backend.Parallel == 0 && f.Backend.Held == 0 && f.Backend.Configured.IsEmpty,
        "opening a single image used the parallel path");
    Console.WriteLine("PASS single images stay serial");
}

static async Task SameContentInParallel()
{
    using var f = new Fixture(4);
    var paths = Enumerable.Range(0, 4).Select(i => f.Image($"same{i}", 7)).ToArray();
    await Run(f, paths);
    Check(paths.All(p => f.Service.Status(p)?.State == RecognitionState.Done), "same-content images failed in parallel: " + string.Join(" / ", paths.Select(p => f.Service.Status(p)?.Error)));
    Check(Directory.GetFiles(f.Library.CacheDir, "*.npz").Length == 1 && Directory.GetFiles(f.Library.CacheDir, "*.tmp").Length == 0,
        "same-content cache writers collided or left temporary files");
    Console.WriteLine("PASS same-content images in parallel share one cache file");
}

static async Task UndecodableImageRetriesAsPng()
{
    byte[] Png(byte[] _) => [0x89, (byte)'P', (byte)'N', (byte)'G'];
    using (var f = new Fixture(1, image => Task.FromResult(Png(image))))
    {
        f.Backend.OnlyPng = true;
        var (info, _) = await f.Service.OpenAsync(f.Images(1)[0]);
        Check(info.SessionId.Length > 0 && f.Backend.Created == 2, "undecodable image was not retried once as PNG");
        Check(f.Logs.Any(l => l.Message.Contains("无法解码图片") && l.Message.Contains("文件头 00010203")) &&
              f.Logs.Any(l => l.Message.Contains("已用 Windows 解码转成 PNG")), "decode failure / retry was not logged");
    }
    using (var f = new Fixture(1, _ => throw new InvalidOperationException("Windows cannot decode it either")))
    {
        f.Backend.OnlyPng = true;
        var error = await Record(() => f.Service.OpenAsync(f.Images(1)[0]));
        Check(error is BackendException { StatusCode: 400 } && error.Message.Contains("无法解码图片") && f.Backend.Created == 1,
            "a failed conversion must surface the backend's original decode error");
        Check(f.Logs.Any(l => l.Error?.Message == "Windows cannot decode it either"), "the Windows decode failure was not logged");
    }
    using (var f = new Fixture(1, image => Task.FromResult(Png(image))))
    {
        f.Backend.Fail500 = true;
        var error = await Record(() => f.Service.OpenAsync(f.Images(1)[0]));
        Check(error is BackendException { StatusCode: 503 } && f.Backend.Created == 1, "only decode errors (400) may be retried");
        Check(f.Logs.Count == 1 && f.Logs[0].Error is BackendException { StatusCode: 503 }, "other failures must be logged once");
    }
    var formats = new (byte[] Data, string Expected)[]
    {
        ([0x00, 0x00, 0x00, 0x18, .. "ftypheic"u8], "HEIC"), ([0x00, 0x00, 0x00, 0x1C, .. "ftypavif"u8], "AVIF"),
        ([0x49, 0x49, 0xBC, 0x01], "JPEG XR"), ([0xFF, 0x0A, 0xFA], "JPEG XL"), ("<!DOCTYPE html>"u8.ToArray(), "HTML"),
        ([0xFF, 0xD8, 0xFF, 0xE0], "JPEG"), ([], "空文件"),
    };
    foreach (var (data, expected) in formats)
        Check(ImageFormat.Describe(data).StartsWith(expected), $"format sniffing: expected {expected}, got {ImageFormat.Describe(data)}");
    Console.WriteLine("PASS undecodable images retry once as PNG decoded by Windows, with details logged");
}

static async Task PeopleNeedAMultiPersonBackend()
{
    const string boxes = "10.0,20.0,300.0,400.0;400.0,20.0,700.0,400.0";
    using (var f = new Fixture(1))
    {
        f.Backend.OldBackend = true;   // 旧后端忽略 boxes，整张图当一个人
        var error = await Record(() => f.Service.OpenPeopleAsync(f.Images(1)[0], boxes));
        Check(error is InvalidOperationException && error.Message.Contains("后端不支持多人识别"), $"old backend result was accepted: {error}");
        Check(!Directory.Exists(f.Library.CacheDir) || Directory.GetFiles(f.Library.CacheDir, "*.npz").Length == 0, "a one-person result was cached as a two-person session");
    }
    using (var f = new Fixture(1))
    {
        var (info, key, sha1) = await f.Service.OpenPeopleAsync(f.Images(1)[0], boxes);
        Check(info.Humans.Length == 2 && key.StartsWith(sha1 + "-m") && f.Backend.Boxes.Single() == boxes, "boxes were not sent or the people did not come back");
        Check(File.Exists(Path.Combine(f.Library.CacheDir, key + ".npz")), "the multi-person session was not cached under its own key");
    }
    Console.WriteLine("PASS multi-person sessions are checked against the number of boxes");
}

static async Task<Exception?> Record(Func<Task> action)
{
    try { await action(); return null; }
    catch (Exception ex) { return ex; }
}

sealed class Fixture : IDisposable
{
    public readonly string Directory = Path.Combine(Path.GetTempPath(), "Posegraph-LibraryChecks-" + Guid.NewGuid().ToString("N"));
    public readonly FakeBackend Backend = new();
    public readonly BackendClient Client;
    public readonly ImageLibrary Library;
    public readonly RecognitionService Service;
    public readonly List<(string Message, Exception? Error)> Logs = [];

    public Fixture(int parallelism, Func<byte[], Task<byte[]>>? toPng = null)
    {
        System.IO.Directory.CreateDirectory(Directory);
        Library = new ImageLibrary(Directory, Directory);
        Client = new BackendClient(() => Backend.Port);
        Service = new RecognitionService(Library, Client, () => true) { Parallelism = () => parallelism, ToPng = toPng };
        Service.Log = (message, error) => { lock (Logs) Logs.Add((message, error)); };
    }

    public string Image(string name, int marker)
    {
        var path = Path.Combine(Library.Root, name + ".png");
        File.WriteAllBytes(path, [(byte)marker, 1, 2, 3]);
        return path;
    }

    public string[] Images(int count, int from = 0) => Enumerable.Range(from, count).Select(i => Image("image" + i, i)).ToArray();

    public void Dispose()
    {
        Service.Dispose();
        Client.Dispose();
        Backend.Dispose();
        var full = Path.GetFullPath(Directory);
        if (!Path.GetFileName(full).StartsWith("Posegraph-LibraryChecks-")) throw new InvalidOperationException("Unexpected fixture directory");
        System.IO.Directory.Delete(full, true);
    }
}

sealed class FakeBackend : IDisposable
{
    private readonly LoopbackServer _server;
    public readonly ConcurrentQueue<int> Configured = new();
    public readonly ConcurrentQueue<string> Deleted = new();
    public readonly ConcurrentQueue<string> Boxes = new();
    public int Created, Parallel, Held, Peak, Unloaded;
    private int _active, _ids;
    public bool OldBackend, OnlyPng, Fail500;
    public object? State;
    public int Port => _server.Port;

    public FakeBackend() => _server = new LoopbackServer(Handle);

    private async Task<MockResponse> Handle(HttpListenerRequest req)
    {
        var path = req.Url!.AbsolutePath;
        var query = req.Url.Query;
        switch (req.HttpMethod, path)
        {
            case ("POST", "/model/config"):
            {
                using var doc = await JsonDocument.ParseAsync(req.InputStream);
                var limit = doc.RootElement.GetProperty("recognition_parallelism").GetInt32();
                Configured.Enqueue(limit);
                return OldBackend ? Json(new { model = new { config = new { } } })
                    : Json(new { model = new { config = new { recognitionParallelism = limit } } });
            }
            case ("POST", "/sessions"):
            {
                Interlocked.Increment(ref Created);
                var body = new byte[4];
                var read = await req.InputStream.ReadAsync(body);
                if (OnlyPng && (read < 2 || body[0] != 0x89 || body[1] != (byte)'P'))
                    return new MockResponse(400, "{\"detail\":\"无法解码图片：cannot identify image file\"}");
                if (Fail500) return new MockResponse(503, "{\"detail\":\"模型加载失败\"}");
                if (query.Contains("parallel=true")) Interlocked.Increment(ref Parallel);
                if (query.Contains("hold=true")) Interlocked.Increment(ref Held);
                var now = Interlocked.Increment(ref _active);
                lock (Configured) Peak = Math.Max(Peak, now);
                await Task.Delay(80);
                Interlocked.Decrement(ref _active);
                var id = "s" + Interlocked.Increment(ref _ids);
                // 新后端按人物框返回同样多的人；旧后端（OldBackend）不认识 boxes，整张图当一个人
                var boxes = System.Web.HttpUtility.ParseQueryString(query)["boxes"];
                if (boxes is not null) Boxes.Enqueue(boxes);
                var people = boxes is null || OldBackend ? 1 : boxes.Split(';').Length;
                var humans = Enumerable.Range(0, people).Select(_ => new { bbox = new[] { 0f, 0f, 1f, 1f } }).ToArray();
                return Json(new { sessionId = id, name = "image", imageSize = new[] { 1, 1 }, inferenceMs = 1, device = "cpu", humans });
            }
            case ("POST", "/model/unload"):
                Interlocked.Increment(ref Unloaded);
                return Json(new { });
            case ("GET", "/health"):
                return Json(new { status = "ok", model = new { parallel = State ?? new { requested = 3, effective = 3, note = "", disabled = (string?)null } } });
            case ("DELETE", _):
                Deleted.Enqueue(path);
                return Json(new { ok = true });
            case ("GET", _) when path.EndsWith("/archive"):
                return new MockResponse(200, "npz");
            default:
                return new MockResponse(404, "{\"detail\":\"unexpected " + path + "\"}");
        }
    }

    private static MockResponse Json(object value) => new(200, JsonSerializer.Serialize(value));

    public void Dispose() => _server.Dispose();
}
