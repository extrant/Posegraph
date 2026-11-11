using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Posegraph.Backend;
using Posegraph.Library;

namespace Posegraph.UI;

/// <summary>
/// 用户应用姿势后又在 Brio 里手动改了骨骼：隔几秒读回角色姿势，和我们推送的那份比，有差别就提示「收进修正」。
/// 收进来 = 后端把改动换成调整页的关节偏移（和 3D 页拖骨骼是同一套），再按图存进图库的修正：
/// 下次打开这张图自动套上，改别的调整值重新应用时也不会把 Brio 里的改动冲掉。
/// 只比身体骨骼的朝向（脸部骨骼、位置、缩放不比）；辅助骨跟着主骨联动，单独改辅助骨收不进来。
/// </summary>
public sealed partial class MainWindow
{
    private const float BrioEditMinDeg = 1f;
    private static readonly TimeSpan BrioEditPollInterval = TimeSpan.FromSeconds(2);

    // 最近一次推送到角色的姿势，以及当时的参数、参考姿势、角色和会话（比较、换算都以它为准）
    private sealed record Pushed(string Pose, RetargetParams Params, string? Reference, bool Trust, int? Actor, string Sid, int Person, string? Sha1);

    private Pushed? _pushed;
    private DateTime _brioPolledAt;
    private volatile bool _brioPolling;
    private string? _brioEdited;       // 读回的、和推送时不同的姿势
    private int _brioEditCount;
    private bool _brioAbsorbing;

    /// <summary>推送成功后记下这份姿势（PushAsync 调用）。</summary>
    private void RememberPushed(string pose)
    {
        if (_session is not { } session) return;
        var trust = _referenceSignature is { } sig && _cleanSignatures.Contains(sig);
        _pushed = new Pushed(pose, _params.Clone(), _reference, trust, _actor, session.SessionId, _person, _imageSha1);
        _brioEdited = null;
        _brioEditCount = 0;
    }

    /// <summary>每帧调用：在 GPose 里、推送过当前图当前人的姿势时，隔一会读回角色姿势比一比。</summary>
    private void PollBrioEdits()
    {
        if (_pushed is not { } pushed || _brioPolling || _brioAbsorbing || _rtBusy || !_clientState.IsGPosing || PoseEditing) return;
        if (pushed.Actor != _actor || pushed.Sid != _session?.SessionId || pushed.Person != _person) return;
        if (DateTime.UtcNow - _brioPolledAt < BrioEditPollInterval) return;
        _brioPolledAt = DateTime.UtcNow;
        _brioPolling = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var now = await _applier.GetPoseAsync(pushed.Actor);
                if (now is null || _pushed != pushed) return;
                var count = CountEdits(pushed.Pose, now);
                _brioEdited = count > 0 ? now : null;
                _brioEditCount = count;
            }
            catch
            {
                // 读不到就下次再试（角色切换、退出 GPose 时常见）
            }
            finally
            {
                _brioPolling = false;
            }
        });
    }

    /// <summary>推送的姿势里有、现在朝向差 1° 以上的身体骨骼数（脸部 j_f_ 不算）。</summary>
    private static int CountEdits(string pushedPose, string currentPose)
    {
        var before = Rotations(pushedPose);
        var after = Rotations(currentPose);
        var cos = MathF.Cos(BrioEditMinDeg * MathF.PI / 360f);
        return before.Count(kv => after.TryGetValue(kv.Key, out var q) && MathF.Abs(Quaternion.Dot(kv.Value, q)) < cos);
    }

    private static Dictionary<string, Quaternion> Rotations(string json)
    {
        var map = new Dictionary<string, Quaternion>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Bones", out var bones)) return map;
        foreach (var bone in bones.EnumerateObject())
        {
            if (bone.Name.StartsWith("j_f_") || !bone.Value.TryGetProperty("Rotation", out var r) || r.GetString() is not { } text) continue;
            var v = text.Split(',').Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : float.NaN).ToArray();
            if (v.Length == 4 && !v.Any(float.IsNaN)) map[bone.Name] = Quaternion.Normalize(new Quaternion(v[0], v[1], v[2], v[3]));
        }
        return map;
    }

    /// <summary>主窗口工具栏下面的提示条：Brio 里改了几根骨骼，「收进修正」/「忽略」。</summary>
    private void DrawBrioEditsBanner()
    {
        if (_brioEditCount <= 0 || _brioEdited is null || _pushed is null) return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Yellow, Loc.F("检测到在 Brio 里改了 {0} 根骨骼", _brioEditCount));
        ImGui.SameLine();
        using (ImRaii.Disabled(_brioAbsorbing))
            if (ImGui.SmallButton(Loc.T("收进修正并存进图库")))
                AbsorbBrioEdits();
        ItemTip(Loc.T("把 Brio 里的改动存进这张图（变成 3D 页的关节偏移），下次打开自动套上。\n只收身体骨骼的朝向"));
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("忽略")))
        {
            // 以现在的姿势为准，之后再改才提示
            _editorEdited = false;
            _pushed = _pushed with { Pose = _brioEdited };
            _brioEdited = null;
            _brioEditCount = 0;
        }
    }

    private volatile bool _editorEdited;   // 姿势编辑器点了「完成」、改动还没收进修正也没忽略

    /// <summary>
    /// 姿势编辑器点「完成」时，以及编辑中 / 编辑完还没存就换图时调用：结束编辑，把角色身上的改动存进这张图的修正——
    /// 骨骼朝向换成关节偏移、整个人挪的位置加到整体位移、拖过的视线存成眼球相对头部的朝向。
    /// 换图时这一步在新图打开之前做（会话、参数都还是这张图的），不然新图的姿势一套上，改动就丢了。失败不挡换图，只提示。
    /// </summary>
    private async Task SaveEditorEditsAsync()
    {
        var editing = PoseEditing;
        if (!editing && !_editorEdited) return;
        if (editing) PoseEditor?.Finish();
        _editorEdited = false;
        var gaze = PoseEditor?.Gaze();
        if (_pushed is not { } pushed || pushed.Sha1 is not { } sha1)
        {
            SetStatus(Loc.T("这个姿势不是从当前图应用的，改动只留在角色身上，没存进图库"), Yellow);
            return;
        }
        _brioAbsorbing = true;
        try
        {
            await Task.Delay(400);   // 等编辑器最后一下写进游戏
            if (await _applier.GetPoseAsync(pushed.Actor) is not { } now) return;
            var r = await _client.BrioEditsAsync(pushed.Sid, pushed.Person, pushed.Params, pushed.Reference, _raceOverride ?? _detectedRace,
                                                 pushed.Trust, _useAux, now);
            var moved = r.RootOffset is { Length: 3 } offset && !offset.SequenceEqual(pushed.Params.RootOffset);
            if (r.Changed.Length == 0 && !moved && gaze is null)
            {
                SetStatus(Loc.T("姿势编辑器里没有可存的改动"), Yellow);
                return;
            }
            var current = sha1 == _imageSha1;
            var p = current ? _params : pushed.Params.Clone();
            p.Offsets = new Dictionary<string, float[]>(r.Offsets);
            if (moved) p.RootOffset = (float[])r.RootOffset!.Clone();
            if (gaze is not null) p.Gaze = gaze.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone());
            Library.WriteCorrection(sha1, new SavedCorrection(_preset, p.Clone(), DateTime.Now));
            if (current)
            {
                _savedFix = FullSnapshot(_preset, _params);
                _pushed = pushed with { Pose = now, Params = _params.Clone() };
                RequestRetarget(false);   // 3D 页、调整页跟着更新；不重新推送（角色身上已经是这个姿势）
            }
            _brioEdited = null;
            _brioEditCount = 0;
            var parts = new List<string>();
            if (r.Changed.Length > 0) parts.Add(Loc.F("{0} 根骨骼的朝向", r.Changed.Length));
            if (moved) parts.Add(Loc.T("整体位置"));
            if (gaze is not null) parts.Add(Loc.T("视线"));
            var what = string.Join(Loc.T("、"), parts);
            SetStatus(current ? Loc.F("已把姿势编辑器的改动（{0}）存进这张图的修正", what) : Loc.F("已把姿势编辑器的改动（{0}）存进上一张图的修正", what),
                      Green);
        }
        catch (Exception ex)
        {
            SetError(Loc.F("姿势编辑器的改动没能存进图库：{0}", ex.Message));
        }
        finally
        {
            _brioAbsorbing = false;
        }
    }

    private void AbsorbBrioEdits()
    {
        _editorEdited = false;
        if (_pushed is not { } pushed || _brioEdited is not { } edited) return;
        _brioAbsorbing = true;
        SetStatus(Loc.T("正在把 Brio 里的改动收进修正…"), Yellow);
        _ = Run(async () =>
        {
            try
            {
                var r = await _client.BrioEditsAsync(pushed.Sid, pushed.Person, pushed.Params, pushed.Reference, _raceOverride ?? _detectedRace,
                                                     pushed.Trust, _useAux, edited);
                if (r.Changed.Length == 0)
                {
                    SetStatus(Loc.T("Brio 里改的是辅助骨、脸部或骨骼位置，收不进修正（已忽略）"), Yellow);
                }
                else
                {
                    _params.Offsets = new Dictionary<string, float[]>(r.Offsets);
                    var saved = "";
                    if (pushed.Sha1 is { } sha1 && sha1 == _imageSha1)
                    {
                        Library.WriteCorrection(sha1, new SavedCorrection(_preset, _params.Clone(), DateTime.Now));
                        _savedFix = FullSnapshot(_preset, _params);
                        saved = Loc.T("，已存进图库");
                    }
                    SetStatus(Loc.F("已把 Brio 里 {0} 根骨骼的改动收进修正", r.Changed.Length) + saved
                              + (r.ResidualDeg > 3 ? Loc.F("（还差 {0:0}°：部分改动是辅助骨或位置）", r.ResidualDeg) : ""), Green);
                    RequestRetarget(false);
                }
                _pushed = pushed with { Pose = edited, Params = _params.Clone() };
                _brioEdited = null;
                _brioEditCount = 0;
            }
            catch (Exception ex)
            {
                SetError(Loc.F("收进修正失败：{0}", ex.Message));
            }
            finally
            {
                _brioAbsorbing = false;
            }
        });
    }
}
