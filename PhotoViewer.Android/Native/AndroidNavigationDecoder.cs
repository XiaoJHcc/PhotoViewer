using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using PhotoViewer.Core.Color;
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

    /// <summary>
    /// 先按目标长边在源编码上等距抽样（逐像素原样搬运，保留色彩标签、ICC、预乘 alpha 与待应用方向），
    /// 再由共享 <see cref="ColorEngine.ToAnalysisSrgb8(DecodedImage)"/> 统一规范到 sRGB8 并转正：
    /// Display P3 码值不会被当成 sRGB，也不需要为 150dp 缩略图整幅做色彩转换。
    /// </summary>
    /// <param name="image">展示产物（调用方必须持有租约）。</param>
    /// <param name="longEdgePx">目标长边。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>显示朝向的 sRGB 位图；编码不支持或缓冲不足时 null。</returns>
    private static Bitmap? CreateFromDisplay(DecodedImage image, int longEdgePx, CancellationToken cancellationToken)
    {
        if (image.Width <= 0 || image.Height <= 0) return null;
        var pixels = image.Pixels;
        int bytesPerPixel = DecodedImage.BytesPerPixel(image.Encoding);
        if (pixels.LongLength < (long)(image.Height - 1) * image.Stride + (long)image.Width * bytesPerPixel) return null;
        int displayLongEdge = Math.Max(image.Width, image.Height);
        int sample = Math.Max(1, (int)Math.Ceiling(displayLongEdge / (double)Math.Max(1, longEdgePx)));
        int width = Math.Max(1, image.Width / sample), height = Math.Max(1, image.Height / sample);
        int stride = checked(width * bytesPerPixel);
        var buffer = new byte[checked(stride * height)];
        for (int y = 0; y < height; y++)
        {
            if ((y & 0x3F) == 0) cancellationToken.ThrowIfCancellationRequested();
            long row = (long)y * sample * image.Stride;
            long output = (long)y * stride;
            for (int x = 0; x < width; x++)
            {
                Array.Copy(pixels, row + (long)x * sample * bytesPerPixel, buffer, output, bytesPerPixel);
                output += bytesPerPixel;
            }
        }

        var iccProfile = image.ColorSpace == ImageColorSpace.Icc ? image.IccProfile.ToArray() : null;
        using var sampled = new DecodedImage(buffer, width, height, stride, image.Encoding, image.ColorSpace,
            width, height, image.Orientation, iccProfile);
        using var analysis = ColorEngine.ToAnalysisSrgb8(sampled);
        return AndroidBitmapConverter.ConvertDetached(analysis);
    }
}
