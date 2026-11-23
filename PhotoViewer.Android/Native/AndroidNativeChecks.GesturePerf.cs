using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using PhotoViewer.Android.Core;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check gesture-perf</c>（验收专用）：把 6000×4000 素材经真实加载会话升到 Full，
/// 再由 Choreographer 每帧向产品入口 <c>AndroidPhotoView.OnTouchEvent</c> 派发真实 <see cref="MotionEvent"/>，
/// 按 <c>pv-gesture-kind</c> 全程只做单指平移、只做双指捏合，或交替两者，记录 EGL swap 次数、
/// TextureView 取用次数、帧间隔、输入到 swap/取用的墙钟，以及 Window FrameMetrics（API 31+ 另含 GPU 时长）。
/// 只给读数与链路完整性判定，不设设备速度门槛。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>目标素材宽（像素），与 Full 纹理宽一致。</summary>
    private const int GesturePerfWidth = 6000;

    /// <summary>目标素材高（像素）。</summary>
    private const int GesturePerfHeight = 4000;

    /// <summary>单场景默认驱动时长（ms）。</summary>
    private const int GesturePerfDefaultDurationMs = 8000;

    /// <summary>单指平移 / 双指捏合各自持续多久（ms）后切换，仅 <c>mixed</c> 使用。</summary>
    private const int GesturePerfSegmentMs = 1400;

    /// <summary>平移往返一圈（ms）：每帧沿椭圆曲线移动，始终围绕照片中心小幅往返。</summary>
    private const int GesturePerfPanCycleMs = 2400;

    /// <summary>捏合 span 正弦周期（ms）：与混合模式的单段时长相同，段尾回到起点 span。</summary>
    private const int GesturePerfPinchCycleMs = GesturePerfSegmentMs;

    /// <summary>
    /// 场景起点物理倍率：捏合 span 正弦从峰值起步、谷值为启动 span 的 85%，识别器以越过阈值的
    /// 峰值 span 为锚，最低物理倍率因此仍高于 100%；平移也留足覆盖与余量。
    /// </summary>
    private const double GesturePerfStartPhysicalScale = 1.45;

    /// <summary>
    /// 捏合 span 相对起点 span 的固定极差 15%：先用固定帧数把 span 抬到正弦峰值（越过识别器阈值），
    /// 之后按同一极差振荡；谷值保持高于识别器最小 span，流不会中途结束，也不按视口或钳制结果调整。
    /// </summary>
    private const float GesturePerfPinchSpanAmplitude = 0.15f;

    /// <summary>捏合起步把 span 抬到峰值的固定帧数：每帧固定增量，累计极差即正弦振幅。</summary>
    private const int GesturePerfPinchRampFrames = 2;

    /// <summary>手势驱动方式：<c>mixed</c> 交替单指平移与双指捏合，<c>pan</c>/<c>pinch</c> 全程只按一种手势连续驱动。</summary>
    private enum GesturePerfKind
    {
        Mixed,
        Pan,
        Pinch,
    }

    /// <summary>
    /// 入口：先准备 24MP Full 素材并核对上传，再按 <c>pv-gesture-scene</c> 跑
    /// <c>normal</c>（正常主屏）/ <c>hidden</c>（临时移出玻璃与栏位覆盖层）/ 缺省 <c>both</c>，
    /// 每个场景按 <c>pv-gesture-kind</c> 选择手势。
    /// </summary>
    private static async Task RunGesturePerfChecksAsync()
    {
        await WaitForScreenAsync();
        await WaitAsync(() => _root!.Photo.SurfaceReadyForChecks, "主图 EGL 表面就绪", 20000);
        var (scene, durationMs, rawScene) = ReadGesturePerfOptions();
        var (kind, rawKind) = ReadGesturePerfKind();
        var rate = ReadGlassRefreshRate(_activity!);
        Lines.Add($"NOTE gesture-perf scene={scene} kind={DescribeGestureKind(kind)} durationMs={durationMs}"
            + $" 目标={GesturePerfWidth}×{GesturePerfHeight} 刷新率 {rate.Describe()}");
        if (rawScene != null && rawScene != scene)
            Lines.Add($"NOTE gesture-perf 未识别 pv-gesture-scene={rawScene}，按 both 运行");
        if (rawKind != null && rawKind.ToLowerInvariant() is not ("mixed" or "pan" or "pinch"))
            Lines.Add($"NOTE gesture-perf 未识别 pv-gesture-kind={rawKind}，按 mixed 运行");
        Lines.Add("NOTE gesture-perf 每帧由 Choreographer 回调派发真实 MotionEvent（Source=Touchscreen），"
            + "计数止于应用进程内（EGL swap / 界面层取用 / Window FrameMetrics），不含系统合成与显示");
        Lines.Add("NOTE gesture-perf 设置走隔离档 input-checks，栏位显隐走隔离档 gesture-perf；"
            + "normal 场景因此固定为栏位全部可见的主屏，不读也不覆盖用户偏好");

        await RunStepAsync("24MP Full 上屏", PrepareGesturePerfImageAsync);
        if (scene is "both" or "normal")
            await RunStepAsync("正常主屏", () => RunGesturePerfSceneAsync(
                overlaysHidden: false, durationMs: durationMs, kind: kind));
        if (scene is "both" or "hidden")
            await RunStepAsync("移出覆盖层", () => RunGesturePerfSceneAsync(
                overlaysHidden: true, durationMs: durationMs, kind: kind));
    }

    /// <summary>读 <c>pv-gesture-kind</c>（mixed/pan/pinch，缺省 mixed）。</summary>
    /// <returns>手势方式与原始参数（未识别值写进报告）。</returns>
    private static (GesturePerfKind Kind, string? Raw) ReadGesturePerfKind()
    {
        string? raw = _activity?.Intent?.GetStringExtra("pv-gesture-kind");
        var kind = (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "pan" => GesturePerfKind.Pan,
            "pinch" => GesturePerfKind.Pinch,
            _ => GesturePerfKind.Mixed,
        };
        return (kind, string.IsNullOrWhiteSpace(raw) ? null : raw!.Trim());
    }

    /// <summary>手势方式的参数写法（与 Intent 取值一致）。</summary>
    /// <param name="kind">手势方式。</param>
    /// <returns>参数写法。</returns>
    private static string DescribeGestureKind(GesturePerfKind kind) => kind switch
    {
        GesturePerfKind.Pan => "pan",
        GesturePerfKind.Pinch => "pinch",
        _ => "mixed",
    };

    /// <summary>读 <c>pv-gesture-scene</c>（normal/hidden/both）与 <c>pv-gesture-ms</c>（场景时长）。</summary>
    /// <returns>场景、时长与原始场景参数（用于报告里指出未识别值）。</returns>
    private static (string Scene, int DurationMs, string? RawScene) ReadGesturePerfOptions()
    {
        var intent = _activity?.Intent;
        string? raw = intent?.GetStringExtra("pv-gesture-scene");
        string scene = (raw ?? "both").Trim().ToLowerInvariant();
        if (scene is not ("normal" or "hidden" or "both")) scene = "both";
        int durationMs = intent?.GetIntExtra("pv-gesture-ms", 0) ?? 0;
        if (durationMs <= 0) durationMs = GesturePerfDefaultDurationMs;
        // 至少覆盖多个手势周期，否则「改变视口」无从判定。
        durationMs = Math.Clamp(durationMs, 4000, 60000);
        return (scene, durationMs, string.IsNullOrWhiteSpace(raw) ? null : raw!.Trim());
    }

    // ── 素材 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 准备 <c>files/pv-check-gesture-perf/</c> 下的 6000×4000 JPEG（优先复制彩色夹具，缺失时运行时生成），
    /// 走真实加载会话上屏后放大到物理 145% 触发 Full，等 24MP 纹理上传并核对本次提交确实是 24MP。
    /// 素材与产物只在 App 内部目录，不改用户照片；会话图像租约由会话持有，检查侧不借出、不归还。
    /// </summary>
    private static async Task PrepareGesturePerfImageAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var photo = _root!.Photo;
        var folder = IOPath.Combine(activity.FilesDir!.AbsolutePath, "pv-check-gesture-perf");
        Directory.CreateDirectory(folder);
        var path = IOPath.Combine(folder, "gesture-perf-6000x4000.jpg");
        bool copied = false, generated = false;
        if (!IsGesturePerfJpeg(path))
        {
            var source = FindGesturePerfSource(folder);
            if (source != null)
            {
                File.Copy(source, path, overwrite: true);
                copied = true;
            }
            else
            {
                // 24MP 位图与 JPEG 编码放后台线程，避免准备阶段占住 UI 线程。
                await Task.Run(() => WriteCheckJpeg(path, GesturePerfWidth, GesturePerfHeight));
                generated = true;
            }
        }
        if (!IsGesturePerfJpeg(path))
            throw new InvalidOperationException("6000×4000 素材生成/复制失败");

        photo.FitToView();
        AndroidUploadStats.Reset();
        await main.FolderVM.OpenImageAsync(new AndroidFileRef(path));
        await WaitAsync(() => main.CurrentFile?.Name == IOPath.GetFileName(path) && session.Image != null,
            "24MP 素材上屏", 30000);
        // 首次真解码在途时会丢掉视口升级请求：等它落定后再放大，未升级则重复上报视口直到 Full 起来。
        await Task.Delay(600);
        photo.ZoomToPhysical(GesturePerfStartPhysicalScale);
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (session.IsProxy && DateTime.UtcNow < deadline)
        {
            photo.ReportViewport();
            await Task.Delay(250);
        }
        await WaitAsync(() => !session.IsProxy && session.Image is { Width: >= GesturePerfWidth, Height: >= GesturePerfHeight },
            "24MP Full 解码上屏", 10000);
        await WaitAsync(() => photo.Viewport.TextureWidth >= GesturePerfWidth, "24MP 纹理上传", 30000);
        await Task.Delay(400);

        var renderer = PhotoRendererForChecks(photo);
        var upload = AndroidUploadStats.LatestForChecks;
        bool texture = renderer.TextureWidth == GesturePerfWidth && renderer.TextureHeight == GesturePerfHeight;
        bool uploaded = upload is { } entry && entry.Width == GesturePerfWidth && entry.Height == GesturePerfHeight
            && entry.SubmitMs > 0 && !entry.CacheHit;
        string origin = generated ? "运行时生成 JPEG" : copied ? "复制彩色夹具" : "沿用已有素材";
        Check("gesture-perf 24MP Full 已上传", texture && uploaded,
            $"素材={origin} texture={renderer.TextureWidth}x{renderer.TextureHeight}"
            + $" session={session.Image?.Width}x{session.Image?.Height} proxy={session.IsProxy}"
            + $" physical={photo.PhysicalScale:P1} upload={DescribeUpload(upload)}"
            + $" payload={(long)GesturePerfWidth * GesturePerfHeight * 4} 目录={folder}");
        photo.CenterOnNormalizedPoint(.5, .5);
    }

    /// <summary>文件是否确为 6000×4000 JPEG（只读文件头，不整幅解码）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>尺寸匹配且可读。</returns>
    private static bool IsGesturePerfJpeg(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeFile(path, bounds);
        return bounds.OutWidth == GesturePerfWidth && bounds.OutHeight == GesturePerfHeight;
    }

    /// <summary>已在设备上的 6000×4000 夹具：先取彩色验收夹具，再看素材目录里的其它 JPEG。</summary>
    /// <param name="folder">本模式的素材目录。</param>
    /// <returns>可用路径；没有返回 null。</returns>
    private static string? FindGesturePerfSource(string folder)
    {
        var files = _activity!.FilesDir!.AbsolutePath;
        foreach (var candidate in new[]
                 {
                     IOPath.Combine(files, "pv-color-fixtures", "color-p3-large.jpg"),
                     IOPath.Combine(files, "pv-color-fixtures", "color-srgb-large.jpg"),
                 })
        {
            if (IsGesturePerfJpeg(candidate)) return candidate;
        }
        foreach (var candidate in Directory.EnumerateFiles(folder, "*.jpg"))
        {
            if (!string.Equals(candidate, IOPath.Combine(folder, "gesture-perf-6000x4000.jpg"), StringComparison.Ordinal)
                && IsGesturePerfJpeg(candidate)) return candidate;
        }
        return null;
    }

    // ── 单场景 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 跑一个场景：可选先按玻璃验收同一套动作临时移出玻璃与栏位覆盖层（保留照片层），
    /// 再把视口摆到物理 145% 起点，由 Choreographer 按 <paramref name="kind"/> 连续驱动手势，
    /// 收尾按原位放回覆盖层。
    /// </summary>
    /// <param name="overlaysHidden">是否临时移出覆盖层。</param>
    /// <param name="durationMs">驱动时长（ms）。</param>
    /// <param name="kind">手势方式（mixed/pan/pinch）。</param>
    private static async Task RunGesturePerfSceneAsync(bool overlaysHidden, int durationMs, GesturePerfKind kind)
    {
        var photo = _root!.Photo;
        var session = _session!;
        string label = overlaysHidden ? "gesture-perf 移出玻璃/栏位覆盖层" : "gesture-perf 正常主屏";
        string sceneName = overlaysHidden ? "hidden" : "normal";
        string kindName = DescribeGestureKind(kind);
        var detached = new List<GlassDetachedView>();
        var metrics = new GestureFrameMetricsCollector();
        try
        {
            if (overlaysHidden)
            {
                int removed = DetachGlassViews(detached, keep: null);
                Check($"{label} 覆盖层移出", removed > 0,
                    $"移出 {removed} 个场景子视图/玻璃绘制层（保留照片层），收尾按原位放回");
                if (removed == 0) throw new InvalidOperationException("没有可移出的场景子视图或玻璃绘制层");
                await Task.Delay(300);
            }

            photo.FitToView();
            photo.ZoomToPhysical(GesturePerfStartPhysicalScale);
            photo.CenterOnNormalizedPoint(.5, .5);
            await WaitStableViewportAsync(label + " 起点");
            await Task.Delay(200);
            // 给外部（dumpsys gfxinfo reset / 截图）一个可对准的起点，同时让缩放后的首帧落地。
            Console.WriteLine($"[NativeCheck] gesture-perf 就绪 scene={sceneName} kind={kindName}"
                + $" durationMs={durationMs} physical={photo.PhysicalScale:0.###} texture={photo.Viewport.TextureWidth}"
                + $" analysis={(_main!.IsAnalysisViewVisible ? "on" : "off")}，1.2s 后开始");
            await Task.Delay(1200);
            Console.WriteLine($"[NativeCheck] gesture-perf 开始 scene={sceneName} kind={kindName}");

            var viewport = photo.Viewport;
            double startPhysical = viewport.PhysicalScale;
            double startPanX = viewport.PanX, startPanY = viewport.PanY;
            int renderedStart = photo.RenderedFrames;
            int viewportEvents = 0;
            void OnViewportChanged() => viewportEvents++;
            photo.ViewportChanged += OnViewportChanged;
            GestureDriverSnapshot snapshot;
            try
            {
                metrics.Attach(_activity!);
                snapshot = await DriveGestureAsync(durationMs, kind);
            }
            finally
            {
                photo.ViewportChanged -= OnViewportChanged;
            }

            int renderedDelta = photo.RenderedFrames - renderedStart;
            var frameMetrics = metrics.Snapshot();
            var renderer = PhotoRendererForChecks(photo);
            var upload = AndroidUploadStats.LatestForChecks;
            double endPhysical = viewport.PhysicalScale;
            double panDeltaX = viewport.PanX - startPanX, panDeltaY = viewport.PanY - startPanY;
            bool fullOnScreen = renderer.TextureWidth == GesturePerfWidth && renderer.TextureHeight == GesturePerfHeight
                && session.Image is { Width: >= GesturePerfWidth } && !session.IsProxy;
            // 只按所选手势的实际视口变化判定：平移看真实 PanX/PanY 累计位移，捏合看真实物理倍率极差。
            bool panExpected = kind is GesturePerfKind.Pan or GesturePerfKind.Mixed;
            bool pinchExpected = kind is GesturePerfKind.Pinch or GesturePerfKind.Mixed;
            double physicalRange = snapshot.MaxPhysical - snapshot.MinPhysical;
            bool panProven = !panExpected
                || (snapshot.PanChangedFrames > 0 && snapshot.PanTravel > 1
                    && snapshot.PanChangedFrames >= snapshot.PanMoveFrames / 2);
            bool pinchProven = !pinchExpected
                || (physicalRange > startPhysical * .05 && snapshot.PinchMoveFrames > 0
                    && (kind == GesturePerfKind.Mixed || snapshot.ScaleChangedFrames >= snapshot.PinchMoveFrames * .95));
            bool sustainedFull = kind == GesturePerfKind.Mixed || snapshot.MinPhysical >= 1;

            Check($"{label} 24MP Full 保持在屏", fullOnScreen && photo.NeedsFull && sustainedFull,
                $"kind={kindName} texture={renderer.TextureWidth}x{renderer.TextureHeight}"
                + $" session={session.Image?.Width}x{session.Image?.Height} proxy={session.IsProxy}"
                + $" needsFull={photo.NeedsFull} physical={endPhysical:P1} upload={DescribeUpload(upload)}");
            Check($"{label} 手势请求改变视口",
                viewportEvents > 0 && snapshot.Consumed > 0 && panProven && pinchProven,
                $"kind={kindName} viewportEvents={viewportEvents}"
                + $" 平移变化帧={snapshot.PanChangedFrames}/{snapshot.PanMoveFrames}"
                + $" 平移累计={snapshot.PanTravel:F1}dp 端点=({panDeltaX:0.#},{panDeltaY:0.#})"
                + $" 缩放变化帧={snapshot.ScaleChangedFrames}/{snapshot.PinchMoveFrames}"
                + $" physical={startPhysical:P2}→{endPhysical:P2} 极值={snapshot.MinPhysical:P2}..{snapshot.MaxPhysical:P2}"
                + $" 事件={snapshot.Consumed}/{snapshot.Dispatched}");
            Check($"{label} EGL swap 与界面层取用计数",
                snapshot.Frames > 0 && snapshot.Swaps > 0 && snapshot.TextureUpdates > 0
                && snapshot.Dispatched >= snapshot.Frames - 1 && snapshot.Consumed == snapshot.Dispatched,
                $"frames={snapshot.Frames} durationMs={snapshot.DurationMs:F0}"
                + $" eglSwap={snapshot.Swaps} swap/frame={(double)snapshot.Swaps / Math.Max(1, snapshot.Frames):F2}"
                + $" renderedFramesDelta={renderedDelta}"
                + $" texture取用={snapshot.TextureUpdates} 取用/frame={(double)snapshot.TextureUpdates / Math.Max(1, snapshot.Frames):F2}"
                + $" input={snapshot.Consumed}/{snapshot.Dispatched}（不设速度门槛）");
            Check($"{label} 帧间隔读数", snapshot.FrameIntervals.HasSamples && snapshot.SwapIntervals.HasSamples,
                $"choreographer {snapshot.FrameIntervals.Describe()} swap间隔 {snapshot.SwapIntervals.Describe()}"
                + $" 取用间隔 {snapshot.TextureIntervals.Describe()}"
                + $" 输入→swap {DescribeMillis(snapshot.InputToSwap)} 输入→下一次取用 {DescribeMillis(snapshot.InputToTexture)}"
                + "（不设速度门槛）");
            Check($"{label} Window FrameMetrics 采样", frameMetrics.Total.Length > 0,
                $"回调={frameMetrics.Total.Length} 丢弃报告={frameMetrics.Dropped}"
                + $" total(ms) {DescribeMillis(Summarize(frameMetrics.Total))}"
                + $" draw(ms) {DescribeMillis(Summarize(frameMetrics.Draw))}"
                + $" gpu(ms) {DescribeMillis(Summarize(frameMetrics.Gpu))}"
                + $" 来源={metrics.AttachNote}（不设速度门槛）");
            Console.WriteLine($"[NativeCheck] gesture-perf scene={sceneName} kind={kindName}"
                + $" frames={snapshot.Frames} swaps={snapshot.Swaps} textureUpdates={snapshot.TextureUpdates}"
                + $" physical={startPhysical:0.###}->{endPhysical:0.###} range={snapshot.MinPhysical:0.###}..{snapshot.MaxPhysical:0.###}"
                + $" frameMedianMs={snapshot.FrameIntervals.MedianMs:0.##} swapMedianMs={snapshot.SwapIntervals.MedianMs:0.##}"
                + $" frameMetrics={frameMetrics.Total.Length} dropped={frameMetrics.Dropped}");
        }
        finally
        {
            metrics.Detach();
            RestoreGlassOverlays(detached);
            photo.FitToView();
        }
    }

    /// <summary>挂上 swap 事件后启动 Choreographer 驱动，留一段落地时间再摘掉事件并取快照。</summary>
    /// <param name="durationMs">驱动时长（ms）。</param>
    /// <param name="kind">手势方式（mixed/pan/pinch）。</param>
    /// <returns>本场景读数。</returns>
    private static async Task<GestureDriverSnapshot> DriveGestureAsync(int durationMs, GesturePerfKind kind)
    {
        var photo = _root!.Photo;
        var rate = ReadGlassRefreshRate(_activity!);
        var driver = new GestureFrameDriver(photo, durationMs, rate, kind);
        photo.PresentationFrameRendered += driver.OnPresentationFrame;
        try
        {
            var run = driver.RunAsync();
            var timeout = Task.Delay(durationMs + 15000);
            if (await Task.WhenAny(run, timeout) != run)
            {
                driver.Stop();
                throw new TimeoutException($"Choreographer 驱动超时（{durationMs + 15000}ms）");
            }
            // 最后一帧的 swap 与界面层取用可能在回调返回之后才落地：保持事件挂载取齐再收尾。
            await Task.Delay(350);
        }
        finally
        {
            photo.PresentationFrameRendered -= driver.OnPresentationFrame;
            driver.Stop();
        }
        return driver.Snapshot();
    }

    // ── 读数 ──────────────────────────────────────────────────────────────────

    /// <summary>一段毫秒读数的统量（n/均值/中位/p95/最大）。</summary>
    /// <param name="Count">样本数。</param>
    /// <param name="Mean">均值（ms）。</param>
    /// <param name="Median">中位（ms）。</param>
    /// <param name="P95">95 分位（ms）。</param>
    /// <param name="Max">最大（ms）。</param>
    private readonly record struct MillisStats(int Count, double Mean, double Median, double P95, double Max)
    {
        /// <summary>是否有样本。</summary>
        internal bool HasSamples => Count > 0;

        /// <summary>报告用描述。</summary>
        /// <returns>可读描述。</returns>
        internal string Describe()
            => Count == 0 ? "无样本" : $"n={Count} 均值={Mean:F2} 中位={Median:F2} p95={P95:F2} 最大={Max:F2}";
    }

    /// <summary>把一份毫秒样本算成熟量；样本为空时各项为 NaN。</summary>
    /// <param name="samples">样本（ms）。</param>
    /// <returns>统量。</returns>
    private static MillisStats Summarize(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return new MillisStats(0, double.NaN, double.NaN, double.NaN, double.NaN);
        var sorted = new double[samples.Count];
        double sum = 0, max = double.NegativeInfinity;
        for (int index = 0; index < samples.Count; index++)
        {
            double sample = samples[index];
            sorted[index] = sample;
            sum += sample;
            if (sample > max) max = sample;
        }
        Array.Sort(sorted);
        int count = sorted.Length;
        double median = count % 2 == 1 ? sorted[count / 2] : (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
        int p95Index = Math.Clamp((int)Math.Ceiling(count * 0.95) - 1, 0, count - 1);
        return new MillisStats(count, sum / count, median, sorted[p95Index], max);
    }

    /// <summary>毫秒统量的报告文本（无样本时不写 NaN）。</summary>
    /// <param name="stats">统量。</param>
    /// <returns>可读描述。</returns>
    private static string DescribeMillis(MillisStats stats) => stats.HasSamples ? stats.Describe() : "无样本";

    /// <summary>帧间隔统量的报告文本（只取中位/p95/最大，不折算掉帧）。</summary>
    /// <param name="stats">统量。</param>
    /// <returns>可读描述。</returns>
    private static string DescribeMillis(GlassFrameStats stats)
        => stats.HasSamples
            ? $"n={stats.Count} 中位={stats.MedianMs:F2} p95={stats.P95Ms:F2} 最大={stats.MaxMs:F2}"
            : "无样本";

    // ── 逐帧驱动 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 每帧在 Choreographer 回调里向产品入口派发真实触摸。<c>mixed</c> 按相邻 1400ms 段交替单指平移与双指捏合；
    /// <c>pan</c>/<c>pinch</c> 全程只有一次 Down…Up，不随时间切换或重启。平移沿视图中心的小幅椭圆曲线往返，
    /// 捏合以固定极差的正弦 span 振荡（从峰值起步，首个 Move 即越过识别器阈值），都不读视口或钳制结果做反馈。
    /// 同一回调内采样 EGL swap、界面层取用与帧间隔，全部只读、无产品分支。
    /// </summary>
    private sealed class GestureFrameDriver : Java.Lang.Object, Choreographer.IFrameCallback
    {
        private readonly AndroidPhotoView _photo;
        private readonly GesturePerfKind _kind;
        private readonly int _durationMs;
        private readonly GlassFrameIntervalRecorder _frameIntervals;
        private readonly GlassFrameIntervalRecorder _swapIntervals;
        private readonly GlassFrameIntervalRecorder _textureIntervals;
        private readonly GlassFrameIntervalRecorder _inputToSwap;
        private readonly GlassFrameIntervalRecorder _inputToTexture;
        private readonly Stopwatch _clock = new();
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Choreographer? _choreographer;
        private long _lastFrameNanos;
        private long _lastSwapAt;
        private long _lastTextureStamp;
        private long _pendingSwapAt;
        private long _pendingTextureAt;
        private int _lastTextureCount;
        private int _frames;
        private int _swaps;
        private int _textureUpdates;
        private int _dispatched;
        private int _consumed;
        private int _panChangedFrames;
        private int _panMoveFrames;
        private int _scaleChangedFrames;
        private int _pinchMoveFrames;
        private double _panTravel;
        private double _minPhysical = double.NaN;
        private double _maxPhysical = double.NaN;
        private int _segment = -1;
        private int _gestureFrames;
        private int _pointers;
        private long _downTime;
        private long _gestureStartMs;
        private float _panCenterX, _panCenterY, _panRadiusX, _panRadiusY, _panLastX, _panLastY;
        private float _focusX, _focusY, _gap, _pinchBaseGap, _pinchRampStep;
        private bool _running;

        /// <summary>绑定主图、刷新率、时长与手势方式；计数器在 <see cref="RunAsync"/> 时从当前读数起算。</summary>
        /// <param name="photo">主图视图。</param>
        /// <param name="durationMs">驱动时长（ms）。</param>
        /// <param name="rate">刷新率读数（用于帧间隔折算）。</param>
        /// <param name="kind">手势方式（mixed/pan/pinch）。</param>
        internal GestureFrameDriver(AndroidPhotoView photo, int durationMs, GlassRefreshRate rate, GesturePerfKind kind)
        {
            _photo = photo;
            _durationMs = durationMs;
            _kind = kind;
            _frameIntervals = new GlassFrameIntervalRecorder(rate);
            _swapIntervals = new GlassFrameIntervalRecorder(rate);
            _textureIntervals = new GlassFrameIntervalRecorder(rate);
            _inputToSwap = new GlassFrameIntervalRecorder(rate);
            _inputToTexture = new GlassFrameIntervalRecorder(rate);
        }

        /// <summary>swap 事件：记录次数、间隔与「输入 → swap」墙钟。</summary>
        internal void OnPresentationFrame()
        {
            long now = Stopwatch.GetTimestamp();
            _swaps++;
            if (_lastSwapAt != 0)
                _swapIntervals.AddSample(Stopwatch.GetElapsedTime(_lastSwapAt, now).TotalMilliseconds);
            _lastSwapAt = now;
            if (_pendingSwapAt != 0)
            {
                _inputToSwap.AddSample(Stopwatch.GetElapsedTime(_pendingSwapAt, now).TotalMilliseconds);
                _pendingSwapAt = 0;
            }
        }

        /// <summary>开始计时并投递第一帧。</summary>
        /// <returns>驱动结束的任务。</returns>
        internal Task RunAsync()
        {
            var choreographer = Choreographer.Instance;
            if (choreographer == null) throw new InvalidOperationException("当前线程缺少 Choreographer");
            _choreographer = choreographer;
            _lastTextureCount = _photo.TextureUpdatesForChecks;
            // 上一次取用可能发生在上一个场景：不从残留时间戳起算首个间隔，等本场景第一帧取用后重新起算。
            _lastTextureStamp = 0;
            _running = true;
            _clock.Start();
            choreographer.PostFrameCallback(this);
            return _completion.Task;
        }

        /// <summary>停止驱动并给仍在按下的手指补一条抬起（异常收尾与超时共用）。</summary>
        internal void Stop()
        {
            _running = false;
            if (_clock.IsRunning) _clock.Stop();
            try
            {
                _choreographer?.RemoveFrameCallback(this);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[NativeCheck] gesture-perf 移除帧回调失败：{exception.GetType().Name}");
            }
            EndGesture();
            _completion.TrySetResult(false);
        }

        /// <summary>每帧：记帧间隔与取用、按手势方式起跑或推进手势、派发本帧事件，再排下一帧。</summary>
        /// <param name="frameTimeNanos">本帧 vsync 时间（ns）。</param>
        public void DoFrame(long frameTimeNanos)
        {
            if (!_running) return;
            if (_lastFrameNanos != 0)
                _frameIntervals.AddSample((frameTimeNanos - _lastFrameNanos) / 1_000_000.0);
            _lastFrameNanos = frameTimeNanos;
            _frames++;
            SampleTextureUpdates();

            long elapsed = _clock.ElapsedMilliseconds;
            if (elapsed >= _durationMs)
            {
                EndGesture();
                _clock.Stop();
                _running = false;
                _completion.TrySetResult(true);
                return;
            }

            // mixed 按 1400ms 分段交替；pan/pinch 全程只有一个段号，只 Down/Up 一次，不切换、不重启。
            int segment = _kind == GesturePerfKind.Mixed ? (int)(elapsed / GesturePerfSegmentMs) : 0;
            if (segment != _segment)
            {
                EndGesture();
                _segment = segment;
                StartGesture(_kind == GesturePerfKind.Pinch
                    || (_kind == GesturePerfKind.Mixed && (segment & 1) == 1));
            }
            else
            {
                StepGesture(elapsed);
            }
            SamplePhysical();
            _choreographer?.PostFrameCallback(this);
        }

        /// <summary>收尾读数（再取一次界面层取用，覆盖回调之后的最后一帧）。</summary>
        /// <returns>本场读数。</returns>
        internal GestureDriverSnapshot Snapshot()
        {
            SampleTextureUpdates();
            return new GestureDriverSnapshot(_frames, _dispatched, _consumed, _swaps, _textureUpdates,
                _panChangedFrames, _panMoveFrames, _panTravel, _scaleChangedFrames, _pinchMoveFrames,
                _minPhysical, _maxPhysical, _clock.Elapsed.TotalMilliseconds,
                _frameIntervals.Compute(), _swapIntervals.Compute(), _textureIntervals.Compute(),
                _inputToSwap.Compute(), _inputToTexture.Compute());
        }

        /// <summary>记下本帧的实际物理倍率，供报告给出捏合极差（平移全程恒定）。</summary>
        private void SamplePhysical()
        {
            double physical = _photo.PhysicalScale;
            if (double.IsNaN(_minPhysical) || physical < _minPhysical) _minPhysical = physical;
            if (double.IsNaN(_maxPhysical) || physical > _maxPhysical) _maxPhysical = physical;
        }

        /// <summary>开始一段手势：平移是单指 Down，捏合是 Down + 第二指 PointerDown。</summary>
        /// <param name="pinch">本段是否捏合。</param>
        private void StartGesture(bool pinch)
        {
            _gestureStartMs = _clock.ElapsedMilliseconds;
            _gestureFrames = 0;
            _downTime = SystemClock.UptimeMillis();
            if (pinch)
            {
                // 谷值仍高于识别器最小 span（1.25 × minSpan × 0.85 ≈ 1.06 × minSpan），缩放流不会中途结束。
                float minSpan = ViewConfiguration.Get(_activity!)?.ScaledMinimumScalingSpan ?? 0;
                float gap = Math.Max(Math.Min(_photo.Width, _photo.Height) * .50f, minSpan * 1.25f);
                gap = Math.Min(Math.Max(gap, 8f), Math.Max(8f, (_photo.Width - 32f) / 1.15f));
                _pinchBaseGap = _gap = gap;
                _pinchRampStep = gap * GesturePerfPinchSpanAmplitude / GesturePerfPinchRampFrames;
                _focusX = _photo.Width / 2f;
                _focusY = _photo.Height / 2f;
                var left = (0, _focusX - gap / 2, _focusY);
                var right = (1, _focusX + gap / 2, _focusY);
                DispatchMulti(MotionEventActions.Down, 0, left);
                DispatchMulti(MotionEventActions.PointerDown, 1, left, right);
                _pointers = 2;
            }
            else
            {
                _panCenterX = _photo.Width / 2f;
                _panCenterY = _photo.Height / 2f;
                float radius = Math.Max(TouchSlopPx * 2f, Math.Min(_photo.Width, _photo.Height) * .06f);
                radius = Math.Min(radius, Math.Min(_photo.Width, _photo.Height) * .5f - 32f);
                _panRadiusX = Math.Max(1f, radius);
                _panRadiusY = _panRadiusX * .55f;
                _panLastX = _panCenterX;
                _panLastY = _panCenterY;
                DispatchSingle(MotionEventActions.Down, _panLastX, _panLastY);
                _pointers = 1;
            }
        }

        /// <summary>推进本段一帧：单指走平移曲线，两指走捏合 span。</summary>
        /// <param name="elapsed">本场景已运行时长（ms）。</param>
        private void StepGesture(long elapsed)
        {
            _gestureFrames++;
            if (_pointers == 2) StepPinch(elapsed);
            else if (_pointers == 1) StepPan(elapsed);
        }

        /// <summary>
        /// 单指平移：围绕视图中心的椭圆曲线往返，首帧横向位移即超过触控容差；
        /// 照片在当前倍率下覆盖视口，曲线始终落在照片内部，每帧都真正改变视口平移。
        /// </summary>
        /// <param name="elapsed">本场景已运行时长（ms）。</param>
        private void StepPan(long elapsed)
        {
            _panMoveFrames++;
            // 首个 Move 固定落在曲线起点（横向 R），不受首帧时序抖动影响，确保越过触控容差。
            double angle = _gestureFrames == 1
                ? 0
                : Math.PI * 2 * Math.Max(0, elapsed - _gestureStartMs) / GesturePerfPanCycleMs;
            float x = _panCenterX + _panRadiusX * (float)Math.Cos(angle);
            float y = _panCenterY + _panRadiusY * (float)Math.Sin(angle);
            _panLastX = x;
            _panLastY = y;
            double beforePanX = _photo.Viewport.PanX, beforePanY = _photo.Viewport.PanY;
            long dispatchedAt = Stopwatch.GetTimestamp();
            DispatchSingle(MotionEventActions.Move, x, y);
            double dx = _photo.Viewport.PanX - beforePanX, dy = _photo.Viewport.PanY - beforePanY;
            _panTravel += Math.Abs(dx) + Math.Abs(dy);
            if (dx != 0 || dy != 0)
            {
                _panChangedFrames++;
                MarkPendingInput(dispatchedAt);
            }
        }

        /// <summary>
        /// 双指捏合：前几帧以固定 span 增量抬到正弦峰值，越过 ScaleGestureDetector 的最小 span 与 span 容差；
        /// 之后 span 按启动 span 的固定极差振荡（周期与单段等长），不读物理倍率或钳制结果。
        /// </summary>
        /// <param name="elapsed">本场景已运行时长（ms）。</param>
        private void StepPinch(long elapsed)
        {
            _pinchMoveFrames++;
            float span;
            if (_gestureFrames <= GesturePerfPinchRampFrames)
            {
                span = _pinchBaseGap + _pinchRampStep * _gestureFrames;
            }
            else
            {
                double phase = Math.PI * 2 * Math.Max(0, elapsed - _gestureStartMs) / GesturePerfPinchCycleMs;
                span = _pinchBaseGap * (float)(1 + GesturePerfPinchSpanAmplitude * Math.Cos(phase));
            }
            _gap = span;
            float halfGap = _gap / 2f;
            var left = (0, _focusX - halfGap, _focusY);
            var right = (1, _focusX + halfGap, _focusY);
            double beforeScale = _photo.PhysicalScale;
            long dispatchedAt = Stopwatch.GetTimestamp();
            DispatchMulti(MotionEventActions.Move, 0, left, right);
            if (_photo.PhysicalScale != beforeScale)
            {
                _scaleChangedFrames++;
                MarkPendingInput(dispatchedAt);
            }
        }

        /// <summary>结束当前手势：两指先 PointerUp 再 Up，单指直接 Up；异常收尾与换段共用。</summary>
        private void EndGesture()
        {
            if (_pointers == 2)
            {
                var left = (0, _focusX - _gap / 2, _focusY);
                var right = (1, _focusX + _gap / 2, _focusY);
                DispatchMulti(MotionEventActions.PointerUp, 0, left, right);
                DispatchMulti(MotionEventActions.Up, 0, right);
                _pointers = 0;
            }
            else if (_pointers == 1)
            {
                DispatchSingle(MotionEventActions.Up, _panLastX, _panLastY);
                _pointers = 0;
            }
        }

        /// <summary>记下一条真正改变视口的事件时间，等 swap/取用落地时算墙钟。</summary>
        /// <param name="dispatchedAt">派发前的时间戳。</param>
        private void MarkPendingInput(long dispatchedAt)
        {
            if (_pendingSwapAt == 0) _pendingSwapAt = dispatchedAt;
            if (_pendingTextureAt == 0) _pendingTextureAt = dispatchedAt;
        }

        /// <summary>读界面层取用计数与时间戳：单次增长才计入间隔与「输入 → 取用」样本。</summary>
        private void SampleTextureUpdates()
        {
            int count = _photo.TextureUpdatesForChecks;
            if (count == _lastTextureCount) return;
            int delta = count - _lastTextureCount;
            long stamp = _photo.LastTextureUpdateTimestampForChecks;
            _lastTextureCount = count;
            _textureUpdates += delta;
            if (delta == 1 && stamp > 0)
            {
                if (_lastTextureStamp != 0 && stamp > _lastTextureStamp)
                    _textureIntervals.AddSample(Stopwatch.GetElapsedTime(_lastTextureStamp, stamp).TotalMilliseconds);
                if (_pendingTextureAt != 0 && stamp > _pendingTextureAt)
                {
                    _inputToTexture.AddSample(Stopwatch.GetElapsedTime(_pendingTextureAt, stamp).TotalMilliseconds);
                    _pendingTextureAt = 0;
                }
            }
            _lastTextureStamp = stamp;
        }

        private void DispatchSingle(MotionEventActions action, float x, float y)
        {
            _dispatched++;
            if (SendPhotoTouch(action, _downTime, x, y)) _consumed++;
        }

        private void DispatchMulti(MotionEventActions action, int actionIndex,
            params (int Id, float X, float Y)[] pointers)
        {
            _dispatched++;
            if (SendPhotoMultiTouch(action, actionIndex, _downTime, pointers)) _consumed++;
        }
    }

    /// <summary>一次驱动结束的读数：计数、视口变化帧与各类间隔统计。</summary>
    /// <param name="Frames">Choreographer 回调帧数。</param>
    /// <param name="Dispatched">派发的 MotionEvent 条数。</param>
    /// <param name="Consumed">被产品入口消费的条数。</param>
    /// <param name="Swaps">窗口期间 <c>PresentationFrameRendered</c> 次数（成功 EGL swap）。</param>
    /// <param name="TextureUpdates">TextureView 表面纹理被界面层取用的次数。</param>
    /// <param name="PanChangedFrames">真正改变平移的帧数。</param>
    /// <param name="PanMoveFrames">单指 Move 派发帧数。</param>
    /// <param name="PanTravel">平移实际变化累计（|ΔPanX| + |ΔPanY|，逻辑点）。</param>
    /// <param name="ScaleChangedFrames">真正改变缩放的帧数。</param>
    /// <param name="PinchMoveFrames">双指 Move 派发帧数。</param>
    /// <param name="MinPhysical">驱动期间最低物理倍率。</param>
    /// <param name="MaxPhysical">驱动期间最高物理倍率。</param>
    /// <param name="DurationMs">驱动时长（ms）。</param>
    /// <param name="FrameIntervals">Choreographer 帧间隔。</param>
    /// <param name="SwapIntervals">相邻 swap 间隔。</param>
    /// <param name="TextureIntervals">相邻界面层取用间隔。</param>
    /// <param name="InputToSwap">改变视口的事件 → 下一次 swap。</param>
    /// <param name="InputToTexture">改变视口的事件 → 下一次界面层取用。</param>
    private readonly record struct GestureDriverSnapshot(int Frames, int Dispatched, int Consumed,
        int Swaps, int TextureUpdates, int PanChangedFrames, int PanMoveFrames, double PanTravel,
        int ScaleChangedFrames, int PinchMoveFrames, double MinPhysical, double MaxPhysical, double DurationMs,
        GlassFrameStats FrameIntervals, GlassFrameStats SwapIntervals, GlassFrameStats TextureIntervals,
        GlassFrameStats InputToSwap, GlassFrameStats InputToTexture);

    // ── Window FrameMetrics ───────────────────────────────────────────────────

    /// <summary>FrameMetrics 采样快照（ms）；GPU 时长只在 API 31+ 有读数。</summary>
    /// <param name="Total">每帧总时长（ms）。</param>
    /// <param name="Draw">绘制阶段时长（ms）。</param>
    /// <param name="Gpu">GPU 时长（ms，API 31+）。</param>
    /// <param name="Dropped">系统报告丢弃的报告数。</param>
    private readonly record struct FrameMetricsSnapshot(double[] Total, double[] Draw, double[] Gpu, int Dropped);

    /// <summary>
    /// 在独立 HandlerThread 上收 Window FrameMetrics：只取值不做计算，避免拖慢报告方；
    /// 挂不上或设备不支持时如实记录原因，不影响手势与计数主链路。
    /// </summary>
    private sealed class GestureFrameMetricsCollector : Java.Lang.Object, Window.IOnFrameMetricsAvailableListener
    {
        private readonly object _gate = new();
        private readonly List<double> _totalMs = [];
        private readonly List<double> _drawMs = [];
        private readonly List<double> _gpuMs = [];
        private HandlerThread? _thread;
        private Handler? _handler;
        private Window? _window;
        private int _dropped;

        /// <summary>挂载结果说明（写入报告）。</summary>
        internal string AttachNote { get; private set; } = "未挂载";

        /// <summary>挂到当前窗口；失败只记录原因。</summary>
        /// <param name="activity">宿主 Activity。</param>
        /// <returns>是否挂载成功。</returns>
        internal bool Attach(global::Android.App.Activity activity)
        {
            try
            {
                _thread = new HandlerThread("pv-gesture-metrics");
                _thread.Start();
                _handler = new Handler(_thread.Looper!);
                _window = activity.Window;
                if (_window == null)
                {
                    AttachNote = "窗口为空";
                    return false;
                }
                _window.AddOnFrameMetricsAvailableListener(this, _handler!);
                AttachNote = "Window.FrameMetrics";
                return true;
            }
            catch (Exception exception)
            {
                AttachNote = exception.GetType().Name + "：" + exception.Message;
                return false;
            }
        }

        /// <summary>系统每帧回调；只读三个时长，FrameMetrics 实例在回调返回后即失效。</summary>
        /// <param name="window">产生该帧的窗口。</param>
        /// <param name="metrics">帧读数（回调返回后失效）。</param>
        /// <param name="dropCountSinceLastInvocation">距上次回调丢弃的报告数。</param>
        public void OnFrameMetricsAvailable(Window? window, FrameMetrics? metrics, int dropCountSinceLastInvocation)
        {
            if (metrics == null) return;
            try
            {
                double total = metrics.GetMetric((int)FrameMetricsId.TotalDuration) / 1e6;
                double draw = metrics.GetMetric((int)FrameMetricsId.DrawDuration) / 1e6;
                double gpu = double.NaN;
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    double reported = metrics.GetMetric((int)FrameMetricsId.GpuDuration) / 1e6;
                    if (reported >= 0) gpu = reported;
                }
                lock (_gate)
                {
                    _totalMs.Add(total);
                    _drawMs.Add(draw);
                    if (!double.IsNaN(gpu)) _gpuMs.Add(gpu);
                    _dropped += Math.Max(0, dropCountSinceLastInvocation);
                }
            }
            catch (Exception)
            {
                // 单帧读数失败不进入样本；不让系统回调抛回给窗口。
            }
        }

        /// <summary>取一份拷贝（回调线程仍在写，必须加锁复制）。</summary>
        /// <returns>快照。</returns>
        internal FrameMetricsSnapshot Snapshot()
        {
            lock (_gate)
                return new FrameMetricsSnapshot(_totalMs.ToArray(), _drawMs.ToArray(), _gpuMs.ToArray(), _dropped);
        }

        /// <summary>摘掉监听并结束采样线程；重复调用无副作用。</summary>
        internal void Detach()
        {
            try
            {
                _window?.RemoveOnFrameMetricsAvailableListener(this);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[NativeCheck] gesture-perf 摘除 FrameMetrics 监听失败：{exception.GetType().Name}");
            }
            _window = null;
            try
            {
                _thread?.QuitSafely();
            }
            catch (Exception)
            {
                // 线程已退出或未启动：无需处理。
            }
            _thread?.Dispose();
            _thread = null;
            _handler?.Dispose();
            _handler = null;
        }
    }
}
