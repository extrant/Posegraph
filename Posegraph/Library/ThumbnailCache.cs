using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace Posegraph.Library;

/// <summary>
/// 图库缩略图：后台解码原图、缩到长边 side 像素再上传，原图纹理立即释放（大图整张常驻显存会占几十 MB）。
/// 同时最多解码 2 张，单张超过 LoadTimeout 算失败（免得一张卡住后面全部排队）；超过 MaxEntries 张时释放最久没画过的。
/// Get / Forget / Clear 在绘制线程调用。
/// </summary>
public sealed class ThumbnailCache(ITextureProvider textures, IPluginLog log, int side) : IDisposable
{
    private const int MaxEntries = 300;
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

    private sealed class Entry
    {
        private IDalamudTextureWrap? _wrap;
        private bool _dropped;
        public volatile bool Failed;
        public long LastUsed;
        public DateTime Stamp;
        public DateTime Checked;   // 上次检查文件修改时间（每 2 秒查一次，不每帧读磁盘）

        public IDalamudTextureWrap? Wrap
        {
            get { lock (this) return _wrap; }
        }

        /// <summary>后台解码完成；如果已经被移出缓存就直接释放。</summary>
        public void Set(IDalamudTextureWrap wrap)
        {
            lock (this)
            {
                if (_dropped) wrap.Dispose();
                else _wrap = wrap;
            }
        }

        public void Drop()
        {
            lock (this)
            {
                _dropped = true;
                _wrap?.Dispose();
                _wrap = null;
            }
        }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _slots = new(2);
    private readonly CancellationTokenSource _cts = new();
    private long _frame;

    /// <summary>返回缩略图；还没解码好返回 null（已排队），解码失败 failed = true。</summary>
    public IDalamudTextureWrap? Get(string path, out bool failed)
    {
        var now = DateTime.UtcNow;
        _entries.TryGetValue(path, out var e);
        if (e is null || now - e.Checked > TimeSpan.FromSeconds(2))
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); }
            catch { stamp = default; }
            if (e is null || e.Stamp != stamp)
            {
                e?.Drop();
                e = _entries[path] = new Entry { Stamp = stamp };
                _ = LoadAsync(path, e);
                Evict();
            }
            e.Checked = now;
        }
        e.LastUsed = ++_frame;
        failed = e.Failed;
        return e.Wrap;
    }

    /// <summary>文件改名 / 移动 / 删除后丢掉旧缓存。</summary>
    public void Forget(string path)
    {
        if (_entries.Remove(path, out var e)) e.Drop();
    }

    /// <summary>丢掉解码失败的条目（刷新时重试）。</summary>
    public void RetryFailed()
    {
        foreach (var (key, e) in _entries.Where(kv => kv.Value.Failed).ToList())
        {
            e.Drop();
            _entries.Remove(key);
        }
    }

    private async Task LoadAsync(string path, Entry e)
    {
        try
        {
            await _slots.WaitAsync(_cts.Token);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(LoadTimeout);
                var bytes = await File.ReadAllBytesAsync(path, timeout.Token);
                e.Set(await TextureImages.ThumbnailAsync(textures, bytes, side, timeout.Token));
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            e.Failed = true;
            log.Warning(ex, $"[Posegraph] 缩略图加载失败：{path}");
        }
    }

    private void Evict()
    {
        if (_entries.Count <= MaxEntries) return;
        foreach (var (key, e) in _entries.OrderBy(kv => kv.Value.LastUsed).Take(_entries.Count - MaxEntries).ToList())
        {
            e.Drop();
            _entries.Remove(key);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var e in _entries.Values) e.Drop();
        _entries.Clear();
    }
}
