using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 缩放指示器（导航图）缩略图来源：直接取当前展示产物（代理/Full/增强预览）的像素，
/// 按显示朝向抽样到目标长边；不再二次解码原始文件，也不回读 GPU 纹理。
/// </summary>
internal static class AndroidNavigationDecoder
{
    /// <summary>与屏幕物理像素的倍率：导航图显示长边 150dp，位图按 2× 生成以保证清晰度。</summary>
    private const int BitmapOverscan = 2;

    /// <summary>把当前展示产物转成显示朝向的导航图位图。</summary>
    /// <param name="image">当前展示产物（会话持有所有权；本方法先取得租约再转后台执行）。</param>
    /// <param name="density">屏幕密度（dp → px）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>位图（调用方负责释放）；产物不可用或不支持该编码时返回 null。</returns>
    internal static Task<Bitmap?> CreateFromDisplayAsync(DecodedImage image, double density, CancellationToken cancellationToken)
    {
        // 在调用线程先借一份租约，避免后台转换期间会话替换产物后缓冲被归还。
        if (!image.TryAcquire()) return Task.FromResult<Bitmap?>(null);
        int longEdge = Math.Max(1, (int)Math.Round(DesignTokens.NavLongEdge * BitmapOverscan * density));
        return Task.Run<Bitmap?>(() =>
        {
            try
            {
                return CreateFromDisplay(image, longEdge, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                Console.WriteLine($"[Native] 导航图转换失败：{exception.Message}");
                return null;
            }
            finally
            {
                image.Release();
            }
        });
    }

    /// <summary>按目标长边等距抽样并转正到显示朝向（调用方必须持有租约）。</summary>
    private static Bitmap? CreateFromDisplay(DecodedImage image, int longEdgePx, CancellationToken cancellationToken)
    {
        if (image.Width <= 0 || image.Height <= 0) return null;
        var pixels = image.Pixels;
        if (pixels.Length < image.Height * image.Stride) return null;
        int displayLongEdge = Math.Max(image.Width, image.Height);
        int sample = Math.Max(1, (int)Math.Ceiling(displayLongEdge / (double)Math.Max(1, longEdgePx)));
        int width = Math.Max(1, image.Width / sample), height = Math.Max(1, image.Height / sample);
        var colors = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            if ((y & 0x3F) == 0) cancellationToken.ThrowIfCancellationRequested();
            int row = y * sample * image.Stride;
            int output = y * width;
            for (int x = 0; x < width; x++)
            {
                int index = row + x * sample * DecodedImage.BytesPerPixel(image.Encoding);
                switch (image.Encoding)
                {
                    case PixelEncoding.Bgra32:
                        colors[output++] = (pixels[index + 3] << 24) | (pixels[index + 2] << 16)
                            | (pixels[index + 1] << 8) | pixels[index];
                        break;
                    case PixelEncoding.Rgba8:
                        colors[output++] = (pixels[index + 3] << 24) | (pixels[index] << 16)
                            | (pixels[index + 1] << 8) | pixels[index + 2];
                        break;
                    case PixelEncoding.Rgb24:
                        colors[output++] = unchecked((int)0xFF000000) | (pixels[index] << 16)
                            | (pixels[index + 1] << 8) | pixels[index + 2];
                        break;
                    default:
                        return null;
                }
            }
        }
        using var sampled = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
        sampled.SetPixels(colors, 0, width, 0, 0, width, height);
        return RotateForDisplay(sampled, image.Orientation);
    }

    /// <summary>按共享方向表（先镜像后顺时针旋转）把解码位图转成显示朝向。</summary>
    private static Bitmap RotateForDisplay(Bitmap source, int orientation)
    {
        var (rotation, mirror) = ImageOrientationMapping.ToDisplay(orientation);
        if (rotation == 0 && !mirror) return source.Copy(Bitmap.Config.Argb8888!, false) ?? source;
        int width = source.Width, height = source.Height;
        bool swap = rotation is 90 or 270;
        int targetWidth = swap ? height : width, targetHeight = swap ? width : height;
        var target = Bitmap.CreateBitmap(targetWidth, targetHeight, Bitmap.Config.Argb8888!);
        using var canvas = new Canvas(target);
        using var paint = new Paint(PaintFlags.FilterBitmap) { FilterBitmap = true };
        canvas.Save();
        // 先镜像（水平），再绕源图中心顺时针旋转，最后把结果平移到目标画布。
        var matrix = new Matrix();
        if (mirror) matrix.PreScale(-1, 1);
        matrix.PostRotate(rotation, 0, 0);
        float translateX = rotation switch
        {
            90 => targetWidth,
            180 => targetWidth,
            _ => 0,
        };
        float translateY = rotation switch
        {
            90 => 0,
            180 => targetHeight,
            270 => targetHeight,
            _ => 0,
        };
        if (mirror && rotation == 0) translateX += targetWidth;
        matrix.PostTranslate(translateX, translateY);
        canvas.DrawBitmap(source, matrix, paint);
        canvas.Restore();
        return target;
    }
}
