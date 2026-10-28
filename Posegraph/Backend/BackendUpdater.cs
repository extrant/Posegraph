using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Posegraph.Backend;

/// <summary>
/// 一键包用户的后端更新。插件与后端同版本号、从同一个 git 标签发布：插件经插件库自动更新后，
/// 这里从 GitHub 下载同一标签的源码压缩包（https://github.com/&lt;repo&gt;/archive/refs/tags/vX.Y.Z.zip），
/// 替换一键包里的 backend\src。
/// 新功能要的 Python 包和模型（面捕、深度图…）做成「组件」：新源码里的 components.json 列出每个组件的检查文件和下载地址
/// （GitHub Release 里的 zip，路径相对一键包根目录），缺哪个就下载哪个、校验 SHA-256 后解压进一键包，
/// 老一键包点一次更新就补齐，不用重新下载整个一键包（打包见 packaging/build_backend_components.py）。
/// 新源码要求的运行环境版本（version.json 的 runtime，Python / PyTorch 本身）高于一键包（根目录 runtime.json）时不替换，提示重新下载完整一键包。
/// 替换前停后端、旧版备份到 src.bak；新版启动失败自动回滚（已解压的组件留着，旧版用不到也不影响）。
/// </summary>
public sealed class BackendUpdater : IDisposable
{
    public const string Repository = "extrant/Posegraph";
    private const long MaxArchiveBytes = 64L * 1024 * 1024;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    // 组件几百 MB，走加速前缀时可能很慢
    private readonly HttpClient _bigHttp = new() { Timeout = TimeSpan.FromHours(2) };

    public sealed record VersionInfo(string Version, int Runtime);

    /// <summary>后端组件（components.json 的一项）。Check：都在才算装了（相对一键包根目录）。</summary>
    public sealed record Component(string Id, string Name, string NameEn, string[] Check, string Url, long Size, string Sha256)
    {
        public string Label => Loc.Lang == "en" ? NameEn : Name;
    }

    public volatile bool Busy;
    public string? Status { get; private set; }

    public static Version PluginVersion => typeof(BackendUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>与插件同版本的后端标签，如 v0.3.1。</summary>
    public static string PluginTag => $"v{PluginVersion.Major}.{PluginVersion.Minor}.{PluginVersion.Build}";

    /// <summary>已安装后端的版本（backend\src\posegraph\version.json）；读不到为 null（旧版本或路径不对）。</summary>
    public static VersionInfo? Installed(string backendDir)
        => ReadVersion(Path.Combine(backendDir, "src", "posegraph", "version.json"));

    /// <summary>一键包的运行环境版本（一键包根目录 runtime.json）；null 表示不是一键包（源码安装请用 git 更新）。</summary>
    public static int? PackageRuntime(string backendDir)
    {
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backendDir.TrimEnd('\\', '/'))) ?? "", "runtime.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("runtime").GetInt32();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>已安装的后端是否比插件旧，或者缺组件（需要更新）。版本读不到也算旧。</summary>
    public static bool NeedsUpdate(string backendDir)
        => Installed(backendDir) is not { } v || !System.Version.TryParse(v.Version, out var installed) || installed < Trim(PluginVersion)
           || MissingComponents(backendDir).Length > 0;

    /// <summary>一键包根目录（backend 的上一级）。</summary>
    private static string PackageRoot(string backendDir) => Path.GetDirectoryName(Path.GetFullPath(backendDir.TrimEnd('\\', '/'))) ?? "";

    /// <summary>已安装的源码要求、但一键包里还没有的组件（旧版源码没有 components.json 时为空）。</summary>
    public static Component[] MissingComponents(string backendDir)
    {
        try
        {
            var path = Path.Combine(backendDir, "src", "posegraph", "components.json");
            return File.Exists(path) ? Missing(ParseComponents(File.ReadAllText(path)), PackageRoot(backendDir)) : [];
        }
        catch
        {
            return [];
        }
    }

    private static Component[] Missing(IEnumerable<Component> all, string root)
        => all.Where(c => c.Check.Any(p => !File.Exists(Path.Combine(root, p)))).ToArray();

    private static Component[] ParseComponents(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("components").EnumerateArray().Select(c => new Component(
            c.GetProperty("id").GetString()!, c.GetProperty("name").GetString()!,
            c.TryGetProperty("nameEn", out var en) ? en.GetString()! : c.GetProperty("name").GetString()!,
            c.GetProperty("check").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            c.GetProperty("url").GetString()!, c.GetProperty("size").GetInt64(), c.GetProperty("sha256").GetString()!)).ToArray();
    }

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static VersionInfo? ReadVersion(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static VersionInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new VersionInfo(doc.RootElement.GetProperty("version").GetString() ?? "", doc.RootElement.GetProperty("runtime").GetInt32());
    }

    /// <summary>
    /// 下载并安装与插件同版本的后端源码。stopBackend：停掉正在运行的后端并等它退出；
    /// startAndCheck：启动后端并确认新版本能正常响应。返回错误信息，成功为 null。
    /// </summary>
    public async Task<string?> UpdateAsync(string backendDir, string proxyPrefix, Func<Task> stopBackend, Func<string, Task<bool>> startAndCheck)
    {
        if (Busy) return Loc.T("正在更新中");
        Busy = true;
        try
        {
            var runtime = PackageRuntime(backendDir);
            if (runtime is null) return Loc.T("不是一键包安装（缺少 runtime.json），源码安装请用 git 更新");

            var tag = PluginTag;
            var url = $"{proxyPrefix.Trim()}https://github.com/{Repository}/archive/refs/tags/{tag}.zip";
            Status = Loc.F("下载 {0}…", tag);
            byte[] data;
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode) return Loc.F("下载失败：HTTP {0}（{1}）", (int)resp.StatusCode, url);
                if (resp.Content.Headers.ContentLength > MaxArchiveBytes) return Loc.T("下载的文件过大，已取消");
                data = await resp.Content.ReadAsByteArrayAsync();
            }

            Status = Loc.T("校验…");
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var root = zip.Entries.FirstOrDefault()?.FullName.Split('/')[0] + "/";
            var srcPrefix = root + "backend/src/";
            var versionEntry = zip.GetEntry(srcPrefix + "posegraph/version.json");
            if (versionEntry is null) return Loc.T("压缩包里没有后端源码（backend/src/posegraph/version.json）");
            VersionInfo incoming;
            using (var reader = new StreamReader(versionEntry.Open()))
                incoming = Parse(await reader.ReadToEndAsync());
            if ("v" + incoming.Version != tag) return Loc.F("版本不符：压缩包是 {0}，需要 {1}", incoming.Version, tag);
            if (incoming.Runtime > runtime)
                return Loc.F("新版后端需要运行环境 {0}，一键包是 {1}：请重新下载完整一键包", incoming.Runtime, runtime);

            // 新源码要的组件：缺的先全部下载、校验好（这时后端还在跑），停后端后再解压
            var packageRoot = PackageRoot(backendDir);
            var manifest = zip.GetEntry(srcPrefix + "posegraph/components.json");
            Component[] missing = [];
            if (manifest is not null)
                using (var reader = new StreamReader(manifest.Open()))
                    missing = Missing(ParseComponents(await reader.ReadToEndAsync()), packageRoot);
            var downloads = new List<string>();
            if (missing.Length > 0)
            {
                var need = missing.Sum(c => c.Size) * 3;   // 压缩包 + 解压后，留余量
                var free = new DriveInfo(Path.GetPathRoot(packageRoot)!).AvailableFreeSpace;
                if (free < need) return Loc.F("磁盘空间不够：组件需要约 {0:0.0} GB，{1} 只剩 {2:0.0} GB", need / 1e9, Path.GetPathRoot(packageRoot), free / 1e9);
                foreach (var c in missing)
                {
                    var (file, error) = await DownloadComponentAsync(c, packageRoot, proxyPrefix);
                    if (error is not null) return error;
                    downloads.Add(file!);
                }
            }

            var src = Path.Combine(backendDir, "src");
            var staged = src + ".new";
            var backup = src + ".bak";
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            var stagedFull = Path.GetFullPath(staged) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(srcPrefix) && e.FullName.Length > srcPrefix.Length))
            {
                var target = Path.GetFullPath(Path.Combine(staged, entry.FullName[srcPrefix.Length..]));
                if (!target.StartsWith(stagedFull, StringComparison.OrdinalIgnoreCase)) return Loc.T("压缩包里有不安全的路径，已取消");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }

            Status = Loc.T("停止后端…");
            await stopBackend();
            for (var i = 0; i < downloads.Count; i++)
            {
                Status = Loc.F("安装组件 {0}…", missing[i].Label);
                var error = Extract(downloads[i], packageRoot);
                if (error is not null) return error;
            }
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.Move(src, backup);
            Directory.Move(staged, src);

            foreach (var file in downloads)
                try { File.Delete(file); } catch { }
            Status = Loc.T("启动新版后端…");
            if (await startAndCheck(incoming.Version)) return null;

            // 回滚：新版起不来就换回旧版
            Status = Loc.T("新版启动失败，回滚…");
            await stopBackend();
            var failed = src + ".failed";
            if (Directory.Exists(failed)) Directory.Delete(failed, true);
            Directory.Move(src, failed);
            Directory.Move(backup, src);
            await startAndCheck("");
            return Loc.F("新版后端启动失败，已回滚到旧版（新版保留在 {0}）", failed);
        }
        catch (Exception ex)
        {
            return Loc.F("更新失败：{0}", ex.Message);
        }
        finally
        {
            Busy = false;
            Status = null;
        }
    }

    /// <summary>下载一个组件到一键包根目录的 .download\（上次下完、校验对的直接用），返回文件路径或错误信息。</summary>
    private async Task<(string? File, string? Error)> DownloadComponentAsync(Component c, string root, string proxyPrefix)
    {
        var dir = Path.Combine(root, ".download");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, c.Id + ".zip");
        if (File.Exists(file) && new FileInfo(file).Length == c.Size && await Sha256Async(file) == c.Sha256) return (file, null);
        var url = proxyPrefix.Trim() + c.Url;
        Status = Loc.F("下载组件 {0}（{1:0} MB）…", c.Label, c.Size / 1048576.0);
        try
        {
            using var resp = await _bigHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return (null, Loc.F("下载组件失败：HTTP {0}（{1}）", (int)resp.StatusCode, url));
            await using (var input = await resp.Content.ReadAsStreamAsync())
            await using (var output = File.Create(file))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n));
                    done += n;
                    if (done > c.Size) return (null, Loc.F("组件 {0} 比预期的大，已取消（{1}）", c.Label, url));
                    Status = Loc.F("下载组件 {0}：{1:0} / {2:0} MB", c.Label, done / 1048576.0, c.Size / 1048576.0);
                }
            }
        }
        catch (Exception ex)
        {
            return (null, Loc.F("下载组件 {0} 失败：{1}（可以在下面填下载加速前缀后重试，已下载的组件不用重下）", c.Label, ex.Message));
        }
        Status = Loc.F("校验组件 {0}…", c.Label);
        if (new FileInfo(file).Length != c.Size || await Sha256Async(file) != c.Sha256)
        {
            try { File.Delete(file); } catch { }
            return (null, Loc.F("组件 {0} 下载不完整或被改动过（校验不符），请重试", c.Label));
        }
        return (file, null);
    }

    private static async Task<string> Sha256Async(string file)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    /// <summary>把组件压缩包解压到一键包根目录（覆盖同名文件）；路径不安全时整个不装。</summary>
    private static string? Extract(string file, string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(file);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(rootFull, entry.FullName));
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(Path.Combine(rootFull, "backend", "src"), StringComparison.OrdinalIgnoreCase))
                return Loc.T("组件压缩包里有不安全的路径，已取消");
        }
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(rootFull, entry.FullName));
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
        _bigHttp.Dispose();
    }
}
