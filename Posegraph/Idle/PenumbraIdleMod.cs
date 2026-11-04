using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Posegraph.Idle;

/// <summary>
/// 生成的 idle 存成一个 Penumbra 模组「Posegraph Idle」：每个种族一个多选组，每个「类型 + 槽位」一个选项（同一槽位再转就覆盖）。
/// 用 Penumbra 一直支持的布局（meta.json、default_mod.json、group_*.json）；文件名带内容哈希（Penumbra 按路径缓存动画，换名才会重新读）。
/// 用户之后直接在 Penumbra 里开关、删除选项；没有选项引用的、我们写的文件下次保存时清掉。
/// IPC 名字同 Penumbra API V5（ReAnimate 也是这样调的）。必须在游戏主线程调用。
/// </summary>
public sealed class PenumbraIdleMod
{
    public const string DefaultName = "Posegraph Idle";
    private const string Author = "Posegraph";
    private const string FilesDir = "files";
    private const int MaxOptions = 32;   // Penumbra 多选组最多 32 个选项

    private readonly ICallGateSubscriber<string> _modDirectory;
    private readonly ICallGateSubscriber<string, int> _addMod;
    private readonly ICallGateSubscriber<string, string, int> _reloadMod;
    private readonly ICallGateSubscriber<string, string, string, int> _setModPath;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> _collectionForObject;
    private readonly ICallGateSubscriber<Guid, string, string, bool, int> _trySetMod;
    private readonly ICallGateSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)> _currentSettings;
    private readonly ICallGateSubscriber<Guid, string, string, string, IReadOnlyList<string>, int> _trySetSettings;
    private readonly ICallGateSubscriber<string, string, IReadOnlyDictionary<string, (string[], int)>?> _availableSettings;
    private readonly ICallGateSubscriber<int, int, object?> _redraw;
    private readonly ICallGateSubscriber<Dictionary<string, string>> _modList;
    private readonly IPluginLog _log;

    public PenumbraIdleMod(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _modDirectory = pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
        _addMod = pi.GetIpcSubscriber<string, int>("Penumbra.AddMod.V5");
        _reloadMod = pi.GetIpcSubscriber<string, string, int>("Penumbra.ReloadMod.V5");
        _setModPath = pi.GetIpcSubscriber<string, string, string, int>("Penumbra.SetModPath.V5");
        _collectionForObject = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        _trySetMod = pi.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5");
        _currentSettings = pi.GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>("Penumbra.GetCurrentModSettings.V5");
        _trySetSettings = pi.GetIpcSubscriber<Guid, string, string, string, IReadOnlyList<string>, int>("Penumbra.TrySetModSettings.V5");
        _availableSettings = pi.GetIpcSubscriber<string, string, IReadOnlyDictionary<string, (string[], int)>?>("Penumbra.GetAvailableModSettings.V5");
        _redraw = pi.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5");
        _modList = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
    }

    /// <summary>
    /// 用户起的模组名 → 模组文件夹名。
    /// - reuse：这个窗口刚才存过的文件夹（同一张图调完再存），还在、还是我们的就覆盖它；
    /// - merge：用户选了「同名时加进已有的模组」，同名文件夹是我们建的就往里加；
    /// - 否则名字被占了（文件夹已有，或者 Penumbra 里已有同名模组）就新建「名字 2」「名字 3」…
    /// </summary>
    public string ResolveFolder(string root, string name, string? reuse, bool merge)
    {
        if (reuse is not null && IsOurs(Path.Combine(root, reuse))) return reuse;
        var baseName = SafeFolder(name);
        if (merge && IsOurs(Path.Combine(root, baseName))) return baseName;
        var registered = ModList();
        for (var i = 1; ; i++)
        {
            var folder = i == 1 ? baseName : $"{baseName} {i}";
            if (!Directory.Exists(Path.Combine(root, folder)) && !registered.ContainsKey(folder) && !registered.ContainsValue(folder)) return folder;
        }
    }

    private static bool IsOurs(string dir)
        => File.Exists(Path.Combine(dir, "meta.json")) && Parse(Path.Combine(dir, "meta.json"))?["Author"]?.GetValue<string>() == Author;

    private Dictionary<string, string> ModList()
    {
        try
        {
            return _modList.InvokeFunc() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Penumbra 的模组根目录；没装 / 没加载时为 null。</summary>
    public string? Root
    {
        get
        {
            try
            {
                var dir = _modDirectory.InvokeFunc();
                return string.IsNullOrWhiteSpace(dir) ? null : dir;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>写进一个选项（folder = ResolveFolder 的结果，group = 种族名，option = 槽位名）：files 是 游戏路径 → pap 内容。</summary>
    public void Write(string root, string folder, string group, string option, string description, Dictionary<string, byte[]> files)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(Path.Combine(dir, FilesDir));
        var map = new JsonObject();
        foreach (var (gamePath, bytes) in files)
        {
            var race = gamePath.Split('/') is { Length: > 2 } parts ? parts[2] : "all";
            var name = $"{race}_{Path.GetFileNameWithoutExtension(gamePath)}.{Convert.ToHexString(SHA256.HashData(bytes))[..8].ToLowerInvariant()}.pap";
            File.WriteAllBytes(Path.Combine(dir, FilesDir, name), bytes);
            map[gamePath] = $"{FilesDir}\\{name}";
        }

        var metaPath = Path.Combine(dir, "meta.json");
        if (Parse(metaPath) is { } meta && (meta["FileVersion"]?.GetValue<int>() ?? 0) >= 4)
        {
            // 新布局（Penumbra 1.x 载入过一次就会转成这样，之后不再读 group_*.json）：组在 meta.json 的 Groups 里
            var groupList = meta["Groups"] as JsonArray ?? [];
            meta["Groups"] = groupList;
            var target = groupList.OfType<JsonObject>().FirstOrDefault(g => g["Name"]?.GetValue<string>() == group && g["Type"]?.GetValue<string>() == "Multi");
            if (target is null) groupList.Add(target = NewGroup(group));
            UpsertOption(target, group, option, description, map);
            File.WriteAllText(metaPath, meta.ToJsonString(Indented));
            // 转成新布局后 Penumbra 不再读 group_*.json：以前（误）写进去的删掉，免得它们引用的旧动画文件清不掉
            foreach (var stale in Directory.GetFiles(dir, "group_*.json")) File.Delete(stale);
            SweepOrphans(dir);
            return;
        }

        WriteIfMissing(metaPath, new JsonObject
        {
            ["FileVersion"] = 3,
            ["Name"] = folder,
            ["Author"] = Author,
            ["Description"] = Loc.T("Posegraph 从图片姿势生成的待机动作。每个种族一组，每个槽位一个选项，可以在这里单独开关或删除。"),
            ["Version"] = "1.0",
            ["Website"] = "https://github.com/extrant/Posegraph",
            ["ModTags"] = new JsonArray("Posegraph", "animation"),
        });
        WriteIfMissing(Path.Combine(dir, "default_mod.json"), new JsonObject
        {
            ["Name"] = "",
            ["Priority"] = 0,
            ["Files"] = new JsonObject(),
            ["FileSwaps"] = new JsonObject(),
            ["Manipulations"] = new JsonArray(),
        });

        var groups = Directory.GetFiles(dir, "group_*.json").Order(StringComparer.Ordinal).ToList();
        var path = groups.FirstOrDefault(g => Parse(g)?["Name"]?.GetValue<string>() == group);
        var doc = path is null ? null : Parse(path);
        if (path is null || doc is null)
        {
            path = Path.Combine(dir, $"group_{groups.Count + 1:D3}_{Safe(group)}.json");
            doc = NewGroup(group);
        }
        UpsertOption(doc, group, option, description, map);
        File.WriteAllText(path, doc.ToJsonString(Indented));
        SweepOrphans(dir);
    }

    private static JsonObject NewGroup(string name) => new()
    {
        ["Name"] = name,
        ["Description"] = "",
        ["Priority"] = 0,
        ["Type"] = "Multi",
        ["DefaultSettings"] = 0,
        ["Options"] = new JsonArray(),
    };

    /// <summary>组里同名选项就换文件，没有就加一个。</summary>
    private static void UpsertOption(JsonObject groupDoc, string group, string option, string description, JsonObject map)
    {
        var options = groupDoc["Options"] as JsonArray ?? [];
        groupDoc["Options"] = options;
        var existing = options.OfType<JsonObject>().FirstOrDefault(o => o["Name"]?.GetValue<string>() == option);
        if (existing is not null)
        {
            existing["Files"] = map;
            existing["Description"] = description;
            return;
        }
        if (options.Count >= MaxOptions) throw new InvalidOperationException(Loc.F("「{0}」这一组的选项满了（{1} 个），先在 Penumbra 里删掉一些", group, MaxOptions));
        options.Add(new JsonObject
        {
            ["Name"] = option,
            ["Description"] = description,
            ["Priority"] = 0,
            ["Files"] = map,
            ["FileSwaps"] = new JsonObject(),
            ["Manipulations"] = new JsonArray(),
        });
    }

    /// <summary>注册或重新载入模组，在自己角色所用的合集里启用这个选项，重绘角色。返回提示（失败原因），成功为 null。</summary>
    public string? Publish(string folder, string group, string option)
    {
        // 按 Penumbra 的模组列表判断有没有载入过（GetAvailableModSettings 对没有选项组的模组也会返回空，不可靠）
        if (!ModList().ContainsKey(folder) && !Knows(folder))
        {
            var ec = _addMod.InvokeFunc(folder);
            if (ec != 0 && !ModList().ContainsKey(folder)) return Loc.F("Penumbra 没能载入模组「{0}」（错误 {1}）：换个模组名再试", folder, ec);
            Try(() => _setModPath.InvokeFunc(folder, "", $"Posegraph/{folder}"));
        }
        else
        {
            // Penumbra 自己的载入后回调有时会抛异常，但载入已经完成
            var ec = 0;
            Try(() => ec = _reloadMod.InvokeFunc(folder, ""));
            if (ec != 0) _log.Warning("Penumbra ReloadMod({Mod}) returned {Code}", folder, ec);
        }

        var (valid, _, (collection, collectionName)) = _collectionForObject.InvokeFunc(0);
        if (!valid || collection == Guid.Empty) return Loc.T("模组已经写好，但找不到角色用的合集：请在 Penumbra 里自己启用");
        _trySetMod.InvokeFunc(collection, folder, "", true);
        var (code, data) = _currentSettings.InvokeFunc(collection, folder, "", false);
        var enabled = code == 0 && data is { } d && d.Item3.TryGetValue(group, out var cur) ? cur : [];
        if (!enabled.Contains(option))
        {
            enabled.Add(option);
            _trySetSettings.InvokeFunc(collection, folder, "", group, enabled);
        }
        Try(() => _redraw.InvokeAction(0, 0));
        _log.Information("idle saved to Penumbra mod {Mod}, collection {Collection}: {Group} / {Option}", folder, collectionName, group, option);
        return null;
    }

    private bool Knows(string folder)
    {
        try
        {
            return _availableSettings.InvokeFunc(folder, "") is not null;
        }
        catch
        {
            return false;
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Penumbra IPC call failed");
        }
    }

    // 只清我们写的文件（种族前缀 + 8 位哈希），用户放进来的别的文件不动
    private static void SweepOrphans(string dir)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in Directory.GetFiles(dir, "*.json"))
            if (Parse(json) is { } doc)
                foreach (var files in Descendants(doc).OfType<JsonObject>().Where(o => o.Parent is JsonObject p && p["Files"] == o))
                    foreach (var (_, v) in files)
                        if (v?.GetValue<string>() is { } rel) referenced.Add(rel.Replace('\\', '/'));
        var filesDir = Path.Combine(dir, FilesDir);
        if (!Directory.Exists(filesDir)) return;
        foreach (var file in Directory.GetFiles(filesDir))
        {
            var name = Path.GetFileName(file);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^(all|c\d{4})_.+\.[0-9a-f]{8}\.pap$")) continue;
            if (!referenced.Contains($"{FilesDir}/{name}")) File.Delete(file);
        }
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode node)
    {
        yield return node;
        var children = node switch
        {
            JsonObject o => o.Select(kv => kv.Value),
            JsonArray a => a.AsEnumerable(),
            _ => [],
        };
        foreach (var child in children)
            if (child is not null)
                foreach (var d in Descendants(child)) yield return d;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonObject? Parse(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteIfMissing(string path, JsonObject doc)
    {
        if (!File.Exists(path)) File.WriteAllText(path, doc.ToJsonString(Indented));
    }

    /// <summary>能当文件夹名的模组名（去掉 Windows 文件名不允许的字符）；空的用默认名。</summary>
    public static string SafeFolder(string name)
    {
        var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return safe.Length > 0 ? safe : DefaultName;
    }

    private static string Safe(string name)
        => string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')).Trim('_') is { Length: > 0 } s ? s : "group";
}
