namespace Posegraph.Idle;

/// <summary>
/// 最简单的 .pap 读写：文件头、动画表（每项 40 字节）、Havok 数据、时间轴（tmb）。只换 Havok 数据，
/// 动画表和时间轴原样保留，写回时重算文件头里的三个偏移。移植自 ReAnimate（MIT，Copyright (c) 2026 Nyaughty）。
/// </summary>
public sealed class PapFile
{
    private const uint Magic = 0x20706170;   // "pap "
    private const int HeaderSize = 26;

    public int Version;
    public short AnimCount;
    public int SkeletonId;
    public byte[] InfoBytes = [];
    public byte[] HavokData = [];
    public byte[] TimelineBytes = [];

    public static PapFile Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a pap file");
        var pap = new PapFile { Version = r.ReadInt32(), AnimCount = r.ReadInt16(), SkeletonId = r.ReadInt32() };
        var infoOffset = r.ReadInt32();
        var havokOffset = r.ReadInt32();
        var timelineOffset = r.ReadInt32();
        r.BaseStream.Seek(infoOffset, SeekOrigin.Begin);
        pap.InfoBytes = r.ReadBytes(havokOffset - infoOffset);
        pap.HavokData = r.ReadBytes(timelineOffset - havokOffset);
        pap.TimelineBytes = r.ReadBytes((int)(r.BaseStream.Length - timelineOffset));
        return pap;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(Version);
        w.Write(AnimCount);
        w.Write(SkeletonId);
        var havokOffset = HeaderSize + InfoBytes.Length;
        w.Write(HeaderSize);
        w.Write(havokOffset);
        w.Write(havokOffset + HavokData.Length);
        w.Write(InfoBytes);
        w.Write(HavokData);
        w.Write(TimelineBytes);
        return ms.ToArray();
    }

    /// <summary>.sklb 里的 Havok 数据（骨架）。文件头版本 0x3132 用 16 位偏移，更新的版本用 32 位。</summary>
    public static byte[] SklbHavok(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        if (r.ReadUInt32() != 0x736B6C62u) throw new InvalidDataException("not a sklb file");   // "blks"
        _ = r.ReadInt16();
        int offset;
        if (r.ReadInt16() == 0x3132)
        {
            _ = r.ReadInt16();
            offset = r.ReadInt16();
        }
        else
        {
            _ = r.ReadInt32();
            offset = r.ReadInt32();
        }
        return data[offset..];
    }
}
