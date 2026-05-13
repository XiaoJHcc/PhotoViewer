using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PhotoViewer.Core.AI;

/// <summary>
/// 诊断图 → 位图的小工具。输入归一化到 [0,1] 的一维平面,按调色板 (viridis/grayscale/raw RGB) 渲染为 <see cref="WriteableBitmap"/>。
/// 生成的是 1:1 原尺寸位图(16×16 或 32×32),由 XAML 端靠 <c>BitmapInterpolationMode=None</c> + Stretch 放大,保证像素边界清晰。
/// </summary>
public static class HeatmapBitmapBuilder
{
    /// <summary>
    /// 用 viridis 近似调色板渲染单通道热力图(输入 [0,1])。0 为深蓝,0.5 青绿,1 黄。
    /// NaN 输入渲染为中灰 (#606060),表示"无效读数"。
    /// </summary>
    /// <param name="plane">长度 width*height 的归一化平面。</param>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    /// <returns>Bgra8888 位图。</returns>
    public static WriteableBitmap BuildViridis(float[] plane, int width, int height)
    {
        return BuildInternal(plane, width, height, static t =>
        {
            if (float.IsNaN(t)) return ((byte)0x60, (byte)0x60, (byte)0x60);
            // 简化 viridis:4 段线性插值
            // 0.00 #440154 (68,1,84)
            // 0.25 #3B528B (59,82,139)
            // 0.50 #21918C (33,145,140)
            // 0.75 #5EC962 (94,201,98)
            // 1.00 #FDE725 (253,231,37)
            (byte r, byte g, byte b)[] stops =
            {
                (68, 1, 84), (59, 82, 139), (33, 145, 140), (94, 201, 98), (253, 231, 37),
            };
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            float f = t * 4f;
            int i = (int)MathF.Floor(f);
            if (i >= 4) i = 3;
            float a = f - i;
            var c0 = stops[i];
            var c1 = stops[i + 1];
            byte r = (byte)(c0.r + (c1.r - c0.r) * a);
            byte g = (byte)(c0.g + (c1.g - c0.g) * a);
            byte b = (byte)(c0.b + (c1.b - c0.b) * a);
            return (r, g, b);
        });
    }

    /// <summary>
    /// 用黑→白灰阶渲染单通道热力图(输入 [0,1])。
    /// </summary>
    public static WriteableBitmap BuildGrayscale(float[] plane, int width, int height)
    {
        return BuildInternal(plane, width, height, static t =>
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            byte v = (byte)(t * 255);
            return (v, v, v);
        });
    }

    /// <summary>
    /// 从 RGB 平面(长度 width*height*3,每通道 [0,1])渲染 RGB 位图。用于 PCA-RGB 预览。
    /// </summary>
    public static WriteableBitmap BuildRgb(float[] rgb, int width, int height)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException($"rgb 长度应为 {width * height * 3},实际 {rgb.Length}", nameof(rgb));

        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888);
        using var fb = bitmap.Lock();
        unsafe
        {
            byte* ptr = (byte*)fb.Address;
            int stride = fb.RowBytes;
            for (int y = 0; y < height; y++)
            {
                byte* line = ptr + y * stride;
                for (int x = 0; x < width; x++)
                {
                    int srcIdx = (y * width + x) * 3;
                    float r = Math.Clamp(rgb[srcIdx + 0], 0, 1);
                    float g = Math.Clamp(rgb[srcIdx + 1], 0, 1);
                    float b = Math.Clamp(rgb[srcIdx + 2], 0, 1);
                    byte* p = line + x * 4;
                    p[0] = (byte)(b * 255);
                    p[1] = (byte)(g * 255);
                    p[2] = (byte)(r * 255);
                    p[3] = 255;
                }
            }
        }
        return bitmap;
    }

    private static WriteableBitmap BuildInternal(float[] plane, int width, int height, Func<float, (byte r, byte g, byte b)> palette)
    {
        ArgumentNullException.ThrowIfNull(plane);
        if (plane.Length != width * height)
            throw new ArgumentException($"plane 长度应为 {width * height},实际 {plane.Length}", nameof(plane));

        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888);
        using var fb = bitmap.Lock();
        unsafe
        {
            byte* ptr = (byte*)fb.Address;
            int stride = fb.RowBytes;
            for (int y = 0; y < height; y++)
            {
                byte* line = ptr + y * stride;
                for (int x = 0; x < width; x++)
                {
                    var (r, g, b) = palette(plane[y * width + x]);
                    byte* p = line + x * 4;
                    p[0] = b;
                    p[1] = g;
                    p[2] = r;
                    p[3] = 255;
                }
            }
        }
        return bitmap;
    }
}
