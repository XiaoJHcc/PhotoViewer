using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>圆角样式，对应上游 Shapes 1.2.1 的 <c>RoundedCornerStyle</c>。</summary>
public enum BackdropCornerStyle
{
    /// <summary>普通圆弧。</summary>
    Circular = 0,

    /// <summary>G2 连续曲率（上游默认）。</summary>
    Continuous = 1,
}

/// <summary>
/// 四角圆角半径（px，顺序为左上、右上、右下、左下），对应上游 Shapes 的
/// <c>RoundedRectangularShape.Corners</c>。
/// </summary>
public readonly struct BackdropCornerRadii : IEquatable<BackdropCornerRadii>
{
    /// <summary>给定四角半径。</summary>
    /// <param name="topLeft">左上。</param>
    /// <param name="topRight">右上。</param>
    /// <param name="bottomRight">右下。</param>
    /// <param name="bottomLeft">左下。</param>
    public BackdropCornerRadii(float topLeft, float topRight, float bottomRight, float bottomLeft)
    {
        TopLeft = topLeft;
        TopRight = topRight;
        BottomRight = bottomRight;
        BottomLeft = bottomLeft;
    }

    /// <summary>左上半径。</summary>
    public float TopLeft { get; }

    /// <summary>右上半径。</summary>
    public float TopRight { get; }

    /// <summary>右下半径。</summary>
    public float BottomRight { get; }

    /// <summary>左下半径。</summary>
    public float BottomLeft { get; }

    /// <summary>四角是否全为 0。</summary>
    public bool IsZero => TopLeft == 0f && TopRight == 0f && BottomRight == 0f && BottomLeft == 0f;

    /// <summary>四角是否相同。</summary>
    public bool IsUniform =>
        TopLeft == TopRight && TopRight == BottomRight && BottomRight == BottomLeft;

    /// <summary>转成着色器 uniform 数组（左上、右上、右下、左下）。</summary>
    /// <returns>长度 4 的数组。</returns>
    public float[] ToArray() => [TopLeft, TopRight, BottomRight, BottomLeft];

    /// <summary>逐角夹取到 [0, <paramref name="maxRadius"/>]。</summary>
    /// <param name="maxRadius">上限（通常为短边一半）。</param>
    /// <returns>夹取后的半径。</returns>
    public BackdropCornerRadii Clamp(float maxRadius) => new(
        Math.Clamp(TopLeft, 0f, maxRadius),
        Math.Clamp(TopRight, 0f, maxRadius),
        Math.Clamp(BottomRight, 0f, maxRadius),
        Math.Clamp(BottomLeft, 0f, maxRadius));

    /// <inheritdoc />
    public bool Equals(BackdropCornerRadii other) =>
        TopLeft == other.TopLeft &&
        TopRight == other.TopRight &&
        BottomRight == other.BottomRight &&
        BottomLeft == other.BottomLeft;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BackdropCornerRadii other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TopLeft, TopRight, BottomRight, BottomLeft);

    /// <summary>值相等。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>是否相等。</returns>
    public static bool operator ==(BackdropCornerRadii left, BackdropCornerRadii right) => left.Equals(right);

    /// <summary>值不等。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>是否不等。</returns>
    public static bool operator !=(BackdropCornerRadii left, BackdropCornerRadii right) => !left.Equals(right);
}

/// <summary>
/// 玻璃形状，对应上游 Shapes 1.2.1 的 <c>RoundedRectangularShape</c>：给出四角半径并输出
/// <see cref="Gfx.Path"/>；<see cref="BackdropCapsule"/> 与 <see cref="BackdropRoundedRectangle"/>
/// 是实际用到的两个形状。
/// </summary>
public abstract class BackdropShape
{
    /// <summary>圆角样式；无样式的形状（上游 <c>style == null</c>）按矩形处理。</summary>
    public abstract BackdropCornerStyle? Style { get; }

    /// <summary>
    /// 取给定尺寸与密度下的四角半径（px）。形状不是圆角矩形时返回 <c>null</c>
    /// （上游 <c>lens</c> 对非圆角形状抛不支持）。
    /// </summary>
    /// <param name="widthPx">宽（px）。</param>
    /// <param name="heightPx">高（px）。</param>
    /// <param name="density">绘制密度。</param>
    /// <returns>四角半径，或 <c>null</c>。</returns>
    public abstract BackdropCornerRadii? GetCornerRadii(float widthPx, float heightPx, float density);

    /// <summary>把闭合轮廓写进 <paramref name="path"/>（先 <c>rewind</c>）。</summary>
    /// <param name="path">目标路径。</param>
    /// <param name="widthPx">宽（px）。</param>
    /// <param name="heightPx">高（px）。</param>
    /// <param name="density">绘制密度。</param>
    public abstract void BuildPath(Gfx.Path path, float widthPx, float heightPx, float density);

    /// <summary>新建一条轮廓路径。</summary>
    /// <param name="widthPx">宽（px）。</param>
    /// <param name="heightPx">高（px）。</param>
    /// <param name="density">绘制密度。</param>
    /// <returns>路径；逐帧绘制请改用 <see cref="BackdropShapeOutline"/> 复用。</returns>
    public Gfx.Path CreatePath(float widthPx, float heightPx, float density)
    {
        var path = new Gfx.Path();
        BuildPath(path, widthPx, heightPx, density);
        return path;
    }

    /// <summary>
    /// 两个形状按比例插值，对应上游 Shapes 的 <c>lerp(RoundedRectangularShape, …)</c>。
    /// 比例为 0 或 1 时直接返回原对象。
    /// </summary>
    /// <param name="start">起始形状。</param>
    /// <param name="stop">结束形状。</param>
    /// <param name="fraction">比例。</param>
    /// <returns>插值形状。</returns>
    public static BackdropShape Lerp(BackdropShape start, BackdropShape stop, float fraction)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(stop);

        if (fraction == 0f) return start;
        if (fraction == 1f) return stop;
        return new BackdropLerpShape(start, stop, fraction, null);
    }

    /// <summary>同上，但显式指定圆角样式。</summary>
    /// <param name="start">起始形状。</param>
    /// <param name="stop">结束形状。</param>
    /// <param name="fraction">比例。</param>
    /// <param name="style">插值后使用的圆角样式。</param>
    /// <returns>插值形状。</returns>
    public static BackdropShape Lerp(
        BackdropShape start,
        BackdropShape stop,
        float fraction,
        BackdropCornerStyle style)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(stop);

        if (fraction == 0f) return start;
        if (fraction == 1f) return stop;
        return new BackdropLerpShape(start, stop, fraction, style);
    }
}

/// <summary>
/// 胶囊，对应上游 Shapes 的 <c>Capsule</c>：半径恒为短边一半，默认 G2 连续曲率。
/// </summary>
public sealed class BackdropCapsule : BackdropShape
{
    /// <summary>建一个胶囊。</summary>
    /// <param name="style">圆角样式，默认 <see cref="BackdropCornerStyle.Continuous"/>。</param>
    public BackdropCapsule(BackdropCornerStyle style = BackdropCornerStyle.Continuous)
    {
        Style = style;
    }

    /// <inheritdoc />
    public override BackdropCornerStyle? Style { get; }

    /// <inheritdoc />
    public override BackdropCornerRadii? GetCornerRadii(float widthPx, float heightPx, float density)
    {
        var radius = MathF.Min(widthPx, heightPx) * 0.5f;
        return new BackdropCornerRadii(radius, radius, radius, radius);
    }

    /// <inheritdoc />
    public override void BuildPath(Gfx.Path path, float widthPx, float heightPx, float density)
    {
        ArgumentNullException.ThrowIfNull(path);
        BackdropShapeGeometry.BuildCapsule(path, widthPx, heightPx, Style ?? BackdropCornerStyle.Continuous);
    }
}

/// <summary>
/// 圆角矩形，对应上游 Shapes 的 <c>RoundedRectangle</c>：圆角以 dp 给出，默认 G2 连续曲率。
/// </summary>
public sealed class BackdropRoundedRectangle : BackdropShape
{
    /// <summary>建一个圆角矩形。</summary>
    /// <param name="cornerRadiusDp">圆角半径（dp）。</param>
    /// <param name="style">圆角样式，默认 <see cref="BackdropCornerStyle.Continuous"/>。</param>
    public BackdropRoundedRectangle(
        float cornerRadiusDp,
        BackdropCornerStyle style = BackdropCornerStyle.Continuous)
    {
        CornerRadiusDp = cornerRadiusDp;
        Style = style;
    }

    /// <summary>圆角半径（dp）。</summary>
    public float CornerRadiusDp { get; }

    /// <inheritdoc />
    public override BackdropCornerStyle? Style { get; }

    /// <inheritdoc />
    public override BackdropCornerRadii? GetCornerRadii(float widthPx, float heightPx, float density)
    {
        var radius = Math.Clamp(CornerRadiusDp * density, 0f, MathF.Min(widthPx, heightPx) * 0.5f);
        return new BackdropCornerRadii(radius, radius, radius, radius);
    }

    /// <inheritdoc />
    public override void BuildPath(Gfx.Path path, float widthPx, float heightPx, float density)
    {
        ArgumentNullException.ThrowIfNull(path);
        var radius = Math.Clamp(CornerRadiusDp * density, 0f, MathF.Min(widthPx, heightPx) * 0.5f);
        BackdropShapeGeometry.BuildRoundedRectangle(
            path,
            widthPx,
            heightPx,
            radius,
            Style ?? BackdropCornerStyle.Continuous);
    }
}

/// <summary>
/// 两个圆角矩形的插值形状，对应上游 Shapes 的 <c>LerpRoundedRectangle</c>。
/// 样式按上游规则取：两端都有样式时按比例 0.5 切换，只有一端有时取那一端，都没有则为矩形。
/// </summary>
internal sealed class BackdropLerpShape : BackdropShape
{
    private readonly BackdropShape _start;
    private readonly BackdropShape _stop;
    private readonly float _fraction;
    private readonly BackdropCornerStyle? _explicitStyle;

    internal BackdropLerpShape(
        BackdropShape start,
        BackdropShape stop,
        float fraction,
        BackdropCornerStyle? style)
    {
        _start = start;
        _stop = stop;
        _fraction = fraction;
        _explicitStyle = style;

        if (style is { } explicitStyle)
        {
            Style = explicitStyle;
            return;
        }

        var startStyle = start.Style;
        var stopStyle = stop.Style;
        Style = startStyle is not null && stopStyle is not null
            ? fraction < 0.5f ? startStyle : stopStyle
            : startStyle ?? stopStyle;
    }

    public override BackdropCornerStyle? Style { get; }

    public override BackdropCornerRadii? GetCornerRadii(float widthPx, float heightPx, float density)
    {
        var startCorners = _start.GetCornerRadii(widthPx, heightPx, density);
        var stopCorners = _stop.GetCornerRadii(widthPx, heightPx, density);
        if (startCorners is not { } from || stopCorners is not { } to) return null;
        if (from == to) return from;

        var maxRadius = MathF.Min(widthPx, heightPx) * 0.5f;
        return new BackdropCornerRadii(
            Math.Clamp(Lerp(from.TopLeft, to.TopLeft, _fraction), 0f, maxRadius),
            Math.Clamp(Lerp(from.TopRight, to.TopRight, _fraction), 0f, maxRadius),
            Math.Clamp(Lerp(from.BottomRight, to.BottomRight, _fraction), 0f, maxRadius),
            Math.Clamp(Lerp(from.BottomLeft, to.BottomLeft, _fraction), 0f, maxRadius));
    }

    public override void BuildPath(Gfx.Path path, float widthPx, float heightPx, float density)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Style is not { } style)
        {
            BackdropShapeGeometry.BuildRectangle(path, widthPx, heightPx);
            return;
        }

        if (GetCornerRadii(widthPx, heightPx, density) is not { } corners)
        {
            BackdropShapeGeometry.BuildRectangle(path, widthPx, heightPx);
            return;
        }

        if (corners.IsUniform)
        {
            BackdropShapeGeometry.BuildRoundedRectangle(path, widthPx, heightPx, corners.TopLeft, style);
        }
        else
        {
            BackdropShapeGeometry.BuildUnevenRoundedRectangle(path, widthPx, heightPx, corners, style);
        }
    }

    private static float Lerp(float start, float stop, float fraction) =>
        start + ((stop - start) * fraction);
}
