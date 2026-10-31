using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Posegraph.Backend;

namespace Posegraph.Video;

/// <summary>
/// 播放片段时同步放原视频的音乐。后端把整个片段范围的音乐按速度切成 WAV（POST /audio/wav，44.1kHz 立体声 16 位），
/// 一次要来放在内存里；用 Windows 自带的 waveOut 流式播放（不带音频库）：后台线程一小块一小块地喂给声卡，不会断，
/// 游戏卡顿也不影响。放着的时候音乐是时钟（ClipPlayer.Clock = 声卡实际放到的位置），画面跟着音乐走，音乐本身不跳；
/// 只在开始放、拖进度、停止、循环回头（ClipPlayer.Jumped）时定位到画面的时刻。音量在这里直接缩放，调音量不用重新要。
/// 速度变了、换片段、换音乐时重新要。录制时、没开、没有音乐来源时不放（画面按游戏时间走）。
/// </summary>
public sealed class ClipAudio : IDisposable
{
    private const int Rate = 44100;
    private const int Channels = 2;
    private const int BlockSamples = Rate / 20;   // 每块 50 毫秒
    private const int Blocks = 8;                 // 最多排 8 块（400 毫秒）在声卡里

    private readonly IFramework _framework;
    private readonly ClipPlayer _player;
    private readonly Func<BackendClient> _client;
    private readonly object _lock = new();
    private readonly Thread _feeder;
    private volatile bool _disposed;

    private IntPtr _wave;                         // waveOut 句柄
    private readonly IntPtr[] _headers = new IntPtr[Blocks];
    private readonly IntPtr[] _data = new IntPtr[Blocks];
    private short[]? _pcm;                        // 片段范围的音乐（已按速度变速），交错立体声
    private (Clip Clip, string Source, float Speed)? _loaded;
    private bool _loading;
    private bool _running;                        // 正在往声卡喂
    private long _next;                           // 下一块从第几个采样（每声道）喂
    private long _origin;                         // 最近一次跳位置时从第几个采样开始（声卡的已播位置从这里算）
    private float _gain = 0.8f;
    private volatile bool _jumped = true;         // 要按画面的时刻重新定位

    public ClipAudio(IFramework framework, ClipPlayer player, Func<BackendClient> client)
    {
        _framework = framework;
        _player = player;
        _client = client;
        _framework.Update += Tick;
        _player.Jumped += OnJumped;
        _player.Clock = Clock;
        _feeder = new Thread(Feed) { IsBackground = true, Name = "Posegraph music" };
        _feeder.Start();
    }

    private void OnJumped() => _jumped = true;

    /// <summary>片段第几秒（声卡实际放到的位置 × 速度）；没在放音乐时 null（画面按游戏时间走）。</summary>
    private double? Clock()
    {
        lock (_lock)
        {
            if (!_running || _jumped || _pcm is null || _loaded is not { } l || l.Clip != _player.Clip || l.Speed != _player.Speed)
                return null;
            var played = Played();
            if (played >= _pcm.Length / Channels) return null;   // 音乐比片段短：放完了，画面自己走
            return (double)played / Rate * l.Speed;
        }
    }

    public bool Enabled { get; set; } = true;

    public float Volume
    {
        get => _gain;
        set => _gain = value;
    }

    /// <summary>音乐来源（原视频或音频文件的完整路径）；null 不放。</summary>
    public Func<Clip, string?> Source { get; set; } = _ => null;

    /// <summary>为 true 时不放（录制中）。</summary>
    public Func<bool> Muted { get; set; } = () => false;

    /// <summary>最近一次要音乐失败的原因（没有音乐来源、后端没装 PyAV 等）；正常时 null。</summary>
    public string? Error { get; private set; }

    private void Tick(IFramework framework)
    {
        var clip = _player.Clip;
        var source = clip is null ? null : Source(clip);
        if (!Enabled || Muted() || clip is null || source is null || !_player.Playing)
        {
            Halt();
            return;
        }
        var speed = _player.Speed;
        if (_loaded is not { } l || l.Clip != clip || l.Source != source || l.Speed != speed)
        {
            Halt();
            if (!_loading) Load(clip, source, speed);
            return;
        }
        lock (_lock)
        {
            if (_pcm is null || (_running && !_jumped)) return;   // 正在放：音乐是时钟，不追画面
            _jumped = false;
            // 音乐里的位置 = 片段时间 ÷ 速度（变速后的音乐是按速度拉伸过的）
            var want = (long)(_player.Time / speed * Rate);
            if (want >= _pcm.Length / Channels)
            {
                if (_running) waveOutReset(_wave);   // 音乐比片段短：这段没有音乐
                _running = false;
                return;
            }
            Seek(want);
        }
    }

    /// <summary>要整个片段范围的音乐（后台），到了就开始放。</summary>
    private void Load(Clip clip, string source, float speed)
    {
        _loading = true;
        Task.Run(async () =>
        {
            try
            {
                var wav = await _client().AudioWavAsync(source, clip.Start, clip.Start + clip.Duration, speed, 1f);
                var pcm = ParseWav(wav);
                lock (_lock)
                {
                    _pcm = pcm;
                    _loaded = (clip, source, speed);
                }
                Error = null;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                await Task.Delay(3000);   // 失败了（后端没起来、没有音乐）别每帧都要
            }
            finally { _loading = false; }
        });
    }

    /// <summary>当前已经播到第几个采样（每声道）。</summary>
    private long Played()
    {
        var t = new MMTIME { wType = TIME_SAMPLES };
        return waveOutGetPosition(_wave, ref t, Marshal.SizeOf<MMTIME>()) == 0 ? _origin + t.u : _origin;
    }

    /// <summary>从第 sample 个采样起重新放（清掉声卡里排着的块）。调用时持有 _lock。</summary>
    private void Seek(long sample)
    {
        if (!Open()) return;
        waveOutReset(_wave);   // 排着的块都标成完成，已播位置归零
        _origin = _next = Math.Max(0, sample);
        _running = true;
        Fill();
    }

    private void Halt()
    {
        lock (_lock)
        {
            _jumped = true;   // 下次放的时候按画面的时刻定位
            if (!_running) return;
            _running = false;
            if (_wave != IntPtr.Zero) waveOutReset(_wave);
        }
    }

    /// <summary>后台线程：每 10 毫秒把放完的块补上。</summary>
    private void Feed()
    {
        while (!_disposed)
        {
            lock (_lock)
            {
                if (_running) Fill();
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>空着的块（没写过或已放完）填上下一段音乐交给声卡。调用时持有 _lock。</summary>
    private void Fill()
    {
        if (_pcm is null || _wave == IntPtr.Zero) return;
        var total = _pcm.Length / Channels;
        var gain = Math.Clamp(_gain, 0f, 1.5f);
        var buffer = new short[BlockSamples * Channels];
        for (var i = 0; i < Blocks && _next < total; i++)
        {
            var flags = (uint)Marshal.ReadInt32(_headers[i], FlagsOffset);
            if ((flags & WHDR_PREPARED) != 0 && (flags & WHDR_DONE) == 0) continue;   // 还在排队 / 正在放
            if ((flags & WHDR_PREPARED) != 0) waveOutUnprepareHeader(_wave, _headers[i], HeaderSize);
            var count = (int)Math.Min(BlockSamples, total - _next);
            for (var k = 0; k < count * Channels; k++)
                buffer[k] = (short)Math.Clamp(_pcm[_next * Channels + k] * gain, short.MinValue, short.MaxValue);
            Marshal.Copy(buffer, 0, _data[i], count * Channels);
            WriteHeader(_headers[i], _data[i], count * Channels * 2);
            waveOutPrepareHeader(_wave, _headers[i], HeaderSize);
            waveOutWrite(_wave, _headers[i], HeaderSize);
            _next += count;
        }
    }

    private bool Open()
    {
        if (_wave != IntPtr.Zero) return true;
        var format = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = Channels, nSamplesPerSec = Rate, wBitsPerSample = 16,
            nBlockAlign = Channels * 2, nAvgBytesPerSec = Rate * Channels * 2,
        };
        if (waveOutOpen(out _wave, WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
        {
            _wave = IntPtr.Zero;
            Error = Loc.T("系统放不了这段声音");
            return false;
        }
        for (var i = 0; i < Blocks; i++)
        {
            _headers[i] = Marshal.AllocHGlobal(HeaderSize);
            _data[i] = Marshal.AllocHGlobal(BlockSamples * Channels * 2);
            WriteHeader(_headers[i], _data[i], 0);
        }
        return true;
    }

    /// <summary>WAVEHDR：lpData、dwBufferLength，其余清零（标志位清零 = 没准备过）。</summary>
    private static void WriteHeader(IntPtr header, IntPtr data, int bytes)
    {
        for (var o = 0; o < HeaderSize; o += 4) Marshal.WriteInt32(header, o, 0);
        Marshal.WriteIntPtr(header, 0, data);
        Marshal.WriteInt32(header, IntPtr.Size, bytes);
    }

    private static short[] ParseWav(byte[] wav)
    {
        // 后端 wave 模块写的标准 44 字节头之后就是数据；保险起见找 "data" 块
        var at = 12;
        while (at + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, at, 4);
            var size = BitConverter.ToInt32(wav, at + 4);
            if (id == "data")
            {
                var bytes = Math.Min(size, wav.Length - at - 8);
                var pcm = new short[bytes / 2];
                Buffer.BlockCopy(wav, at + 8, pcm, 0, pcm.Length * 2);
                return pcm;
            }
            at += 8 + size;
        }
        throw new InvalidDataException("no data chunk");
    }

    public void Dispose()
    {
        _framework.Update -= Tick;
        _player.Jumped -= OnJumped;
        if (_player.Clock == Clock) _player.Clock = null;
        _disposed = true;
        _feeder.Join(200);
        lock (_lock)
        {
            if (_wave == IntPtr.Zero) return;
            waveOutReset(_wave);
            for (var i = 0; i < Blocks; i++)
            {
                if (((uint)Marshal.ReadInt32(_headers[i], FlagsOffset) & WHDR_PREPARED) != 0)
                    waveOutUnprepareHeader(_wave, _headers[i], HeaderSize);
                Marshal.FreeHGlobal(_headers[i]);
                Marshal.FreeHGlobal(_data[i]);
            }
            waveOutClose(_wave);
            _wave = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- winmm

    private static readonly int HeaderSize = IntPtr.Size == 8 ? 48 : 32;    // WAVEHDR
    private static readonly int FlagsOffset = IntPtr.Size == 8 ? 24 : 16;   // dwFlags
    private const uint WHDR_DONE = 0x01;
    private const uint WHDR_PREPARED = 0x02;
    private const int WAVE_MAPPER = -1;
    private const uint TIME_SAMPLES = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MMTIME
    {
        public uint wType;
        public uint u;
        public uint pad;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr hwo, int device, ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern int waveOutGetPosition(IntPtr hwo, ref MMTIME time, int size);
}
