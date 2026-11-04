using System.Numerics;

namespace Posegraph.Idle;

/// <summary>骨骼的局部（相对父骨骼）或模型空间变换。</summary>
public readonly record struct BoneTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    public static readonly BoneTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}

/// <summary>
/// 烘焙 idle 用到的数学。Havok 轨道是局部空间的绝对变换，原版 idle 的呼吸起伏 = 每帧相对参考帧的旋转增量，
/// 叠回到我们的姿势上。移植自 ReAnimate（MIT，Copyright (c) 2026 Nyaughty）。
/// </summary>
public static class IdleMath
{
    /// <summary>
    /// 第 f 帧 = 姿势的旋转 × 原版这一帧相对参考帧的增量（在骨骼自己的坐标系里）。只换旋转：平移、缩放用原版的
    /// （骨长是这个种族自己的，Customize+ 之类的缩放也不会被烤进去）。withTranslation：平移以姿势为准（只用于定身体位置的 n_hara）。
    /// weight：增量的强度，0 = 一直保持第 0 帧的姿势，1 = 原版的幅度，大于 1 放大（只用于预览）。
    /// </summary>
    public static BoneTransform Bake(in BoneTransform pose, in BoneTransform vanillaRef, in BoneTransform vanillaFrame, bool withTranslation, float weight = 1f)
    {
        var delta = Power(Quaternion.Normalize(Quaternion.Inverse(vanillaRef.Rotation) * vanillaFrame.Rotation), weight);
        var move = (vanillaFrame.Translation - vanillaRef.Translation) * weight;
        var translation = (withTranslation ? pose.Translation : vanillaRef.Translation) + move;
        return new BoneTransform(translation, Quaternion.Normalize(pose.Rotation * delta),
                                 vanillaRef.Scale + (vanillaFrame.Scale - vanillaRef.Scale) * weight);
    }

    /// <summary>用户给的整体旋转（度）：x 前后倾、y 转身、z 侧倾。</summary>
    public static Quaternion BodyRotation(Vector3 degrees)
        => Quaternion.CreateFromYawPitchRoll(degrees.Y * MathF.PI / 180f, degrees.X * MathF.PI / 180f, degrees.Z * MathF.PI / 180f);

    /// <summary>把局部变换在模型空间里转 r（父骨骼模型空间朝向 parent）：绕这根骨骼自己的原点转，位置不变。</summary>
    public static BoneTransform RotateInModel(in BoneTransform local, Quaternion parent, Quaternion r)
        => local with { Rotation = Quaternion.Normalize(Quaternion.Inverse(parent) * r * parent * local.Rotation) };

    /// <summary>旋转的 k 倍（同一根轴，角度乘 k）。</summary>
    public static Quaternion Power(Quaternion q, float k)
    {
        if (k == 1f) return q;
        if (q.W < 0) q = -q;   // 取短的那一边
        var sin = MathF.Sqrt(MathF.Max(0f, 1f - q.W * q.W));
        if (k == 0f || sin < 1e-6f) return Quaternion.Identity;
        var angle = 2f * MathF.Atan2(sin, q.W);
        return Quaternion.CreateFromAxisAngle(new Vector3(q.X, q.Y, q.Z) / sin, angle * k);
    }

    /// <summary>父骨骼在前的局部姿势 → 模型空间。</summary>
    public static BoneTransform[] ToModel(ReadOnlySpan<BoneTransform> locals, ReadOnlySpan<short> parents)
    {
        var model = new BoneTransform[locals.Length];
        for (var i = 0; i < locals.Length; i++)
        {
            var p = parents[i];
            model[i] = p < 0 || p >= i ? locals[i] : Compose(model[p], locals[i]);
        }
        return model;
    }

    public static BoneTransform Compose(in BoneTransform parent, in BoneTransform local) => new(
        parent.Translation + Vector3.Transform(parent.Scale * local.Translation, parent.Rotation),
        Quaternion.Normalize(parent.Rotation * local.Rotation),
        parent.Scale * local.Scale);

    /// <summary>模型空间 → 相对父骨骼（Compose 的逆）。</summary>
    public static BoneTransform Relative(in BoneTransform parent, in BoneTransform model)
    {
        var inv = Quaternion.Inverse(parent.Rotation);
        var scale = new Vector3(Safe(parent.Scale.X), Safe(parent.Scale.Y), Safe(parent.Scale.Z));
        return new BoneTransform(Vector3.Transform(model.Translation - parent.Translation, inv) / scale,
                                 Quaternion.Normalize(inv * model.Rotation), model.Scale / scale);

        static float Safe(float s) => MathF.Abs(s) < 1e-6f ? 1f : s;
    }

    /// <summary>让姿势踩在地上要把身体往上挪多少（模型空间，米）：姿势里最低的腿部骨骼对上原版参考帧的。正数 = 姿势太低。</summary>
    public static float GroundShift(ReadOnlySpan<BoneTransform> pose, ReadOnlySpan<BoneTransform> reference, ReadOnlySpan<short> parents, ReadOnlySpan<bool> planted)
        => Lowest(ToModel(reference, parents), planted) - Lowest(ToModel(pose, parents), planted);

    private static float Lowest(BoneTransform[] model, ReadOnlySpan<bool> planted)
    {
        var y = float.MaxValue;
        for (var i = 0; i < model.Length; i++)
            if (planted[i]) y = MathF.Min(y, model[i].Translation.Y);
        return y == float.MaxValue ? 0f : y;
    }

    /// <summary>
    /// _start 过渡的「从根到梢」缓动：父骨骼先到、子骨骼后到（肩、肘、手指依次），每根骨骼的过渡时长一样，越深开始得越晚；
    /// 最深的骨骼正好在最后一帧到位，第 0 帧都不动。
    /// </summary>
    public readonly struct StaggeredEase
    {
        private const float SettleFraction = 0.55f;   // 整串过渡在片段的这么多比例内完成
        private readonly int _total;
        private readonly float _window;
        private readonly float _stagger;
        private readonly int _minDepth;

        public StaggeredEase(int frames, int minDepth, int maxDepth)
        {
            _total = Math.Max(1, frames - 1);
            _minDepth = minDepth;
            var settle = Math.Max(1f, _total * SettleFraction);
            _window = Math.Min(settle, Math.Max(6f, settle / 3f));
            var span = Math.Max(0, maxDepth - minDepth);
            _stagger = span > 0 ? (settle - _window) / span : 0f;
        }

        public float Weight(int depth, int frame)
        {
            if (frame >= _total) return 1f;
            var t = Math.Clamp((frame - (depth - _minDepth) * _stagger) / _window, 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }

    /// <summary>原版轨道每帧走完自己动作的多少（累计转角 / 总转角，单调 0 → 1）；原版几乎不动这根骨骼时为 null。
    /// 修正按这个进度加进去，骨骼只在原版动它的时候动一次。</summary>
    public static float[]? ArcProgress(ReadOnlySpan<Quaternion> track)
    {
        const float MinTotalArc = 0.035f;
        if (track.Length < 2) return null;
        var cumulative = new float[track.Length];
        for (var i = 1; i < track.Length; i++)
        {
            var dot = Math.Clamp(MathF.Abs(Quaternion.Dot(track[i - 1], track[i])), 0f, 1f);
            cumulative[i] = cumulative[i - 1] + 2f * MathF.Acos(dot);
        }
        var total = cumulative[^1];
        if (total < MinTotalArc) return null;
        for (var i = 0; i < cumulative.Length; i++) cumulative[i] = Math.Clamp(cumulative[i] / total, 0f, 1f);
        cumulative[^1] = 1f;
        return cumulative;
    }

    /// <summary>四元数符号沿轨道保持连续，插值不会绕远路。</summary>
    public static Quaternion AlignSign(in Quaternion previous, in Quaternion current) => Quaternion.Dot(previous, current) < 0 ? -current : current;
}
