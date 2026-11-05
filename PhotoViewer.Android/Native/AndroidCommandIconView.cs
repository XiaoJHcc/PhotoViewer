using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
using PhotoViewer.Core.Design;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 设置页控制栏功能行的命令图标（SETTINGS-01 / TOOLBAR-04）：与控制栏同一命令语义与字号。
/// 评分快捷命令 1–5 画实心星并在星内叠数字（数字取按钮底色），0 画清零叉；其余命令画 uicons 描边字形。
/// 字形是 PUA 码位，只用于绘制，不进入读屏。
/// </summary>
internal sealed class AndroidCommandIconView : View
{
    /// <summary>图标字号（sp），与控制栏命令字形同源。</summary>
    internal const double GlyphSizeSp = DesignTokens.ToolbarCommandGlyphFontSize;

    private readonly Paint _glyph;
    private readonly Paint _solid;
    private readonly Paint _digit;
    private string _text = string.Empty;
    private string? _number;
    private bool _solidGlyph;

    /// <summary>建立图标视图。</summary>
    /// <param name="context">宿主上下文。</param>
    internal AndroidCommandIconView(Context context) : base(context)
    {
        _glyph = AndroidUiTheme.Paint(AndroidUiTheme.TextPrimary);
        _glyph.TextSize = AndroidUiTheme.Sp(context, GlyphSizeSp);
        _glyph.TextAlign = Paint.Align.Center;
        _solid = AndroidUiTheme.Paint(AndroidUiTheme.TextPrimary);
        _solid.TextSize = AndroidUiTheme.Sp(context, GlyphSizeSp);
        _solid.TextAlign = Paint.Align.Center;
        _digit = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarRatingDigitFontSize,
            AndroidUiTheme.FromToken(DesignTokens.ColorToolbarCommandBackground), bold: true);
        if (AndroidUiTheme.IconTypeface(context) is { } regular) _glyph.SetTypeface(regular);
        if (AndroidUiTheme.IconSolidTypeface(context) is { } solid) _solid.SetTypeface(solid);
        // 图标只是命令名的附属符号：读屏只念整行名称，不朗读 PUA 字形。
        ImportantForAccessibility = ImportantForAccessibility.No;
        SetWillNotDraw(false);
    }

    /// <summary>当前绘制字形（验收：与控制栏语义比对）。</summary>
    internal string Symbol => _text;
    /// <summary>评分命令叠在星内的数字；非评分命令为 null。</summary>
    internal string? RatingDigit => _number;
    /// <summary>是否使用实心星字形。</summary>
    internal bool SolidGlyph => _solidGlyph;
    /// <summary>字形字号（sp）。</summary>
    internal double FontSizeSp => GlyphSizeSp;
    /// <summary>字形实际像素字号（验收：应等于 18sp 的像素值，不被二次缩放）。</summary>
    internal float GlyphTextSizePx => (_solidGlyph ? _solid : _glyph).TextSize;

    /// <summary>
    /// 绑定命令图标：评分命令按控制栏语义绘制（0 为清零叉，1–5 为实心星叠数字），
    /// 其余命令取共享命令目录的字形；命令无字形时用主图菜单的同语义图标（雾天增强）。
    /// </summary>
    /// <param name="command">命令 id。</param>
    /// <param name="symbol">共享命令目录给出的字形。</param>
    internal void Show(string? command, string? symbol)
    {
        _number = null;
        _solidGlyph = false;
        if (!string.IsNullOrEmpty(command) && command.StartsWith("SetRating", StringComparison.Ordinal))
        {
            string digit = command["SetRating".Length..];
            if (digit == "0")
            {
                _text = AndroidUiTheme.IconGlyph(DesignTokens.IconClear);
            }
            else
            {
                _text = AndroidUiTheme.IconGlyph(DesignTokens.IconStarFilled);
                _number = digit;
                _solidGlyph = true;
            }
        }
        else if (string.IsNullOrEmpty(symbol) && command == "ToggleEnhance")
        {
            _text = AndroidUiTheme.IconGlyph(DesignTokens.IconCommandToggleEnhance);
        }
        else
        {
            _text = symbol ?? string.Empty;
        }
        Visibility = string.IsNullOrEmpty(_text) ? ViewStates.Gone : ViewStates.Visible;
        RequestLayout();
        Invalidate();
    }

    /// <summary>按字形实际尺寸测量，容器随系统大字体增高，不裁切字形。</summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var paint = _solidGlyph ? _solid : _glyph;
        var metrics = paint.GetFontMetrics()!;
        int minimum = (int)AndroidUiTheme.Dp(Context!, DesignTokens.ToolbarCommandGlyphFontSize);
        int width = Math.Max(minimum, (int)Math.Ceiling(paint.MeasureText(_text)) + 2);
        int height = Math.Max(minimum, (int)Math.Ceiling(metrics.Descent - metrics.Ascent) + 2);
        SetMeasuredDimension(ResolveSize(width, widthMeasureSpec), ResolveSize(height, heightMeasureSpec));
    }

    /// <summary>居中绘制字形；评分命令再叠数字。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (string.IsNullOrEmpty(_text)) return;
        DrawCentered(canvas, _solidGlyph ? _solid : _glyph, _text);
        if (_number != null) DrawCentered(canvas, _digit, _number);
    }

    private void DrawCentered(Canvas canvas, Paint paint, string text)
    {
        var metrics = paint.GetFontMetrics()!;
        float baseline = Height / 2f - (metrics.Ascent + metrics.Descent) / 2f;
        canvas.DrawText(text, Width / 2f, baseline, paint);
    }
}
