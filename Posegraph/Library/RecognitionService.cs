using System.Collections.Concurrent;
using Posegraph.Backend;

namespace Posegraph.Library;

public enum RecognitionState { Queued, Running, Done, Failed }

public sealed record RecognitionStatus(RecognitionState State, string? Error = null);

/// <summary>
/// 识别与识别缓存：同一张图（按内容）识别过就从缓存恢复会话，不再跑模型；批量识别按队列逐张进行（后端一次只跑一个模型）。
/// </summary>
public sealed class RecognitionService(ImageLibrary library, BackendClient client) : IDisposable
{
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, RecognitionStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private int _worker;

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
        var info = await client.CreateSessionAsync(bytes, name);
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

    /// <summary>会话丢了（后端重启、被挤出）时用缓存恢复；没有缓存返回 null。</summary>
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
            while (!_cts.IsCancellationRequested && _queue.TryDequeue(out var path))
            {
                if (!_status.ContainsKey(path)) continue;   // 已取消
                _status[path] = new RecognitionStatus(RecognitionState.Running);
                var ok = false;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, _cts.Token);
                    var sha1 = library.RememberHash(path, bytes);
                    if (!library.HasCache(sha1))
                    {
                        var info = await client.CreateSessionAsync(bytes, Path.GetFileNameWithoutExtension(path));
                        library.WriteCache(sha1, await client.SessionArchiveAsync(info.SessionId));
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
        finally
        {
            Volatile.Write(ref _worker, 0);
            if (!_queue.IsEmpty && !_cts.IsCancellationRequested && Interlocked.CompareExchange(ref _worker, 1, 0) == 0)
                _ = Task.Run(WorkAsync);
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
