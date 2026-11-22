using System;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 纯绘制图标视图：按字体排版框中心把 Fluent UI System Icons 字形落在显式图标槽里。
/// 图标槽（布局占位）与字号分离：字号恒为令牌 dp 的精确像素，不随系统文字缩放；
/// 槽位由令牌或 1.2em 排版框给出，字形因此不会被文本 advance 撑宽、换行或被窄槽裁切。
/// 字形是 PUA 码位，只用于绘制，不进入读屏。
/// </summary>
internal sealed class AndroidGlyphView : View
{
    private readonly Paint _paint;
    private readonly string _glyph;
    private readonly int _slot;

    /// <summary>建一个图标槽视图。</summary>
    /// <param name="context">宿主上下文。</param>
    /// <param name="glyph">Fluent UI System Icons 字形。</param>
    /// <param name="sizeDp">字号（dp 令牌）。</param>
    /// <param name="color">字形颜色。</param>
    /// <param name="slotDp">图标槽边长（dp 令牌）；null 时取字号对应的 1.2em 排版框加光栅余量。</param>
    internal AndroidGlyphView(Context context, string glyph, double sizeDp, Color color, double? slotDp = null)
        : base(context)
    {
        _glyph = glyph;
        _paint = AndroidUiTheme.Paint(color);
        _paint.TextSize = AndroidUiTheme.Dp(context, sizeDp);
        if (AndroidUiTheme.IconTypeface(context) is { } typeface) _paint.SetTypeface(typeface);
        _slot = slotDp is { } slot ? AndroidUiTheme.IconSlotPx(context, slot) : AndroidUiTheme.GlyphSlotPx(context, sizeDp);
        ImportantForAccessibility = ImportantForAccessibility.No;
        SetWillNotDraw(false);
    }

    /// <summary>当前绘制字形（验收读取）。</summary>
    internal string Symbol => _glyph;

    /// <summary>字号（px，验收读取）。</summary>
    internal float FontSizePx => _paint.TextSize;

    /// <summary>图标槽边长（px，验收读取）。</summary>
    internal int SlotPx => _slot;

    /// <summary>按图标槽给定固有尺寸，容器可按更宽的行几何拉伸而不影响字形的中心落位。</summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        SetMeasuredDimension(ResolveSize(_slot, widthMeasureSpec), ResolveSize(_slot, heightMeasureSpec));
    }

    /// <summary>按排版框中心绘制字形。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        AndroidUiTheme.DrawGlyphCentered(canvas, _glyph, _paint, Width / 2f, Height / 2f);
    }
}
