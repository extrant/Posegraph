using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Posegraph.Video;

/// <summary>
/// 时间轴修正的一个关键帧（和后端 edits.Key 同一个格式）：在 Time 秒把 Person 这个人的几根骨骼改掉。
/// Kind：delta = 相对父骨骼的改动量（跟着原动作走，Brio 里改了收进来的、「粘贴改动」都是这种）；
/// pose = 相对父骨骼的朝向，原样贴上（「粘贴骨骼姿势」）。
/// Mode：ease = 前后 Ease 秒渐入渐出；hold = 保持到这根骨骼的下一个关键帧。
/// 存在片段文件的 meta.edits 里，生成片段时后端按帧插值施加（辅助骨照样联动）。
/// </summary>
public sealed class ClipKey
{
    [JsonPropertyName("person")] public int Person { get; set; }
    [JsonPropertyName("time")] public double Time { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "delta";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "ease";
    [JsonPropertyName("ease")] public double Ease { get; set; } = 0.5;
    [JsonPropertyName("bones")] public Dictionary<string, float[]> Bones { get; set; } = new();

    public bool Hold => Mode == "hold";

    public ClipKey Clone() => new()
    {
        Person = Person, Time = Time, Kind = Kind, Mode = Mode, Ease = Ease,
        Bones = Bones.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone()),
    };

    public JsonObject ToJson()
    {
        var bones = new JsonObject();
        foreach (var (name, q) in Bones) bones[name] = new JsonArray(q.Select(v => (JsonNode)v).ToArray());
        return new JsonObject
        {
            ["person"] = Person, ["time"] = Math.Round(Time, 6), ["kind"] = Kind, ["mode"] = Mode, ["ease"] = Math.Round(Ease, 4),
            ["bones"] = bones,
        };
    }

    public static ClipKey? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o || o["time"] is null || o["bones"] is not JsonObject bones) return null;
        var key = new ClipKey
        {
            Person = (int?)o["person"] ?? 0,
            Time = (double)o["time"]!,
            Kind = (string?)o["kind"] == "pose" ? "pose" : "delta",
            Mode = (string?)o["mode"] == "hold" ? "hold" : "ease",
            Ease = Math.Max(0, (double?)o["ease"] ?? 0.5),
        };
        foreach (var (name, q) in bones)
            if (q is JsonArray a && a.Count == 4)
                key.Bones[name] = a.Select(v => (float)(double)v!).ToArray();
        return key.Bones.Count > 0 ? key : null;
    }

    /// <summary>这个关键帧影响到的时间段（秒），和后端 edits.span 一样：渐入开始 ~ 渐出结束（hold 到这些骨骼的下一个关键帧），
    /// 和前后连着的关键帧之间的过渡段也算。改、删、挪这个关键帧时只重算这一段。</summary>
    public (double Start, double End) Span(IEnumerable<ClipKey> all, double duration)
    {
        var others = all.Where(k => k != this && k.Person == Person && k.Bones.Keys.Intersect(Bones.Keys).Any()).ToList();
        var start = Time - Ease;
        var end = Hold ? others.Where(k => k.Time > Time + 1e-6).Select(k => k.Time).DefaultIfEmpty(duration).Min() : Time + Ease;
        foreach (var k in others)
        {
            if (k.Time < Time && (k.Hold || Time - k.Time <= k.Ease + Ease)) start = Math.Min(start, k.Time);
            if (k.Time > Time && (Hold || k.Time - Time <= k.Ease + Ease)) end = Math.Max(end, k.Time);
        }
        return (Math.Max(0, start), Math.Min(duration, end));
    }
}

/// <summary>剪贴板：复制的修正（delta：某个关键帧的改动量；pose：某一帧几根骨骼的朝向）。</summary>
public sealed record ClipClipboard(string Kind, Dictionary<string, float[]> Bones, string Label);
