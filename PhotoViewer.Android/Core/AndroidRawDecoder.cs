using System;
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
/// 文件字节经 <see cref="AndroidRawReadLease"/> 按文件引用身份在活跃租约期间共享：正式开发与内嵌预览重叠时只读一次文件，
/// 最后一个租约释放即归还缓冲（没有常驻文件缓存，也不会按存储身份复用陈旧 revision）。
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
    /// <param name="file">文件引用；同一对象的并发调用共享一次文件读取。</param>
    /// <param name="maxLongEdge">代理长边上限；null 表示全分辨率。半尺寸输出长边仍 ≥ 上限时走 LibRaw half_size。</param>
    /// <param name="target">目标色彩空间；Display P3 由 LibRaw 直接输出 P3 原色，其余按 sRGB。</param>
    /// <param name="ct">取消令牌；取消时中止 LibRaw 并抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>未旋转的 RGBA8 产物；不支持的 RAW、内存准入被拒或失败返回 null。</returns>
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
        try
        {
            // 同一次加载的正式开发与内嵌预览共用一个文件租约：重叠时只读一次文件，
            // 最后一个租约释放即归还池化缓冲（无跨代际的常驻文件缓存）。
            using var read = AndroidRawReadHandle.Open(file, DecodeContext.Priority);
            var fileBytes = read.TryGetBytes(ct, out int length, out var readStatus);
            if (fileBytes == null)
            {
                // 文件字节准入被拒不是解码错误：代理返回 null 时 BitmapLoader 不会记错误（P0 才会回退 Full）。
                if (readStatus == AndroidRawReadStatus.Refused)
                {
                    Console.WriteLine($"[Native] RAW 文件读取跳过（{file.Name}）：{read.Detail}");
                }
                return null;
            }

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
    }

    /// <summary>
    /// 解内嵌预览（最大且 ≤2MP 的内嵌 JPEG，Sony 为 1616x1080）。只做容器打开与缩略图解包，不解 RAW。
    /// 供 STATE-01 低分辨率顶替使用；与同一文件引用上的正式开发共用一次文件读取。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <param name="target">目标色彩空间。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>未旋转的 RGBA8 预览；没有合适的内嵌 JPEG 时返回 null。</returns>
    public static DecodedImage? DecodeEmbeddedPreview(IFileRef file, ImageColorSpace target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var space = target == ImageColorSpace.DisplayP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;

        try
        {
            // 顶替预览是可选的：文件租约未就绪（准入被拒/读取失败）就按"没有预览"返回，不影响随后的正式解码。
            using var read = AndroidRawReadHandle.Open(file, DecodeContext.Priority);
            var fileBytes = read.TryGetBytes(ct, out int length, out _);
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
    }

    /// <summary>
    /// 解码峰值内存估算（含调用方自己的 RGBA8 目标缓冲，不含源文件字节与进程基线）。
    /// 按 A7C II（33MP）/A6700（26MP）实测 RSS 拟合：2.2 字节/原始像素（raw 缓冲）
    /// + 11 字节/输出像素（ushort×4 工作图 + 4 字节 RGBA 目标），实测偏保守 10–30%（默认 AHD）。
    /// 换 DHT（quality=11）另加约 10 字节/输出像素。
    /// 调用方可把本值减去已被别处记账的部分再申请准入：源文件字节由共享读取租约记账，
    /// 最终产物字节由 <c>BitmapLoader</c> 记账（口径见 <see cref="EstimateWorkBytes"/>）。
    /// 极端/伪造尺寸按 <see cref="long.MaxValue"/> 饱和，不溢出成负数。
    /// </summary>
    /// <param name="width">未旋转的全分辨率宽。</param>
    /// <param name="height">未旋转的全分辨率高。</param>
    /// <param name="halfSize">是否走 LibRaw half_size。</param>
    /// <returns>估算峰值字节数。</returns>
    public static long EstimatePeakBytes(int width, int height, bool halfSize)
    {
        if (width <= 0 || height <= 0) return 0;
        long outWidth = halfSize ? HalfSize(width) : width;
        long outHeight = halfSize ? HalfSize(height) : height;
        long rawBytes = SaturatingMultiply((long)width * height, 22) / 10;
        long outBytes = SaturatingMultiply(outWidth * outHeight, 11);
        return SaturatingAdd(SaturatingAdd(8L << 20, rawBytes), outBytes);
    }

    /// <summary>
    /// 开发工作内存申请额（准入用的那个数）：峰值估算减去调用方已记账的产物字节。
    /// 直接返回的 RGBA 缓冲就是调用方（<c>BitmapLoader</c> 按代理 Square 上界 / 源尺寸）已记账的产物，扣掉避免重复记账；
    /// 走了精缩时 half/全尺寸中间缓冲**不是**最终产物，必须留在申请额里，同时也就覆盖了精缩目标的临时占用。
    /// 尺寸非法或溢出时返回饱和值（≥0），不会退回负额度。
    /// </summary>
    /// <param name="fullWidth">未旋转的全分辨率宽。</param>
    /// <param name="fullHeight">未旋转的全分辨率高。</param>
    /// <param name="maxLongEdge">代理长边上限；null 为全分辨率。</param>
    /// <returns>工作内存字节数。</returns>
    internal static long EstimateWorkBytes(int fullWidth, int fullHeight, int? maxLongEdge)
    {
        if (fullWidth <= 0 || fullHeight <= 0) return 0;

        // half_size 输出长边仍 ≥ 目标长边时用它：省掉去马赛克，代理目标精度仍 ≥ 2×。
        bool half = maxLongEdge is { } limit && Math.Max(HalfSize(fullWidth), HalfSize(fullHeight)) >= limit;
        long outWidth = half ? HalfSize(fullWidth) : fullWidth;
        long outHeight = half ? HalfSize(fullHeight) : fullHeight;
        bool downscales = maxLongEdge is { } postLimit && Math.Max(outWidth, outHeight) > postLimit;
        long productBytes = downscales ? 0 : SaturatingMultiply(outWidth * outHeight, 4);
        return Math.Max(0, EstimatePeakBytes(fullWidth, fullHeight, half) - productBytes);
    }

    /// <summary>half_size 输出边长（向上取整；int 边界不溢出）。</summary>
    private static long HalfSize(int value) => ((long)value + 1) / 2;

    /// <summary>饱和乘法：极端尺寸下按 long 上限夹取，不返回负值。</summary>
    private static long SaturatingMultiply(long a, long b)
        => a == 0 || b == 0 ? 0 : a > long.MaxValue / b ? long.MaxValue : a * b;

    /// <summary>饱和加法：极端尺寸下按 long 上限夹取，不返回负值。</summary>
    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    private static DecodedImage? DecodeWithHandle(IntPtr handle, int? maxLongEdge, ImageColorSpace space, bool background, CancellationToken ct)
    {
        if (pv_raw_get_info(handle, out var info) != LibRawSuccess) return null;

        int fullWidth = info.Width, fullHeight = info.Height;
        if (fullWidth <= 0 || fullHeight <= 0) return null;
        int orientation = MapFlip(info.Flip);

        bool half = maxLongEdge is { } limit && Math.Max(HalfSize(fullWidth), HalfSize(fullHeight)) >= limit;

        // 开发工作内存另申请额度：源文件字节已由共享读取租约记账，这里只算 LibRaw 工作图与我们自己的缓冲
        // （口径与不重复记账规则见 EstimateWorkBytes）。P0 豁免准入但照常记账，后台被拒即跳过、不做任何回落。
        long scratch = EstimateWorkBytes(fullWidth, fullHeight, maxLongEdge);

        MemoryGovernor.AdmissionPermit? permit = null;
        if (!MemoryGovernor.TryAcquire(scratch, DecodeContext.Priority, out permit))
        {
            // 代理返回 null 时 BitmapLoader 不会记解码错误（P0 才会回退 Full），预取按"本轮没拿到"处理。
            Console.WriteLine($"[Native] RAW 解码跳过（工作内存准入被拒 {scratch >> 20}MB，"
                + $"level={MemoryGovernor.CurrentLevel}，后台={background}）");
            return null;
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
}
