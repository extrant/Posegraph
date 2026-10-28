// 一键包后端更新（BackendUpdater）的检查：在临时目录里摆一个「旧一键包」，本机 HTTP 冒充 GitHub
// （下载加速前缀填本机地址，请求路径就是原来的 GitHub 地址），真跑一遍下载源码 + 补组件 + 替换 + 回滚。
//   dotnet run -c Release                          小的假组件
//   dotnet run -c Release -- <组件 zip 目录>        用 packaging/build_backend_components.py 打出来的真组件和 components.json
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Posegraph.Backend;

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) failures++;
}

var realDir = args.Length > 0 ? args[0] : null;
var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var files = new Dictionary<string, byte[]>();     // 请求路径（不含前导 /）→ 内容
var hits = new Dictionary<string, int>();

// 组件：真组件读 components.json 和 zip；假组件现场做两个小 zip
string manifestJson;
if (realDir is not null)
{
    var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "backend/src/posegraph/components.json")));
    foreach (var c in manifest.RootElement.GetProperty("components").EnumerateArray())
    {
        var url = c.GetProperty("url").GetString()!;
        files[url] = File.ReadAllBytes(Path.Combine(realDir, url[(url.LastIndexOf('/') + 1)..]));
    }
    manifestJson = File.ReadAllText(Path.Combine(repo, "backend/src/posegraph/components.json"));
}
else
{
    var list = new List<object>();
    foreach (var (id, check) in new[] { ("face", "python/Lib/site-packages/fakeface/__init__.py"), ("depth", "backend/checkpoints/fakedepth/model.pt") })
    {
        var data = Zip(new() { [check] = Encoding.UTF8.GetBytes(id + " component") });
        var url = $"https://github.com/extrant/Posegraph/releases/download/backend-components-test/{id}.zip";
        files[url] = data;
        list.Add(new { id, name = id, nameEn = id, check = new[] { check }, url, size = data.Length, sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() });
    }
    manifestJson = JsonSerializer.Serialize(new { tag = "test", components = list });
}

// 「GitHub 源码压缩包」：v0.5.0，带 components.json
var source = Zip(new()
{
    ["Posegraph-0.5.0/README.md"] = "x"u8.ToArray(),
    ["Posegraph-0.5.0/backend/src/posegraph/version.json"] = """{"version": "0.5.0", "runtime": 1}"""u8.ToArray(),
    ["Posegraph-0.5.0/backend/src/posegraph/components.json"] = Encoding.UTF8.GetBytes(manifestJson),
    ["Posegraph-0.5.0/backend/src/posegraph/app.py"] = "# new"u8.ToArray(),
});
files["https://github.com/extrant/Posegraph/archive/refs/tags/v0.5.0.zip"] = source;

using var server = new Server(files, hits);
var proxy = $"http://127.0.0.1:{server.Port}/";

string NewPackage()
{
    var root = Path.Combine(Path.GetTempPath(), "posegraph-updater-check-" + Guid.NewGuid().ToString("N")[..8], "Posegraph-Backend");
    Directory.CreateDirectory(Path.Combine(root, "backend/src/posegraph"));
    Directory.CreateDirectory(Path.Combine(root, "python/Lib/site-packages"));
    File.WriteAllText(Path.Combine(root, "runtime.json"), """{"runtime": 1}""");
    File.WriteAllText(Path.Combine(root, "backend/src/posegraph/version.json"), """{"version": "0.4.0", "runtime": 1}""");
    File.WriteAllText(Path.Combine(root, "backend/src/posegraph/app.py"), "# old");
    return root;
}

var started = new List<string>();
Task Stop() => Task.CompletedTask;
Task<bool> Start(string version)
{
    started.Add(version);
    return Task.FromResult(true);
}

// 1. 旧一键包：缺两个组件，更新一次全部补齐
var pkg = NewPackage();
var backend = Path.Combine(pkg, "backend");
using var updater = new BackendUpdater();
Check(BackendUpdater.NeedsUpdate(backend), "旧版本需要更新");
var error = await updater.UpdateAsync(backend, proxy, Stop, Start);
Check(error is null, $"更新成功 {error}");
Check(BackendUpdater.Installed(backend)?.Version == "0.5.0", "源码换成 0.5.0");
Check(File.ReadAllText(Path.Combine(backend, "src.bak/posegraph/app.py")) == "# old", "旧源码备份在 src.bak");
var manifestCheck = JsonDocument.Parse(manifestJson).RootElement.GetProperty("components").EnumerateArray()
                                .SelectMany(c => c.GetProperty("check").EnumerateArray().Select(x => x.GetString()!)).ToArray();
Check(manifestCheck.All(p => File.Exists(Path.Combine(pkg, p))), "组件都装进一键包");
Check(BackendUpdater.MissingComponents(backend).Length == 0 && !BackendUpdater.NeedsUpdate(backend), "装完不再提示更新");
Check(!Directory.Exists(Path.Combine(pkg, ".download")) || Directory.GetFiles(Path.Combine(pkg, ".download")).Length == 0, "下载的压缩包已删除");
Check(started.SequenceEqual(["0.5.0"]), "启动新版检查一次");
if (realDir is not null)
{
    var dist = Path.Combine(repo, "../Posegraph-dist/Posegraph-Backend");
    foreach (var p in manifestCheck.Where(p => File.Exists(Path.Combine(dist, p))))
        Check(File.ReadAllBytes(Path.Combine(pkg, p)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dist, p))), $"和一键包里的 {p} 一模一样");
}

// 2. 组件删掉一个：只补这一个，源码照样换
var componentUrls = files.Keys.Where(k => k.Contains("/releases/download/")).ToArray();
var before = componentUrls.ToDictionary(u => u, u => hits.GetValueOrDefault(u));
File.Delete(Path.Combine(pkg, manifestCheck[0]));
Check(BackendUpdater.NeedsUpdate(backend), "缺组件也提示更新");
error = await updater.UpdateAsync(backend, proxy, Stop, Start);
Check(error is null, "补装成功");
Check(componentUrls.Count(u => hits.GetValueOrDefault(u) > before[u]) == 1, "只下载缺的那一个组件");

// 3. 组件被改过（校验不符）：不装、不换源码
var pkg2 = NewPackage();
var url0 = componentUrls[0];
var good = files[url0];
files[url0] = good.Select((b, i) => i == good.Length / 2 ? (byte)(b ^ 0xFF) : b).ToArray();
error = await updater.UpdateAsync(Path.Combine(pkg2, "backend"), proxy, Stop, Start);
Check(error is not null && error.Contains("校验不符"), $"校验不符时拒绝：{error}");
Check(BackendUpdater.Installed(Path.Combine(pkg2, "backend"))?.Version == "0.4.0", "校验失败时源码不动");
files[url0] = good;

// 4. 新版启动失败：回滚源码
var pkg3 = NewPackage();
error = await updater.UpdateAsync(Path.Combine(pkg3, "backend"), proxy, Stop, v => Task.FromResult(v == ""));
Check(error is not null && BackendUpdater.Installed(Path.Combine(pkg3, "backend"))?.Version == "0.4.0", "启动失败回滚到旧版");

foreach (var dir in new[] { pkg, pkg2, pkg3 })
    try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch { }
Console.WriteLine(failures == 0 ? "全部通过" : $"{failures} 项失败");
return failures == 0 ? 0 : 1;

static byte[] Zip(Dictionary<string, byte[]> entries)
{
    using var ms = new MemoryStream();
    using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        foreach (var (name, data) in entries)
        {
            using var s = z.CreateEntry(name).Open();
            s.Write(data);
        }
    return ms.ToArray();
}

internal sealed class Server : IDisposable
{
    private readonly HttpListener _listener = new();
    public int Port { get; }

    public Server(Dictionary<string, byte[]> files, Dictionary<string, int> hits)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                {
                    var ctx = await _listener.GetContextAsync();
                    // 加速前缀后面接的就是原地址：/https://github.com/...（HttpListener 会把 // 并成 /）
                    var path = ctx.Request.RawUrl!.TrimStart('/').Replace("https:/github.com", "https://github.com");
                    lock (hits) hits[path] = hits.GetValueOrDefault(path) + 1;
                    if (files.TryGetValue(path, out var data))
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentLength64 = data.Length;
                        await ctx.Response.OutputStream.WriteAsync(data);
                    }
                    else ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        });
    }

    public void Dispose() => _listener.Close();
}
