using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image.Heif;

/// <summary>
/// HEIF 快速解码路径:容器自解析 + <c>pvhevc</c> 原生库(静态 FFmpeg,仅 HEVC 解码器)并行解瓦片,
/// 再由 <see cref="YuvImageConverter"/> 一次融合完成色度上采样/矩阵/缩放/线性化。
/// </summary>
/// <remarks>
/// 与 libheif 回落的分工:凡是容器结构、位深、色度或传输特性超出支持面,本类返回 null 并给出原因,
/// 由调用方走 libheif。取消不是失败,按 <see cref="OperationCanceledException"/> 抛出。
///
/// 并发纪律:
/// <list type="bullet">
/// <item>瓦片:每块瓦片独占一个 pvhevc 上下文(上下文可复用,池中空闲上限 <see cref="FastHeifOptions.MaxIdleContexts"/> 个,多余关闭);
/// 整幅图需要的上下文数在开跑前**一次性预留**,避免多幅图各自持有一部分许可互相等待;</item>
/// <item>同时在用的上下文总数上限 = 处理器数(全局),**当前照片(P0)另算**:
/// 它可以直接越过仍在排队的后台请求、并用超出常规上限的上下文,否则连翻时整幅解码要么排在预取后面、
/// 要么等后台释放上下文(实测这两段等掉 130–280 ms,正是切图 p95 长尾);</item>
/// <item>同时在转换的图像数上限 = max(2, 处理器数 / 4):一张 33MP 4:2:2 文件的原生平面约 135 MB,
/// 多幅图并行会把内存推到预算之外。</item>
/// </list>
///
/// 排队通道由 <see cref="DecodeContext"/> 的票面决定(见 <see cref="LaneOf"/>):
/// P0 当前图 &gt; 沿 P0 并行的前两张预取 &gt; 普通后台;普通后台在 P0 在解时让位。
/// 两个名额按"先图像、后上下文,上下文全有或全无"的顺序取,所以不存在拿着上下文等图像的反向等待,
/// 不会有死锁;取消在任一步都会把已拿到的名额归还。
///
/// 原生库位置由平台注入:见 <see cref="Configure"/>;未配置时按 <c>DllImport("pvhevc")</c> 的默认探测加载。
/// </remarks>
public static partial class FastHeifDecoder
{
    private const string LibraryName = "pvhevc";

    /// <summary>当前生效的并发与排队参数(由 <see cref="Configure"/> 一次性写入,默认值等同 Windows 头历史口径)。</summary>
    private static volatile Settings s_settings = CreateSettings(null);

    /// <summary>配置参数快照。</summary>
    private sealed record Settings(
        int MaxIdleContexts,
        int MaxConcurrentContexts,
        int MaxCurrentContexts,
        int MaxConcurrentImages,
        int BackgroundTilesDuringCurrent,
        bool HoldBackgroundDuringCurrent);

    /// <summary>应用一次配置(可空 = 默认值),并叠加压测对照用的环境变量。</summary>
    private static Settings CreateSettings(FastHeifOptions? options)
    {
        var concurrentContexts = Math.Max(1, options?.MaxConcurrentContexts ?? Math.Max(1, Environment.ProcessorCount));
        var extraContexts = Math.Max(0, options?.MaxCurrentContextsExtra ?? Math.Clamp(concurrentContexts / 2, 2, 8));
        return new Settings(
            Math.Max(0, options?.MaxIdleContexts ?? 6),
            concurrentContexts,
            concurrentContexts + extraContexts,
            Math.Max(1, options?.MaxConcurrentImages ?? Math.Max(2, Environment.ProcessorCount / 4)),
            EnvInt("PV_HEIF_BACKGROUND_TILES", options?.BackgroundTilesDuringCurrent ?? 2),
            EnvFlag("PV_HEIF_HOLD_BACKGROUND", options?.HoldBackgroundDuringCurrent ?? true));
    }

    /// <summary>等名额时的轮询粒度:通道由票面决定,票面可能被别的请求提升,所以不能只靠信号唤醒。</summary>
    private const int AdmissionPollMs = 15;

    private static readonly object s_admissionGate = new();
    private static readonly object s_poolGate = new();
    private static readonly Stack<IntPtr> s_idleContexts = new();
    private static readonly object s_initializationGate = new();

    // 名额账:图像与上下文按通道各记一份(P0 通道不参与常规上限)
    private static int s_currentImages;
    private static int s_backgroundImages;
    private static int s_currentContexts;
    private static int s_backgroundContexts;
    private static int s_waitingCurrent;
    private static int s_waitingAlongside;

    private static int s_lastFrameBytes;
    private static int s_configured;
    private static volatile bool s_initialized;
    private static volatile bool s_available;

    private static readonly IdleContextMemoryClient s_memoryClient = new();

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    /// <summary><c>pv_hevc.h</c> 的帧描述(x64 下 72 字节,平面指针自偏移 16 起)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFrame
    {
        public int Width;
        public int Height;
        public int BitDepth;
        public int ChromaFormat;
        public IntPtr Y;
        public IntPtr Cb;
        public IntPtr Cr;
        public int StrideY;
        public int StrideCb;
        public int StrideCr;
        public int FullRange;
        public int Matrix;
        public int Primaries;
        public int Transfer;
    }

    [LibraryImport(LibraryName)]
    private static partial int pv_hevc_version();

    [LibraryImport(LibraryName)]
    private static partial IntPtr pv_hevc_open(int threads);

    [LibraryImport(LibraryName)]
    private static unsafe partial int pv_hevc_decode(IntPtr context, byte* annexB, int length, NativeFrame* frame);

    [LibraryImport(LibraryName)]
    private static partial void pv_hevc_close(IntPtr context);

    [LibraryImport(LibraryName)]
    private static partial IntPtr pv_hevc_last_error(IntPtr context);

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------

    /// <summary>
    /// 读取整个文件的字节:HEIF 容器解析与瓦片码流都从同一份缓冲切出,只读一次盘。
    /// </summary>
    /// <param name="file">文件引用。</param>
    /// <returns>文件字节。</returns>
    public static async Task<byte[]> ReadAllBytesAsync(IFileRef file)
    {
        await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
        using var buffer = stream.CanSeek
            ? new MemoryStream((int)Math.Min(stream.Length, int.MaxValue))
            : new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// 解码 HEIF 主图。<paramref name="file"/> 为整个文件的字节(容器解析与瓦片码流都从中切出)。
    /// </summary>
    /// <param name="file">完整文件字节。</param>
    /// <param name="maxLongEdge">目标长边上限;null 或 ≥ 画布长边时按画布尺寸输出。</param>
    /// <param name="encoding">目标编码(线性 RGBA16F、sRGB BGRA32 或 8 位 RGBA)。</param>
    /// <param name="cancellationToken">取消令牌;取消会抛出,不返回 null。</param>
    /// <param name="fallbackReason">返回 null 时的原因(不支持/解码失败);成功时为 null。</param>
    /// <param name="rgba8ColorSpace">
    /// <see cref="PixelEncoding.Rgba8"/> 的目标色彩空间(<see cref="ImageColorSpace.Srgb"/> 或 <see cref="ImageColorSpace.DisplayP3"/>);
    /// 其它编码忽略此参数。
    /// </param>
    /// <returns>未旋转的解码产物(方向在 <see cref="DecodedImage.Orientation"/>);需要回落返回 null。</returns>
    public static DecodedImage? TryDecode(
        byte[] file,
        int? maxLongEdge,
        PixelEncoding encoding,
        CancellationToken cancellationToken,
        out string? fallbackReason,
        ImageColorSpace rgba8ColorSpace = ImageColorSpace.Srgb)
    {
        fallbackReason = null;
        if (encoding is not (PixelEncoding.RgbaF16 or PixelEncoding.Bgra32 or PixelEncoding.Rgba8))
        {
            fallbackReason = "快速 HEIF 解码不支持目标编码 " + encoding;
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureInitialized()) return null;

        var container = HeifContainer.TryParse(file, out var parseReason);
        if (container is null)
        {
            fallbackReason = parseReason ?? "HEIF 容器不受支持";
            return null;
        }

        return DecodeContainer(container, maxLongEdge, encoding, rgba8ColorSpace, cancellationToken, out fallbackReason);
    }

    /// <summary>
    /// 用容器自带的 <c>thmb</c> 缩略图项代替主图解码:输出尺寸与主图路径完全一致
    /// (<see cref="TargetSize"/> 仍按主图画布折算),只是像素取自相机渲染的小图。
    /// </summary>
    /// <remarks>
    /// 相机写进容器的 1616×1080 / 320×212 项就是它自己渲染的小图,解一项约 5 ms;
    /// 胶片栏一次连翻要出十几张缩略图,用主图(33MP、6 块瓦片)会与当前图解码抢满 CPU。
    /// 只在请求的目标短边不超过缩略图项短边时使用(不放大相机小图),否则、或该项编码超出支持面/解码失败时返回 null,
    /// 由调用方走主图路径。
    /// </remarks>
    /// <param name="file">完整文件字节。</param>
    /// <param name="maxLongEdge">目标长边上限(按主图画布折算目标尺寸)。</param>
    /// <param name="encoding">目标编码(RGBA16F、BGRA32 或 8 位 RGBA)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="reason">返回 null 的原因(仅用于诊断)。</param>
    /// <param name="rgba8ColorSpace">
    /// <see cref="PixelEncoding.Rgba8"/> 的目标色彩空间;其它编码忽略此参数。
    /// </param>
    /// <returns>未旋转的缩略图项产物(方向为该 <c>thmb</c> 项自己的 <c>irot</c>/<c>imir</c>)。</returns>
    public static DecodedImage? TryDecodeContainerThumbnail(
        byte[] file,
        int maxLongEdge,
        PixelEncoding encoding,
        CancellationToken cancellationToken,
        out string? reason,
        ImageColorSpace rgba8ColorSpace = ImageColorSpace.Srgb)
    {
        reason = null;
        if (encoding is not (PixelEncoding.RgbaF16 or PixelEncoding.Bgra32 or PixelEncoding.Rgba8))
        {
            reason = "快速 HEIF 解码不支持目标编码 " + encoding;
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureInitialized())
        {
            reason = "pvhevc 不可用";
            return null;
        }

        var container = HeifContainer.TryParse(file, out var parseReason);
        if (container is null)
        {
            reason = parseReason ?? "HEIF 容器不受支持";
            return null;
        }

        var (targetWidth, targetHeight) = TargetSize(container, maxLongEdge);
        var minShortSide = Math.Min(targetWidth, targetHeight);
        // 没有缩略图项、或没有够大的(FindThumbnail 会退回最大项,那会把相机小图放大)都属于常规回落,
        // 不给原因:调用方只在真有解码失败时才值得记一条诊断。
        var item = container.FindThumbnail(minShortSide);
        if (item is null || item.ShortSide < minShortSide) return null;

        return DecodeItem(container, item, targetWidth, targetHeight, encoding, rgba8ColorSpace, cancellationToken, out reason);
    }

    // ------------------------------------------------------------------
    // 解码与转换
    // ------------------------------------------------------------------

    private static DecodedImage? DecodeContainer(
        HeifContainer container,
        int? maxLongEdge,
        PixelEncoding encoding,
        ImageColorSpace rgba8ColorSpace,
        CancellationToken cancellationToken,
        out string? fallbackReason)
    {
        fallbackReason = null;
        var tiles = container.Tiles;
        if (tiles.Count == 0)
        {
            fallbackReason = "容器没有可解码的瓦片";
            return null;
        }

        var inFlight = HeifDecodeStats.Enter();
        try
        {
            var reserved = Math.Min(tiles.Count, s_settings.MaxConcurrentContexts);
            var admission = Admit(reserved, cancellationToken, out var gateMs, out var contextsMs);
            var frames = new Slot[tiles.Count];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tilesStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var failure = DecodeTiles(container, frames, admission.Lane, cancellationToken);
                var tilesDone = System.Diagnostics.Stopwatch.GetTimestamp();
                if (failure is not null)
                {
                    fallbackReason = failure;
                    return null;
                }

                var result = Convert(container, frames, maxLongEdge, encoding, rgba8ColorSpace, cancellationToken, out fallbackReason);
                HeifDecodeStats.Record(
                    inFlight,
                    admission.Lane == DecodeLane.Current,
                    gateMs,
                    contextsMs,
                    Ms(tilesStart, tilesDone),
                    Ms(tilesDone, System.Diagnostics.Stopwatch.GetTimestamp()));
                return result;
            }
            finally
            {
                ReturnContexts(frames);
                admission.Dispose();
            }
        }
        finally
        {
            HeifDecodeStats.Leave();
        }
    }

    /// <summary>两个时间戳之间的毫秒数。</summary>
    private static double Ms(long from, long to)
        => (to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>一块瓦片的解码结果:持有的上下文(平面指针在其上有效)与帧描述。</summary>
    private struct Slot
    {
        public IntPtr Context;
        public NativeFrame Frame;
        public int Index;
    }

    /// <summary>并行解码全部瓦片;成功返回 null,失败返回原因(已回收失败的上下文)。</summary>
    private static unsafe string? DecodeTiles(HeifContainer container, Slot[] frames, DecodeLane lane, CancellationToken cancellationToken)
    {
        string? failure = null;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = TileParallelism(container.Tiles.Count, lane),
            CancellationToken = cancellationToken,
        };

        try
        {
            Parallel.For(0, container.Tiles.Count, options, (index, state) =>
            {
                if (Volatile.Read(ref failure) is not null)
                {
                    state.Stop();
                    return;
                }

                var tile = container.Tiles[index];
                if (!TryRentContext(out var context))
                {
                    Interlocked.CompareExchange(ref failure, "pvhevc 无法创建解码上下文", null);
                    state.Stop();
                    return;
                }

                var frame = new NativeFrame();
                int status;
                fixed (byte* stream = tile.AnnexB)
                {
                    status = pv_hevc_decode(context, stream, tile.AnnexB.Length, &frame);
                }

                if (status != 0)
                {
                    var message = Marshal.PtrToStringUTF8(pv_hevc_last_error(context));
                    // 解码失败的上下文状态未知,直接关闭不再入池。
                    pv_hevc_close(context);
                    Interlocked.CompareExchange(
                        ref failure,
                        "pvhevc 解码瓦片失败(" + status + "):" + (string.IsNullOrEmpty(message) ? "无附加信息" : message),
                        null);
                    state.Stop();
                    return;
                }

                if (ValidateFrame(container, tile, frame) is { } mismatch)
                {
                    pv_hevc_close(context);
                    Interlocked.CompareExchange(ref failure, mismatch, null);
                    state.Stop();
                    return;
                }

                frames[index] = new Slot { Context = context, Frame = frame, Index = index };
            });
        }
        catch (OperationCanceledException)
        {
            // 取消是控制流:先把手上的上下文收回,再原样抛出。
            ReturnContexts(frames);
            throw;
        }

        return failure;
    }

    /// <summary>
    /// 校验解码帧与容器声明一致:有效区域覆盖瓦片 <c>ispe</c>、位深与色度采样格式一致。
    /// </summary>
    /// <returns>不一致时的原因;一致为 null。</returns>
    private static string? ValidateFrame(HeifContainer container, HeifTile tile, in NativeFrame frame)
    {
        if (frame.Width < tile.Width || frame.Height < tile.Height)
        {
            return "瓦片 " + tile.Column + "," + tile.Row + " 解码尺寸 " + frame.Width + "x" + frame.Height
                + " 小于声明的 " + tile.Width + "x" + tile.Height;
        }

        if (frame.BitDepth is not (8 or 10 or 12)) return "解码位深 " + frame.BitDepth + " 不受支持";

        var bytesPerSample = frame.BitDepth > 8 ? 2 : 1;
        if (frame.StrideY < (long)frame.Width * bytesPerSample) return "解码亮度跨距小于有效宽度";
        if (frame.ChromaFormat != 0)
        {
            var chromaMinimum = 4 * bytesPerSample;
            if (frame.StrideCb < chromaMinimum || frame.StrideCr < chromaMinimum) return "解码色度跨距过小";
        }

        if (frame.ChromaFormat != container.ChromaFormat)
        {
            return "解码色度格式 " + frame.ChromaFormat + " 与容器声明 " + container.ChromaFormat + " 不一致";
        }

        var declaredBits = container.PixelInfo.BitsPerChannel;
        if (declaredBits.Count > 0 && declaredBits[0] != frame.BitDepth)
        {
            return "解码位深 " + frame.BitDepth + " 与容器 pixi 声明 " + declaredBits[0] + " 不一致";
        }

        return null;
    }

    /// <summary>把已解码的 YUV 平面一次性转成目标编码的展示缓冲。</summary>
    private static DecodedImage? Convert(
        HeifContainer container,
        Slot[] frames,
        int? maxLongEdge,
        PixelEncoding encoding,
        ImageColorSpace rgba8ColorSpace,
        CancellationToken cancellationToken,
        out string? fallbackReason)
    {
        fallbackReason = null;
        var yuvTiles = new YuvTile[frames.Length];
        for (var index = 0; index < frames.Length; index++)
        {
            var slot = frames[index];
            if (slot.Context == IntPtr.Zero)
            {
                fallbackReason = "瓦片解码结果缺失";
                return null;
            }

            var tile = container.Tiles[slot.Index];
            yuvTiles[index] = new YuvTile
            {
                Y = slot.Frame.Y,
                Cb = slot.Frame.Cb,
                Cr = slot.Frame.Cr,
                StrideY = slot.Frame.StrideY,
                StrideCb = slot.Frame.StrideCb,
                StrideCr = slot.Frame.StrideCr,
                Column = tile.PixelX,
                Row = tile.PixelY,
                Width = tile.Width,
                Height = tile.Height,
            };
        }

        var first = frames[0].Frame;
        var source = BuildYuvImage(yuvTiles, container.Width, container.Height, container.Color, first);
        var (targetWidth, targetHeight) = TargetSize(container, maxLongEdge);

        // 帧缓冲字节数只用于上下文池的内存记账(空闲上下文按此估算占用)。
        Volatile.Write(ref s_lastFrameBytes, FrameBytes(first));

        return Encode(
            source,
            targetWidth,
            targetHeight,
            encoding,
            rgba8ColorSpace,
            container.ExifOrientation,
            container.Width,
            container.Height,
            cancellationToken,
            out fallbackReason);
    }

    /// <summary>
    /// 解码一块 <c>thmb</c> 缩略图项并转成目标尺寸(目标尺寸由主图画布折算,与主图路径逐像素对齐)。
    /// </summary>
    /// <remarks>
    /// 相机把 1616×1080 项编码成 1664×1088 的码流(16 像素对齐),有效区域是 <c>ispe</c>:
    /// 与 libheif 一致,只取左上角 <c>ispe</c> 大小,多处的高度/宽度由 <see cref="YuvTile"/> 的有效尺寸声明。
    /// </remarks>
    private static unsafe DecodedImage? DecodeItem(
        HeifContainer container,
        HeifCodedImage item,
        int targetWidth,
        int targetHeight,
        PixelEncoding encoding,
        ImageColorSpace rgba8ColorSpace,
        CancellationToken cancellationToken,
        out string? reason)
    {
        reason = null;
        var admission = Admit(1, cancellationToken, out _, out _);
        var frames = new Slot[1];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryRentContext(out var context))
            {
                reason = "pvhevc 无法创建解码上下文";
                return null;
            }

            var frame = new NativeFrame();
            int status;
            fixed (byte* stream = item.AnnexB)
            {
                status = pv_hevc_decode(context, stream, item.AnnexB.Length, &frame);
            }

            if (status != 0)
            {
                var message = Marshal.PtrToStringUTF8(pv_hevc_last_error(context));
                pv_hevc_close(context);
                reason = "缩略图项解码失败(" + status + "):" + (string.IsNullOrEmpty(message) ? "无附加信息" : message);
                return null;
            }

            if (ValidateItemFrame(item, frame) is { } mismatch)
            {
                pv_hevc_close(context);
                reason = mismatch;
                return null;
            }

            frames[0] = new Slot { Context = context, Frame = frame, Index = 0 };

            var tile = new YuvTile
            {
                Y = frame.Y,
                Cb = frame.Cb,
                Cr = frame.Cr,
                StrideY = frame.StrideY,
                StrideCb = frame.StrideCb,
                StrideCr = frame.StrideCr,
                Column = 0,
                Row = 0,
                Width = item.Width,
                Height = item.Height,
            };
            var source = BuildYuvImage(new[] { tile }, item.Width, item.Height, item.Color, frame);

            Volatile.Write(ref s_lastFrameBytes, FrameBytes(frame));

            return Encode(
                source,
                targetWidth,
                targetHeight,
                encoding,
                rgba8ColorSpace,
                item.ExifOrientation,
                container.Width,
                container.Height,
                cancellationToken,
                out reason);
        }
        finally
        {
            ReturnContexts(frames);
            admission.Dispose();
        }
    }

    /// <summary>校验缩略图项的解码帧与该项自身声明(<c>ispe</c>/<c>hvcC</c>/<c>pixi</c>)一致。</summary>
    /// <returns>不一致时的原因;一致为 null。</returns>
    private static string? ValidateItemFrame(HeifCodedImage item, in NativeFrame frame)
    {
        if (frame.Width < item.Width || frame.Height < item.Height)
        {
            return "缩略图项 " + item.ItemId + " 解码尺寸 " + frame.Width + "x" + frame.Height
                + " 小于声明的 " + item.Width + "x" + item.Height;
        }

        if (frame.BitDepth is not (8 or 10 or 12)) return "缩略图项解码位深 " + frame.BitDepth + " 不受支持";

        var bytesPerSample = frame.BitDepth > 8 ? 2 : 1;
        if (frame.StrideY < (long)frame.Width * bytesPerSample) return "缩略图项亮度跨距小于解码宽度";
        if (frame.ChromaFormat != 0)
        {
            var chromaMinimum = 4 * bytesPerSample;
            if (frame.StrideCb < chromaMinimum || frame.StrideCr < chromaMinimum) return "缩略图项色度跨距过小";
        }

        if (frame.ChromaFormat != item.ChromaFormat)
        {
            return "缩略图项解码色度 " + frame.ChromaFormat + " 与 hvcC 声明 " + item.ChromaFormat + " 不一致";
        }

        var declaredBits = item.PixelInfo.BitsPerChannel;
        if (declaredBits.Count > 0 && declaredBits[0] != frame.BitDepth)
        {
            return "缩略图项解码位深 " + frame.BitDepth + " 与 pixi 声明 " + declaredBits[0] + " 不一致";
        }

        return null;
    }

    /// <summary>按解码帧与容器/项的色彩声明组装转换输入。</summary>
    private static YuvImage BuildYuvImage(
        YuvTile[] tiles,
        int canvasWidth,
        int canvasHeight,
        HeifColorInfo color,
        in NativeFrame frame)
        => new()
        {
            Tiles = tiles,
            CanvasWidth = canvasWidth,
            CanvasHeight = canvasHeight,
            BitDepth = frame.BitDepth,
            Chroma = frame.ChromaFormat switch
            {
                0 => HeifChromaSampling.Monochrome,
                2 => HeifChromaSampling.Yuv422,
                3 => HeifChromaSampling.Yuv444,
                _ => HeifChromaSampling.Yuv420,
            },
            // 色彩优先取容器 nclx(HEIF 权威声明),缺失时用码流 VUI 的码点。
            MatrixCode = color.HasNclx ? color.Matrix : frame.Matrix,
            PrimariesCode = color.HasNclx ? color.Primaries : frame.Primaries,
            TransferCode = color.HasNclx ? color.Transfer : frame.Transfer,
            FullRange = color.HasNclx ? color.FullRange : frame.FullRange != 0,
        };

    /// <summary>把 YUV 平面一次性转成目标编码的展示缓冲(尺寸、编码与方向由调用方给定)。</summary>
    private static DecodedImage? Encode(
        YuvImage source,
        int targetWidth,
        int targetHeight,
        PixelEncoding encoding,
        ImageColorSpace rgba8ColorSpace,
        int exifOrientation,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken,
        out string? fallbackReason)
    {
        fallbackReason = null;
        if (!YuvImageConverter.IsSupported(source, out var unsupported))
        {
            fallbackReason = unsupported;
            return null;
        }

        var linearHalf = encoding == PixelEncoding.RgbaF16;
        var bytesPerPixel = linearHalf ? 8 : 4;
        var stride = checked(targetWidth * bytesPerPixel);
        var buffer = DecodedImage.RentPixels(checked(stride * targetHeight));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? reason = null;
            var converted = encoding switch
            {
                PixelEncoding.RgbaF16 => YuvImageConverter.TryConvertToRgbaLinearF16(
                    source, targetWidth, targetHeight, buffer, stride, out reason),
                PixelEncoding.Rgba8 => YuvImageConverter.TryConvertToRgba8(
                    source, targetWidth, targetHeight, buffer, stride, rgba8ColorSpace, out reason),
                _ => YuvImageConverter.TryConvertToBgra32(
                    source, targetWidth, targetHeight, buffer, stride, out reason),
            };
            if (!converted)
            {
                ImageBufferPool.Return(buffer);
                fallbackReason = reason ?? "YUV 转换失败";
                return null;
            }

            return DecodedImage.CreatePooled(
                buffer,
                targetWidth,
                targetHeight,
                stride,
                encoding,
                encoding switch
                {
                    PixelEncoding.RgbaF16 => ImageColorSpace.LinearSrgb,
                    PixelEncoding.Rgba8 => rgba8ColorSpace,
                    _ => ImageColorSpace.Srgb,
                },
                sourceWidth,
                sourceHeight,
                exifOrientation);
        }
        catch
        {
            ImageBufferPool.Return(buffer);
            throw;
        }
    }

    /// <summary>目标尺寸:长边上限不小于画布长边时不重采样;否则按长边比例取整(与代理档口径一致)。</summary>
    private static (int Width, int Height) TargetSize(HeifContainer container, int? maxLongEdge)
    {
        var longEdge = Math.Max(container.Width, container.Height);
        if (maxLongEdge is not int limit || limit <= 0 || limit >= longEdge)
        {
            return (container.Width, container.Height);
        }

        var scale = (double)limit / longEdge;
        return (
            Math.Max(1, (int)Math.Round(container.Width * scale)),
            Math.Max(1, (int)Math.Round(container.Height * scale)));
    }

    /// <summary>一帧 YUV 平面的字节数(亮度 + 两路色度,按解码帧的宽高与位深)。</summary>
    private static int FrameBytes(in NativeFrame frame)
    {
        var bytesPerSample = frame.BitDepth > 8 ? 2 : 1;
        long chromaWidth = frame.ChromaFormat == 0 ? 0 : (frame.Width + 1) / 2;
        long chromaHeight = frame.ChromaFormat == 1 ? (frame.Height + 1) / 2 : frame.Height;
        if (frame.ChromaFormat == 3)
        {
            chromaWidth = frame.Width;
        }
        else if (frame.ChromaFormat == 0)
        {
            chromaHeight = 0;
        }

        var total = ((long)frame.Width * frame.Height + (2 * chromaWidth * chromaHeight)) * bytesPerSample;
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    // ------------------------------------------------------------------
    // 排队通道:当前图 / 沿 P0 并行 / 普通后台
    // ------------------------------------------------------------------

    /// <summary>解码排队通道(数值越小越优先)。</summary>
    private enum DecodeLane
    {
        /// <summary>P0 当前照片:不等后台,可以用超出常规上限的上下文。</summary>
        Current = 0,
        /// <summary>翻页方向上即将上屏的预取:排普通后台之前,可在 P0 在解时继续跑(流水线要跑在前面)。</summary>
        Alongside = 1,
        /// <summary>其余后台解码。</summary>
        Background = 2,
    }

    /// <summary>
    /// 从当前异步上下文的解码票面定通道:没有票面(缩略图等)按普通后台。
    /// 票面可能被合并进同一条在途解码的 P0 请求提升,所以判票要现读,不能缓存在外层。
    /// </summary>
    private static DecodeLane LaneOf()
    {
        var ticket = DecodeContext.Current;
        if (ticket is null) return DecodeLane.Background;
        if (ticket.Priority == DecodePriority.P0_Current) return DecodeLane.Current;
        return ticket.Alongside ? DecodeLane.Alongside : DecodeLane.Background;
    }

    /// <summary>
    /// 一张图的瓦片并行度:瓦片各自单线程,并行度就是它同时抢几个核。
    /// P0 与沿 P0 并行全开;普通后台在 P0 在解时收窄(默认 2 路),把核让给当前图。
    /// </summary>
    /// <param name="tileCount">该图的瓦片数。</param>
    /// <param name="lane">入场所用通道。</param>
    /// <returns>并行度。</returns>
    private static int TileParallelism(int tileCount, DecodeLane lane)
    {
        var limit = Math.Min(tileCount, Environment.ProcessorCount);
        if (lane != DecodeLane.Background || s_settings.BackgroundTilesDuringCurrent <= 0) return limit;
        return Volatile.Read(ref s_currentImages) > 0
            ? Math.Min(limit, s_settings.BackgroundTilesDuringCurrent)
            : limit;
    }

    /// <summary>
    /// 解码入场:先取"同时在解的图像数"名额,再一次预留全部瓦片的解码上下文(全有或全无)。
    /// </summary>
    /// <remarks>
    /// P0 通道不等后台:图像名额直接放行,上下文用 <see cref="FastHeifOptions.MaxCurrentContextsExtra"/>(常规上限之外另算),
    /// 所以连翻时的整幅解码不会排在预取后面,也不会等后台释放上下文。
    /// 沿 P0 并行通道排在普通后台之前,且不受"P0 在解就让位"约束(它的意义就是在 P0 跑着的同时跑)。
    /// 普通后台在 P0 在解时让位(<see cref="FastHeifOptions.HoldBackgroundDuringCurrent"/>):只挡排队,不打断已开跑的图。
    /// 名额先图像后上下文,且上下文全有或全无,故不存在持有上下文等图像的反向等待,无死锁;
    /// 取消在任一步都归还已拿到的名额。
    /// </remarks>
    /// <param name="contextCount">本图要预留的上下文数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="gateMs">等图像名额耗时(ms)。</param>
    /// <param name="contextsMs">等上下文预留耗时(ms)。</param>
    /// <returns>释放即归还名额。</returns>
    private static Admission Admit(int contextCount, CancellationToken cancellationToken, out double gateMs, out double contextsMs)
    {
        var admission = new Admission { Lane = LaneOf() };
        var registered = admission.Lane;
        RegisterWaiter(registered);
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            lock (s_admissionGate)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var lane = LaneOf();
                    if (lane != DecodeLane.Current && ImageSlotBusyLocked(lane))
                    {
                        Monitor.Wait(s_admissionGate, AdmissionPollMs);
                        continue;
                    }

                    if (lane == DecodeLane.Current) s_currentImages++;
                    else s_backgroundImages++;
                    admission.ImageAdmitted = true;
                    admission.ImageOnCurrentChannel = lane == DecodeLane.Current;
                    admission.Lane = lane;
                    break;
                }
            }

            gateMs = Ms(start, System.Diagnostics.Stopwatch.GetTimestamp());
            var contextsStart = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (s_admissionGate)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var lane = LaneOf();
                    if (lane == DecodeLane.Current)
                    {
                        if (s_currentContexts + contextCount > s_settings.MaxCurrentContexts)
                        {
                            Monitor.Wait(s_admissionGate, AdmissionPollMs);
                            continue;
                        }

                        s_currentContexts += contextCount;
                        admission.ContextsOnCurrentChannel = true;
                    }
                    else
                    {
                        if (s_backgroundContexts + contextCount > s_settings.MaxConcurrentContexts
                            || s_waitingCurrent > 0
                            || (lane == DecodeLane.Background
                                && (s_waitingAlongside > 0 || (s_settings.HoldBackgroundDuringCurrent && s_currentImages > 0))))
                        {
                            Monitor.Wait(s_admissionGate, AdmissionPollMs);
                            continue;
                        }

                        s_backgroundContexts += contextCount;
                        admission.ContextsOnCurrentChannel = false;
                    }

                    admission.Contexts = contextCount;
                    admission.Lane = lane;
                    break;
                }
            }

            contextsMs = Ms(contextsStart, System.Diagnostics.Stopwatch.GetTimestamp());
            return admission;
        }
        catch
        {
            admission.Dispose();
            throw;
        }
        finally
        {
            UnregisterWaiter(registered);
        }
    }

    /// <summary>图像名额是否已满(按通道);调用方须持有 <see cref="s_admissionGate"/>。</summary>
    private static bool ImageSlotBusyLocked(DecodeLane lane)
        => s_backgroundImages >= s_settings.MaxConcurrentImages
            || s_waitingCurrent > 0
            || (lane == DecodeLane.Background
                && (s_waitingAlongside > 0 || (s_settings.HoldBackgroundDuringCurrent && s_currentImages > 0)));

    /// <summary>登记"有该通道的请求在等",让更低优先级的请求先让开。</summary>
    private static void RegisterWaiter(DecodeLane lane)
    {
        switch (lane)
        {
            case DecodeLane.Current:
                Interlocked.Increment(ref s_waitingCurrent);
                break;
            case DecodeLane.Alongside:
                Interlocked.Increment(ref s_waitingAlongside);
                break;
        }
    }

    /// <summary>注销等待登记。</summary>
    private static void UnregisterWaiter(DecodeLane lane)
    {
        switch (lane)
        {
            case DecodeLane.Current:
                Interlocked.Decrement(ref s_waitingCurrent);
                break;
            case DecodeLane.Alongside:
                Interlocked.Decrement(ref s_waitingAlongside);
                break;
        }
    }

    /// <summary>一次解码入场拿到的名额;释放时归还(先归还上下文,再释放本对象)。</summary>
    private sealed class Admission : IDisposable
    {
        private int _disposed;

        /// <summary>入场所用通道(决定瓦片并行度)。</summary>
        public DecodeLane Lane;

        /// <summary>图像名额是否已拿到。</summary>
        public bool ImageAdmitted;

        /// <summary>图像名额记在 P0 通道。</summary>
        public bool ImageOnCurrentChannel;

        /// <summary>上下文名额是否记在 P0 通道。</summary>
        public bool ContextsOnCurrentChannel;

        /// <summary>预留的上下文个数;0 = 尚未预留。</summary>
        public int Contexts;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (s_admissionGate)
            {
                if (ImageAdmitted)
                {
                    if (ImageOnCurrentChannel) s_currentImages--;
                    else s_backgroundImages--;
                }

                if (Contexts > 0)
                {
                    if (ContextsOnCurrentChannel) s_currentContexts -= Contexts;
                    else s_backgroundContexts -= Contexts;
                }

                Monitor.PulseAll(s_admissionGate);
            }
        }
    }

    /// <summary>读取布尔型环境变量(压测对照实验用);缺省取 <paramref name="fallback"/>。</summary>
    private static bool EnvFlag(string name, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Trim().ToLowerInvariant() is not ("0" or "false" or "off");
    }

    /// <summary>读取整数型环境变量(压测对照实验用);缺省或非法取 <paramref name="fallback"/>。</summary>
    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    // ------------------------------------------------------------------
    // 初始化与上下文池
    // ------------------------------------------------------------------

    /// <summary>
    /// 一次性配置 pvhevc 的加载方式与解码并发上限,须在首次解码前调用。
    /// </summary>
    /// <remarks>
    /// 共享工程不假定任何平台的原生库位置:未调用本方法的平台按 <c>DllImport("pvhevc")</c> 的默认探测加载
    /// (Windows 即应用目录下的 <c>pvhevc.dll</c>,Android 即 APK 的 native 库目录),完全不受本类影响;
    /// 需要自定义路径(例如单文件发布解包目录)的平台在启动时传入 <see cref="FastHeifOptions.LoadLibrary"/>。
    /// 解析器注册在共享程序集上且只认 <c>pvhevc</c> 这个名字,不影响同程序集的其它原生依赖。
    /// 每个进程只允许调用一次。
    /// </remarks>
    /// <param name="options">加载方式与并发上限。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">解码器已初始化或已配置过。</exception>
    public static void Configure(FastHeifOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (s_initializationGate)
        {
            if (s_initialized)
            {
                throw new InvalidOperationException("FastHeifDecoder 已初始化,Configure 必须在首次解码前调用。");
            }
            if (s_configured != 0)
            {
                throw new InvalidOperationException("FastHeifDecoder 只能配置一次。");
            }

            s_settings = CreateSettings(options);
            if (options.LoadLibrary is not null) InstallResolver(options.LoadLibrary);
            s_configured = 1;
        }
    }

    /// <summary>
    /// 串行完成一次性初始化:解析器安装 + 首次 open/close。
    /// </summary>
    /// <remarks>
    /// 并行首用会让多个线程同时触发解码器的全局初始化,现场见过一次并行首解抛异常;
    /// 因此先单线程打开再关闭一个上下文,后续并行解码不受影响。
    /// </remarks>
    /// <returns>DLL 与解码器可用时为 true。</returns>
    private static bool EnsureInitialized()
    {
        if (s_initialized) return s_available;
        lock (s_initializationGate)
        {
            if (s_initialized) return s_available;
            try
            {
                s_available = pv_hevc_version() != 0;
                if (s_available)
                {
                    var context = pv_hevc_open(1);
                    if (context != IntPtr.Zero) pv_hevc_close(context);
                    else s_available = false;
                }
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                s_available = false;
                Console.WriteLine("[FastHeifDecoder] pvhevc.dll 不可用:" + exception.Message);
            }

            MemoryGovernor.Register(s_memoryClient);
            s_initialized = true;
            return s_available;
        }
    }

    /// <summary>
    /// 安装平台注入的 pvhevc 加载器:只接管名字为 <c>pvhevc</c> 的导入,其余交回默认探测;
    /// 加载器返回 0 时同样交回默认探测。
    /// </summary>
    /// <remarks>同一程序集只允许一个 <c>DllImport</c> 解析器,已被占用时记录一条诊断并保留默认探测。</remarks>
    private static void InstallResolver(Func<IntPtr> loadLibrary)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(FastHeifDecoder).Assembly, (name, _, _) =>
                string.Equals(name, LibraryName, StringComparison.OrdinalIgnoreCase)
                    ? loadLibrary()
                    : IntPtr.Zero);
        }
        catch (InvalidOperationException exception)
        {
            Console.WriteLine("[FastHeifDecoder] 程序集已有 DllImport 解析器,改用默认探测:" + exception.Message);
        }
    }

    /// <summary>取一个上下文:优先复用池中空闲实例,池空时新建。</summary>
    private static bool TryRentContext(out IntPtr context)
    {
        lock (s_poolGate)
        {
            if (s_idleContexts.Count > 0)
            {
                context = s_idleContexts.Pop();
                return true;
            }
        }

        context = pv_hevc_open(1);
        return context != IntPtr.Zero;
    }

    /// <summary>归还一批瓦片持有的上下文(整幅解码结束后一次归还)。</summary>
    private static void ReturnContexts(Slot[] frames)
    {
        for (var index = 0; index < frames.Length; index++)
        {
            var context = frames[index].Context;
            if (context == IntPtr.Zero) continue;
            frames[index].Context = IntPtr.Zero;
            ReturnContext(context);
        }
    }

    /// <summary>归还一个上下文:池未满则入池复用,否则关闭。</summary>
    private static void ReturnContext(IntPtr context)
    {
        if (context == IntPtr.Zero) return;
        lock (s_poolGate)
        {
            if (s_idleContexts.Count < s_settings.MaxIdleContexts)
            {
                s_idleContexts.Push(context);
                return;
            }
        }

        pv_hevc_close(context);
    }

    /// <summary>关闭全部空闲上下文(压力修剪;不阻塞)。</summary>
    private static void TrimIdleContexts()
    {
        IntPtr[] idle;
        lock (s_poolGate)
        {
            if (s_idleContexts.Count == 0) return;
            idle = s_idleContexts.ToArray();
            s_idleContexts.Clear();
        }

        foreach (var context in idle) pv_hevc_close(context);
    }

    private static int IdleContextCount
    {
        get
        {
            lock (s_poolGate) return s_idleContexts.Count;
        }
    }

    /// <summary>
    /// 空闲解码上下文的内存记账:空闲上下文按其内部帧缓冲(最近一帧的平面字节数)估算占用,
    /// 压力 Caution 起全部关闭。
    /// </summary>
    private sealed class IdleContextMemoryClient : IMemoryClient
    {
        public string Name => "HevcContexts";

        public long FootprintBytes => (long)IdleContextCount * Volatile.Read(ref s_lastFrameBytes);

        public void Trim(PressureLevel level)
        {
            if (level < PressureLevel.Caution) return;
            TrimIdleContexts();
        }
    }
}

/// <summary>
/// pvhevc 原生库加载方式与解码并发的可选配置;未调用 <see cref="FastHeifDecoder.Configure"/> 时全部取默认值。
/// </summary>
public sealed class FastHeifOptions
{
    /// <summary>
    /// 打开 pvhevc 原生库并返回模块句柄;null 表示交给默认 DllImport 探测(应用目录同名的 <c>pvhevc</c> 库)。
    /// 回调返回 <see cref="IntPtr.Zero"/> 时同样退回默认探测。
    /// </summary>
    public Func<IntPtr>? LoadLibrary { get; init; }

    /// <summary>空闲上下文池上限;超出上限的归还即关闭,避免空闲解码器长占内存。</summary>
    public int MaxIdleContexts { get; init; } = 6;

    /// <summary>全局同时在用的解码上下文上限(默认处理器数)。</summary>
    public int MaxConcurrentContexts { get; init; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// 当前照片通道在常规上限之外额外放宽的上下文数:保证 P0 不等后台释放上下文。
    /// 默认按核数收敛(处理器数的一半,2–8)。
    /// </summary>
    public int MaxCurrentContextsExtra { get; init; }
        = Math.Clamp(Math.Max(1, Environment.ProcessorCount) / 2, 2, 8);

    /// <summary>同时在转换的图像上限(默认 max(2, 处理器数 / 4))。</summary>
    public int MaxConcurrentImages { get; init; } = Math.Max(2, Environment.ProcessorCount / 4);

    /// <summary>后台起解(进瓦片阶段)时若当前图在解,瓦片并行度上限;0 表示不收窄。</summary>
    public int BackgroundTilesDuringCurrent { get; init; } = 2;

    /// <summary>当前图在解时普通后台是否让位(只影响排队,不影响已开跑的图)。</summary>
    public bool HoldBackgroundDuringCurrent { get; init; } = true;
}
