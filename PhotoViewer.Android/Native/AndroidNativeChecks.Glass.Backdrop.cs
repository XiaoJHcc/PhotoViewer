using System;
using System.IO;
using System.Threading.Tasks;
using Android.Graphics;
using Android.Views;
using PhotoViewer.Android.Backdrop;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Image;
using ImageColorSpace = PhotoViewer.Core.Color.ImageColorSpace;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>pv-check glass</c> 的取样组（SAMPLE）与多层组（LAYERS）——阶段 0 的迁移层机制检查（legacy），验收专用。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>这两组只在显式点名 <c>pv-glass-stage sample|layers</c> 时运行，**不再作为产品材质验收证据**：
/// 产品材质（栏底与带衬底 EXIF）的验收在 <c>product</c> 组，直接跑真实主屏的照片层与共享材质。</item>
/// <item>SAMPLE：用「另建」的 <see cref="AndroidPhotoView"/> 放进临时 <see cref="LayerBackdropLayout"/>，
/// 栏底取样条按上游配方（<c>vibrancy</c> → <c>blur</c> 8dp）取样，验证切图/缩放后取样实时更新、画面确实被模糊，并记录心跳。</item>
/// <item>LAYERS：三层嵌套（屏幕 → 场景 → 照片），验证按钮下方的照片、滚过的卡片与窗口内面板各取到「正确的层」像素；
/// 三层属于阶段 3 范围，阶段 2 不据此下产品结论。</item>
/// <item>阶段 0 只验取样与平台效果：不做 AGSL 折射、着色器高光、G2 形状与按压弹簧（属阶段 3）。</item>
/// <item>检查 UI 只在本次运行期间装配，<c>using</c> 收尾整体拆除；产品路径不引用。</item>
/// <item>无法自动判定的观感项写 <c>NOTE</c>，不计入 PASS/FAIL，也不拿相邻读数冒充通过。</item>
/// </list>
/// </remarks>
internal static partial class AndroidNativeChecks
{
    /// <summary>取样条模糊半径（dp，上游示例底栏配方）。</summary>
    private const float GlassBackdropBlurDp = 8f;

    /// <summary>切图/缩放后取样均值的最小变化量（通道绝对值平均，8 位）。</summary>
    private const double GlassBackdropChangeThreshold = 10;

    /// <summary>取样心跳的预算（ms）：p95 与最大值都要落在其中（最大值兜住偶发的一次长阻塞）。</summary>
    private const double GlassBackdropHeartbeatBudgetMs = 250;

    /// <summary>
    /// SAMPLE 入口（legacy 机制检查，只在显式点名 <c>sample</c> 时运行）：异常收在本组内，
    /// 不连带后面的 LAYERS 组（宿主按顺序直接调用本方法）。产品材质验收在 <c>product</c> 组。
    /// </summary>
    private static async Task RunGlassSampleChecksAsync()
    {
        try
        {
            await RunGlassSampleChecksCoreAsync();
        }
        catch (Exception exception)
        {
            Check("glass 取样组（中止）", false, exception.GetType().Name + "：" + exception.Message);
        }
    }

    /// <summary>
    /// SAMPLE：最小照片层取样。装配「照片层（真实 AndroidPhotoView）→ 栏底取样条」，按上游配方取样，
    /// 依次验证取到内容、切图与缩放后实时更新、vibrancy 提饱和、blur 确实模糊，最后记录心跳。
    /// </summary>
    private static async Task RunGlassSampleChecksCoreAsync()
    {
        var activity = _activity ?? throw new InvalidOperationException("glass 取样检查缺少宿主 Activity");
        GlassBackdropNote("glass 取样组定位",
            "legacy 迁移层机制检查：挂临时舞台、另建原型主图，只在显式点名 sample 时运行；"
            + "产品材质（栏底与带衬底 EXIF）的验收在 product 组，本组结论不作为产品验收证据");
        GlassBackdropNote("glass 平台分级", BackdropCapabilities.Describe());

        if (BackdropCapabilities.Tier == BackdropTier.ColorLayer)
        {
            GlassBackdropNote("glass 取样", "API 31 以下不取样：栏底与控件按分级绘制正式色层，本组不执行");
            return;
        }

        using var stage = new GlassBackdropStage(activity);
        if (!stage.IsAttached)
        {
            Check("glass 取样舞台", false, "窗口 DecorView 不可用，无法装配检查 UI");
            return;
        }

        var overlay = stage.Overlay;
        await WaitAsync(() => overlay.Width > 0 && overlay.Height > 0, "检查叠加层布局", 8000);
        int width = overlay.Width, height = overlay.Height;
        if (width < 320 || height < 400)
        {
            GlassBackdropNote("glass 取样", $"窗口 {width}x{height} 过小，跳过取样检查");
            return;
        }

        var photoLayer = stage.AddLayer(overlay, Color.Argb(255, 0, 0, 0));
        int photoHeight = Math.Max(160, height * 45 / 100);
        var photo = stage.AddPhoto(photoLayer, new Rect(0, 0, width, photoHeight));
        await WaitAsync(() => photo.Width > 0 && photo.Height > 0, "原型主图布局", 8000);
        await WaitAsync(() => photo.SurfaceReadyForChecks, "原型主图 EGL 表面就绪", 20000);

        var colorSpace = photo.SurfaceIsP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;

        // 栏底取样条落在照片中部；条下再铺一块屏幕层独有的标记色块。
        // 取样画出内容时回读到照片色带，取样没画出来（空取样）时只会露出这块标记——
        // 「没画」与「画对」因此可以区分，不会把透出来的照片当成取样成功。
        int stripHeight = Math.Max(48, height * 8 / 100);
        int stripTop = Math.Max(32, (photoHeight - stripHeight) / 2);
        var markerColor = Color.Argb(255, 245, 225, 60);
        stage.AddBlock(overlay, markerColor, new Rect(0, stripTop - 24, width, stripTop + stripHeight + 24));
        var strip = stage.AddSampler(overlay, photoLayer.Backdrop, new Rect(0, stripTop, width, stripTop + stripHeight));
        strip.Continuous = true;

        var rate = ReadGlassRefreshRate(activity);
        var recorder = new GlassFrameIntervalRecorder(rate);
        strip.Recorder = recorder;

        await WaitAsync(() => strip.Width > 0 && strip.Height > 0, "取样条布局", 8000);
        var stripRect = GlassBackdropWindowRect(strip);
        var stripRegion = GlassBackdropInset(stripRect, 6);
        var markerRegion = new Rect(stripRect.Left + 6, stripRect.Top - 18, stripRect.Right - 6, stripRect.Top - 6);
        var photoRegion = GlassBackdropSubRect(photo, 0.10, 0.10, 0.20, 0.20);
        var photoBandRegion = GlassBackdropInset(GlassBackdropSubRect(photo, 0, 0.10, 1.0, 0.08), 6);

        using (var imageA = GlassBackdropCreateStripeImage(photo.Width, photo.Height, 0, colorSpace))
        using (var imageB = GlassBackdropCreateStripeImage(photo.Width, photo.Height, 1, colorSpace))
        {
            await GlassBackdropShowImageAsync(photo, imageA, 1, "首图");

            // 前置条件：原型主图自己得先出图，否则取样对照无从判读（不拿「全黑」冒充取样成功）。
            var photoReference = await GlassBackdropReadRegionAsync(photoRegion);
            using (var reference = photoReference.Buffer)
            {
                var referenceMean = GlassBackdropMeanRgb(reference);
                bool photoDrawn = reference != null && GlassBackdropBrightness(referenceMean) > 8;
                Check("glass 原型主图出图（前置条件）", photoDrawn,
                    reference is null
                        ? photoReference.Error
                        : $"照片区均值=({referenceMean.R:F1},{referenceMean.G:F1},{referenceMean.B:F1}) 区域={photoRegion}");
                if (!photoDrawn)
                {
                    GlassBackdropNote("glass 取样", "原型主图未出图：切图/缩放/模糊对照均不可判读，本组按前置条件未满足处理");
                    return;
                }
            }

            Check("glass 照片层录制内容", photoLayer.Backdrop.HasContent,
                $"HasContent={photoLayer.Backdrop.HasContent}"
                    + $" 录制={photoLayer.Backdrop.RecordedWidth}x{photoLayer.Backdrop.RecordedHeight}"
                    + $" 视图={photoLayer.Width}x{photoLayer.Height}");

            recorder.Restart();
            await Task.Delay(400);
            await GlassBackdropWaitDrawsAsync(strip, 3);

            // 负对照：摘掉取样源，取样条画不出任何内容，回读到的只能是条下的标记色块；
            // 同时证明「标记 ≠ 照片色带」，空取样才不会被当成取样成功。
            strip.SetSource(null);
            await GlassBackdropWaitDrawsAsync(strip, 3);
            var emptyCapture = await GlassBackdropReadRegionAsync(stripRegion);
            using var emptySample = emptyCapture.Buffer;
            strip.SetSource(photoLayer.Backdrop);
            await GlassBackdropWaitDrawsAsync(strip, 3);

            var baseline = await GlassBackdropReadRegionAsync(stripRegion);
            using var b0 = baseline.Buffer;
            var markerCapture = await GlassBackdropReadRegionAsync(markerRegion);
            using var marker = markerCapture.Buffer;
            var bandCapture = await GlassBackdropReadRegionAsync(photoBandRegion);
            using var band = bandCapture.Buffer;

            var emptyMean = GlassBackdropMeanRgb(emptySample);
            var sampleMean = GlassBackdropMeanRgb(b0);
            var markerMean = GlassBackdropMeanRgb(marker);
            var bandMean = GlassBackdropMeanRgb(band);
            double emptyToMarker = GlassBackdropMeanGap(emptyMean, markerMean);
            double markerToBand = GlassBackdropMeanGap(markerMean, bandMean);
            Check("glass 取样空态对照", emptySample != null && emptyToMarker <= 8 && markerToBand >= 40,
                $"空取样=({emptyMean.R:F1},{emptyMean.G:F1},{emptyMean.B:F1})"
                    + $" 标记块=({markerMean.R:F1},{markerMean.G:F1},{markerMean.B:F1}) Δ={emptyToMarker:F1}（≤8）"
                    + $"；标记与照片带差={markerToBand:F1}（≥40，空态不会被误判成取样成功）"
                    + $"；empty={emptyCapture.Error} marker={markerCapture.Error} band={bandCapture.Error}");

            double sampleToBand = GlassBackdropMeanGap(sampleMean, bandMean);
            double sampleToMarker = GlassBackdropMeanGap(sampleMean, markerMean);
            Check("glass 取样条像素与照片一致", !double.IsNaN(sampleToBand) && sampleToBand <= 8 && sampleToMarker >= 40,
                $"取样条=({sampleMean.R:F1},{sampleMean.G:F1},{sampleMean.B:F1})"
                    + $" 照片带=({bandMean.R:F1},{bandMean.G:F1},{bandMean.B:F1})"
                    + $" 标记块=({markerMean.R:F1},{markerMean.G:F1},{markerMean.B:F1})"
                    + $" 取样-照片={sampleToBand:F1}（≤8） 取样-标记={sampleToMarker:F1}（≥40）");

            if (GlassBackdropBrightness(sampleMean) <= 8)
            {
                GlassBackdropNote("glass 取样条内容", "取样条回读接近全黑而标记块与照片带都有色：照片层录制里没有 TextureView 内容，"
                    + "阶段 0 的「真模糊主图」前提不成立，需按计划的备选路径重新审批");
            }

            await GlassBackdropShowImageAsync(photo, imageB, 2, "切图");
            await GlassBackdropWaitDrawsAsync(strip, 3);
            var switched = await GlassBackdropReadRegionAsync(stripRegion);
            using var b1 = switched.Buffer;
            var mean1 = GlassBackdropMeanRgb(b1);
            double switchDelta = GlassBackdropMeanDelta(b0, b1);
            Check("glass 切图后取样实时更新", !double.IsNaN(switchDelta) && switchDelta >= GlassBackdropChangeThreshold,
                $"Δ均值={switchDelta:F1}（阈值 {GlassBackdropChangeThreshold:F0}） 切图后=({mean1.R:F1},{mean1.G:F1},{mean1.B:F1})");

            strip.SetEffect(GlassBackdropRecipe(strip, scope => scope.Vibrancy()));
            await GlassBackdropWaitDrawsAsync(strip, 3);
            var vibrant = await GlassBackdropReadRegionAsync(stripRegion);
            using var b2 = vibrant.Buffer;
            double chromaBase = GlassBackdropMeanChroma(b1);
            double chromaVibrant = GlassBackdropMeanChroma(b2);
            Check("glass vibrancy 提高饱和度", chromaBase > 0 && chromaVibrant >= chromaBase * 1.08,
                $"平均色度 {chromaBase:F1} → {chromaVibrant:F1}（≥1.08×）");

            strip.SetEffect(GlassBackdropRecipe(strip, scope => scope.Blur(scope.DpToPx(GlassBackdropBlurDp))));
            await GlassBackdropWaitDrawsAsync(strip, 3);
            var blurred = await GlassBackdropReadRegionAsync(stripRegion);
            using var b3 = blurred.Buffer;
            int sharpEdges = GlassBackdropStrongEdges(b1);
            int blurredEdges = GlassBackdropStrongEdges(b3);
            Check("glass 取样确实模糊", sharpEdges >= 40 && blurredEdges * 2 <= sharpEdges,
                $"强边缘像素 {sharpEdges} → {blurredEdges}（相邻像素通道差 ≥40 计一个，模糊后应减到一半以下）");

            strip.SetEffect(BackdropEffect.None);
            await GlassBackdropWaitDrawsAsync(strip, 2);
            var beforeZoom = await GlassBackdropReadRegionAsync(stripRegion);
            using var b4 = beforeZoom.Buffer;
            photo.ZoomToPhysical(2.0);
            await Task.Delay(600);
            await GlassBackdropWaitDrawsAsync(strip, 3);
            var afterZoom = await GlassBackdropReadRegionAsync(stripRegion);
            using var b5 = afterZoom.Buffer;
            double zoomDelta = GlassBackdropMeanDelta(b4, b5);
            Check("glass 缩放后取样实时更新", !double.IsNaN(zoomDelta) && zoomDelta >= GlassBackdropChangeThreshold,
                $"Δ均值={zoomDelta:F1}（阈值 {GlassBackdropChangeThreshold:F0}）");

            var stats = recorder.Compute();
            Check("glass 取样心跳", HeartbeatWithinBudget(stats),
                $"{stats.Describe()}；{rate.Describe()}；门槛 p95 ≤ {GlassBackdropHeartbeatBudgetMs:F0}ms 且最大 < "
                    + $"{GlassBackdropHeartbeatBudgetMs:F0}ms；口径是取样视图自身的重绘节奏，不是基线帧率对照");
        }

        await GlassBackdropSaveScreenshotAsync(GlassBackdropWindowRect(overlay), "glass-backdrop-sample");
        GlassBackdropNote("glass 取样目检", "取样条截图见上一条路径：模糊观感与产品令牌色的观感对照需作者手验");
        GlassBackdropNote("glass 取样范围", "阶段 0 只验单层取样与模糊/饱和度，未做折射、着色器高光、G2 形状与按压弹簧（阶段 3）");
    }

    /// <summary>
    /// LAYERS 入口（legacy 机制检查，只在显式点名 <c>layers</c> 时运行）：异常收在本组内，报告里记一条 FAIL 后正常返回。
    /// 三层取样属阶段 3，阶段 2 不据此下产品结论。
    /// </summary>
    private static async Task RunGlassLayersChecksAsync()
    {
        try
        {
            await RunGlassLayersChecksCoreAsync();
        }
        catch (Exception exception)
        {
            Check("glass 多层组（中止）", false, exception.GetType().Name + "：" + exception.Message);
        }
    }

    /// <summary>
    /// LAYERS：三层嵌套录制。屏幕层（全窗）→ 场景层 → 照片层（真实 AndroidPhotoView），
    /// 逐一验证按钮取到照片、卡片滚过后按钮取样实时更新、场景层取不到屏幕层独有内容、
    /// 屏幕层取得到、面板取到玻璃按钮的输出；最后记录心跳。
    /// </summary>
    private static async Task RunGlassLayersChecksCoreAsync()
    {
        var activity = _activity ?? throw new InvalidOperationException("glass 多层检查缺少宿主 Activity");
        GlassBackdropNote("glass 多层组定位",
            "legacy 迁移层机制检查：三层嵌套属阶段 3 范围，只在显式点名 layers 时运行；"
            + "阶段 2 的产品材质结论只取 product 组");
        GlassBackdropNote("glass 平台分级", BackdropCapabilities.Describe());

        if (BackdropCapabilities.Tier == BackdropTier.ColorLayer)
        {
            GlassBackdropNote("glass 多层取样", "API 31 以下不取样：本组不执行");
            return;
        }

        using var stage = new GlassBackdropStage(activity);
        if (!stage.IsAttached)
        {
            Check("glass 多层舞台", false, "窗口 DecorView 不可用，无法装配检查 UI");
            return;
        }

        var overlay = stage.Overlay;
        await WaitAsync(() => overlay.Width > 0 && overlay.Height > 0, "检查叠加层布局", 8000);
        int width = overlay.Width, height = overlay.Height;
        if (width < 320 || height < 480)
        {
            GlassBackdropNote("glass 多层取样", $"窗口 {width}x{height} 过小，跳过");
            return;
        }

        var screen = stage.AddLayer(overlay, Color.Argb(255, 8, 8, 8));
        var scene = stage.AddLayer(screen, Color.Argb(255, 10, 10, 10));

        int photoHeight = Math.Max(160, height * 42 / 100);
        var photoLayer = stage.AddLayer(scene, Color.Argb(255, 0, 0, 0), new Rect(0, 0, width, photoHeight));
        var photo = stage.AddPhoto(photoLayer, new Rect(0, 0, width, photoHeight));
        await WaitAsync(() => photo.Width > 0 && photo.Height > 0, "原型主图布局", 8000);
        await WaitAsync(() => photo.SurfaceReadyForChecks, "原型主图 EGL 表面就绪", 20000);

        var colorSpace = photo.SurfaceIsP3 ? ImageColorSpace.DisplayP3 : ImageColorSpace.Srgb;
        var cardColor = Color.Argb(255, 240, 140, 30);
        var markerColor = Color.Argb(255, 245, 225, 60);
        var surfaceTint = Color.Argb(160, 220, 40, 70);      // 按钮表面色（暖，偏不透明以便「画没画」可判读）
        var panelTint = Color.Argb(128, 0, 40, 120);         // 面板表面色（冷）：面板画没画按已知混合比判读

        using var photoImage = GlassBackdropCreateSolidImage(photo.Width, photo.Height, 20, 190, 240, colorSpace);
        await GlassBackdropShowImageAsync(photo, photoImage, 1, "实色照片");

        // 卡片压在照片上；按钮与面板只盖住中段，上下各留可见带：上带做卡片原始色对照，
        // 下带给照片层取样器做「空取样会露出卡片」的负对照。
        int cardTop = Math.Max(8, photoHeight * 38 / 100);
        int cardHeight = Math.Max(96, photoHeight * 22 / 100);
        int cardWidth = width * 55 / 100;
        int buttonTop = cardTop + (cardHeight * 22 / 100);
        int buttonHeight = cardHeight * 55 / 100;
        var card = stage.AddBlock(scene, cardColor, new Rect(0, cardTop, cardWidth, cardTop + cardHeight));

        // 屏幕层独有的标记块：位于场景层内容之外，用来判定「层归属」。
        int lowerTop = photoHeight;
        int lowerHeight = Math.Max(120, height - photoHeight);
        int markerTop = lowerTop + (lowerHeight * 20 / 100);
        int markerHeight = Math.Max(72, lowerHeight * 30 / 100);
        int markerLeft = width * 70 / 100;
        int markerWidth = width * 28 / 100;
        var marker = stage.AddBlock(screen, markerColor,
            new Rect(markerLeft, markerTop, markerLeft + markerWidth, markerTop + markerHeight));

        // 玻璃按钮：场景层取样方（压住卡片），再叠半透明表面色（模拟产品色层）。
        int buttonLeft = width * 2 / 100;
        int buttonWidth = width * 45 / 100;
        var button = stage.AddSampler(screen, scene.Backdrop,
            new Rect(buttonLeft, buttonTop, buttonLeft + buttonWidth, buttonTop + buttonHeight), surfaceTint);
        button.Continuous = true;

        // 窗口内面板：屏幕层取样方（与按钮右半重叠、右端伸到按钮外），自己再叠一层表面色——
        // 表面色是「面板真的画了」的水印：空面板只会露出下面未叠色的原始画面。
        int panelLeft = buttonLeft + (buttonWidth / 2);
        int panelWidth = buttonWidth * 9 / 10;
        var panel = stage.AddSampler(overlay, screen.Backdrop,
            new Rect(panelLeft, buttonTop, panelLeft + panelWidth, buttonTop + buttonHeight), panelTint);
        panel.Continuous = true;

        // 心跳挂在面板上：面板不在任何录制层内，避免把嵌套层的额外合成成本混进同一组计数。
        var rate = ReadGlassRefreshRate(activity);
        var recorder = new GlassFrameIntervalRecorder(rate);
        panel.Recorder = recorder;

        // 照片层取样器：落在卡片下带——取样画出内容时是照片（冷），空取样会露出卡片（暖）。
        var photoSampler = stage.AddSampler(overlay, photoLayer.Backdrop,
            new Rect(width * 6 / 100, cardTop + (cardHeight * 84 / 100),
                width * 24 / 100, cardTop + (cardHeight * 96 / 100)));

        // 层归属对照：同一块屏幕层独有的标记上，场景层取样方与屏幕层取样方各取一次。
        var sceneOnMarker = stage.AddSampler(overlay, scene.Backdrop,
            new Rect(markerLeft + (markerWidth * 32 / 100), markerTop + 8,
                markerLeft + (markerWidth * 46 / 100), markerTop + markerHeight - 8));
        var screenOnMarker = stage.AddSampler(overlay, screen.Backdrop,
            new Rect(markerLeft + (markerWidth * 52 / 100), markerTop + 8,
                markerLeft + (markerWidth * 80 / 100), markerTop + markerHeight - 8), panelTint);

        recorder.Restart();
        await Task.Delay(400);
        await GlassBackdropWaitDrawsAsync(panel, 3);

        Check("glass 三层录制", screen.Backdrop.HasContent && scene.Backdrop.HasContent && photoLayer.Backdrop.HasContent,
            $"屏幕 {screen.Backdrop.RecordedWidth}x{screen.Backdrop.RecordedHeight}"
                + $" 场景 {scene.Backdrop.RecordedWidth}x{scene.Backdrop.RecordedHeight}"
                + $" 照片层 {photoLayer.Backdrop.RecordedWidth}x{photoLayer.Backdrop.RecordedHeight}（视图 {width}x{height}）");

        // 1. 照片层取样：先做空取样负对照（露出卡片），再取真取样（应为照片色）。
        var photoSampleRegion = GlassBackdropInset(GlassBackdropWindowRect(photoSampler), 4);
        var photoVisibleRegion = GlassBackdropSubRect(photo, 0.10, 0.08, 0.20, 0.20);
        photoSampler.SetSource(null);
        await GlassBackdropWaitDrawsAsync(panel, 2);
        var underlayCapture = await GlassBackdropReadRegionAsync(photoSampleRegion);
        using var underlay = underlayCapture.Buffer;
        photoSampler.SetSource(photoLayer.Backdrop);
        await GlassBackdropWaitDrawsAsync(panel, 2);
        var sampledPhoto = await GlassBackdropReadRegionAsync(photoSampleRegion);
        var visiblePhoto = await GlassBackdropReadRegionAsync(photoVisibleRegion);
        using (var sampled = sampledPhoto.Buffer)
        using (var visible = visiblePhoto.Buffer)
        {
            var sampledMean = GlassBackdropMeanRgb(sampled);
            var visibleMean = GlassBackdropMeanRgb(visible);
            var underlayMean = GlassBackdropMeanRgb(underlay);
            double toPhoto = GlassBackdropMeanGap(sampledMean, visibleMean);
            double toUnderlay = GlassBackdropMeanGap(sampledMean, underlayMean);
            bool photoVisible = GlassBackdropBrightness(visibleMean) > 8;
            Check("glass 照片层取样像素", photoVisible && !double.IsNaN(toPhoto) && toPhoto <= 8 && toUnderlay >= 40,
                $"取样=({sampledMean.R:F1},{sampledMean.G:F1},{sampledMean.B:F1})"
                    + $" 照片=({visibleMean.R:F1},{visibleMean.G:F1},{visibleMean.B:F1})"
                    + $" 空态=({underlayMean.R:F1},{underlayMean.G:F1},{underlayMean.B:F1})"
                    + $" 取样-照片={toPhoto:F1}（≤8） 取样-空态={toUnderlay:F1}（≥40）"
                    + (sampled is null ? $"；{sampledPhoto.Error}" : string.Empty)
                    + (visible is null ? $"；{visiblePhoto.Error}" : string.Empty));

            if (!photoVisible)
            {
                GlassBackdropNote("glass 照片层取样", "照片自身可见区全黑：原型主图未出图，本项不可判读（不按通过记）");
            }
            else if (!double.IsNaN(toPhoto) && toPhoto > 8 && GlassBackdropBrightness(sampledMean) <= 8)
            {
                GlassBackdropNote("glass 照片层取样", "取样区域接近全黑而照片可见区有色：说明 TextureView 内容没有进 RenderNode 录制，"
                    + "阶段 0 的「真模糊主图」前提不成立，需按计划的备选路径重新审批");
            }
        }

        // 2. 层归属：场景层取不到屏幕层独有的标记块，屏幕层取得到。
        var markerReference = await GlassBackdropReadRegionAsync(GlassBackdropSubRect(marker, 0.05, 0.10, 0.15, 0.80));
        var sceneOnMarkerCapture = await GlassBackdropReadRegionAsync(
            GlassBackdropInset(GlassBackdropWindowRect(sceneOnMarker), 4));
        var screenOnMarkerCapture = await GlassBackdropReadRegionAsync(
            GlassBackdropInset(GlassBackdropWindowRect(screenOnMarker), 4));
        using (var reference = markerReference.Buffer)
        using (var fromScene = sceneOnMarkerCapture.Buffer)
        using (var fromScreen = screenOnMarkerCapture.Buffer)
        {
            var referenceMean = GlassBackdropMeanRgb(reference);
            var sceneMean = GlassBackdropMeanRgb(fromScene);
            var screenMean = GlassBackdropMeanRgb(fromScreen);
            Check("glass 场景层取样不含屏幕层内容",
                fromScene != null && GlassBackdropBrightness(sceneMean) <= 60 && Math.Abs(sceneMean.R - sceneMean.B) <= 20,
                $"场景层取样=({sceneMean.R:F1},{sceneMean.G:F1},{sceneMean.B:F1})，"
                    + $"标记块=({referenceMean.R:F1},{referenceMean.G:F1},{referenceMean.B:F1})");
            // 这个取样方叠了已知表面色：空取样会露出未叠色的标记块，与「标记块+表面色」的期望差一个整表面色，
            // 所以既能判「取到屏幕层」，也能判「真的画了」。
            var expectedScreen = GlassBackdropBlend(referenceMean, panelTint);
            double screenBlendGap = GlassBackdropMeanGap(screenMean, expectedScreen);
            double screenUnpainted = GlassBackdropMeanGap(screenMean, referenceMean);
            Check("glass 屏幕层取样含屏幕层内容", !double.IsNaN(screenBlendGap) && screenBlendGap <= 18 && screenUnpainted >= 40,
                $"屏幕层取样=({screenMean.R:F1},{screenMean.G:F1},{screenMean.B:F1})"
                    + $" 期望=({expectedScreen.R:F1},{expectedScreen.G:F1},{expectedScreen.B:F1}) Δ={screenBlendGap:F1}（≤18）"
                    + $" 未叠色差={screenUnpainted:F1}（≥40，空取样不会满足）");
        }

        // 3. 面板：按已知表面色的混合比判读。参照区两处——按钮内（面板压在按钮上）与按钮外（面板压卡片），
        //    后者不与按钮重叠；两者一起说明面板取样的是屏幕层（含按钮的合成结果），空面板会差出一个整表面色。
        var buttonVisible = await GlassBackdropReadRegionAsync(GlassBackdropSubRect(button, 0.03, 0.25, 0.30, 0.50));
        var cardVisible = await GlassBackdropReadRegionAsync(GlassBackdropSubRect(card, 0.05, 0.02, 0.30, 0.10));
        var panelOverButton = await GlassBackdropReadRegionAsync(GlassBackdropSubRect(panel, 0.05, 0.25, 0.15, 0.50));
        var panelOverCard = await GlassBackdropReadRegionAsync(GlassBackdropSubRect(panel, 0.60, 0.25, 0.12, 0.50));
        using (var buttonPixels = buttonVisible.Buffer)
        using (var cardPixels = cardVisible.Buffer)
        using (var overButtonPixels = panelOverButton.Buffer)
        using (var overCardPixels = panelOverCard.Buffer)
        {
            var buttonMean = GlassBackdropMeanRgb(buttonPixels);
            var cardMean = GlassBackdropMeanRgb(cardPixels);
            var overButtonMean = GlassBackdropMeanRgb(overButtonPixels);
            var overCardMean = GlassBackdropMeanRgb(overCardPixels);
            var expectedOverButton = GlassBackdropBlend(buttonMean, panelTint);
            var expectedOverCard = GlassBackdropBlend(cardMean, panelTint);
            double buttonBlendGap = GlassBackdropMeanGap(overButtonMean, expectedOverButton);
            double cardBlendGap = GlassBackdropMeanGap(overCardMean, expectedOverCard);
            double unpaintedGap = GlassBackdropMeanGap(overButtonMean, buttonMean);
            Check("glass 面板取到玻璃按钮输出",
                !double.IsNaN(buttonBlendGap) && buttonBlendGap <= 18
                    && !double.IsNaN(cardBlendGap) && cardBlendGap <= 18
                    && unpaintedGap >= 40,
                $"面板/按钮=({overButtonMean.R:F1},{overButtonMean.G:F1},{overButtonMean.B:F1})"
                    + $" 期望=({expectedOverButton.R:F1},{expectedOverButton.G:F1},{expectedOverButton.B:F1})"
                    + $" Δ={buttonBlendGap:F1}（≤18）"
                    + $"；面板/卡片=({overCardMean.R:F1},{overCardMean.G:F1},{overCardMean.B:F1})"
                    + $" 期望=({expectedOverCard.R:F1},{expectedOverCard.G:F1},{expectedOverCard.B:F1})"
                    + $" Δ={cardBlendGap:F1}（≤18，参照区在按钮外）"
                    + $"；与未叠色按钮差={unpaintedGap:F1}（≥40，空面板不会满足）");
        }

        // 4. 卡片从按钮下方滚过后，按钮取样实时更新（同一取样方、同一按钮区域）。
        var buttonRegion = GlassBackdropSubRect(button, 0.03, 0.25, 0.30, 0.50);
        var beforeScroll = await GlassBackdropReadRegionAsync(buttonRegion);
        var cardUnderButton = await GlassBackdropReadRegionAsync(GlassBackdropSubRect(card, 0.05, 0.02, 0.30, 0.10));
        card.TranslationY = cardHeight * 2;
        await Task.Delay(200);
        await GlassBackdropWaitDrawsAsync(panel, 3);
        var afterScroll = await GlassBackdropReadRegionAsync(buttonRegion);
        var photoUnderButton = await GlassBackdropReadRegionAsync(photoVisibleRegion);
        using (var beforePixels = beforeScroll.Buffer)
        using (var afterPixels = afterScroll.Buffer)
        using (var cardPixels = cardUnderButton.Buffer)
        using (var photoPixels = photoUnderButton.Buffer)
        {
            var beforeMean = GlassBackdropMeanRgb(beforePixels);
            var afterMean = GlassBackdropMeanRgb(afterPixels);
            var cardMean = GlassBackdropMeanRgb(cardPixels);
            var photoMean = GlassBackdropMeanRgb(photoPixels);
            // 滚前按钮下是卡片、滚后是照片：两个状态都按「下层 + 已知表面色」判读，
            // 空取样露出的是未叠色的原画面，会差出一个整表面色，不会被当成取样成功。
            var expectedBefore = GlassBackdropBlend(cardMean, surfaceTint);
            var expectedAfter = GlassBackdropBlend(photoMean, surfaceTint);
            double beforeGap = GlassBackdropMeanGap(beforeMean, expectedBefore);
            double afterGap = GlassBackdropMeanGap(afterMean, expectedAfter);
            double unpaintedBefore = GlassBackdropMeanGap(beforeMean, cardMean);
            double unpaintedAfter = GlassBackdropMeanGap(afterMean, photoMean);
            double scrollDelta = GlassBackdropMeanDelta(beforePixels, afterPixels);
            Check("glass 卡片滚过后按钮取样更新",
                !double.IsNaN(scrollDelta) && scrollDelta >= 20
                    && !double.IsNaN(beforeGap) && beforeGap <= 18 && unpaintedBefore >= 15
                    && !double.IsNaN(afterGap) && afterGap <= 18 && unpaintedAfter >= 15,
                $"滚前=({beforeMean.R:F1},{beforeMean.G:F1},{beforeMean.B:F1})"
                    + $" 期望=({expectedBefore.R:F1},{expectedBefore.G:F1},{expectedBefore.B:F1})"
                    + $" Δ={beforeGap:F1}（≤18） 未叠色差={unpaintedBefore:F1}（≥15）"
                    + $"；滚后=({afterMean.R:F1},{afterMean.G:F1},{afterMean.B:F1})"
                    + $" 期望=({expectedAfter.R:F1},{expectedAfter.G:F1},{expectedAfter.B:F1})"
                    + $" Δ={afterGap:F1}（≤18） 未叠色差={unpaintedAfter:F1}（≥15）"
                    + $"；Δ均值={scrollDelta:F1}（≥20） 卡片位移={cardHeight * 2}px");
        }

        var stats = recorder.Compute();
        Check("glass 多层心跳", HeartbeatWithinBudget(stats),
            $"{stats.Describe()}；{rate.Describe()}；门槛 p95 ≤ {GlassBackdropHeartbeatBudgetMs:F0}ms 且最大 < "
                + $"{GlassBackdropHeartbeatBudgetMs:F0}ms；口径是层外取样视图的重绘节奏，不是基线帧率对照");

        await GlassBackdropSaveScreenshotAsync(GlassBackdropWindowRect(overlay), "glass-backdrop-layers");
        GlassBackdropNote("glass 多层目检", "三层取样截图见上一条路径：照片/卡片/按钮/面板的观感与边缘过渡需作者手验");
        GlassBackdropNote("glass 多层范围", "阶段 0 只验三层录制与取样归属；折射、高光、阴影、G2 形状与弹簧属阶段 3，未纳入");
        GlassBackdropNote("glass 卡片滚动", "卡片以视图平移模拟滚动（真实列表甩动的掉帧率由宿主组的帧指标对照承担）；"
            + "心跳挂在层外面板上量取样节奏；嵌套层各自仍有录制与合成成本");
    }

    // ── NOTE 与回读 ───────────────────────────────────────────────────────────

    /// <summary>NOTE：无法自动判定的视觉/观感项，只写一行说明，不计入 PASS/FAIL。</summary>
    /// <param name="name">项名。</param>
    /// <param name="detail">说明。</param>
    private static void GlassBackdropNote(string name, string detail) => Lines.Add($"NOTE {name} — {detail}");

    /// <summary>整窗回读一块区域并读成托管像素副本；调用方负责释放返回的缓冲。</summary>
    /// <param name="region">窗口坐标下的区域。</param>
    /// <returns>像素副本与失败原因。</returns>
    private static async Task<(GlassPixelBuffer? Buffer, string Error)> GlassBackdropReadRegionAsync(Rect region)
    {
        if (region.Width() < 8 || region.Height() < 8) return (null, $"回读区域过小：{region}");

        using var result = await CaptureGlassWindowAsync(_activity!, sourceRect: region);
        if (!result.Ok || result.Bitmap is null) return (null, $"整窗回读失败：{result.Message}");

        var buffer = GlassPixelBuffer.TryRead(result.Bitmap, out string error);
        return buffer is null ? (null, $"像素读取失败：{error}") : (buffer, string.Empty);
    }

    /// <summary>把一块区域回读成 PNG 落盘，供作者目检；失败只写 NOTE。</summary>
    /// <param name="region">窗口坐标下的区域。</param>
    /// <param name="name">文件名前缀。</param>
    private static async Task GlassBackdropSaveScreenshotAsync(Rect region, string name)
    {
        try
        {
            using var result = await CaptureGlassWindowAsync(_activity!, sourceRect: region);
            if (!result.Ok || result.Bitmap is null)
            {
                GlassBackdropNote($"{name} 截图", $"回读失败：{result.Message}");
                return;
            }

            var directory = IOPath.Combine(_activity!.FilesDir!.AbsolutePath, "PhotoViewer");
            Directory.CreateDirectory(directory);
            var path = IOPath.Combine(directory, $"{name}-{AndroidAppCapabilities.EditionName}.png");
            using var stream = File.Create(path);
            result.Bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
            GlassBackdropNote($"{name} 截图", path);
        }
        catch (Exception exception)
        {
            GlassBackdropNote($"{name} 截图", $"{exception.GetType().Name}：{exception.Message}");
        }
    }

    // ── 几何与像素度量 ────────────────────────────────────────────────────────

    /// <summary>视图在窗口坐标下的矩形（<c>PixelCopy</c> 的整窗源用同一坐标系）。</summary>
    /// <param name="view">视图。</param>
    /// <returns>窗口矩形。</returns>
    private static Rect GlassBackdropWindowRect(View view)
    {
        var location = new int[2];
        view.GetLocationInWindow(location);
        return new Rect(location[0], location[1], location[0] + view.Width, location[1] + view.Height);
    }

    /// <summary>按比例取视图窗口矩形的一部分（避开边界混叠，取样对照只用内部像素）。</summary>
    /// <param name="view">视图。</param>
    /// <param name="left">左（0–1）。</param>
    /// <param name="top">上（0–1）。</param>
    /// <param name="width">宽（0–1）。</param>
    /// <param name="height">高（0–1）。</param>
    /// <returns>窗口矩形。</returns>
    private static Rect GlassBackdropSubRect(View view, double left, double top, double width, double height)
    {
        var rect = GlassBackdropWindowRect(view);
        int x = rect.Left + (int)Math.Round(rect.Width() * left);
        int y = rect.Top + (int)Math.Round(rect.Height() * top);
        int w = Math.Max(8, (int)Math.Round(rect.Width() * width));
        int h = Math.Max(8, (int)Math.Round(rect.Height() * height));
        return new Rect(x, y, Math.Min(rect.Right, x + w), Math.Min(rect.Bottom, y + h));
    }

    /// <summary>四边内缩，避开边界像素。</summary>
    /// <param name="rect">原矩形。</param>
    /// <param name="inset">内缩量（px）。</param>
    /// <returns>内缩后的矩形（可能为空）。</returns>
    private static Rect GlassBackdropInset(Rect rect, int inset)
        => new(rect.Left + inset, rect.Top + inset, Math.Max(rect.Left + inset, rect.Right - inset),
            Math.Max(rect.Top + inset, rect.Bottom - inset));

    /// <summary>区域平均 RGB（跳过内缩边界）；无像素时为 NaN。</summary>
    /// <param name="buffer">像素副本。</param>
    /// <param name="inset">内缩量（px）。</param>
    /// <returns>平均通道。</returns>
    private static (double R, double G, double B) GlassBackdropMeanRgb(GlassPixelBuffer? buffer, int inset = 3)
    {
        if (buffer is null) return (double.NaN, double.NaN, double.NaN);

        double red = 0, green = 0, blue = 0;
        int count = 0;
        for (int y = inset; y < buffer.Height - inset; y++)
        {
            for (int x = inset; x < buffer.Width - inset; x++)
            {
                if (!buffer.TryGetRgb8(x, y, out byte r, out byte g, out byte b)) continue;
                red += r;
                green += g;
                blue += b;
                count++;
            }
        }
        return count == 0 ? (double.NaN, double.NaN, double.NaN) : (red / count, green / count, blue / count);
    }

    /// <summary>亮度（三通道均值，8 位）。</summary>
    /// <param name="mean">平均通道。</param>
    /// <returns>亮度。</returns>
    private static double GlassBackdropBrightness((double R, double G, double B) mean)
        => double.IsNaN(mean.R) ? double.NaN : (mean.R + mean.G + mean.B) / 3;

    /// <summary>
    /// 心跳是否在预算内：样本够、p95 不超过预算、且最大值也低于预算。
    /// 只看 p95 会放过「偶发一次长阻塞」，所以最大值单列；两个读数都是取样视图自己的重绘节奏，
    /// 不是基线帧率对照，报告里不得当成帧率。
    /// </summary>
    /// <param name="stats">取样视图的心跳统计。</param>
    /// <returns>是否在预算内。</returns>
    private static bool HeartbeatWithinBudget(GlassFrameStats stats)
        => stats.HasSamples && stats.Count >= 20
            && stats.P95Ms <= GlassBackdropHeartbeatBudgetMs
            && stats.MaxMs < GlassBackdropHeartbeatBudgetMs;

    /// <summary>
    /// 把表面色按 alpha 叠在下层平均色上，用来算「这块面板应该长成什么样」。
    /// 与 <c>Canvas.DrawColor</c> 的 SRC_OVER 同口径：面板画了就会贴近期望，空面板会差出一个整表面色。
    /// </summary>
    /// <param name="underlay">下层（被取样到的）平均色。</param>
    /// <param name="tint">面板/按钮的表面色。</param>
    /// <returns>期望的平均色。</returns>
    private static (double R, double G, double B) GlassBackdropBlend((double R, double G, double B) underlay, Color tint)
    {
        double alpha = tint.A / 255.0;
        return (alpha * tint.R + (1 - alpha) * underlay.R,
            alpha * tint.G + (1 - alpha) * underlay.G,
            alpha * tint.B + (1 - alpha) * underlay.B);
    }

    /// <summary>两组平均色的通道绝对差均值（区域尺寸不同也能比；缺数据时为 NaN）。</summary>
    /// <param name="first">平均色 A。</param>
    /// <param name="second">平均色 B。</param>
    /// <returns>差值（8 位）。</returns>
    private static double GlassBackdropMeanGap((double R, double G, double B) first,
        (double R, double G, double B) second)
    {
        if (double.IsNaN(first.R) || double.IsNaN(second.R)) return double.NaN;
        return (Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B)) / 3;
    }

    /// <summary>两块同尺寸区域的通道绝对差均值；尺寸不同或缺数据时为 NaN。</summary>
    /// <param name="before">区域 A。</param>
    /// <param name="after">区域 B。</param>
    /// <returns>差值（8 位）。</returns>
    private static double GlassBackdropMeanDelta(GlassPixelBuffer? before, GlassPixelBuffer? after)
    {
        if (before is null || after is null) return double.NaN;
        if (before.Width != after.Width || before.Height != after.Height) return double.NaN;

        double sum = 0;
        int count = 0;
        for (int y = 3; y < before.Height - 3; y++)
        {
            for (int x = 3; x < before.Width - 3; x++)
            {
                if (!before.TryGetRgb8(x, y, out byte r0, out byte g0, out byte b0)) continue;
                if (!after.TryGetRgb8(x, y, out byte r1, out byte g1, out byte b1)) continue;
                sum += Math.Abs(r1 - r0) + Math.Abs(g1 - g0) + Math.Abs(b1 - b0);
                count += 3;
            }
        }
        return count == 0 ? double.NaN : sum / count;
    }

    /// <summary>平均色度（三通道极差的均值）：提饱和应上升，纯灰内容接近 0。</summary>
    /// <param name="buffer">像素副本。</param>
    /// <param name="inset">内缩量（px）。</param>
    /// <returns>平均色度（8 位）。</returns>
    private static double GlassBackdropMeanChroma(GlassPixelBuffer? buffer, int inset = 3)
    {
        if (buffer is null) return double.NaN;

        double sum = 0;
        int count = 0;
        for (int y = inset; y < buffer.Height - inset; y++)
        {
            for (int x = inset; x < buffer.Width - inset; x++)
            {
                if (!buffer.TryGetRgb8(x, y, out byte r, out byte g, out byte b)) continue;
                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                sum += max - min;
                count++;
            }
        }
        return count == 0 ? double.NaN : sum / count;
    }

    /// <summary>
    /// 强边缘像素数：相邻像素某通道差 ≥ 阈值的对数。模糊把集中的边界摊开，这个计数会显著下降，
    /// 而「平均梯度」在大面积上会被稀释，不适合当过零门槛。
    /// </summary>
    /// <param name="buffer">像素副本。</param>
    /// <param name="threshold">通道差阈值（8 位）。</param>
    /// <param name="inset">内缩量（px）。</param>
    /// <returns>强边缘像素数。</returns>
    private static int GlassBackdropStrongEdges(GlassPixelBuffer? buffer, int threshold = 40, int inset = 2)
    {
        if (buffer is null) return 0;

        int count = 0;
        for (int y = inset; y < buffer.Height - inset; y++)
        {
            for (int x = inset; x < buffer.Width - inset - 1; x++)
            {
                if (!buffer.TryGetRgb8(x, y, out byte r0, out byte g0, out byte b0)) continue;
                if (!buffer.TryGetRgb8(x + 1, y, out byte r1, out byte g1, out byte b1)) continue;
                if (Math.Abs(r1 - r0) >= threshold || Math.Abs(g1 - g0) >= threshold || Math.Abs(b1 - b0) >= threshold)
                    count++;
            }
        }
        return count;
    }

    // ── 原型素材与配方 ────────────────────────────────────────────────────────

    /// <summary>按效果配方求值一次，得到可跨帧复用的效果链快照。</summary>
    /// <param name="sampler">取样视图（提供密度与绘制尺寸）。</param>
    /// <param name="recipe">配方。</param>
    /// <returns>效果链快照。</returns>
    private static BackdropEffect GlassBackdropRecipe(GlassBackdropSampleView sampler, Action<BackdropEffectScope> recipe)
    {
        var context = BackdropDrawContext.ForView(sampler);
        var scope = new BackdropEffectScope();
        scope.Apply(recipe, context.Density, context.WidthPx, context.HeightPx);
        return scope.Build();
    }

    /// <summary>把一张图交给原型主图并等到它真的出帧（含适应态复位）。</summary>
    /// <param name="photo">原型主图。</param>
    /// <param name="image">图。</param>
    /// <param name="generation">代际。</param>
    /// <param name="label">步骤名。</param>
    private static async Task GlassBackdropShowImageAsync(AndroidPhotoView photo, DecodedImage image, int generation, string label)
    {
        int frames = photo.RenderedFrames;
        photo.SetImageForChecks(image, generation);
        photo.FitToView();
        await WaitAsync(() => photo.RenderedFrames > frames, $"原型主图出帧：{label}", 10000);
        await Task.Delay(120);
    }

    /// <summary>等到取样视图再画若干帧（保证读到当前状态已上屏）。</summary>
    /// <param name="sampler">取样视图。</param>
    /// <param name="draws">期望新增帧数。</param>
    /// <param name="timeoutMs">超时（ms）。</param>
    private static async Task GlassBackdropWaitDrawsAsync(GlassBackdropSampleView sampler, int draws, int timeoutMs = 4000)
    {
        int target = sampler.DrawCount + Math.Max(1, draws);
        await WaitAsync(() => sampler.DrawCount >= target, $"取样视图重绘 {draws} 帧", timeoutMs);
    }

    /// <summary>四条竖向色带的合成图（切图与缩放都会改变取样区域的平均色）。</summary>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <param name="variant">配色档（0/1 两档）。</param>
    /// <param name="colorSpace">标签（与表面同口径，避免额外转换）。</param>
    /// <returns>合成图（调用方持有租约）。</returns>
    private static DecodedImage GlassBackdropCreateStripeImage(int width, int height, int variant, ImageColorSpace colorSpace)
    {
        // 两档配色的通道均值差约 20（8 位）：切图后取样区域的平均色一定跟着变。
        int[] stripes = variant == 0
            ? [0xC83C3C, 0x3CBE5A, 0x4664D2, 0xD2BE46]
            : [0x3C3CD2, 0xD2783C, 0x3CD2D2, 0xDC3C5A];

        int stride = checked(width * 4);
        var pixels = DecodedImage.RentPixels(checked(stride * height));
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int color = stripes[Math.Min(stripes.Length - 1, x * stripes.Length / Math.Max(1, width))];
                int offset = row + (x * 4);
                pixels[offset] = (byte)((color >> 16) & 0xFF);
                pixels[offset + 1] = (byte)((color >> 8) & 0xFF);
                pixels[offset + 2] = (byte)(color & 0xFF);
                pixels[offset + 3] = 255;
            }
        }
        return DecodedImage.CreatePooled(pixels, width, height, stride, PixelEncoding.Rgba8, colorSpace, width, height);
    }

    /// <summary>实色合成图（层归属对照用：任何内部点的颜色都相同）。</summary>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <param name="red">红。</param>
    /// <param name="green">绿。</param>
    /// <param name="blue">蓝。</param>
    /// <param name="colorSpace">标签。</param>
    /// <returns>合成图（调用方持有租约）。</returns>
    private static DecodedImage GlassBackdropCreateSolidImage(int width, int height, byte red, byte green, byte blue,
        ImageColorSpace colorSpace)
    {
        int stride = checked(width * 4);
        var pixels = DecodedImage.RentPixels(checked(stride * height));
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
            pixels[offset + 3] = 255;
        }
        return DecodedImage.CreatePooled(pixels, width, height, stride, PixelEncoding.Rgba8, colorSpace, width, height);
    }
}
