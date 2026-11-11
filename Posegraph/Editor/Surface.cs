using System.Numerics;

namespace Posegraph.Editor;

/// <summary>表面上离某点最近的地方。Signed：点在表面外侧（法线一侧）为正，钻进去为负。Actor / Bone：这块表面属于谁、主要跟哪根骨骼动。</summary>
public readonly record struct Contact(Vector3 Point, Vector3 Normal, float Signed, int Actor, string Bone);

/// <summary>
/// 身体表面（编辑中角色的模型空间里的一堆三角形，可以来自自己和别的角色）：均匀网格加速的最近点查询。
/// 法线用顶点法线按重心坐标插值（模型文件里的法线朝外），所以能分清在表面外侧还是钻进去了。
/// </summary>
public sealed class Surface
{
    // 格子大小按三角形大小定（边长中位数的 3 倍，1.2–5cm）：高面数的 mod 身体一格里塞几百个三角形，查一次要扫很多
    private readonly float Cell;

    private readonly Vector3[] _p;
    private readonly Vector3[] _n;
    private readonly int[] _tri;          // 每 3 个一组
    private readonly int[] _actor;        // 每个三角形
    private readonly string[] _bone;      // 每个三角形
    private readonly Dictionary<(int, int, int), List<int>> _grid = [];
    private readonly int[] _stamp;         // 查询时标记看过的三角形（比每次新建集合快；只在一个线程里用）
    private int _query;
    private readonly sbyte[] _covered;     // 被外层盖住：0 还没算，1 盖住，2 露在外面（按需算、缓存）
    private readonly int[] _coverStamp;
    private int _coverQuery;
    private const float CoverReach = 0.08f;

    public int TriangleCount => _tri.Length / 3;

    private Surface(Vector3[] p, Vector3[] n, int[] tri, int[] actor, string[] bone)
    {
        (_p, _n, _tri, _actor, _bone) = (p, n, tri, actor, bone);
        _stamp = new int[actor.Length];
        _covered = new sbyte[actor.Length];
        _coverStamp = new int[actor.Length];
        var count = _actor.Length;
        var step = Math.Max(1, count / 2000);
        var edges = new List<float>();
        for (var s = 0; s < count; s += step)
            edges.Add(Vector3.Distance(_p[_tri[s * 3]], _p[_tri[s * 3 + 1]]));
        edges.Sort();
        Cell = edges.Count > 0 ? Math.Clamp(edges[edges.Count / 2] * 3f, 0.012f, 0.05f) : 0.05f;
        for (var t = 0; t < _actor.Length; t++)
        {
            Vector3 a = _p[_tri[t * 3]], b = _p[_tri[t * 3 + 1]], c = _p[_tri[t * 3 + 2]];
            var lo = Key(Vector3.Min(a, Vector3.Min(b, c)));
            var hi = Key(Vector3.Max(a, Vector3.Max(b, c)));
            for (var x = lo.Item1; x <= hi.Item1; x++)
            for (var y = lo.Item2; y <= hi.Item2; y++)
            for (var z = lo.Item3; z <= hi.Item3; z++)
            {
                if (!_grid.TryGetValue((x, y, z), out var list)) _grid[(x, y, z)] = list = [];
                list.Add(t);
            }
        }
    }

    /// <summary>一块网格：顶点、法线、三角形（每 3 个一组）、每个三角形主要跟的骨骼，以及哪些三角形要（null 全要）。</summary>
    public sealed record Piece(Vector3[] Positions, Vector3[] Normals, int[] Triangles, string[] TriangleBones, int Actor, Func<int, bool>? Keep = null);

    public static Surface Build(IEnumerable<Piece> pieces)
    {
        var p = new List<Vector3>();
        var n = new List<Vector3>();
        var tri = new List<int>();
        var actor = new List<int>();
        var bone = new List<string>();
        foreach (var piece in pieces)
        {
            var offset = p.Count;
            p.AddRange(piece.Positions);
            n.AddRange(piece.Normals);
            for (var t = 0; t < piece.Triangles.Length / 3; t++)
            {
                if (piece.Keep is { } keep && !keep(t)) continue;
                tri.Add(piece.Triangles[t * 3] + offset);
                tri.Add(piece.Triangles[t * 3 + 1] + offset);
                tri.Add(piece.Triangles[t * 3 + 2] + offset);
                actor.Add(piece.Actor);
                bone.Add(piece.TriangleBones[t]);
            }
        }
        return new Surface([.. p], [.. n], [.. tri], [.. actor], [.. bone]);
    }

    /// <summary>只留 keep(角色, 骨骼) 为真的三角形（「贴到部位」时只要选中的部位）。</summary>
    public Surface Subset(Func<int, string, bool> keep)
    {
        var tri = new List<int>();
        var actor = new List<int>();
        var bone = new List<string>();
        for (var t = 0; t < _actor.Length; t++)
        {
            if (!keep(_actor[t], _bone[t])) continue;
            tri.Add(_tri[t * 3]);
            tri.Add(_tri[t * 3 + 1]);
            tri.Add(_tri[t * 3 + 2]);
            actor.Add(_actor[t]);
            bone.Add(_bone[t]);
        }
        return new Surface(_p, _n, [.. tri], [.. actor], [.. bone]);
    }

    /// <summary>keep(角色, 骨骼) 为真的三角形的三个顶点（画高亮用）。</summary>
    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Triangles(Func<int, string, bool> keep)
        => Triangles((a, b, _) => keep(a, b));

    /// <summary>按（角色, 骨骼, 三角形中心）挑三角形。</summary>
    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Triangles(Func<int, string, Vector3, bool> keep)
    {
        for (var t = 0; t < _actor.Length; t++)
        {
            Vector3 a = _p[_tri[t * 3]], b = _p[_tri[t * 3 + 1]], c = _p[_tri[t * 3 + 2]];
            if (keep(_actor[t], _bone[t], (a + b + c) / 3f)) yield return (a, b, c);
        }
    }

    /// <summary>
    /// center 周围 radius 以内（按三角形中心算）、keep 为真的三角形，带法线（三个顶点法线的平均）和离 center 的距离。
    /// 用网格只看附近的格子，高面数身体上每帧调也不慢。「贴到部位」预览只画光标附近这一块用。
    /// </summary>
    public List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, float Distance)> Around(Vector3 center, float radius, Func<int, string, Vector3, bool> keep)
    {
        var result = new List<(Vector3, Vector3, Vector3, Vector3, float)>();
        var lo = Key(center - new Vector3(radius));
        var hi = Key(center + new Vector3(radius));
        var stamp = ++_query;
        for (var x = lo.Item1; x <= hi.Item1; x++)
        for (var y = lo.Item2; y <= hi.Item2; y++)
        for (var z = lo.Item3; z <= hi.Item3; z++)
        {
            if (!_grid.TryGetValue((x, y, z), out var list)) continue;
            foreach (var t in list)
            {
                if (_stamp[t] == stamp) continue;
                _stamp[t] = stamp;
                int i0 = _tri[t * 3], i1 = _tri[t * 3 + 1], i2 = _tri[t * 3 + 2];
                var c = (_p[i0] + _p[i1] + _p[i2]) / 3f;
                var d = Vector3.Distance(c, center);
                if (d > radius || !keep(_actor[t], _bone[t], c)) continue;
                var n = _n[i0] + _n[i1] + _n[i2];
                if (n.LengthSquared() < 1e-12f) n = Vector3.Cross(_p[i1] - _p[i0], _p[i2] - _p[i0]);
                result.Add((_p[i0], _p[i1], _p[i2], n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.Zero, d));
            }
        }
        return result;
    }

    /// <summary>按（角色, 骨骼, 三角形中心）挑出一部分表面。</summary>
    public Surface Subset(Func<int, string, Vector3, bool> keep)
    {
        var tri = new List<int>();
        var actor = new List<int>();
        var bone = new List<string>();
        for (var t = 0; t < _actor.Length; t++)
        {
            var center = (_p[_tri[t * 3]] + _p[_tri[t * 3 + 1]] + _p[_tri[t * 3 + 2]]) / 3f;
            if (!keep(_actor[t], _bone[t], center)) continue;
            tri.Add(_tri[t * 3]);
            tri.Add(_tri[t * 3 + 1]);
            tri.Add(_tri[t * 3 + 2]);
            actor.Add(_actor[t]);
            bone.Add(_bone[t]);
        }
        return new Surface(_p, _n, [.. tri], [.. actor], [.. bone]);
    }

    /// <summary>射线（origin 出发沿 direction）最先碰到的表面，碰不到时 null。法线朝着射线来的方向一侧时才算（从外面点到的）。</summary>
    public Contact? Raycast(Vector3 origin, Vector3 direction, float maxDistance = 50f)
    {
        direction = Vector3.Normalize(direction);
        var best = maxDistance;
        var bestT = -1;
        Vector3 bestW = default;
        for (var t = 0; t < _actor.Length; t++)
        {
            Vector3 a = _p[_tri[t * 3]], b = _p[_tri[t * 3 + 1]], c = _p[_tri[t * 3 + 2]];
            // Möller–Trumbore
            Vector3 e1 = b - a, e2 = c - a, h = Vector3.Cross(direction, e2);
            var det = Vector3.Dot(e1, h);
            if (MathF.Abs(det) < 1e-12f) continue;
            var inv = 1f / det;
            var s = origin - a;
            var u = inv * Vector3.Dot(s, h);
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1);
            var v = inv * Vector3.Dot(direction, q);
            if (v < 0 || u + v > 1) continue;
            var dist = inv * Vector3.Dot(e2, q);
            if (dist <= 1e-5f || dist >= best) continue;
            (best, bestT, bestW) = (dist, t, new Vector3(1 - u - v, u, v));
        }
        if (bestT < 0) return null;
        var normal = bestW.X * _n[_tri[bestT * 3]] + bestW.Y * _n[_tri[bestT * 3 + 1]] + bestW.Z * _n[_tri[bestT * 3 + 2]];
        normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : -direction;
        return new Contact(origin + direction * best, normal, 0, _actor[bestT], _bone[bestT]);
    }

    /// <summary>离 point 最近的表面（maxDistance 以内），没有时 null。</summary>
    public Contact? Closest(Vector3 point, float maxDistance)
    {
        var best = maxDistance * maxDistance;
        var bestT = -1;
        Vector3 bestQ = default, bestW = default;
        // 从点所在的格子一圈圈往外找：第 r 圈的格子离点至少 (r−1)·Cell 远，已经找到更近的就不用再往外了
        // （以前把半径内的格子全扫一遍，高面数的 mod 身体上一次解算要几十秒）
        var center = Key(point);
        var rings = (int)MathF.Ceiling(maxDistance / Cell);
        var stamp = ++_query;
        for (var r = 0; r <= rings; r++)
        {
            if (r > 0 && bestT >= 0 && best <= ((r - 1) * Cell) * ((r - 1) * Cell)) break;
            for (var dx = -r; dx <= r; dx++)
            for (var dy = -r; dy <= r; dy++)
            {
                // 只走这一圈的外壳：x、y 在边上时 z 整列都是外壳，否则只有 z = ±r 两格
                var edge = Math.Abs(dx) == r || Math.Abs(dy) == r;
                for (var dz = -r; dz <= r; dz += edge || r == 0 ? 1 : 2 * r)
                {
                if (!_grid.TryGetValue((center.Item1 + dx, center.Item2 + dy, center.Item3 + dz), out var list)) continue;
                foreach (var t in list)
                {
                    if (_stamp[t] == stamp) continue;
                    _stamp[t] = stamp;
                    if (Covered(t)) continue;
                    var (q, w) = ClosestOnTriangle(point, _p[_tri[t * 3]], _p[_tri[t * 3 + 1]], _p[_tri[t * 3 + 2]]);
                    var d = Vector3.DistanceSquared(point, q);
                    if (d >= best) continue;
                    (best, bestT, bestQ, bestW) = (d, t, q, w);
                }
                }
            }
        }
        if (bestT < 0) return null;
        Vector3 a = _p[_tri[bestT * 3]], b = _p[_tri[bestT * 3 + 1]], c = _p[_tri[bestT * 3 + 2]];
        var normal = bestW.X * _n[_tri[bestT * 3]] + bestW.Y * _n[_tri[bestT * 3 + 1]] + bestW.Z * _n[_tri[bestT * 3 + 2]];
        if (normal.LengthSquared() < 1e-12f) normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() < 1e-12f) return null;
        normal = Vector3.Normalize(normal);
        var signed = Vector3.Dot(point - bestQ, normal);
        // 模型是开口的（袖口、腰口、领口）：离得远的点最近点会落在开口的边上，连线几乎和法线垂直，
        // 点积是个小负数，看起来像「压进去」，其实在开口外面的空中。连线偏离法线超过 60° 的按在外面算
        var distance = MathF.Sqrt(best);
        if (signed < 0 && -signed < 0.5f * distance) signed = distance;
        return new Contact(bestQ, normal, signed, _actor[bestT], _bone[bestT]);
    }

    /// <summary>
    /// 这个三角形被外层盖住了吗（衣服底下的皮肤、双层布料的里层……）：从中心沿法线往外打一条 8cm 的短射线，碰到别的网格就是盖住了。
    /// 盖住的不参与吸附——宽松的裤子底下还有大腿的皮肤，手吸到皮肤上就钻进裤子里面了。头发不算遮挡（头发底下的身体照样能摸）。
    /// </summary>
    private bool Covered(int t)
    {
        if (_covered[t] != 0) return _covered[t] == 1;
        Vector3 a = _p[_tri[t * 3]], b = _p[_tri[t * 3 + 1]], c = _p[_tri[t * 3 + 2]];
        var normal = _n[_tri[t * 3]] + _n[_tri[t * 3 + 1]] + _n[_tri[t * 3 + 2]];
        var covered = false;
        if (normal.LengthSquared() > 1e-12f)
        {
            normal = Vector3.Normalize(normal);
            var origin = (a + b + c) / 3f + normal * 0.001f;
            var stamp = ++_coverQuery;
            _coverStamp[t] = stamp;
            (int, int, int)? last = null;
            for (var s = 0f; s <= CoverReach && !covered; s += Cell * 0.5f)
            {
                var key = Key(origin + normal * s);
                if (last == key || !_grid.TryGetValue(key, out var list)) { last = key; continue; }
                last = key;
                foreach (var u in list)
                {
                    if (_coverStamp[u] == stamp) continue;
                    _coverStamp[u] = stamp;
                    if (IsHair(_bone[u])) continue;
                    if (RayTriangle(origin, normal, _p[_tri[u * 3]], _p[_tri[u * 3 + 1]], _p[_tri[u * 3 + 2]]) is { } d && d > 1e-4f && d < CoverReach)
                    {
                        covered = true;
                        break;
                    }
                }
            }
        }
        _covered[t] = covered ? (sbyte)1 : (sbyte)2;
        return covered;
    }

    public static bool IsHair(string bone) => bone.StartsWith("j_kami", StringComparison.Ordinal) || bone.StartsWith("j_ex_h", StringComparison.Ordinal);

    /// <summary>射线和三角形（Möller–Trumbore，不分正反面）：碰到时返回距离。</summary>
    private static float? RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 e1 = b - a, e2 = c - a, h = Vector3.Cross(direction, e2);
        var det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-12f) return null;
        var inv = 1f / det;
        var s = origin - a;
        var u = inv * Vector3.Dot(s, h);
        if (u < 0 || u > 1) return null;
        var q = Vector3.Cross(s, e1);
        var v = inv * Vector3.Dot(direction, q);
        if (v < 0 || u + v > 1) return null;
        return inv * Vector3.Dot(e2, q);
    }

    private (int, int, int) Key(Vector3 v) => ((int)MathF.Floor(v.X / Cell), (int)MathF.Floor(v.Y / Cell), (int)MathF.Floor(v.Z / Cell));

    /// <summary>三角形上离 p 最近的点和它的重心坐标（Ericson《Real-Time Collision Detection》5.1.5）。</summary>
    private static (Vector3 Point, Vector3 Bary) ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return (a, new Vector3(1, 0, 0));
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return (b, new Vector3(0, 1, 0));
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            var v = d1 / (d1 - d3);
            return (a + v * ab, new Vector3(1 - v, v, 0));
        }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return (c, new Vector3(0, 0, 1));
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            var w = d2 / (d2 - d6);
            return (a + w * ac, new Vector3(1 - w, 0, w));
        }
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            var w = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return (b + w * (c - b), new Vector3(0, 1 - w, w));
        }
        var denom = 1f / (va + vb + vc);
        var vv = vb * denom;
        var ww = vc * denom;
        return (a + ab * vv + ac * ww, new Vector3(1 - vv - ww, vv, ww));
    }
}
