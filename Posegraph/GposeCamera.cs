using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace Posegraph;

/// <summary>
/// GPose 活动相机的字段布局。偏移与 Brio 0.8（Brio/Game/Camera/BrioCamera.cs，
/// 部分来自 Lights, Camera, Action by NeNeppie）一致。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
public struct GposeCameraStruct
{
    [FieldOffset(0x000)] public GameCamera Camera;
    [FieldOffset(0x124)] public float Distance;
    [FieldOffset(0x128)] public float MinDistance;
    [FieldOffset(0x12C)] public float MaxDistance;
    [FieldOffset(0x130)] public float FoV;
    [FieldOffset(0x13C)] public float Zoom;      // 叠加在 FoV 上的视角增量（弧度），GPose 默认 0
    [FieldOffset(0x140)] public Vector2 Angle;   // 绕目标的水平 / 俯仰角
    [FieldOffset(0x160)] public Vector2 Pan;     // 相机不动、视线的水平 / 垂直偏转
    [FieldOffset(0x170)] public float Roll;
}

/// <summary>读写 GPose 相机。所有方法必须在游戏主线程调用。</summary>
public static unsafe class GposeCamera
{
    private static GposeCameraStruct* Active => (GposeCameraStruct*)CameraManager.Instance()->GetActiveCamera();

    public static object? Read()
    {
        var cam = Active;
        if (cam == null) return null;
        var scene = cam->Camera.CameraBase.SceneCamera;
        var view = scene.ViewMatrix;
        view.M44 = 1;
        Matrix4x4.Invert(view, out var world);
        var render = scene.RenderCamera;
        return new
        {
            angle = new[] { cam->Angle.X, cam->Angle.Y },
            pan = new[] { cam->Pan.X, cam->Pan.Y },
            roll = cam->Roll,
            distance = cam->Distance,
            minDistance = cam->MinDistance,
            maxDistance = cam->MaxDistance,
            fov = cam->FoV,
            zoom = cam->Zoom,
            position = new[] { world.M41, world.M42, world.M43 },
            lookAt = new[] { scene.LookAtVector.X, scene.LookAtVector.Y, scene.LookAtVector.Z },
            view = Rows(view),
            projection = render != null ? Rows(render->ProjectionMatrix) : null,
        };
    }

    /// <summary>当前 GPose 相机在世界里的位置（灯光按它摆）；不在 GPose 时为 null。</summary>
    public static Vector3? WorldPosition()
    {
        var cam = Active;
        if (cam == null) return null;
        var view = cam->Camera.CameraBase.SceneCamera.ViewMatrix;
        view.M44 = 1;
        return Matrix4x4.Invert(view, out var world) ? new Vector3(world.M41, world.M42, world.M43) : null;
    }

    /// <summary>当前的垂直视角（弧度，按投影矩阵）、画面宽高比和 Zoom；不在 GPose 时为 null。</summary>
    public static (float FovY, float Aspect, float Zoom)? Projection()
    {
        var cam = Active;
        if (cam == null) return null;
        var render = cam->Camera.CameraBase.SceneCamera.RenderCamera;
        if (render == null) return null;
        var p = render->ProjectionMatrix;
        return (2 * MathF.Atan(1 / p.M22), p.M22 / p.M11, cam->Zoom);
    }

    /// <summary>GPose 相机在世界里的位置、视线、上方向、垂直视角（弧度）和画面宽高比（灯光 3D 视图画镜头用）。</summary>
    public static CameraPose? Pose()
    {
        var cam = Active;
        if (cam == null || Projection() is not { } p) return null;
        var view = cam->Camera.CameraBase.SceneCamera.ViewMatrix;
        view.M44 = 1;
        if (!Matrix4x4.Invert(view, out var w)) return null;
        // 行向量约定：逆矩阵的三行是相机的右、上、后方向
        return new CameraPose(new Vector3(w.M41, w.M42, w.M43), -Vector3.Normalize(new Vector3(w.M31, w.M32, w.M33)),
                              Vector3.Normalize(new Vector3(w.M21, w.M22, w.M23)), p.FovY, p.Aspect);
    }

    public static Vector2 ReadAngle() => Active == null ? default : Active->Angle;

    public static Vector2 ReadPan() => Active == null ? default : Active->Pan;

    public static float ReadDistance() => Active == null ? 0 : Active->Distance;

    /// <summary>原型：视角直接到位（0x1A4 是游戏平滑中的当前视角，目标 = FoV + Zoom）。</summary>
    public static void SnapZoom(float zoom)
    {
        var cam = Active;
        if (cam == null) return;
        cam->Zoom = zoom;
        *(float*)((byte*)cam + 0x1A4) = cam->FoV + zoom;
    }

    /// <summary>原型：导出活动相机结构的前 size 字节（按 float 读）。</summary>
    public static float[] DumpFloats(int size)
    {
        var cam = (float*)Active;
        if (cam == null) return [];
        var result = new float[size / 4];
        for (var i = 0; i < result.Length; i++) result[i] = float.IsFinite(cam[i]) ? cam[i] : 0;
        return result;
    }

    /// <summary>原型：距离直接到位（同时写游戏平滑用的 InterpDistance）。</summary>
    public static void SnapDistance(float distance)
    {
        var cam = Active;
        if (cam == null) return;
        cam->Distance = distance;
        cam->Camera.InterpDistance = distance;
    }

    private const int CurrentFovOffset = 0x1A4;   // 游戏平滑中的当前视角（弧度），目标是 FoV + Zoom
    private const float MaxPitchParam = 1.4f;      // |Angle.y| 再大时轴心会偏（相机接近正上 / 正下）
    private const float MinFov = 0.22f;            // 视角下限约 12.6°

    /// <summary>
    /// 把 GPose 相机放到世界里的 position、垂直视角 fovY（弧度），当帧生效（视频逐帧播放用）。
    /// 相机绕轴心转：位置 = 轴心 + Distance × 方向(Angle.x, -Angle.y)，轴心由当前相机反推（见 backend/src/posegraph/camera.py）。
    /// 距离、视角在写目标值的同时写游戏平滑用的当前值（InterpDistance、0x1A4），跳过约 0.5 秒的平滑。视线方向由 SteerView 负责。
    /// </summary>
    public static bool PlacePosition(Vector3 position, float fovY)
    {
        var cam = Active;
        if (cam == null || Pose() is not { } now) return false;
        var pivot = now.Position - cam->Distance * Direction(cam->Angle.X, -cam->Angle.Y);
        var d = position - pivot;
        var distance = d.Length();
        if (distance < 1e-3f) return false;
        cam->MinDistance = 0f;
        cam->MaxDistance = 500f;
        cam->Distance = distance;
        cam->Camera.InterpDistance = distance;
        cam->Angle = new Vector2(MathF.Atan2(d.X, d.Z), Math.Clamp(-MathF.Asin(Math.Clamp(d.Y / distance, -1f, 1f)), -MaxPitchParam, MaxPitchParam));
        var zoom = MathF.Max(fovY, MinFov) - cam->FoV;
        cam->Zoom = zoom;
        *(float*)((byte*)cam + CurrentFovOffset) = cam->FoV + zoom;
        return true;
    }

    /// <summary>
    /// 把视线往 forward / up 拧一步（闭环）：读这一帧实际画出来的朝向，按误差改 Pan / Roll，下一帧生效。
    /// Pan 不是简单的世界偏航 / 俯仰：Pan.x 绕随俯仰倾斜的轴转，Pan.y 和俯仰也不是 1:1（游戏内实测），
    /// 所以不直接解，而是每帧修正：世界偏航 ≈ 基准 − Pan.x、俯仰 ≈ 基准 + 0.9 × Pan.y、滚转 = −Roll。
    /// 播放时镜头连续移动，每帧的差很小，几帧内收敛。返回修正前的视线误差（度）。
    /// </summary>
    public static float SteerView(Vector3 forward, Vector3 up)
    {
        var cam = Active;
        if (cam == null || Pose() is not { } now) return 0;
        var (yaw, pitch, roll) = ViewAngles(now.Forward, now.Up);
        var (goalYaw, goalPitch, goalRoll) = ViewAngles(forward, up);
        cam->Pan = new Vector2(Wrap(cam->Pan.X + Wrap(yaw - goalYaw)), Math.Clamp(cam->Pan.Y + (goalPitch - pitch) / 0.9f, -1.5f, 1.5f));
        cam->Roll = Wrap(cam->Roll + Wrap(roll - goalRoll));
        var cos = Vector3.Dot(Vector3.Normalize(now.Forward), Vector3.Normalize(forward));
        return MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>相机朝向的世界角（偏航、俯仰、滚转），与 GPose 的 Pan / Roll 同一种分解（相机看 -z，先偏航再俯仰再滚转）。</summary>
    public static (float Yaw, float Pitch, float Roll) ViewAngles(Vector3 forward, Vector3 up)
    {
        var back = -Vector3.Normalize(forward);
        var right = Vector3.Normalize(Vector3.Cross(up, back));
        var up2 = Vector3.Cross(back, right);
        return (MathF.Atan2(back.X, back.Z), MathF.Asin(Math.Clamp(-back.Y, -1f, 1f)), MathF.Atan2(right.Y, up2.Y));
    }

    private static Vector3 Direction(float yaw, float elevation)
        => new(MathF.Sin(yaw) * MathF.Cos(elevation), MathF.Sin(elevation), MathF.Cos(yaw) * MathF.Cos(elevation));

    private static float Wrap(float a) => a - MathF.Tau * MathF.Floor((a + MathF.PI) / MathF.Tau);

    /// <summary>改 Zoom（垂直视角 = 常数 + Zoom，游戏下限约 12.6°）。</summary>
    public static void SetZoom(float zoom)
    {
        var cam = Active;
        if (cam != null) cam->Zoom = zoom;
    }

    public static bool Write(CameraRequest req)
    {
        var cam = Active;
        if (cam == null) return false;
        if (req.Delimit == true)
        {
            cam->MinDistance = 0f;
            cam->MaxDistance = 500f;
        }
        if (req.Distance is { } d) cam->Distance = d;
        if (req.Angle is { Length: 2 } a) cam->Angle = new Vector2(a[0], a[1]);
        if (req.Pan is { Length: 2 } p) cam->Pan = new Vector2(p[0], p[1]);
        if (req.Roll is { } r) cam->Roll = r;
        if (req.Zoom is { } z) cam->Zoom = z;
        return true;
    }

    private static float[][] Rows(Matrix4x4 m) =>
    [
        [m.M11, m.M12, m.M13, m.M14],
        [m.M21, m.M22, m.M23, m.M24],
        [m.M31, m.M32, m.M33, m.M34],
        [m.M41, m.M42, m.M43, m.M44],
    ];
}

public readonly record struct CameraPose(Vector3 Position, Vector3 Forward, Vector3 Up, float FovY, float Aspect);

public sealed class CameraRequest
{
    public float? Distance { get; set; }
    public float[]? Angle { get; set; }
    public float[]? Pan { get; set; }
    public float? Roll { get; set; }
    public float? Zoom { get; set; }
    /// <summary>解除距离上下限（GPose 默认 1.5~20）。</summary>
    public bool? Delimit { get; set; }
}
