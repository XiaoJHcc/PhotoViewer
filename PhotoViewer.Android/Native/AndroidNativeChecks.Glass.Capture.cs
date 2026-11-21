using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using PhotoViewer.Core.Image;
using AndroidColorSpace = Android.Graphics.ColorSpace;
using AndroidActivity = Android.App.Activity;
using JavaByteBuffer = Java.Nio.ByteBuffer;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 阶段 0 技术验证的回读与逐像素度量（验收专用，<c>pv-check glass</c> 宿主组使用）：
/// 整窗与图层的 <see cref="PixelCopy"/> 异步回读（超时、真实结果码、位图回收），
/// 物理 100% 的逐字节比较（另给 ±1、最大差与几何偏移诊断），以及 8 位/半精度位深证据的取样。
/// 只做测量：不改变产品路径，不含任何材质、模糊或光学实现。
/// </summary>
/// <remarks>
/// 口径约定：
/// <list type="bullet">
/// <item>整窗回读的源坐标是**窗口表面坐标**，整窗即 <c>(0, 0, DecorView 宽, 高)</c>；传 rect 时按同一空间解释并裁到源内。</item>
/// <item>Window 与 SurfaceView 两种 <see cref="PixelCopy"/> 源的绑定都按 API 26 标注，本文件统一在运行时分级
/// （Surface 源的裸 <c>Surface</c> 重载自 API 24 可用，但产品走窗口/图层两条，不另开一路）。</item>
/// <item>逐字节一致的判定**只**看三通道是否相等（与 <see cref="ColorDeltaRgb"/> 同一口径，窗口表面没有 alpha 位）；
/// ±1、最大差、偏移都只是诊断输出，不得拿去当 PASS 依据。</item>
/// <item>超时或取消时不在原地回收位图：那次回读可能仍在写入，回收交给迟到的回调，避免位图被提前释放。
/// 连续超时到上限后暂停新的回读（不再分配位图），避免占用量继续叠加——宁可如实报失败，也不冒原生崩溃的风险。</item>
/// </list>
/// </remarks>
internal static partial class AndroidNativeChecks
{
    /// <summary>回读默认超时（ms）。</summary>
    internal const int GlassCaptureTimeoutMs = 3000;

    /// <summary>逐字节比较的默认几何偏移搜索半径（像素）。</summary>
    internal const int GlassOffsetSearchRadius = 4;

    /// <summary>连续超时上限：超时那次回读的位图要等迟到回调才能回收，连续超时说明管线卡住。</summary>
    /// <remarks>
    /// 到上限后暂停回读并如实报失败（不再分配位图，避免继续叠加占用），
    /// 下一次成功或收到真实结果码立即复位。任何情况下都不提前回收可能仍在写入的位图。
    /// </remarks>
    internal const int GlassCaptureMaxConsecutiveTimeouts = 3;

    /// <summary>连续超时计数（成功或收到真实结果码即复位）。</summary>
    private static int _glassCaptureConsecutiveTimeouts;

    // PixelCopy 结果码的平台常量（android.view.PixelCopy）。这里按数值复述，避免绑定层把同名常量
    // 生成成枚举类型后与监听器的 int 参数对不上；数值与平台常量的对应关系逐条注明。
    private const int GlassPixelCopySuccess = 0;
    private const int GlassPixelCopyErrorUnknown = 1;
    private const int GlassPixelCopyErrorTimeout = 2;
    private const int GlassPixelCopyErrorSourceInvalid = 3;
    private const int GlassPixelCopyErrorSourceNoData = 4;
    private const int GlassPixelCopyErrorDestinationInvalid = 5;

    /// <summary>回读位图的像素格式。</summary>
    internal enum GlassCapturePixelFormat
    {
        /// <summary>ARGB_8888：逐字节比较只接受这一种，位深证据也可用。</summary>
        Argb8888,
        /// <summary>RGBA_F16（API 26）：只用于位深/量化证据，不参与逐字节一致判定。</summary>
        RgbaF16,
    }

    /// <summary>回读位图的目标色域标签。</summary>
    internal enum GlassCaptureColorSpace
    {
        /// <summary>sRGB（也是 API 26 以下位图的唯一解释）。</summary>
        Srgb,
        /// <summary>Display P3。</summary>
        DisplayP3,
    }

    /// <summary>回读失败的分类（超时、平台结果码与调用方取消分开记，不混成一句话）。</summary>
    internal enum GlassCaptureFailure
    {
        /// <summary>没有失败。</summary>
        None,
        /// <summary>当前系统版本没有这项能力（Window 源需要 API 26）。</summary>
        UnsupportedApi,
        /// <summary>回读矩形为空或完全落在源外。</summary>
        InvalidSourceRect,
        /// <summary>目标位图分配失败（如内存不足）。</summary>
        AllocationFailed,
        /// <summary>PixelCopy 返回了非成功结果码。</summary>
        PixelCopyFailed,
        /// <summary>回调未在超时内到达；位图由迟到的回调回收。</summary>
        Timeout,
        /// <summary>调用方取消；位图由迟到的回调回收。</summary>
        Cancelled,
    }

    /// <summary>源矩形（不含位图所有权的纯数据，报告里可直接写）。</summary>
    internal readonly record struct GlassCaptureRect(int Left, int Top, int Width, int Height)
    {
        /// <summary>空矩形。</summary>
        internal static GlassCaptureRect Empty => new(0, 0, 0, 0);

        /// <summary>是否没有可取像素。</summary>
        internal bool IsEmpty => Width <= 0 || Height <= 0;

        /// <summary>转成平台矩形（调用方负责释放）。</summary>
        internal Rect ToRect() => new(Left, Top, Left + Width, Top + Height);

        /// <summary>报告用描述。</summary>
        internal string Describe() => $"({Left},{Top}) {Width}x{Height}";
    }

    /// <summary>
    /// 一次回读的结果：成功时独占一张位图（调用方 <c>using</c> 或 <see cref="TakeBitmap"/> 接管），
    /// 失败时带真实结果码与可读原因。<see cref="Dispose"/> 会回收位图，幂等。
    /// </summary>
    internal sealed class GlassCaptureResult : IDisposable
    {
        private Bitmap? _bitmap;
        private int _disposed;

        private GlassCaptureResult(Bitmap? bitmap, GlassCaptureFailure failure, string message, int resultCode,
            GlassCaptureRect sourceRect, GlassCapturePixelFormat format, GlassCaptureColorSpace colorSpace,
            double elapsedMs)
        {
            _bitmap = bitmap;
            Failure = failure;
            Message = message;
            ResultCode = resultCode;
            SourceRect = sourceRect;
            Format = format;
            ColorSpace = colorSpace;
            ElapsedMs = elapsedMs;
        }

        /// <summary>是否拿到了可读位图。</summary>
        internal bool Ok => _bitmap != null;

        /// <summary>失败分类；成功为 <see cref="GlassCaptureFailure.None"/>。</summary>
        internal GlassCaptureFailure Failure { get; }

        /// <summary>失败原因（含 PixelCopy 结果码名、系统版本或异常名）。</summary>
        internal string Message { get; }

        /// <summary>PixelCopy 的原始结果码；未进入回读为 -1。</summary>
        internal int ResultCode { get; }

        /// <summary>本次回读的源矩形（窗口表面坐标或图层表面坐标）。</summary>
        internal GlassCaptureRect SourceRect { get; }

        /// <summary>请求的像素格式。</summary>
        internal GlassCapturePixelFormat Format { get; }

        /// <summary>请求的目标色域标签。</summary>
        internal GlassCaptureColorSpace ColorSpace { get; }

        /// <summary>提交到结果返回的墙钟（ms，含等待回调）。</summary>
        internal double ElapsedMs { get; }

        /// <summary>回读位图；所有权归本结果，<see cref="Dispose"/> 后为 null。</summary>
        internal Bitmap? Bitmap => _bitmap;

        /// <summary>位图自报色域的可读名（API 26 以下如实写「无色彩管理」）。</summary>
        internal string BitmapColorSpaceName
        {
            get
            {
                var bitmap = _bitmap;
                return bitmap == null ? "无位图" : GlassBitmapColorSpaceName(bitmap);
            }
        }

        /// <summary>成功结果。</summary>
        internal static GlassCaptureResult Succeeded(Bitmap bitmap, GlassCaptureRect sourceRect,
            GlassCapturePixelFormat format, GlassCaptureColorSpace colorSpace, int resultCode, double elapsedMs)
            => new(bitmap, GlassCaptureFailure.None, string.Empty, resultCode, sourceRect, format, colorSpace, elapsedMs);

        /// <summary>失败结果（没有位图）。</summary>
        internal static GlassCaptureResult Failed(GlassCaptureFailure failure, string message,
            GlassCaptureRect sourceRect, GlassCapturePixelFormat format, GlassCaptureColorSpace colorSpace,
            double elapsedMs, int resultCode = -1)
            => new(null, failure, message, resultCode, sourceRect, format, colorSpace, elapsedMs);

        /// <summary>把位图所有权交出去；交接后 <see cref="Dispose"/> 不再回收它。</summary>
        internal Bitmap? TakeBitmap()
        {
            var bitmap = _bitmap;
            _bitmap = null;
            return bitmap;
        }

        /// <summary>回收位图；幂等，可在任意线程调用。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            RecycleGlassBitmap(TakeBitmap());
        }
    }

    /// <summary>提交一次 PixelCopy 的委托（窗口与图层两种源共用同一条超时/结果链路）。</summary>
    /// <param name="destination">目标位图。</param>
    /// <param name="listener">结果监听器。</param>
    /// <param name="handler">回调用 Handler。</param>
    private delegate void GlassPixelCopySubmit(Bitmap destination,
        PixelCopy.IOnPixelCopyFinishedListener listener, Handler handler);

    /// <summary>
    /// 整窗回读（默认取整个 DecorView）。窗口源从 API 26 起可用，低版本返回
    /// <see cref="GlassCaptureFailure.UnsupportedApi"/> 而不抛异常。
    /// </summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="format">目标像素格式。</param>
    /// <param name="colorSpace">目标色域标签。</param>
    /// <param name="sourceRect">窗口表面坐标下的源矩形；null 表示整窗。</param>
    /// <param name="timeoutMs">超时（ms）。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>回读结果；调用方负责 <c>using</c>。</returns>
    internal static Task<GlassCaptureResult> CaptureGlassWindowAsync(AndroidActivity activity,
        GlassCapturePixelFormat format = GlassCapturePixelFormat.Argb8888,
        GlassCaptureColorSpace colorSpace = GlassCaptureColorSpace.Srgb,
        Rect? sourceRect = null, int timeoutMs = GlassCaptureTimeoutMs, CancellationToken token = default)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return Task.FromResult(GlassCaptureResult.Failed(GlassCaptureFailure.UnsupportedApi,
                $"整窗 PixelCopy 需要 API 26，当前 API {(int)Build.VERSION.SdkInt}", GlassCaptureRect.Empty,
                format, colorSpace, 0));

        var window = activity.Window;
        var decor = window?.DecorView;
        if (window == null || decor == null)
            return Task.FromResult(GlassCaptureResult.Failed(GlassCaptureFailure.InvalidSourceRect,
                "窗口未就绪（Window/DecorView 为 null）", GlassCaptureRect.Empty, format, colorSpace, 0));
        if (decor.Width <= 0 || decor.Height <= 0)
            return Task.FromResult(GlassCaptureResult.Failed(GlassCaptureFailure.InvalidSourceRect,
                $"DecorView 尚未布局：{decor.Width}x{decor.Height}", GlassCaptureRect.Empty, format, colorSpace, 0));

        var rect = ResolveGlassCaptureRect(sourceRect, decor.Width, decor.Height, out string error);
        if (rect.IsEmpty)
            return Task.FromResult(GlassCaptureResult.Failed(GlassCaptureFailure.InvalidSourceRect, error,
                rect, format, colorSpace, 0));

        return CaptureGlassWindowApi26Async(window, rect, format, colorSpace, timeoutMs, token);
    }

    /// <summary>整窗回读的 API 26 提交路径（调用方已按版本分级）。</summary>
    /// <param name="window">窗口。</param>
    /// <param name="rect">已裁剪的源矩形。</param>
    /// <param name="format">目标像素格式。</param>
    /// <param name="colorSpace">目标色域标签。</param>
    /// <param name="timeoutMs">超时（ms）。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>回读结果。</returns>
    [SupportedOSPlatform("android26.0")]
    private static Task<GlassCaptureResult> CaptureGlassWindowApi26Async(Window window, GlassCaptureRect rect,
        GlassCapturePixelFormat format, GlassCaptureColorSpace colorSpace, int timeoutMs, CancellationToken token)
        => CaptureGlassAsync(rect, format, colorSpace, timeoutMs, token,
            (bitmap, listener, handler) => SubmitGlassWindowCopy(window, rect.ToRect(), bitmap, listener, handler));

    /// <summary>
    /// 图层回读（<see cref="SurfaceView"/> 源）：用于拿 SurfaceView 基线自己的像素，
    /// 与整窗回读对照「照片是否真的进了窗口缓冲」。绑定按 API 26 标注，低版本如实返回
    /// <see cref="GlassCaptureFailure.UnsupportedApi"/>。
    /// </summary>
    /// <param name="view">目标 SurfaceView。</param>
    /// <param name="format">目标像素格式。</param>
    /// <param name="colorSpace">目标色域标签。</param>
    /// <param name="sourceRect">图层表面坐标下的源矩形；null 表示整个视图。</param>
    /// <param name="timeoutMs">超时（ms）。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>回读结果；调用方负责 <c>using</c>。</returns>
    internal static Task<GlassCaptureResult> CaptureGlassSurfaceViewAsync(SurfaceView view,
        GlassCapturePixelFormat format = GlassCapturePixelFormat.Argb8888,
        GlassCaptureColorSpace colorSpace = GlassCaptureColorSpace.Srgb,
        Rect? sourceRect = null, int timeoutMs = GlassCaptureTimeoutMs, CancellationToken token = default)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return Task.FromResult(GlassCaptureResult.Failed(GlassCaptureFailure.UnsupportedApi,
                $"图层 PixelCopy 需要 API 26，当前 API {(int)Build.VERSION.SdkInt}", GlassCaptureRect.Empty,
                format, colorSpace, 0));
        var holderSurface = view.Holder?.Surface;
        if (holderSurface == null || !holderSurface.IsValid)
            return Task.FromResult(GlassCaptureResult.Failed(GlassCaptureFailure.InvalidSourceRect,
                "SurfaceView 表面不可用", GlassCaptureRect.Empty, format, colorSpace, 0));
        if (view.Width <= 0 || view.Height <= 0)
            return Task.FromResult(GlassCaptureResult.Failed(GlassCaptureFailure.InvalidSourceRect,
                $"SurfaceView 尚未布局：{view.Width}x{view.Height}", GlassCaptureRect.Empty, format, colorSpace, 0));

        var rect = ResolveGlassCaptureRect(sourceRect, view.Width, view.Height, out string error);
        if (rect.IsEmpty)
            return Task.FromResult(GlassCaptureResult.Failed(GlassCaptureFailure.InvalidSourceRect, error,
                rect, format, colorSpace, 0));

        return CaptureGlassSurfaceViewApi26Async(view, rect, format, colorSpace, timeoutMs, token);
    }

    /// <summary>图层回读的 API 26 提交路径（调用方已按版本分级）。</summary>
    /// <param name="view">SurfaceView。</param>
    /// <param name="rect">已裁剪的源矩形。</param>
    /// <param name="format">目标像素格式。</param>
    /// <param name="colorSpace">目标色域标签。</param>
    /// <param name="timeoutMs">超时（ms）。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>回读结果。</returns>
    [SupportedOSPlatform("android26.0")]
    private static Task<GlassCaptureResult> CaptureGlassSurfaceViewApi26Async(SurfaceView view,
        GlassCaptureRect rect, GlassCapturePixelFormat format, GlassCaptureColorSpace colorSpace, int timeoutMs,
        CancellationToken token)
        => CaptureGlassAsync(rect, format, colorSpace, timeoutMs, token,
            (bitmap, listener, handler) => SubmitGlassSurfaceViewCopy(view, rect.ToRect(), bitmap, listener, handler));

    /// <summary>把请求矩形裁进源尺寸；请求为空或裁完为空时给出原因。</summary>
    /// <param name="requested">调用方请求的矩形；null 表示整个源。</param>
    /// <param name="maxWidth">源宽。</param>
    /// <param name="maxHeight">源高。</param>
    /// <param name="error">为空时的原因。</param>
    /// <returns>裁剪后的矩形；空表示不可回读。</returns>
    private static GlassCaptureRect ResolveGlassCaptureRect(Rect? requested, int maxWidth, int maxHeight,
        out string error)
    {
        error = string.Empty;
        if (requested == null) return new GlassCaptureRect(0, 0, maxWidth, maxHeight);
        int left = Math.Max(0, requested.Left);
        int top = Math.Max(0, requested.Top);
        int right = Math.Min(maxWidth, requested.Right);
        int bottom = Math.Min(maxHeight, requested.Bottom);
        if (right <= left || bottom <= top)
        {
            error = $"回读矩形在源内为空：请求 {requested}，源 {maxWidth}x{maxHeight}";
            return GlassCaptureRect.Empty;
        }
        return new GlassCaptureRect(left, top, right - left, bottom - top);
    }

    /// <summary>Window 源的提交（API 26；调用方已按版本分级）。</summary>
    /// <param name="window">窗口。</param>
    /// <param name="rect">窗口表面坐标下的源矩形。</param>
    /// <param name="bitmap">目标位图。</param>
    /// <param name="listener">结果监听器。</param>
    /// <param name="handler">回调用 Handler。</param>
    [SupportedOSPlatform("android26.0")]
    private static void SubmitGlassWindowCopy(Window window, Rect rect, Bitmap bitmap,
        PixelCopy.IOnPixelCopyFinishedListener listener, Handler handler)
        => PixelCopy.Request(window, rect, bitmap, listener, handler);

    /// <summary>SurfaceView 源的提交（API 26；调用方已按版本分级）。</summary>
    /// <param name="view">SurfaceView。</param>
    /// <param name="rect">图层表面坐标下的源矩形。</param>
    /// <param name="bitmap">目标位图。</param>
    /// <param name="listener">结果监听器。</param>
    /// <param name="handler">回调用 Handler。</param>
    [SupportedOSPlatform("android26.0")]
    private static void SubmitGlassSurfaceViewCopy(SurfaceView view, Rect rect, Bitmap bitmap,
        PixelCopy.IOnPixelCopyFinishedListener listener, Handler handler)
        => PixelCopy.Request(view, rect, bitmap, listener, handler);

    /// <summary>
    /// 回读主干：分配位图 → 提交 → 等真实结果或超时/取消。位图在成功时转交调用方，
    /// 失败时由回调或迟到回调回收；本方法不吞掉 PixelCopy 的结果码。
    /// </summary>
    /// <param name="sourceRect">已裁剪的源矩形。</param>
    /// <param name="format">目标像素格式。</param>
    /// <param name="colorSpace">目标色域标签。</param>
    /// <param name="timeoutMs">超时（ms）。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="submit">提交委托。</param>
    /// <returns>回读结果。</returns>
    private static async Task<GlassCaptureResult> CaptureGlassAsync(GlassCaptureRect sourceRect,
        GlassCapturePixelFormat format, GlassCaptureColorSpace colorSpace, int timeoutMs,
        CancellationToken token, GlassPixelCopySubmit submit)
    {
        long started = Stopwatch.GetTimestamp();
        if (Volatile.Read(ref _glassCaptureConsecutiveTimeouts) >= GlassCaptureMaxConsecutiveTimeouts)
        {
            // 管线已连续卡住：不再分配位图（超时那几张只能等迟到回调回收），如实报失败。
            return GlassCaptureResult.Failed(GlassCaptureFailure.Timeout,
                $"已连续 {GlassCaptureMaxConsecutiveTimeouts} 次回读超时，本次不再分配位图；"
                    + "需排查主线程/表面状态后再试", sourceRect, format, colorSpace, 0);
        }

        Bitmap bitmap;
        try
        {
            bitmap = CreateGlassCaptureBitmap(sourceRect.Width, sourceRect.Height, format, colorSpace);
        }
        catch (Exception exception)
        {
            return GlassCaptureResult.Failed(GlassCaptureFailure.AllocationFailed,
                $"{exception.GetType().Name}：{exception.Message}", sourceRect, format, colorSpace,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var request = new GlassPixelCopyRequest(bitmap, sourceRect, format, colorSpace, started);
        try
        {
            submit(bitmap, request, new Handler(Looper.MainLooper!));
        }
        catch (Exception exception)
        {
            // 提交失败时请求没有进入平台，位图可以就地回收。
            RecycleGlassBitmap(request.TakeBitmap());
            return GlassCaptureResult.Failed(GlassCaptureFailure.PixelCopyFailed,
                $"提交 PixelCopy 失败：{exception.GetType().Name}：{exception.Message}", sourceRect, format,
                colorSpace, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var copy = request.Completion.Task;
        var delay = Task.Delay(Math.Max(0, timeoutMs), token);
        if (await Task.WhenAny(copy, delay).ConfigureAwait(false) != copy)
        {
            // 先收口再取结果：Abandon 已经写入失败结果，迟到的回调看到收口标记后只回收位图。
            bool cancelled = token.IsCancellationRequested;
            if (!cancelled) Interlocked.Increment(ref _glassCaptureConsecutiveTimeouts);
            request.Abandon(cancelled ? GlassCaptureFailure.Cancelled : GlassCaptureFailure.Timeout,
                cancelled
                    ? "回读已取消（位图由迟到的回调回收）"
                    : $"回读 {timeoutMs}ms 未收到回调（位图由迟到的回调回收）",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        return await copy.ConfigureAwait(false);
    }

    /// <summary>按格式与色域分配回读位图；API 26 以下只接受 ARGB_8888 且恒为 sRGB。</summary>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="colorSpace">色域标签。</param>
    /// <returns>新建位图。</returns>
    private static Bitmap CreateGlassCaptureBitmap(int width, int height, GlassCapturePixelFormat format,
        GlassCaptureColorSpace colorSpace)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            return CreateGlassCaptureBitmapApi26(width, height, format, colorSpace);
        if (format == GlassCapturePixelFormat.RgbaF16)
            throw new NotSupportedException($"RGBA_F16 回读需要 API 26，当前 API {(int)Build.VERSION.SdkInt}");
        return Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
    }

    /// <summary>API 26 起的位图分配：同一次调用里带上色域标签，不做事后改标。</summary>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="colorSpace">色域标签。</param>
    /// <returns>新建位图。</returns>
    [SupportedOSPlatform("android26.0")]
    private static Bitmap CreateGlassCaptureBitmapApi26(int width, int height, GlassCapturePixelFormat format,
        GlassCaptureColorSpace colorSpace)
    {
        var config = format == GlassCapturePixelFormat.RgbaF16 ? Bitmap.Config.RgbaF16! : Bitmap.Config.Argb8888!;
        var named = colorSpace == GlassCaptureColorSpace.DisplayP3
            ? AndroidColorSpace.Named.DisplayP3
            : AndroidColorSpace.Named.Srgb;
        var space = AndroidColorSpace.Get(named!)!;
        return Bitmap.CreateBitmap(width, height, config, true, space)!;
    }

    /// <summary>位图是否 RGBA_F16（API 26 以下必为 false）。</summary>
    /// <param name="bitmap">位图。</param>
    /// <returns>是否半精度。</returns>
    private static bool GlassBitmapIsF16(Bitmap bitmap)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return false;
        return IsRgbaF16Config(bitmap);
    }

    /// <summary>位图自报色域的可读名（API 26 以下如实写「无色彩管理」）。</summary>
    /// <param name="bitmap">位图。</param>
    /// <returns>色域名。</returns>
    private static string GlassBitmapColorSpaceName(Bitmap bitmap)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return "sRGB（API<26 无色彩管理）";
        return BitmapColorSpaceLabel(bitmap);
    }

    /// <summary>读取位图配置并与 RGBA_F16 比较（API 26）。</summary>
    /// <param name="bitmap">位图。</param>
    /// <returns>是否为半精度位图。</returns>
    [SupportedOSPlatform("android26.0")]
    private static bool IsRgbaF16Config(Bitmap bitmap) => bitmap.GetConfig() == Bitmap.Config.RgbaF16;

    /// <summary>回收位图：先释放像素再放 Java 对象，幂等且不抛。</summary>
    /// <param name="bitmap">位图；null 直接返回。</param>
    private static void RecycleGlassBitmap(Bitmap? bitmap)
    {
        if (bitmap == null) return;
        try
        {
            if (!bitmap.IsRecycled) bitmap.Recycle();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[NativeCheck] 回读位图回收失败：{exception.GetType().Name}：{exception.Message}");
        }
        finally
        {
            try { bitmap.Dispose(); }
            catch (Exception) { /* 已释放的包装对象无需再处理 */ }
        }
    }

    /// <summary>PixelCopy 结果码的可读名（数值对应见文件头的本地常量）。</summary>
    /// <param name="result">结果码。</param>
    /// <returns>可读名。</returns>
    private static string DescribeGlassPixelCopyResult(int result) => result switch
    {
        GlassPixelCopySuccess => "Success",
        GlassPixelCopyErrorUnknown => "ErrorUnknown",
        GlassPixelCopyErrorTimeout => "ErrorTimeout",
        GlassPixelCopyErrorSourceInvalid => "ErrorSourceInvalid",
        GlassPixelCopyErrorSourceNoData => "ErrorSourceNoData",
        GlassPixelCopyErrorDestinationInvalid => "ErrorDestinationInvalid",
        _ => "未知结果码",
    };

    /// <summary>
    /// 单次回读的监听器与状态：把平台回调、超时/取消与位图所有权收在同一个对象里，
    /// 保证「先完成的结果生效、位图只被回收一次、迟到的回调不会覆盖已收口的结果」。
    /// </summary>
    private sealed class GlassPixelCopyRequest : Java.Lang.Object, PixelCopy.IOnPixelCopyFinishedListener
    {
        private readonly GlassCaptureRect _sourceRect;
        private readonly GlassCapturePixelFormat _format;
        private readonly GlassCaptureColorSpace _colorSpace;
        private readonly long _started;
        private Bitmap? _bitmap;
        private int _finished;
        private GlassCaptureResult? _result;

        /// <summary>构造一次在途回读。</summary>
        /// <param name="bitmap">目标位图（所有权先归本对象）。</param>
        /// <param name="sourceRect">源矩形。</param>
        /// <param name="format">像素格式。</param>
        /// <param name="colorSpace">色域标签。</param>
        /// <param name="started">提交前的时间戳（<see cref="Stopwatch.GetTimestamp"/>）。</param>
        internal GlassPixelCopyRequest(Bitmap bitmap, GlassCaptureRect sourceRect, GlassCapturePixelFormat format,
            GlassCaptureColorSpace colorSpace, long started)
        {
            _bitmap = bitmap;
            _sourceRect = sourceRect;
            _format = format;
            _colorSpace = colorSpace;
            _started = started;
        }

        /// <summary>JNI 句柄构造：平台把同一实例从 Java 侧回送时使用（与仓库其他 Java 对等类型的写法一致）。</summary>
        /// <param name="javaReference">Java 侧句柄。</param>
        /// <param name="transfer">句柄所有权。</param>
        public GlassPixelCopyRequest(IntPtr javaReference, global::Android.Runtime.JniHandleOwnership transfer)
            : base(javaReference, transfer)
        {
            _sourceRect = GlassCaptureRect.Empty;
            _format = GlassCapturePixelFormat.Argb8888;
            _colorSpace = GlassCaptureColorSpace.Srgb;
        }

        /// <summary>结果完成源（只完成一次）。</summary>
        internal TaskCompletionSource<GlassCaptureResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>已完成的结果；未完成时为 null。</summary>
        internal GlassCaptureResult? Result => _result;

        /// <summary>把位图所有权交出去（成功路径）。</summary>
        /// <returns>位图；已被回收或已交接时为 null。</returns>
        internal Bitmap? TakeBitmap()
        {
            var bitmap = _bitmap;
            _bitmap = null;
            return bitmap;
        }

        /// <summary>
        /// 超时或取消时收口：结果按失败写盘，但**不**回收位图——那次回读可能仍在写入，
        /// 交给迟到的 <see cref="OnPixelCopyFinished"/> 回收。
        /// </summary>
        /// <param name="failure">失败分类。</param>
        /// <param name="message">原因。</param>
        /// <param name="elapsedMs">已等待的墙钟（ms）。</param>
        internal void Abandon(GlassCaptureFailure failure, string message, double elapsedMs)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            _result = GlassCaptureResult.Failed(failure, message, _sourceRect, _format, _colorSpace, elapsedMs);
            Completion.TrySetResult(_result);
        }

        /// <summary>平台回调：成功时转交位图，失败时报真实结果码；重复到达的回调只负责回收。</summary>
        /// <param name="copyResult">PixelCopy 结果码。</param>
        public void OnPixelCopyFinished(int copyResult)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0)
            {
                // 迟到的回调：这次回读早已超时/取消收口，本回调的唯一职责就是回收位图。
                RecycleGlassBitmap(TakeBitmap());
                return;
            }

            // 平台真的回了结果（无论成败）说明回读管线是通的：连续超时计数复位。
            Interlocked.Exchange(ref _glassCaptureConsecutiveTimeouts, 0);
            double elapsedMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            if (copyResult == GlassPixelCopySuccess)
            {
                var bitmap = TakeBitmap();
                if (bitmap == null)
                {
                    _result = GlassCaptureResult.Failed(GlassCaptureFailure.PixelCopyFailed,
                        "PixelCopy 报成功但位图已不可用", _sourceRect, _format, _colorSpace, elapsedMs, copyResult);
                }
                else
                {
                    _result = GlassCaptureResult.Succeeded(bitmap, _sourceRect, _format, _colorSpace, copyResult,
                        elapsedMs);
                }
                Completion.TrySetResult(_result);
                return;
            }

            RecycleGlassBitmap(TakeBitmap());
            _result = GlassCaptureResult.Failed(GlassCaptureFailure.PixelCopyFailed,
                $"PixelCopy 结果 {DescribeGlassPixelCopyResult(copyResult)}（{copyResult}）", _sourceRect, _format,
                _colorSpace, elapsedMs, copyResult);
            Completion.TrySetResult(_result);
        }
    }

    /// <summary>
    /// 回读位图的托管副本：8 位走打包 int（沿用 <see cref="ReadBitmapPacked"/> 的字节序口径），
    /// 半精度保留原始 half 字节供位深证据使用。位图可以在读取后立刻释放。
    /// </summary>
    internal sealed class GlassPixelBuffer : IDisposable
    {
        private int[]? _packed;
        private byte[]? _half;
        private readonly int _rowBytes;

        private GlassPixelBuffer(int width, int height, int[]? packed, byte[]? half, int rowBytes,
            string colorSpaceName)
        {
            Width = width;
            Height = height;
            _packed = packed;
            _half = half;
            _rowBytes = rowBytes;
            ColorSpaceName = colorSpaceName;
        }

        /// <summary>宽（像素）。</summary>
        internal int Width { get; }

        /// <summary>高（像素）。</summary>
        internal int Height { get; }

        /// <summary>是否半精度回读。</summary>
        internal bool IsF16 => _half != null;

        /// <summary>位图自报色域的可读名。</summary>
        internal string ColorSpaceName { get; }

        /// <summary>像素数。</summary>
        internal int PixelCount => checked(Width * Height);

        internal GlassPixelBuffer Crop(Rect rect)
        {
            if (rect.Left < 0 || rect.Top < 0 || rect.Right > Width || rect.Bottom > Height
                || rect.Width() < 1 || rect.Height() < 1)
                throw new ArgumentOutOfRangeException(nameof(rect));
            int width = rect.Width(), height = rect.Height();
            if (_packed != null)
            {
                var cropped = new int[checked(width * height)];
                for (int y = 0; y < height; y++)
                    Array.Copy(_packed, (long)(rect.Top + y) * Width + rect.Left, cropped, (long)y * width, width);
                return new GlassPixelBuffer(width, height, cropped, null, 0, ColorSpaceName);
            }

            int rowBytes = checked(width * 8);
            var half = new byte[checked(rowBytes * height)];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(_half!, (rect.Top + y) * _rowBytes + rect.Left * 8, half, y * rowBytes, rowBytes);
            return new GlassPixelBuffer(width, height, null, half, rowBytes, ColorSpaceName);
        }

        /// <summary>
        /// 把回读位图读成托管副本：ARGB_8888 直接打包，RGBA_F16 保留原始 half 行
        /// （行距取位图真实值，不假设等于 <c>宽 × 8</c>）。
        /// </summary>
        /// <param name="bitmap">回读位图，所有权不转移；null 表示回读失败。</param>
        /// <param name="error">无法读取时的原因。</param>
        /// <returns>像素副本；没有位图或无法解释的格式返回 null。</returns>
        internal static GlassPixelBuffer? TryRead(Bitmap? bitmap, out string error)
        {
            error = string.Empty;
            if (bitmap == null)
            {
                error = "没有位图（回读未成功）";
                return null;
            }
            if (bitmap.IsRecycled)
            {
                error = "位图已回收";
                return null;
            }

            string colorName = GlassBitmapColorSpaceName(bitmap);

            if (GlassBitmapIsF16(bitmap))
            {
                int rowBytes = bitmap.RowBytes;
                byte[] half;
                try
                {
                    half = new byte[checked(rowBytes * bitmap.Height)];
                    using var buffer = JavaByteBuffer.Wrap(half)
                        ?? throw new InvalidOperationException("ByteBuffer.Wrap 返回 null");
                    bitmap.CopyPixelsToBuffer(buffer);
                }
                catch (Exception exception)
                {
                    error = $"{exception.GetType().Name}：{exception.Message}";
                    return null;
                }
                return new GlassPixelBuffer(bitmap.Width, bitmap.Height, null, half, rowBytes, colorName);
            }

            if (bitmap.GetConfig() != Bitmap.Config.Argb8888)
            {
                error = $"回读位图为 {bitmap.GetConfig()?.ToString() ?? "null"}，逐像素比较只接受 ARGB_8888";
                return null;
            }

            int[] packed;
            try
            {
                packed = ReadBitmapPacked(bitmap);
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name}：{exception.Message}";
                return null;
            }
            return new GlassPixelBuffer(bitmap.Width, bitmap.Height, packed, null, 0, colorName);
        }

        /// <summary>
        /// 取一个像素的 8 位 RGB。半精度回读在这里按 <c>round(值 × 255)</c> 量化——对 8 位源是无损往返，
        /// 但窗口缓冲究竟是 8 位还是半精度必须另有证据（见位深取样）；逐字节一致判定只使用 8 位回读。
        /// </summary>
        /// <param name="x">横坐标。</param>
        /// <param name="y">纵坐标。</param>
        /// <param name="r">红。</param>
        /// <param name="g">绿。</param>
        /// <param name="b">蓝。</param>
        /// <returns>坐标在范围内为 true。</returns>
        internal bool TryGetRgb8(int x, int y, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (_packed == null && _half == null) return false;
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            if (_packed != null)
            {
                // 打包字节序与通道口径沿用同一份 <see cref="UnpackRawRgba"/>（内存 R,G,B,A 小端读出）。
                var (packedR, packedG, packedB, _) = UnpackRawRgba(_packed[(long)y * Width + x]);
                r = packedR;
                g = packedG;
                b = packedB;
                return true;
            }

            var half = _half!;
            r = QuantizeHalfChannel(half, _rowBytes, x, y, 0);
            g = QuantizeHalfChannel(half, _rowBytes, x, y, 1);
            b = QuantizeHalfChannel(half, _rowBytes, x, y, 2);
            return true;
        }

        /// <summary>
        /// 取一个通道的归一化原始值（0–1）：半精度回读给出未量化的原始值，8 位回读给出 <c>字节/255</c>。
        /// 位深证据（平坦色块内的取值个数、斜坡的可分辨级数）靠这个接口，不受 8 位量化影响。
        /// </summary>
        /// <param name="x">横坐标。</param>
        /// <param name="y">纵坐标。</param>
        /// <param name="channel">通道序号：0 红 / 1 绿 / 2 蓝 / 3 alpha。</param>
        /// <param name="value">归一化值。</param>
        /// <returns>坐标与通道合法为 true。</returns>
        internal bool TryGetChannel01(int x, int y, int channel, out double value)
        {
            value = 0;
            if (_packed == null && _half == null) return false;
            if (x < 0 || y < 0 || x >= Width || y >= Height || channel is < 0 or > 3) return false;
            if (_packed != null)
            {
                var (packedR, packedG, packedB, packedA) = UnpackRawRgba(_packed[(long)y * Width + x]);
                int channelValue = channel switch
                {
                    0 => packedR,
                    1 => packedG,
                    2 => packedB,
                    _ => packedA,
                };
                value = channelValue / 255.0;
                return true;
            }

            int at = y * _rowBytes + x * 8 + channel * 2;
            value = (double)BitConverter.ToHalf(_half!, at);
            return true;
        }

        /// <summary>
        /// 统计矩形内某通道的不同取值个数：8 位回读是字节取值数，半精度回读是 half 位型数。
        /// </summary>
        /// <remarks>
        /// **这个计数没有区分位深的能力**：<see cref="PixelCopy"/> 会按目标位图的格式与色域重写源，
        /// 8 位窗口读进 F16 目标只是把同一取值无损放大（个数仍是 1），F16 窗口读进 8 位目标也可能
        /// 因抖动出现多个值。它只能说明「这块平坦区在本次回读里是否稳定」，属辅助读数；
        /// 窗口缓冲的真实格式以 <c>dumpsys SurfaceFlinger</c> 为准，不得据此下 F16 或 8 位的结论。
        /// </remarks>
        /// <param name="rect">取样矩形（本缓冲坐标）。</param>
        /// <param name="channel">通道序号：0–3。</param>
        /// <returns>不同取值个数；矩形为空为 0。</returns>
        internal int CountDistinctChannelValues(Rect rect, int channel)
        {
            int left = Math.Max(0, rect.Left), top = Math.Max(0, rect.Top);
            int right = Math.Min(Width, rect.Right), bottom = Math.Min(Height, rect.Bottom);
            if (_packed == null && _half == null) return 0;
            if (right <= left || bottom <= top || channel is < 0 or > 3) return 0;
            var seen = new HashSet<int>();
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    if (_packed != null)
                    {
                        var (packedR, packedG, packedB, packedA) = UnpackRawRgba(_packed[(long)y * Width + x]);
                        seen.Add(channel switch
                        {
                            0 => packedR,
                            1 => packedG,
                            2 => packedB,
                            _ => packedA,
                        });
                    }
                    else
                    {
                        var half = _half!;
                        int at = y * _rowBytes + x * 8 + channel * 2;
                        seen.Add(half[at] | (half[at + 1] << 8));
                    }
                }
            }
            return seen.Count;
        }

        /// <summary>释放托管副本（位图早已由调用方处理）。</summary>
        public void Dispose()
        {
            _packed = null;
            _half = null;
        }

        /// <summary>把 half 通道量化成 8 位。</summary>
        /// <param name="half">原始 half 行。</param>
        /// <param name="rowBytes">行距。</param>
        /// <param name="x">横坐标。</param>
        /// <param name="y">纵坐标。</param>
        /// <param name="channel">通道序号。</param>
        /// <returns>8 位值。</returns>
        private static byte QuantizeHalfChannel(byte[] half, int rowBytes, int x, int y, int channel)
        {
            double value = (double)BitConverter.ToHalf(half, y * rowBytes + x * 8 + channel * 2);
            return (byte)Math.Clamp((int)Math.Round(value * 255.0), 0, 255);
        }
    }

    /// <summary>
    /// 物理 100% 的逐字节比较结果。<see cref="ByteExact"/> 是唯一可用于 PASS 的判定
    /// （三通道全等，alpha 不参与）；<see cref="WithinOne"/>、<see cref="MaxChannelDelta"/>
    /// 与偏移诊断只用于定位问题，不得单独放宽成 PASS。
    /// </summary>
    internal readonly record struct GlassPixelComparison(
        int TotalPixels,
        int MismatchedPixels,
        int MaxChannelDelta,
        int AboveOnePixels,
        int Delta0Pixels,
        int Delta1Pixels,
        int Delta2Pixels,
        int Delta3PlusPixels,
        int WorstX,
        int WorstY,
        (byte R, byte G, byte B) WorstExpected,
        (byte R, byte G, byte B) WorstActual,
        int OffsetSearchRadius,
        int BestOffsetX,
        int BestOffsetY,
        int BestOffsetMismatchedPixels)
    {
        /// <summary>三通道逐字节全等（PASS 判定口径）。</summary>
        internal bool ByteExact => MismatchedPixels == 0;

        /// <summary>全部像素的通道差都不超过 1（仅诊断）。</summary>
        internal bool WithinOne => AboveOnePixels == 0;

        /// <summary>不匹配像素占比。</summary>
        internal double MismatchRatio => TotalPixels > 0 ? (double)MismatchedPixels / TotalPixels : 1;

        /// <summary>几何偏移是否为零（零偏移才谈得上逐像素对齐）。</summary>
        internal bool GeometryAligned => BestOffsetX == 0 && BestOffsetY == 0;

        /// <summary>报告用的一行描述：逐字节判定 + 全部分布诊断。</summary>
        /// <returns>可读描述。</returns>
        internal string Describe()
        {
            string worst = WorstX < 0
                ? "无差异像素"
                : $"最差 ({WorstX},{WorstY}) 期望({WorstExpected.R},{WorstExpected.G},{WorstExpected.B})"
                    + $" 实际({WorstActual.R},{WorstActual.G},{WorstActual.B})";
            return $"逐字节{(ByteExact ? "一致" : "不一致")} 不匹配 {MismatchedPixels}/{TotalPixels}"
                + $"（{MismatchRatio:P3}）最大差 {MaxChannelDelta}"
                + $" 超1像素 {AboveOnePixels} Δ0/1/2/3+={Delta0Pixels}/{Delta1Pixels}/{Delta2Pixels}/{Delta3PlusPixels}"
                + $" {worst} 偏移搜索±{OffsetSearchRadius} 最优({BestOffsetX},{BestOffsetY})"
                + $" 最优不匹配 {BestOffsetMismatchedPixels}";
        }
    }

    /// <summary>
    /// 物理 100% 下的整窗逐字节比较：参考是调用方按**实际窗口色域**准备的 RGBA8/BGRA32 产物
    /// （通常是 <c>ColorEngine.ToDisplayEncoding</c> 的结果），实际是整窗回读的托管副本（F16 通道量化到 8 位后比较）。
    /// </summary>
    /// <remarks>
    /// 调用方必须先断言「物理倍率 = 1、纹理宽 = 源宽、回读尺寸 = 视图物理尺寸」，本方法不替它猜缩放；
    /// 参考的起点给的是 <paramref name="originX"/>/<paramref name="originY"/>，即在源图上与回读左上角对应的像素。
    /// </remarks>
    /// <param name="reference">参考图像（RGBA8 或 BGRA32，按实际窗口色域）。</param>
    /// <param name="originX">回读左上角在参考里的横坐标。</param>
    /// <param name="originY">回读左上角在参考里的纵坐标。</param>
    /// <param name="actual">整窗回读的托管副本。</param>
    /// <param name="offsetSearchRadius">几何偏移诊断的搜索半径（像素）。</param>
    /// <returns>比较结果。</returns>
    internal static GlassPixelComparison CompareGlassPhysicalScale(DecodedImage reference, int originX, int originY,
        GlassPixelBuffer actual, int offsetSearchRadius = GlassOffsetSearchRadius)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual);
        if (reference.Encoding is not (PixelEncoding.Rgba8 or PixelEncoding.Bgra32))
            throw new NotSupportedException($"逐字节比较只读 8 位 RGBA/BGRA 参考，实际 {reference.Encoding}");
        if (originX < 0 || originY < 0 || originX + actual.Width > reference.Width
            || originY + actual.Height > reference.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(originX),
                $"回读窗口 {actual.Width}x{actual.Height} 超出参考 {reference.Width}x{reference.Height}（起点 {originX},{originY}）");
        }

        var zero = CompareGlassPixelsAt(reference, originX, originY, actual);
        var (bestX, bestY, bestMismatched) = SearchGlassOffset(reference, originX, originY, actual,
            Math.Max(0, offsetSearchRadius));
        if (bestX != 0 || bestY != 0)
        {
            // 稀疏搜索只用来找偏移，报出的最优值用全量重算，避免把采样误差当成结论。
            bestMismatched = CompareGlassPixelsAt(reference, originX + bestX, originY + bestY, actual).Mismatched;
        }
        else
        {
            bestMismatched = zero.Mismatched;
        }

        return new GlassPixelComparison(zero.Total, zero.Mismatched, zero.MaxDelta, zero.AboveOne, zero.Delta0,
            zero.Delta1, zero.Delta2, zero.Delta3Plus, zero.WorstX, zero.WorstY, zero.WorstExpected,
            zero.WorstActual, Math.Max(0, offsetSearchRadius), bestX, bestY, bestMismatched);
    }

    /// <summary>一次全量比较的原始计数。</summary>
    private readonly record struct GlassFullComparison(int Total, int Mismatched, int MaxDelta, int AboveOne,
        int Delta0, int Delta1, int Delta2, int Delta3Plus, int WorstX, int WorstY,
        (byte R, byte G, byte B) WorstExpected, (byte R, byte G, byte B) WorstActual);

    /// <summary>在给定起点上逐像素全量比较（三通道，alpha 不参与）。</summary>
    /// <param name="reference">参考图像。</param>
    /// <param name="originX">起点横坐标。</param>
    /// <param name="originY">起点纵坐标。</param>
    /// <param name="actual">回读副本。</param>
    /// <returns>原始计数。</returns>
    private static GlassFullComparison CompareGlassPixelsAt(DecodedImage reference, int originX, int originY,
        GlassPixelBuffer actual)
    {
        int mismatched = 0, maxDelta = 0, aboveOne = 0, delta0 = 0, delta1 = 0, delta2 = 0, delta3Plus = 0;
        int worstX = -1, worstY = -1;
        (byte R, byte G, byte B) worstExpected = default, worstActual = default;
        for (int y = 0; y < actual.Height; y++)
        {
            for (int x = 0; x < actual.Width; x++)
            {
                GlassReferencePixel(reference, originX + x, originY + y, out byte er, out byte eg, out byte eb);
                if (!actual.TryGetRgb8(x, y, out byte ar, out byte ag, out byte ab))
                    throw new InvalidOperationException($"回读副本取像素失败：({x},{y})");
                int delta = Math.Max(Math.Abs(ar - er), Math.Max(Math.Abs(ag - eg), Math.Abs(ab - eb)));
                switch (delta)
                {
                    case 0: delta0++; break;
                    case 1: delta1++; break;
                    case 2: delta2++; break;
                    default: delta3Plus++; break;
                }
                if (delta == 0) continue;
                mismatched++;
                if (delta <= 1) continue;
                aboveOne++;
                if (delta <= maxDelta) continue;
                maxDelta = delta;
                worstX = x;
                worstY = y;
                worstExpected = (er, eg, eb);
                worstActual = (ar, ag, ab);
            }
        }
        return new GlassFullComparison(checked(actual.Width * actual.Height), mismatched, maxDelta, aboveOne,
            delta0, delta1, delta2, delta3Plus, worstX, worstY, worstExpected, worstActual);
    }

    /// <summary>
    /// 稀疏几何偏移搜索：只用于诊断「整窗回读是否整体平移」。同分时优先更小的位移，再优先零位移，
    /// 因此零偏移永远是最保守的候选；返回值 -1 表示所有候选都越界（正常不会发生）。
    /// </summary>
    /// <param name="reference">参考图像。</param>
    /// <param name="originX">零偏移起点横坐标。</param>
    /// <param name="originY">零偏移起点纵坐标。</param>
    /// <param name="actual">回读副本。</param>
    /// <param name="radius">搜索半径（像素）。</param>
    /// <returns>最优偏移与稀疏采样下的不匹配数。</returns>
    private static (int X, int Y, int Mismatched) SearchGlassOffset(DecodedImage reference, int originX, int originY,
        GlassPixelBuffer actual, int radius)
    {
        int bestX = 0, bestY = 0, bestMismatched = int.MaxValue;
        int step = Math.Max(1, Math.Min(actual.Width, actual.Height) / 48);
        for (int dy = -radius; dy <= radius; dy++)
        {
            if (originY + dy < 0 || originY + dy + actual.Height > reference.Height) continue;
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (originX + dx < 0 || originX + dx + actual.Width > reference.Width) continue;
                int mismatched = 0;
                for (int y = 0; y < actual.Height; y += step)
                {
                    for (int x = 0; x < actual.Width; x += step)
                    {
                        GlassReferencePixel(reference, originX + dx + x, originY + dy + y,
                            out byte er, out byte eg, out byte eb);
                        if (!actual.TryGetRgb8(x, y, out byte ar, out byte ag, out byte ab)) continue;
                        if (er != ar || eg != ag || eb != ab) mismatched++;
                    }
                }
                int distance = Math.Abs(dx) + Math.Abs(dy);
                int bestDistance = Math.Abs(bestX) + Math.Abs(bestY);
                if (mismatched < bestMismatched || (mismatched == bestMismatched && distance < bestDistance))
                {
                    bestMismatched = mismatched;
                    bestX = dx;
                    bestY = dy;
                }
            }
        }
        return bestMismatched == int.MaxValue ? (0, 0, -1) : (bestX, bestY, bestMismatched);
    }

    /// <summary>取参考图像的一个像素（RGBA8 / BGRA32，通道口径与 <c>ColorPixelAt</c> 一致）。</summary>
    /// <param name="reference">参考图像。</param>
    /// <param name="x">横坐标。</param>
    /// <param name="y">纵坐标。</param>
    /// <param name="r">红。</param>
    /// <param name="g">绿。</param>
    /// <param name="b">蓝。</param>
    private static void GlassReferencePixel(DecodedImage reference, int x, int y,
        out byte r, out byte g, out byte b)
    {
        long at = (long)y * reference.Stride + (long)x * DecodedImage.BytesPerPixel(reference.Encoding);
        var pixels = reference.Pixels;
        if (reference.Encoding == PixelEncoding.Rgba8)
        {
            r = pixels[at];
            g = pixels[at + 1];
            b = pixels[at + 2];
            return;
        }
        b = pixels[at];
        g = pixels[at + 1];
        r = pixels[at + 2];
    }
}
