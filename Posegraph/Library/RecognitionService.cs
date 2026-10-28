using System.Collections.Concurrent;
using Posegraph.Backend;

namespace Posegraph.Library;

public enum RecognitionState { Queued, Running, Done, Failed }

public sealed record RecognitionStatus(RecognitionState State, string? Error = null);

/// <summary>
/// 识别与识别缓存：同一张图（按内容）识别过就从缓存恢复会话，不再跑模型；批量识别按队列逐张进行（后端一次只跑一个模型）。
/// releaseImmediately：模型释放策略是「识别后立即释放」（批量结束时由这里卸载，见 Batch）。
/// </summary>
public sealed partial class RecognitionService(ImageLibrary library, BackendClient client, Func<bool> releaseImmediately) : IDisposable
{
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, RecognitionStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private int _worker;
    private int _batches;   // 进行中的批次数：大于 0 时识别请求带 hold，模型不在每张之后卸载
    private int _held;      // 本轮批次里真的跑过模型（结束时才需要卸载）

    /// <summary>
    /// 批量识别范围（主图 + 辅助视角、拆分模式全部应用、批量队列）：范围内模型只加载一次。
    /// 释放策略为「立即释放」时，所有批次都结束后卸载一次；其他策略照常按空闲时间释放。可以嵌套、并行。
    /// </summary>
    public IAsyncDisposable Batch()
    {
        Interlocked.Increment(ref _batches);
        return new BatchScope(this);
    }

    private sealed class BatchScope(RecognitionService owner) : IAsyncDisposable
    {
        private int _done;

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _done, 1) == 1 ? ValueTask.CompletedTask : owner.EndBatchAsync();
    }

    private async ValueTask EndBatchAsync()
    {
        if (Interlocked.Decrement(ref _batches) > 0 || Interlocked.Exchange(ref _held, 0) == 0 || !releaseImmediately()) return;
        try
        {
            await client.UnloadModelAsync();
        }
        catch
        {
            // 后端没响应时它自己会在空闲一分钟后释放
        }
    }

    /// <summary>图片字节 → PNG（经 Windows 解码）；后端解码不了时用来转码重试，见 CreateAsync。</summary>
    public Func<byte[], Task<byte[]>>? ToPng { get; init; }

    /// <summary>识别失败的详细信息写进 Dalamud 日志（/xllog）：文件真实格式、后端报错、转码重试的经过（由 Plugin 接上）。</summary>
    public Action<string, Exception?>? Log { get; set; }

    private async Task<SessionInfo> CreateAsync(byte[] bytes, string name, bool parallel = false, string? boxes = null, MaskData? masks = null)
    {
        var hold = Volatile.Read(ref _batches) > 0;
        if (hold) Interlocked.Exchange(ref _held, 1);
        try
        {
            return await client.CreateSessionAsync(bytes, name, hold, parallel, boxes, masks);
        }
        catch (BackendException ex) when (ex.StatusCode == 400 && !IsBoxError(ex))
        {
            // 后端解码不了（HEIC、JPEG XL 等改了扩展名的图）：Windows 能解码的话转成 PNG 再试一次，解不了就报原来的错
            Log?.Invoke($"[识别] {name}：{ex.Message}；实际内容：{ImageFormat.Describe(bytes)}", null);
            if (ToPng is null) throw;
            byte[]? png = null;
            try { png = await ToPng(bytes); }
            catch (Exception decode) { Log?.Invoke($"[识别] {name}：Windows 也无法解码，不再重试", decode); }
            if (png is null) throw;
            Log?.Invoke($"[识别] {name}：已用 Windows 解码转成 PNG（{png.Length} 字节），重试", null);
            try
            {
                return await client.CreateSessionAsync(png, name, hold, parallel, boxes, masks);
            }
            catch (Exception retry)
            {
                Log?.Invoke($"[识别] {name}：转成 PNG 后仍然失败", retry);
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && LogFailure(name, bytes, ex))
        {
            throw;   // LogFailure 总是返回 false，这里不会进来：只是在不改变异常的前提下记一笔
        }
    }

    /// <summary>400 是人物框不对（不是图片解码不了）：转码重试没用。</summary>
    private static bool IsBoxError(BackendException ex)
        => ex.Message.Contains("人物框") || ex.Message.Contains("人物遮罩")
           || ex.Message.Contains("person box", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("person mask", StringComparison.OrdinalIgnoreCase);

    private bool LogFailure(string name, byte[] bytes, Exception ex)
    {
        Log?.Invoke($"[识别] {name} 失败；图片：{ImageFormat.Describe(bytes)}", ex);
        return false;
    }

    public int Pending => _queue.Count;
    public bool Busy => Volatile.Read(ref _worker) != 0;

    /// <summary>识别完成一张（批量队列里的）：path、是否成功。</summary>
    public event Action<string, bool>? Completed;

    public RecognitionStatus? Status(string path) => _status.GetValueOrDefault(path);

    /// <summary>打开一张图：有缓存就恢复，没有（或 force）就识别并写入缓存。返回会话与内容哈希。</summary>
    public async Task<(SessionInfo Info, string Sha1)> OpenAsync(string path, bool force = false)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var sha1 = library.RememberHash(path, bytes);
        var name = Path.GetFileNameWithoutExtension(path);
        if (!force && library.ReadCache(sha1) is { } archive)
        {
            try
            {
                return (await client.RestoreSessionAsync(archive, name), sha1);
            }
            catch (BackendException ex) when (ex.StatusCode == 400)
            {
                library.DeleteCache(sha1);   // 缓存坏了或格式过时：重新识别
            }
        }
        var info = await CreateAsync(bytes, name);
        try
        {
            library.WriteCache(sha1, await client.SessionArchiveAsync(info.SessionId));
        }
        catch
        {
            // 缓存写不进去不影响这次使用
        }
        _status[path] = new RecognitionStatus(RecognitionState.Done);
        library.Save();
        return (info, sha1);
    }

    /// <summary>
    /// 合并模式：只识别这些人物框（一张图多人，boxes 见 BackendClient.FormatBoxes）。识别缓存按「图片内容 + 框」记，
    /// 同一张图同样的框再打开不重跑模型。masks：遮罩编辑器改过的人物遮罩（一起作为提示，缓存键也算上它）。
    /// 返回会话、缓存键（会话丢了时用它恢复）与图片内容哈希。
    /// </summary>
    public async Task<(SessionInfo Info, string Key, string Sha1)> OpenPeopleAsync(string path, string boxes, MaskData? masks = null)
        => await OpenPeopleAsync(path, boxes, masks, cacheOnly: false) ?? throw new InvalidOperationException();

    /// <summary>cacheOnly：只读识别缓存，没有缓存返回 null，不跑模型（重新打开图时恢复上次的多人结果）。</summary>
    public async Task<(SessionInfo Info, string Key, string Sha1)?> OpenPeopleAsync(string path, string boxes, MaskData? masks, bool cacheOnly)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var sha1 = library.RememberHash(path, bytes);
        // 缓存键：图片 + 框（+ 遮罩：改过遮罩的识别结果不一样）
        var tag = masks is null ? boxes : $"{boxes}|{masks.Width}x{masks.Height}|{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(masks.Labels))}";
        var key = $"{sha1}-m{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(tag)))[..10]}";
        var name = Path.GetFileNameWithoutExtension(path);
        var count = boxes.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
        if (library.ReadCache(key) is { } archive)
        {
            try
            {
                var restored = await client.RestoreSessionAsync(archive, name);
                if (restored.Humans.Length == count) return (restored, key, sha1);
                // 人数不对：旧后端忽略了人物框、把整张图当一个人存下的缓存，作废重新识别
                library.DeleteCache(key);
                _ = client.DeleteSessionAsync(restored.SessionId).ContinueWith(_ => { });
            }
            catch (BackendException ex) when (ex.StatusCode == 400)
            {
                library.DeleteCache(key);
            }
        }
        if (cacheOnly) return null;
        var info = await CreateAsync(bytes, name, boxes: boxes, masks: masks);
        if (info.Humans.Length != count)
        {
            // 旧后端不认识 boxes 参数，会把整张图当一个人识别：不能当成多人结果用，也不写缓存
            _ = client.DeleteSessionAsync(info.SessionId).ContinueWith(_ => { });
            throw new InvalidOperationException(Loc.T("后端不支持多人识别：请在设置页更新后端，然后重启后端"));
        }
        try
        {
            library.WriteCache(key, await client.SessionArchiveAsync(info.SessionId));
        }
        catch
        {
            // 缓存写不进去不影响这次使用
        }
        return (info, key, sha1);
    }

    private readonly ConcurrentDictionary<string, AuxView> _views = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>辅助视角图上次融合时识别出的视角（菜单里显示）；还没融合过返回 null。</summary>
    public AuxView? ViewOf(string auxPath) => _views.GetValueOrDefault(auxPath);

    /// <summary>
    /// 把主图挂着的辅助视角图（没识别过的先识别，结果同样进识别缓存）融合进主图的会话，之后的重定向默认用融合结果。
    /// 没有辅助图时清掉会话里的融合结果。辅助图的会话用完就删。返回 (辅助图路径, 视角) 列表。
    /// </summary>
    public async Task<(string Path, AuxView View)[]> AttachAuxAsync(string sid, string sha1)
    {
        await using var batch = Batch();
        var aux = library.AuxImages(sha1);
        var sessions = new List<string>();
        try
        {
            foreach (var path in aux)
                sessions.Add((await OpenAsync(path)).Info.SessionId);
            var views = await client.SetViewsAsync(sid, sessions);
            var result = aux.Zip(views).ToArray();
            foreach (var (path, view) in result) _views[path] = view;
            return result;
        }
        finally
        {
            foreach (var s in sessions)
                _ = client.DeleteSessionAsync(s).ContinueWith(_ => { });
        }
    }

    /// <summary>会话丢了（后端重启、被挤出）时用缓存恢复；没有缓存返回 null。key：图片内容哈希，合并模式是 OpenPeopleAsync 给的键。</summary>
    public async Task<SessionInfo?> RestoreAsync(string sha1, string name)
        => library.ReadCache(sha1) is { } archive ? await client.RestoreSessionAsync(archive, name) : null;

    /// <summary>加入批量识别队列（已在队列或已有缓存的跳过）。</summary>
    public void Enqueue(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_status.TryGetValue(path, out var s) && s.State is RecognitionState.Queued or RecognitionState.Running) continue;
            _status[path] = new RecognitionStatus(RecognitionState.Queued);
            _queue.Enqueue(path);
        }
        if (Interlocked.CompareExchange(ref _worker, 1, 0) == 0)
            _ = Task.Run(WorkAsync);
    }

    /// <summary>清空还没开始的；正在识别的那张会做完。</summary>
    public void CancelPending()
    {
        while (_queue.TryDequeue(out var path))
            _status.TryRemove(path, out _);
    }

    private async Task WorkAsync()
    {
        try
        {
            await using var batch = Batch();   // 整个队列只加载一次模型
            await DrainParallelAsync();        // 并行识别（实验，RecognitionService.Parallel.cs）；没开时直接返回
            await DrainAsync();
        }
        finally
        {
            Volatile.Write(ref _worker, 0);
            if (!_queue.IsEmpty && !_cts.IsCancellationRequested && Interlocked.CompareExchange(ref _worker, 1, 0) == 0)
                _ = Task.Run(WorkAsync);
        }
    }

    /// <summary>逐张识别队列里的图，直到队列空或取消。parallel：请求标为并行，后端可共享模型同时处理。</summary>
    private async Task DrainAsync(bool parallel = false)
    {
        while (!_cts.IsCancellationRequested && _queue.TryDequeue(out var path))
        {
            if (!_status.ContainsKey(path)) continue;   // 已取消
            _status[path] = new RecognitionStatus(RecognitionState.Running);
            var ok = false;
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, _cts.Token);
                var sha1 = library.RememberHash(path, bytes);
                // 挂着的辅助视角图一起识别，打开时不用再等
                foreach (var file in library.AuxImages(sha1).Prepend(path))
                {
                    var data = file == path ? bytes : await File.ReadAllBytesAsync(file, _cts.Token);
                    var hash = file == path ? sha1 : library.RememberHash(file, data);
                    if (library.HasCache(hash)) continue;
                    var info = await CreateAsync(data, Path.GetFileNameWithoutExtension(file), parallel);
                    library.WriteCache(hash, await client.SessionArchiveAsync(info.SessionId));
                    await client.DeleteSessionAsync(info.SessionId);   // 批量识别的会话不留在后端，免得挤掉当前图
                }
                _status[path] = new RecognitionStatus(RecognitionState.Done);
                ok = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _status[path] = new RecognitionStatus(RecognitionState.Failed, ex.Message);
            }
            library.Save();
            Completed?.Invoke(path, ok);
        }
    }

    /// <summary>文件改名 / 移动后，状态跟着走。</summary>
    public void Rename(string from, string to)
    {
        if (_status.TryRemove(from, out var s)) _status[to] = s;
    }

    public void Dispose()
    {
        _cts.Cancel();
        CancelPending();
    }
}
