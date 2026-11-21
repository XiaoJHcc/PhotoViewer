using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Android.Graphics;
using Android.OS;
using Android.Opengl;
using Android.Views;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Image;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check glass</c> 的液态玻璃验收调度（独立模式，不并入 <c>all</c>）：
/// <c>--es pv-glass-stage host|product|sample|layers|all</c>，缺省 <c>all</c> = <c>host</c> + <c>product</c>。
/// <list type="bullet">
/// <item><c>host</c>：用当前真实的 <see cref="TextureView"/> 主图核对能力与 EGL 缓冲尺寸、整窗 <see cref="PixelCopy"/>
/// 色块与 <c>color</c> 期望、物理 100% 适应区逐字节比较（<see cref="GlassPixelComparison.ByteExact"/> 才判 PASS）、
/// 输入响应的两段延迟（GL swap 交还 / 界面层取用）与 250ms 心跳，并把窗口位图格式与色域的原始读数写进报告。
/// 另可用 <c>--es pv-glass-host-step capability|quadrants|physical|input|hide|all</c>（缺省 <c>all</c>）
/// 只跑所选宿主步骤，用于定位污染产品材质的来源；<c>hide</c> 只做「隐藏全部根子视图→恢复」的最小动作，
/// 缺省 <c>all</c> 仍只跑原来那四步。缺省行为不变。</item>
/// <item><c>product</c>：真实产品材质验收（SHELL-12@android）——根视图现有照片层与共享材质、6 个真实栏底与
/// 带衬底 EXIF 的结构/配方/分级断言，加上真实可见栏底的像素证明（照片取样、低通、边缘展开）与切图/缩放跟随。</item>
/// <item><c>sample</c> / <c>layers</c>：阶段 0 的迁移层机制检查（legacy），只在显式点名时运行，
/// 不再作为产品验收证据；它们挂临时舞台、另建原型主图，与产品材质无关。</item>
/// </list>
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>宿主组「输入响应」的取样次数（每种指标各一份）。</summary>
    private const int GlassHostLatencySamples = 10;

    /// <summary>等待「输入响应」任一段落地（swap 交还 / 界面层取用）的墙钟上限（ms）。</summary>
    private const int GlassHostFrameWaitMs = 2000;

    /// <summary>物理 100% 素材在视口各轴额外铺出的像素（保证 100% 下照片铺满适应区并两侧各留余量）。</summary>
    private const int GlassHostPatternMargin = 128;

    /// <summary>
    /// 读取 <c>pv-glass-stage</c> 并按分组跑验收；非法参数如实记一条 FAIL 后不再继续。
    /// 缺省 <c>all</c> 只覆盖宿主与产品组，legacy 机制检查必须显式点名。
    /// </summary>
    private static async Task RunGlassChecksAsync()
    {
        string stage = (_activity!.Intent?.GetStringExtra("pv-glass-stage") ?? "all").Trim().ToLowerInvariant();
        bool host = stage is "host" or "all";
        bool product = stage is "product" or "all";
        bool sample = stage is "sample";
        bool layers = stage is "layers";
        if (!host && !product && !sample && !layers)
        {
            Check("glass 阶段参数有效", false, $"pv-glass-stage={stage}，只接受 host|product|sample|layers|all");
            return;
        }

        await WaitForScreenAsync();
        await EnsureGesturePhotoAsync();
        Phase($"glass 阶段={stage}");
        GlassHostNote("阶段口径：all = host + product（产品验收证据）；sample/layers 是阶段 0 迁移层机制检查（legacy），"
            + "只在显式点名时运行，不作为产品材质验收证据");
        if (host) await RunGlassHostChecksAsync();
        if (product) await RunStepAsync("产品材质", RunGlassProductChecksAsync);
        if (sample) await RunGlassSampleChecksAsync();
        if (layers) await RunGlassLayersChecksAsync();
    }

    /// <summary>
    /// 宿主组：能力/缓冲读数 → 整窗色块 → 物理 100% 逐字节 → 输入响应（swap / 界面层取用）→ 250ms 心跳。
    /// 心跳循环覆盖整组，收尾无论成败都还原会话图与栏位可见性。
    /// <c>--es pv-glass-host-step capability|quadrants|physical|input|hide|all</c>（缺省 <c>all</c>）只跑所选步骤，
    /// 用于定位哪一步会污染随后的产品材质；<c>hide</c> 只做「隐藏全部根子视图→恢复」这一最小动作，
    /// 缺省 <c>all</c> 仍只跑原来那四步，不把 <c>hide</c> 额外加入。
    /// </summary>
    private static async Task RunGlassHostChecksAsync()
    {
        string hostStep = (_activity!.Intent?.GetStringExtra("pv-glass-host-step") ?? "all").Trim().ToLowerInvariant();
        bool stepCapability = hostStep is "capability" or "all";
        bool stepQuadrants = hostStep is "quadrants" or "all";
        bool stepPhysical = hostStep is "physical" or "all";
        bool stepInput = hostStep is "input" or "all";
        bool stepHide = hostStep is "hide";
        if (!stepCapability && !stepQuadrants && !stepPhysical && !stepInput && !stepHide)
        {
            Check("glass host-step 参数有效", false,
                $"pv-glass-host-step={hostStep}，只接受 capability|quadrants|physical|input|hide|all");
            return;
        }
        GlassHostNote($"glass host-step={hostStep}（缺省 all；只影响宿主组内部步骤选择，不改缺省行为）");

        var photo = _root!.Photo;
        var session = _session!;
        var rate = ReadGlassRefreshRate(_activity!);
        var heartbeat = new GlassFrameIntervalRecorder(rate);
        var swapLatency = new GlassFrameIntervalRecorder(rate);
        var presentLatency = new GlassFrameIntervalRecorder(rate);
        // 心跳间隔按一帧时长向上取整：固定 16ms 在 120Hz（8.33ms 一帧）上每个 tick 跨两帧，
        // 会被掉帧口径误算成 50% 掉帧。
        int tickMs = rate.HeartbeatTickMs;
        var handler = new Handler(Looper.MainLooper!);
        bool ticking = true;
        Action tick = null!;
        tick = () =>
        {
            if (!ticking) return;
            heartbeat.Mark();
            handler.PostDelayed(tick, tickMs);
        };
        heartbeat.Restart();
        handler.PostDelayed(tick, tickMs);
        GlassHostNote($"刷新率 {rate.Describe()} 心跳间隔={tickMs}ms（按一帧时长向上取整）");
        try
        {
            if (stepCapability) await RunStepAsync("宿主能力", () => CheckGlassHostCapabilityAsync(rate));
            if (stepQuadrants) await RunStepAsync("整窗色块", CheckGlassWindowQuadrantsAsync);
            if (stepPhysical) await RunStepAsync("物理100%逐像素", CheckGlassPhysicalPixelsAsync);
            if (stepInput)
                await RunStepAsync("输入响应", () => CheckGlassInputToCommitAsync(swapLatency, presentLatency, rate));
            if (stepHide) await RunStepAsync("全隐藏恢复", CheckGlassHideRestoreAsync);
            var beat = heartbeat.Compute();
            Check("glass 心跳无 250ms 阻塞", beat.HasSamples && beat.MaxMs < 250, beat.Describe());
        }
        finally
        {
            ticking = false;
            await RestoreColorSessionImageAsync(session.Image, session.Generation);
            photo.FitToView();
        }
    }

    /// <summary>报告里记录一条证据/读数（不计 PASS/FAIL）。</summary>
    /// <param name="text">内容。</param>
    private static void GlassHostNote(string text) => Lines.Add("NOTE " + text);

    /// <summary>
    /// 宿主能力：表面就绪、EGL 缓冲尺寸等于视图物理像素、整窗回读可用性，并把表面/窗口的色域与
    /// EGL 配置位宽写成原始读数（位深结论仍需 SurfaceFlinger 实测，这里不下结论）。
    /// </summary>
    /// <param name="rate">本轮刷新率读数。</param>
    private static async Task CheckGlassHostCapabilityAsync(GlassRefreshRate rate)
    {
        var photo = _root!.Photo;
        await WaitAsync(() => photo.SurfaceReadyForChecks, "主图 EGL 表面就绪", 20000);
        var extent = photo.SurfaceExtentForChecks;
        Check("宿主 EGL 表面已建立", extent.Width > 0 && extent.Height > 0,
            $"缓冲={extent.Width}x{extent.Height} 视图={photo.Width}x{photo.Height}");
        Check("宿主 EGL 缓冲=视图物理像素", extent.Width == photo.Width && extent.Height == photo.Height,
            $"缓冲={extent.Width}x{extent.Height} 视图={photo.Width}x{photo.Height} density={photo.Density:F2}");
        // 版本门槛在 API 26 以上恒真，不拿它刷 PASS：只记读数，真实回读能力由「整窗色块」步骤实测。
        GlassHostNote($"API={(int)Build.VERSION.SdkInt}；整窗 PixelCopy 需 API 26，实际回读见「整窗色块」");
        Check("宿主 刷新率读数可用", rate.IsUsable, rate.Describe());

        var display = _activity!.Window?.DecorView?.Display;
        bool windowWide = OperatingSystem.IsAndroidVersionAtLeast(27) && _activity.Window?.IsWideColorGamut == true;
        bool displayWide = OperatingSystem.IsAndroidVersionAtLeast(26) && display?.IsWideColorGamut == true;
        GlassHostNote($"表面 P3={photo.SurfaceIsP3} 表面色域={ColorspaceName(photo)}"
            + $" 帧缓冲编码={FramebufferName(photo)}"
            + $" 窗口广色域={windowWide}"
            + $" 显示广色域={displayWide}"
            + $" 设备 P3 能力={AndroidDisplayCapabilities.DisplaySupportsP3}");
        GlassHostNote($"EGL 配置位宽 {DescribeGlassEglConfig(photo)}（请求 8/8/8/0；位深结论需 SurfaceFlinger 实测）");
    }

    /// <summary>读当前 EGL 配置的 RGBA 位宽（验收用反射取 display/config，失败如实写读数不可用）。</summary>
    /// <param name="photo">主图视图。</param>
    /// <returns>读数描述。</returns>
    private static string DescribeGlassEglConfig(AndroidPhotoView photo)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var renderer = PhotoRendererForChecks(photo);
            var display = typeof(AndroidPhotoRenderer).GetField("_display", flags)?.GetValue(renderer) as EGLDisplay;
            var config = typeof(AndroidPhotoRenderer).GetField("_config", flags)?.GetValue(renderer) as EGLConfig;
            if (display == null || config == null) return "不可用（display/config 未就绪）";

            var value = new int[1];
            string Read(int attribute) => EGL14.EglGetConfigAttrib(display, config, attribute, value, 0)
                ? value[0].ToString()
                : "?";
            return $"RGBA={Read(EGL14.EglRedSize)}/{Read(EGL14.EglGreenSize)}/"
                + $"{Read(EGL14.EglBlueSize)}/{Read(EGL14.EglAlphaSize)}"
                + $" 深度={Read(EGL14.EglDepthSize)} 模板={Read(EGL14.EglStencilSize)}";
        }
        catch (Exception exception)
        {
            return $"不可用（{exception.GetType().Name}：{exception.Message}）";
        }
    }

    /// <summary>
    /// 整窗色块：把 2×2 色块图挂到真实主图（适应态），整窗回读后在四个象限中心对照
    /// <see cref="ColorEngine"/> 期望值（与 <c>color</c> 模式同一套期望与容差口径），
    /// 另用 F16 目标补一次回读，输出平坦象限内的通道取值个数作为位深原始证据。
    /// </summary>
    private static async Task CheckGlassWindowQuadrantsAsync()
    {
        var photo = _root!.Photo;
        var target = SurfaceImageColorSpace();
        var colorSpace = GlassWindowColorSpace(target);
        using var quad = CreateColorQuadImage(target) ?? throw new InvalidOperationException("合成色块图创建失败");
        await ShowColorSyntheticAsync(quad);
        await Task.Delay(200);

        var expected = ExpectedColorQuadrants(quad, target);
        // 色块回读的位图与像素副本只在这一段里活着：位深证据要再占一份 F16 整窗，两者不叠在一起。
        {
        using var capture = await CaptureGlassWindowAsync(_activity!, GlassCapturePixelFormat.Argb8888, colorSpace);
        Check("整窗回读 取得位图", capture.Ok,
            capture.Ok
                ? $"{capture.Bitmap!.Width}x{capture.Bitmap.Height}"
                    + $" 格式={capture.Bitmap.GetConfig()?.ToString() ?? "?"} 色域={capture.BitmapColorSpaceName}"
                    + $" 源={capture.SourceRect.Describe()} 耗时={capture.ElapsedMs:F1}ms"
                : $"{capture.Failure}：{capture.Message}");
        if (!capture.Ok) return;

        using var pixels = GlassPixelBuffer.TryRead(capture.Bitmap!, out string error);
        if (pixels == null)
        {
            Check("整窗回读 像素可读", false, error);
            return;
        }

        var actual = new (byte R, byte G, byte B, byte A)[4];
        var detail = new List<string>(4);
        bool sampled = true;
        for (int index = 0; index < actual.Length; index++)
        {
            var (u, v) = QuadrantCenter(index);
            var (x, y) = GlassWindowPoint(photo, u, v);
            if (!pixels.TryGetRgb8(x, y, out byte r, out byte g, out byte b))
            {
                detail.Add($"Q{index} 越界({x},{y})");
                sampled = false;
                continue;
            }
            actual[index] = (r, g, b, 255);
            detail.Add($"Q{index} Δ{ColorDelta(actual[index], expected[index])} {DescribeColor(actual[index])}");
        }
        Check("整窗色块 与 color 期望一致", sampled && QuadrantsWithin(actual, expected, ConvertTolerance),
            $"容差={ConvertTolerance} {string.Join(" ", detail)}");
        }

        await DescribeGlassWindowDepthAsync(photo, colorSpace);
    }

    /// <summary>
    /// 窗口位深的原始证据：F16 目标回读，统计两个平坦象限内各通道的不同取值个数。
    /// 8 位缓冲应只有一个取值，F16 才可能更多；结论一律交 SurfaceFlinger 实测，本检查不判 PASS。
    /// </summary>
    /// <param name="photo">主图视图。</param>
    /// <param name="colorSpace">回读目标色域。</param>
    private static async Task DescribeGlassWindowDepthAsync(AndroidPhotoView photo,
        GlassCaptureColorSpace colorSpace)
    {
        using var capture = await CaptureGlassWindowAsync(_activity!, GlassCapturePixelFormat.RgbaF16, colorSpace);
        if (!capture.Ok)
        {
            GlassHostNote($"窗口 F16 回读未取到：{capture.Failure}：{capture.Message}");
            return;
        }
        using var pixels = GlassPixelBuffer.TryRead(capture.Bitmap!, out string error);
        if (pixels == null)
        {
            GlassHostNote($"窗口 F16 回读不可解析：{error}");
            return;
        }

        var (u, v) = GlassWindowPoint(photo, 0.25, 0.25);
        var patch = new Rect(u - 8, v - 8, u + 8, v + 8);
        var (w, z) = GlassWindowPoint(photo, 0.75, 0.75);
        var patchB = new Rect(w - 8, z - 8, w + 8, z + 8);
        string Counts(Rect rect) => $"{pixels.CountDistinctChannelValues(rect, 0)}/"
            + $"{pixels.CountDistinctChannelValues(rect, 1)}/{pixels.CountDistinctChannelValues(rect, 2)}";
        GlassHostNote($"窗口 F16 回读：色域={pixels.ColorSpaceName} 格式={capture.Bitmap!.GetConfig()?.ToString() ?? "?"}"
            + $" 平坦块取值个数 R/G/B=左上{Counts(patch)} 右下{Counts(patchB)}"
            + "（辅助读数，对位深没有区分力：PixelCopy 会按目标位图格式与色域重写源；"
            + "窗口缓冲的真实格式以 dumpsys SurfaceFlinger 为准，本检查不下 F16/8 位结论）");
    }

    /// <summary>
    /// 物理 100% 逐字节比较：用逐像素各异的合成图（铺满适应区），把视口对齐到整数像素后回读适应区，
    /// 与上传的源字节逐像素对照。<see cref="GlassPixelComparison.ByteExact"/> 是唯一 PASS 依据，
    /// ±1/最大差/偏移只作诊断。比较期间把栏位临时设为不可见，仅为隔离照片层，收尾立刻还原。
    /// </summary>
    private static async Task CheckGlassPhysicalPixelsAsync()
    {
        var photo = _root!.Photo;
        int width = photo.Width, height = photo.Height;
        if (width < 64 || height < 64) throw new InvalidOperationException($"主图视图尺寸异常 {width}x{height}");
        var hidden = new List<(View View, ViewStates Visibility)>();
        int hiddenCount = HideGlassOverlays(hidden);
        try
        {
            await Task.Delay(80);
            int patternWidth = width + GlassHostPatternMargin;
            int patternHeight = height + GlassHostPatternMargin;
            using var pattern = await Task.Run(() =>
                CreateZoomPattern(patternWidth, patternHeight, patternWidth, patternHeight, 3));
            await ShowColorSyntheticAsync(pattern);
            photo.Viewport.ZoomToPhysical(1);
            photo.Viewport.CenterOnNormalizedPoint(0.5, 0.5);
            // 适应区比窗口小（栏位让位）时照片在 100% 下仍只铺满适应区：比较区因此取适应区，
            // 栏位占用的区域按产品口径本来就不显示照片，不参与逐像素判定。
            AlignGlassImageOrigin(photo);
            // 设置通知可能在等待期间把栏位重新显示出来：绘制前再压一次（清单合并，收尾统一还原），
            // 随后的等待同时覆盖「隐藏生效」与「照片帧进入窗口缓冲」。
            hiddenCount += HideGlassOverlays(hidden);
            photo.Draw();
            await Task.Delay(160);

            var viewport = photo.Viewport;
            double scale = photo.Density;
            var insets = viewport.FitInsets;
            var location = new int[2];
            photo.GetLocationInWindow(location);
            var (leftDp, topDp) = viewport.ViewPoint(0, 0);
            double leftPx = leftDp * scale, topPx = topDp * scale;
            int compareLeft = location[0] + (int)Math.Round(insets.Left * scale);
            int compareTop = location[1] + (int)Math.Round(insets.Top * scale);
            int compareWidth = photo.Width - (int)Math.Round((insets.Left + insets.Right) * scale);
            int compareHeight = photo.Height - (int)Math.Round((insets.Top + insets.Bottom) * scale);
            int originX = (int)Math.Round(compareLeft - (location[0] + leftPx));
            int originY = (int)Math.Round(compareTop - (location[1] + topPx));

            Check("物理100% 倍率=1", Math.Abs(photo.PhysicalScale - 1) < 1e-3,
                $"physical={photo.PhysicalScale:0.#####} fit={photo.IsFit}");
            Check("物理100% 纹理=源宽", viewport.TextureWidth == pattern.Width,
                $"纹理={viewport.TextureWidth} 源={pattern.Width}x{pattern.Height}"
                    + $" 方向={pattern.Orientation} 隔离栏位={hiddenCount}");
            bool inside = compareWidth >= 64 && compareHeight >= 64 && originX >= 0 && originY >= 0
                && originX + compareWidth <= pattern.Width && originY + compareHeight <= pattern.Height;
            Check("物理100% 比较区落在源内", inside,
                $"适应区={insets} 比较区={compareWidth}x{compareHeight}@({compareLeft},{compareTop})"
                    + $" 源内起点=({originX},{originY}) 源={pattern.Width}x{pattern.Height}"
                    + $" 亚像素余量=({leftPx - Math.Round(leftPx):0.####},{topPx - Math.Round(topPx):0.####})");
            if (!inside)
            {
                Check("物理100% 逐字节一致", false, "比较区越出源范围，未比较");
                return;
            }

            var requested = new Rect(compareLeft, compareTop, compareLeft + compareWidth, compareTop + compareHeight);
            var uploaded = PhotoRendererForChecks(photo).CheckTexturePixelsForChecks(pattern);
            Check("物理100% 上传纹理与源一致（边界抽样）", uploaded.Matches, uploaded.Details);

            // 同帧对照：先在默认帧缓冲上按同一视口整块回读（GL→源），提交上屏后立刻发起整窗回读，
            // 两次回读看的是同一帧内容；逐像素比较在后台跑，不插在两次回读之间，也不逐像素 swap。
            byte[]? glBytes = null;
            string glFailure = string.Empty;
            try
            {
                glBytes = photo.ReadRegionForChecks(viewport, scale,
                    compareLeft - location[0], compareTop - location[1], compareWidth, compareHeight);
            }
            catch (Exception exception)
            {
                glFailure = $"{exception.GetType().Name}：{exception.Message}";
            }

            var captureTask = CaptureGlassWindowAsync(_activity!, GlassCapturePixelFormat.Argb8888,
                GlassWindowColorSpace(SurfaceImageColorSpace()), requested);

            bool glByteExact = false;
            if (glBytes == null)
            {
                Check("物理100% GL 帧缓冲逐字节一致", false, $"默认帧缓冲整块回读失败：{glFailure}");
            }
            else
            {
                using var glPixels = GlassPixelBuffer.FromRgba8(glBytes, compareWidth, compareHeight);
                var glComparison = await Task.Run(() =>
                    CompareGlassPhysicalScale(pattern, originX, originY, glPixels, GlassOffsetSearchRadius));
                glByteExact = glComparison.ByteExact;
                Check("物理100% GL 帧缓冲逐字节一致", glComparison.ByteExact, glComparison.Describe());
            }

            using var capture = await captureTask;
            // 回读矩形被裁过（窗口尺寸/insets 与预期不符）时，下面的源内起点就不可信：先比矩形再比像素。
            bool rectMatches = capture.SourceRect.Left == requested.Left
                && capture.SourceRect.Top == requested.Top
                && capture.SourceRect.Width == requested.Width()
                && capture.SourceRect.Height == requested.Height();
            Check("物理100% 回读矩形=请求矩形", capture.Ok && rectMatches,
                $"请求={requested}（{requested.Width()}x{requested.Height()}）实际={capture.SourceRect.Describe()}"
                    + (capture.Ok ? string.Empty : $" 回读失败={capture.Failure}：{capture.Message}"));
            if (!capture.Ok || !rectMatches)
            {
                Check("物理100% 逐字节一致", false, "回读矩形与请求不符，已停止比较");
                return;
            }
            using var pixels = GlassPixelBuffer.TryRead(capture.Bitmap!, out string error);
            if (pixels == null)
            {
                Check("物理100% 逐字节一致", false, $"回读不可解析：{error}");
                return;
            }
            bool sizeMatches = pixels.Width == compareWidth && pixels.Height == compareHeight;
            Check("物理100% 回读尺寸=比较区", sizeMatches,
                $"回读={pixels.Width}x{pixels.Height} 比较区={compareWidth}x{compareHeight}");
            if (!sizeMatches)
            {
                Check("物理100% 逐字节一致", false, "回读尺寸与比较区不符，已停止比较");
                return;
            }
            // 逐像素比较在主线程之外跑：几百万像素的循环若占住 UI 线程，会把心跳判据（250ms）算成自己造成的阻塞。
            var comparison = await Task.Run(() =>
                CompareGlassPhysicalScale(pattern, originX, originY, pixels, GlassOffsetSearchRadius));
            Check("物理100% 逐字节一致", comparison.ByteExact, comparison.Describe());
            if (!comparison.ByteExact && glByteExact)
            {
                // 只是差异定位的读数方向，不改变上面两条严格判定：渲染器输出逐字节一致而整窗不一致。
                GlassHostNote("物理100% 差异定位：GL 帧缓冲→源逐字节一致、整窗→源不一致"
                    + "——差异出在窗口合成/回读这一段（色域转换或缓冲位深），不在渲染器输出");
            }
            if (!comparison.ByteExact)
            {
                using var f16Capture = await CaptureGlassWindowAsync(_activity!, GlassCapturePixelFormat.RgbaF16,
                    GlassWindowColorSpace(SurfaceImageColorSpace()));
                if (f16Capture.Ok)
                {
                    using var f16Window = GlassPixelBuffer.TryRead(f16Capture.Bitmap!, out _);
                    using var f16Pixels = f16Window?.Crop(new Rect(compareLeft, compareTop,
                        compareLeft + compareWidth, compareTop + compareHeight));
                    if (f16Pixels != null)
                    {
                        var f16Comparison = await Task.Run(() =>
                            CompareGlassPhysicalScale(pattern, originX, originY, f16Pixels, GlassOffsetSearchRadius));
                        GlassHostNote("物理100% 整窗 F16 回读裁剪量化对照（仅诊断，不改变上方严格判定）" + f16Comparison.Describe());
                    }
                }
                else
                {
                    GlassHostNote($"物理100% F16 回读失败：{f16Capture.Message}");
                }
            }
        }
        finally
        {
            RestoreGlassOverlays(hidden);
        }
    }

    /// <summary>把照片原点对齐到整数屏幕像素：物理 100% 下的亚像素偏移会让线性采样混入相邻纹素。</summary>
    /// <param name="photo">主图视图。</param>
    private static void AlignGlassImageOrigin(AndroidPhotoView photo)
    {
        double scale = photo.Density;
        var (leftDp, topDp) = photo.Viewport.ViewPoint(0, 0);
        double dx = Math.Round(leftDp * scale) - leftDp * scale;
        double dy = Math.Round(topDp * scale) - topDp * scale;
        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6) return;
        // PanBy 自带边界钳制：居中位置离边界最远，微调不会被夹掉。
        photo.Viewport.PanBy(dx / scale, dy / scale);
    }

    /// <summary>归一化图像点 → 窗口像素坐标（取主图视图真实窗口位置与密度）。</summary>
    /// <param name="photo">主图视图。</param>
    /// <param name="u">归一化横坐标。</param>
    /// <param name="v">归一化纵坐标。</param>
    /// <returns>窗口像素坐标。</returns>
    private static (int X, int Y) GlassWindowPoint(AndroidPhotoView photo, double u, double v)
    {
        var (x, y) = photo.Viewport.ViewPoint(u, v);
        var location = new int[2];
        photo.GetLocationInWindow(location);
        double scale = photo.Density;
        return ((int)Math.Round(location[0] + x * scale), (int)Math.Round(location[1] + y * scale));
    }

    /// <summary>
    /// 二分用最小步骤：只做「隐藏全部根子视图（保留照片层）→ 等 160ms → 恢复可见性」，
    /// 不注入合成图案、不缩放、不做 GL 回读也不做 <see cref="PixelCopy"/>。用来判断「全隐藏再恢复」
    /// 本身是否会让后续取样退化为直通（<c>physical</c> 是唯一会同时隐藏全部取样视图的步骤）。
    /// </summary>
    private static async Task CheckGlassHideRestoreAsync()
    {
        var hidden = new List<(View View, ViewStates Visibility)>();
        int hiddenCount;
        try
        {
            hiddenCount = HideGlassOverlays(hidden);
            await Task.Delay(160);
        }
        finally
        {
            RestoreGlassOverlays(hidden);
        }
        GlassHostNote($"glass 全隐藏恢复：隐藏 {hiddenCount} 个根子视图（保留照片层）→ 等 160ms → 已恢复可见性；"
            + "本步不注入图案、不缩放、不做 GL 回读与 PixelCopy");
        Check("glass 全隐藏恢复 可执行", hiddenCount > 0, $"本次隐藏={hiddenCount} 恢复清单={hidden.Count}");
    }

    /// <summary>
    /// 把主屏上除照片层以外的可见子视图临时设为不可见（不参与布局，只不绘制），让整窗回读只剩照片层。
    /// 照片层是根视图的直接子视图（SHELL-12@android 的取样源），必须保留；不能再按 <c>Photo.Parent</c>
    /// 反推——主图分区已经挂在照片层里，那样会把照片层本身也隐藏掉。
    /// 可重复调用：期间被设置通知重新显示的栏位会在下一次调用里再被隐藏，并追加进同一份清单。
    /// </summary>
    /// <param name="hidden">原可见性清单（调用方持有，收尾交给 <see cref="RestoreGlassOverlays"/>）。</param>
    /// <returns>本次新隐藏的视图数。</returns>
    private static int HideGlassOverlays(List<(View View, ViewStates Visibility)> hidden)
    {
        var root = _root!;
        var photoLayer = root.PhotoLayer;
        int added = 0;
        for (int index = 0; index < root.ChildCount; index++)
        {
            var child = root.GetChildAt(index);
            if (child == null || ReferenceEquals(child, photoLayer)) continue;
            if (child.Visibility != ViewStates.Visible) continue;
            hidden.Add((child, child.Visibility));
            child.Visibility = ViewStates.Invisible;
            added++;
        }
        return added;
    }

    /// <summary>还原栏位可见性。</summary>
    /// <param name="hidden">原可见性清单。</param>
    private static void RestoreGlassOverlays(List<(View View, ViewStates Visibility)> hidden)
    {
        foreach (var (view, visibility) in hidden) view.Visibility = visibility;
    }

    /// <summary>
    /// 输入响应：真实触摸派发一次拖动，分别测两个时刻——渲染器交出下一帧（GL swap 交还）与
    /// 界面层取用该帧（<c>OnSurfaceTextureUpdated</c>）。后者在更新计数递增后用视图写入的 Stopwatch
    /// 时间戳求差，不受轮询粒度影响。两项都止于应用进程内，不含系统合成与显示，不冒充光子时间。
    /// </summary>
    /// <param name="swap">输入 → GL swap 交还的样本。</param>
    /// <param name="present">输入 → 界面层取用的样本。</param>
    /// <param name="rate">本轮刷新率读数。</param>
    private static async Task CheckGlassInputToCommitAsync(GlassFrameIntervalRecorder swap,
        GlassFrameIntervalRecorder present, GlassRefreshRate rate)
    {
        var photo = _root!.Photo;
        if (photo.Width < 16 || photo.Height < 16) throw new InvalidOperationException("主图视图尺寸异常");
        // 先让上一步留下的待绘帧落地，避免它冒充本次输入的首帧。
        await Task.Delay(120);
        float centerX = photo.Width / 2f, centerY = photo.Height / 2f;
        int missed = 0, notPresented = 0;
        for (int index = 0; index < GlassHostLatencySamples; index++)
        {
            // 左右往返，且位移超过系统触控容差：小位移会被当成点按、根本不改视口。
            float step = index % 2 == 0 ? DragStep() : -DragStep();
            double panBefore = photo.Viewport.PanX;
            int framesBefore = photo.RenderedFrames;
            int updatesBefore = photo.TextureUpdatesForChecks;
            long downTime = SystemClock.UptimeMillis();
            SendPhotoTouch(MotionEventActions.Down, downTime, centerX, centerY);
            long started = Stopwatch.GetTimestamp();
            bool consumed = SendPhotoTouch(MotionEventActions.Move, downTime, centerX + step, centerY);

            // 第一段：等渲染器提交（Task.Yield 让出消息循环，绘制帧才能被处理；轮询粒度约 0.1ms）。
            var frameDeadline = DateTime.UtcNow.AddMilliseconds(GlassHostFrameWaitMs);
            while (photo.RenderedFrames <= framesBefore && DateTime.UtcNow < frameDeadline) await Task.Yield();
            double swapMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            bool swapped = photo.RenderedFrames > framesBefore;

            SendPhotoTouch(MotionEventActions.Up, downTime, centerX + step, centerY);
            bool panned = Math.Abs(photo.Viewport.PanX - panBefore) > 1e-6;
            if (!consumed || !panned || !swapped)
            {
                missed++;
                continue;
            }
            swap.AddSample(swapMs);

            // 第二段：等界面层取用该帧；时刻取视图写入的 Stopwatch 时间戳，而不是轮询到的时间。
            var updateDeadline = DateTime.UtcNow.AddMilliseconds(GlassHostFrameWaitMs);
            while (photo.TextureUpdatesForChecks <= updatesBefore && DateTime.UtcNow < updateDeadline)
                await Task.Yield();
            bool updated = photo.TextureUpdatesForChecks > updatesBefore;
            double presentMs = updated
                ? Stopwatch.GetElapsedTime(started, photo.LastTextureUpdateTimestampForChecks).TotalMilliseconds
                : double.NaN;
            if (!updated || double.IsNaN(presentMs) || presentMs < 0) notPresented++;
            else present.AddSample(presentMs);
            await Task.Delay(30);
        }

        var swapStats = swap.Compute();
        var presentStats = present.Compute();
        Check("输入响应 取样完整",
            swapStats.Count >= GlassHostLatencySamples - 1 && presentStats.Count >= GlassHostLatencySamples - 1,
            $"swap 样本={swapStats.Count} 界面层样本={presentStats.Count}/{GlassHostLatencySamples}"
                + $" 未提交={missed} 未取用={notPresented}");
        GlassHostNote($"输入 → GL swap 交还 {swapStats.Describe()}");
        GlassHostNote($"输入 → 界面层取用（OnSurfaceTextureUpdated）{presentStats.Describe()}");
        GlassHostNote($"刷新率 {rate.Describe()}；两项都止于应用进程内，不含系统合成与显示，"
            + "不冒充光子时间；「比 SurfaceView 基线多 ≤1 帧」需基线构建同脚本对照，本检查不判该门槛");
    }

    /// <summary>表面编码口径 → 回读位图的目标色域。</summary>
    /// <param name="space">当前表面色域。</param>
    /// <returns>回读目标色域。</returns>
    private static GlassCaptureColorSpace GlassWindowColorSpace(ImageColorSpace space)
        => space == ImageColorSpace.DisplayP3 ? GlassCaptureColorSpace.DisplayP3 : GlassCaptureColorSpace.Srgb;
}
