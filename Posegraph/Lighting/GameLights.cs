using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using SceneLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;
using RenderLight = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Light;

namespace Posegraph.Lighting;

/// <summary>游戏灯的种类（和游戏里 LightShape 的值一致）。</summary>
public enum LightKind : uint
{
    Point = LightShape.PointLight,   // 向四周发光
    Spot = LightShape.SpotLight,     // 沿自身 +Z 方向的锥形光
    Flat = LightShape.FlatLight,     // 沿自身 +Z 方向的平行四边形面光
}

/// <summary>一盏灯的世界状态。Color 是线性 RGB、不封顶：大小当亮度、比例当色相（游戏默认约 20）。</summary>
public sealed record LightState(
    LightKind Kind, Vector3 Position, Quaternion Rotation, Vector3 Color, float Range,
    float SpotAngle = 45f, float Softness = 0.5f, bool Shadows = false, float Falloff = 1f);

/// <summary>
/// Posegraph 自己生成的游戏灯（只用 FFXIVClientStructs 公开的结构和函数）。
/// 所有方法都必须在游戏主线程调用；退出 GPose 前要 DestroyAll（游戏切场景时这些灯会失效）。
/// Brio 会把这些灯列进它的「世界灯」候选里，但不会自动接管。
/// </summary>
public sealed unsafe class GameLights : IDisposable
{
    private readonly List<nint> _lights = [];

    public int Count => _lights.Count;

    public nint Spawn(LightState state)
    {
        var light = SceneLight.Create((LightShape)state.Kind, default(InteropGenerator.Runtime.CStringPointer), null);   // 不指定灯池名（同 GPose 里新建的灯）
        if (light == null) return 0;
        if (light->RenderLight == null)
        {
            Free(light);
            return 0;
        }
        var render = light->RenderLight;
        render->Transform = (Transform*)&light->Position;     // 渲染用的灯读场景对象自己的位置 / 朝向 / 缩放
        render->MaxRange = RenderLight.UnlimitedMaxRange;      // 和 GPose 自带的灯一样不按范围剔除
        render->FalloffType = LightFalloffType.Quadratic;
        render->FlatLightSkewAngleDegrees = Vector2.Zero;
        render->CharacterShadowRange = 110f;
        render->ShadowPlaneNear = 0.01f;
        render->ShadowPlaneFar = 17f;
        Apply(light, state);
        _lights.Add((nint)light);
        return (nint)light;
    }

    /// <summary>改一盏已生成的灯；不是本类生成的或已经销毁的忽略。</summary>
    public bool Update(nint handle, LightState state)
    {
        if (!_lights.Contains(handle)) return false;
        Apply((SceneLight*)handle, state);
        return true;
    }

    public void SetVisible(nint handle, bool visible)
    {
        if (_lights.Contains(handle)) ((SceneLight*)handle)->IsVisible = visible;
    }

    public void Destroy(nint handle)
    {
        if (_lights.Remove(handle)) Free((SceneLight*)handle);
    }

    public void DestroyAll()
    {
        foreach (var handle in _lights) Free((SceneLight*)handle);
        _lights.Clear();
    }

    public void Dispose() => DestroyAll();

    private static void Apply(SceneLight* light, LightState s)
    {
        light->Position = s.Position;
        light->Rotation = s.Rotation;
        light->Scale = Vector3.One;
        var render = light->RenderLight;
        render->LightShape = (LightShape)s.Kind;
        render->Color = s.Color;
        render->Intensity = 1f;
        render->Range = s.Range;
        render->FalloffFactor = s.Falloff;
        render->SpotLightAngleDegrees = s.SpotAngle;
        render->AngularFalloffDegrees = s.Softness;
        render->LightFlags = LightFlags.SpecularHighlights | (s.Shadows ? LightFlags.DynamicShadows | LightFlags.CharacterShadows : 0);
        light->IsVisible = true;
        light->UpdateTransforms(false);
        light->UpdateCulling();
        light->UpdateMaterials();
    }

    private static void Free(SceneLight* light)
    {
        light->CleanupRender();
        light->Dtor(1);
    }
}
