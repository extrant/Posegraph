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
