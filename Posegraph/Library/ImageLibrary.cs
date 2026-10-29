using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Posegraph.Library;

/// <summary>图库来源：用户已有的文件夹（不复制文件）。自带图库不在列表里，固定排第一。</summary>
[Serializable]
public sealed class LibrarySource
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>图片的标签与收藏（图片文件本身存不了，记在图库索引里）。</summary>
public sealed class ImageMeta
{
    public List<string> Tags { get; set; } = [];
    public bool Favorite { get; set; }
}

/// <summary>文件内容的 SHA-1 备忘：大小和修改时间没变就不重新计算。</summary>
public sealed record HashMemo(long Size, long Ticks, string Sha1);

/// <summary>图库索引，存在插件配置目录的 library.json。键为小写的完整路径。</summary>
public sealed class LibraryIndex
{
    public int Version { get; set; } = 1;
    public Dictionary<string, ImageMeta> Images { get; set; } = new();
    public Dictionary<string, HashMemo> Hashes { get; set; } = new();
}

public sealed record FolderListing(string Path, string[] Folders, string[] Images);

/// <summary>导入结果：新复制的、内容重复而跳过的（来源文件 → 图库里已有的那张）、超出数量上限跳过的张数。</summary>
public sealed class ImportResult
{
    public List<string> Added { get; } = new();
    public List<(string Source, string Existing)> Duplicates { get; } = new();
    public int OverLimit { get; set; }

    /// <summary>导入后要选中的：新复制的 + 已有的重复图。</summary>
    public IEnumerable<string> Selection => Added.Concat(Duplicates.Select(d => d.Existing)).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>一张图保存的修正：在 3D 页调好后存进识别缓存，下次打开这张图自动恢复。</summary>
/// <summary>图库里一张图存的灯光：方案（含逐盏改过的灯）和整体亮度这些微调。灯的位置相对脸和镜头，换站位、换地图都能用。</summary>
public sealed record SavedLighting(Lighting.LightRig Rig, Lighting.RigTweaks Tweaks, DateTime SavedAt);

public sealed record SavedCorrection(string Preset, Backend.RetargetParams Params, DateTime SavedAt);

/// <summary>
/// 图库：自带图库（文档\Posegraph\Images，导入和剪切板的图片复制到这里）+ 用户添加的文件夹来源。
/// 分类 = 真实子文件夹；标签、收藏存在索引里，经图库重命名 / 移动时跟着走。
/// 识别结果按文件内容（SHA-1）缓存在 文档\Posegraph\Cache，文件改名、移动后仍然命中。
/// </summary>
public sealed class ImageLibrary
{
    public const int MaxImportFiles = 500;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".avif",
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _indexPath;
    private readonly object _lock = new();
    private LibraryIndex _index;
    private bool _dirty;

    public string Root { get; }
    public string CacheDir { get; }

    public ImageLibrary(string configDir)
        : this(configDir, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) { }

    // The public constructor retains the user's Documents layout; checks use an isolated root.
    internal ImageLibrary(string configDir, string docs)
    {
        Root = System.IO.Path.Combine(docs, "Posegraph", "Images");
        CacheDir = System.IO.Path.Combine(docs, "Posegraph", "Cache");
        Directory.CreateDirectory(Root);
        _indexPath = System.IO.Path.Combine(configDir, "library.json");
        try
        {
            _index = File.Exists(_indexPath)
                ? JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(_indexPath), Json) ?? new()
                : new();
        }
        catch
        {
            _index = new();   // 索引坏了不影响使用，只是标签和收藏丢失
        }
    }

    public static bool IsImage(string path) => Extensions.Contains(System.IO.Path.GetExtension(path));

    private static string Key(string path) => System.IO.Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();

    // ------------------------------------------------------------------ 浏览

    /// <summary>列出一个文件夹（不递归）。跳过以 . 开头的文件夹和目录联接。</summary>
    public static FolderListing List(string folder)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        var dirs = Directory.EnumerateDirectories(folder, "*", options)
                            .Where(d => !System.IO.Path.GetFileName(d).StartsWith('.'))
                            .OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var files = Directory.EnumerateFiles(folder, "*", options).Where(IsImage)
                             .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new FolderListing(folder, dirs, files);
    }

    /// <summary>文件夹里（不含子文件夹）有几张图、按名称排第一张是哪张（文件夹图标上的数量和预览）。</summary>
    public static (int Count, string? First) FolderSummary(string folder)
    {
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            var images = Directory.EnumerateFiles(folder, "*", options).Where(IsImage).ToList();
            return (images.Count, images.Min(StringComparer.CurrentCultureIgnoreCase));
        }
        catch
        {
            return (0, null);
        }
    }

    /// <summary>从 folder 起递归搜索：文件名包含关键字，或「#标签」匹配标签。最多 limit 个结果。</summary>
    public string[] Search(string folder, string query, bool favoritesOnly, int limit = 2000)
    {
        query = query.Trim();
        var tag = query.StartsWith('#') ? query[1..] : null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        var result = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            if (!IsImage(file)) continue;
            var meta = Meta(file);
            if (favoritesOnly && meta?.Favorite != true) continue;
            if (query.Length > 0)
            {
                var hit = tag is not null
                    ? meta?.Tags.Any(t => t.Contains(tag, StringComparison.CurrentCultureIgnoreCase)) == true
                    : System.IO.Path.GetFileName(file).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                      || meta?.Tags.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase)) == true;
                if (!hit) continue;
            }
            result.Add(file);
            if (result.Count >= limit) break;
        }
        return result.ToArray();
    }

    // ------------------------------------------------------------------ 标签与收藏

    public ImageMeta? Meta(string path)
    {
        lock (_lock) return _index.Images.GetValueOrDefault(Key(path));
    }

    public void SetFavorite(string path, bool favorite) => Edit(path, m => m.Favorite = favorite);

    public void SetTags(string path, IEnumerable<string> tags)
        => Edit(path, m => m.Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList());

    public string[] AllTags()
    {
        lock (_lock) return _index.Images.Values.SelectMany(m => m.Tags).Distinct().OrderBy(t => t).ToArray();
    }

    private void Edit(string path, Action<ImageMeta> edit)
    {
        lock (_lock)
        {
            var key = Key(path);
            var meta = _index.Images.GetValueOrDefault(key) ?? new ImageMeta();
            edit(meta);
            if (meta.Tags.Count == 0 && !meta.Favorite) _index.Images.Remove(key);
            else _index.Images[key] = meta;
            _dirty = true;
        }
        Save();
    }

    /// <summary>文件 / 文件夹换了路径：把索引里的记录（文件夹则包括其下所有文件）搬到新路径。</summary>
    private void Rekey(string from, string to)
    {
        lock (_lock)
        {
            var src = Key(from);
            var dst = Key(to);
            foreach (var dict in new System.Collections.IDictionary[] { _index.Images, _index.Hashes })
            {
                var keys = dict.Keys.Cast<string>().Where(k => k == src || k.StartsWith(src + "\\")).ToList();
                foreach (var k in keys)
                {
                    var value = dict[k];
                    dict.Remove(k);
                    dict[dst + k[src.Length..]] = value;
                }
            }
            _dirty = true;
        }
    }

    private void Forget(string path)
    {
        lock (_lock)
        {
            var key = Key(path);
            foreach (var dict in new System.Collections.IDictionary[] { _index.Images, _index.Hashes })
                foreach (var k in dict.Keys.Cast<string>().Where(k => k == key || k.StartsWith(key + "\\")).ToList())
                    dict.Remove(k);
            _dirty = true;
        }
    }

    public void Save()
    {
        // Concurrent recognition completions and UI edits share one ordered write.
        // Keep snapshot and atomic replacement under the same lock so an older
        // snapshot cannot overwrite a newer index or race on library.json.tmp.
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                var json = JsonSerializer.Serialize(_index, Json);
                var tmp = _indexPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _indexPath, true);
                _dirty = false;
            }
            catch { _dirty = true; }
        }
    }

    // ------------------------------------------------------------------ 识别缓存

    public static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    /// <summary>文件内容的 SHA-1（大小、修改时间没变就用备忘，不重读文件）。</summary>
    public string Hash(string path)
    {
        var info = new FileInfo(path);
        var key = Key(path);
        lock (_lock)
            if (_index.Hashes.TryGetValue(key, out var memo) && memo.Size == info.Length && memo.Ticks == info.LastWriteTimeUtc.Ticks)
                return memo.Sha1;
        return RememberHash(path, File.ReadAllBytes(path));
    }

    /// <summary>已经读出文件内容时直接算，顺便记下备忘。</summary>
    public string RememberHash(string path, byte[] data)
    {
        var info = new FileInfo(path);
        var sha1 = Sha1(data);
        lock (_lock)
        {
            _index.Hashes[Key(path)] = new HashMemo(info.Length, info.LastWriteTimeUtc.Ticks, sha1);
            _dirty = true;
        }
        return sha1;
    }

    /// <summary>只查备忘，不读文件（画缩略图时判断"已识别"用）；没有备忘返回 null。</summary>
    public string? KnownHash(string path)
    {
        lock (_lock) return _index.Hashes.GetValueOrDefault(Key(path))?.Sha1;
    }

    private string CachePath(string sha1) => System.IO.Path.Combine(CacheDir, sha1 + ".npz");

    private HashSet<string>? _cached;   // 已有缓存的哈希（启动时列一次目录，之后随写入 / 删除更新，画网格时不用每帧查磁盘）

    public bool HasCache(string sha1)
    {
        lock (_lock)
        {
            _cached ??= Directory.Exists(CacheDir)
                ? Directory.EnumerateFiles(CacheDir, "*.npz").Select(f => System.IO.Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return _cached.Contains(sha1);
        }
    }

    public byte[]? ReadCache(string sha1)
    {
        try { return File.Exists(CachePath(sha1)) ? File.ReadAllBytes(CachePath(sha1)) : null; }
        catch { return null; }
    }

    public void WriteCache(string sha1, byte[] archive)
    {
        Directory.CreateDirectory(CacheDir);
        var tmp = $"{CachePath(sha1)}.{Guid.NewGuid():N}.tmp";   // 同一张图可能同时在识别（批量并行、打开图片），各写各的临时文件
        File.WriteAllBytes(tmp, archive);
        try
        {
            File.Move(tmp, CachePath(sha1), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(CachePath(sha1)))
        {
            File.Delete(tmp);   // 另一路刚写好（或正在读）同一张图的缓存，内容相同
        }
        if (HasCache(sha1)) return;
        lock (_lock) _cached!.Add(sha1);
    }

    public void DeleteCache(string sha1)
    {
        try { File.Delete(CachePath(sha1)); } catch { }
        if (!HasCache(sha1)) return;
        lock (_lock) _cached!.Remove(sha1);
    }

    // ------------------------------------------------------------------ 每张图涂的人（合并模式，MaskDocument.Save）

    private string PaintPath(string sha1) => System.IO.Path.Combine(CacheDir, sha1 + ".paint");

    public byte[]? ReadPaint(string sha1)
    {
        try { return File.Exists(PaintPath(sha1)) ? File.ReadAllBytes(PaintPath(sha1)) : null; }
        catch { return null; }
    }

    public void WritePaint(string sha1, byte[] data)
    {
        Directory.CreateDirectory(CacheDir);
        var tmp = $"{PaintPath(sha1)}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, PaintPath(sha1), true);
    }

    public void DeletePaint(string sha1)
    {
        try { File.Delete(PaintPath(sha1)); } catch { }
    }

    // ------------------------------------------------------------------ 每张图保存的修正

    private string FixPath(string sha1) => System.IO.Path.Combine(CacheDir, sha1 + ".fix.json");

    private HashSet<string>? _fixed;   // 有保存修正的哈希（同 _cached，画网格时不查磁盘）

    public bool HasCorrection(string sha1)
    {
        lock (_lock)
        {
            _fixed ??= Directory.Exists(CacheDir)
                ? Directory.EnumerateFiles(CacheDir, "*.fix.json").Select(f => System.IO.Path.GetFileName(f)[..^".fix.json".Length])
                           .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return _fixed.Contains(sha1);
        }
    }

    /// <summary>这张图保存的修正（预设 + 调整参数，含手动关节偏移）；没有返回 null。</summary>
    public SavedCorrection? ReadCorrection(string sha1)
    {
        try
        {
            return File.Exists(FixPath(sha1)) ? JsonSerializer.Deserialize<SavedCorrection>(File.ReadAllText(FixPath(sha1)), Json) : null;
        }
        catch
        {
            return null;
        }
    }

    public void WriteCorrection(string sha1, SavedCorrection correction)
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(FixPath(sha1), JsonSerializer.Serialize(correction, Json));
        if (HasCorrection(sha1)) return;
        lock (_lock) _fixed!.Add(sha1);
    }

    public void DeleteCorrection(string sha1)
    {
        try { File.Delete(FixPath(sha1)); } catch { }
        if (!HasCorrection(sha1)) return;
        lock (_lock) _fixed!.Remove(sha1);
    }

    // ------------------------------------------------------------------ 每张图保存的灯光

    private string LightPath(string sha1) => System.IO.Path.Combine(CacheDir, sha1 + ".light.json");

    private HashSet<string>? _lit;   // 存了灯光的哈希（同 _fixed）

    public bool HasLighting(string sha1)
    {
        lock (_lock)
        {
            _lit ??= Directory.Exists(CacheDir)
                ? Directory.EnumerateFiles(CacheDir, "*.light.json").Select(f => System.IO.Path.GetFileName(f)[..^".light.json".Length])
                           .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return _lit.Contains(sha1);
        }
    }

    /// <summary>这张图存的灯光；没有返回 null。</summary>
    public SavedLighting? ReadLighting(string sha1)
    {
        if (!HasLighting(sha1)) return null;
        try
        {
            return JsonSerializer.Deserialize<SavedLighting>(File.ReadAllText(LightPath(sha1)), Json);
        }
        catch
        {
            return null;
        }
    }

    public void WriteLighting(string sha1, SavedLighting lighting)
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(LightPath(sha1), JsonSerializer.Serialize(lighting, Json));
        if (HasLighting(sha1)) return;
        lock (_lock) _lit!.Add(sha1);
    }

    public void DeleteLighting(string sha1)
    {
        try { File.Delete(LightPath(sha1)); } catch { }
        if (!HasLighting(sha1)) return;
        lock (_lock) _lit!.Remove(sha1);
    }

    // ------------------------------------------------------------------ 辅助视角

    // 同一姿势别的角度的图，挂在主图上（按主图内容哈希），存在 Cache\<哈希>.aux\ 里，不在图库网格里显示。
    // views.json 记用户手动指定的视角（文件名 → left / right / back），没有的按识别结果自动判断。

    private const string AuxLabelsFile = "views.json";

    public string AuxDir(string sha1) => System.IO.Path.Combine(CacheDir, sha1 + ".aux");

    private Dictionary<string, int>? _auxCount;   // 画网格角标用，不每帧查磁盘

    public int AuxCount(string sha1)
    {
        lock (_lock)
        {
            if (_auxCount is null)
            {
                _auxCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(CacheDir))
                    foreach (var dir in Directory.EnumerateDirectories(CacheDir, "*.aux"))
                        _auxCount[System.IO.Path.GetFileNameWithoutExtension(dir)] = Directory.EnumerateFiles(dir).Count(IsImage);
            }
            return _auxCount.GetValueOrDefault(sha1);
        }
    }

    public string[] AuxImages(string sha1)
    {
        var dir = AuxDir(sha1);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(IsImage).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToArray()
            : [];
    }

    /// <summary>某张主图（哈希）的辅助视角图增删了：主窗口正开着这张图时重新融合。</summary>
    public event Action<string>? AuxChanged;

    private void RecountAux(string sha1)
    {
        var count = AuxImages(sha1).Length;
        lock (_lock)
            if (_auxCount is not null) _auxCount[sha1] = count;
        AuxChanged?.Invoke(sha1);
    }

    /// <summary>
    /// 把图片挂为 sha1 那张主图的辅助视角：复制（move 为移动，图库里的图挂上去后就不在网格里了）。
    /// 与主图或已有辅助图内容相同的跳过。返回挂上的新路径。
    /// </summary>
    public List<string> AddAux(string sha1, IEnumerable<string> files, bool move = false)
    {
        var dir = AuxDir(sha1);
        Directory.CreateDirectory(dir);
        var known = AuxImages(sha1).Select(Hash).Append(sha1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var file in files.Where(f => File.Exists(f) && IsImage(f)))
        {
            var bytes = File.ReadAllBytes(file);
            if (!known.Add(Sha1(bytes))) continue;
            var target = Unique(System.IO.Path.Combine(dir, System.IO.Path.GetFileName(file)));
            if (move)
            {
                File.Move(file, target);
                Forget(file);
            }
            else File.WriteAllBytes(target, bytes);
            RememberHash(target, bytes);
            added.Add(target);
        }
        RecountAux(sha1);
        Save();
        return added;
    }

    /// <summary>剪切板图片挂为辅助视角；与主图或已有辅助图相同时返回 null。</summary>
    public string? AddAuxPng(string sha1, byte[] png)
    {
        if (sha1.Equals(Sha1(png), StringComparison.OrdinalIgnoreCase) || AuxImages(sha1).Any(f => Hash(f).Equals(Sha1(png), StringComparison.OrdinalIgnoreCase)))
            return null;
        Directory.CreateDirectory(AuxDir(sha1));
        var path = Unique(System.IO.Path.Combine(AuxDir(sha1), $"{Loc.T("剪切板")} {DateTime.Now:yyyyMMdd-HHmmss}.png"));
        File.WriteAllBytes(path, png);
        RememberHash(path, png);
        RecountAux(sha1);
        Save();
        return path;
    }

    /// <summary>去掉一张辅助视角图（进回收站）。</summary>
    public void RemoveAux(string sha1, string path)
    {
        RecycleBin.Send([path]);
        if (!File.Exists(path)) Forget(path);
        SetAuxLabel(sha1, path, null);
        RecountAux(sha1);
        Save();
    }

    private Dictionary<string, string> ReadAuxLabels(string sha1)
    {
        try
        {
            var file = System.IO.Path.Combine(AuxDir(sha1), AuxLabelsFile);
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file), Json) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>用户手动指定的视角（left / right / back），没指定返回 null。</summary>
    public string? AuxLabel(string sha1, string path) => ReadAuxLabels(sha1).GetValueOrDefault(System.IO.Path.GetFileName(path));

    public void SetAuxLabel(string sha1, string path, string? label)
    {
        var labels = ReadAuxLabels(sha1);
        var name = System.IO.Path.GetFileName(path);
        if (label is null && !labels.Remove(name)) return;
        if (label is not null) labels[name] = label;
        if (!Directory.Exists(AuxDir(sha1))) return;
        File.WriteAllText(System.IO.Path.Combine(AuxDir(sha1), AuxLabelsFile), JsonSerializer.Serialize(labels, Json));
    }

    /// <summary>
    /// 文件名约定：「xxx-1.png」「xxx-2.jpg」这样的图，同一文件夹里有「xxx.任意扩展名」时自动挂为它的辅助视角（移出图库网格）。
    /// 返回挂上的张数。
    /// </summary>
    public int AttachNumbered(IEnumerable<string> paths)
    {
        var attached = 0;
        foreach (var path in paths.Where(File.Exists))
        {
            var m = System.Text.RegularExpressions.Regex.Match(System.IO.Path.GetFileNameWithoutExtension(path), @"^(.+)-\d{1,2}$");
            if (!m.Success) continue;
            var dir = System.IO.Path.GetDirectoryName(path)!;
            var main = Directory.EnumerateFiles(dir, m.Groups[1].Value + ".*").FirstOrDefault(IsImage);
            if (main is null) continue;
            attached += AddAux(Hash(main), [path], move: true).Count;
        }
        return attached;
    }

    // ------------------------------------------------------------------ 文件操作

    /// <summary>文件名里不能有的字符、首尾空格、点结尾都视为非法。</summary>
    public static bool ValidName(string name)
        => name.Trim().Length > 0 && name == name.Trim() && !name.EndsWith('.') && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;

    /// <summary>目标已存在时加 (2)、(3)…</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>重命名文件或文件夹（newName 对文件不含扩展名，扩展名保持不变）。返回新路径。</summary>
    public string Rename(string path, string newName)
    {
        if (!ValidName(newName)) throw new ArgumentException(Loc.T("名称不合法"));
        var dir = System.IO.Path.GetDirectoryName(path)!;
        string target;
        if (Directory.Exists(path))
        {
            target = System.IO.Path.Combine(dir, newName);
            if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
                throw new IOException(Loc.T("同名文件夹已存在"));
            Directory.Move(path, target);
        }
        else
        {
            target = System.IO.Path.Combine(dir, newName + System.IO.Path.GetExtension(path));
            if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                throw new IOException(Loc.T("同名文件已存在"));
            File.Move(path, target);
        }
        Rekey(path, target);
        Save();
        return target;
    }

    /// <summary>把文件 / 文件夹移动到 folder 里（重名自动加编号）。返回失败的条目与原因。</summary>
    public List<string> Move(IEnumerable<string> paths, string folder)
    {
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (string.Equals(System.IO.Path.GetDirectoryName(path), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(path) && Key(folder).StartsWith(Key(path)))
                    throw new IOException(Loc.T("不能移动到自己的子文件夹里"));
                var target = Unique(System.IO.Path.Combine(folder, System.IO.Path.GetFileName(path)));
                if (Directory.Exists(path)) Directory.Move(path, target);
                else File.Move(path, target);
                Rekey(path, target);
            }
            catch (Exception ex)
            {
                errors.Add($"{System.IO.Path.GetFileName(path)}：{ex.Message}");
            }
        }
        Save();
        return errors;
    }

    public string NewFolder(string parent, string name)
    {
        if (!ValidName(name)) throw new ArgumentException(Loc.T("名称不合法"));
        var path = Unique(System.IO.Path.Combine(parent, name));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>删除到回收站（可以找回）。</summary>
    public void Delete(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        RecycleBin.Send(paths);
        foreach (var path in paths)
            if (!File.Exists(path) && !Directory.Exists(path)) Forget(path);
        Save();
    }

    /// <summary>
    /// 复制进图库的 folder：文件直接复制；文件夹按原结构复制其中的图片（放进同名子文件夹）。
    /// 总数最多 MaxImportFiles 张，超出的跳过并通过 skipped 返回。返回复制后的新路径。
    /// </summary>
    /// <summary>来源 root 下所有图片的 内容哈希 → 路径（查重用；哈希有备忘，只有新文件或改过的文件才读盘）。</summary>
    public Dictionary<string, string> ContentIndex(string root)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return index;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        foreach (var file in Directory.EnumerateFiles(root, "*", options).Where(IsImage))
        {
            if (file[(root.TrimEnd('\\').Length + 1)..].Split('\\').Any(p => p.StartsWith('.'))) continue;
            try { index.TryAdd(Hash(file), file); }
            catch (IOException) { }
        }
        Save();
        return index;
    }

    /// <summary>
    /// 复制进图库的 folder：文件直接复制；文件夹按原结构复制其中的图片（放进同名子文件夹）。
    /// 按内容查重：来源 root 里已有同样的图（不管叫什么、在哪个子文件夹）就不再复制，记进 Duplicates。
    /// 总数最多 MaxImportFiles 张，超出的跳过。
    /// </summary>
    public ImportResult Import(IEnumerable<string> paths, string folder, string root)
    {
        var result = new ImportResult();
        var known = ContentIndex(root);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

        void CopyOne(string file, string target)
        {
            if (result.Added.Count >= MaxImportFiles)
            {
                result.OverLimit++;
                return;
            }
            var bytes = File.ReadAllBytes(file);
            var sha1 = Sha1(bytes);
            if (known.TryGetValue(sha1, out var existing))
            {
                result.Duplicates.Add((file, existing));
                return;
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            target = Unique(target);
            File.WriteAllBytes(target, bytes);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            RememberHash(target, bytes);
            known[sha1] = target;
            result.Added.Add(target);
        }

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var baseDir = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(path.TrimEnd('\\')));
                foreach (var file in Directory.EnumerateFiles(path, "*", options).Where(IsImage))
                    CopyOne(file, System.IO.Path.Combine(baseDir, System.IO.Path.GetRelativePath(path, file)));
            }
            else if (File.Exists(path) && IsImage(path))
                CopyOne(path, System.IO.Path.Combine(folder, System.IO.Path.GetFileName(path)));
        }
        Save();
        return result;
    }

    /// <summary>剪切板图片存成 PNG；来源 root 里已有同样内容的图就不再存，返回已有的那张（duplicate = true）。</summary>
    public string SaveClipboard(byte[] png, string folder, string root, out bool duplicate)
    {
        if (ContentIndex(root).TryGetValue(Sha1(png), out var existing))
        {
            duplicate = true;
            return existing;
        }
        duplicate = false;
        var path = Unique(System.IO.Path.Combine(folder, $"{Loc.T("剪切板")} {DateTime.Now:yyyyMMdd-HHmmss}.png"));
        File.WriteAllBytes(path, png);
        RememberHash(path, png);
        Save();
        return path;
    }

    /// <summary>路径属于哪个来源的根目录（自带图库或某个来源），用于面包屑和"返回上级"的边界。</summary>
    public string? SourceRootOf(string path, IEnumerable<LibrarySource> sources)
    {
        var key = Key(path);
        return new[] { Root }.Concat(sources.Where(s => s.Enabled).Select(s => s.Path))
                             .Where(r => r.Length > 0)
                             .OrderByDescending(r => r.Length)
                             .FirstOrDefault(r => key == Key(r) || key.StartsWith(Key(r) + "\\"));
    }
}

/// <summary>用 Shell 的 SHFileOperation 把文件放进回收站（.NET 没有直接的接口）。</summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_SILENT = 0x4, FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public static void Send(IEnumerable<string> paths)
    {
        // pFrom：多个路径以 \0 分隔，整体以两个 \0 结尾
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths.Select(System.IO.Path.GetFullPath)) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var code = SHFileOperation(ref op);
        if (code != 0) throw new IOException(Loc.F("删除失败（错误码 {0}）", code));
    }
}
