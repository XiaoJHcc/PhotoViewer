using System;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 按「形状 + 尺寸 + 密度」缓存轮廓路径与四角半径，对应上游 <c>internal/ShapeProvider.kt</c>
/// 的形状/尺寸缓存。
/// </summary>
/// <remarks>
/// 复用同一个 <see cref="Gfx.Path"/>，逐帧绘制不再新建路径；调用方不要改写 <see cref="Path"/>，
/// 尺寸或形状变化时由 <see cref="Update"/> 重新生成。高光、阴影、裁切与描边共用这一份轮廓。
/// </remarks>
public sealed class BackdropShapeOutline
{
    private BackdropShape _shape;
    private float _widthPx;
    private float _heightPx;
    private float _density;
    private BackdropCornerRadii _cornerRadii;
    private bool _dirty = true;

    /// <summary>为一个形状建轮廓缓存。</summary>
    /// <param name="shape">形状。</param>
    public BackdropShapeOutline(BackdropShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        _shape = shape;
        Path = new Gfx.Path();
    }

    /// <summary>缓存的轮廓路径（只读使用）。</summary>
    public Gfx.Path Path { get; }

    /// <summary>当前形状；换形状会让下次 <see cref="Update"/> 重建路径。</summary>
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

    /// <summary>上次更新的宽（px）。</summary>
    public float WidthPx => _widthPx;

    /// <summary>上次更新的高（px）。</summary>
    public float HeightPx => _heightPx;

    /// <summary>上次更新的绘制密度。</summary>
    public float Density => _density;

    /// <summary>当前四角半径（px）；首次访问前需先 <see cref="Update"/>。</summary>
    public BackdropCornerRadii CornerRadii
    {
        get
        {
            Ensure();
            return _cornerRadii;
        }
    }

    /// <summary>按当前尺寸与密度刷新；参数未变时直接复用已有路径。</summary>
    /// <param name="widthPx">宽（px）。</param>
    /// <param name="heightPx">高（px）。</param>
    /// <param name="density">绘制密度。</param>
    public void Update(float widthPx, float heightPx, float density)
    {
        if (widthPx != _widthPx || heightPx != _heightPx || density != _density)
        {
            _widthPx = widthPx;
            _heightPx = heightPx;
            _density = density;
            _dirty = true;
        }

        Ensure();
    }

    /// <summary>强制下次更新重建路径。</summary>
    public void Invalidate() => _dirty = true;

    private void Ensure()
    {
        if (!_dirty) return;

        _cornerRadii = _shape.GetCornerRadii(_widthPx, _heightPx, _density)
            ?? throw new NotSupportedException("形状不提供圆角半径。");
        _shape.BuildPath(Path, _widthPx, _heightPx, _density);
        _dirty = false;
    }
}
