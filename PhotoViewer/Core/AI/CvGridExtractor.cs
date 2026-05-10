using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace PhotoViewer.Core.AI;

/// <summary>
/// CV 网格一期提取器（§A3.8）：在传入的 bitmap 上计算 16×16 格 × 5 标量 × 3 层金字塔。
/// 纯托管、无 native 依赖；所有标量共享一次 per-cell 扫描以避免为 Laplacian/Sobel 响应图
/// 整图额外分配；金字塔 3 层顺序处理，同一时刻仅持有一份 luma 缓冲。
/// </summary>
public static class CvGridExtractor
{
    private const int Grid = CvGridResult.GridSize;

    /// <summary>短边最小像素数。低于该阈值无法保证 16 格各自有 3×3 卷积邻域，直接抛异常。</summary>
    private const int MinShortSide = Grid * 3;

    /// <summary>
    /// 从已解码 bitmap 提取 CV 网格。调用方负责把适合尺寸的 bitmap 交进来；
    /// 一期不做调度、不做缓存，由上层决定何时触发。
    /// </summary>
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
        var curLuma = luma;
        int curW = w, curH = h;

        for (int p = 0; p < CvGridResult.PyramidLevels; p++)
        {
            ct.ThrowIfCancellationRequested();
            ComputeLevel(curLuma, curW, curH, p, data);

            if (p < CvGridResult.PyramidLevels - 1)
            {
                (curLuma, curW, curH) = Downsample2x(curLuma, curW, curH);
            }
        }

        return new CvGridResult { Version = CvGridResult.CurrentVersion, Data = data };
    }

    /// <summary>对单个金字塔层并行算 16×16 = 256 个格子。</summary>
    private static void ComputeLevel(float[] luma, int w, int h, int pyramidLevel, float[] data)
    {
        int cellW = Math.Max(1, w / Grid);
        int cellH = Math.Max(1, h / Grid);

        Parallel.For(0, Grid * Grid, cellIdx =>
        {
            int gy = cellIdx / Grid;
            int gx = cellIdx % Grid;
            ComputeCell(luma, w, h, gx * cellW, gy * cellH, cellW, cellH, pyramidLevel, gy, gx, data);
        });
    }

    /// <summary>
    /// 单格扫描：一次循环同时累加 5 个标量所需的统计量。3×3 卷积跳过图像绝对边缘一圈。
    /// </summary>
    private static void ComputeCell(
        float[] luma, int w, int h,
        int x0, int y0, int cw, int ch,
        int pyramid, int gy, int gx, float[] data)
    {
        int xStart = Math.Max(1, x0);
        int yStart = Math.Max(1, y0);
        int xEnd = Math.Min(w - 1, x0 + cw);
        int yEnd = Math.Min(h - 1, y0 + ch);
        if (xEnd <= xStart || yEnd <= yStart)
        {
            WriteCell(data, pyramid, gy, gx, 0, 0, 0, 0, 0);
            return;
        }

        double lumaSum = 0, lumaSqSum = 0;
        double lapSum = 0, lapSqSum = 0;
        double magSum = 0;
        Span<double> hist = stackalloc double[8];
        long samples = 0;

        for (int y = yStart; y < yEnd; y++)
        {
            int row = y * w;
            int rowPrev = row - w;
            int rowNext = row + w;
            for (int x = xStart; x < xEnd; x++)
            {
                float c = luma[row + x];
                lumaSum += c;
                lumaSqSum += c * c;

                float l  = luma[row + x - 1];
                float r  = luma[row + x + 1];
                float u  = luma[rowPrev + x];
                float d  = luma[rowNext + x];
                float ul = luma[rowPrev + x - 1];
                float ur = luma[rowPrev + x + 1];
                float dl = luma[rowNext + x - 1];
                float dr = luma[rowNext + x + 1];

                float lap = l + r + u + d - 4f * c;
                lapSum += lap;
                lapSqSum += lap * lap;

                float sx = (ur + 2f * r + dr) - (ul + 2f * l + dl);
                float sy = (dl + 2f * d + dr) - (ul + 2f * u + ur);
                float mag = MathF.Sqrt(sx * sx + sy * sy);
                magSum += mag;

                if (mag > 1e-3f)
                {
                    // atan2 → [-π, π]；再把对向方向合并（无向边）到 [0, 1) 后分 8 bin。
                    double theta = Math.Atan2(sy, sx);
                    double norm = (theta + Math.PI) / Math.PI;
                    if (norm >= 1.0) norm -= 1.0;
                    int bin = (int)(norm * 8);
                    if (bin >= 8) bin = 7;
                    hist[bin] += mag;
                }

                samples++;
            }
        }

        double lumaMean = lumaSum / samples;
        double lumaVar = Math.Max(0, lumaSqSum / samples - lumaMean * lumaMean);
        double lapMean = lapSum / samples;
        double lapVar = Math.Max(0, lapSqSum / samples - lapMean * lapMean);
        double sobelMean = magSum / samples;

        double histSum = 0;
        for (int i = 0; i < 8; i++) histSum += hist[i];
        double entropy = 0;
        if (histSum > 0)
        {
            for (int i = 0; i < 8; i++)
            {
                double p = hist[i] / histSum;
                if (p > 0) entropy -= p * Math.Log2(p);
            }
        }

        WriteCell(data, pyramid, gy, gx,
            (float)lapVar,
            (float)sobelMean,
            (float)entropy,
            (float)lumaMean,
            (float)Math.Sqrt(lumaVar));
    }

    /// <summary>将 5 标量写入扁平数组。标量顺序必须与 <see cref="CvGridResult.ScalarNames"/> 对齐。</summary>
    private static void WriteCell(float[] data, int p, int gy, int gx,
        float lapVar, float sobelMean, float gradEntropy, float lumaMean, float lumaStd)
    {
        int stride = CvGridResult.LevelStride;
        int plane = CvGridResult.PlaneLength;
        int baseIdx = p * stride + gy * Grid + gx;
        data[baseIdx + 0 * plane] = lapVar;
        data[baseIdx + 1 * plane] = sobelMean;
        data[baseIdx + 2 * plane] = gradEntropy;
        data[baseIdx + 3 * plane] = lumaMean;
        data[baseIdx + 4 * plane] = lumaStd;
    }

    /// <summary>2×2 box 下采样，零插值（与 patch 下采样策略一致，避免引入低通偏差）。</summary>
    private static (float[] luma, int w, int h) Downsample2x(float[] src, int w, int h)
    {
        int dw = w / 2;
        int dh = h / 2;
        var dst = new float[dw * dh];
        Parallel.For(0, dh, dy =>
        {
            int dr = dy * dw;
            int sr0 = (dy * 2) * w;
            int sr1 = sr0 + w;
            for (int dx = 0; dx < dw; dx++)
            {
                int sx = dx * 2;
                dst[dr + dx] = 0.25f * (src[sr0 + sx] + src[sr0 + sx + 1] + src[sr1 + sx] + src[sr1 + sx + 1]);
            }
        });
        return (dst, dw, dh);
    }

    /// <summary>
    /// 把任意解码格式的 bitmap 归一到 BGRA8888，再按 Rec.709 转灰度。
    /// 套路同 <see cref="DinoFeatureExtractor"/> 的 BuildInputTensor，但不缩放尺寸。
    /// </summary>
    private static (float[] luma, int w, int h) ReadLuminance(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        int w = size.Width;
        int h = size.Height;
        if (w < MinShortSide || h < MinShortSide)
        {
            throw new InvalidOperationException(
                $"CvGridExtractor: bitmap {w}×{h} 过小，至少需 {MinShortSide}×{MinShortSide}");
        }

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
