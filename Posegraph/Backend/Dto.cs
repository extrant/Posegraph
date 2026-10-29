using System.Text.Json;
using System.Text.Json.Serialization;

namespace Posegraph.Backend;

/// <summary>重定向参数，字段与后端 posegraph.tuning.RetargetParams 一致。</summary>
public sealed class RetargetParams
{
    [JsonPropertyName("amplitude")] public Dictionary<string, float> Amplitude { get; set; } = new();
    [JsonPropertyName("offsets")] public Dictionary<string, float[]> Offsets { get; set; } = new();
    [JsonPropertyName("hand_strength")] public float HandStrength { get; set; } = 1f;
    [JsonPropertyName("hand_near")] public float HandNear { get; set; } = 0.20f;
    [JsonPropertyName("hand_far")] public float HandFar { get; set; } = 0.40f;
    [JsonPropertyName("upright")] public bool Upright { get; set; } = true;
    [JsonPropertyName("upright_max_deg")] public float UprightMaxDeg { get; set; } = 30f;
    [JsonPropertyName("ground")] public bool Ground { get; set; } = true;
    /// <summary>踩地的脚放平（只转脚，不动腿）：识别出的脚常脚尖微翘、只有脚跟着地。</summary>
    [JsonPropertyName("flat_feet")] public bool FlatFeet { get; set; } = true;
    [JsonPropertyName("helper_strength")] public float HelperStrength { get; set; } = 1f;
    /// <summary>整体旋转（度，模型系 x 俯仰 / y 水平转 / z 侧倾，绕髋部）。</summary>
    [JsonPropertyName("root_rotation")] public float[] RootRotation { get; set; } = new float[3];
    /// <summary>整体位移（米，模型系 x 左右 / y 上下 / z 前后）；复刻站位不会抵消它。</summary>
    [JsonPropertyName("root_offset")] public float[] RootOffset { get; set; } = new float[3];
    /// <summary>把照片表情套到脸部骨骼（后端 face_rig.py，需要先 /posegraph facedump 导出游戏表情数据）。</summary>
    [JsonPropertyName("face")] public bool Face { get; set; }
    [JsonPropertyName("face_strength")] public float FaceStrength { get; set; } = 1f;
    /// <summary>表情分组权重（eyes 眼睑 / brows 眉毛 / mouth 嘴部 / jaw 下巴 / gaze 视线），和强度相乘，缺省 1。</summary>
    [JsonPropertyName("face_gains")] public Dictionary<string, float> FaceGains { get; set; } = new();
    /// <summary>头部朝向跟面捕的脸走多少（0 = 只用识别的头，1 = 完全按脸）；不开表情也生效。</summary>
    [JsonPropertyName("face_head")] public float FaceHead { get; set; } = 0.5f;

    public float FaceGain(string group) => FaceGains.TryGetValue(group, out var v) ? v : 1f;

    public float Amp(string group) => Amplitude.TryGetValue(group, out var v) ? v : 1f;

    public RetargetParams Clone() => new()
    {
        Amplitude = new(Amplitude),
        Offsets = Offsets.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone()),
        HandStrength = HandStrength, HandNear = HandNear, HandFar = HandFar,
        Upright = Upright, UprightMaxDeg = UprightMaxDeg, Ground = Ground, FlatFeet = FlatFeet, HelperStrength = HelperStrength,
        RootRotation = (float[])RootRotation.Clone(), RootOffset = (float[])RootOffset.Clone(),
        Face = Face, FaceStrength = FaceStrength, FaceGains = new(FaceGains), FaceHead = FaceHead,
    };

    public static RetargetParams Preset(string name)
    {
        var p = new RetargetParams();
        if (name == "illustration")
        {
            // 插画的头颈比例夸张，识别容易把头颈角度估大（游戏实测 0.7 合适）
            p.Amplitude["neck"] = 0.7f;
            p.Amplitude["head"] = 0.7f;
        }
        return p;
    }
}

public sealed record HumanInfo(float[] Bbox);

public sealed record SessionInfo(string SessionId, string Name, int[] ImageSize, int InferenceMs, string? Device, HumanInfo[] Humans);

/// <summary>合并模式：图里找到的一个人。Bbox 为原图像素 [x1, y1, x2, y2]；Source：photo（真人模型）/ anime（插画模型）。</summary>
public sealed record DetectedPerson(float[] Bbox, float Score, string Source);

/// <summary>复刻站位的一个人：会话里的序号、应用时用的参数、角色的游戏姿势（含模型位置）、骨架、骨长是否信游戏值。</summary>
public sealed record PlacementInput(int Person, RetargetParams Params, string? Reference, string? Race, bool Trust, ModelPose? Model);

/// <summary>角色的模型变换（Brio GetModelTransform）：世界位置、旋转（四元数 xyzw）、缩放。</summary>
public sealed record ModelPose(float[] Position, float[] Rotation, float[] Scale);

/// <summary>相对基准角色（基准角色模型系：X 右 / Y 上 / Z 前，米）的位置与朝向差。</summary>
public sealed record PlacementOffset(float X, float Y, float Z, float YawDeg);

/// <summary>一个人的角色应在的世界位置、朝向（四元数 xyzw）；FramingErrorPct：用基准人物复刻镜头时这个人与照片的构图偏差。</summary>
public sealed record PlacedPerson(int Person, float[] Position, float[] Rotation, float[] Scale, PlacementOffset Relative, float? FramingErrorPct);

public sealed record PlacementResult(PlacedPerson[] People);

/// <summary>
/// 合并模式的人物遮罩（GET /sessions/{sid}/masks）：Labels 每个像素一个字节（0 没人，k + 1 为会话里第 k 个人），
/// Image 为同尺寸的照片 RGB（魔棒按它取色）。尺寸是原图等比缩到长边不超过 1280。
/// </summary>
public sealed record MaskData(int Width, int Height, int Count, byte[] Labels, byte[] Image);

/// <summary>POST /detect 的结果：人物从左到右编号；Kind 为实际用的检测模型。</summary>
public sealed record DetectResult(int[] ImageSize, DetectedPerson[] People, string Kind, float ElapsedMs);

public sealed record Segment(float[] A, float[] B, string Side);

public sealed record Overlay(Segment[] Mhr, Segment[] Ffxiv);

public sealed record StickJoint(string Name, float[] Pos, string? Parent);

/// <summary>
/// 面捕结果（/sessions/{sid}/face）：52 个表情系数（ARKit 命名）、头部朝向和置信度。
/// Used 为 false 时置信度太低（Reason 说明原因），不要用；Available 为 false 时后端没装面捕组件。
/// </summary>
public sealed record FaceResult(bool Available, bool Found, float Confidence, bool Used, string Reason,
                                Dictionary<string, float> Blendshapes, float YawDeg = 0, float PitchDeg = 0);

/// <summary>照片里的一种颜色：Rgb 线性、最大分量 1；Tinted：离色温线远（彩色灯、霓虹、彩色背景），该直接用 Rgb；
/// Brightness 只有背景色有：背景亮度 / 脸的亮度。</summary>
public sealed record PhotoColor(float Kelvin, float[]? Rgb, bool Tinted, float Brightness = 0);

/// <summary>
/// 照片同款灯光（backend lighting.py）：从照片里人物的脸估出的灯。方位、仰角和灯光方案（LightSpec）同一套约定；
/// Power 以主光为 1；Softness 0～1.5，越大光越柔；色温只分得出冷暖。
/// </summary>
public sealed record PhotoLight(float Azimuth, float Elevation, float Power, float Softness, float Kelvin, float[]? Rgb = null, bool Tinted = false)
{
    public PhotoColor Color => new(Kelvin, Rgb, Tinted);
}

/// <summary>Flat：光比接近 1:1（正面闪光、大面积柔光），方向没有意义；Ambient：环境光相对主光的强度；Ratio：左右脸光比；
/// AmbientColor：环境光的颜色；Background：照片背景的颜色和亮度（人占满画面时为 null）；
/// Source：face（按脸估的）/ body（没有脸，按身上露出的皮肤估的）/ coarse（皮肤也少，按衣服的明暗强制估的）。</summary>
public sealed record PhotoLighting(bool Found, string Reason, bool Flat = false, float Ratio = 1, float Ambient = 0, PhotoLight[]? Lights = null,
                                   PhotoColor? AmbientColor = null, PhotoColor? Background = null, string Source = "face");

public sealed record Stick(StickJoint[] Joints, Segment[] Mhr);

public sealed record PoseWarning(string Bone, string Level, float Value, float Limit, string Message, string? Group, float? Suggest);

/// <summary>后端实际使用的骨架。Source：plugin（插件识别）/ guess（按骨长推断）/ default。</summary>
public sealed record RaceUsed(string Code, string Name, string Group, bool Validated, string Source, string[] Warnings);

public sealed record RetargetResult(
    string Pose, PoseWarning[] Warnings, Dictionary<string, string> Severity,
    JsonElement Compensation, Overlay? Overlay, Stick? Stick, float ElapsedMs, RaceUsed? Race, bool ReferenceStale = false,
    bool? Ivcs = null,    // 游戏角色是否加载了 IVCS 骨骼（读到游戏姿势时才有）
    OnionPerson[]? Onion = null,    // 合并模式 3D 页的洋葱皮：其他人的骨架（已按站位放进这个人的模型系）
    FacePoseInfo? Face = null,      // 套用照片表情的结果（参数里开了 face 才有）
    float? HeadFromFaceDeg = null); // 头部按面捕的脸修正时，脸和识别的头差多少度（没用上为 null）

/// <summary>Brio 改动换成的关节偏移：Offsets 是完整的新偏移（替换原来的），Changed 改了哪些骨骼，ResidualDeg 收完后还差的最大角度。</summary>
public sealed record BrioEditsResult(Dictionary<string, float[]> Offsets, string[] Changed, float ResidualDeg);

/// <summary>套用照片表情的结果：用的面部骨架、各表情通道的权重；没套上时 Reason 说明原因，NeedsDump 为要先导出游戏表情数据。</summary>
public sealed record FacePoseInfo(bool Applied, bool NeedsDump, string Reason, string? Skeleton = null,
                                  Dictionary<string, float>? Channels = null);

/// <summary>洋葱皮里的一个人：会话里的序号和骨架关节。</summary>
public sealed record OnionPerson(int Person, StickJoint[] Joints);

/// <summary>
/// 一张辅助视角图相对主图的情况（backend multiview.describe）。Yaw：机位绕人物转了多少度（正 = 人物右侧）；
/// View：same / left / right / back；Share：主要骨段平均采纳了多少；Conflict：主要骨段画面方向与主图平均差几度，Worst 为差得最多的骨段。
/// </summary>
public sealed record AuxView(float Yaw, string View, float Share, float Conflict, string Worst, float WorstDeg);

public sealed record ViewsResult(AuxView[] Views);

public sealed record CameraResult(float PositionErrorCm, float ForwardErrorDeg, float FovDeg, float TargetFovDeg,
                                  float FramingErrorPct, string[] Log);

public sealed record GpuInfo(string Name, int FreeMB, int TotalMB, int UsedMB);

/// <summary>Device / Precision 为当前加载的实际值，DeviceReason 为"自动"档的选择原因。</summary>
public sealed record ModelState(bool Loaded, bool Loading, string? Error, string? Device, string? Precision, string? DeviceReason);

public sealed record Health(string Status, string Version, ModelState Model, GpuInfo? Gpu, int Sessions,
                           Dictionary<string, bool>? Detectors = null);   // 人物检测模型是否就绪（photo / anime），旧后端没有
