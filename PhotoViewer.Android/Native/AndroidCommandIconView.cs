using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
using PhotoViewer.Core.Design;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 设置页控制栏功能行的命令图标（SETTINGS-01 / TOOLBAR-04）：与控制栏同一命令语义与字号。
/// 评分快捷命令 1–5 画实心星并在星内叠数字（数字取按钮底色），0 画清零叉；其余命令画 Fluent 描边字形。
/// 字形是 PUA 码位，只用于绘制，不进入读屏。
/// </summary>
internal sealed class AndroidCommandIconView : View
{
    /// <summary>图标尺寸（dp），与控制栏命令字形同源，不随系统文字缩放。</summary>
    internal const double GlyphSizeDp = DesignTokens.ToolbarCommandGlyphFontSize;

    /// <summary>图标槽边长（dp）：独立于字号的布局令牌，容纳 1.2em 排版框内的全部墨迹。</summary>
    internal const double SlotSizeDp = DesignTokens.SettingsMobileCommandIconSlotSize;

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
        _glyph.TextSize = AndroidUiTheme.Dp(context, GlyphSizeDp);
        _glyph.TextAlign = Paint.Align.Center;
        _solid = AndroidUiTheme.Paint(AndroidUiTheme.TextPrimary);
        _solid.TextSize = AndroidUiTheme.Dp(context, GlyphSizeDp);
        _solid.TextAlign = Paint.Align.Center;
        _digit = AndroidUiTheme.TextPaint(context, DesignTokens.ToolbarRatingDigitFontSize,
            AndroidUiTheme.FromToken(DesignTokens.ColorToolbarCommandBackground), bold: true);
        _digit.TextSize = AndroidUiTheme.Dp(context, DesignTokens.ToolbarRatingDigitFontSize);
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
    /// <summary>字形尺寸（dp）。</summary>
    internal double FontSizeDp => GlyphSizeDp;
    /// <summary>字形实际像素尺寸（验收：应等于 18dp 的像素值，不被二次缩放）。</summary>
    internal float GlyphTextSizePx => (_solidGlyph ? _solid : _glyph).TextSize;
    /// <summary>图标槽边长（px，验收读取）：取槽位令牌而非字形尺寸。</summary>
    internal int SlotPx => AndroidUiTheme.IconSlotPx(Context!, SlotSizeDp);
    /// <summary>叠字字号（px，验收读取）；非评分命令为 0。</summary>
    internal float DigitTextSizePx => _number == null ? 0 : _digit.TextSize;

    /// <summary>
    /// 绑定命令图标：评分命令按控制栏语义绘制（0 为清零叉，1–5 为实心星叠数字），
    /// 所有命令统一按命令 ID 查共享语义清单。
    /// </summary>
    /// <param name="command">命令 id。</param>
    internal void Show(string? command)
    {
        _number = null;
        _solidGlyph = false;
        if (!string.IsNullOrEmpty(command) && command.StartsWith("SetRating", StringComparison.Ordinal))
        {
            string digit = command["SetRating".Length..];
            if (digit == "0")
            {
                _text = AndroidIcons.Clear;
            }
            else
            {
                _text = AndroidIcons.StarFilled;
                _number = digit;
                _solidGlyph = true;
            }
        }
        else if (!string.IsNullOrEmpty(command))
        {
            _text = AndroidIcons.ForCommand(command);
        }
        else
        {
            _text = string.Empty;
        }
        Visibility = string.IsNullOrEmpty(_text) ? ViewStates.Gone : ViewStates.Visible;
        RequestLayout();
        Invalidate();
    }

    /// <summary>
    /// 图标槽取共享的独立槽位令牌（24dp），不按字体 advance 量测：随包 Fluent 的 advance 是 1.2em 方形单元
    /// （字形居中、留白对称），按 advance 量测会把槽位与「勾选 → 图标 → 命令名」的视觉间距一起推宽；
    /// 槽位令牌大于字形墨迹（18dp 字号下最大约 20.2dp），字形按排版框中心落在槽内、不被裁切也不缩放。
    /// </summary>
    /// <param name="widthMeasureSpec">宽约束。</param>
    /// <param name="heightMeasureSpec">高约束。</param>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int slot = AndroidUiTheme.IconSlotPx(Context!, SlotSizeDp);
        SetMeasuredDimension(ResolveSize(slot, widthMeasureSpec), ResolveSize(slot, heightMeasureSpec));
    }

    /// <summary>按字体单元中心绘制字形；评分命令再按墨迹中心叠数字。</summary>
    /// <param name="canvas">画布。</param>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (string.IsNullOrEmpty(_text)) return;
        DrawGlyph(canvas, _solidGlyph ? _solid : _glyph, _text);
        if (_number != null) DrawDigit(canvas, _digit, _number);
    }

    private void DrawGlyph(Canvas canvas, Paint paint, string glyph)
    {
        AndroidUiTheme.DrawGlyphCentered(canvas, glyph, paint, Width / 2f, Height / 2f);
    }

    private void DrawDigit(Canvas canvas, Paint paint, string digit)
    {
        AndroidUiTheme.DrawTextInkCentered(canvas, digit, paint, Width / 2f, Height / 2f);
    }
}
