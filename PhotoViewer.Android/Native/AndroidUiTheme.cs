using System;
using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using PhotoViewer.Core.Design;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 原生壳视觉常量与绘制辅助（A3）：固定深色、显式圆角/字重，不引用 OEM 主题派生属性。
/// 颜色与 iOS 原生壳同口径（#111 背景、白色 2/15 控件底、#444 描边），尺寸全部以 dp 表达。
/// </summary>
internal static class AndroidUiTheme
{
    /// <summary>窗口背景。</summary>
    internal static readonly Color Background = FromToken(DesignTokens.ColorBarBackground);
    /// <summary>主图区背景。</summary>
    internal static readonly Color PhotoBackground = Color.Black!;
    /// <summary>栏位底色（SHELL-01）。</summary>
    internal static readonly Color BarBackground = FromToken(DesignTokens.ColorBarBackground);
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
    /// <summary>蓝色高亮（当前卡片）。</summary>
    internal static readonly Color Accent = FromToken(DesignTokens.ColorFilmCardCurrentBorder);
    /// <summary>星级金色（FILM-05）。</summary>
    internal static readonly Color StarActive = FromToken(DesignTokens.ColorStarOn);
    /// <summary>星级未点亮（FILM-05）。</summary>
    internal static readonly Color StarInactive = FromToken(DesignTokens.ColorStarOff);
    /// <summary>0 星清零叉（FILM-05）。</summary>
    internal static readonly Color StarClear = FromToken(DesignTokens.ColorStarClear);
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

    /// <summary>图标字体（uicons）在 Assets 中的路径。</summary>
    internal const string IconFontAsset = "Fonts/uicons-regular-rounded.ttf";

    private static Typeface? _iconTypeface;
    private static bool _iconLoadFailed;

    /// <summary>把设计令牌的 0xAARRGGBB 数值转成 Android 颜色。</summary>
    /// <param name="argb">设计令牌颜色。</param>
    /// <returns>Android 颜色。</returns>
    internal static Color FromToken(uint argb) => Color.Argb(
        (int)((argb >> 24) & 0xFF),
        (int)((argb >> 16) & 0xFF),
        (int)((argb >> 8) & 0xFF),
        (int)(argb & 0xFF))!;

    /// <summary>dp → px 转换。</summary>
    /// <param name="context">上下文。</param>
    /// <param name="dp">dp 值。</param>
    /// <returns>像素值。</returns>
    internal static float Dp(Context context, double dp) => (float)(dp * (context.Resources?.DisplayMetrics?.Density ?? 1));

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

    /// <summary>加载 uicons 图标字体；缺失时回退系统字体并只记录一次。</summary>
    /// <param name="context">上下文。</param>
    /// <returns>图标字体或 null。</returns>
    internal static Typeface? IconTypeface(Context context)
    {
        if (_iconTypeface != null || _iconLoadFailed) return _iconTypeface;
        try
        {
            _iconTypeface = Typeface.CreateFromAsset(context.Assets, IconFontAsset);
        }
        catch (Exception exception)
        {
            _iconLoadFailed = true;
            Console.WriteLine($"[Native] 图标字体加载失败，回退系统字体：{exception.Message}");
        }
        return _iconTypeface;
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
