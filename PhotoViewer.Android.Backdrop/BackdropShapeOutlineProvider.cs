using System;
using Android.Views;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 把 <see cref="BackdropShape"/> 交给 <see cref="View.OutlineProvider"/>，对应上游
/// <c>internal/Outline.kt</c> 的轮廓分支（矩形 / 圆角矩形 / 一般路径）。
/// </summary>
/// <remarks>
/// 四角相同且（普通圆弧或正方形顶满）时用平台圆角矩形轮廓，其余用 <c>SetConvexPath</c>；
/// G2 路径为凸，满足平台约束。用于裁剪与系统投影，不参与玻璃取样本身。
/// </remarks>
public sealed class BackdropShapeOutlineProvider : ViewOutlineProvider
{
    private readonly Gfx.Path _path = new();
    private BackdropShape _shape;
    private int _width;
    private int _height;
    private float _density;
    private bool _dirty = true;

    /// <summary>为形状建轮廓提供器。</summary>
    /// <param name="shape">形状。</param>
    public BackdropShapeOutlineProvider(BackdropShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        _shape = shape;
    }

    /// <summary>当前形状；换形状后下次取轮廓时重建。</summary>
    public BackdropShape Shape
    {
        get => _shape;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_shape, value)) return;
            _shape = value;
            _dirty = true;
        }
    }

    /// <inheritdoc />
    public override void GetOutline(View? view, Gfx.Outline? outline)
    {
        if (view is null || outline is null) return;

        var width = view.Width;
        var height = view.Height;
        if (width <= 0 || height <= 0) return;

        var density = view.Resources?.DisplayMetrics?.Density ?? 1f;
        if (_dirty || width != _width || height != _height || density != _density)
        {
            _width = width;
            _height = height;
            _density = density;
            _shape.BuildPath(_path, width, height, density);
            _dirty = false;
        }

        var corners = _shape.GetCornerRadii(width, height, density);
        if (_shape.Style is not { } style || corners is not { } radii || radii.IsZero)
        {
            outline.SetRect(0, 0, width, height);
            return;
        }

        var maxRadius = MathF.Min(width, height) * 0.5f;
        if (radii.IsUniform &&
            (style == BackdropCornerStyle.Circular || (width == height && radii.TopLeft >= maxRadius)))
        {
            outline.SetRoundRect(0, 0, width, height, radii.TopLeft);
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            outline.SetPath(_path);
        }
        else
        {
            outline.SetConvexPath(_path);
        }
    }
}
