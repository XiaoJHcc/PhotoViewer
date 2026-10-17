using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.Media;
using PhotoViewer.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using AndroidColorSpace = Android.Graphics.ColorSpace;
using AndroidUri = Android.Net.Uri;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 原生主图解码器（A2）：<see cref="BitmapFactory"/> 负责 JPEG/PNG/系统支持的 HEIF，
/// 系统解不了的用户 HEIF（YUV 4:2:2 等）回退打包的 libheif 软件解码。
/// 代理用 <c>InSampleSize</c> + 精确缩放；EXIF 方向不旋转像素，只写入 <see cref="DecodedImage.Orientation"/> 交顶点阶段处理。
/// 色彩：表面以 Display P3 创建时解码到 P3，否则 sRGB8；不做隐式降级。
/// </summary>
public sealed class AndroidPresentationDecoder : IPresentationImageDecoder
{
    /// <summary>在线程池解码；代理限制长边，不使用容器内的小缩略图。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="encoding">本次必须产出的编码（Android 只支持 Rgba8）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>携带源尺寸、色彩与待应用方向的解码产物。</returns>
    public Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
    {
        if (encoding != PixelEncoding.Rgba8)
            throw new NotSupportedException($"Android 展示解码器不支持编码 {encoding}");
        return Task.Run(() => Decode(file, maxLongEdge, cancellationToken), cancellationToken);
    }

    /// <summary>无内嵌预览的用户图片按短边缩解码；返回已旋转的 sRGB8 小图，不回退原图全尺寸。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="minShortEdge">目标短边（至少）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已转正方向的 sRGB8 小图。</returns>
    public Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken)
        => Task.Run<DecodedImage?>(() =>
        {
            using var decoded = Decode(file, Math.Max(minShortEdge, 1) * 3, cancellationToken);
            if (decoded == null) return null;
            return ColorEngine.ToAnalysisSrgb8(decoded);
        }, cancellationToken);

    /// <summary>同步解码主体（在线程池执行）。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码产物；失败返回 null。</returns>
    private static DecodedImage? Decode(IFileRef file, int? maxLongEdge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int sourceWidth = 0, sourceHeight = 0;
        try
        {
            using var bitmap = DecodeBitmap(file, maxLongEdge, out sourceWidth, out sourceHeight, cancellationToken);
            if (bitmap == null)
                return DecodeHeifFallback(file, maxLongEdge, cancellationToken);
            int orientation = ReadOrientation(file);
            var colorSpace = AndroidDisplayCapabilities.PresentationColorSpace;
            var target = maxLongEdge is { } limit ? ScaleToLimit(bitmap, limit) : null;
            try
            {
                return ToRgba8(target ?? bitmap, colorSpace, sourceWidth, sourceHeight, orientation);
            }
            finally { target?.Dispose(); }
        }
        catch (System.OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 展示解码失败（{file.Name}）：{exception.GetType().Name}: {exception.Message}");
            return DecodeHeifFallback(file, maxLongEdge, cancellationToken);
        }
    }

    /// <summary>系统 HEIF 解码失败时用打包的 libheif 软解，并转成目标色彩空间。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码产物；不是 HEIF 或软解也失败时为 null。</returns>
    private static DecodedImage? DecodeHeifFallback(IFileRef file, int? maxLongEdge, CancellationToken cancellationToken)
    {
        if (!HeifLoader.IsHeifFile(file)) return null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var decoded = HeifLoader.DecodeAsync(file, maxLongEdge).GetAwaiter().GetResult();
            if (decoded == null) return null;
            cancellationToken.ThrowIfCancellationRequested();
            int orientation = ReadOrientation(file);
            bool p3 = AndroidDisplayCapabilities.SurfaceIsP3;
            using var converted = p3 ? ColorEngine.ToDisplayP3(decoded) : ColorEngine.ToSrgb8(decoded);
            var buffer = DecodedImage.RentPixels(checked(converted.Width * converted.Height * 4));
            try
            {
                CopyToRgba8(converted, buffer);
                int width = converted.Width, height = converted.Height;
                return DecodedImage.CreatePooled(buffer, width, height, checked(width * 4), PixelEncoding.Rgba8,
                    p3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb,
                    Math.Max(width, decoded.SourceWidth), Math.Max(height, decoded.SourceHeight), orientation);
            }
            catch
            {
                ImageBufferPool.Return(buffer);
                throw;
            }
        }
        catch (System.OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] libheif 兜底解码失败（{file.Name}）：{exception.Message}");
            return null;
        }
    }

    /// <summary>用 BitmapFactory 解码（代理时按 2 的幂采样），并返回未旋转源尺寸。</summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限。</param>
    /// <param name="sourceWidth">未旋转源宽。</param>
    /// <param name="sourceHeight">未旋转源高。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码位图；系统解不了时为 null。</returns>
    private static Bitmap? DecodeBitmap(IFileRef file, int? maxLongEdge, out int sourceWidth, out int sourceHeight, CancellationToken cancellationToken)
    {
        sourceWidth = 0;
        sourceHeight = 0;
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        using (var stream = file.OpenReadAsync().GetAwaiter().GetResult())
            BitmapFactory.DecodeStream(stream, null, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;
        sourceWidth = bounds.OutWidth;
        sourceHeight = bounds.OutHeight;
        cancellationToken.ThrowIfCancellationRequested();

        int sample = 1;
        if (maxLongEdge is { } limit)
        {
            while (Math.Max(sourceWidth, sourceHeight) / (sample * 2) >= limit) sample *= 2;
        }
        var options = new BitmapFactory.Options
        {
            InPreferredConfig = Bitmap.Config.Argb8888,
            InSampleSize = sample,
        };
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
        {
            var named = AndroidDisplayCapabilities.SurfaceIsP3
                ? AndroidColorSpace.Named.DisplayP3
                : AndroidColorSpace.Named.Srgb;
            options.InPreferredColorSpace = AndroidColorSpace.Get(named);
        }
        using var read = file.OpenReadAsync().GetAwaiter().GetResult();
        var bitmap = BitmapFactory.DecodeStream(read, null, options);
        if (bitmap == null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }

    /// <summary>把位图缩到长边上限（采样后可能仍略大），保持比例。</summary>
    /// <param name="bitmap">源位图。</param>
    /// <param name="limit">长边上限。</param>
    /// <returns>缩放后的新位图；无需缩放时返回 null。</returns>
    private static Bitmap? ScaleToLimit(Bitmap bitmap, int limit)
    {
        int longEdge = Math.Max(bitmap.Width, bitmap.Height);
        if (longEdge <= limit) return null;
        double ratio = (double)limit / longEdge;
        int width = Math.Max(1, (int)Math.Round(bitmap.Width * ratio));
        int height = Math.Max(1, (int)Math.Round(bitmap.Height * ratio));
        return Bitmap.CreateScaledBitmap(bitmap, width, height, true);
    }

    /// <summary>读取 EXIF 方向；本地路径与 content URI 都支持，失败按 1（不旋转）。</summary>
    /// <param name="file">文件引用。</param>
    /// <returns>1–8 的方向码。</returns>
    private static int ReadOrientation(IFileRef file)
    {
        try
        {
            int orientation;
            string? local = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(local))
            {
                using var exif = new ExifInterface(local);
                orientation = exif.GetAttributeInt(ExifInterface.TagOrientation, 1);
            }
            else if (file.Path is { IsAbsoluteUri: true } path
                && string.Equals(path.Scheme, "content", StringComparison.OrdinalIgnoreCase))
            {
                var resolver = Application.Context.ContentResolver;
                if (resolver == null) return 1;
                using var descriptor = resolver.OpenFileDescriptor(AndroidUri.Parse(path.AbsoluteUri), "r");
                if (descriptor?.FileDescriptor == null) return 1;
                using var exif = new ExifInterface(descriptor.FileDescriptor);
                orientation = exif.GetAttributeInt(ExifInterface.TagOrientation, 1);
            }
            else return 1;
            return orientation is >= 2 and <= 8 ? orientation : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 读取 EXIF 方向失败（{file.Name}）：{exception.Message}");
            return 1;
        }
    }

    /// <summary>把位图按显示色彩空间拷成 RGBA8 池化缓冲（照片不透明，保留 A 通道原值）。</summary>
    /// <param name="bitmap">解码位图。</param>
    /// <param name="colorSpace">像素色彩标签。</param>
    /// <param name="sourceWidth">未旋转源宽。</param>
    /// <param name="sourceHeight">未旋转源高。</param>
    /// <param name="orientation">EXIF 方向。</param>
    /// <returns>RGBA8 产物。</returns>
    private static DecodedImage ToRgba8(Bitmap bitmap, ImageColorSpace colorSpace, int sourceWidth, int sourceHeight, int orientation)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var buffer = DecodedImage.RentPixels(checked(width * height * 4));
        try
        {
            const int rowsPerChunk = 64;
            var pixels = new int[width * Math.Min(height, rowsPerChunk)];
            int offset = 0;
            for (int y = 0; y < height; y += rowsPerChunk)
            {
                int rows = Math.Min(rowsPerChunk, height - y);
                bitmap.GetPixels(pixels, 0, width, 0, y, width, rows);
                int count = width * rows;
                for (int i = 0; i < count; i++)
                {
                    int color = pixels[i];
                    buffer[offset++] = (byte)(color >> 16);
                    buffer[offset++] = (byte)(color >> 8);
                    buffer[offset++] = (byte)color;
                    buffer[offset++] = (byte)(color >> 24);
                }
            }
            return DecodedImage.CreatePooled(buffer, width, height, checked(width * 4), PixelEncoding.Rgba8,
                colorSpace, Math.Max(sourceWidth, width), Math.Max(sourceHeight, height), orientation);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>把任意 8 位编码的已解码图像拷成 RGBA8（Bgra32 交换 R/B，Rgb24 补 alpha）。</summary>
    /// <param name="source">源图像。</param>
    /// <param name="target">目标缓冲（调用方持有，失败由调用方归还）。</param>
    private static void CopyToRgba8(DecodedImage source, byte[] target)
    {
        var pixels = source.Pixels;
        int offset = 0;
        switch (source.Encoding)
        {
            case PixelEncoding.Rgba8:
                Buffer.BlockCopy(pixels, 0, target, 0, checked(source.Width * source.Height * 4));
                break;
            case PixelEncoding.Bgra32:
                for (int y = 0; y < source.Height; y++)
                {
                    int row = y * source.Stride;
                    for (int x = 0; x < source.Width; x++)
                    {
                        int index = row + x * 4;
                        target[offset++] = pixels[index + 2];
                        target[offset++] = pixels[index + 1];
                        target[offset++] = pixels[index];
                        target[offset++] = pixels[index + 3];
                    }
                }
                break;
            case PixelEncoding.Rgb24:
                for (int y = 0; y < source.Height; y++)
                {
                    int row = y * source.Stride;
                    for (int x = 0; x < source.Width; x++)
                    {
                        int index = row + x * 3;
                        target[offset++] = pixels[index];
                        target[offset++] = pixels[index + 1];
                        target[offset++] = pixels[index + 2];
                        target[offset++] = 255;
                    }
                }
                break;
            default:
                throw new NotSupportedException($"不支持从 {source.Encoding} 拷到 RGBA8");
        }
    }
}
