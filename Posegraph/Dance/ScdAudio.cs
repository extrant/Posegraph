using System.Text;

namespace Posegraph.Dance;

/// <summary>
/// 把一段 Ogg Vorbis 装进游戏的 .scd：拿游戏里一个现成的 scd 当模板（声音、轨道、音量这些设置照抄），只换第 0 个音频条目的数据。
/// 音频条目在 scd 里排在最后，换掉以后把后面的条目往后挪、改偏移表和文件长度。
/// Vorbis 条目照 VFXEditor 导入 ogg 的写法（MIT，Copyright 2021 Michael Kaminsky）：不加密、整个 ogg 原样放进去，
/// 前面放按 Ogg 页算出来的跳转表。
/// </summary>
public static class ScdAudio
{
    private const int FormatVorbis = 0x06;
    private const int MarkerFlag = 0x01;

    /// <summary>模板 scd + ogg 文件内容 → 新 scd。</summary>
    public static byte[] ReplaceFirstAudio(byte[] template, byte[] ogg)
    {
        var (channels, rate) = OggInfo(ogg);
        var soundCount = BitConverter.ToInt16(template, 0x30);
        var trackCount = BitConverter.ToInt16(template, 0x32);
        var audioCount = BitConverter.ToInt16(template, 0x34);
        var audioTable = BitConverter.ToInt32(template, 0x3C);
        if (audioCount < 1) throw new InvalidDataException("template scd has no audio entry");
        _ = soundCount;
        _ = trackCount;
        var offsets = new int[audioCount];
        for (var i = 0; i < audioCount; i++) offsets[i] = BitConverter.ToInt32(template, audioTable + 4 * i);
        var first = offsets[0];
        if (first <= 0 || offsets.Any(o => o != 0 && o < first)) throw new InvalidDataException("template scd audio entries are not in the expected order");

        // 旧的第 0 个条目占多大：32 字节头 + 子信息 + 数据，补齐到 16
        var oldLength = Pad16(32 + BitConverter.ToInt32(template, first + 0x18) + BitConverter.ToInt32(template, first));
        var oldFlags = BitConverter.ToInt32(template, first + 0x1C);
        var entry = VorbisEntry(ogg, channels, rate, oldFlags & ~MarkerFlag);

        var tailStart = Math.Min(first + oldLength, template.Length);
        var shift = entry.Length - (tailStart - first);
        var result = new byte[first + entry.Length + (template.Length - tailStart)];
        Buffer.BlockCopy(template, 0, result, 0, first);
        Buffer.BlockCopy(entry, 0, result, first, entry.Length);
        Buffer.BlockCopy(template, tailStart, result, first + entry.Length, template.Length - tailStart);
        for (var i = 1; i < audioCount; i++)
            if (offsets[i] > first) BitConverter.TryWriteBytes(result.AsSpan(audioTable + 4 * i, 4), offsets[i] + shift);
        BitConverter.TryWriteBytes(result.AsSpan(0x10, 4), result.Length);
        return result;
    }

    private static byte[] VorbisEntry(byte[] ogg, int channels, int rate, int flags)
    {
        var (seekStep, seekTable) = SeekTable(ogg, rate);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var subInfo = 0x20 + seekTable.Count * 4;
        w.Write(ogg.Length);       // 数据长度
        w.Write(channels);
        w.Write(rate);
        w.Write(FormatVorbis);
        w.Write(0);                // 循环起点（不循环：每圈由时间轴重新触发）
        w.Write(0);                // 循环终点
        w.Write(subInfo);
        w.Write(flags);
        w.Write((short)0);         // 加密方式：不加密
        w.Write((short)0);         // 异或字节
        w.Write(0);                // 异或偏移
        w.Write(0);                // 异或长度
        w.Write(seekStep);
        w.Write(seekTable.Count * 4);
        w.Write(0);                // Vorbis 头长度：0 = 头就在数据里（整个 ogg 原样放）
        w.Write(0);
        w.Write(0);
        foreach (var s in seekTable) w.Write(s);
        w.Write(ogg);
        while (ms.Length % 16 != 0) w.Write((byte)0);
        return ms.ToArray();
    }

    /// <summary>每隔 SeekStep 秒对应数据里哪一页（字节位置），照 VFXEditor 的 PopulateSeekTable。</summary>
    private static (float Step, List<int> Table) SeekTable(byte[] ogg, int rate)
    {
        var step = 0.1f;
        var table = new List<int>();
        for (var i = 0; i + 14 <= ogg.Length; i++)
        {
            if (ogg[i] != 'O' || ogg[i + 1] != 'g' || ogg[i + 2] != 'g' || ogg[i + 3] != 'S' || ogg[i + 4] != 0) continue;
            var samples = BitConverter.ToInt32(ogg, i + 6);
            var time = (float)samples / rate;
            if (table.Count == 1 && time > step) step = time;
            while (step * table.Count - time < 0.02f) table.Add(i);
        }
        return (step, table);
    }

    /// <summary>Vorbis 识别头里的声道数、采样率。</summary>
    public static (int Channels, int Rate) OggInfo(byte[] ogg)
    {
        var at = IndexOf(ogg, "\u0001vorbis"u8.ToArray());
        if (ogg.Length < 4 || Encoding.ASCII.GetString(ogg, 0, 4) != "OggS" || at < 0 || at + 16 > ogg.Length)
            throw new InvalidDataException("not an ogg vorbis file");
        return (ogg[at + 11], BitConverter.ToInt32(ogg, at + 12));
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Length && i < 4096; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        return -1;
    }

    private static int Pad16(int n) => (n + 15) / 16 * 16;
}
