using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Android.Graphics;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Storage;
using AndroidColorSpace = Android.Graphics.ColorSpace;

namespace PhotoViewer.Android.Core;

/// <summary>
/// RAW（Sony ARW）解码：<c>libpvraw.so</c> = LibRaw 0.22 + 薄 C ABI（<c>NativeSrc/pvraw</c>）。
/// 产物固定为**未旋转**的 RGBA8（A=255），sRGB 或 Display P3（原色 + sRGB 传输曲线），
/// 方向写进 <see cref="DecodedImage.Orientation"/> 由渲染层应用；<c>SourceWidth/Height</c> 是全分辨率裁剪尺寸。
/// 曝光与色彩口径由原生层统一（相机白平衡、libraw 自动白点 + 限幅、无 ad-hoc 曲线），
/// 因此同一文件在任何线程/设备上渲染一致。
/// </summary>
public static partial class AndroidRawDecoder
{
    private const string LibraryName = "pvraw";

    private const int LibRawSuccess = 0;
    private const int LibRawCancelledByCallback = -100010;

    /// <summary>内嵌预览的体积上限：Sony 的 1616x1080 PreviewImage 在范围内，全尺寸 JpgFromRaw 被排除。</summary>
    private const long MaxEmbeddedPreviewPixels = 2_000_000;

    /// <summary>后台 RAW 开发串行闸：同一时刻至多一条非 P0 解码。</summary>
    private static readonly SemaphoreSlim BackgroundGate = new(1, 1);

    /// <summary>RAW 开发总并发闸：P0 与后台合计不超过 2（P0 直接占空位，不与后台排队）。</summary>
    private static readonly SemaphoreSlim DevelopGate = new(2, 2);

    /// <summary>LibRaw output_color：sRGB。</summary>
    private const int OutputColorSrgb = 1;

    /// <summary>LibRaw output_color：DCI-P3 D65（= Display P3 原色 + D65，配 sRGB 曲线）。</summary>
    private const int OutputColorDisplayP3 = 7;

    #region 原生接口

    [StructLayout(LayoutKind.Sequential)]
    private struct PvRawInfo
    {
        public int Width;
        public int Height;
        public int Flip;
        public int RawWidth;
        public int RawHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PvRawThumbInfo
    {
        public int Width;
        public int Height;
        public int Format;
        public int Bytes;
    }

    [LibraryImport(LibraryName)]
    private static unsafe partial IntPtr pv_raw_open(byte* data, long len);

    [LibraryImport(LibraryName)]
    private static partial int pv_raw_get_info(IntPtr handle, out PvRawInfo info);

    [LibraryImport(LibraryName)]
    private static partial int pv_raw_process(IntPtr handle, int halfSize, int quality, out PvRawInfo info);

    [LibraryImport(LibraryName)]
    private static unsafe partial int pv_raw_copy_rgba8(IntPtr handle, byte* dst, int stride);

    [LibraryImport(LibraryName)]
    private static partial void pv_raw_set_output_color(IntPtr handle, int outputColor);

    [LibraryImport(LibraryName)]
    private static partial void pv_raw_cancel(IntPtr handle);

    [LibraryImport(LibraryName)]
    private static partial void pv_raw_close(IntPtr handle);

    [LibraryImport(LibraryName)]
    private static partial IntPtr pv_raw_last_error(IntPtr handle);

    [LibraryImport(LibraryName)]
    private static partial int pv_raw_thumb_count(IntPtr handle);

    [LibraryImport(LibraryName)]
    private static partial int pv_raw_thumb_get_info(IntPtr handle, int index, out PvRawThumbInfo info);

    [LibraryImport(LibraryName)]
    private static partial int pv_raw_thumb_unpack(IntPtr handle, int index);

    [LibraryImport(LibraryName)]
    private static unsafe partial byte* pv_raw_thumb_data(IntPtr handle, out int length);

    #endregion

    /// <summary>
    /// 全分辨率或代理解码 RAW。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。半尺寸输出长边仍 ≥ 上限时走 LibRaw half_size。</param>
    /// <param name="target">目标色彩空间；Display P3 由 LibRaw 直接输出 P3 原色，其余按 sRGB。</param>
    /// <param name="ct">取消令牌；取消时中止 LibRaw 并抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>未旋转的 RGBA8 产物；不支持的 RAW 或失败返回 null。</returns>
    public static DecodedImage? Decode(IFileRef file, int? maxLongEdge, ImageColorSpace target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var space = target == ImageColorSpace.DisplayP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;

        // RAW 开发峰值 0.4–0.5 GB：后台（非 P0）串行 + 总并发 ≤2，P0 不与后台排队。
        bool background = DecodeContext.Priority != DecodePriority.P0_Current;
        if (background) BackgroundGate.Wait(ct);
        try
        {
            DevelopGate.Wait(ct);
            try
            {
                return DecodeCore(file, maxLongEdge, space, background, ct);
            }
            finally { DevelopGate.Release(); }
        }
        finally
        {
            if (background) BackgroundGate.Release();
        }
    }

    private static DecodedImage? DecodeCore(IFileRef file, int? maxLongEdge, ImageColorSpace space, bool background, CancellationToken ct)
    {
        byte[]? fileBytes = null;
        try
        {
            fileBytes = ReadFile(file, ct, out int length);
            if (fileBytes == null) return null;

            unsafe
            {
                fixed (byte* pinned = fileBytes)
                {
                    IntPtr handle = pv_raw_open(pinned, length);
                    if (handle == IntPtr.Zero)
                    {
                        Console.WriteLine($"[Native] RAW 打开失败（{file.Name}）：不是可解码的 RAW 或文件损坏");
                        return null;
                    }
                    try
                    {
                        using var registration = ct.Register(static state =>
                        {
                            if (state is IntPtr native) pv_raw_cancel(native);
                        }, handle);
                        return DecodeWithHandle(handle, maxLongEdge, space, background, ct);
                    }
                    finally
                    {
                        pv_raw_close(handle);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] RAW 解码失败（{file.Name}）：{exception.GetType().Name}: {exception.Message}");
            return null;
        }
        finally
        {
            if (fileBytes != null) ImageBufferPool.Return(fileBytes);
        }
    }

    /// <summary>
    /// 解内嵌预览（最大且 ≤2MP 的内嵌 JPEG，Sony 为 1616x1080）。只做容器打开与缩略图解包，不解 RAW。
    /// 供 STATE-01 低分辨率顶替使用。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="target">目标色彩空间。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>未旋转的 RGBA8 预览；没有合适的内嵌 JPEG 时返回 null。</returns>
    public static DecodedImage? DecodeEmbeddedPreview(IFileRef file, ImageColorSpace target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var space = target == ImageColorSpace.DisplayP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;

        byte[]? fileBytes = null;
        try
        {
            fileBytes = ReadFile(file, ct, out int length);
            if (fileBytes == null) return null;

            unsafe
            {
                fixed (byte* pinned = fileBytes)
                {
                    IntPtr handle = pv_raw_open(pinned, length);
                    if (handle == IntPtr.Zero) return null;
                    try
                    {
                        using var registration = ct.Register(static state =>
                        {
                            if (state is IntPtr native) pv_raw_cancel(native);
                        }, handle);
                        if (pv_raw_get_info(handle, out var info) != LibRawSuccess) return null;

                        int index = PickPreviewThumb(handle);
                        if (index < 0) return null;
                        ct.ThrowIfCancellationRequested();
                        if (pv_raw_thumb_unpack(handle, index) != LibRawSuccess) return null;

                        byte* data = pv_raw_thumb_data(handle, out int bytes);
                        if (data == null || bytes < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
                        var jpeg = new ReadOnlySpan<byte>(data, bytes).ToArray();

                        var options = new BitmapFactory.Options { InPreferredConfig = Bitmap.Config.Argb8888 };
                        if (OperatingSystem.IsAndroidVersionAtLeast(26))
                        {
                            var named = space == ImageColorSpace.DisplayP3
                                ? AndroidColorSpace.Named.DisplayP3
                                : AndroidColorSpace.Named.Srgb;
                            options.InPreferredColorSpace = AndroidColorSpace.Get(named!)!;
                        }
                        using var bitmap = BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length, options);
                        if (bitmap == null) return null;
                        ct.ThrowIfCancellationRequested();
                        return AndroidBitmapPixels.ToRgba8(bitmap, space, info.Width, info.Height, MapFlip(info.Flip));
                    }
                    finally
                    {
                        pv_raw_close(handle);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] RAW 内嵌预览失败（{file.Name}）：{exception.GetType().Name}: {exception.Message}");
            return null;
        }
        finally
        {
            if (fileBytes != null) ImageBufferPool.Return(fileBytes);
        }
    }

    /// <summary>
    /// 解码峰值内存估算（含调用方自己的 RGBA8 目标缓冲，不含源文件字节与进程基线）。
    /// 按 A7C II（33MP）/A6700（26MP）实测 RSS 拟合：2.2 字节/原始像素（raw 缓冲）
    /// + 11 字节/输出像素（ushort×4 工作图 + 4 字节 RGBA 目标），实测偏保守 10–30%（默认 AHD）。
    /// 换 DHT（quality=11）另加约 10 字节/输出像素。
    /// </summary>
    /// <param name="width">未旋转的全分辨率宽。</param>
    /// <param name="height">未旋转的全分辨率高。</param>
    /// <param name="halfSize">是否走 LibRaw half_size。</param>
    /// <returns>估算峰值字节数。</returns>
    public static long EstimatePeakBytes(int width, int height, bool halfSize)
    {
        if (width <= 0 || height <= 0) return 0;
        long outWidth = halfSize ? (width + 1) / 2 : width;
        long outHeight = halfSize ? (height + 1) / 2 : height;
        long rawBytes = (long)width * height * 22 / 10;
        long outBytes = outWidth * outHeight * 11;
        return (8L << 20) + rawBytes + outBytes;
    }

    private static DecodedImage? DecodeWithHandle(IntPtr handle, int? maxLongEdge, ImageColorSpace space, bool background, CancellationToken ct)
    {
        if (pv_raw_get_info(handle, out var info) != LibRawSuccess) return null;

        int fullWidth = info.Width, fullHeight = info.Height;
        if (fullWidth <= 0 || fullHeight <= 0) return null;
        int orientation = MapFlip(info.Flip);

        // half_size 输出长边仍 ≥ 目标长边时用它：省掉去马赛克，代理目标精度仍 ≥ 2×。
        bool half = maxLongEdge is { } limit &&
                    Math.Max((fullWidth + 1) / 2, (fullHeight + 1) / 2) >= limit;

        // 后台解码为工作内存另申请额度（BitmapLoader 只按产物尺寸预留）。
        // 被拒即跳过：代理返回 null 时 BitmapLoader 不会记错误（P0 才会回退 Full），预取按"本轮没拿到"处理。
        MemoryGovernor.AdmissionPermit? permit = null;
        if (background)
        {
            long outWidth = half ? (fullWidth + 1) / 2 : fullWidth;
            long outHeight = half ? (fullHeight + 1) / 2 : fullHeight;
            long scratch = Math.Max(0, EstimatePeakBytes(fullWidth, fullHeight, half) - outWidth * outHeight * 4);
            if (!MemoryGovernor.TryAcquire(scratch, DecodeContext.Priority, out permit))
            {
                Console.WriteLine($"[Native] RAW 后台解码跳过（内存准入被拒 {scratch >> 20}MB，"
                    + $"level={MemoryGovernor.CurrentLevel}）");
                return null;
            }
        }

        try
        {
            pv_raw_set_output_color(handle, space == ImageColorSpace.DisplayP3 ? OutputColorDisplayP3 : OutputColorSrgb);

            int rc = pv_raw_process(handle, half ? 1 : 0, -1, out var processed);
            if (rc != LibRawSuccess)
            {
                if (rc == LibRawCancelledByCallback) throw new OperationCanceledException(ct);
                Console.WriteLine($"[Native] RAW 处理失败（rc={rc}）：{Marshal.PtrToStringUTF8(pv_raw_last_error(handle))}");
                return null;
            }
            ct.ThrowIfCancellationRequested();

            int width = processed.Width, height = processed.Height;
            if (width <= 0 || height <= 0) return null;

            var buffer = DecodedImage.RentPixels(checked(width * height * 4));
            int copyRc;
            unsafe
            {
                fixed (byte* dst = buffer) { copyRc = pv_raw_copy_rgba8(handle, dst, width * 4); }
            }
            if (copyRc != LibRawSuccess)
            {
                ImageBufferPool.Return(buffer);
                Console.WriteLine($"[Native] RAW 像素拷贝失败（rc={copyRc}）：{Marshal.PtrToStringUTF8(pv_raw_last_error(handle))}");
                return null;
            }

            DecodedImage image;
            try
            {
                image = DecodedImage.CreatePooled(buffer, width, height, width * 4, PixelEncoding.Rgba8,
                    space, fullWidth, fullHeight, orientation);
            }
            catch
            {
                ImageBufferPool.Return(buffer);
                throw;
            }

            if (maxLongEdge is { } target && Math.Max(width, height) > target)
                return Downscale(image, target, space, fullWidth, fullHeight, orientation);
            return image;
        }
        finally
        {
            permit?.Dispose();
        }
    }

    /// <summary>
    /// Mitchell 精缩到代理长边（直接对 RGBA8 缩放，省掉 Bgra32 往返的整图拷贝）。
    /// 成功时释放源图；无需缩放或失败时原样返回源图。
    /// </summary>
    private static DecodedImage? Downscale(DecodedImage source, int maxLongEdge, ImageColorSpace space,
        int sourceWidth, int sourceHeight, int orientation)
    {
        var scaled = AndroidBitmapPixels.ScaleToLongEdge(source, maxLongEdge, space, sourceWidth, sourceHeight, orientation);
        if (scaled == null) return source;
        source.Release();
        return scaled;
    }

    /// <summary>挑最大的、面积 ≤2MP 的内嵌缩略图（跳过全尺寸 JpgFromRaw）。</summary>
    private static int PickPreviewThumb(IntPtr handle)
    {
        int count = pv_raw_thumb_count(handle);
        int best = -1;
        long bestArea = 0;
        for (int i = 0; i < count; i++)
        {
            if (pv_raw_thumb_get_info(handle, i, out var thumb) != LibRawSuccess) continue;
            if (thumb.Width <= 0 || thumb.Height <= 0 || thumb.Bytes <= 0) continue;
            long area = (long)thumb.Width * thumb.Height;
            if (area > MaxEmbeddedPreviewPixels) continue;
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
            }
        }
        return best;
    }

    /// <summary>LibRaw flip（dcraw 编码）→ EXIF 方向码。</summary>
    /// <param name="flip">0..7；原生层已把 90/180/270 归一化。</param>
    /// <returns>EXIF Orientation 1..8。</returns>
    private static int MapFlip(int flip) => flip switch
    {
        0 => 1,
        1 => 2,
        2 => 4,
        3 => 3,
        4 => 5,
        5 => 8,
        6 => 6,
        7 => 7,
        _ => 1,
    };

    /// <summary>把整个文件读进池化缓冲（LibRaw 需要随机访问；句柄关闭前不得释放）。</summary>
    private static byte[]? ReadFile(IFileRef file, CancellationToken ct, out int length)
    {
        length = 0;
        using var stream = file.OpenReadAsync().GetAwaiter().GetResult();
        long size = stream.CanSeek ? stream.Length : -1;
        if (size <= 0)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            size = copy.Length;
            if (size <= 0 || size > int.MaxValue) return null;
            var rented = DecodedImage.RentPixels((int)size);
            copy.Position = 0;
            copy.ReadExactly(rented, 0, (int)size);
            length = (int)size;
            return rented;
        }
        if (size > int.MaxValue) return null;

        var buffer = DecodedImage.RentPixels((int)size);
        int read = 0;
        try
        {
            while (read < size)
            {
                int n = stream.Read(buffer, read, (int)(size - read));
                if (n <= 0) break;
                read += n;
                if ((read & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
        if (read != size)
        {
            ImageBufferPool.Return(buffer);
            return null;
        }
        length = read;
        return buffer;
    }
}
