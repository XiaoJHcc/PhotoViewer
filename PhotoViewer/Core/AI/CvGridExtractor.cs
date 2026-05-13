using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace PhotoViewer.Core.AI;

/// <summary>
/// CV 网格 v2 提取器（中心采样 + Marziliano 边宽量化）。
/// 每个 16×16 格只在中心取一个 <see cref="CvGridResult.BlockSize"/> 大小的采样块，
/// 在块内测"强边的跨像素宽度"，直接输出像素量化结果。
/// 相较 v0/v1 放弃了"每格代表全格"的假设 —— 采样命中墙壁/纯色时以 NaN 标记，诊断页画灰。
/// </summary>
public static class CvGridExtractor
{
    private const int Grid = CvGridResult.GridSize;
    private const int MaxHalfWidth = 8;
    private const int BucketCount = 8;
    private const int BinCount = 64;
    private const int MinEdgesForRead = 80;
    private const int MinEdgesPerBucket = 5;
    private const float PlateauRatio = 0.25f;
    private const float P20Fraction = 0.2f;
    /// <summary>边强度绝对下限（luma 0-255 标度的 Sobel 幅值）；块内 p90 不及此则整块判 NaN。</summary>
    private const float TauEdgeFloor = 30f;

    /// <summary>对外入口：异步包一层，实际计算在 <see cref="Extract"/> 里。</summary>
    public static Task<CvGridResult> ExtractAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return Task.Run(() => Extract(bitmap, cancellationToken), cancellationToken);
    }

    private static CvGridResult Extract(Bitmap bitmap, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (luma, w, h) = ReadLuminance(bitmap);

        var data = new float[CvGridResult.DataLength];
        // 先把所有 NaN 填满；命中的格再覆盖回去，未命中/不足的自动留 NaN。
        for (int i = 0; i < data.Length; i++) data[i] = float.NaN;

        // 根据短边自适应块尺寸：短边/Grid 是格心间距，用它作为块边长可以几乎不重叠。
        int shortSide = Math.Min(w, h);
        int blockSize = Math.Clamp(shortSide / Grid, CvGridResult.MinBlockSize, CvGridResult.MaxBlockSize);
        if (shortSide < CvGridResult.MinBlockSize)
        {
            return new CvGridResult { Version = CvGridResult.CurrentVersion, Data = data };
        }

        Parallel.For(0, Grid * Grid, idx =>
        {
            int gy = idx / Grid;
            int gx = idx % Grid;
            // 格中心像素坐标（连续坐标取整）
            int cx = (int)((gx + 0.5) * w / Grid);
            int cy = (int)((gy + 0.5) * h / Grid);
            int half = blockSize / 2;
            int bx0 = cx - half;
            int by0 = cy - half;
            int bx1 = cx + half;
            int by1 = cy + half;
            // 越界时平移；平移后仍超短边就收缩。
            if (bx0 < 0) { bx1 -= bx0; bx0 = 0; }
            if (by0 < 0) { by1 -= by0; by0 = 0; }
            if (bx1 > w) { bx0 -= (bx1 - w); bx1 = w; }
            if (by1 > h) { by0 -= (by1 - h); by1 = h; }
            bx0 = Math.Max(0, bx0);
            by0 = Math.Max(0, by0);
            int bw = bx1 - bx0;
            int bh = by1 - by0;
            if (bw < CvGridResult.MinBlockSize || bh < CvGridResult.MinBlockSize) return;

            ComputeCell(luma, w, h, bx0, by0, bw, bh, gy, gx, data);
        });

        return new CvGridResult { Version = CvGridResult.CurrentVersion, Data = data };
    }

    /// <summary>
    /// 单格处理：在块内算 Sobel → 自适应 τ_edge → NMS → Marziliano 测宽 → 方向桶。
    /// 6 标量直接写入输出数组。
    /// </summary>
    private static void ComputeCell(
        float[] luma, int w, int h,
        int bx0, int by0, int bw, int bh,
        int gy, int gx, float[] data)
    {
        int px = bw * bh;
        var magArr = ArrayPool<float>.Shared.Rent(px);
        var sxArr = ArrayPool<float>.Shared.Rent(px);
        var syArr = ArrayPool<float>.Shared.Rent(px);
        try
        {
            double lumaSum = 0;
            long lumaSamples = 0;
            float magMax = 0f;

            // Sobel：块内每像素求 sx/sy/mag；全图外圈 1 像素填 0。
            for (int y = 0; y < bh; y++)
            {
                int absY = by0 + y;
                bool rowEdge = absY <= 0 || absY >= h - 1;
                int row = absY * w;
                int rowPrev = row - w;
                int rowNext = row + w;
                for (int x = 0; x < bw; x++)
                {
                    int absX = bx0 + x;
                    int idx = y * bw + x;
                    float c = luma[row + absX];
                    lumaSum += c;
                    lumaSamples++;
                    if (rowEdge || absX <= 0 || absX >= w - 1)
                    {
                        magArr[idx] = 0f;
                        sxArr[idx] = 0f;
                        syArr[idx] = 0f;
                        continue;
                    }
                    float l = luma[row + absX - 1];
                    float r = luma[row + absX + 1];
                    float u = luma[rowPrev + absX];
                    float d = luma[rowNext + absX];
                    float ul = luma[rowPrev + absX - 1];
                    float ur = luma[rowPrev + absX + 1];
                    float dl = luma[rowNext + absX - 1];
                    float dr = luma[rowNext + absX + 1];
                    float sx = (ur + 2f * r + dr) - (ul + 2f * l + dl);
                    float sy = (dl + 2f * d + dr) - (ul + 2f * u + ur);
                    float mag = MathF.Sqrt(sx * sx + sy * sy);
                    sxArr[idx] = sx;
                    syArr[idx] = sy;
                    magArr[idx] = mag;
                    if (mag > magMax) magMax = mag;
                }
            }

            float lumaMean = lumaSamples > 0 ? (float)(lumaSum / lumaSamples) : 0f;
            WriteScalar(data, 5, gy, gx, lumaMean);

            if (magMax < 1e-3f)
            {
                WriteScalar(data, 0, gy, gx, 0f);
                return;
            }

            // 自适应 τ_edge = 块内 mag 的 p90（用 64 bin 直方图估算）。
            Span<int> hist = stackalloc int[BinCount];
            int nonZero = 0;
            for (int i = 0; i < px; i++)
            {
                float m = magArr[i];
                if (m <= 1e-3f) continue;
                int b = (int)(m / magMax * BinCount);
                if (b >= BinCount) b = BinCount - 1;
                hist[b]++;
                nonZero++;
            }
            int target = (int)(nonZero * 0.9);
            int cum = 0;
            int p90Bin = BinCount - 1;
            for (int b = 0; b < BinCount; b++)
            {
                cum += hist[b];
                if (cum >= target) { p90Bin = b; break; }
            }
            float tauEdge = (p90Bin + 0.5f) / BinCount * magMax;
            // 绝对下限兜底：纯色 / 极弱纹理块的 p90 可能远低于真边阈值，强行采样会挤出"假边"。
            if (tauEdge < TauEdgeFloor)
            {
                WriteScalar(data, 0, gy, gx, 0f);
                return;
            }
            float tauPlateau = tauEdge * PlateauRatio;

            var widths = new List<float>(1024);
            var buckets = new List<float>[BucketCount];
            for (int b = 0; b < BucketCount; b++) buckets[b] = new List<float>(64);

            for (int y = 1; y < bh - 1; y++)
            {
                int row = y * bw;
                for (int x = 1; x < bw - 1; x++)
                {
                    int idx = row + x;
                    float mag = magArr[idx];
                    if (mag < tauEdge) continue;

                    float sx = sxArr[idx];
                    float sy = syArr[idx];
                    bool horizontal = MathF.Abs(sx) >= MathF.Abs(sy);

                    // NMS：只沿主导轴做局部极大抑制。
                    if (horizontal)
                    {
                        if (magArr[idx - 1] > mag || magArr[idx + 1] > mag) continue;
                    }
                    else
                    {
                        if (magArr[idx - bw] > mag || magArr[idx + bw] > mag) continue;
                    }

                    int dx = horizontal ? (sx >= 0 ? 1 : -1) : 0;
                    int dy = horizontal ? 0 : (sy >= 0 ? 1 : -1);

                    int right = StepUntilPlateau(magArr, bw, bh, x, y, dx, dy, tauPlateau);
                    int left = StepUntilPlateau(magArr, bw, bh, x, y, -dx, -dy, tauPlateau);
                    if (right <= 0 || left <= 0) continue;

                    float width = right + left;
                    widths.Add(width);

                    double theta = Math.Atan2(sy, sx);
                    double norm = (theta + Math.PI) / Math.PI;
                    if (norm >= 1.0) norm -= 1.0;
                    int bin = (int)(norm * BucketCount);
                    if (bin >= BucketCount) bin = BucketCount - 1;
                    buckets[bin].Add(width);
                }
            }

            int edgeCount = widths.Count;
            WriteScalar(data, 0, gy, gx, edgeCount);
            if (edgeCount < MinEdgesForRead) return;

            widths.Sort();
            int p20End = Math.Max(1, (int)(widths.Count * P20Fraction));
            double p20Sum = 0;
            for (int i = 0; i < p20End; i++) p20Sum += widths[i];
            float p20 = (float)(p20Sum / p20End);
            float median = widths[widths.Count / 2];

            float minBw = float.PositiveInfinity;
            float maxBw = float.NegativeInfinity;
            int maxBucket = -1;
            for (int b = 0; b < BucketCount; b++)
            {
                if (buckets[b].Count < MinEdgesPerBucket) continue;
                buckets[b].Sort();
                float bm = buckets[b][buckets[b].Count / 2];
                if (bm < minBw) minBw = bm;
                if (bm > maxBw) { maxBw = bm; maxBucket = b; }
            }
            float spread = maxBucket >= 0 && !float.IsPositiveInfinity(minBw) ? maxBw - minBw : float.NaN;
            float direction = maxBucket >= 0 ? (maxBucket + 0.5f) / BucketCount * MathF.PI : float.NaN;

            WriteScalar(data, 1, gy, gx, p20);
            WriteScalar(data, 2, gy, gx, median);
            WriteScalar(data, 3, gy, gx, spread);
            WriteScalar(data, 4, gy, gx, direction);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(magArr);
            ArrayPool<float>.Shared.Return(sxArr);
            ArrayPool<float>.Shared.Return(syArr);
        }
    }

    /// <summary>
    /// 从 (x,y) 出发沿 (dx,dy) 单向步进，直到 mag 跌破 τ_plateau 或超过 MaxHalfWidth；
    /// 返回走出的步数。撞到块边界返回 0（整条边丢弃）。
    /// </summary>
    private static int StepUntilPlateau(float[] mag, int bw, int bh, int x, int y, int dx, int dy, float tauPlateau)
    {
        for (int k = 1; k <= MaxHalfWidth; k++)
        {
            int nx = x + k * dx;
            int ny = y + k * dy;
            if (nx < 0 || nx >= bw || ny < 0 || ny >= bh) return 0;
            if (mag[ny * bw + nx] < tauPlateau) return k;
        }
        return MaxHalfWidth;
    }

    private static void WriteScalar(float[] data, int scalar, int gy, int gx, float value)
    {
        data[scalar * CvGridResult.PlaneLength + gy * Grid + gx] = value;
    }

    /// <summary>
    /// 把任意格式 bitmap 归一到 BGRA8888 后按 Rec.709 转灰度。
    /// 短边不足 MinBlockSize 时，<see cref="Extract"/> 会直接返回全 NaN 结果，由热力图渲染层兜底。
    /// </summary>
    private static (float[] luma, int w, int h) ReadLuminance(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        int w = size.Width;
        int h = size.Height;

        var target = new RenderTargetBitmap(new PixelSize(w, h));
        try
        {
            using (var ctx = target.CreateDrawingContext())
            {
                ctx.DrawImage(bitmap, new Rect(0, 0, w, h));
            }

            int rowBytes = w * 4;
            int totalBytes = rowBytes * h;
            var rented = ArrayPool<byte>.Shared.Rent(totalBytes);
            try
            {
                unsafe
                {
                    fixed (byte* ptr = rented)
                    {
                        target.CopyPixels(
                            new PixelRect(0, 0, w, h),
                            (IntPtr)ptr,
                            totalBytes,
                            rowBytes);
                    }
                }

                var luma = new float[w * h];
                Parallel.For(0, h, y =>
                {
                    int row = y * rowBytes;
                    int dst = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 4;
                        byte b = rented[i + 0];
                        byte g = rented[i + 1];
                        byte r = rented[i + 2];
                        luma[dst + x] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    }
                });
                return (luma, w, h);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        finally
        {
            target.Dispose();
        }
    }
}
