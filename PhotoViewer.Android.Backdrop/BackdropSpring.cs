using System;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 阻尼谐振弹簧，对应 Compose <c>spring(dampingRatio, stiffness, visibilityThreshold)</c> 与
/// AndroidX <c>SpringForce</c> 的同一物理模型（质量 1）。
/// </summary>
/// <remarks>
/// <para>
/// 每帧用闭式解推进，而不是按步长数值积分：欠阻尼、临界阻尼与过阻尼三种情况都按
/// <c>x(t)</c> 的解析式求值，时间点采样与 Compose 弹簧的解析解一致。
/// </para>
/// <para>
/// 位移与速度都小于可见阈值时收束到目标值，速度清零；<see cref="Advance"/> 返回是否仍需下一帧。
/// 由调用方驱动帧并重绘，本类不持有视图或计时器。
/// </para>
/// </remarks>
public sealed class BackdropSpring
{
    private const float Epsilon = 1e-4f;

    /// <summary>建一根处于静止状态的弹簧。</summary>
    /// <param name="initialValue">初始值。</param>
    /// <param name="dampingRatio">阻尼比（0 为无阻尼，1 为临界阻尼）。</param>
    /// <param name="stiffness">刚度（每秒平方）。</param>
    /// <param name="visibilityThreshold">可见阈值（与值同单位）。</param>
    public BackdropSpring(
        float initialValue,
        float dampingRatio,
        float stiffness,
        float visibilityThreshold)
    {
        if (dampingRatio < 0f) throw new ArgumentOutOfRangeException(nameof(dampingRatio));
        if (stiffness <= 0f) throw new ArgumentOutOfRangeException(nameof(stiffness));
        if (visibilityThreshold < 0f) throw new ArgumentOutOfRangeException(nameof(visibilityThreshold));

        DampingRatio = dampingRatio;
        Stiffness = stiffness;
        VisibilityThreshold = visibilityThreshold;
        Value = initialValue;
        Target = initialValue;
    }

    /// <summary>阻尼比。</summary>
    public float DampingRatio { get; }

    /// <summary>刚度。</summary>
    public float Stiffness { get; }

    /// <summary>可见阈值。</summary>
    public float VisibilityThreshold { get; }

    /// <summary>当前值。</summary>
    public float Value { get; private set; }

    /// <summary>当前速度（每秒）。</summary>
    public float Velocity { get; private set; }

    /// <summary>目标值。</summary>
    public float Target { get; private set; }

    /// <summary>是否已收束到目标值。</summary>
    public bool IsSettled =>
        MathF.Abs(Value - Target) < VisibilityThreshold &&
        MathF.Abs(Velocity) < VisibilityThreshold;

    /// <summary>立即把值与目标都设到给定点，速度清零。</summary>
    /// <param name="value">值。</param>
    public void SnapTo(float value)
    {
        Value = value;
        Target = value;
        Velocity = 0f;
    }

    /// <summary>以当前速度开始向目标值运动。</summary>
    /// <param name="target">目标值。</param>
    public void AnimateTo(float target) => Target = target;

    /// <summary>
    /// 推进 <paramref name="deltaSeconds"/> 秒。返回是否仍在运动（需要下一帧）。
    /// </summary>
    /// <param name="deltaSeconds">距上一帧的秒数；≤ 0 时不推进。</param>
    /// <returns>是否仍需下一帧。</returns>
    public bool Advance(float deltaSeconds)
    {
        if (IsSettled)
        {
            Value = Target;
            Velocity = 0f;
            return false;
        }

        if (deltaSeconds <= 0f || float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds))
        {
            return true;
        }

        var x0 = Value - Target;
        var v0 = Velocity;
        var omega = MathF.Sqrt(Stiffness);
        var zeta = DampingRatio;
        var t = deltaSeconds;

        float x;
        float v;

        if (zeta < 1f - Epsilon)
        {
            var alpha = zeta * omega;
            var omegaD = omega * MathF.Sqrt(1f - (zeta * zeta));
            var decay = MathF.Exp(-alpha * t);
            var cos = MathF.Cos(omegaD * t);
            var sin = MathF.Sin(omegaD * t);
            var b = (v0 + (alpha * x0)) / omegaD;

            x = decay * ((x0 * cos) + (b * sin));
            v = decay * (v0 * cos - (((alpha * v0) + (omega * omega * x0)) / omegaD) * sin);
        }
        else if (zeta <= 1f + Epsilon)
        {
            var decay = MathF.Exp(-omega * t);
            var b = v0 + (omega * x0);

            x = (x0 + (b * t)) * decay;
            v = (v0 - (omega * b * t)) * decay;
        }
        else
        {
            var alpha = zeta * omega;
            var beta = omega * MathF.Sqrt((zeta * zeta) - 1f);
            var r1 = -alpha + beta;
            var r2 = -alpha - beta;
            var c1 = (v0 - (r2 * x0)) / (r1 - r2);
            var c2 = x0 - c1;
            var e1 = MathF.Exp(r1 * t);
            var e2 = MathF.Exp(r2 * t);

            x = (c1 * e1) + (c2 * e2);
            v = (c1 * r1 * e1) + (c2 * r2 * e2);
        }

        Value = Target + x;
        Velocity = v;

        if (IsSettled)
        {
            Value = Target;
            Velocity = 0f;
            return false;
        }

        return true;
    }
}
