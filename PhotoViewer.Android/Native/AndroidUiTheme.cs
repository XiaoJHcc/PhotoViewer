using System;
using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using PhotoViewer.Core.Design;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 原生壳视觉常量与绘制辅助（A3）：固定深色、显式圆角/字重，不引用 OEM 主题派生属性。
/// 颜色取设计令牌（#111 背景、主屏栏底与按钮底材按 `@android` 覆盖合成、#444 描边），尺寸全部以 dp 表达。
/// </summary>
internal static class AndroidUiTheme
{
    /// <summary>窗口背景。</summary>
    internal static readonly Color Background = FromToken(DesignTokens.ColorBarBackground);
    /// <summary>主图区背景。</summary>
    internal static readonly Color PhotoBackground = Color.Black!;
    /// <summary>主屏栏底独立色层（SHELL-12）：<c>color.bar.overlay@android</c> 按 <c>shell.bar.overlay.opacity@android</c> 铺底。</summary>
    internal static readonly Color BarOverlay = Layer(DesignTokens.ColorBarOverlayAndroid, DesignTokens.ShellBarOverlayOpacityAndroid);
    /// <summary>主屏按钮常态底材（TOOLBAR-10）：白色染色按 <c>shell.glass.button.tint.opacity@android</c> 叠色，不降低内容不透明度。</summary>
    internal static readonly Color GlassButtonFill = Layer(DesignTokens.ColorToolbarButtonTint, DesignTokens.ShellGlassButtonTintOpacityAndroid);
    /// <summary>
    /// 带衬底 EXIF 块叠在照片层模糊上的深灰色层（SHELL-12@android）：
    /// <c>color.toolbar.block.background</c> 按 <c>shell.glass.android.exif.tint.opacity@android</c> 降低不透明度；
    /// API 24–30 不取样，仍用不透明正式色层。
    /// </summary>
    internal static readonly Color ExifGlassTint = Layer(DesignTokens.ColorToolbarBlockBackground, DesignTokens.ShellGlassAndroidExifTintOpacity);
    /// <summary>分隔线。</summary>
    internal static readonly Color Divider = FromToken(DesignTokens.ColorDivider);
    /// <summary>控件底色（白 2/15，浮层与下拉等系统外观）。</summary>
    internal static readonly Color ControlFill = Color.ParseColor("#262626")!;
    /// <summary>控件描边。</summary>
    internal static readonly Color ControlBorder = Color.ParseColor("#444444")!;
    /// <summary>卡片与 EXIF/评分块底色（FILM-05、TOOLBAR-10）。</summary>
    internal static readonly Color CardBackground = FromToken(DesignTokens.ColorFilmCardBackground);
    /// <summary>主文字。</summary>
    internal static readonly Color TextPrimary = FromToken(DesignTokens.ColorTextPrimary);
    /// <summary>次要文字。</summary>
    internal static readonly Color TextSecondary = FromToken(DesignTokens.ColorTextSecondary);
    /// <summary>三级文字（空态副标题分隔字）。</summary>
    internal static readonly Color TextTertiary = FromToken(DesignTokens.ColorTextTertiary);
    /// <summary>蓝色高亮（当前卡片）。</summary>
    internal static readonly Color Accent = FromToken(DesignTokens.ColorFilmCardCurrentBorder);
    /// <summary>星级金色（FILM-05）。</summary>
    internal static readonly Color StarActive = FromToken(DesignTokens.ColorStarOn);
    /// <summary>星级未点亮（FILM-05）。</summary>
    internal static readonly Color StarInactive = FromToken(DesignTokens.ColorStarOff);
    /// <summary>0 星清零叉（FILM-05）。</summary>
    internal static readonly Color StarClear = FromToken(DesignTokens.ColorStarClear);
    /// <summary>星级冲突警示三角（RATING-04，<c>color.star.conflict</c>）。</summary>
    internal static readonly Color StarConflict = FromToken(DesignTokens.ColorStarConflict);
    /// <summary>抖动徽标橙色。</summary>
    internal static readonly Color ShakeBadge = FromToken(DesignTokens.ColorFilmShakeBadge);
    /// <summary>相似聚类面板底色（SIM-01）。</summary>
    internal static readonly Color SimPanelBackground = FromToken(DesignTokens.ColorSimPanelBackground);
    /// <summary>相似聚类面板描边（SIM-01）。</summary>
    internal static readonly Color SimPanelBorder = FromToken(DesignTokens.ColorSimPanelBorder);
    /// <summary>相似聚类说明文字（SIM-01）。</summary>
    internal static readonly Color SimCaption = FromToken(DesignTokens.ColorSimCaption);
    /// <summary>相似聚类空态占位文字（SIM-02）。</summary>
    internal static readonly Color SimPlaceholder = FromToken(DesignTokens.ColorSimPlaceholder);
    /// <summary>相似聚类错误文字（SIM-10）。</summary>
    internal static readonly Color SimError = FromToken(DesignTokens.ColorSimError);
    /// <summary>相似聚类动作按钮底色（SIM-03）。</summary>
    internal static readonly Color SimActionBackground = FromToken(DesignTokens.ColorSimActionBackground);
    /// <summary>相似聚类动作按钮描边（SIM-03）。</summary>
    internal static readonly Color SimActionBorder = FromToken(DesignTokens.ColorSimActionBorder);
    /// <summary>相似聚类动作按钮文字（SIM-03）。</summary>
    internal static readonly Color SimActionForeground = FromToken(DesignTokens.ColorSimActionForeground);
    /// <summary>相似聚类提取进度文字（SIM-03）。</summary>
    internal static readonly Color SimProgress = FromToken(DesignTokens.ColorSimProgress);
    /// <summary>相似聚类开关选中底色（FILTER-01）。</summary>
    internal static readonly Color FilterToggleCheckedBackground = FromToken(DesignTokens.ColorFilterToggleCheckedBackground);
    /// <summary>相似聚类开关选中描边（FILTER-01）。</summary>
    internal static readonly Color FilterToggleCheckedBorder = FromToken(DesignTokens.ColorFilterToggleCheckedBorder);
    /// <summary>相似聚类开关选中文字（FILTER-01）。</summary>
    internal static readonly Color FilterToggleCheckedForeground = FromToken(DesignTokens.ColorFilterToggleCheckedForeground);
    /// <summary>相似聚类开关未选中描边（FILTER-01）。</summary>
    internal static readonly Color FilterToggleBorder = FromToken(DesignTokens.ColorFilterToggleBorder);
    /// <summary>相似聚类开关未选中文字（FILTER-01）。</summary>
    internal static readonly Color FilterToggleForeground = FromToken(DesignTokens.ColorFilterToggleForeground);
    /// <summary>设置页持久化失败状态文字（SETTINGS-14）。</summary>
    internal static readonly Color ErrorText = FromToken(DesignTokens.ColorSimError);
    internal static readonly Color WarningText = FromToken(DesignTokens.ColorSettingsWarning);

    /// <summary>共享 Fluent Regular 在 Assets 中的路径。</summary>
    internal const string IconFontAsset = "Icons/FluentSystemIcons-Regular.ttf";

    /// <summary>共享 Fluent Filled 在 Assets 中的路径。</summary>
    internal const string IconSolidFontAsset = "Icons/FluentSystemIcons-Filled.ttf";

    private static Typeface? _iconTypeface;
    private static Typeface? _iconSolidTypeface;

    /// <summary>把设计令牌的 0xAARRGGBB 数值转成 Android 颜色。</summary>
    /// <param name="argb">设计令牌颜色。</param>
    /// <returns>Android 颜色。</returns>
    internal static Color FromToken(uint argb) => Color.Argb(
        (int)((argb >> 24) & 0xFF),
        (int)((argb >> 16) & 0xFF),
        (int)((argb >> 8) & 0xFF),
        (int)(argb & 0xFF))!;

    /// <summary>按设计令牌颜色与不透明度合成独立色层（原 RGB 保留，alpha 取比例）。</summary>
    /// <param name="argb">设计令牌颜色。</param>
    /// <param name="opacity">0–1 的不透明度。</param>
    /// <returns>带合成 alpha 的颜色。</returns>
    internal static Color Layer(uint argb, double opacity) => Color.Argb(
        Math.Clamp((int)Math.Round(255 * opacity), 0, 255),
        (int)((argb >> 16) & 0xFF),
        (int)((argb >> 8) & 0xFF),
        (int)(argb & 0xFF))!;

    /// <summary>图标 dp → px 转换；图标是固定几何图形，不随系统文字缩放。</summary>
    /// <param name="context">上下文。</param>
    /// <param name="dp">dp 值。</param>
    /// <returns>像素值。</returns>
    internal static float Dp(Context context, double dp) => (float)(dp * (context.Resources?.DisplayMetrics?.Density ?? 1));

    /// <summary>
    /// 归一字体排版框与字号之比：1.2em 方形单元，全部缩放后的墨迹都落在框内。
    /// 与随包字体由同一生成器产出。
    /// </summary>
    internal const double IconCellEm = IconCatalog.FluentCellEm;

    /// <summary>
    /// 图标槽边长（px）：同一份 dp 令牌向上取整到整数像素，只用于布局尺寸。
    /// 字形画笔的字号必须直接用 <see cref="Dp"/>（精确浮点像素），不拿这个取整值当字号。
    /// </summary>
    /// <param name="context">上下文。</param>
    /// <param name="dp">dp 值（图标尺寸令牌）。</param>
    /// <returns>槽位像素边长。</returns>
    internal static int IconSlotPx(Context context, double dp) => Math.Max(1, (int)Math.Ceiling(Dp(context, dp)));

    /// <summary>
    /// 没有专用槽位令牌时的图标槽边长（px）：字号对应的 <see cref="IconCellEm"/> 排版框向上取整，
    /// 另加每侧 1px 光栅化与像素取整余量，因此任何字形墨迹都完整落在槽内，不需要也不允许按槽位缩小字号。
    /// </summary>
    /// <param name="context">上下文。</param>
    /// <param name="sizeDp">字号（dp 令牌）。</param>
    /// <returns>槽位像素边长。</returns>
    internal static int GlyphSlotPx(Context context, double sizeDp)
        => Math.Max(1, (int)Math.Ceiling(Dp(context, sizeDp) * IconCellEm) + 2);

    /// <summary>dp 密度。</summary>
    /// <param name="context">上下文。</param>
    /// <returns>密度。</returns>
    internal static double Density(Context context) => context.Resources?.DisplayMetrics?.Density ?? 1;

    /// <summary>sp → px（显式字号，跟随系统字体缩放但布局尺寸仍按 dp）。</summary>
    /// <param name="context">上下文。</param>
    /// <param name="sp">sp 值。</param>
    /// <returns>像素值。</returns>
    internal static float Sp(Context context, double sp)
    {
        var metrics = context.Resources?.DisplayMetrics;
        return metrics == null ? (float)sp : TypedValue.ApplyDimension(ComplexUnitType.Sp, (float)sp, metrics);
    }

    /// <summary>加载随包 Fluent Regular；资产缺失时显式失败，不回退系统字体。</summary>
    internal static Typeface? IconTypeface(Context context)
    {
        if (_iconTypeface != null) return _iconTypeface;
        try
        {
            _iconTypeface = Typeface.CreateFromAsset(context.Assets, IconFontAsset);
            return _iconTypeface;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"缺少随包图标字体 {IconFontAsset}", exception);
        }
    }

    /// <summary>加载随包 Fluent Filled；资产缺失时显式失败，不回退 Regular 或系统字体。</summary>
    internal static Typeface? IconSolidTypeface(Context context)
    {
        if (_iconSolidTypeface != null) return _iconSolidTypeface;
        try
        {
            _iconSolidTypeface = Typeface.CreateFromAsset(context.Assets, IconSolidFontAsset);
            return _iconSolidTypeface;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"缺少随包图标字体 {IconSolidFontAsset}", exception);
        }
    }

    /// <summary>创建抗锯齿画笔。</summary>
    /// <param name="color">颜色。</param>
    /// <param name="width">线宽（px，0 表示填充）。</param>
    /// <returns>画笔。</returns>
    internal static Paint Paint(Color color, float width = 0)
    {
        var paint = new Paint(PaintFlags.AntiAlias) { Color = color };
        if (width > 0)
        {
            paint.SetStyle(global::Android.Graphics.Paint.Style.Stroke);
            paint.StrokeWidth = width;
        }
        return paint;
    }

    /// <summary>创建文本画笔。</summary>
    /// <param name="context">上下文。</param>
    /// <param name="sizeSp">字号（sp）。</param>
    /// <param name="color">颜色。</param>
    /// <param name="bold">是否粗体。</param>
    /// <returns>画笔。</returns>
    internal static Paint TextPaint(Context context, double sizeSp, Color color, bool bold = false)
    {
        var paint = Paint(color);
        paint.TextSize = Sp(context, sizeSp);
        paint.TextAlign = global::Android.Graphics.Paint.Align.Center;
        if (bold) paint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
        return paint;
    }

    /// <summary>按尾部截断文本到给定宽度。</summary>
    /// <param name="text">原文。</param>
    /// <param name="paint">画笔。</param>
    /// <param name="widthPx">可用宽度（px）。</param>
    /// <returns>截断后的文本。</returns>
    internal static string Ellipsize(string? text, Paint paint, float widthPx)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (paint.MeasureText(text) <= widthPx) return text;
        const string ellipsis = "…";
        float ellipsisWidth = paint.MeasureText(ellipsis);
        int low = 0, high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = text[..mid];
            if (paint.MeasureText(candidate) + ellipsisWidth <= widthPx) low = mid;
            else high = mid - 1;
        }
        return low <= 0 ? ellipsis : text[..low] + ellipsis;
    }

    /// <summary>绘制居中单行文本。</summary>
    /// <param name="canvas">画布。</param>
    /// <param name="text">文本。</param>
    /// <param name="paint">画笔。</param>
    /// <param name="centerX">中心横坐标。</param>
    /// <param name="baseline">基线纵坐标。</param>
    /// <param name="maxWidth">最大宽度（px，用于尾部截断）。</param>
    internal static void DrawCentered(Canvas canvas, string? text, Paint paint, float centerX, float baseline, float maxWidth)
    {
        string clipped = Ellipsize(text, paint, maxWidth);
        canvas.DrawText(clipped, centerX, baseline, paint);
    }

    /// <summary>
    /// 按字体单元中心绘制 Fluent UI System Icons 字形：随包字体把每个缩放后的字形放在 1.2em 方形单元中心，
    /// ascent/descent 对称、无行距、advance 一致，所以横向居中 advance（<see cref="global::Android.Graphics.Paint.Align.Center"/>）、
    /// 纵向取字体度量的上下中线作基线即可，不必逐帧测量墨迹边界。
    /// </summary>
    internal static void DrawGlyphCentered(Canvas canvas, string glyph, Paint paint, float centerX, float centerY)
    {
        if (string.IsNullOrEmpty(glyph)) return;
        var alignment = paint.TextAlign;
        paint.TextAlign = global::Android.Graphics.Paint.Align.Center;
        canvas.DrawText(glyph, centerX, GlyphBaseline(paint, centerY), paint);
        paint.TextAlign = alignment;
    }

    /// <summary>字形单元上下中线对应的基线：字体对称时即中心线，留白不对称时也不会把字形推离中心。</summary>
    /// <param name="paint">已设好字体与字号的画笔。</param>
    /// <param name="centerY">单元中心纵坐标。</param>
    /// <returns>基线纵坐标。</returns>
    internal static float GlyphBaseline(Paint paint, float centerY)
        => paint.GetFontMetrics() is { } metrics ? centerY - (metrics.Ascent + metrics.Descent) / 2 : centerY;

    /// <summary>按墨迹边界居中绘制文本：叠在图标上的数字等系统字体内容走这里，字形单元本身不保证居中。</summary>
    internal static void DrawTextInkCentered(Canvas canvas, string text, Paint paint, float centerX, float centerY)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var bounds = new Rect();
        var alignment = paint.TextAlign;
        paint.TextAlign = global::Android.Graphics.Paint.Align.Left;
        paint.GetTextBounds(text, 0, text.Length, bounds);
        canvas.DrawText(text, centerX - bounds.ExactCenterX(), centerY - bounds.ExactCenterY(), paint);
        paint.TextAlign = alignment;
    }

    /// <summary>绘制带圆角的控件底与描边。</summary>
    /// <param name="canvas">画布。</param>
    /// <param name="rect">矩形（px）。</param>
    /// <param name="radius">圆角（px）。</param>
    /// <param name="fill">填充色。</param>
    /// <param name="border">描边色（可空）。</param>
    /// <param name="borderWidth">描边宽度（px）。</param>
    internal static void DrawRounded(Canvas canvas, RectF rect, float radius, Color fill, Color? border = null, float borderWidth = 1)
    {
        using var fillPaint = Paint(fill);
        canvas.DrawRoundRect(rect, radius, radius, fillPaint);
        if (border is { } borderColor)
        {
            using var strokePaint = Paint(borderColor, borderWidth);
            canvas.DrawRoundRect(rect, radius, radius, strokePaint);
        }
    }

    /// <summary>View 的 dp 宽度转 px（布局用）。</summary>
    /// <param name="view">视图。</param>
    /// <param name="dp">dp 值。</param>
    /// <returns>像素值。</returns>
    internal static int DpPx(View view, double dp) => (int)Math.Round(dp * (view.Resources?.DisplayMetrics?.Density ?? 1));
}
