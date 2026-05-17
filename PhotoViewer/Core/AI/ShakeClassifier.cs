using System;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 抖动判定结果。语义与 DinoDebugViewModel 旧版文字标签 1:1 对应。
/// </summary>
public enum ShakeVerdict
{
    /// <summary>信息不足:Σw 或 MaskRatio 太低,不做判定。</summary>
    Insufficient = 0,

    /// <summary>静止/纹理:全图方向相关性低或运动量级未到阈值。</summary>
    Stationary = 1,

    /// <summary>强旋转抖动:|ω| 远超阈值且 R_global 充分。</summary>
    StrongRotation = 2,

    /// <summary>旋转抖动:|ω| 与方向一致性 + R_local p10 都达标。</summary>
    Rotation = 3,

    /// <summary>平移抖动:|T|/D 与 R_global 达标。</summary>
    Translation = 4,

    /// <summary>混乱场景(车流 / 树叶 / 各向异性纹理)。</summary>
    Chaotic = 5,

    /// <summary>弱信号 / 难判。</summary>
    Weak = 6,
}

/// <summary>
/// 抖动判定的纯函数门面。从 <see cref="CvHeatmap.FitRigidMotion"/> 的输出推断抖动类型,
/// 阈值常量仍集中在 <see cref="CvHeatmap"/>,改一处必同步 14 张样本回归。
/// </summary>
public static class ShakeClassifier
{
    /// <summary>
    /// 把刚体拟合结果分类。判定优先级 = 信息不足 &gt; 强旋转抖动 &gt; 静止纹理(R_global &lt; RGlobalQuietBelow 早拦)
    ///   &gt; 旋转抖动 &gt; 平移抖动 &gt; 混乱场景 &gt; 兜底静止 &gt; 弱信号。
    /// </summary>
    public static ShakeVerdict Classify(RigidMotionResult rigid, float diagonal)
    {
        float halfDiag = diagonal * 0.5f;
        float omegaPx = rigid.RotationMagnitude * halfDiag;
        float motionScale = MathF.Max(
            MathF.Sqrt(rigid.TranslationMagnitude * rigid.TranslationMagnitude + omegaPx * omegaPx),
            1e-3f);
        float translateR = diagonal > 0 ? rigid.TranslationMagnitude / diagonal : 0f;

        if (rigid.WeightSum < CvHeatmap.WeightSumMin || rigid.MaskRatio < CvHeatmap.MaskRatioMin)
            return ShakeVerdict.Insufficient;

        if (rigid.RotationMagnitude >= CvHeatmap.OmegaStrongRot
            && rigid.DirectionalConsistency >= CvHeatmap.RGlobalStrongRotAbove)
            return ShakeVerdict.StrongRotation;

        if (rigid.DirectionalConsistency < CvHeatmap.RGlobalQuietBelow)
            return ShakeVerdict.Stationary;

        if (rigid.RotationMagnitude >= CvHeatmap.OmegaRot
            && rigid.DirectionalConsistency >= CvHeatmap.RGlobalMotionAbove
            && rigid.RLocalP10 >= CvHeatmap.RLocalP10RotMin)
            return ShakeVerdict.Rotation;

        if (translateR >= CvHeatmap.TranslateMinDragR
            && rigid.DirectionalConsistency >= CvHeatmap.RGlobalMotionAbove)
            return ShakeVerdict.Translation;

        if (rigid.ResidualRms > CvHeatmap.ResidualMotionRatio * motionScale)
            return ShakeVerdict.Chaotic;

        if (translateR < CvHeatmap.TranslateMinDragR && rigid.RotationMagnitude < CvHeatmap.OmegaRot)
            return ShakeVerdict.Stationary;

        return ShakeVerdict.Weak;
    }

    /// <summary>三类"算抖"的判定:平移 / 旋转 / 强旋转。其余(信息不足 / 静止 / 混乱 / 弱信号)都不挂抖动徽标。</summary>
    public static bool IsShake(ShakeVerdict verdict) =>
        verdict == ShakeVerdict.Translation
        || verdict == ShakeVerdict.Rotation
        || verdict == ShakeVerdict.StrongRotation;

    /// <summary>把判定结果格式化为中文标签,DinoDebugViewModel 文本面板与潜在 ToolTip 共用。</summary>
    public static string FormatLabel(ShakeVerdict verdict) => verdict switch
    {
        ShakeVerdict.Insufficient => "信息不足",
        ShakeVerdict.Stationary => "静止纹理",
        ShakeVerdict.StrongRotation => "强旋转抖动",
        ShakeVerdict.Rotation => "旋转抖动",
        ShakeVerdict.Translation => "平移抖动",
        ShakeVerdict.Chaotic => "混乱场景（车流 / 树叶 / 各向异性纹理）",
        ShakeVerdict.Weak => "弱信号 / 难判",
        _ => "未知",
    };
}
