using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Posegraph.Backend;

namespace Posegraph.UI;

/// <summary>
/// 合并模式：一张图里有多个人。先在人物遮罩窗口里给每个人涂上大致范围（每人一个图层，MaskDocument）：
/// 看得见的部分涂「看得见」，被别人或东西挡住的身体涂「被挡住的范围」。涂过的范围就是人物框，看得见的部分作为遮罩，
/// 一起交给识别模型识别成一个多人会话；每个人指定一个角色后「全部应用」。
/// 也可以「自动初稿」：找人 → 按框识别 → 识别结果投影成涂抹，再改。涂抹按图片存在图库里，重新打开这张图时恢复，
/// 识别过的直接读识别缓存。每个人有自己的调整值：主窗口切到第 N 人时编辑的是这个人的值，并切到这个人对应的角色。
/// 列表在角色列表窗口的「合并」页。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// 图里的一个人（涂抹的第 k 个图层）。Human：识别后在会话里的序号（没识别或这次没勾选为 null）。
    /// Actor：对应的角色（ObjectIndex，本次 GPose 有效）。Params：这个人自己的调整值（编辑过才有）。
    /// </summary>
    public sealed class MergePerson
    {
        public bool Checked { get; set; } = true;
        public int? Actor { get; set; }
        public int? Human { get; set; }
        public RetargetParams? Params { get; set; }
        public (string Text, Vector4 Color)? Status { get; set; }
    }

    /// <summary>一张图上涂的人：Doc 的第 k 个图层是 People[k]。</summary>
    private sealed record PaintState(string Path, string Sha1, MaskDocument Doc, List<MergePerson> People);

    // 编号颜色：图层、框、列表里的编号用同一种颜色
    private static readonly Vector4[] PersonColors =
    [
        new(0.30f, 0.80f, 1.00f, 1), new(1.00f, 0.55f, 0.25f, 1), new(0.55f, 0.90f, 0.35f, 1), new(0.95f, 0.40f, 0.70f, 1),
        new(1.00f, 0.85f, 0.25f, 1), new(0.65f, 0.55f, 1.00f, 1), new(0.35f, 0.95f, 0.80f, 1), new(1.00f, 0.45f, 0.45f, 1),
    ];

    public static Vector4 PersonColor(int index) => PersonColors[index % PersonColors.Length];

    private volatile PaintState? _paint;
    private readonly ConcurrentQueue<Action> _paintActions = new();   // 后台算好、要在绘制线程改涂抹的操作
    private int _recognizedVersion = -1;   // 上次识别时涂抹的版本（之后改过就提示重新识别）
    private string? _sessionKey;           // 当前会话的识别缓存键：普通会话是图片哈希，合并会话是图片 + 框 + 遮罩
    private volatile bool _mergeSession;   // 当前会话是合并模式的多人会话
    private RetargetParams? _mergeBase;    // 合并会话里每个人的初始调整值（这张图保存的修正或预设）
    private volatile bool _drafting, _loadingPaint, _mergeApplying;
    private MergePerson? _anchor;          // 复刻站位的基准人物（它的角色不动），没指定时取第一个能应用的人

    /// <summary>角色列表开着且在「合并」页（由 Plugin 接上）：开着时图像页显示每个人的范围。</summary>
    public Func<bool>? MergeListOpen { get; set; }

    /// <summary>打开角色列表的「合并」页（由 Plugin 接上）：图里像是有 2 个以上的人时自动打开。</summary>
    public Action? ShowPeopleList { get; set; }

    /// <summary>人物遮罩（涂人）窗口（由 Plugin 接上）。</summary>
    public MaskEditorWindow? MaskEditor { get; set; }

    /// <summary>当前图上涂的人（只在绘制线程调用；没涂过为 null）。</summary>
    public MaskDocument? Paint
    {
        get
        {
            while (_paintActions.TryDequeue(out var action)) action();
            return _paint is { } p && p.Path == _imagePath ? p.Doc : null;
        }
    }

    /// <summary>当前图里的人（只在绘制线程调用）。</summary>
    public IReadOnlyList<MergePerson> People => Paint is not null ? _paint!.People : [];

    /// <summary>后端支持合并模式（新版 /health 带 detectors 字段；旧后端会忽略人物框，把整张图当一个人）。</summary>
    public bool MergeSupported => _health?.Detectors is not null;

    /// <summary>人物检测组件能用（onnxruntime 与模型都在）：能「自动初稿」。</summary>
    public bool DetectorsReady => _health?.Detectors?.Values.Any(v => v) == true;

    public bool Drafting => _drafting;
    public bool LoadingPaint => _loadingPaint;

    /// <summary>当前会话的原图尺寸（宽、高）。</summary>
    public int[]? SessionImageSize => _session?.ImageSize;
    public bool MergeApplying => _mergeApplying;
    public bool MergeSessionActive => _mergeSession;
    public bool Recognizing => _recognizing;

    /// <summary>能应用的人：勾选、识别过、选了角色。</summary>
    private static bool Ready(MergePerson p) => p.Checked && p.Human is not null && p.Actor is not null;

    /// <summary>复刻站位的基准人物：指定的人还能应用就用它，否则第一个能应用的人。</summary>
    public MergePerson? Anchor
    {
        get => _anchor is { } a && People.Contains(a) && Ready(a) ? a : People.FirstOrDefault(Ready);
        set => _anchor = value;
    }

    /// <summary>识别以后又改过涂抹（或勾选了还没识别的人）：要重新识别才生效。</summary>
    public bool PeopleChanged => _mergeSession && Paint is { } doc
                                 && (doc.Version != _recognizedVersion || People.Any(p => p.Checked && p.Human is null));

    private bool MergeEditing => MergeListOpen?.Invoke() == true && _session is not null && _imagePath is not null;

    private MergePerson? PersonOfHuman(int human) => People.FirstOrDefault(p => p.Human == human);

    /// <summary>3D 页洋葱皮要一起算的其他人（会话里的序号和各自的调整值）；不是合并会话或没开为空。可在后台线程调用。</summary>
    private (int Person, RetargetParams Params)[] OnionInputs()
    {
        if (!_mergeSession || !_config.ShowOnion || _paint is not { } paint) return [];
        return paint.People.ToArray()
                    .Where(p => p.Human is { } h && h != _person)
                    .Select(p => (p.Human!.Value, (p.Params ?? _mergeBase ?? _params).Clone()))
                    .ToArray();
    }

    /// <summary>会话里的第 human 个人在列表里的编号（从 1 开始）；不是合并会话时为 human + 1。</summary>
    private int PersonNumber(int human)
    {
        if (!_mergeSession) return human + 1;
        var people = People;
        for (var i = 0; i < people.Count; i++)
            if (people[i].Human == human) return i + 1;
        return human + 1;
    }

    // ------------------------------------------------------------------ 涂抹：开始、加减人、存取

    /// <summary>
    /// 打开图片识别完后（后台线程）：这张图涂过人就读回来，合并页开着时直接从识别缓存恢复多人结果（不跑模型）；
    /// 没涂过、装了检测组件时顺带找人（一两百毫秒），像是有 2 个以上的人就打开合并页提示。
    /// </summary>
    private async Task LoadPaintAsync(string path, string sha1, SessionInfo info)
    {
        SavePaint();
        var previous = _paint;
        _paint = null;
        _recognizedVersion = -1;
        try
        {
            if (Library.ReadPaint(sha1) is { } saved)
            {
                var photo = await _client.MasksAsync(info.SessionId, render: false);
                if (MaskDocument.Load(saved, photo.Width, photo.Height, photo.Image) is { } doc)
                {
                    // 重新打开同一张图：沿用上次各人选的角色、调整值
                    var people = previous is not null && previous.Sha1 == sha1 && previous.People.Count == doc.Count
                        ? previous.People
                        : Enumerable.Range(0, doc.Count).Select(_ => new MergePerson()).ToList();
                    _paint = new PaintState(path, sha1, doc, people);
                    if (MergeListOpen?.Invoke() == true && doc.Count > 0) _paintActions.Enqueue(() => RecognizePeople(cacheOnly: true));
                    return;
                }
            }
            if (!DetectorsReady) return;
            var r = await _client.DetectAsync(await File.ReadAllBytesAsync(path), _config.MergeDetector);
            if (r.People.Length < 2 || path != _imagePath) return;
            ShowPeopleList?.Invoke();
            SetStatus(Loc.F("图里好像有 {0} 个人：在角色列表的「合并」页涂人，或者「自动初稿」", r.People.Length), Green);
        }
        catch (Exception ex)
        {
            Recognition.Log?.Invoke($"[合并] 读取涂抹 / 找人失败：{path}", ex);   // 只影响合并模式的提示，不打扰单人流程
        }
    }

    /// <summary>打开涂人窗口：这张图还没涂过就从两个空图层开始（照片从后端取，和识别用的同一张、方向已摆正）。</summary>
    public void StartPainting()
    {
        if (MaskEditor is not { } editor || _imagePath is not { } path || _imageSha1 is not { } sha1 || _session is not { } session) return;
        if (Paint is not null)
        {
            editor.IsOpen = true;
            return;
        }
        if (_loadingPaint) return;
        _loadingPaint = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var photo = await WithSessionAsync(sid => _client.MasksAsync(sid, render: false), session);
                _paint = new PaintState(path, sha1, new MaskDocument(photo.Width, photo.Height, photo.Image, 2),
                                        [new MergePerson(), new MergePerson()]);
                editor.IsOpen = true;
                SetStatus(Loc.T("给每个人涂上大致范围（不用很准，看得见的手脚要涂到），被挡住的身体用「被挡住」画笔涂，然后点「识别」"), Green);
            }
            catch (Exception ex)
            {
                SetError(Loc.F("打开涂人窗口失败：{0}", ex.Message));
            }
            finally
            {
                _loadingPaint = false;
            }
        });
    }

    /// <summary>加一个人（空图层）。</summary>
    public void AddPerson()
    {
        if (Paint is not { } doc || _recognizing || doc.AddLayer() < 0) return;
        _paint!.People.Add(new MergePerson());
    }

    /// <summary>删掉第 k 个人（它的涂抹一起删，后面的人编号往前挪）。</summary>
    public void RemovePersonAt(int k)
    {
        if (Paint is not { } doc || _recognizing || _mergeApplying || k < 0 || k >= doc.Count) return;
        doc.RemoveLayer(k);
        _paint!.People.RemoveAt(k);
        SavePaint();
    }

    /// <summary>把这张图上涂的人存进图库（关窗、识别、换图时）。</summary>
    public void SavePaint()
    {
        if (_paint is not { } p) return;
        try
        {
            if (p.Doc.Count == 0) Library.DeletePaint(p.Sha1);
            else Library.WritePaint(p.Sha1, p.Doc.Save());
        }
        catch
        {
            // 存不进去不影响这次使用
        }
    }

    // ------------------------------------------------------------------ 识别

    /// <summary>
    /// 按涂抹识别勾选的人（一个多人会话）：每个人涂过的范围（看得见 + 被挡住）当人物框，看得见的部分当遮罩。
    /// cacheOnly：只读识别缓存，没有缓存就什么都不做（重新打开图时恢复上次的结果）。
    /// </summary>
    public void RecognizePeople(bool cacheOnly = false)
    {
        if (Paint is not { } doc || _session is not { } session || _recognizing || !BackendOnline) return;
        var chosen = Enumerable.Range(0, doc.Count).Where(k => _paint!.People[k].Checked && doc.Box(k, session.ImageSize) is not null).ToList();
        if (chosen.Count == 0)
        {
            if (!cacheOnly) SetError(Loc.T("还没有涂人：先在涂人窗口里给每个人涂上大致范围"));
            return;
        }
        var boxes = chosen.Select(k => doc.Box(k, session.ImageSize)!).ToList();
        Recognize(_paint!, chosen, boxes, doc.ToData(chosen), doc.Version, cacheOnly);
    }

    private void Recognize(PaintState paint, List<int> chosen, List<float[]> boxes, MaskData? masks, int version, bool cacheOnly)
    {
        var path = paint.Path;
        StorePersonParams();
        _recognizing = true;
        if (!cacheOnly)
            SetStatus(ModelReady ? Loc.F("识别 {0} 个人…", chosen.Count) : Loc.F("加载模型并识别 {0} 个人…", chosen.Count), Yellow);
        _ = Task.Run(async () =>
        {
            try
            {
                await using var batch = Recognition.Batch();
                if (await Recognition.OpenPeopleAsync(path, BackendClient.FormatBoxes(boxes), masks, cacheOnly) is not { } opened)
                    return;   // 只读缓存、没有缓存
                var (info, key, sha1) = opened;
                AdoptMergeSession(paint, chosen, info, key, sha1);
                _recognizedVersion = version;
                SavePaint();
                SetStatus(info.Device is null
                    ? Loc.F("已从识别缓存读取 {0} 个人", chosen.Count)
                    : Loc.F("识别了 {0} 个人（{1} {2:0.0} 秒）：给每个人选好角色后「全部应用」", chosen.Count, DeviceLabel(info.Device), info.InferenceMs / 1000f),
                    Green);
            }
            catch (Exception ex)
            {
                SetError(Loc.F("识别失败：{0}", ex.Message));
            }
            finally
            {
                _recognizing = false;
            }
        });
    }

    /// <summary>识别出的多人会话换上：第 i 个勾选的图层是会话里的第 i 个人。</summary>
    private void AdoptMergeSession(PaintState paint, List<int> chosen, SessionInfo info, string key, string sha1)
    {
        var old = _session;
        foreach (var p in paint.People) p.Human = null;
        for (var i = 0; i < chosen.Count; i++)
        {
            paint.People[chosen[i]].Human = i;
            paint.People[chosen[i]].Status = null;
        }
        var first = paint.People[chosen[0]];
        var fix = Library.ReadCorrection(sha1);
        if (fix is not null) _preset = fix.Preset;
        _mergeBase = fix?.Params.Clone() ?? LoadParams(_preset);
        _session = info;
        _sessionKey = key;
        _imageSha1 = sha1;
        _mergeSession = true;
        _auxViews = [];
        _person = 0;
        _params = (first.Params ?? _mergeBase).Clone();
        _savedFix = fix is null ? null : FullSnapshot(fix.Preset, fix.Params);
        _selected = null;
        _result = null;
        if (first.Actor is { } actor && actor != _actor) SelectActor(actor);
        if (old is not null && old.SessionId != info.SessionId) _ = _client.DeleteSessionAsync(old.SessionId).ContinueWith(_ => { });
        RequestRetarget(false);
    }

    /// <summary>
    /// 自动初稿：找人 → 按检测框识别 → 识别结果投影成涂抹（替换现在涂的），打开涂人窗口接着改。
    /// 投影出来的是模型以为的样子：错的地方（椅子、袖子是谁的、被挡住的腿）改好后再「识别」。
    /// </summary>
    public void AutoDraft()
    {
        if (_imagePath is not { } path || _imageSha1 is not { } sha1 || _drafting || _recognizing || !BackendOnline || !DetectorsReady) return;
        _drafting = true;
        _recognizing = true;
        SetStatus(Loc.T("正在找图里的人…"), Yellow);
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await _client.DetectAsync(await File.ReadAllBytesAsync(path), _config.MergeDetector);
                if (r.People.Length == 0)
                {
                    SetStatus(Loc.T("没找到人：请在涂人窗口里自己涂"), Yellow);
                    return;
                }
                var people = r.People.Take(MaskDocument.MaxPeople).ToList();
                SetStatus(ModelReady ? Loc.F("识别 {0} 个人…", people.Count) : Loc.F("加载模型并识别 {0} 个人…", people.Count), Yellow);
                await using var batch = Recognition.Batch();
                var (info, key, _) = await Recognition.OpenPeopleAsync(path, BackendClient.FormatBoxes(people.Select(p => p.Bbox)));
                var masks = await _client.MasksAsync(info.SessionId);
                var paint = new PaintState(path, sha1, new MaskDocument(masks), people.Select(_ => new MergePerson()).ToList());
                SavePaint();
                _paint = paint;
                var chosen = Enumerable.Range(0, people.Count).ToList();
                AdoptMergeSession(paint, chosen, info, key, sha1);
                _recognizedVersion = paint.Doc.Version;
                SavePaint();
                if (MaskEditor is { } editor) editor.IsOpen = true;
                SetStatus(Loc.F("自动初稿：找到 {0} 个人并识别好了。涂抹是识别结果投影的样子，错的地方改好后再点「识别」", people.Count), Green);
            }
            catch (Exception ex)
            {
                SetError(Loc.F("自动初稿失败：{0}", ex.Message));
            }
            finally
            {
                _drafting = false;
                _recognizing = false;
            }
        });
    }

    /// <summary>涂人窗口「按识别结果重新生成」：看得见的部分换成当前多人会话的投影（被挡住的范围不动，可以撤销）。</summary>
    public void RegeneratePaint()
    {
        if (Paint is null || !_mergeSession || _session is not { } session || _loadingPaint) return;
        var paint = _paint!;
        var layerOf = Enumerable.Range(0, session.Humans.Length).Select(h => paint.People.FindIndex(p => p.Human == h)).ToList();
        if (layerOf.Any(k => k < 0)) return;
        _loadingPaint = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var masks = await WithSessionAsync(sid => _client.MasksAsync(sid), session);
                _paintActions.Enqueue(() => paint.Doc.ReplaceVisible(masks.Labels, layerOf));
            }
            catch (Exception ex)
            {
                SetError(Loc.F("生成遮罩失败：{0}", ex.Message));
            }
            finally
            {
                _loadingPaint = false;
            }
        });
    }

    /// <summary>会话丢了（后端重启）时从识别缓存恢复一次再调。</summary>
    private async Task<T> WithSessionAsync<T>(Func<string, Task<T>> call, SessionInfo session)
    {
        try
        {
            return await call(session.SessionId);
        }
        catch (BackendException ex) when (ex.StatusCode == 404 && (_sessionKey ?? _imageSha1) is { } key)
        {
            if (await Recognition.RestoreAsync(key, session.Name) is not { } info)
                throw new InvalidOperationException(Loc.T("会话已失效且没有识别缓存，请重新识别"));
            if (session == _session) _session = info;
            return await call(info.SessionId);
        }
    }

    // ------------------------------------------------------------------ 切换、编辑、应用

    /// <summary>把正在编辑的人的调整值记回这个人（切换人物、全部应用、重新识别前）。</summary>
    private void StorePersonParams()
    {
        if (_mergeSession && PersonOfHuman(_person) is { } current) current.Params = _params.Clone();
    }

    /// <summary>主窗口切到会话里的第 human 个人：换成这个人的调整值，切到这个人对应的角色。</summary>
    private void SwitchPerson(int human)
    {
        if (_mergeSession)
        {
            StorePersonParams();
            var next = PersonOfHuman(human);
            _params = (next?.Params ?? _mergeBase ?? _params).Clone();
            _selected = null;
            if (next?.Actor is { } actor && actor != _actor) SelectActor(actor);
        }
        _person = human;
        _result = null;
        RequestRetarget(false);
    }

    /// <summary>列表里「编辑」：主窗口切到这个人。</summary>
    public void EditPerson(MergePerson person)
    {
        if (person.Human is not { } human) return;
        IsOpen = true;
        if (human != _person) SwitchPerson(human);
        else if (person.Actor is { } actor && actor != _actor) SelectActor(actor);
    }

    /// <summary>一个角色只对应一个人：选了已被别人占用的角色时，别人的那个清掉。</summary>
    public void SetPersonActor(MergePerson person, int? actor)
    {
        if (actor is not null)
            foreach (var other in People)
                if (other != person && other.Actor == actor) other.Actor = null;
        person.Actor = actor;
        person.Status = null;
        if (actor is { } a && _mergeSession && person.Human == _person && a != _actor) SelectActor(a);
    }

    /// <summary>把每个勾选、识别过、选了角色的人用各自的调整值应用到对应角色。</summary>
    public void ApplyPeople()
    {
        if (_mergeApplying || !_mergeSession || _session is null) return;
        StorePersonParams();
        var rows = People.Where(Ready)
                         .Select(p => (Person: p, Human: p.Human!.Value, Actor: p.Actor!.Value, Params: (p.Params ?? _mergeBase ?? _params).Clone()))
                         .ToList();
        if (rows.Count == 0) return;
        var anchor = Anchor;
        var place = _config.MergePlacement && rows.Count >= 2;
        _mergeApplying = true;
        _ = Task.Run(async () =>
        {
            var ok = 0;
            try
            {
                foreach (var row in rows)
                {
                    row.Person.Status = (Loc.T("应用中…"), Yellow);
                    try
                    {
                        var error = await ApplyPersonAsync(row.Actor, row.Human, row.Params);
                        row.Person.Status = error is null ? (Loc.T("已应用"), Green) : (error, Red);
                        if (error is null) ok++;
                    }
                    catch (Exception ex)
                    {
                        row.Person.Status = (ex.Message, Red);
                    }
                }
                if (ok < rows.Count)
                    SetError(Loc.F("已应用 {0} 个人，{1} 个失败", ok, rows.Count - ok));
                else if (!place)
                    SetStatus(Loc.F("已应用 {0} 个人", ok), Green);
                else
                {
                    SetStatus(Loc.T("复刻站位中…"), Yellow);
                    var moved = await PlacePeopleAsync(rows.Select(r => (r.Person, r.Human, r.Actor, r.Params)).ToList(), anchor!);
                    SetStatus(Loc.F("已应用 {0} 个人并复刻站位（以 {1} 号为基准，移动了 {2} 个角色）", ok, People.ToList().IndexOf(anchor!) + 1, moved), Green);
                }
            }
            finally
            {
                _mergeApplying = false;
            }
        });
    }

    /// <summary>
    /// 复刻站位：读回各角色应用后的游戏姿势（含模型位置），按照片算出每个人的角色该站哪、朝哪，基准人物的角色不动。
    /// 返回移动了几个角色。
    /// </summary>
    private async Task<int> PlacePeopleAsync(List<(MergePerson Person, int Human, int Actor, RetargetParams Params)> rows, MergePerson anchor)
    {
        var inputs = new List<PlacementInput>();
        foreach (var row in rows)
        {
            var id = await _applier.IdentifyAsync(row.Actor);
            var race = id is null ? null : (id.Key == _actorKey ? _raceOverride : _actorStates.GetValueOrDefault(id.Key)?.RaceOverride) ?? id.Race;
            var trust = id is not null && _cleanSignatures.Contains(id.Signature);
            inputs.Add(new PlacementInput(row.Human, row.Params, await _applier.GetPoseAsync(row.Actor), race, trust,
                                          await _applier.GetTransformAsync(row.Actor)));
        }
        var result = await _client.PlacementAsync(_session!.SessionId, rows.FindIndex(r => r.Person == anchor), inputs);
        var moved = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var p = result.People[i];
            if (rows[i].Person == anchor) continue;
            var position = new Vector3(p.Position[0], p.Position[1], p.Position[2]);
            var rotation = new Quaternion(p.Rotation[0], p.Rotation[1], p.Rotation[2], p.Rotation[3]);
            if (!await _applier.MoveAsync(rows[i].Actor, position, rotation))
                rows[i].Person.Status = (Loc.T("已应用，但移动角色失败"), Yellow);
            else if (rows[i].Person != anchor)
            {
                moved++;
                rows[i].Person.Status = (Loc.F("已应用，站在基准角色{0} {1:0.00} 米", p.Relative.X >= 0 ? Loc.T("右边") : Loc.T("左边"), MathF.Abs(p.Relative.X)), Green);
            }
        }
        anchor.Status = (Loc.T("已应用（站位基准）"), Green);
        return moved;
    }

    /// <summary>按基准人物复刻照片镜头：站位复刻过的话，所有人都在画面里对应的位置。</summary>
    public void ReplicateMergeCamera()
    {
        if (_session is not { } session || Anchor is not { Human: { } human, Actor: { } actor } anchor) return;
        if (!_server.IsRunning)
        {
            SetError(Loc.T("复刻镜头需要 Posegraph HTTP 服务（/posegraph on）"));
            return;
        }
        StorePersonParams();
        var p = (anchor.Params ?? _mergeBase ?? _params).Clone();
        SetStatus(Loc.T("复刻镜头中…"), Yellow);
        _ = Run(async () =>
        {
            var id = await _applier.IdentifyAsync(actor);
            var race = id is null ? null : (id.Key == _actorKey ? _raceOverride : _actorStates.GetValueOrDefault(id.Key)?.RaceOverride) ?? id.Race;
            var r = await _client.CameraAsync(session.SessionId, human, p, actor, race, false);
            SetStatus(Loc.F("镜头已复刻：机位误差 {0:0.0}cm，构图偏差 {1:0.0}%", r.PositionErrorCm, r.FramingErrorPct), Green);
        });
    }

    /// <summary>会话丢了（后端重启）时从识别缓存恢复一次再算。</summary>
    private Task<string?> ApplyPersonAsync(int actor, int human, RetargetParams p)
        => WithSessionAsync(sid => RetargetAndApplyAsync(actor, sid, human, p), _session!);

    // ------------------------------------------------------------------ 图像页

    /// <summary>图像页图片上方的提示（合并页开着时）。</summary>
    private void DrawMergeHint()
    {
        if (!MergeEditing) return;
        if (Paint is null)
            UiText.Fit(Loc.T("合并模式：在角色列表里点「涂人」，给每个人涂上大致范围（或者「自动初稿」）"), Yellow);
        else
            UiText.Fit(Loc.T("合并模式：框是每个人涂过的范围，在涂人窗口里修改"), Grey);
    }

    /// <summary>在图像页画每个人涂过的范围（只显示，修改在涂人窗口里）。</summary>
    private void DrawPeopleBoxes(Vector2 origin, float scale)
    {
        if (!MergeEditing || _session is not { } session || Paint is not { } doc) return;
        var dl = ImGui.GetWindowDrawList();
        var current = _mergeSession ? PersonOfHuman(_person) : null;
        var people = People;
        for (var k = 0; k < doc.Count; k++)
        {
            if (doc.Box(k, session.ImageSize) is not { } b) continue;
            var p = people[k];
            var color = PersonColor(k);
            if (!p.Checked) color.W = 0.4f;
            var min = origin + new Vector2(b[0], b[1]) * scale;
            var max = origin + new Vector2(b[2], b[3]) * scale;
            dl.AddRect(min, max, ImGui.GetColorU32(color), 3, ImDrawFlags.None, p == current ? 3.5f : 1.8f);
            // 左上角编号；勾选了但还没识别的加「*」
            var label = (k + 1) + (p.Checked && p.Human is null && _mergeSession ? "*" : "");
            var size = ImGui.CalcTextSize(label) + new Vector2(8, 2) * ImGuiHelpers.GlobalScale;
            dl.AddRectFilled(min, min + size, ImGui.GetColorU32(color), 3);
            dl.AddText(min + new Vector2(4, 1) * ImGuiHelpers.GlobalScale, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 1)), label);
        }
    }
}
