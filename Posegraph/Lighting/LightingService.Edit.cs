namespace Posegraph.Lighting;

/// <summary>
/// 编辑灯：加灯（LightRigs.Presets 里的一种）、删灯、单盏 / 整套恢复成套用方案时的样子，以及撤销 / 重做。
/// 加删改都作用在正在调的那一套（Lights：选了某一套就是那一套自己的，否则是所有套共用的）。
/// 撤销记录的是整份灯光状态（方案、共用的灯、单独调过的各套、整体微调、关掉的灯）；界面在每次改动开始前调 Checkpoint。
/// 退出 GPose 时清空记录（灯和人都没了）。
/// </summary>
public sealed partial class LightingService
{
    private const int MaxHistory = 60;
    private const double MergeSeconds = 0.8;   // 同一种连续改动（滚轮改距离）在这么久以内算一步

    private List<LightSpec> _original = [];                   // 套用方案时的灯（已编号）：「整套恢复原样」回到这里
    private Dictionary<string, LightSpec> _base = new();      // 每盏灯原来的样子（按 Id）：方案里的、加灯时的
    private readonly Stack<State> _undo = new();
    private readonly Stack<State> _redo = new();
    private string? _mergeKey;
    private DateTime _mergeAt;

    /// <summary>撤销用的一份灯光状态（都是拷贝）。独显不算在内——它只是看效果用的。</summary>
    private sealed record State(LightRig? Active, List<LightSpec> Shared, Dictionary<string, List<LightSpec>> Overrides, RigTweaks Tweaks,
                                int[] Muted, int? SelectedGroup, List<LightSpec> Original, Dictionary<string, LightSpec> Base);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>新灯的编号：随机的短串（存过的方案、图库里的灯带着旧编号，不能从 1 数起，免得撞上）。</summary>
    private static string NewId() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>给没编号的灯编号（套用方案、照片配色加的灯），新编号的灯记下原来的样子。</summary>
    private List<LightSpec> WithIds(IEnumerable<LightSpec> lights)
    {
        var result = new List<LightSpec>();
        foreach (var l in lights)
        {
            if (l.Id is not null)
            {
                result.Add(l);
                continue;
            }
            var spec = l with { Id = NewId() };
            _base[spec.Id!] = spec;
            result.Add(spec);
        }
        return result;
    }

    /// <summary>新套了方案：记下现在的灯是「原样」。</summary>
    private void RememberOriginal()
    {
        _original = [.. _shared];
        _base = _shared.ToDictionary(l => l.Id!, l => l);
    }

    /// <summary>
    /// 加一盏灯到正在调的那一套，选中它。还没打光时（Active 为 null）从空白开始：读主窗口的角色，只有这一盏灯。
    /// 返回失败原因，成功为 null。
    /// </summary>
    public async Task<string?> AddLightAsync(LightPreset preset, int? actor)
    {
        var name = preset.Spec.Name;   // 存中文原名（界面显示时翻译，见 LightRigs.DisplayName；补光倍数也按名字认）
        if (Active is null)
        {
            var rig = new LightRig(LightRigs.ManualRigId, "自定义", Loc.T("自己加的灯"), [preset.Spec]);
            var error = await ApplyAsync(rig, actor);
            if (error is null) SelectedLight = 0;
            return error;
        }
        var taken = Lights.Select(l => l.Name).ToHashSet();
        var unique = name;
        for (var n = 2; taken.Contains(unique); n++) unique = $"{name} {n}";
        var spec = preset.Spec with { Name = unique, Id = NewId() };
        _base[spec.Id!] = spec;
        Lights.Add(spec);
        SelectedLight = Lights.Count - 1;
        Refresh();
        return null;
    }

    /// <summary>删掉正在调的那一套里的第 i 盏灯（关掉、独显的编号跟着往前挪）。</summary>
    public void RemoveLight(int i)
    {
        if (i < 0 || i >= Lights.Count) return;
        Lights.RemoveAt(i);
        var muted = Muted.Where(m => m != i).Select(m => m > i ? m - 1 : m).ToList();
        Muted.Clear();
        Muted.UnionWith(muted);
        Solo = Solo == i ? null : Solo > i ? Solo - 1 : Solo;
        SelectedLight = null;
        Refresh();
    }

    /// <summary>第 i 盏灯和它原来的样子不一样（改过参数）。</summary>
    public bool IsModified(int i)
        => i >= 0 && i < Lights.Count && Lights[i].Id is { } id && _base.TryGetValue(id, out var spec) && spec != Lights[i];

    /// <summary>第 i 盏灯恢复成原来的样子（方案里的、或刚加进来时的）。</summary>
    public void ResetLight(int i)
    {
        if (i < 0 || i >= Lights.Count || Lights[i].Id is not { } id || !_base.TryGetValue(id, out var spec)) return;
        Lights[i] = spec;
        Refresh();
    }

    /// <summary>现在的灯是从空白开始一盏盏加的（还没存成方案）：没有「原样」可回。</summary>
    public bool IsManual => Active?.Id == LightRigs.ManualRigId;

    /// <summary>灯和刚套方案时不一样（改过参数、加删过灯、单独调过某一套、关掉过灯）。从空白开始加的灯没有原样，总是 false。</summary>
    public bool IsRigModified => Active is not null && !IsManual
                                 && (_overrides.Count > 0 && _overrides.Values.Any(o => !o.SequenceEqual(_original))
                                     || !_shared.SequenceEqual(_original) || Muted.Count > 0);

    /// <summary>整套恢复成刚套方案时的样子：逐盏的改动、加删的灯、单独调过的套、关掉的灯都去掉（整体亮度这些微调不动）。</summary>
    public void ResetRig()
    {
        if (Active is null) return;
        _shared = [.. _original];
        _overrides.Clear();
        if (EditKey is { } key) _overrides[key] = [.. _shared];   // 还选着某一套就接着单独调，从原样开始
        Muted.Clear();
        Solo = null;
        SelectedLight = null;
        Refresh();
    }

    // ------------------------------------------------------------------ 撤销 / 重做

    /// <summary>
    /// 记下改动之前的样子（界面在改动开始时调：拖滑杆、拖灯按下的那一下，点按钮之前）。同一帧只记一次。
    /// merge：同一种连续改动（比如滚轮改距离）在 MergeSeconds 内只记第一下。
    /// </summary>
    public void Checkpoint(string? merge = null)
    {
        var now = DateTime.UtcNow;
        if (merge is not null && merge == _mergeKey && (now - _mergeAt).TotalSeconds < MergeSeconds)
        {
            _mergeAt = now;
            return;
        }
        var frame = Dalamud.Bindings.ImGui.ImGui.GetFrameCount();
        if (frame == _checkpointFrame) return;
        _checkpointFrame = frame;
        (_mergeKey, _mergeAt) = (merge, now);
        Push(_undo, Capture());
        _redo.Clear();
    }

    private int _checkpointFrame = -1;

    public void Undo()
    {
        if (_undo.Count == 0) return;
        Push(_redo, Capture());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        Push(_undo, Capture());
        Restore(_redo.Pop());
    }

    private void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _mergeKey = null;
    }

    private static void Push(Stack<State> stack, State state)
    {
        stack.Push(state);
        if (stack.Count <= MaxHistory) return;
        var keep = stack.Take(MaxHistory).Reverse().ToList();   // 丢掉最老的
        stack.Clear();
        foreach (var s in keep) stack.Push(s);
    }

    private State Capture() => new(Active, [.. _shared], _overrides.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()), Tweaks,
                                   [.. Muted], SelectedGroup, [.. _original], new Dictionary<string, LightSpec>(_base));

    private void Restore(State s)
    {
        _mergeKey = null;
        if (s.Active is null)
        {
            Clear();
            Tweaks = s.Tweaks;
            return;
        }
        Active = s.Active;
        _shared = [.. s.Shared];
        _overrides.Clear();
        foreach (var (key, lights) in s.Overrides) _overrides[key] = [.. lights];
        Tweaks = s.Tweaks;
        Muted.Clear();
        Muted.UnionWith(s.Muted);
        SelectedGroup = s.SelectedGroup is { } g && g < GroupKeys.Count ? g : null;
        _original = [.. s.Original];
        _base = new Dictionary<string, LightSpec>(s.Base);
        if (Solo >= Lights.Count) Solo = null;
        if (SelectedLight >= Lights.Count) SelectedLight = null;
        Refresh();
    }
}
