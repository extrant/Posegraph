using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Posegraph.Backend;

namespace Posegraph.UI;

/// <summary>
/// 合并模式里一张图上涂的人：每个人一个图层，两层涂抹——
/// 看得见（Visible：每个像素属于谁，0 没人，k + 1 为第 k 个人，同一个像素只属于一个人，作为遮罩交给识别模型）
/// 和被挡住的范围（Hidden：这个人被别人或东西挡住的身体大概在哪，只用来放大这个人的范围，不当遮罩）。
/// 这个人的范围（两层合起来的外接框）就是交给识别模型的人物框。坐标是标签图像素（原图等比缩到长边 ≤ 1280，见 MaskData）。
/// 画笔把像素划给当前图层（从别人那里抢过来），橡皮擦只擦当前图层，魔棒按照片颜色选一片相连的区域。
/// 每一笔（按下到松开）、每次魔棒是一步撤销；加、删图层会清空撤销记录。
/// </summary>
public sealed class MaskDocument
{
    public const int MaxPeople = 8;
    private const int MaxUndo = 40;
    private const uint Magic = 0x31504750;   // "PGP1"

    private readonly List<(byte[] Visible, byte[] Hidden)> _undo = new();
    private readonly List<(byte[] Visible, byte[] Hidden)> _redo = new();
    private readonly Dictionary<int, float[]?> _bounds = new();
    private int _boundsVersion = -1;

    public MaskDocument(int width, int height, byte[] rgb, int count, byte[]? visible = null, byte[]? hidden = null)
    {
        Width = width;
        Height = height;
        Rgb = rgb;
        Count = Math.Clamp(count, 0, MaxPeople);
        Visible = visible ?? new byte[width * height];
        Hidden = hidden ?? new byte[width * height];
    }

    /// <summary>识别结果投影出来的遮罩当作看得见的部分（自动初稿）。</summary>
    public MaskDocument(MaskData data) : this(data.Width, data.Height, data.Image, data.Count, (byte[])data.Labels.Clone()) { }

    public int Width { get; }
    public int Height { get; }
    public int Count { get; private set; }
    public byte[] Visible { get; private set; }
    public byte[] Hidden { get; private set; }
    public byte[] Rgb { get; }

    /// <summary>每次改动加一（编辑器按它决定要不要重新生成叠加纹理，范围按它缓存）。</summary>
    public int Version { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private byte[] Plane(bool hidden) => hidden ? Hidden : Visible;

    // ------------------------------------------------------------------ 撤销

    /// <summary>开始新的一步（按下画笔 / 橡皮擦、点魔棒之前）：记下现在的样子供撤销。</summary>
    public void BeginStep()
    {
        _undo.Add(((byte[])Visible.Clone(), (byte[])Hidden.Clone()));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add((Visible, Hidden));
        (Visible, Hidden) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Version++;
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add((Visible, Hidden));
        (Visible, Hidden) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Version++;
    }

    // ------------------------------------------------------------------ 图层

    /// <summary>加一个人（空图层），返回它的序号；满了返回 -1。</summary>
    public int AddLayer()
    {
        if (Count >= MaxPeople) return -1;
        Count++;
        Version++;
        return Count - 1;
    }

    /// <summary>删掉一个人：它的涂抹清掉，后面的人往前挪一位。</summary>
    public void RemoveLayer(int layer)
    {
        if (layer < 0 || layer >= Count) return;
        var label = (byte)(layer + 1);
        foreach (var plane in new[] { Visible, Hidden })
            for (var i = 0; i < plane.Length; i++)
            {
                if (plane[i] == label) plane[i] = 0;
                else if (plane[i] > label) plane[i]--;
            }
        Count--;
        _undo.Clear();
        _redo.Clear();
        Version++;
    }

    /// <summary>清空一个人的一层（可以撤销）。</summary>
    public void Clear(int layer, bool hidden)
    {
        BeginStep();
        var label = (byte)(layer + 1);
        var plane = Plane(hidden);
        for (var i = 0; i < plane.Length; i++)
            if (plane[i] == label) plane[i] = 0;
        Version++;
    }

    /// <summary>
    /// 看得见的部分换成识别结果投影出来的遮罩（可以撤销）；被挡住的范围不动。
    /// labels 里 k + 1 是会话里第 k 个人，layerOf[k] 是它对应的图层。
    /// </summary>
    public void ReplaceVisible(byte[] labels, IReadOnlyList<int> layerOf)
    {
        BeginStep();
        var map = new byte[256];
        for (var k = 0; k < layerOf.Count; k++) map[k + 1] = (byte)(layerOf[k] + 1);
        for (var i = 0; i < Visible.Length; i++) Visible[i] = map[labels[i]];
        Version++;
    }

    // ------------------------------------------------------------------ 画笔、魔棒

    /// <summary>
    /// 画笔 / 橡皮擦的一段（从 a 到 b，圆形笔头，半径 radius 像素）。hidden：涂被挡住的范围这一层。
    /// erase：只擦掉属于 layer 的像素；否则把经过的像素都划给 layer。
    /// </summary>
    public void Stroke(Vector2 a, Vector2 b, float radius, int layer, bool erase, bool hidden = false)
    {
        var label = (byte)(layer + 1);
        var plane = Plane(hidden);
        radius = Math.Max(radius, 0.5f);
        var r2 = radius * radius;
        var x0 = Math.Max((int)MathF.Floor(MathF.Min(a.X, b.X) - radius), 0);
        var x1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.X, b.X) + radius), Width - 1);
        var y0 = Math.Max((int)MathF.Floor(MathF.Min(a.Y, b.Y) - radius), 0);
        var y1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.Y, b.Y) + radius), Height - 1);
        var d = b - a;
        var len2 = d.LengthSquared();
        var changed = false;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                // 像素中心到线段的距离
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var t = len2 > 1e-6f ? Math.Clamp(Vector2.Dot(p - a, d) / len2, 0, 1) : 0;
                if ((p - (a + t * d)).LengthSquared() > r2) continue;
                var i = y * Width + x;
                if (erase)
                {
                    if (plane[i] != label) continue;
                    plane[i] = 0;
                }
                else
                {
                    if (plane[i] == label) continue;
                    plane[i] = label;
                }
                changed = true;
            }
        }
        if (changed) Version++;
    }

    /// <summary>
    /// 魔棒：从 (x, y) 开始，颜色和起点相差不超过 tolerance（0–255，RGB 各通道最大差）的相连像素（4 邻接）。
    /// 返回选中的像素序号。
    /// </summary>
    public List<int> Wand(int x, int y, int tolerance)
    {
        var picked = new List<int>();
        if (x < 0 || y < 0 || x >= Width || y >= Height) return picked;
        var start = y * Width + x;
        int r0 = Rgb[start * 3], g0 = Rgb[start * 3 + 1], b0 = Rgb[start * 3 + 2];
        var seen = new bool[Width * Height];
        var stack = new Stack<int>();
        stack.Push(start);
        seen[start] = true;
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            picked.Add(i);
            int px = i % Width, py = i / Width;
            Visit(px - 1, py); Visit(px + 1, py); Visit(px, py - 1); Visit(px, py + 1);
        }
        return picked;

        void Visit(int vx, int vy)
        {
            if (vx < 0 || vy < 0 || vx >= Width || vy >= Height) return;
            var j = vy * Width + vx;
            if (seen[j]) return;
            if (Math.Abs(Rgb[j * 3] - r0) > tolerance || Math.Abs(Rgb[j * 3 + 1] - g0) > tolerance
                || Math.Abs(Rgb[j * 3 + 2] - b0) > tolerance) return;
            seen[j] = true;
            stack.Push(j);
        }
    }

    /// <summary>把选中的像素加进 layer（remove：从 layer 里去掉，别人的不动）。算一步撤销。</summary>
    public void Apply(IReadOnlyList<int> pixels, int layer, bool remove, bool hidden = false)
    {
        if (pixels.Count == 0) return;
        BeginStep();
        var label = (byte)(layer + 1);
        var plane = Plane(hidden);
        foreach (var i in pixels)
        {
            if (remove) { if (plane[i] == label) plane[i] = 0; }
            else plane[i] = label;
        }
        Version++;
    }

    // ------------------------------------------------------------------ 统计、范围

    /// <summary>每个人看得见部分的像素数。</summary>
    public int[] PixelCounts()
    {
        var counts = new int[Count];
        foreach (var v in Visible)
            if (v > 0 && v <= Count) counts[v - 1]++;
        return counts;
    }

    /// <summary>
    /// 一个人的范围（标签图像素 [x1, y1, x2, y2]，含右下边界）：看得见 + 被挡住两层合起来；没涂过为 null。
    /// 不算零碎的小块（不到这个人像素数的 1%）：涂抹时漏在别处的几个点不会把范围撑到整张图。按 Version 缓存。
    /// </summary>
    public float[]? Bounds(int layer)
    {
        if (_boundsVersion != Version)
        {
            _bounds.Clear();
            _boundsVersion = Version;
        }
        if (_bounds.TryGetValue(layer, out var cached)) return cached;
        var label = (byte)(layer + 1);
        var mine = new bool[Visible.Length];
        var total = 0;
        for (var i = 0; i < mine.Length; i++)
            if (Visible[i] == label || Hidden[i] == label) { mine[i] = true; total++; }
        float[]? result = null;
        if (total > 0)
        {
            var stack = new Stack<int>();
            int x0 = Width, y0 = Height, x1 = -1, y1 = -1;
            for (var start = 0; start < mine.Length; start++)
            {
                if (!mine[start]) continue;
                // 一块相连的区域（4 邻接）；走过的清掉
                int bx0 = Width, by0 = Height, bx1 = -1, by1 = -1, size = 0;
                stack.Push(start);
                mine[start] = false;
                while (stack.Count > 0)
                {
                    var i = stack.Pop();
                    size++;
                    int px = i % Width, py = i / Width;
                    bx0 = Math.Min(bx0, px); bx1 = Math.Max(bx1, px);
                    by0 = Math.Min(by0, py); by1 = Math.Max(by1, py);
                    if (px > 0 && mine[i - 1]) { mine[i - 1] = false; stack.Push(i - 1); }
                    if (px < Width - 1 && mine[i + 1]) { mine[i + 1] = false; stack.Push(i + 1); }
                    if (py > 0 && mine[i - Width]) { mine[i - Width] = false; stack.Push(i - Width); }
                    if (py < Height - 1 && mine[i + Width]) { mine[i + Width] = false; stack.Push(i + Width); }
                }
                if (size < total * 0.01) continue;
                x0 = Math.Min(x0, bx0); x1 = Math.Max(x1, bx1);
                y0 = Math.Min(y0, by0); y1 = Math.Max(y1, by1);
            }
            if (x1 >= 0) result = [x0, y0, x1 + 1, y1 + 1];
        }
        _bounds[layer] = result;
        return result;
    }

    /// <summary>交给识别模型的人物框（原图像素）：这个人的范围四边各放宽 3%（涂抹贴着边缘时手指、头发不被切掉）。没涂过为 null。</summary>
    public float[]? Box(int layer, int[] imageSize)
    {
        if (Bounds(layer) is not { } b) return null;
        var sx = imageSize[0] / (float)Width;
        var sy = imageSize[1] / (float)Height;
        var px = (b[2] - b[0]) * sx * 0.03f;
        var py = (b[3] - b[1]) * sy * 0.03f;
        return
        [
            Math.Max(0, b[0] * sx - px), Math.Max(0, b[1] * sy - py),
            Math.Min(imageSize[0], b[2] * sx + px), Math.Min(imageSize[1], b[3] * sy + py),
        ];
    }

    /// <summary>
    /// 交给识别模型的遮罩：只有看得见的部分，只含 layers 里的人，按 layers 的顺序重新编号（第 i 个为 i + 1）。
    /// </summary>
    public MaskData ToData(IReadOnlyList<int> layers)
    {
        var map = new byte[256];
        for (var i = 0; i < layers.Count; i++) map[layers[i] + 1] = (byte)(i + 1);
        var labels = new byte[Visible.Length];
        for (var i = 0; i < labels.Length; i++) labels[i] = map[Visible[i]];
        return new MaskData(Width, Height, layers.Count, labels, Rgb);
    }

    // ------------------------------------------------------------------ 存取（图库里按图片哈希存，照片像素不存）

    public byte[] Save()
    {
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], Count);
        ms.Write(header);
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(Visible);
            z.Write(Hidden);
        }
        return ms.ToArray();
    }

    /// <summary>读回存的涂抹；尺寸和这次的照片对不上（或格式不对）返回 null。</summary>
    public static MaskDocument? Load(byte[] data, int width, int height, byte[] rgb)
    {
        try
        {
            if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic) return null;
            var w = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
            var h = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
            var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
            if (w != width || h != height || count is < 0 or > MaxPeople) return null;
            using var z = new ZLibStream(new MemoryStream(data, 16, data.Length - 16), CompressionMode.Decompress);
            var visible = new byte[w * h];
            var hidden = new byte[w * h];
            z.ReadExactly(visible);
            z.ReadExactly(hidden);
            return new MaskDocument(w, h, rgb, count, visible, hidden);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }
}
