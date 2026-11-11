using System.Text;

namespace Posegraph.Dance;

/// <summary>
/// 表情时间轴（.tmb）：
/// - 动作时间轴 chara/action/emote/xxx_loop.tmb（所有种族共用）：TMDH 的总帧数改成新动作的长度，C010 指向的动画名可以换（开场用）；
/// - pap 自带的时间轴：整个重建 —— 身体动画（C009，时长 = 新长度）、可选的整段表情（C010 cfxf_*）、可选的音乐（C063）。
///   原版里按原版舞步定时的表情切换、脚步声、音效都去掉（时机对不上我们的舞）。
/// 音乐放在 pap 时间轴里、标志 0x7（按距离衰减 + 动画结束时停止 + 用绑定点 71），照游戏里循环音效的表情
/// （浇水 sp60_loop、沙锤 sp61_loop）：放在共用的动作时间轴里时停跳不会停（2026-11-08 用户实测）。
/// 格式（偏移都相对条目开头 + 8，也就是 id 所在的位置）参考 VFXEditor 的 TmbFormat（MIT，Copyright 2021 Michael Kaminsky）。
/// 动作时间轴只认游戏里表情循环的那种简单结构（TMDH、TMPP、TMAL、一个 TMAC、TMTR、C010），别的条目报错不改。
/// </summary>
public static class TmbEdit
{
    /// <summary>C063 的「声音位置」标志：0x1 按距离衰减、0x2 动画结束时停止、0x4 用绑定点（游戏里循环音效的表情都是 0x7、绑定点 71）。</summary>
    public const byte StopOnAnimationEnd = 0x02;
    public const byte UseMinMaxRange = 0x01;
    public const byte UseBindId = 0x04;
    public const byte LoopSoundBind = 71;

    private sealed class Track
    {
        public short Id, Time;
        public int Unk;
        public List<short> Entries = [];
    }

    private sealed class Actor
    {
        public short Id, Time;
        public int Unk1, Unk2;
        public List<short> Tracks = [];
    }

    private sealed record Entry(string Magic, short Id, short Time, byte[] Body, string? Text);

    /// <summary>
    /// 改动作时间轴：frames = 新的总帧数（30 帧/秒）；sound = 声音文件的游戏路径（null 不加声音）。
    /// 原文件已有 C063（别的模组加过）时先去掉再加我们的。
    /// </summary>
    public static byte[] RewriteActionTimeline(byte[] tmb, int frames, string? sound, byte soundFlags = UseMinMaxRange | StopOnAnimationEnd,
                                               string? animName = null)
    {
        if (frames is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(frames), "timeline is limited to 32767 frames (about 18 minutes)");
        var r = new Reader(tmb);
        if (r.Magic(0) != "TMLB") throw new InvalidDataException("not a tmb file");
        var count = r.I32(8);
        var pos = 12;
        short headerId = 0, headerUnk = 0, headerUnk2 = 0;
        string? pathText = null;
        var actorIds = new List<short>();
        var actors = new List<Actor>();
        var tracks = new List<Track>();
        var entries = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            var magic = r.Magic(pos);
            var size = r.I32(pos + 4);
            var b = pos + 8;   // 偏移的基准
            switch (magic)
            {
                case "TMDH":
                    headerId = r.I16(b);
                    headerUnk = r.I16(b + 2);
                    headerUnk2 = r.I16(b + 6);
                    break;
                case "TMPP":
                    pathText = r.Str(b + r.I32(b));
                    break;
                case "TMAL":
                    actorIds = r.Shorts(b + r.I32(b), r.I32(b + 4));
                    break;
                case "TMAC":
                    actors.Add(new Actor
                    {
                        Id = r.I16(b), Time = r.I16(b + 2), Unk1 = r.I32(b + 4), Unk2 = r.I32(b + 8),
                        Tracks = r.Shorts(b + r.I32(b + 12), r.I32(b + 16)),
                    });
                    break;
                case "TMTR":
                    if (r.I32(b + 12) != 0) throw new InvalidDataException("tmb track has extra data (TMFC), not supported");
                    tracks.Add(new Track { Id = r.I16(b), Time = r.I16(b + 2), Entries = r.Shorts(b + r.I32(b + 4), r.I32(b + 8)), Unk = r.I32(b + 12) });
                    break;
                case "C010":
                {
                    var body = tmb[(b + 4)..(pos + size)];   // id、时间之后的部分
                    var text = r.Str(b + r.I32(b + 24));
                    entries.Add(new Entry(magic, r.I16(b), r.I16(b + 2), body, text));
                    break;
                }
                case "C063":
                    entries.Add(new Entry(magic, r.I16(b), r.I16(b + 2), [], null));   // 先记下，后面去掉
                    break;
                default:
                    throw new InvalidDataException($"unexpected tmb entry {magic}");
            }
            pos += size;
        }
        if (actors.Count != 1 || entries.Count(e => e.Magic == "C010") != 1)
            throw new InvalidDataException("expected one actor with one animation entry");

        // 去掉已有的声音事件
        var removed = entries.Where(e => e.Magic == "C063").Select(e => e.Id).ToHashSet();
        entries.RemoveAll(e => e.Magic == "C063");
        foreach (var t in tracks) t.Entries.RemoveAll(removed.Contains);

        var anim = entries.Single(e => e.Magic == "C010");
        var animTrack = tracks.FirstOrDefault(t => t.Entries.Contains(anim.Id)) ?? throw new InvalidDataException("animation entry is not on a track");
        // C010：时长、结束帧跟着改（Use Time Control 关着时游戏不看，但保持和原版文件一致的写法）
        var animBody = (byte[])anim.Body.Clone();
        BitConverter.TryWriteBytes(animBody.AsSpan(0, 4), frames);          // Duration
        BitConverter.TryWriteBytes(animBody.AsSpan(16, 4), (float)frames);  // Animation End Frame
        entries[entries.IndexOf(anim)] = anim with { Body = animBody, Text = animName ?? anim.Text };

        // 重新编号：TMAC、TMTR、事件依次从 2 开始（TMDH 是 1），和游戏文件一样
        short next = 2;
        var remap = new Dictionary<short, short>();
        foreach (var a in actors) remap[a.Id] = next++;
        foreach (var t in tracks) remap[t.Id] = next++;
        foreach (var e in entries) remap[e.Id] = next++;
        short? soundId = sound is null ? null : next++;
        actorIds = [.. actorIds.Select(id => remap[id])];
        foreach (var a in actors)
        {
            a.Id = remap[a.Id];
            a.Tracks = [.. a.Tracks.Select(id => remap[id])];
        }
        foreach (var t in tracks)
        {
            t.Id = remap[t.Id];
            t.Entries = [.. t.Entries.Select(id => remap[id])];
        }
        entries = [.. entries.Select(e => e with { Id = remap[e.Id] })];
        if (soundId is { } sid) animTrack.Entries.Add(sid);

        // 写：条目依次排，后面是各个 id 列表，最后是字符串
        var layout = new List<(string Magic, int Size)> { ("TMDH", 0x10) };
        if (pathText is not null) layout.Add(("TMPP", 0x0C));
        layout.Add(("TMAL", 0x10));
        layout.AddRange(actors.Select(_ => ("TMAC", 0x1C)));
        layout.AddRange(tracks.Select(_ => ("TMTR", 0x18)));
        layout.AddRange(entries.Select(e => (e.Magic, 8 + 4 + e.Body.Length)));
        if (soundId is not null) layout.Add(("C063", 0x20));
        var entriesSize = layout.Sum(l => l.Size);
        var extra = 12 + entriesSize;   // id 列表从这里开始
        var listBytes = 2 * (actorIds.Count + actors.Sum(a => a.Tracks.Count) + tracks.Sum(t => t.Entries.Count));
        var strings = new List<byte[]>();
        var stringAt = extra + listBytes;
        int AddString(string s)
        {
            var at = stringAt + strings.Sum(x => x.Length);
            strings.Add([.. Encoding.ASCII.GetBytes(s), 0]);
            return at;
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("TMLB"));
        w.Write(0);   // 总长，最后回填
        w.Write(layout.Count);
        var listCursor = extra;
        int TakeList(int n)
        {
            var at = listCursor;
            listCursor += 2 * n;
            return at;
        }
        void Head(string magic, int size)
        {
            w.Write(Encoding.ASCII.GetBytes(magic));
            w.Write(size);
        }
        int Rel(int absolute) => absolute - (int)ms.Position;   // 相对 id 位置（调用前 Position 已在 id 处）

        Head("TMDH", 0x10);
        w.Write(headerId);
        w.Write(headerUnk);
        w.Write((short)frames);
        w.Write(headerUnk2);
        var deferredStrings = new List<(long Field, int Base, string Text)>();
        if (pathText is not null)
        {
            Head("TMPP", 0x0C);
            deferredStrings.Add((ms.Position, (int)ms.Position, pathText));
            w.Write(0);
        }
        Head("TMAL", 0x10);
        w.Write(Rel(TakeList(actorIds.Count)));
        w.Write(actorIds.Count);
        foreach (var a in actors)
        {
            Head("TMAC", 0x1C);
            var idPos = (int)ms.Position;
            w.Write(a.Id);
            w.Write(a.Time);
            w.Write(a.Unk1);
            w.Write(a.Unk2);
            w.Write(TakeList(a.Tracks.Count) - idPos);
            w.Write(a.Tracks.Count);
        }
        foreach (var t in tracks)
        {
            Head("TMTR", 0x18);
            var idPos = (int)ms.Position;
            w.Write(t.Id);
            w.Write(t.Time);
            w.Write(TakeList(t.Entries.Count) - idPos);
            w.Write(t.Entries.Count);
            w.Write(t.Unk);
        }
        foreach (var e in entries)
        {
            Head(e.Magic, 8 + 4 + e.Body.Length);
            var idPos = (int)ms.Position;
            w.Write(e.Id);
            w.Write(e.Time);
            var body = (byte[])e.Body.Clone();
            if (e.Magic == "C010" && e.Text is not null)
                deferredStrings.Add((ms.Position + 20, idPos, e.Text));   // C010 的路径偏移在 id 之后第 24 字节
            w.Write(body);
        }
        if (soundId is { } soundEntry)
        {
            Head("C063", 0x20);
            var idPos = (int)ms.Position;
            w.Write(soundEntry);
            w.Write((short)0);     // 第 0 帧
            w.Write(1);            // Loop/Duration（游戏文件都是 1）
            w.Write(0);            // Unknown 1
            deferredStrings.Add((ms.Position, idPos, sound!));
            w.Write(0);            // 路径偏移，回填
            w.Write(0);            // Sound Index
            w.Write(soundFlags);
            w.Write((byte)0);      // Bind Id
            w.Write((short)0);     // Unknown 2
        }
        if (ms.Position != extra) throw new InvalidOperationException("tmb layout size mismatch");
        foreach (var id in actorIds) w.Write(id);
        foreach (var a in actors) foreach (var id in a.Tracks) w.Write(id);
        foreach (var t in tracks) foreach (var id in t.Entries) w.Write(id);
        foreach (var (field, idPos, text) in deferredStrings)
        {
            var at = AddString(text);
            var save = ms.Position;
            ms.Position = field;
            w.Write(at - idPos);
            ms.Position = save;
        }
        foreach (var s in strings) w.Write(s);
        ms.Position = 4;
        w.Write((int)ms.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// 重建 pap 自带的时间轴：vanilla = 原版 pap 的时间轴（取 TMDH 的两个未知字段）；animName = pap 里的动画名；
    /// face = 整段的表情动画（如 cfxf_smile，null 不加）；sound = 音乐的游戏路径（null 不加）。
    /// </summary>
    public static byte[] BuildPapTimeline(byte[] vanilla, int frames, string animName, string? face, string? sound)
    {
        if (frames is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(frames), "timeline is limited to 32767 frames (about 18 minutes)");
        short headerUnk = 0, headerUnk2 = 3;
        var r = new Reader(vanilla);
        if (vanilla.Length >= 28 && r.Magic(0) == "TMLB" && r.Magic(12) == "TMDH")
        {
            headerUnk = r.I16(22);
            headerUnk2 = r.I16(26);
        }
        // 事件：身体动画、表情、音乐，各占一条轨道
        var events = new List<(string Magic, int Size)> { ("C009", 0x18) };
        if (face is not null) events.Add(("C010", 0x28));
        if (sound is not null) events.Add(("C063", 0x20));
        const short actorId = 2;
        var trackIds = Enumerable.Range(0, events.Count).Select(i => (short)(3 + i)).ToArray();
        var eventIds = Enumerable.Range(0, events.Count).Select(i => (short)(3 + events.Count + i)).ToArray();
        var entryCount = 3 + events.Count * 2;   // TMDH、TMAL、TMAC + 轨道 + 事件
        var extra = 12 + 0x10 + 0x10 + 0x1C + 0x18 * events.Count + events.Sum(e => e.Size);
        var lists = extra;                          // TMAL 的列表、TMAC 的列表、每条轨道的列表
        var tmacList = lists + 2;
        var trackList = tmacList + 2 * events.Count;
        var strings = new List<byte[]>();
        var stringStart = trackList + 2 * events.Count;
        int AddString(string text)
        {
            var at = stringStart + strings.Sum(x => x.Length);
            strings.Add([.. Encoding.ASCII.GetBytes(text), 0]);
            return at;
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Head(string magic, int size)
        {
            w.Write(Encoding.ASCII.GetBytes(magic));
            w.Write(size);
        }
        w.Write(Encoding.ASCII.GetBytes("TMLB"));
        w.Write(0);
        w.Write(entryCount);
        Head("TMDH", 0x10);
        w.Write((short)1);
        w.Write(headerUnk);
        w.Write((short)frames);
        w.Write(headerUnk2);
        Head("TMAL", 0x10);
        w.Write(lists - (int)ms.Position);
        w.Write(1);
        Head("TMAC", 0x1C);
        var at = (int)ms.Position;
        w.Write(actorId);
        w.Write((short)0);
        w.Write(0);
        w.Write(0);
        w.Write(tmacList - at);
        w.Write(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            Head("TMTR", 0x18);
            at = (int)ms.Position;
            w.Write(trackIds[i]);
            w.Write((short)0);
            w.Write(trackList + 2 * i - at);
            w.Write(1);
            w.Write(0);
        }
        for (var i = 0; i < events.Count; i++)
        {
            var (magic, size) = events[i];
            Head(magic, size);
            at = (int)ms.Position;
            w.Write(eventIds[i]);
            w.Write((short)0);   // 第 0 帧
            switch (magic)
            {
                case "C009":                          // 身体动画（只用于 pap）：时长、未知、动画名
                    w.Write(frames);
                    w.Write(0);
                    w.Write(AddString(animName) - at);
                    break;
                case "C010":                          // 整段表情：时长、未知、用时间控制、未知、起止（0~1）、动画名、未知
                    w.Write(frames);
                    w.Write(0);
                    w.Write((byte)1);
                    w.Write((short)0);
                    w.Write((byte)0);
                    w.Write(0f);
                    w.Write(1f);
                    w.Write(AddString(face!) - at);
                    w.Write(0);
                    break;
                default:                              // C063 音乐：循环 1、未知、路径、声音序号、标志、绑定点、未知
                    w.Write(1);
                    w.Write(0);
                    w.Write(AddString(sound!) - at);
                    w.Write(0);
                    w.Write((byte)(UseMinMaxRange | StopOnAnimationEnd | UseBindId));
                    w.Write(LoopSoundBind);
                    w.Write((short)0);
                    break;
            }
        }
        if (ms.Position != extra) throw new InvalidOperationException("pap timeline layout size mismatch");
        w.Write(actorId);
        foreach (var id in trackIds) w.Write(id);
        foreach (var id in eventIds) w.Write(id);
        foreach (var str in strings) w.Write(str);
        while (ms.Length % 4 != 0) w.Write((byte)0);   // pap 里时间轴按 4 字节对齐
        ms.Position = 4;
        w.Write((int)ms.Length);
        return ms.ToArray();
    }

    /// <summary>pap 自带的时间轴：总帧数和等于原长度的 C010 时长 / 结束帧原地改成 frames。返回原来的总帧数。</summary>
    public static int PatchEmbeddedDuration(byte[] tmb, int frames)
    {
        var r = new Reader(tmb);
        if (tmb.Length < 12 || r.Magic(0) != "TMLB") return 0;
        var count = r.I32(8);
        var pos = 12;
        var old = 0;
        for (var i = 0; i < count && pos + 8 <= tmb.Length; i++)
        {
            var magic = r.Magic(pos);
            var size = r.I32(pos + 4);
            var b = pos + 8;
            if (magic == "TMDH")
            {
                old = r.I16(b + 4);
                BitConverter.TryWriteBytes(tmb.AsSpan(b + 4, 2), (short)frames);
            }
            else if (magic == "C010" && old > 0)
            {
                if (r.I32(b + 4) == old) BitConverter.TryWriteBytes(tmb.AsSpan(b + 4, 4), frames);
                if (Math.Abs(BitConverter.ToSingle(tmb, b + 20) - old) < 0.5f) BitConverter.TryWriteBytes(tmb.AsSpan(b + 20, 4), (float)frames);
            }
            if (size <= 0) break;
            pos += size;
        }
        return old;
    }

    /// <summary>动作时间轴的总帧数（TMDH）。</summary>
    public static int Duration(byte[] tmb)
    {
        var r = new Reader(tmb);
        return tmb.Length >= 28 && r.Magic(0) == "TMLB" && r.Magic(12) == "TMDH" ? r.I16(24) : 0;
    }

    private readonly struct Reader(byte[] data)
    {
        public string Magic(int at) => Encoding.ASCII.GetString(data, at, 4);
        public int I32(int at) => BitConverter.ToInt32(data, at);
        public short I16(int at) => BitConverter.ToInt16(data, at);

        public List<short> Shorts(int at, int n)
        {
            var list = new List<short>(n);
            for (var i = 0; i < n; i++) list.Add(I16(at + 2 * i));
            return list;
        }

        public string Str(int at)
        {
            var end = Array.IndexOf(data, (byte)0, at);
            return Encoding.ASCII.GetString(data, at, (end < 0 ? data.Length : end) - at);
        }
    }
}
