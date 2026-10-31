using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Posegraph.Backend;
using Posegraph.Library;
using Posegraph.Video;

namespace Posegraph.UI;

/// <summary>
/// 视频：载入视频 → 拖到某一帧「找人」并点选 → 选起止、帧率 → 后端逐帧识别（backend video.py，可取消）→ 结果存成片段文件
/// （文档\Posegraph\Clips）→ 播放器在游戏里逐帧套姿势、摆镜头（ClipPlayer：跟拍 / 走动两种镜头）。
/// 第一版只认一个人、一个角色；视频里有切镜时只提示（截取一段连续镜头）。
/// 片段可以录成视频（ClipRecorder：逐帧摆好再截游戏画面，后端编码成 mp4，存在 文档\Posegraph\Videos）。
/// </summary>
public sealed partial class VideoWindow : Window, IDisposable
{
    private static readonly Vector4 Green = PosegraphTheme.Green;
    private static readonly Vector4 Yellow = PosegraphTheme.Yellow;
    private static readonly Vector4 Grey = PosegraphTheme.Grey;
    private static readonly int[] FpsChoices = [5, 10, 15, 30];
    private static readonly int[] RecordFpsChoices = [0, 30, 60];   // 0 = 跟片段一样
    private static readonly string VideoFilter = "{.mp4,.mov,.m4v,.webm,.mkv,.avi}";

    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly ITextureProvider _textures;
    private readonly MainWindow _main;
    private readonly ClipPlayer _player;
    private readonly ClipRecorder _recorder;
    private readonly PoseApplier _applier;
    private readonly FileDialogManager _dialog = new();

    private string? _videoPath;
    private VideoInfo? _video;
    private IDalamudTextureWrap? _frame;
    private double _frameTime = -1;       // 预览图对应的时间
    private double _scrub;                // 滑条上的时间
    private bool _frameLoading;
    private double _start, _end;
    private DetectedPerson[] _people = [];
    private double _peopleTime = -1;      // 找人是在哪一帧
    private List<int> _chosen = [];          // 画面上选中的人（找人结果的序号），第一个是主角
    private int? _actor;                     // 主角对应的游戏角色
    private readonly List<int?> _extraActors = [];   // 第 2、3… 个人对应的游戏角色
    private VideoJobStatus? _job;
    private string _status = "";
    private Vector4 _statusColor = Grey;
    private bool _busy;
    private bool _rebuilding;
    private DateTime _nextBodyCheck;
    private string? _skipBody;
    private readonly ClipAudio _audio;
    private (string? Clip, string Path)? _musicOverride;   // 录制配音乐：某个片段手动选的音乐文件            // 这个体型重算失败过，不再自动重试（换了体型再试）

    public VideoWindow(Configuration config, IDalamudPluginInterface pi, IFramework framework, ITextureProvider textures,
                       ITextureReadbackProvider readback, IGameGui gameGui, MainWindow main, BrioBridge brio, PoseApplier applier)
        : base(Loc.T("Posegraph 视频") + "###PosegraphVideo")
    {
        _config = config;
        _pi = pi;
        _textures = textures;
        _main = main;
        _player = new ClipPlayer(framework, brio, i => applier.Resolve(i))
        {
            Loop = config.VideoLoop,
            DriveCamera = config.VideoDriveCamera,
        };
        _player.SetMode(config.VideoWalk ? ClipCameraMode.Walk : ClipCameraMode.Follow);
        _player.WalkCamera = (WalkCamera)Math.Clamp(config.VideoWalkCamera, 0, 3);
        _player.HiddenCamera = (HiddenCamera)Math.Clamp(config.VideoHiddenCamera, 0, 2);
        _recorder = new ClipRecorder(framework, textures, readback, gameGui);
        _audio = new ClipAudio(framework, _player, () => _main.Client)
        {
            Enabled = config.VideoPlayMusic,
            Volume = config.VideoMusicVolume,
            Source = MusicSource,
            Muted = () => _recorder.Running,
        };
        _applier = applier;
        Size = new Vector2(620, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>ReShade 兼容录制期间为 true：插件窗口都不画。</summary>
    public bool HideUi => _recorder.HideUi;

    public static string VideosFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Posegraph", "Videos");

    public override void PreDraw() => PosegraphTheme.Push();

    public override void PostDraw() => PosegraphTheme.Pop();

    public override void Draw()
    {
        _dialog.Draw();
        DrawSource();
        if (_video is not null)
        {
            ImGui.Separator();
            DrawPreview();
            DrawRange();
            DrawRecognize();
        }
        ImGui.Separator();
        SyncActors();
        PollBrioEdits();
        DrawPlayer();
        if (_status.Length > 0) UiText.Fit(_status, _statusColor);
        WatchActorBody();
    }

    /// <summary>
    /// 角色列表变了（重进 GPose、换场景、Brio 删了角色）后，选着的角色编号可能已经不存在：主角换成选中 / 第一个角色，
    /// 配角不存在的清掉；和播放器手上的不一样就同步过去（不然播放器拿旧编号一直「找不到角色」）。录制、重算时不动。
    /// </summary>
    private void SyncActors()
    {
        if (_recorder.Running || _rebuilding) return;
        var actors = _main.Actors;
        bool Exists(int? index) => index is { } i && actors.Any(x => x.Index == i);
        if (!Exists(_actor)) _actor = Exists(_main.SelectedActor) ? _main.SelectedActor : actors.Length > 0 ? actors[0].Index : null;
        for (var i = 0; i < _extraActors.Count; i++)
            if (!Exists(_extraActors[i]) || _extraActors[i] == _actor) _extraActors[i] = null;
        if (_player.Clip is not { } clip || _actor is null) return;
        FillExtraActors(clip.People.Length);
        List<int?> want = [_actor, .. _extraActors.Take(clip.People.Length - 1)];
        if (!want.SequenceEqual(_player.Actors)) _player.SetActors(want);
    }

    /// <summary>识别 / 重算参数里的表情部分（开关、强度、分项权重）。</summary>
    private RetargetParams FaceParams() => new()
    {
        Face = _config.VideoFace, FaceStrength = _config.VideoFaceStrength, FaceGains = new(_config.VideoFaceGains),
    };

    /// <summary>
    /// 每秒看一次播放的角色：种族或体型（Glamourer 换种族、改身高等）和片段算的时候不一样，就用存下的识别原始数据重算骨骼、镜头和表情
    /// （几秒，不重新识别），存成这个种族的片段并换上。旧片段没有原始数据时只提示。
    /// </summary>
    private void WatchActorBody()
    {
        if (DateTime.UtcNow < _nextBodyCheck || _rebuilding || _busy || _recorder.Running || !_main.InGpose
            || _player.Clip is not { } clip || _player.Actor is not { } actor)
            return;
        _nextBodyCheck = DateTime.UtcNow.AddSeconds(1);
        var actors = _player.Actors;
        _ = Task.Run(async () =>
        {
            if (await _main.ActorBodyAsync(actor) is not { } now || _player.Clip != clip || _player.Actor != actor) return;
            if (clip.People.Length > 1)
            {
                // 多人：每个配了角色的人的体型签名拼起来比
                var body = await BodiesAsync(actors, clip.People.Length);
                if (body == clip.Body || body == _skipBody || _player.Clip != clip) return;
                if (clip.RawPath is null)
                {
                    _skipBody = body;
                    return;
                }
                await RebuildAsync(clip, actor, Loc.T("角色换了种族或体型，已重算骨骼、站位、镜头和表情"), body);
                return;
            }
            if (now.Body == clip.Body || now.Body == _skipBody) return;
            if (clip.RawPath is null)
            {
                _skipBody = now.Body;
                if (clip.Race != now.Race)
                    SetStatus(Loc.F("角色是 {0}，片段是按 {1} 算的；这个片段没有识别原始数据（旧版本识别的），要重新识别才能换", Races.Label(now.Race), Races.Label(clip.Race)), Yellow);
                return;
            }
            var reason = clip.Race != now.Race
                ? Loc.F("角色换成了 {0}，已按新种族重算骨骼、镜头和表情", Races.Label(now.Race))
                : Loc.T("角色体型变了，已重算骨骼、镜头和表情");
            await RebuildAsync(clip, actor, reason, now.Body);
        });
    }

    /// <summary>用片段的识别原始数据按角色现在的种族 / 骨长 / 表情权重重算，存成这个种族的片段（同名覆盖）并换上。</summary>
    private async Task RebuildAsync(Clip clip, int actor, string done, string? body = null)
    {
        if (clip.RawPath is not { } rawPath || _rebuilding) return;
        _rebuilding = true;
        SetStatus(Loc.T("正在重算片段…"), Yellow);
        try
        {
            var time = _player.Time;
            var actors = _player.Actors.Count > 0 ? _player.Actors.ToList() : [actor];
            _player.Stop();
            var people = new List<ClipRebuildPerson>();
            for (var p = 0; p < clip.People.Length; p++)
            {
                if (p < actors.Count && actors[p] is { } a)
                {
                    await _applier.ResetPoseAsync(a);   // 清掉按旧种族套的姿势，读到的骨长才是新角色的
                    var (reference, race) = await _main.ActorContextAsync(a);
                    people.Add(new ClipRebuildPerson(race, reference, FaceParams()));
                }
                else people.Add(new ClipRebuildPerson(clip.People[p].Race, null, FaceParams()));   // 没配角色：按识别时的种族
            }
            body ??= clip.People.Length > 1 ? await BodiesAsync(actors, clip.People.Length) : (await _main.ActorBodyAsync(actor))?.Body;
            var raw = await _main.Client.UploadRawAsync(await File.ReadAllBytesAsync(rawPath));
            var request = new ClipRebuildRequest([.. people], clip.Smooth);
            var json = await _main.Client.RebuildClipAsync(raw.RawId, RequestWithEdits(request, clip.Edits));
            var path = Clip.Save(json, clip.BaseName, clip.Video, body, Path.GetFileName(rawPath), overwrite: true, clip.VideoPath, clip.View,
                                 clip.Edits);
            var rebuilt = Clip.Load(path);
            RememberEditContext(path, rebuilt.RawPath, request, raw.RawId);
            _player.Load(rebuilt, actors);
            _player.Seek(time);
            SetStatus(done + Loc.F("（{0}）", rebuilt.Name), Green);
        }
        catch (Exception ex)
        {
            _skipBody = body;
            SetStatus(Loc.F("重算失败：{0}", ex.Message), Yellow);
        }
        finally { _rebuilding = false; }
    }

    /// <summary>多人片段的体型签名：每个人对应角色的签名按顺序拼起来（没配角色的写空）。</summary>
    private async Task<string> BodiesAsync(IReadOnlyList<int?> actors, int people)
    {
        var parts = new List<string>();
        for (var p = 0; p < people; p++)
            parts.Add(p < actors.Count && actors[p] is { } a ? (await _main.ActorBodyAsync(a))?.Body ?? "" : "");
        return string.Join("||", parts);
    }

    /// <summary>片段里第 p 个人现在配的游戏角色（第一个是主角）。</summary>
    private int? ActorFor(int p) => p == 0 ? _actor : p - 1 < _extraActors.Count ? _extraActors[p - 1] : null;

    /// <summary>按人数补齐配角的角色：没配过的依次挑还没用过的角色。</summary>
    private void FillExtraActors(int people)
    {
        var actors = _main.Actors;
        while (_extraActors.Count < people - 1)
        {
            var used = new HashSet<int?>(_extraActors) { _actor };
            _extraActors.Add(actors.Select(x => (int?)x.Index).FirstOrDefault(i => !used.Contains(i)));
        }
    }

    // ------------------------------------------------------------------ 视频

    private void DrawSource()
    {
        using (ImRaii.Disabled(_busy || !_main.BackendOnline))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderOpen, Loc.T("打开视频…")))
                _dialog.OpenFileDialog(Loc.T("打开视频"), Loc.T("视频") + VideoFilter, (ok, paths) =>
                {
                    if (ok && paths.Count > 0) OpenVideo(paths[0]);
                }, 1, Directory.Exists(_config.LastVideoDir) ? _config.LastVideoDir : null);
        Tip(_main.BackendOnline ? Loc.T("mp4 / mov / webm / mkv / avi；视频会传给识别后端（本机）") : Loc.T("识别后端没有运行"));
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Film, Loc.T("打开片段…")))
        {
            Directory.CreateDirectory(Clip.Folder);
            _dialog.OpenFileDialog(Loc.T("打开片段"), Loc.T("片段") + "{.pgclip}", (ok, paths) =>
            {
                if (ok && paths.Count > 0) LoadClip(paths[0]);
            }, 1, Clip.Folder);
        }
        Tip(Loc.F("识别好的片段自动存在 {0}", Clip.Folder));
        if (_video is { } v)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            UiText.Fit(Loc.F("{0} · {1:0.0} 秒 · {2:0.##} 帧/秒 · {3}×{4}", Path.GetFileName(_videoPath), v.Duration, v.Fps, v.Width, v.Height), Grey);
            if (v.Cuts is { Length: > 0 } cuts)
                ImGui.TextColored(Yellow, Loc.F("视频里有切镜（{0}）：第一版只支持连续的单个镜头，起止请选在两次切镜之间",
                                                string.Join("、", cuts.Select(c => $"{c:0.0}s"))));
        }
    }

    private void OpenVideo(string path)
    {
        _config.LastVideoDir = Path.GetDirectoryName(path) ?? "";
        _pi.SavePluginConfig(_config);
        _busy = true;
        SetStatus(Loc.T("正在载入视频…"), Yellow);
        Task.Run(async () =>
        {
            try
            {
                var info = await _main.Client.UploadVideoAsync(path);
                _videoPath = path;
                _video = info;
                _start = 0;
                _end = Math.Min(info.Duration, 10);
                _scrub = 0;
                _people = [];
                _chosen = [];
                _peopleTime = -1;
                SetStatus(info.Duration > 10 ? Loc.T("已载入。默认截取前 10 秒，可以拖动后「设为起点 / 终点」") : Loc.T("已载入"), Green);
                await LoadFrameAsync(0);
                for (var i = 0; i < 120 && _video == info && info.Cuts is null; i++)   // 后台查切镜，查完更新
                {
                    await Task.Delay(1000);
                    var latest = await _main.Client.VideoAsync(info.VideoId);
                    if (latest.Cuts is not null && _video == info) _video = info = latest;
                }
            }
            catch (Exception ex)
            {
                SetStatus(Loc.F("载入失败：{0}", ex.Message), Yellow);
            }
            finally { _busy = false; }
        });
    }

    private async Task LoadFrameAsync(double t)
    {
        if (_video is not { } v) return;
        _frameLoading = true;
        try
        {
            var bytes = await _main.Client.VideoFrameAsync(v.VideoId, t);
            var texture = await _textures.CreateFromImageAsync(bytes);
            var old = _frame;
            _frame = texture;
            _frameTime = t;
            old?.Dispose();
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("读不到这一帧：{0}", ex.Message), Yellow);
        }
        finally { _frameLoading = false; }
    }

    private void DrawPreview()
    {
        var v = _video!;
        // 拖动后松手（或停住）再取那一帧，取的过程中不重复请求
        if (!_frameLoading && Math.Abs(_frameTime - _scrub) > 1e-3 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _ = LoadFrameAsync(_scrub);
        var avail = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(avail, avail * v.Height / Math.Max(1, v.Width));
        var maxH = 380 * ImGuiHelpers.GlobalScale;
        if (size.Y > maxH) size *= maxH / size.Y;
        var origin = ImGui.GetCursorScreenPos() + new Vector2((avail - size.X) / 2, 0);
        ImGui.SetCursorScreenPos(origin);
        if (_frame is { } frame) ImGui.Image(frame.Handle, size);
        else ImGui.Dummy(size);
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var scale = size.X / v.Width;
        if (Math.Abs(_peopleTime - _frameTime) < 1e-3)
        {
            for (var i = 0; i < _people.Length; i++)
            {
                var b = _people[i].Bbox;
                var min = origin + new Vector2(b[0], b[1]) * scale;
                var max = origin + new Vector2(b[2], b[3]) * scale;
                var order = _chosen.IndexOf(i);
                var chosen = order >= 0;
                var over = hovered && ImGui.IsMouseHoveringRect(min, max);
                var color = chosen ? PosegraphTheme.Accent : over ? new Vector4(1, 1, 1, 0.9f) : new Vector4(1, 1, 1, 0.5f);
                dl.AddRect(min, max, ImGui.GetColorU32(color), 0, ImDrawFlags.None, chosen ? 3f : 1.5f);
                dl.AddText(min + new Vector2(4, 2), ImGui.GetColorU32(color),
                           order == 0 ? Loc.T("主角") : chosen ? Loc.F("第 {0} 人", order + 1) : (i + 1).ToString());
                if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if (chosen)
                    {
                        _chosen.RemoveAt(order);
                        if (order > 0 && order - 1 < _extraActors.Count) _extraActors.RemoveAt(order - 1);
                    }
                    else if (_chosen.Count < MaxPeople) _chosen.Add(i);
                    FillExtraActors(_chosen.Count);
                }
            }
        }
        var t = (float)_scrub;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##scrub", ref t, 0f, (float)v.Duration, Loc.T("%.2f 秒"))) _scrub = t;
    }

    private void DrawRange()
    {
        if (ImGui.Button(Loc.T("设为起点"))) _start = Math.Min(_scrub, Math.Max(0, _end - 0.2));
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("设为终点"))) _end = Math.Max(_scrub, _start + 0.2);
        ImGui.SameLine();
        var fps = _config.VideoFps;
        var frames = (int)Math.Floor((_end - _start) * fps + 1e-6) + 1;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.F("{0:0.00} ~ {1:0.00} 秒（{2} 帧）", _start, _end, frames));
        Tip(Loc.F("按每帧约 0.5～1 秒估算，识别要 {0:0} ～ {1:0} 秒（游戏开着时显卡被占用会慢一些）", frames * 0.5, frames * 1.0));

        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo(Loc.T("帧率"), Loc.F("{0} 帧/秒", fps)))
        {
            foreach (var f in FpsChoices)
                if (ImGui.Selectable(Loc.F("{0} 帧/秒", f), f == fps))
                {
                    _config.VideoFps = f;
                    _pi.SavePluginConfig(_config);
                }
            ImGui.EndCombo();
        }
        Tip(Loc.T("每秒识别多少帧，帧之间插值播放。快的动作用 15 / 30，慢的 5 / 10 就够"));
        ImGui.SameLine();
        var smooth = _config.VideoSmooth;
        ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat(Loc.T("平滑"), ref smooth, 0f, 1f, "%.2f"))
        {
            _config.VideoSmooth = smooth;
            _pi.SavePluginConfig(_config);
        }
        Tip(Loc.T("压掉逐帧识别的抖动；太大会让快动作变钝"));
        ImGui.SameLine();
        var face = _config.VideoFace;
        if (ImGui.Checkbox(Loc.T("表情"), ref face))
        {
            _config.VideoFace = face;
            _pi.SavePluginConfig(_config);
        }
        Tip(Loc.T("逐帧识别脸上的表情（眼睛、眉毛、嘴、下巴），套到脸部骨骼上。视频里比照片保守：强度打七折、时间上平滑得更多，脸太小、侧脸的帧不用。关掉时保留角色现在的表情"));
    }

    private void DrawRecognize()
    {
        var v = _video!;
        var running = _job is { Finished: false };
        using (ImRaii.Disabled(_busy || running))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Search, Loc.T("找人")))
                FindPeople(_scrub);
        Tip(Loc.T("在当前这一帧里找人；找到后在画面上点选要识别的那个人"));
        ImGui.SameLine();
        DrawActorCombo();
        ImGui.SameLine();
        var ready = _chosen.Count > 0 && _actor is not null && Enumerable.Range(1, _chosen.Count - 1).All(p => ActorFor(p) is not null)
                    && _main.BackendOnline;
        if (running)
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Stop, Loc.T("取消")))
                _ = _main.Client.CancelVideoJobAsync(_job!.JobId);
        }
        else
        {
            using (ImRaii.Disabled(!ready || _busy))
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, Loc.T("开始识别")))
                    StartJob(v);
            Tip(_chosen.Count == 0 ? Loc.T("先「找人」并在画面上点选一个人") : !ready ? Loc.T("先给每个选中的人选一个游戏角色")
                : Loc.T("逐帧识别选中的人，完成后自动存成片段并载入播放器"));
        }
        if (_chosen.Count > 1) DrawExtraActors(_chosen.Count, apply: false);
        if (_job is { } job && !job.Finished)
        {
            var phase = job.State switch
            {
                "detecting" => Loc.T("找人"),
                "estimating" => Loc.T("识别"),
                "retargeting" => Loc.T("换算骨架"),
                _ => Loc.T("排队"),
            };
            ImGui.ProgressBar(job.Total > 0 ? (float)job.Done / job.Total : 0, new Vector2(-1, 0),
                              Loc.F("{0} {1}/{2}（{3:0} 秒）", phase, job.Done, job.Total, job.Elapsed));
        }
    }

    private const int MaxPeople = 4;

    /// <summary>配角对应的游戏角色（第 2、3… 个人各一个下拉框）。apply：改了马上换到播放器上。</summary>
    private void DrawExtraActors(int people, bool apply)
    {
        FillExtraActors(people);
        var actors = _main.Actors;
        for (var p = 1; p < people; p++)
        {
            var current = _extraActors[p - 1];
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Loc.F("第 {0} 人", p + 1));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
            var label = current is { } cur ? actors.FirstOrDefault(x => x.Index == cur).Name ?? "?" : Loc.T("（不放）");
            if (ImGui.BeginCombo($"##extraActor{p}", label))
            {
                if (ImGui.Selectable(Loc.T("（不放）"), current is null)) current = null;
                foreach (var (index, name) in actors)
                    if (ImGui.Selectable($"{name}##extra{p}_{index}", index == current))
                        current = index;
                ImGui.EndCombo();
            }
            Tip(Loc.T("这个人的动作套到哪个 GPose 角色上；按视频里和主角的相对位置摆在主角旁边。选「不放」时这个人只参与识别"));
            if (current != _extraActors[p - 1])
            {
                _extraActors[p - 1] = current;
                if (apply) _player.SetActors([_actor, .. _extraActors.Take(people - 1)]);
            }
        }
    }

    private void DrawActorCombo()
    {
        var actors = _main.Actors;
        _actor ??= _main.SelectedActor ?? (actors.Length > 0 ? actors[0].Index : null);
        if (_actor is { } a && actors.All(x => x.Index != a)) _actor = actors.Length > 0 ? actors[0].Index : null;
        var label = _actor is { } cur ? actors.FirstOrDefault(x => x.Index == cur).Name ?? "?" : Loc.T("（没有角色）");
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##videoActor", label))
        {
            foreach (var (index, name) in actors)
                if (ImGui.Selectable($"{name}##{index}", index == _actor))
                {
                    _actor = index;
                    _player.SetActor(index);
                }
            ImGui.EndCombo();
        }
        Tip(Loc.T("动作套到哪个 GPose 角色上（骨长、种族按这个角色算）"));
        // 配角和主角选了同一个角色：配角让出来
        for (var i = 0; i < _extraActors.Count; i++)
            if (_extraActors[i] == _actor) _extraActors[i] = null;
    }

    private void FindPeople(double t)
    {
        if (_video is not { } v) return;
        _busy = true;
        SetStatus(Loc.T("正在找人…"), Yellow);
        Task.Run(async () =>
        {
            try
            {
                var r = await _main.Client.VideoDetectAsync(v.VideoId, t, _config.MergeDetector);
                if (Math.Abs(_frameTime - t) > 1e-3) await LoadFrameAsync(t);
                _people = r.People;
                _peopleTime = t;
                _chosen = r.People.Length == 1 ? [0] : [];
                SetStatus(r.People.Length switch
                {
                    0 => Loc.T("这一帧没找到人，换一帧试试"),
                    1 => Loc.T("找到 1 个人，已选中"),
                    _ => Loc.F("找到 {0} 个人：在画面上点选要识别的人，可以多选（先点的是主角，镜头跟着他），再点一下取消", r.People.Length),
                }, r.People.Length == 0 ? Yellow : Green);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.F("找人失败：{0}", ex.Message), Yellow);
            }
            finally { _busy = false; }
        });
    }

    private void StartJob(VideoInfo v)
    {
        var chosen = _chosen.ToList();
        var boxes = chosen.Select(i => _people[i].Bbox).ToList();
        var actors = Enumerable.Range(0, chosen.Count).Select(ActorFor).ToList();
        var actor = _actor!.Value;
        var seed = Math.Clamp(_peopleTime, _start, _end);
        var name = $"{Path.GetFileNameWithoutExtension(_videoPath)} {_start:0.0}-{_end:0.0}s";
        _busy = true;
        SetStatus(Loc.T("正在开始识别…"), Yellow);
        Task.Run(async () =>
        {
            try
            {
                var people = new List<VideoJobPerson>();
                for (var p = 0; p < chosen.Count; p++)
                {
                    var (reference, race) = await _main.ActorContextAsync(actors[p]!.Value);
                    people.Add(new VideoJobPerson(boxes[p], race, reference, FaceParams()));
                }
                var body = chosen.Count > 1 ? await BodiesAsync(actors, chosen.Count) : (await _main.ActorBodyAsync(actor))?.Body;
                var request = new VideoJobRequest(_start, _end, _config.VideoFps, seed, _config.VideoSmooth, [.. people]);
                var job = await _main.Client.StartVideoJobAsync(v.VideoId, request);
                _job = job;
                _busy = false;
                SetStatus(Loc.T("识别中…（可以关掉窗口，完成后会自动载入）"), Yellow);
                while (!job.Finished)
                {
                    await Task.Delay(800);
                    _job = job = await _main.Client.VideoJobAsync(job.JobId);
                }
                if (job.State == "cancelled")
                {
                    SetStatus(Loc.T("已取消"), Grey);
                    return;
                }
                if (job.State == "error") throw new InvalidOperationException(job.Error);
                var json = await _main.Client.VideoJobResultAsync(job.JobId);
                string? raw = null;
                try { raw = Clip.SaveRaw(await _main.Client.VideoJobRawAsync(job.JobId), name); }
                catch { /* 旧版后端没有原始数据：片段照存，只是换种族时要重新识别 */ }
                var path = Clip.Save(json, name, Path.GetFileName(_videoPath) ?? "", body, raw, overwrite: false, _videoPath);
                var clip = Clip.Load(path);
                _player.Load(clip, actors);
                RememberEditContext(path, clip.RawPath,
                                    new ClipRebuildRequest([.. people.Select(p => new ClipRebuildPerson(p.Race, p.Reference, p.Params))], _config.VideoSmooth));
                var missing = clip.People.Sum(p => p.Missing.Count(m => m));
                SetStatus(Loc.F("识别完成（{0:0} 秒），已存成片段 {1}{2}", job.Elapsed, Path.GetFileName(path),
                                missing > 0 ? Loc.F("；{0} 帧没识别到人，用前后插值补上", missing) : ""), Green);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.F("识别失败：{0}", ex.Message), Yellow);
            }
            finally { _busy = false; }
        });
    }

    // ------------------------------------------------------------------ 播放

    private void LoadClip(string path)
    {
        try
        {
            var clip = Clip.Load(path);
            _actor ??= _main.SelectedActor ?? (_main.Actors.Length > 0 ? _main.Actors[0].Index : null);
            FillExtraActors(clip.People.Length);
            _player.Load(clip, [_actor, .. _extraActors.Take(clip.People.Length - 1)]);
            SetStatus(Loc.F("已载入片段 {0}", clip.Name), Green);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("片段读不了：{0}", ex.Message), Yellow);
        }
    }

    private void DrawPlayer()
    {
        if (_player.Clip is not { } clip)
        {
            ImGui.TextDisabled(Loc.T("还没有片段：识别一段视频，或者「打开片段…」"));
            return;
        }
        ImGui.TextUnformatted(Loc.F("片段：{0}（{1:0.0} 秒，{2} 帧，{3:0.##} 帧/秒）", clip.Name, clip.Duration, clip.FrameCount, clip.Fps));
        foreach (var w in clip.Warnings) ImGui.TextColored(Yellow, w);
        if (clip.People.Length > 1)
        {
            if (clip.People.Skip(1).All(p => p.Offset.Length == 0))
                ImGui.TextColored(Yellow, Loc.T("这个片段是旧版本识别的：没有多人站位，只放主角（有识别原始数据时换一次角色会自动重算）"));
            DrawExtraActors(clip.People.Length, apply: true);
        }
        if (!_main.InGpose) ImGui.TextColored(Yellow, Loc.T("进入 GPose 后才能播放"));
        if (_video is null)
        {
            // 只打开了片段（没载入视频）：识别区不显示，在这里选主角
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Loc.T("角色"));
            ImGui.SameLine();
            DrawActorCombo();
        }
        if (_player.Actor is null) ImGui.TextColored(Yellow, Loc.T("先选一个游戏角色（进 GPose 后才有）"));

        using var recording = ImRaii.Disabled(_recorder.Running);
        using (ImRaii.Disabled(!_main.InGpose || _player.Actor is null))
        {
            if (ImGuiComponents.IconButton("##play", _player.Playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play))
            {
                if (_player.Playing) _player.Pause();
                else _player.Play();
            }
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("##stop", FontAwesomeIcon.Stop)) _player.Stop();
            Tip(Loc.T("停止：回到开头，走动模式下角色回到原位"));
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("##prev", FontAwesomeIcon.StepBackward)) _player.Seek(_player.Time - 1 / clip.Fps);
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("##next", FontAwesomeIcon.StepForward)) _player.Seek(_player.Time + 1 / clip.Fps);
            ImGui.SameLine();
            var t = (float)_player.Time;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##clipTime", ref t, 0f, (float)clip.Duration, Loc.T("%.2f 秒"))) _player.Seek(t);
        }
        if (_main.InGpose && _player.Actor is not null) DrawTimeline(clip);

        var loop = _player.Loop;
        if (ImGui.Checkbox(Loc.T("循环"), ref loop))
        {
            _player.Loop = _config.VideoLoop = loop;
            _pi.SavePluginConfig(_config);
        }
        ImGui.SameLine();
        var speed = _player.Speed;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat(Loc.T("速度"), ref speed, 0.1f, 2f, "%.2fx")) _player.Speed = speed;
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _player.Speed = 1f;
        Tip(Loc.T("右键恢复 1x"));
        DrawPlayMusic(clip);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("镜头"));
        ImGui.SameLine();
        var walk = _player.Mode == ClipCameraMode.Walk;
        if (ImGui.RadioButton(Loc.T("跟拍"), !walk)) SetWalk(false);
        Tip(Loc.T("角色原地不动，镜头每帧按原视频的构图移动：手持、跟拍的视频看起来最像原片"));
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.T("走动"), walk)) SetWalk(true);
        Tip(Loc.T("角色按视频在场景里走（原视频镜头的运动已经分出去了），镜头按下面选的机位拍"));
        ImGui.SameLine();
        var drive = _player.DriveCamera;
        if (ImGui.Checkbox(Loc.T("控制镜头"), ref drive))
        {
            _player.DriveCamera = _config.VideoDriveCamera = drive;
            _pi.SavePluginConfig(_config);
        }
        Tip(Loc.T("关掉后只套动作，镜头自己摆"));
        if (walk) DrawWalkCamera(clip);
        DrawViewAdjust(clip);

        DrawFaceWeights(clip);
        using (ImRaii.Disabled(_player.CurrentPose() is null))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, Loc.T("存当前帧到 Brio 姿势库")))
            {
                var name = Races.Suffixed($"{(clip.BaseName.Length > 0 ? clip.BaseName : clip.Name)} {_player.Time:0.00}s", clip.Race);
                var path = BrioPoseLibrary.Save(_player.CurrentPose()!, name, clip.Race is null ? ["Video"] : ["Video", Races.Label(clip.Race)], null,
                                                Loc.F("Posegraph 视频片段 {0} 第 {1:0.00} 秒", clip.Name, _player.Time));
                SetStatus(Loc.F("已存到 {0}", path), Green);
            }
        UiText.SameLineIfFits(UiText.IconButtonWidth(Loc.T("片段文件夹")));
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderOpen, Loc.T("片段文件夹")))
        {
            Directory.CreateDirectory(Clip.Folder);
            Dalamud.Utility.Util.OpenLink(Clip.Folder);
        }
        recording.Dispose();
        DrawRecord(clip);
        if (_player.Error is { } error) ImGui.TextColored(Yellow, error);
    }

    private static readonly (WalkCamera Value, string Label, string Tip)[] WalkCameras =
    [
        (WalkCamera.Original, "原运镜", "原视频的镜头怎么动，游戏里就怎么动（推拉摇移都复刻），角色在场景里按视频走"),
        (WalkCamera.Fixed, "固定机位", "镜头停在开头的位置不动，角色在画面里走来走去"),
        (WalkCamera.Follow, "平滑跟拍", "保持开头的拍摄距离和角度，镜头带一点延迟跟着角色走；角色长时间转向新的方向时慢慢绕过去"),
        (WalkCamera.Pan, "定点摇镜", "机位停在开头的位置，只转镜头对准角色；走远了推近、走近了拉远，角色在画面里大小不变"),
    ];

    private static readonly (HiddenCamera Value, string Label)[] HiddenCameras =
    [
        (HiddenCamera.Original, "照原视频"),
        (HiddenCamera.Follow, "换成平滑跟拍"),
        (HiddenCamera.Pan, "换成定点摇镜"),
    ];

    /// <summary>走动模式：机位、原运镜拍不到人时怎么办、镜头解算的情况；旧片段只能固定机位（有原始数据时可以重算）。</summary>
    private void DrawWalkCamera(Clip clip)
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (!clip.HasWalkTracks)
        {
            ImGui.TextDisabled(Loc.T("这个片段是旧版本识别的：只有固定机位（把原视频当成没动过的镜头）"));
            if (clip.RawPath is not null && _player.Actor is { } actor && _main.InGpose)
            {
                ImGui.SameLine();
                using (ImRaii.Disabled(_rebuilding || _recorder.Running))
                    if (ImGui.SmallButton(Loc.T("重算走位")))
                        _ = Task.Run(() => RebuildAsync(clip, actor, Loc.T("已重算走位和镜头")));
                Tip(Loc.T("用存下的识别数据重算角色走位、脚底防滑和自动镜头（几秒，不重新识别）。\n旧数据里没有原视频镜头的运动，原运镜按固定机位算；要分出镜头运动请重新识别"));
            }
            return;
        }
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("机位"));
        ImGui.SameLine();
        var current = Array.FindIndex(WalkCameras, c => c.Value == _player.WalkCamera);
        ImGui.SetNextItemWidth(130 * scale);
        if (ImGui.BeginCombo("##walkCamera", Loc.T(WalkCameras[Math.Max(0, current)].Label)))
        {
            foreach (var (value, label, tip) in WalkCameras)
            {
                if (ImGui.Selectable(Loc.T(label), value == _player.WalkCamera))
                {
                    _player.WalkCamera = value;
                    _config.VideoWalkCamera = (int)value;
                    _pi.SavePluginConfig(_config);
                }
                Tip(Loc.T(tip));
            }
            ImGui.EndCombo();
        }
        if (_player.WalkCamera == WalkCamera.Original)
        {
            UiText.SameLineIfFits(ImGui.CalcTextSize(Loc.T("拍不到人时")).X + 150 * scale);
            ImGui.TextUnformatted(Loc.T("拍不到人时"));
            ImGui.SameLine();
            var hidden = Array.FindIndex(HiddenCameras, c => c.Value == _player.HiddenCamera);
            ImGui.SetNextItemWidth(140 * scale);
            if (ImGui.BeginCombo("##hiddenCamera", Loc.T(HiddenCameras[Math.Max(0, hidden)].Label)))
            {
                foreach (var (value, label) in HiddenCameras)
                    if (ImGui.Selectable(Loc.T(label), value == _player.HiddenCamera))
                    {
                        _player.HiddenCamera = value;
                        _config.VideoHiddenCamera = (int)value;
                        _pi.SavePluginConfig(_config);
                    }
                ImGui.EndCombo();
            }
            Tip(Loc.T("原视频镜头移开、画面里没有人的那几段：照原视频（游戏里那几秒也看不到角色），或者换成自动镜头对准角色，前后慢慢过渡"));
        }
        if (clip.Motion is { } m)
        {
            var how = m.Camera switch
            {
                "depth" => Loc.T("推拉摇移都解出来了"),
                "rotation" => Loc.T("只解出镜头转动（推拉移按没动算）"),
                _ => Loc.T("没解出来，按固定机位算"),
            };
            var text = Loc.F("原视频镜头：{0}，可信 {1:P0}", how, m.Solved);
            if (m.Breaks.Length > 0) text += Loc.F("；切镜 {0} 处（{1}）", m.Breaks.Length, string.Join("、", m.Breaks.Select(b => $"{b:0.0}s")));
            UiText.Fit(text, Grey);
            Tip(Loc.T("背景太单调（纯色墙）、大面积在动、切镜叠化时镜头会跟丢，那一步当作镜头没动；切镜处角色的位置和朝向接着上一个镜头"));
        }
    }

    private static readonly (ClipTarget Value, string Label)[] Targets =
    [
        (ClipTarget.Head, "头（脸）"),
        (ClipTarget.Chest, "胸口"),
        (ClipTarget.Hips, "腰"),
        (ClipTarget.Body, "全身中心"),
    ];

    /// <summary>
    /// 镜头调整（每个片段各存一份）：跟踪点、全身入画（只影响自动镜头）；距离、变焦、环绕、平移、摇镜、倾斜（所有模式）。
    /// 拖动时马上生效，松手后写进片段文件；右键单个滑条恢复默认。
    /// </summary>
    private void DrawViewAdjust(Clip clip)
    {
        var label = _player.View.Moves ? Loc.T("镜头调整（已调整）") : Loc.T("镜头调整");
        if (!ImGui.TreeNode(label + "###clipView")) return;
        var scale = ImGuiHelpers.GlobalScale;
        var v = _player.View;
        var commit = false;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.T("跟踪点"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * scale);
        var current = Array.FindIndex(Targets, x => x.Value == v.Target);
        if (ImGui.BeginCombo("##viewTarget", Loc.T(Targets[Math.Max(0, current)].Label)))
        {
            foreach (var (value, name) in Targets)
                if (ImGui.Selectable(Loc.T(name), value == v.Target))
                {
                    v = v with { Target = value };
                    commit = true;
                }
            ImGui.EndCombo();
        }
        Tip(Loc.T("自动镜头（平滑跟拍、定点摇镜、拍不到人时换上的自动镜头）对准角色的哪里，也是调整这些镜头时环绕、推近的中心"));
        ImGui.SameLine();
        var fit = v.FitBody;
        if (ImGui.Checkbox(Loc.T("全身入画"), ref fit))
        {
            v = v with { FitBody = fit };
            commit = true;
        }
        Tip(Loc.T("自动镜头按整个人的范围留距离：举手、踢腿时自动退一点（定点摇镜是放宽视角），手脚不出画"));

        bool Slider(string name, string id, float value, float min, float max, string format, float reset, string tip, out float result)
        {
            result = value;
            ImGui.SetNextItemWidth(220 * scale);
            var changed = ImGui.SliderFloat($"{Loc.T(name)}##{id}", ref result, min, max, format);
            commit |= ImGui.IsItemDeactivatedAfterEdit();
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && value != reset)
            {
                result = reset;
                changed = commit = true;
            }
            Tip(Loc.T(tip) + Loc.T("\n右键恢复默认"));
            return changed;
        }

        if (Slider("距离", "viewDistance", v.Distance, 0.3f, 3f, "%.2fx", 1f, "机位往中心点推近（小于 1）或拉远（大于 1），透视会变，像人走近拍", out var d)) v = v with { Distance = d };
        if (Slider("变焦", "viewZoom", v.Zoom, 0.5f, 4f, "%.2fx", 1f, "机位不动，只改视角：大于 1 画面放大、背景被压缩（像长焦），小于 1 更广", out var z)) v = v with { Zoom = z };
        if (Slider("环绕（左右）", "viewOrbitYaw", v.OrbitYaw, -180f, 180f, "%.0f°", 0, "机位绕中心点左右转，换个方向拍；正数往右绕", out var oy)) v = v with { OrbitYaw = oy };
        if (Slider("环绕（上下）", "viewOrbitPitch", v.OrbitPitch, -80f, 80f, "%.0f°", 0, "机位绕中心点上下转：正数抬高往下拍（俯拍），负数放低往上拍（仰拍）", out var op)) v = v with { OrbitPitch = op };
        if (Slider("左右平移", "viewShiftX", v.ShiftX, -3f, 3f, Loc.T("%.2f 米"), 0, "机位和镜头一起沿画面左右平移，角色在画面里往反方向移", out var sx)) v = v with { ShiftX = sx };
        if (Slider("上下平移", "viewShiftY", v.ShiftY, -3f, 3f, Loc.T("%.2f 米"), 0, "机位和镜头一起沿画面上下平移", out var sy)) v = v with { ShiftY = sy };
        if (Slider("镜头左右", "viewPan", v.Pan, -60f, 60f, "%.0f°", 0, "机位不动，镜头原地往左右看（摇镜）；正数往右", out var pan)) v = v with { Pan = pan };
        if (Slider("镜头上下", "viewTilt", v.Tilt, -45f, 45f, "%.0f°", 0, "机位不动，镜头原地往上下看；正数往上", out var tilt)) v = v with { Tilt = tilt };
        if (Slider("倾斜", "viewRoll", v.Roll, -45f, 45f, "%.0f°", 0, "画面歪一点（荷兰角）；正数顺时针", out var roll)) v = v with { Roll = roll };

        using (ImRaii.Disabled(!v.Moves))
            if (ImGui.SmallButton(Loc.T("重置镜头调整")))
            {
                v = ClipView.Default with { Target = v.Target, FitBody = v.FitBody };
                commit = true;
            }
        Tip(Loc.T("距离、变焦、环绕、平移、摇镜、倾斜都恢复默认（跟踪点、全身入画不变）"));
        ImGui.TextDisabled(_player.Mode == ClipCameraMode.Walk && _player.WalkCamera is WalkCamera.Follow or WalkCamera.Pan
            ? Loc.T("中心点：跟踪点")
            : Loc.T("中心点：镜头正前方、角色所在深度的那一点（跟踪点只影响自动镜头）"));
        ImGui.TreePop();

        if (v != _player.View) _player.View = v;
        if (!commit || clip.Path is not { } path) return;
        clip.View = v;
        _ = Task.Run(() =>
        {
            try { Clip.SaveView(path, v); }
            catch (Exception ex) { SetStatus(Loc.F("镜头调整没存上：{0}", ex.Message), Yellow); }
        });
    }

    /// <summary>录成视频：按钮、帧率、视频文件夹；录制中显示进度和取消。</summary>
    private void DrawRecord(Clip clip)
    {
        if (_recorder.Running)
        {
            var scale = ImGuiHelpers.GlobalScale;
            ImGui.ProgressBar(_recorder.Total == 0 ? 0 : (float)_recorder.Done / _recorder.Total, new Vector2(-90 * scale, 0),
                              Loc.F("录制中 {0}/{1}", _recorder.Done, _recorder.Total));
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("取消") + "##record", new Vector2(-1, 0))) _recorder.Cancel();
            return;
        }
        using (ImRaii.Disabled(!_main.InGpose || _player.Actor is null))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Video, Loc.T("录成视频")))
                StartRecording(clip);
        Tip(Loc.T(_config.ShotReShade
            ? "从头到尾一帧一帧摆好姿势和镜头再截图，编码成 mp4。录的时候比实时慢。\n构图窗口勾了「ReShade 兼容」：录制期间藏起游戏界面和插件窗口、拷屏幕上的画面（带 ReShade），游戏窗口不要被挡住"
            : "从头到尾一帧一帧摆好姿势和镜头再截图，编码成 mp4。录的时候比实时慢。\n录制期间藏起游戏界面；画面不带 ReShade（要带的话在构图窗口勾「ReShade 兼容」）"));
        ImGui.SameLine();
        var current = Math.Max(0, Array.IndexOf(RecordFpsChoices, _config.VideoRecordFps));
        string Label(int f) => f == 0 ? Loc.F("跟片段（{0:0.##} 帧/秒）", clip.Fps) : Loc.F("{0} 帧/秒", f);
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##recordFps", Label(RecordFpsChoices[current])))
        {
            foreach (var f in RecordFpsChoices)
                if (ImGui.Selectable(Label(f), f == RecordFpsChoices[current]))
                {
                    _config.VideoRecordFps = f;
                    _pi.SavePluginConfig(_config);
                }
            ImGui.EndCombo();
        }
        Tip(Loc.T("生成视频的帧率：帧之间按片段插值，30 / 60 更顺"));
        ImGui.SameLine();
        var music = _config.VideoMusic;
        if (ImGui.Checkbox(Loc.T("配音乐"), ref music))
        {
            _config.VideoMusic = music;
            _pi.SavePluginConfig(_config);
        }
        var source = MusicSource(clip);
        Tip(source is null
            ? Loc.T("把原视频的音乐按时间对齐配进去。这个片段没记原视频在哪（旧版本识别的），点右边选一个视频或音频文件")
            : Loc.F("把原视频的音乐按时间对齐配进去（从第 {0:0.0} 秒起）：{1}\n视频里没有声音时用同一文件夹里同名的音频文件（.m4a、.mp3 等）", clip.Start, source));
        if (music)
        {
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("##musicPick", FontAwesomeIcon.Music))
                _dialog.OpenFileDialog(Loc.T("选音乐"), Loc.T("视频或音频") + "{.mp4,.mkv,.mov,.webm,.m4a,.mp3,.aac,.wav,.ogg,.opus,.flac}", (ok, paths) =>
                {
                    if (ok && paths.Count > 0) _musicOverride = (clip.Path, paths[0]);
                }, 1, source is null ? _config.LastVideoDir : Path.GetDirectoryName(source));
            Tip(Loc.T("换一个音乐文件（只对这个片段）"));
        }
        UiText.SameLineIfFits(UiText.IconButtonWidth(Loc.T("视频文件夹")));
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderOpen, Loc.T("视频文件夹")))
        {
            Directory.CreateDirectory(VideosFolder);
            Dalamud.Utility.Util.OpenLink(VideosFolder);
        }
    }

    /// <summary>临时测试（/spike/record）：载入片段、选角色，录到视频文件夹，返回结果或错误。</summary>
    internal async Task<object> SpikeRecordAsync(IFramework framework, string clipPath, int actor, double fps, bool reshade)
    {
        var clip = Clip.Load(clipPath);
        _player.Load(clip, actor);
        await framework.DelayTicks(3);
        var (id, size) = await framework.RunOnFrameworkThread(() => (ImGui.GetMainViewport().ID, ImGui.GetMainViewport().Size));
        var path = Path.Combine(VideosFolder, $"spike_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _recorder.RecordAsync(_main.Client, _player, path, fps, reshade, id, size);
            return new { result.Path, result.Frames, result.Codec, seconds = clock.Elapsed.TotalSeconds, viewport = new[] { size.X, size.Y } };
        }
        catch (Exception ex)
        {
            return new { error = ex.ToString() };
        }
    }

    private void StartRecording(Clip clip)
    {
        var viewport = ImGui.GetMainViewport();
        var (viewportId, viewportSize) = (viewport.ID, viewport.Size);
        var fps = _config.VideoRecordFps > 0 ? _config.VideoRecordFps : clip.Fps;
        var reshade = _config.ShotReShade;
        var music = _config.VideoMusic ? MusicSource(clip) : null;
        var safe = string.Concat(clip.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var path = Path.Combine(VideosFolder, $"{(safe.Length > 0 ? safe : "clip")}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        SetStatus(reshade ? Loc.T("录制中…（ReShade 兼容：插件窗口先藏起来，录完自动回来）") : Loc.T("录制中…"), Yellow);
        Task.Run(async () =>
        {
            try
            {
                var result = await _recorder.RecordAsync(_main.Client, _player, path, fps, reshade, viewportId, viewportSize, music);
                SetStatus(Loc.F("已录好 {0}（{1} 帧）", Path.GetFileName(result.Path), result.Frames)
                          + (result.MusicSeconds > 0 ? Loc.T("，带音乐") : "")
                          + (result.Codec == "H264" ? "" : Loc.T("；这台电脑没有 H.264 编码器，用的是 MPEG-4，个别播放器放不了"))
                          + (string.IsNullOrEmpty(result.Warning) ? "" : "；" + result.Warning),
                          string.IsNullOrEmpty(result.Warning) ? Green : Yellow);
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc.T("已取消录制"), Grey);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.F("录制失败：{0}", ex.Message), Yellow);
            }
        });
    }

    /// <summary>
    /// 表情权重（同主窗口的面部权重）：强度 × 分项（眼睑、眉毛、嘴部、下巴、视线）。下次识别按它算；
    /// 当前片段有识别原始数据时，松开滑条就按新权重重算（几秒）。
    /// </summary>
    private void DrawFaceWeights(Clip clip)
    {
        if (!_config.VideoFace) return;
        if (!ImGui.TreeNode(Loc.T("表情权重") + "###videoFaceWeights")) return;
        var changed = false;
        var strength = _config.VideoFaceStrength;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat(Loc.T("强度") + "##videoFace", ref strength, 0f, 2f, "%.2f")) _config.VideoFaceStrength = strength;
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        Tip(Loc.T("所有表情的幅度倍数：视频里本来就比照片保守（再打七折），不明显时调大，夸张时调小"));
        foreach (var (key, label, tip) in MainWindow.FaceGroups)
        {
            var v = _config.VideoFaceGains.TryGetValue(key, out var g) ? g : 1f;
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderFloat($"{Loc.T(label)}##videoFace{key}", ref v, 0f, 2f, "%.2f")) _config.VideoFaceGains[key] = v;
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            Tip(Loc.T(tip) + Loc.T("\n和上面的强度相乘；再大也不会超过游戏里这块能动的范围"));
        }
        if (clip.RawPath is null)
            ImGui.TextDisabled(Loc.T("这个片段没有识别原始数据（旧版本识别的）：权重只对下次识别生效"));
        else if (!clip.People.Any(p => p.Bones.Any(b => b.StartsWith("j_f_"))))
            ImGui.TextDisabled(Loc.T("这个片段识别时没开表情：权重只对下次识别生效"));
        ImGui.TreePop();
        if (!changed) return;
        _pi.SavePluginConfig(_config);
        if (clip.RawPath is not null && _player.Actor is { } actor && _main.InGpose && !_recorder.Running)
            _ = Task.Run(() => RebuildAsync(clip, actor, Loc.T("已按新的表情权重重算")));
    }

    /// <summary>播放时放音乐：开关、音量；没有音乐来源时提示去选。</summary>
    private void DrawPlayMusic(Clip clip)
    {
        ImGui.SameLine();
        var on = _config.VideoPlayMusic;
        if (ImGui.Checkbox(Loc.T("放音乐"), ref on))
        {
            _audio.Enabled = _config.VideoPlayMusic = on;
            _pi.SavePluginConfig(_config);
        }
        var source = MusicSource(clip);
        Tip(source is null
            ? Loc.T("播放时同步放原视频的音乐。这个片段没记原视频在哪（旧版本识别的），在下面「配音乐」旁边选一个视频或音频文件")
            : Loc.F("播放时同步放原视频的音乐（按片段时间对齐，拖进度、循环、调速都跟着；调速时音调会跟着变）：{0}", source));
        if (!on) return;
        ImGui.SameLine();
        var volume = _config.VideoMusicVolume;
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##musicVolume", ref volume, 0f, 1.5f, Loc.T("音量 %.1f"))) _audio.Volume = _config.VideoMusicVolume = volume;
        if (ImGui.IsItemDeactivatedAfterEdit()) _pi.SavePluginConfig(_config);
        if (source is null) ImGui.TextColored(Yellow, Loc.T("没找到原视频：在下面「配音乐」旁边的音符按钮选音乐文件"));
        else if (_audio.Error is { } error) ImGui.TextColored(Yellow, Loc.F("放不了音乐：{0}", error));
    }

    /// <summary>配音乐的来源：这个片段手动选过的文件，否则原视频（片段里记的完整路径，文件还在时）。</summary>
    private string? MusicSource(Clip clip)
    {
        if (_musicOverride is { } o && o.Clip == clip.Path && File.Exists(o.Path)) return o.Path;
        return clip.VideoPath is { } v && File.Exists(v) ? v : null;
    }

    private void SetWalk(bool walk)
    {
        _player.SetMode(walk ? ClipCameraMode.Walk : ClipCameraMode.Follow);
        _config.VideoWalk = walk;
        _pi.SavePluginConfig(_config);
    }

    private void SetStatus(string text, Vector4 color) => (_status, _statusColor) = (text, color);

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }

    public void Dispose()
    {
        _audio.Dispose();
        _player.Dispose();
        _frame?.Dispose();
    }
}
