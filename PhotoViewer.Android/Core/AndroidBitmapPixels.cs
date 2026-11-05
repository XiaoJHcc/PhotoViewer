using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Android.Graphics;
using Java.Interop;
using SkiaSharp;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using AndroidColorSpace = Android.Graphics.ColorSpace;
using JavaByteBuffer = Java.Nio.ByteBuffer;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android <see cref="Bitmap"/> 与 RGBA8 池化缓冲之间的小工具（RAW 解码器与预览解码共用）。
/// 像素只从**原始 ARGB_8888 存储**读取（JNI 锁像素 / <see cref="Bitmap.CopyPixelsToBuffer"/>，行距取位图真实值）：
/// <see cref="Bitmap.GetPixels"/> 返回的是 sRGB 色彩空间的打包 int，广色域位图在那里先被隐式压到 sRGB 再拆通道，
/// 拿到的字节再也无法如实标注 P3/Adobe，因此展示路径不得使用。
/// 位图自报的色域经 <see cref="ColorEngine"/> 显式规范到调用方捕获的展示目标；无法如实解释的格式与色域一律拒绝，不按请求标签硬标。
/// </summary>
internal static class AndroidBitmapPixels
{
    /// <summary>jnigraphics 的成功返回码（ANDROID_BITMAP_RESULT_SUCCESS）。</summary>
    private const int BitmapSuccess = 0;

    /// <summary>回退路径的分带回读粒度：整幅 direct ByteBuffer 之外只多一份小缓冲。</summary>
    private const int BandBytes = 8 << 20;

    /// <summary>JNI 锁像素可用（0）或已确认不可用（1，后续直接走回退，不再反复试探）。</summary>
    private static int _lockPixelsState;

    /// <summary>jnigraphics 的 lockPixels：成功时给出整幅像素的原生地址，解锁前不会移动。</summary>
    /// <param name="environment">当前线程的 JNIEnv。</param>
    /// <param name="bitmap">位图的 jobject。</param>
    /// <param name="pixels">像素首地址。</param>
    /// <returns>结果码。</returns>
    [DllImport("jnigraphics", EntryPoint = "AndroidBitmap_lockPixels")]
    private static extern int AndroidBitmapLockPixels(IntPtr environment, IntPtr bitmap, out IntPtr pixels);

    /// <summary>与 <see cref="AndroidBitmapLockPixels"/> 配对，解锁后原生地址不再可用。</summary>
    /// <param name="environment">当前线程的 JNIEnv。</param>
    /// <param name="bitmap">位图的 jobject。</param>
    /// <returns>结果码。</returns>
    [DllImport("jnigraphics", EntryPoint = "AndroidBitmap_unlockPixels")]
    private static extern int AndroidBitmapUnlockPixels(IntPtr environment, IntPtr bitmap);

    /// <summary>
    /// 把位图按原始 ARGB_8888 存储拷成 RGBA8 池化缓冲，并规范到调用方捕获的展示目标色域。
    /// 照片不透明（A 取位图原值）；位图未预乘时顺手预乘，保持 <see cref="PixelEncoding.Rgba8"/> 的预乘口径。
    /// </summary>
    /// <param name="bitmap">已解码位图，所有权不转移。</param>
    /// <param name="targetColorSpace">本次展示的目标色域（调用方须在没有竞态的位置捕获一次并贯穿选项与产物）。</param>
    /// <param name="sourceWidth">未旋转的全分辨率源宽。</param>
    /// <param name="sourceHeight">未旋转的全分辨率源高。</param>
    /// <param name="orientation">待渲染的 EXIF 方向。</param>
    /// <returns>RGBA8 产物；位图格式或色域无法如实解释时返回 null。</returns>
    internal static DecodedImage? ToRgba8(Bitmap bitmap, ImageColorSpace targetColorSpace,
        int sourceWidth, int sourceHeight, int orientation)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0) return null;

        Bitmap? converted = null;
        Bitmap? recolored = null;
        try
        {
            var source = bitmap;
            // 只接受能如实解释的存储：ARGB_8888 在内存里就是 R,G,B,A 各 8 位。其余 Config（565/4444/F16/1010102）
            // 先让系统在同色域内换成 ARGB_8888（只换位深/布局，不动色彩解释），换不了就如实放弃。
            if (source.GetConfig() != Bitmap.Config.Argb8888)
            {
                converted = source.Copy(Bitmap.Config.Argb8888!, false);
                if (converted == null)
                {
                    Console.WriteLine($"[Native] 位图 {source.GetConfig()?.ToString() ?? "未知配置"} 无法当 RGBA8 读取，已跳过");
                    return null;
                }
                source = converted;
            }

            var actual = ResolveColorSpace(source);
            if (actual == null)
            {
                // 位图自报色域无法用 ImageColorSpace/ICC 表达（DCI-P3、BT.2020、ProPhoto 等）：
                // 交系统色彩管理换成目标色域，绝不按请求标签硬标。
                recolored = ConvertToColorSpace(source, targetColorSpace);
                if (recolored == null) return null;
                source = recolored;
                actual = targetColorSpace;
            }

            var raw = ReadPixels(source, actual.Value, sourceWidth, sourceHeight, orientation);
            if (raw == null) return null;
            if (raw.ColorSpace == targetColorSpace) return raw;
            // 位图实际色域与展示目标不一致（如旧系统无色彩管理、平台按 sRGB 原样输出码值）：
            // 显式规范到捕获的目标，产物标签与字节一致。
            Console.WriteLine($"[Native] 位图色域 {raw.ColorSpace} → 展示目标 {targetColorSpace}（{raw.Width}x{raw.Height}）");
            using (raw)
                return ColorEngine.ToDisplayEncoding(raw, PixelEncoding.Rgba8, targetColorSpace);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 位图转 RGBA8 失败：{exception.GetType().Name}: {exception.Message}");
            return null;
        }
        finally
        {
            recolored?.Dispose();
            converted?.Dispose();
        }
    }

    /// <summary>
    /// 把已转换好的 8 位图像按紧凑行距重新装箱，并写入调用方给出的全分辨率源尺寸与待应用方向
    /// （<see cref="ColorEngine"/> 的转换产物只沿用源的元数据，无法覆盖这两项）。
    /// </summary>
    /// <param name="source">源图像（RGBA8），所有权不转移。</param>
    /// <param name="colorSpace">产物色彩标签。</param>
    /// <param name="sourceWidth">未旋转的全分辨率源宽。</param>
    /// <param name="sourceHeight">未旋转的全分辨率源高。</param>
    /// <param name="orientation">待渲染的 EXIF 方向。</param>
    /// <returns>重新装箱的 RGBA8 产物；编码不符时返回 null。</returns>
    internal static DecodedImage? CopyWithGeometry(DecodedImage source, ImageColorSpace colorSpace,
        int sourceWidth, int sourceHeight, int orientation)
    {
        if (source.Encoding != PixelEncoding.Rgba8) return null;
        int width = source.Width, height = source.Height;
        int stride = checked(width * 4);
        var buffer = DecodedImage.RentPixels(checked(stride * height));
        try
        {
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(source.Pixels, checked(y * source.Stride), buffer, y * stride, stride);
            return DecodedImage.CreatePooled(buffer, width, height, stride, PixelEncoding.Rgba8, colorSpace,
                Math.Max(sourceWidth, width), Math.Max(sourceHeight, height), orientation,
                colorSpace == ImageColorSpace.Icc ? source.IccProfile.ToArray() : null);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>RGBA8 → Bgra32（Skia 缩放通道只吃 Bgra32/Rgb24）。调用方负责释放返回对象。</summary>
    /// <param name="source">源图像，所有权不转移。</param>
    /// <returns>Bgra32 图像；编码不符或失败返回 null。</returns>
    internal static DecodedImage? ToBgra32(DecodedImage source)
    {
        if (source.Encoding != PixelEncoding.Rgba8) return null;
        int width = source.Width, height = source.Height;
        var buffer = DecodedImage.RentPixels(checked(width * height * 4));
        try
        {
            var src = source.Pixels;
            for (int y = 0; y < height; y++)
            {
                long srcRow = (long)y * source.Stride;
                long dstRow = (long)y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    long si = srcRow + x * 4;
                    long di = dstRow + x * 4;
                    buffer[di] = src[si + 2];     // B
                    buffer[di + 1] = src[si + 1]; // G
                    buffer[di + 2] = src[si];     // R
                    buffer[di + 3] = src[si + 3]; // A
                }
            }
            return DecodedImage.CreatePooled(buffer, width, height, checked(width * 4), PixelEncoding.Bgra32,
                source.ColorSpace, source.SourceWidth, source.SourceHeight, source.Orientation);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>把 Bgra32 像素拷进 RGBA8 目标缓冲（R/B 交换，保留行距）。</summary>
    /// <param name="source">源像素。</param>
    /// <param name="sourceStride">源行距（字节）。</param>
    /// <param name="target">目标缓冲，长度 ≥ width*height*4。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    internal static void CopyBgraToRgba(byte[] source, int sourceStride, byte[] target, int width, int height)
    {
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            long row = (long)y * sourceStride;
            for (int x = 0; x < width; x++)
            {
                long i = row + x * 4;
                target[offset++] = source[i + 2];
                target[offset++] = source[i + 1];
                target[offset++] = source[i];
                target[offset++] = source[i + 3];
            }
        }
    }

    /// <summary>
    /// RGBA8 直接按长边精缩（Mitchell 三次滤波，与 <see cref="SkiaImageCodec.ScaleToLongEdge"/> 同一采样口径）。
    /// 不走 Bgra32 往返：RAW 代理常见 25MP→4800px，四次整图拷贝的代价比缩放本身还大。
    /// </summary>
    /// <param name="source">源图像（RGBA8，不透明），所有权不转移。</param>
    /// <param name="targetLongEdge">目标长边。</param>
    /// <param name="colorSpace">产物色彩标签。</param>
    /// <param name="sourceWidth">全分辨率源宽（几何基准）。</param>
    /// <param name="sourceHeight">全分辨率源高。</param>
    /// <param name="orientation">待渲染方向。</param>
    /// <returns>缩放产物；无需缩放或失败返回 null。</returns>
    internal static DecodedImage? ScaleToLongEdge(DecodedImage source, int targetLongEdge, ImageColorSpace colorSpace,
        int sourceWidth, int sourceHeight, int orientation)
    {
        if (source.Encoding != PixelEncoding.Rgba8 || targetLongEdge <= 0) return null;
        int longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= targetLongEdge) return null;
        double scale = (double)targetLongEdge / longEdge;
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));

        byte[]? outBuffer = null;
        try
        {
            unsafe
            {
                fixed (byte* sourcePixels = source.Pixels)
                {
                    // 照片不透明：Opaque 让 Skia 走整数快速路径（Unpremul 会退化成逐像素 alpha 混合）。
                    var sourceInfo = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    using var sourceBitmap = new SKBitmap();
                    if (!sourceBitmap.InstallPixels(sourceInfo, (IntPtr)sourcePixels, source.Stride)) return null;
                    using var sourceImage = SKImage.FromBitmap(sourceBitmap);

                    var targetInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    using var target = new SKBitmap(targetInfo);
                    using (var canvas = new SKCanvas(target))
                    {
                        canvas.Clear(SKColors.Black);
                        canvas.DrawImage(sourceImage,
                            SKRect.Create(0, 0, source.Width, source.Height),
                            SKRect.Create(0, 0, width, height),
                            new SKSamplingOptions(SKCubicResampler.Mitchell), new SKPaint());
                    }

                    outBuffer = DecodedImage.RentPixels(checked(width * height * 4));
                    Marshal.Copy(target.GetPixels(), outBuffer, 0, width * height * 4);
                }
            }
            var result = DecodedImage.CreatePooled(outBuffer, width, height, width * 4, PixelEncoding.Rgba8,
                colorSpace, sourceWidth, sourceHeight, orientation);
            outBuffer = null;   // 所有权已转移
            return result;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] RAW 代理缩放失败：{exception.Message}");
            return null;
        }
        finally
        {
            if (outBuffer != null) ImageBufferPool.Return(outBuffer);
        }
    }

    /// <summary>位图自报的色域标签；无法用已知标签或 ICC 表达时返回 null（不猜色域）。</summary>
    /// <param name="bitmap">位图。</param>
    /// <returns>色彩标签；无法如实表达时为 null。</returns>
    private static ImageColorSpace? ResolveColorSpace(Bitmap bitmap)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return ImageColorSpace.Srgb;   // 旧系统无色彩管理，位图恒 sRGB
        var space = bitmap.ColorSpace;
        // 未标记色域：Android 文档规定按 sRGB 解释。
        if (space == null || space.IsSrgb) return ImageColorSpace.Srgb;
        if (MatchesNamed(space, AndroidColorSpace.Named.DisplayP3)) return ImageColorSpace.DisplayP3;
        if (MatchesNamed(space, AndroidColorSpace.Named.AdobeRgb)) return ImageColorSpace.AdobeRgb;
        return null;
    }

    /// <summary>位图色域是否就是某个系统命名色域（数值比较，ICC 解析出的色域同样适用）。</summary>
    /// <param name="space">位图色域。</param>
    /// <param name="named">命名色域。</param>
    /// <returns>数值等价时为 true。</returns>
    [SupportedOSPlatform("android26.0")]
    private static bool MatchesNamed(AndroidColorSpace space, AndroidColorSpace.Named? named)
    {
        var known = named == null ? null : AndroidColorSpace.Get(named);
        return known != null && known.Equals(space);
    }

    /// <summary>用系统画布把位图换到目标色域（由系统按真实 profile 做色彩管理，比自行近似更准）。</summary>
    /// <param name="bitmap">源位图。</param>
    /// <param name="targetColorSpace">目标色域。</param>
    /// <returns>目标色域的新位图；系统不支持或失败时为 null。</returns>
    private static Bitmap? ConvertToColorSpace(Bitmap bitmap, ImageColorSpace targetColorSpace)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return null;
        var named = targetColorSpace == ImageColorSpace.DisplayP3
            ? AndroidColorSpace.Named.DisplayP3
            : AndroidColorSpace.Named.Srgb;
        var space = named == null ? null : AndroidColorSpace.Get(named);
        if (space == null) return null;

        var destination = Bitmap.CreateBitmap(bitmap.Width, bitmap.Height, Bitmap.Config.Argb8888!, bitmap.HasAlpha, space);
        if (destination == null) return null;
        try
        {
            using var canvas = new Canvas(destination);
            canvas.DrawBitmap(bitmap, 0, 0, null);
            Console.WriteLine($"[Native] 位图色域 {bitmap.ColorSpace?.Name ?? "未标记"} 由系统规范到 {targetColorSpace}");
            return destination;
        }
        catch (Exception exception)
        {
            destination.Dispose();
            Console.WriteLine($"[Native] 位图色域规范失败：{exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    /// <summary>按给定色域读位图的原始 ARGB_8888 字节，元数据按调用方给的全分辨率源尺寸与方向填写。</summary>
    /// <param name="bitmap">ARGB_8888 位图。</param>
    /// <param name="colorSpace">像素色彩标签。</param>
    /// <param name="sourceWidth">未旋转的全分辨率源宽。</param>
    /// <param name="sourceHeight">未旋转的全分辨率源高。</param>
    /// <param name="orientation">待渲染的 EXIF 方向。</param>
    /// <returns>RGBA8 产物；读取失败返回 null。</returns>
    private static DecodedImage? ReadPixels(Bitmap bitmap, ImageColorSpace colorSpace,
        int sourceWidth, int sourceHeight, int orientation)
    {
        int width = bitmap.Width, height = bitmap.Height;
        int stride = checked(width * 4);
        var buffer = DecodedImage.RentPixels(checked(stride * height));
        try
        {
            if (!CopyRawPixels(bitmap, buffer, width, height))
            {
                ImageBufferPool.Return(buffer);
                return null;
            }
            return DecodedImage.CreatePooled(buffer, width, height, stride, PixelEncoding.Rgba8, colorSpace,
                Math.Max(sourceWidth, width), Math.Max(sourceHeight, height), orientation);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>把位图的原始像素写进紧凑 RGBA8 缓冲：先 JNI 锁像素（单次拷贝），不可用时退到 CopyPixelsToBuffer。</summary>
    /// <param name="bitmap">ARGB_8888 位图。</param>
    /// <param name="target">目标缓冲，长度 ≥ width*height*4。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <returns>是否成功。</returns>
    private static bool CopyRawPixels(Bitmap bitmap, byte[] target, int width, int height)
    {
        if (TryCopyLockedPixels(bitmap, target, width, height)) return true;
        return TryCopyPixelsToBuffer(bitmap, target, width, height);
    }

    /// <summary>
    /// JNI 锁像素直读：整幅只有一次拷贝，行距与预乘状态取位图真实值。
    /// 失败（硬件位图、jnigraphics 缺失、线程未附着）只记一次并停用该路径，不当作解码失败。
    /// </summary>
    /// <param name="bitmap">ARGB_8888 位图。</param>
    /// <param name="target">目标缓冲。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <returns>是否成功。</returns>
    private static unsafe bool TryCopyLockedPixels(Bitmap bitmap, byte[] target, int width, int height)
    {
        if (Volatile.Read(ref _lockPixelsState) != 0) return false;
        try
        {
            IntPtr environment = JniEnvironment.EnvironmentPointer;
            IntPtr handle = bitmap.Handle;
            if (environment == IntPtr.Zero || handle == IntPtr.Zero) return false;

            int rowBytes = bitmap.RowBytes;
            if (rowBytes < width * 4) return false;
            if (AndroidBitmapLockPixels(environment, handle, out var pixels) != BitmapSuccess || pixels == IntPtr.Zero)
                return false;
            try
            {
                CopyRows((byte*)pixels, rowBytes, target, width, height, bitmap.IsPremultiplied);
                return true;
            }
            finally
            {
                _ = AndroidBitmapUnlockPixels(environment, handle);
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _lockPixelsState, 1) == 0)
                Console.WriteLine($"[Native] JNI 锁像素不可用，位图读取改走 CopyPixelsToBuffer："
                    + $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// <see cref="Bitmap.CopyPixelsToBuffer"/> 回退：整幅写进 direct ByteBuffer 再分带回读
    /// （<c>ByteBuffer.Wrap(byte[])</c> 不行——托管数组在调用返回时就拷回了，之后写入的数据留在 Java 副本里）。
    /// 代价是多一份整图原生内存，但不做任何色彩猜测。
    /// </summary>
    /// <param name="bitmap">ARGB_8888 位图。</param>
    /// <param name="target">目标缓冲。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <returns>是否成功。</returns>
    private static bool TryCopyPixelsToBuffer(Bitmap bitmap, byte[] target, int width, int height)
    {
        int rowBytes = checked(width * 4);
        int stride = bitmap.RowBytes;
        long total = (long)stride * height;
        if (stride < rowBytes || total > int.MaxValue) return false;
        try
        {
            using var direct = JavaByteBuffer.AllocateDirect((int)total);
            if (direct == null) return false;
            bitmap.CopyPixelsToBuffer((Java.Nio.Buffer)direct);
            direct.Position(0);

            int bandRows = Math.Max(1, BandBytes / stride);
            var band = new byte[checked(bandRows * stride)];
            for (int y = 0; y < height; y += bandRows)
            {
                int rows = Math.Min(bandRows, height - y);
                direct.Get(band, 0, rows * stride);
                if (bitmap.IsPremultiplied)
                {
                    for (int row = 0; row < rows; row++)
                        Buffer.BlockCopy(band, row * stride, target, (y + row) * rowBytes, rowBytes);
                }
                else
                {
                    for (int row = 0; row < rows; row++)
                        PremultiplyRow(band, row * stride, target, (y + row) * rowBytes, width);
                }
            }
            return true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] CopyPixelsToBuffer 回退失败：{exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    /// <summary>行拷贝：行距可能大于 width×4（系统按对齐分配），源未预乘时顺手预乘。</summary>
    /// <param name="source">源像素首地址（ARGB_8888 = 内存中 R,G,B,A）。</param>
    /// <param name="stride">源行距（字节）。</param>
    /// <param name="target">目标紧凑 RGBA8 缓冲。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="premultiplied">源是否已预乘；未预乘时按 alpha 预乘后再写，保持 RGBA8 的预乘口径。</param>
    private static unsafe void CopyRows(byte* source, int stride, byte[] target, int width, int height, bool premultiplied)
    {
        long rowBytes = (long)width * 4;
        fixed (byte* destination = target)
        {
            if (premultiplied)
            {
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(source + (long)y * stride, destination + (long)y * rowBytes, rowBytes, rowBytes);
                return;
            }
            for (int y = 0; y < height; y++)
            {
                byte* sourceRow = source + (long)y * stride;
                byte* targetRow = destination + (long)y * rowBytes;
                for (int x = 0; x < width; x++, sourceRow += 4, targetRow += 4)
                {
                    byte alpha = sourceRow[3];
                    targetRow[0] = Premultiply(sourceRow[0], alpha);
                    targetRow[1] = Premultiply(sourceRow[1], alpha);
                    targetRow[2] = Premultiply(sourceRow[2], alpha);
                    targetRow[3] = alpha;
                }
            }
        }
    }

    /// <summary>托管缓冲上的同一预乘行变换（回退路径用）。</summary>
    /// <param name="source">源缓冲。</param>
    /// <param name="sourceOffset">源行起始偏移。</param>
    /// <param name="target">目标缓冲。</param>
    /// <param name="targetOffset">目标行起始偏移。</param>
    /// <param name="width">像素宽。</param>
    private static void PremultiplyRow(byte[] source, int sourceOffset, byte[] target, int targetOffset, int width)
    {
        for (int x = 0; x < width; x++)
        {
            int sourceIndex = sourceOffset + x * 4;
            int targetIndex = targetOffset + x * 4;
            byte alpha = source[sourceIndex + 3];
            target[targetIndex] = Premultiply(source[sourceIndex], alpha);
            target[targetIndex + 1] = Premultiply(source[sourceIndex + 1], alpha);
            target[targetIndex + 2] = Premultiply(source[sourceIndex + 2], alpha);
            target[targetIndex + 3] = alpha;
        }
    }

    /// <summary>单通道预乘（按 255 取整，alpha 不透明时原值返回）。</summary>
    /// <param name="value">颜色分量。</param>
    /// <param name="alpha">alpha。</param>
    /// <returns>预乘后的分量。</returns>
    private static byte Premultiply(byte value, byte alpha)
        => alpha == 255 ? value : (byte)((value * alpha + 127) / 255);
}
