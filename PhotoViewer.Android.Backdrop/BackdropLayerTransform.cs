using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 玻璃元素自身的渲染变换（缩放与 Z 轴旋转），供取样时按逆变换还原背景，对应上游
/// <c>internal/InverseLayerScope.kt</c> 的 <c>inverseTransform</c>。
/// </summary>
/// <remarks>
/// <para>
/// 上游在 <c>layerBlock</c> 里给玻璃元素设平移、缩放与旋转，取样时只对旋转与缩放做逆变换，
/// 且以元素左上角为原点；平移不参与逆变换（元素整体移动时取样随之移动，上游同样如此）。
/// 这里保留同一算法：<c>rotationZ == 0</c> 时只 <c>scale(1/scaleX, 1/scaleY)</c>，否则用
/// <c>a00 = cos·scaleX、a01 = sin·scaleY、a10 = −sin·scaleX、a11 = cos·scaleY</c> 的
/// 2×2 矩阵求逆后 <c>concat</c>。
/// </para>
/// <para>
/// 默认值 <c>default</c>（全零）与 <see cref="Identity"/> 都表示不变换：前者按上游的零缩放
/// 保护直接跳过，后者是显式的单位变换。
/// </para>
/// </remarks>
public readonly struct BackdropLayerTransform
{
    /// <summary>单位变换。</summary>
    public static readonly BackdropLayerTransform Identity = new(1f, 1f, 0f);

    /// <summary>给定缩放与旋转（角度制）。</summary>
    /// <param name="scaleX">X 轴缩放。</param>
    /// <param name="scaleY">Y 轴缩放。</param>
    /// <param name="rotationZ">Z 轴旋转（度）。</param>
    public BackdropLayerTransform(float scaleX, float scaleY, float rotationZ)
    {
        ScaleX = scaleX;
        ScaleY = scaleY;
        RotationZ = rotationZ;
    }

    /// <summary>X 轴缩放。</summary>
    public float ScaleX { get; }

    /// <summary>Y 轴缩放。</summary>
    public float ScaleY { get; }

    /// <summary>Z 轴旋转（度）。</summary>
    public float RotationZ { get; }

    /// <summary>是否为显式单位变换。</summary>
    public bool IsIdentity => RotationZ == 0f && ScaleX == 1f && ScaleY == 1f;

    /// <summary>
    /// 在画布上追加逆变换。变换原点固定在左上角，与上游 <c>inverseTransformAtTopLeft</c> 相同。
    /// </summary>
    /// <param name="canvas">目标画布。</param>
    public void ApplyInverse(Gfx.Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (RotationZ == 0f)
        {
            // 上游：缩放为零时不做逆变换；缩放为 1 时也不必追加矩阵。
            if (ScaleX != 0f && ScaleY != 0f && (ScaleX != 1f || ScaleY != 1f))
            {
                canvas.Scale(1f / ScaleX, 1f / ScaleY);
            }

            return;
        }

        var rz = RotationZ * (MathF.PI / 180f);
        var sin = MathF.Sin(rz);
        var cos = MathF.Cos(rz);

        var a00 = cos * ScaleX;
        var a01 = sin * ScaleY;
        var a10 = -sin * ScaleX;
        var a11 = cos * ScaleY;

        var det = (a00 * a11) - (a01 * a10);
        if (det == 0f) return;

        var invDet = 1f / det;
        var matrix = new Gfx.Matrix();
        matrix.SetValues(
        [
            a11 * invDet, -a01 * invDet, 0f,
            -a10 * invDet, a00 * invDet, 0f,
            0f, 0f, 1f,
        ]);
        canvas.Concat(matrix);
    }
}
