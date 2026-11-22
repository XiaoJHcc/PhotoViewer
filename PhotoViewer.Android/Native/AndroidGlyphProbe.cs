using System;
using Android.Content;
using Android.Graphics;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 字形墨迹探针（验收用）：把产品真实绘制的字形与同一字号、无边界约束的参照字形按设计锚点
/// （排版框中心）对齐比较。参照画在远大于墨迹的位图上，不可能被裁切；产品墨迹一旦被图标槽或
/// 控件边界削掉（含对称裁切），四边余量、外接尺寸与面积都会与参照不一致。
/// 比较允许光栅化与像素取整差异，但不允许按槽位缩放或平移字形。
/// </summary>
internal static class AndroidGlyphProbe
{
    /// <summary>墨迹阈值：半透明以上算作墨迹，抗锯齿边缘仍参与比较。</summary>
    private const int AlphaThreshold = 128;

    /// <summary>四边余量与逐像素比较的容差（px）：只放行光栅化与像素取整。</summary>
    private const double EdgeTolerancePx = 1.5;

    /// <summary>墨迹读数：二值掩码与外接矩形。</summary>
    internal sealed class Ink
    {
        internal Ink(int width, int height, bool[] pixels, int count, int left, int top, int right, int bottom)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
            Count = count;
            Bounds = new Rect(left, top, right, bottom);
        }

        internal int Width { get; }
        internal int Height { get; }
        internal bool[] Pixels { get; }
        internal int Count { get; }
        internal Rect Bounds { get; }
        internal bool Found => Count > 0;

        internal bool At(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && Pixels[y * Width + x];

        /// <summary>像素或其 8 邻域内有墨迹：吸收亚像素落位带来的 1px 相位差。</summary>
        internal bool Near(int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (At(x + dx, y + dy)) return true;
            return false;
        }
    }

    /// <summary>产品绘制：按视图自身尺寸取离屏位图上的字形墨迹（透明底、实际着色与透明度）。</summary>
    /// <param name="width">视图宽（px）。</param>
    /// <param name="height">视图高（px）。</param>
    /// <param name="draw">产品真实绘制路径。</param>
    /// <returns>墨迹读数。</returns>
    internal static Ink Capture(int width, int height, Action<Canvas> draw)
    {
        if (width <= 0 || height <= 0) return Empty();
        using var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
        using (var canvas = new Canvas(bitmap))
            draw(canvas);
        return FromBitmap(bitmap, width, height);
    }

    /// <summary>
    /// 参照绘制：在超尺寸位图（≥ 两倍字号）上按排版框中心绘制同一字形与叠字，并返回墨迹读数；
    /// 锚点即位图中心，因此不可能被裁切。
    /// </summary>
    /// <param name="context">上下文（字体来源）。</param>
    /// <param name="glyph">字形。</param>
    /// <param name="solid">是否实心图标字体。</param>
    /// <param name="digit">叠字（评分命令的 1–5；null 表示没有）。</param>
    /// <param name="fontPx">字形字号（px，与产品完全相同）。</param>
    /// <param name="digitPx">叠字字号（px）。</param>
    /// <returns>墨迹读数。</returns>
    internal static Ink Reference(Context context, string glyph, bool solid, string? digit, float fontPx, float digitPx)
    {
        int size = Math.Max(8, (int)Math.Ceiling(fontPx * 2) + 8);
        using var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
        using (var canvas = new Canvas(bitmap))
        using (var glyphPaint = AndroidUiTheme.Paint(Color.White))
        {
            glyphPaint.TextSize = fontPx;
            if ((solid ? AndroidUiTheme.IconSolidTypeface(context) : AndroidUiTheme.IconTypeface(context)) is { } typeface)
                glyphPaint.SetTypeface(typeface);
            float anchorX = size / 2f, anchorY = size / 2f;
            AndroidUiTheme.DrawGlyphCentered(canvas, glyph, glyphPaint, anchorX, anchorY);
            if (!string.IsNullOrEmpty(digit) && digitPx > 0)
            {
                using var digitPaint = AndroidUiTheme.Paint(Color.White);
                digitPaint.TextSize = digitPx;
                digitPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
                AndroidUiTheme.DrawTextInkCentered(canvas, digit!, digitPaint, anchorX, anchorY);
            }
        }
        return FromBitmap(bitmap, size, size);
    }

    /// <summary>
    /// 对齐比较：按设计锚点把参照墨迹平移到产品坐标，逐边比较墨迹到锚点的距离、墨迹面积与逐像素覆盖，
    /// 另核对墨迹完整落在图标槽与控件内。返回是否一致与明细。
    /// </summary>
    /// <param name="product">产品墨迹。</param>
    /// <param name="reference">参照墨迹（锚点为位图中心）。</param>
    /// <param name="anchorX">产品绘制锚点横坐标（排版框中心，px）。</param>
    /// <param name="anchorY">产品绘制锚点纵坐标（px）。</param>
    /// <param name="slot">产品图标槽矩形（px）。</param>
    /// <param name="viewWidth">产品控件宽（px）。</param>
    /// <param name="viewHeight">产品控件高（px）。</param>
    /// <param name="density">密度（明细换算 dp）。</param>
    /// <returns>是否一致与明细。</returns>
    internal static (bool Ok, string Detail) Compare(Ink product, Ink reference, float anchorX, float anchorY,
        Rect slot, int viewWidth, int viewHeight, double density)
    {
        if (!reference.Found) return (false, "参照墨迹为空");
        if (!product.Found) return (false, "产品墨迹为空");
        float referenceAnchorX = reference.Width / 2f, referenceAnchorY = reference.Height / 2f;
        double left = Math.Abs((product.Bounds.Left - anchorX) - (reference.Bounds.Left - referenceAnchorX));
        double right = Math.Abs((product.Bounds.Right - anchorX) - (reference.Bounds.Right - referenceAnchorX));
        double top = Math.Abs((product.Bounds.Top - anchorY) - (reference.Bounds.Top - referenceAnchorY));
        double bottom = Math.Abs((product.Bounds.Bottom - anchorY) - (reference.Bounds.Bottom - referenceAnchorY));
        bool edges = left <= EdgeTolerancePx && right <= EdgeTolerancePx
            && top <= EdgeTolerancePx && bottom <= EdgeTolerancePx;
        double areaTolerance = Math.Max(3, reference.Count * 0.08);
        double areaDelta = product.Count - reference.Count;
        bool area = Math.Abs(areaDelta) <= areaTolerance;

        float deltaX = anchorX - referenceAnchorX, deltaY = anchorY - referenceAnchorY;
        int missing = 0, extra = 0;
        for (int y = 0; y < reference.Height; y++)
        for (int x = 0; x < reference.Width; x++)
        {
            if (!reference.Pixels[y * reference.Width + x]) continue;
            if (!product.Near((int)Math.Round(x + deltaX), (int)Math.Round(y + deltaY))) missing++;
        }
        for (int y = 0; y < product.Height; y++)
        for (int x = 0; x < product.Width; x++)
        {
            if (!product.Pixels[y * product.Width + x]) continue;
            if (!reference.Near((int)Math.Round(x - deltaX), (int)Math.Round(y - deltaY))) extra++;
        }
        bool covered = missing <= reference.Count * 0.15 && extra <= product.Count * 0.15;

        // 裁切会把墨迹贴到图标槽或控件边界：要求四边都至少留 1px，与参照比较一起构成裁切证明。
        int marginLeft = product.Bounds.Left - slot.Left;
        int marginTop = product.Bounds.Top - slot.Top;
        int marginRight = slot.Right - product.Bounds.Right;
        int marginBottom = slot.Bottom - product.Bounds.Bottom;
        bool insideSlot = slot.Width() > 0 && marginLeft >= 1 && marginTop >= 1 && marginRight >= 1 && marginBottom >= 1;
        bool insideView = product.Bounds.Left >= 1 && product.Bounds.Top >= 1
            && product.Bounds.Right <= viewWidth - 1 && product.Bounds.Bottom <= viewHeight - 1;
        bool ok = edges && area && covered && insideSlot && insideView;
        string detail = $"density={density:0.##} ref={reference.Bounds}/{reference.Count}px "
            + $"ink={product.Bounds}/{product.Count}px "
            + $"edges=({left:0.##},{top:0.##},{right:0.##},{bottom:0.##}) "
            + $"area={areaDelta:+0;-0;0}/{areaTolerance:0} "
            + $"missing={missing}/{reference.Count} extra={extra}/{product.Count} "
            + $"slot={slot} margins=({marginLeft},{marginTop},{marginRight},{marginBottom}) "
            + $"insideSlot={insideSlot} insideView={insideView}";
        return (ok, detail);
    }

    private static Ink Empty() => new(0, 0, [], 0, 0, 0, 0, 0);

    private static Ink FromBitmap(Bitmap bitmap, int width, int height)
    {
        var pixels = new int[width * height];
        bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);
        var mask = new bool[pixels.Length];
        int count = 0, left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (Color.GetAlphaComponent(pixels[y * width + x]) < AlphaThreshold) continue;
            mask[y * width + x] = true;
            count++;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + 1);
            bottom = Math.Max(bottom, y + 1);
        }
        return right < 0
            ? new Ink(width, height, mask, 0, 0, 0, 0, 0)
            : new Ink(width, height, mask, count, left, top, right, bottom);
    }
}
