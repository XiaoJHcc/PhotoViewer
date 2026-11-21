using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Android.Graphics;
using Android.Views;
using PhotoViewer.Android.Backdrop;
using PhotoViewer.Android.Core;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>pv-glass-stage product</c>：液态玻璃方案 1 阶段 2 的真实产品材质验收（SHELL-12@android）。
/// </summary>
/// <remarks>
/// <para>
/// 全部断言直接跑在根视图现有的照片层与共享材质上：不另建 <see cref="AndroidPhotoView"/>、不挂
/// <see cref="GlassBackdropStage"/>、不改写 <c>SurfaceIsP3</c> 与 <c>ExternalFootprint</c>。
/// 取样源就是根视图里包住主图的那个 <see cref="LayerBackdropLayout"/>。
/// </para>
/// <para>
/// 像素证明不用「均值变化」冒充模糊：同一帧内既测被材质覆盖的栏底，也测栏外未被覆盖的照片（直通对照），
/// 用 1px 条纹的高频振幅与阶跃边缘的 10%→90% 展开宽度同时否决「纯色层」与「锐利直通」。
/// 注入合成图走 <c>color</c> 同一条租约路径（<see cref="ShowColorSyntheticAsync"/> /
/// <see cref="RestoreColorSessionImageAsync"/>），并把视口压到物理 100%，使照片真实铺到栏位下方。
/// </para>
/// </remarks>
internal static partial class AndroidNativeChecks
{
    /// <summary>产品图案在主图视图两轴之外铺出的像素：保证物理 100% 下照片铺满整窗，条纹与边缘可任意定位。</summary>
    private const int GlassProductPatternMargin = 256;

    /// <summary>注入合成图后等界面层取用与窗口合成落地的墙钟（ms）。</summary>
    private const int GlassProductSettleMs = 240;

    /// <summary>等主图换帧与材质重绘的上限（ms）。</summary>
    private const int GlassProductMaterialWaitMs = 4000;

    /// <summary>
    /// 8 位亮度比较的量化容差：通道量化、色域转换（P3 回读）与合成取整合计；不用于放宽「有没有模糊」的判定。
    /// </summary>
    private const double GlassProductLumaTolerance = 8;

    /// <summary>黑白衬底亮度变化的相对下限：色层只做整体电平缩放，不该把 Δ 吃掉 40% 以上。</summary>
    private const double GlassProductDeltaRatio = 0.6;

    /// <summary>1px 条纹在材质内相对「同透射率直通」的振幅比上限：超过它就不能算被低通掉了。</summary>
    private const double GlassProductStripeAmplitudeRatio = 0.15;

    /// <summary>1px 条纹振幅的绝对下限（8 位）：低于这个量级时比率判定没有区分力，只能按绝对值判。</summary>
    private const double GlassProductStripeAmplitudeFloor = 6;

    /// <summary>栏外照片上同一条纹必须保留的最小振幅占满幅的比例（证明源确实是 1px 高频，不是已抹平的图）。</summary>
    private const double GlassProductSourceAmplitudeRatio = 0.6;

    /// <summary>物理 100% 直通边缘的 10%→90% 宽度上限（px）。</summary>
    private const double GlassProductSharpEdgeMaxPx = 3;

    /// <summary>栏底边缘至少要比直通边缘宽出多少 px（否决锐利直通）。</summary>
    private const double GlassProductMinEdgeGainPx = 5;

    /// <summary>
    /// 三色衬底实测读数（sRGB 8 位亮度；NaN 表示本次没测到）。只写进报告 NOTE，供
    /// <c>platforms.md</c> 的 SHELL-12@android 校准记录使用，不参与判定，也不改令牌。
    /// </summary>
    private static double _glassBarDarkLuma = double.NaN;
    private static double _glassBarGrayLuma = double.NaN;
    private static double _glassBarBrightLuma = double.NaN;
    private static double _glassExifDarkLuma = double.NaN;
    private static double _glassExifGrayLuma = double.NaN;
    private static double _glassExifBrightLuma = double.NaN;

    /// <summary>
    /// 栏底色层的合成参数：不透明度与透射率取 <see cref="ExpectedBarOverlay"/> 的实际 alpha
    /// （即 <c>color.bar.overlay@android</c> × <c>shell.bar.overlay.opacity@android</c>），色层亮度取它的 RGB。
    /// 黑白衬底的期望亮度与最小变化量全部由这一份推导，不另编一套颜色。
    /// </summary>
    private readonly record struct GlassTint(double Opacity, double Transmission, double Luma)
    {
        /// <summary>该色层压在给定衬底亮度上的期望亮度（8 位口径）。</summary>
        /// <param name="sourceLuma">衬底亮度（0–255）。</param>
        internal double Over(double sourceLuma) => Transmission * sourceLuma + Opacity * Luma;

        /// <summary>该色层下纯黑→纯白衬底的亮度变化。</summary>
        internal double SwatchDelta => Transmission * 255;

        /// <summary>报告用描述。</summary>
        internal string Describe() => $"不透明度={Opacity:F3} 透射率={Transmission:F3} 色层亮度={Luma:F1}";
    }

    /// <summary>按令牌颜色与不透明度合成色层，并拆成可推导阈值的形式（与产品实现同源，但独立计算）。</summary>
    /// <param name="argb">令牌颜色。</param>
    /// <param name="opacity">令牌不透明度；null 表示直接用颜色自带 alpha。</param>
    private static GlassTint GlassTintOf(uint argb, double? opacity = null)
    {
        int alpha = opacity is { } value
            ? Math.Clamp((int)Math.Round(255 * value), 0, 255)
            : (int)((argb >> 24) & 0xFF);
        var color = Color.Argb(alpha, (int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF),
            (int)(argb & 0xFF))!;
        double opacity01 = color.A / 255.0;
        return new GlassTint(opacity01, 1 - opacity01, GlassLuma(color.R, color.G, color.B));
    }

    /// <summary>栏底色层（<c>color.bar.overlay@android</c> × <c>shell.bar.overlay.opacity@android</c>）。</summary>
    private static GlassTint GlassBarOverlayTint()
        => GlassTintOf(DesignTokens.ColorBarOverlayAndroid, DesignTokens.ShellBarOverlayOpacityAndroid);

    /// <summary>带衬底 EXIF 的色层（<c>color.toolbar.block.background</c> × <c>shell.glass.android.exif.tint.opacity@android</c>）。</summary>
    private static GlassTint GlassExifTint()
        => GlassTintOf(DesignTokens.ColorToolbarBlockBackground, DesignTokens.ShellGlassAndroidExifTintOpacity);

    /// <summary>合成图案种类（全部为整幅铺开，物理 100% 下像素与源 1:1）。</summary>
    private enum GlassProductPattern
    {
        /// <summary>纯黑。</summary>
        FlatBlack,
        /// <summary>纯白。</summary>
        FlatWhite,
        /// <summary>sRGB 8 位 128 的 50% 灰（SHELL-12 三色衬底校准用）。</summary>
        FlatGray,
        /// <summary>周期 2 像素的黑白条纹（默认竖条纹，<c>horizontal</c> 为 true 时横条纹）。</summary>
        Stripes,
        /// <summary>阶跃边缘：<c>edgeX</c> 左侧黑、右侧白（<c>horizontal</c> 为 true 时上黑下白）。</summary>
        Edge,
    }

    /// <summary>
    /// 产品组：分级/结构 → 真实可见栏底的像素证明与动态跟随 → 带衬底 EXIF →（低版本）色层回退断言。
    /// 收尾无条件恢复会话图、适应态与被临时隐藏的视图。
    /// </summary>
    private static async Task RunGlassProductChecksAsync()
    {
        var root = _root!;
        var session = _session!;
        var material = root.PhotoMaterial;
        GlassHostNote($"产品材质分级 {BackdropCapabilities.Describe()}");
        GlassHostNote($"产品形态 form={root.Form} 行布局={_main!.IsRowLayout}"
            + $" 窗口={root.Width}x{root.Height} 密度={AndroidUiTheme.Density(_activity!):F2}");
        GlassHostNote($"共享效果层估算 footprint={material.FootprintBytes >> 10}KiB"
            + $"（层物理尺寸+模糊外扩，RGBA8；与主图纹理同一次上报，不重复计费）");
        _glassBarDarkLuma = _glassBarGrayLuma = _glassBarBrightLuma = double.NaN;
        _glassExifDarkLuma = _glassExifGrayLuma = _glassExifBrightLuma = double.NaN;

        CheckGlassProductStructure(root);
        bool filmRestored = false;
        bool filmWasVisible = _main.IsThumbnailViewVisible;
        // 产品组只用现有主图宿主：这两处进程级状态在任何时刻都不该被本次验收改写（只做留底对照）。
        var footprintBefore = AndroidMemoryBudgetProvider.ExternalFootprint;
        bool surfaceP3Before = AndroidDisplayCapabilities.SurfaceIsP3;
        try
        {
            if (!material.IsRealMaterial)
            {
                // API 24–30：不取样，只走正式色层；这里必须如实断言「没有模糊」，不得伪造 PASS。
                await CheckGlassColorLayerFallbackAsync(root);
                return;
            }

            var hidden = new List<(View View, ViewStates Visibility)>();
            bool hasBar = TryPickGlassBar(root, out var bar, out var barRect);
            if (!hasBar)
            {
                // 栏位可能被用户设置关掉：临时打开胶片栏让栏底出现（isolated 档默认也开，这里只兜底）。
                filmRestored = !filmWasVisible;
                if (filmRestored)
                {
                    _main.IsThumbnailViewVisible = true;
                    await Task.Delay(400);
                }
                hasBar = TryPickGlassBar(root, out bar, out barRect);
            }
            if (!hasBar)
            {
                GlassHostNote("产品栏底像素证明：当前形态没有任何可见栏底（宽或高不足 48px），"
                    + "未能执行像素证明；结构断言仍有效");
                Check("glass 产品 存在真实可见栏底", false,
                    "6 个栏底都不可见或尺寸过小，无法做像素证明");
            }
            else
            {
                try
                {
                    HideGlassBarContents(hidden, bar.Bar);
                    await Task.Delay(80);
                    await CheckGlassBarMaterialAsync(root, bar, barRect, hidden);
                }
                finally
                {
                    RestoreGlassOverlays(hidden);
                }
            }

            await CheckGlassExifProductAsync(root);

            GlassHostNote("三色衬底实测（sRGB 8 位亮度均值，供 platforms.md SHELL-12@android 校准；不改令牌）："
                + $"栏底 黑={GlassProductLuma(_glassBarDarkLuma)} 灰={GlassProductLuma(_glassBarGrayLuma)}"
                + $" 白={GlassProductLuma(_glassBarBrightLuma)}；"
                + $"EXIF 黑={GlassProductLuma(_glassExifDarkLuma)} 灰={GlassProductLuma(_glassExifGrayLuma)}"
                + $" 白={GlassProductLuma(_glassExifBrightLuma)}"
                + "（令牌推导期望：栏底 32.8/58.4/83.8，EXIF 27.2/52.8/78.2）");

            Check("glass 产品 未改写预算读数委托与表面色域（只用现有主图宿主）",
                ReferenceEquals(AndroidMemoryBudgetProvider.ExternalFootprint, footprintBefore)
                    && AndroidDisplayCapabilities.SurfaceIsP3 == surfaceP3Before,
                $"footprint 委托保持={ReferenceEquals(AndroidMemoryBudgetProvider.ExternalFootprint, footprintBefore)}"
                    + $" 表面P3 {surfaceP3Before}→{AndroidDisplayCapabilities.SurfaceIsP3}");
        }
        finally
        {
            await RestoreColorSessionImageAsync(session.Image, session.Generation);
            root.Photo.FitToView();
            if (filmRestored) _main.IsThumbnailViewVisible = filmWasVisible;
            await Task.Delay(120);
        }
    }

    /// <summary>
    /// 结构断言：照片层的 z 序与尺寸、主图宿主唯一、取样源身份、6 个栏底与两个 EXIF 块的材质接线、
    /// 配方取值来自令牌。
    /// </summary>
    /// <param name="root">主屏根视图。</param>
    private static void CheckGlassProductStructure(AndroidNativeRootView root)
    {
        var layer = root.PhotoLayer;
        var material = root.PhotoMaterial;
        bool real = material.IsRealMaterial;

        bool photoInsideLayer = IsGlassDescendant(layer, root.Photo);
        bool bottomMost = root.ChildCount > 0 && ReferenceEquals(root.GetChildAt(0), layer);
        Check("glass 产品 照片层是根视图最底层且包住主图", bottomMost && photoInsideLayer,
            $"child0={root.GetChildAt(0)?.GetType().Name ?? "none"} 主图在照片层内={photoInsideLayer}"
                + $" 层={layer.Width}x{layer.Height} 窗口={root.Width}x{root.Height}");
        int photos = CountGlassPhotoViews(root);
        Check("glass 产品 主图宿主唯一（没有第二个 AndroidPhotoView）", photos == 1, $"count={photos}");
        Check("glass 产品 照片层铺满窗口", layer.Width == root.Width && layer.Height == root.Height,
            $"层={layer.Width}x{layer.Height} 窗口={root.Width}x{root.Height}");
        Check("glass 产品 照片层录制开关与分级一致", layer.RecordsBackdrop == real,
            $"RecordsBackdrop={layer.RecordsBackdrop} tier={BackdropCapabilities.Tier}");

        if (real)
        {
            Check("glass 产品 取样源就是照片层的 Backdrop", ReferenceEquals(material.Source, layer.Backdrop),
                $"material.Source={(material.Source == null ? "null" : material.Source.GetType().Name)}"
                    + $" layer.Backdrop={layer.Backdrop.GetType().Name}");
            Check("glass 产品 照片层已录到整窗画面",
                layer.Backdrop.HasContent && layer.Backdrop.RecordedWidth == root.Width
                    && layer.Backdrop.RecordedHeight == root.Height,
                $"hasContent={layer.Backdrop.HasContent} 录制={layer.Backdrop.RecordedWidth}x"
                    + $"{layer.Backdrop.RecordedHeight} 窗口={root.Width}x{root.Height}");
        }
        else
        {
            Check("glass 产品 低版本无取样源、照片层不录制",
                material.Source == null && !layer.RecordsBackdrop,
                $"source={material.Source?.GetType().Name ?? "null"} records={layer.RecordsBackdrop}");
        }

        var badSource = new List<string>();
        var badColor = new List<string>();
        var expectedOverlay = ExpectedBarOverlay();
        foreach (var (name, bar) in root.BarScrimsForChecks)
        {
            bool sourceOk = real
                ? bar.UsesPhotoMaterialForChecks && ReferenceEquals(bar.BackdropSourceForChecks, layer.Backdrop)
                : !bar.UsesPhotoMaterialForChecks && bar.BackdropSourceForChecks == null;
            if (!sourceOk) badSource.Add(name);
            if (bar.ColorForChecks != expectedOverlay) badColor.Add(name);
        }
        Check("glass 产品 6 个栏底取样源一致（API31+ 同一照片层 / 低版本不取样）", badSource.Count == 0,
            badSource.Count == 0
                ? $"栏底={root.BarScrimsForChecks.Count} tier={BackdropCapabilities.Tier}"
                : "不一致：" + string.Join(",", badSource));
        Check("glass 产品 6 个栏底色层取 color.bar.overlay/opacity@android", badColor.Count == 0,
            badColor.Count == 0 ? $"期望={expectedOverlay}" : "不一致：" + string.Join(",", badColor));

        Check("glass 产品 配方取令牌（vibrancy 饱和度 / 模糊半径）",
            Math.Abs(material.VibrancySaturation - DesignTokens.ShellGlassAndroidBarVibrancySaturation) < 1e-6
                && Math.Abs(material.BlurRadiusDp - DesignTokens.ShellBarBlurRadiusAndroid) < 1e-6,
            $"实测 饱和度={material.VibrancySaturation} 半径={material.BlurRadiusDp}dp"
                + $" 令牌={DesignTokens.ShellGlassAndroidBarVibrancySaturation}/{DesignTokens.ShellBarBlurRadiusAndroid}dp"
                + $"（顺序：ColorControls → Blur）");

        CheckGlassExifStructure(root, real);
    }

    /// <summary>两个 EXIF 块的变体、衬底色层与 tint 令牌断言（像素证明另在 <see cref="CheckGlassExifProductAsync"/>）。</summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="real">当前是否走真实照片层材质。</param>
    private static void CheckGlassExifStructure(AndroidNativeRootView root, bool real)
    {
        var candidates = new List<(string Name, AndroidExifBlock Block)>
        {
            ("手机EXIF", root.PhoneExif),
            ("控制栏EXIF", root.ControlBar.ExifBlock),
        };
        var expectedTint = TokenLayer(DesignTokens.ColorToolbarBlockBackground, DesignTokens.ShellGlassAndroidExifTintOpacity);
        var expectedBackdrop = AndroidUiTheme.FromToken(DesignTokens.ColorToolbarBlockBackground);
        bool anyVisible = false;
        foreach (var (name, block) in candidates)
        {
            if (block.Visibility != ViewStates.Visible || block.Width < 1 || block.Height < 1) continue;
            anyVisible = true;
            Check($"glass 产品 {name} 衬底颜色与 tint 取令牌",
                block.BackdropColorForChecks == expectedBackdrop && block.MaterialTintForChecks == expectedTint,
                $"底色={block.BackdropColorForChecks} 期望={expectedBackdrop} tint={block.MaterialTintForChecks} 期望={expectedTint}");
            if (!block.HasBackdropForChecks)
            {
                bool bareOk = !block.UsesPhotoMaterialForChecks && block.BackdropSourceForChecks == null
                    && block.Background == null;
                Check($"glass 产品 {name} 无衬底变体不取样、保持透明", bareOk,
                    $"bare=true 取样={block.UsesPhotoMaterialForChecks}"
                        + $" source={block.BackdropSourceForChecks?.GetType().Name ?? "null"}"
                        + $" Background={(block.Background == null ? "null" : "非空")}");
                continue;
            }

            if (real)
            {
                Check($"glass 产品 {name} 带衬底变体取同一照片层并去掉旧不透明底",
                    block.UsesPhotoMaterialForChecks
                        && ReferenceEquals(block.BackdropSourceForChecks, root.PhotoLayer.Backdrop)
                        && block.Background == null,
                    $"取样={block.UsesPhotoMaterialForChecks} source="
                        + $"{block.BackdropSourceForChecks?.GetType().Name ?? "null"} Background={(block.Background == null ? "null" : "非空")}");
            }
            else
            {
                Check($"glass 产品 {name} 低版本保留不透明正式色层",
                    !block.UsesPhotoMaterialForChecks && block.Background != null,
                    $"取样={block.UsesPhotoMaterialForChecks} Background={(block.Background == null ? "null" : "非空")}");
            }
        }
        if (!anyVisible) GlassHostNote("glass 产品 EXIF：当前形态没有可见的 EXIF 块（结构断言未执行，像素证明跳过）");
    }

    /// <summary>
    /// 真实可见栏底的像素证明与动态跟随：白/黑衬底、1px 条纹低通、阶跃边缘展开，同帧对照栏外照片；
    /// 另核对切图与缩放后 <see cref="AndroidPhotoView.RenderedFrames"/> 与材质绘制计数同步推进。
    /// </summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="bar">被测栏底（真实产品视图）。</param>
    /// <param name="barRect">栏底窗口矩形（px）。</param>
    private static async Task CheckGlassBarMaterialAsync(AndroidNativeRootView root,
        (string Name, AndroidBarScrim Bar) bar, Rect barRect,
        List<(View View, ViewStates Visibility)> hidden)
    {
        var photo = root.Photo;
        var material = root.PhotoMaterial;
        double density = photo.Density;
        int radiusPx = (int)Math.Round(AndroidUiTheme.Dp(_activity!, material.BlurRadiusDp));
        int inset = Math.Max(4, (int)Math.Round(AndroidUiTheme.Dp(_activity!, 2)));
        // 左右各再避开 2×模糊半径：栏底取样被 Clamp 在照片层（=整窗）边界上，最左/最右 1–2px 是钳制外延，
        // 同帧 PNG 实测那里出现 49.8/68.0 的假振幅（中央 3.2 才是真实残差）。上下保持原 inset。
        int edgeInset = 2 * radiusPx;
        var window = new Rect(0, 0, root.Width, root.Height);
        var sample = new Rect(
            Math.Clamp(barRect.Left + edgeInset, 0, window.Right),
            Math.Clamp(barRect.Top + inset, 0, window.Bottom),
            Math.Clamp(barRect.Right - edgeInset, 0, window.Right),
            Math.Clamp(barRect.Bottom - inset, 0, window.Bottom));
        if (sample.Width() < 32 || sample.Height() < 8)
        {
            GlassHostNote($"产品栏底像素证明：{bar.Name} 内区仅 {sample.Width()}x{sample.Height()}px"
                + $"（左右已各排除 2×半径={edgeInset}px），无可用采样带");
            Check("glass 产品 栏底像素证明 采样带可用", false,
                $"{bar.Name} rect={barRect} 内区={sample} 左右排除={edgeInset}px");
            return;
        }
        GlassHostNote($"产品栏底取样：{bar.Name} rect={barRect} 内区={sample}"
            + $" 密度={density:F2} 模糊半径={material.BlurRadiusDp}dp≈{radiusPx}px"
            + $"（上下 inset={inset}px，左右各排除 2×半径={edgeInset}px：排除照片层 Clamp 边界外延）");

        // 同一帧的直通对照：栏外未被任何材质覆盖的照片（其余视图已隐藏，只剩照片层与被测栏底）。
        // 对照区必须整体绕开材质视图（这里就是被测栏底整块），不能只避开内缩后的采样带。
        var materialRects = GlassVisibleMaterialRects(root);
        if (!TryGlassControlRect(window, sample, materialRects, horizontalProfile: true, out var control))
        {
            GlassHostNote("产品栏底像素证明：找不到与被测栏底不重叠的栏外对照区，条纹/边缘对照降级为只测栏底");
        }
        else
        {
            GlassHostNote($"产品栏底直通对照区={control}（栏外照片，未覆盖任何材质；已绕开 {materialRects.Count} 个可见材质）");
        }

        var target = SurfaceImageColorSpace();
        int imageWidth = window.Width() + GlassProductPatternMargin;
        int imageHeight = window.Height() + GlassProductPatternMargin;
        int offsetX = (imageWidth - window.Width()) / 2;
        int offsetY = (imageHeight - window.Height()) / 2;
        // 亮度门槛全部由栏底色层的实际 alpha 与 RGB 推导（白/黑衬底的理论合成值），不另编一套颜色。
        var tint = GlassBarOverlayTint();
        double whiteTarget = tint.Over(255);
        double blackTarget = tint.Over(0);
        double brightMin = whiteTarget - GlassProductLumaTolerance;
        double darkMax = blackTarget + GlassProductLumaTolerance;
        double deltaMin = tint.SwatchDelta * GlassProductDeltaRatio;
        GlassHostNote($"栏底亮度门槛（由色层推导）：{tint.Describe()}"
            + $" 白衬底期望={whiteTarget:F1}（下限 {brightMin:F1}） 黑衬底期望={blackTarget:F1}（上限 {darkMax:F1}）"
            + $" Δ期望={tint.SwatchDelta:F1}（下限 {deltaMin:F1}）");

        // 1) 适应态 + 白色：栏位下方是主图区背景（黑），栏底应暗；同时确认材质随切图跟上。
        using var white = CreateGlassProductPattern(GlassProductPattern.FlatWhite, imageWidth, imageHeight, target)
            ?? throw new InvalidOperationException("白色图案创建失败");
        int framesBefore = photo.RenderedFrames;
        int drawsBefore = material.MaterialDrawCount;
        await ShowColorSyntheticAsync(white);
        await WaitGlassFrameAsync(root, framesBefore, "适应态白色");
        var darkAtFit = await MeasureGlassRectAsync(sample, hidden, bar.Bar);

        // 2) 物理 100% + 白色：照片铺到栏底下方，栏底应亮；同时覆盖「缩放/视口变化」的材质跟随。
        framesBefore = photo.RenderedFrames;
        drawsBefore = material.MaterialDrawCount;
        ZoomGlassPhotoToPhysical(photo);
        await WaitGlassFrameAsync(root, framesBefore, "缩放");
        bool zoomFollowed = photo.RenderedFrames > framesBefore;
        int zoomDraws = material.MaterialDrawCount;
        var bright = await MeasureGlassRectAsync(sample, hidden, bar.Bar);
        // 「跟随」以渲染代际推进 + 后续实际像素变化为准；材质 OnDraw 计数只作诊断（共享 RenderNode 引用
        // 可以让取样输出更新而不重新执行该 View 的 OnDraw，计数不增并不代表没跟随）。
        Check("glass 产品 缩放后材质跟随（渲染代际推进且像素随之变化）", zoomFollowed
                && !photo.IsFit && Math.Abs(photo.PhysicalScale - 1) < 1e-2,
            $"代际 {framesBefore}→{photo.RenderedFrames} physical={photo.PhysicalScale:0.####} fit={photo.IsFit}"
                + $" 材质OnDraw（诊断）{drawsBefore}→{zoomDraws}");
        Check("glass 产品 栏底取到照片（黑→白衬底亮度）",
            darkAtFit.Mean <= darkMax && bright.Mean >= brightMin
                && bright.Mean - darkAtFit.Mean >= deltaMin,
            $"适应态(黑底)={darkAtFit.Describe()} 100%(白底)={bright.Describe()}"
                + $" 门槛 暗≤{darkMax:F1} 亮≥{brightMin:F1} Δ≥{deltaMin:F1}"
                + $"（色层透射率 {tint.Transmission:F2}）");

        var effect = material.Effect;
        Check("glass 产品 效果快照含模糊外扩（配方真正生效）",
            effect.RenderEffect != null && Math.Abs(effect.PaddingPx - radiusPx) <= 1.5,
            $"RenderEffect={(effect.RenderEffect != null)} padding={effect.PaddingPx:F1}px 期望≈{radiusPx}px（8dp）");

        // 3) 切图：同视口切到纯黑，栏底应回到暗；渲染代际必须继续推进。
        using var black = CreateGlassProductPattern(GlassProductPattern.FlatBlack, imageWidth, imageHeight, target)
            ?? throw new InvalidOperationException("黑色图案创建失败");
        framesBefore = photo.RenderedFrames;
        drawsBefore = material.MaterialDrawCount;
        await ShowColorSyntheticAsync(black);
        ZoomGlassPhotoToPhysical(photo);
        await WaitGlassFrameAsync(root, framesBefore, "切图");
        bool cutFollowed = photo.RenderedFrames > framesBefore;
        int cutDraws = material.MaterialDrawCount;
        var darkAfterCut = await MeasureGlassRectAsync(sample, hidden, bar.Bar);
        // 取证：黑底（物理 100%）的整窗/区域截图，采样带与对照区各一张，供目检为何出现 16..238 的宽范围。
        await SaveGlassProductShotAsync(
            control.Width() > 0 ? GlassProductUnion(sample, control) : sample, "glass-product-bar-black",
            hidden, bar.Bar);
        if (control.Width() > 0)
            await SaveGlassProductShotAsync(control, "glass-product-bar-black-control", hidden, bar.Bar);
        Check("glass 产品 切图后材质跟随（渲染代际推进且像素随之变化）", cutFollowed,
            $"代际 {framesBefore}→{photo.RenderedFrames} 材质OnDraw（诊断）{drawsBefore}→{cutDraws}");
        Check("glass 产品 栏底跟随切图（白→黑衬底亮度）",
            darkAfterCut.Mean <= darkMax
                && bright.Mean - darkAfterCut.Mean >= deltaMin,
            $"100%(黑底)={darkAfterCut.Describe()} 上一张(白底)={bright.Describe()}"
                + $" 门槛 暗≤{darkMax:F1} Δ≥{deltaMin:F1} 采样带={sample} 对照区={control}");

        // 3b) 满窗 50% 灰（sRGB 128）：同一内区、同一套令牌推导，为 SHELL-12 三色衬底校准留实测读数。
        using var gray = CreateGlassProductPattern(GlassProductPattern.FlatGray, imageWidth, imageHeight, target)
            ?? throw new InvalidOperationException("灰底图案创建失败");
        await ShowGlassPatternAtPhysicalAsync(root, gray, photo);
        var grayAt100 = await MeasureGlassRectAsync(sample, hidden, bar.Bar);
        double grayTarget = tint.Over(128);
        _glassBarDarkLuma = darkAfterCut.Mean;
        _glassBarGrayLuma = grayAt100.Mean;
        _glassBarBrightLuma = bright.Mean;
        Check("glass 产品 栏底取到 50% 灰（按令牌推导）",
            Math.Abs(grayAt100.Mean - grayTarget) <= GlassProductLumaTolerance,
            $"100%(灰底)={grayAt100.Describe()} 期望={grayTarget:F1} 容差=±{GlassProductLumaTolerance}"
                + $"（令牌透射率 {tint.Transmission:F2}）采样带={sample}");

        // 4) 1px 条纹：栏底高频被压掉，栏外照片上的同一条纹保持满幅（同一帧）。
        // 单次回读不足以下结论：实测里不同回读之间效果层可能复用陈旧输出（振幅 18 与 22/178 交替出现），
        // 因此连续回读 3 次、每次都满足原低通阈值才判 PASS；阈值与配方不改。
        const int stripeReads = 3;
        using var stripes = CreateGlassProductPattern(GlassProductPattern.Stripes, imageWidth, imageHeight, target)
            ?? throw new InvalidOperationException("条纹图案创建失败");
        await ShowGlassPatternAtPhysicalAsync(root, stripes, photo);
        var stripeAmplitudes = new List<double>(stripeReads);
        var stripeMeans = new List<double>(stripeReads);
        GlassProductProfile stripeBar = default;
        for (int read = 0; read < stripeReads; read++)
        {
            // 第一次回读顺便把**同一张位图**落盘（glass-product-bar-stripes-read1），与读数同源。
            stripeBar = await MeasureGlassProfileAsync(sample, horizontalProfile: true, hidden, bar.Bar,
                read == 0 ? "glass-product-bar-stripes-read1" : null);
            stripeAmplitudes.Add(stripeBar.Amplitude);
            stripeMeans.Add(stripeBar.Mean);
            if (read + 1 < stripeReads) await Task.Delay(80);
        }
        var stripeControl = control.Width() > 0
            ? await MeasureGlassProfileAsync(control, horizontalProfile: true, hidden, bar.Bar,
                "glass-product-bar-stripes-control-read1")
            : default;
        await SaveGlassProductShotAsync(
            control.Width() > 0 ? GlassProductUnion(sample, control) : sample, "glass-product-bar-stripes",
            hidden, bar.Bar);
        if (control.Width() > 0)
            await SaveGlassProductShotAsync(control, "glass-product-bar-stripes-control", hidden, bar.Bar);
        // 「直通」在栏底应有的振幅 = 透射率 × 栏外振幅；低通之后应只剩它的一小部分。
        double stripePassthrough = tint.Transmission * stripeControl.Amplitude;
        double barLimit = Math.Max(GlassProductStripeAmplitudeFloor,
            GlassProductStripeAmplitudeRatio * stripePassthrough);
        double stripeWorst = 0;
        foreach (double amplitude in stripeAmplitudes) stripeWorst = Math.Max(stripeWorst, amplitude);
        Check("glass 产品 栏底低通（1px 条纹被压掉，连续 3 次回读）",
            stripeAmplitudes.Count == stripeReads && stripeWorst <= barLimit
                && (!stripeControl.HasSamples
                    || stripeControl.Amplitude >= 255 * GlassProductSourceAmplitudeRatio),
            $"栏底振幅(每次)={string.Join("/", stripeAmplitudes.ConvertAll(value => value.ToString("F1")))}"
                + $" 最差={stripeWorst:F1}（上限={barLimit:F1}，直通期望={stripePassthrough:F1}）"
                + $" 栏底均值(每次)={string.Join("/", stripeMeans.ConvertAll(value => value.ToString("F1")))}"
                + $" 栏外直通振幅={stripeControl.Amplitude:F1}"
                + $"（下限={255 * GlassProductSourceAmplitudeRatio:F1}）"
                + $" 剖面={stripeBar.Length}px 采样带={sample} 对照区={control}"
                + $" ｜ {GlassProductMaterialDraws(root)}");

        // 5) 阶跃边缘：栏底边缘展开宽度须与 8dp 令牌相容，且明显宽于栏外直通的锐利边缘。
        int splitX = offsetX + sample.CenterX();
        int splitY = offsetY + sample.CenterY();
        using var edge = CreateGlassProductPattern(GlassProductPattern.Edge, imageWidth, imageHeight, target,
            edgeX: splitX, edgeY: splitY)
            ?? throw new InvalidOperationException("边缘图案创建失败");
        await ShowGlassPatternAtPhysicalAsync(root, edge, photo);
        var edgeBar = await MeasureGlassProfileAsync(sample, horizontalProfile: true, hidden, bar.Bar,
            "glass-product-bar-edge-read1");
        var edgeControl = control.Width() > 0
            ? await MeasureGlassProfileAsync(control, horizontalProfile: true, hidden, bar.Bar)
            : default;
        await SaveGlassProductShotAsync(
            control.Width() > 0 ? GlassProductUnion(sample, control) : sample, "glass-product-bar-edge",
            hidden, bar.Bar);
        if (control.Width() > 0)
            await SaveGlassProductShotAsync(control, "glass-product-bar-edge-control", hidden, bar.Bar);
        double minEdge = Math.Max(6, 0.4 * radiusPx);
        double maxEdge = 3.5 * radiusPx;
        Check("glass 产品 栏底边缘展开与 8dp 模糊相容",
            !double.IsNaN(edgeBar.EdgeWidthPx) && edgeBar.EdgeWidthPx >= minEdge && edgeBar.EdgeWidthPx <= maxEdge,
            $"栏底边缘 10%→90%={edgeBar.EdgeWidthPx:F1}px 容许[{minEdge:F1},{maxEdge:F1}]px（半径 {radiusPx}px）"
                + $" 剖面={edgeBar.Length}px 幅度={edgeBar.Amplitude:F1} 分界=({splitX},{splitY})"
                + $" 采样带={sample} 对照区={control} ｜ {GlassProductMaterialDraws(root)}");
        Check("glass 产品 栏底边缘明显宽于直通（否决锐利直通）",
            !double.IsNaN(edgeBar.EdgeWidthPx)
                && (!edgeControl.HasSamples
                    || (edgeControl.EdgeWidthPx <= GlassProductSharpEdgeMaxPx
                        && edgeBar.EdgeWidthPx - edgeControl.EdgeWidthPx >= GlassProductMinEdgeGainPx)),
            $"栏底={edgeBar.EdgeWidthPx:F1}px 栏外直通={edgeControl.EdgeWidthPx:F1}px"
                + $"（直通≤{GlassProductSharpEdgeMaxPx}，差≥{GlassProductMinEdgeGainPx}）"
                + $" ｜ {GlassProductMaterialDraws(root)}");
    }

    /// <summary>
    /// 带衬底 EXIF 块的像素证明：用块内**无文字且不受圆角影响**的上缘带做与栏底相同的黑白/条纹/边缘测量；
    /// 几何上确实没有可信采样区时如实记 NOTE，绝不用结构断言冒充像素验收。
    /// </summary>
    /// <param name="root">主屏根视图。</param>
    private static async Task CheckGlassExifProductAsync(AndroidNativeRootView root)
    {
        var photo = root.Photo;
        var material = root.PhotoMaterial;
        var candidates = new List<(string Name, AndroidExifBlock Block)>
        {
            ("手机EXIF", root.PhoneExif),
            ("控制栏EXIF", root.ControlBar.ExifBlock),
        };
        (string Name, AndroidExifBlock Block)? picked = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Block.Visibility != ViewStates.Visible) continue;
            if (!candidate.Block.HasBackdropForChecks) continue;
            if (candidate.Block.Width < 1 || candidate.Block.Height < 1) continue;
            picked = candidate;
            break;
        }
        if (picked is not { } target)
        {
            GlassHostNote("产品 EXIF 像素证明：当前形态没有可见的带衬底 EXIF 块"
                + "（宽形态左右布局与手机横屏取无衬底变体，不取样），像素部分未执行；"
                + "结构断言已单独成立");
            return;
        }

        if (!TryGlassExifStrip(target.Block, out var strip, out string stripDetail))
        {
            GlassHostNote($"产品 EXIF 像素证明：{target.Name} 块内没有同时满足「无文字」与「不受圆角影响」的采样带"
                + $"（{stripDetail}），像素部分未执行；结构断言已单独成立，本条不作为像素验收证据");
            Check("glass 产品 EXIF 像素证明 采样带可用", false, $"{target.Name}：{stripDetail}");
            return;
        }
        GlassHostNote($"产品 EXIF 取样：{target.Name} 无文字带={strip}（{stripDetail}）");

        var window = new Rect(0, 0, root.Width, root.Height);
        var hidden = new List<(View View, ViewStates Visibility)>();
        try
        {
            HideGlassBarContents(hidden, target.Block);
            await Task.Delay(80);

            // 对照区必须整体绕开 EXIF 块（上缘采样带本身就在块内，只避开采样带会量到块内模糊值），
            // 同时不与其他可见材质相交；因此清单在隐藏无关视图之后再取。
            var materialRects = GlassVisibleMaterialRects(root);
            if (!TryGlassControlRect(window, strip, materialRects, horizontalProfile: true, out var control))
            {
                GlassHostNote("产品 EXIF 像素证明：找不到绕开 EXIF 块与其余可见材质的栏外对照区，条纹/边缘对照降级为只测块内");
            }
            else
            {
                GlassHostNote($"产品 EXIF 直通对照区={control}（栏外照片；已绕开 {materialRects.Count} 个可见材质）");
            }

            var colorSpace = SurfaceImageColorSpace();
            int imageWidth = window.Width() + GlassProductPatternMargin;
            int imageHeight = window.Height() + GlassProductPatternMargin;
            int offsetX = (imageWidth - window.Width()) / 2;
            // 亮度门槛同样由「color.toolbar.block.background × exif.tint.opacity」的 alpha 与 RGB 推导。
            var tint = GlassExifTint();
            double whiteTarget = tint.Over(255);
            double blackTarget = tint.Over(0);
            double brightMin = whiteTarget - GlassProductLumaTolerance;
            double darkMax = blackTarget + GlassProductLumaTolerance;
            double deltaMin = tint.SwatchDelta * GlassProductDeltaRatio;
            GlassHostNote($"EXIF 衬底亮度门槛（由色层推导）：{tint.Describe()}"
                + $" 白衬底期望={whiteTarget:F1}（下限 {brightMin:F1}） 黑衬底期望={blackTarget:F1}（上限 {darkMax:F1}）"
                + $" Δ期望={tint.SwatchDelta:F1}（下限 {deltaMin:F1}）");

            using var white = CreateGlassProductPattern(GlassProductPattern.FlatWhite, imageWidth, imageHeight, colorSpace)
                ?? throw new InvalidOperationException("白色图案创建失败");
            await ShowGlassPatternAtPhysicalAsync(root, white, photo);
            var bright = await MeasureGlassRectAsync(strip, hidden, target.Block);

            using var black = CreateGlassProductPattern(GlassProductPattern.FlatBlack, imageWidth, imageHeight, colorSpace)
                ?? throw new InvalidOperationException("黑色图案创建失败");
            await ShowGlassPatternAtPhysicalAsync(root, black, photo);
            var dark = await MeasureGlassRectAsync(strip, hidden, target.Block);
            Check("glass 产品 EXIF 衬底取到照片（黑→白亮度）",
                dark.Mean <= darkMax && bright.Mean >= brightMin
                    && bright.Mean - dark.Mean >= deltaMin,
                $"黑底={dark.Describe()} 白底={bright.Describe()}"
                    + $" 门槛 暗≤{darkMax:F1} 亮≥{brightMin:F1} Δ≥{deltaMin:F1}"
                    + $"（color.toolbar.block.background × {DesignTokens.ShellGlassAndroidExifTintOpacity} 之后，"
                    + $"透射率 {tint.Transmission:F2}）");

            // 满窗 50% 灰（sRGB 128）：与栏底同一套令牌推导，供 SHELL-12 三色校准取实测读数。
            using var gray = CreateGlassProductPattern(GlassProductPattern.FlatGray, imageWidth, imageHeight, colorSpace)
                ?? throw new InvalidOperationException("灰底图案创建失败");
            await ShowGlassPatternAtPhysicalAsync(root, gray, photo);
            var grayBlock = await MeasureGlassRectAsync(strip, hidden, target.Block);
            double grayTarget = tint.Over(128);
            _glassExifDarkLuma = dark.Mean;
            _glassExifGrayLuma = grayBlock.Mean;
            _glassExifBrightLuma = bright.Mean;
            Check("glass 产品 EXIF 衬底取到 50% 灰（按令牌推导）",
                Math.Abs(grayBlock.Mean - grayTarget) <= GlassProductLumaTolerance,
                $"灰底={grayBlock.Describe()} 期望={grayTarget:F1} 容差=±{GlassProductLumaTolerance}"
                    + $"（透射率 {tint.Transmission:F2}）带={strip}");

            using var stripes = CreateGlassProductPattern(GlassProductPattern.Stripes, imageWidth, imageHeight, colorSpace)
                ?? throw new InvalidOperationException("条纹图案创建失败");
            await ShowGlassPatternAtPhysicalAsync(root, stripes, photo);
            var stripeBlock = await MeasureGlassProfileAsync(strip, horizontalProfile: true, hidden, target.Block);
            var stripeControl = control.Width() > 0
                ? await MeasureGlassProfileAsync(control, horizontalProfile: true, hidden, target.Block)
                : default;
            double stripePassthrough = tint.Transmission * stripeControl.Amplitude;
            double barLimit = Math.Max(GlassProductStripeAmplitudeFloor,
                GlassProductStripeAmplitudeRatio * stripePassthrough);
            Check("glass 产品 EXIF 衬底低通（1px 条纹被压掉）",
                stripeBlock.Amplitude <= barLimit
                    && (!stripeControl.HasSamples
                        || stripeControl.Amplitude >= 255 * GlassProductSourceAmplitudeRatio),
                $"块内振幅={stripeBlock.Amplitude:F1}（上限={barLimit:F1}，直通期望={stripePassthrough:F1}）"
                    + $" 栏外直通振幅={stripeControl.Amplitude:F1}"
                    + $" 块内均值={stripeBlock.Mean:F1} 剖面={stripeBlock.Length}px"
                    + $" ｜ {GlassProductMaterialDraws(root)}");

            int splitX = offsetX + strip.CenterX();
            using var edge = CreateGlassProductPattern(GlassProductPattern.Edge, imageWidth, imageHeight, colorSpace,
                edgeX: splitX, edgeY: 0)
                ?? throw new InvalidOperationException("边缘图案创建失败");
            await ShowGlassPatternAtPhysicalAsync(root, edge, photo);
            var edgeBlock = await MeasureGlassProfileAsync(strip, horizontalProfile: true, hidden, target.Block);
            var edgeControl = control.Width() > 0
                ? await MeasureGlassProfileAsync(control, horizontalProfile: true, hidden, target.Block)
                : default;
            int radiusPx = (int)Math.Round(AndroidUiTheme.Dp(_activity!, material.BlurRadiusDp));
            double minEdge = Math.Max(6, 0.4 * radiusPx);
            double maxEdge = 3.5 * radiusPx;
            Check("glass 产品 EXIF 衬底边缘展开与 8dp 模糊相容",
                !double.IsNaN(edgeBlock.EdgeWidthPx) && edgeBlock.EdgeWidthPx >= minEdge && edgeBlock.EdgeWidthPx <= maxEdge,
                $"块内边缘 10%→90%={edgeBlock.EdgeWidthPx:F1}px 容许[{minEdge:F1},{maxEdge:F1}]px（半径 {radiusPx}px）"
                    + $" 剖面={edgeBlock.Length}px 幅度={edgeBlock.Amplitude:F1}"
                    + $" ｜ {GlassProductMaterialDraws(root)}");
            Check("glass 产品 EXIF 衬底边缘宽于直通（否决锐利直通）",
                !double.IsNaN(edgeBlock.EdgeWidthPx)
                    && (!edgeControl.HasSamples
                        || (edgeControl.EdgeWidthPx <= GlassProductSharpEdgeMaxPx
                            && edgeBlock.EdgeWidthPx - edgeControl.EdgeWidthPx >= GlassProductMinEdgeGainPx)),
                $"块内={edgeBlock.EdgeWidthPx:F1}px 栏外直通={edgeControl.EdgeWidthPx:F1}px"
                    + $" ｜ {GlassProductMaterialDraws(root)}");
        }
        finally
        {
            RestoreGlassOverlays(hidden);
        }
    }

    /// <summary>
    /// API 24–30 的正式色层回退：不录制、无取样源、6 个栏底只画色层、带衬底 EXIF 保留不透明色层；
    /// 并在 API 26+ 上用同一条纹图案证明栏底**没有**被低通（振幅与栏外直通一致），杜绝低版本伪造模糊 PASS。
    /// </summary>
    /// <param name="root">主屏根视图。</param>
    private static async Task CheckGlassColorLayerFallbackAsync(AndroidNativeRootView root)
    {
        var material = root.PhotoMaterial;
        var layer = root.PhotoLayer;
        GlassHostNote("glass 产品：API 31 以下不取样，按 SHELL-12 保留正式色层外观；本组只做色层回退断言");
        Check("glass 低版本 照片层不录制、无取样源",
            !layer.RecordsBackdrop && material.Source == null && !layer.Backdrop.HasContent,
            $"records={layer.RecordsBackdrop} source={material.Source?.GetType().Name ?? "null"}"
                + $" hasContent={layer.Backdrop.HasContent}");
        var expectedOverlay = ExpectedBarOverlay();
        var badBars = new List<string>();
        foreach (var (name, bar) in root.BarScrimsForChecks)
        {
            if (bar.UsesPhotoMaterialForChecks || bar.BackdropSourceForChecks != null
                || bar.ColorForChecks != expectedOverlay)
                badBars.Add(name);
        }
        Check("glass 低版本 6 个栏底只画令牌色层", badBars.Count == 0,
            badBars.Count == 0 ? $"色层={expectedOverlay}" : "不一致：" + string.Join(",", badBars));
        Check("glass 低版本 材质绘制计数为 0", material.MaterialDrawCount == 0,
            $"draws={material.MaterialDrawCount}");

        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            GlassHostNote("glass 低版本 像素证明：PixelCopy 需要 API 26，当前设备无法回读窗口像素，"
                + "只保留结构断言（不把缺读数当通过）");
            return;
        }

        if (!TryPickGlassBar(root, out var bar2, out var barRect))
        {
            GlassHostNote("glass 低版本 像素证明：当前形态没有可见栏底，像素部分未执行");
            return;
        }
        var window = new Rect(0, 0, root.Width, root.Height);
        int inset = Math.Max(4, (int)Math.Round(AndroidUiTheme.Dp(_activity!, 2)));
        var sample = new Rect(barRect.Left + inset, barRect.Top + inset, barRect.Right - inset, barRect.Bottom - inset);
        if (sample.Width() < 32 || sample.Height() < 8)
        {
            GlassHostNote($"glass 低版本 像素证明：{bar2.Name} 内区仅 {sample.Width()}x{sample.Height()}px，跳过");
            return;
        }
        var hidden = new List<(View View, ViewStates Visibility)>();
        try
        {
            HideGlassBarContents(hidden, bar2.Bar);
            await Task.Delay(80);
            var colorSpace = SurfaceImageColorSpace();
            int imageWidth = window.Width() + GlassProductPatternMargin;
            int imageHeight = window.Height() + GlassProductPatternMargin;
            using var stripes = CreateGlassProductPattern(GlassProductPattern.Stripes, imageWidth, imageHeight, colorSpace)
                ?? throw new InvalidOperationException("条纹图案创建失败");
            await ShowGlassPatternAtPhysicalAsync(root, stripes, root.Photo);
            var barProfile = await MeasureGlassProfileAsync(sample, horizontalProfile: true, hidden, bar2.Bar);
            var materialRects = GlassVisibleMaterialRects(root);
            var controlProfile = TryGlassControlRect(window, sample, materialRects, horizontalProfile: true,
                out var control)
                ? await MeasureGlassProfileAsync(control, horizontalProfile: true, hidden, bar2.Bar)
                : default;
            // 直通时栏底振幅 = 透射率 × 栏外振幅（本例 0.20 × 满幅 ≈ 51）；被低通掉会掉到接近 0。
            // 上下界同时卡住：低于下限说明高频没了（伪造模糊），高于上限说明色层没铺（或没按令牌合成）。
            var tint = GlassBarOverlayTint();
            double expected = tint.Transmission * (controlProfile.HasSamples ? controlProfile.Amplitude : 255);
            double lower = expected * 0.6;
            double upper = expected * 1.5 + GlassProductLumaTolerance;
            Check("glass 低版本 栏底保持直通色层（不伪造模糊）",
                barProfile.Amplitude >= lower && barProfile.Amplitude <= upper,
                $"{bar2.Name} 栏底振幅={barProfile.Amplitude:F1} 直通期望={expected:F1} 容许[{lower:F1},{upper:F1}]"
                    + $" 栏外振幅={controlProfile.Amplitude:F1}"
                    + $"（色层不透明度={tint.Opacity:F2} → 只被压暗、不被低通）");
        }
        finally
        {
            RestoreGlassOverlays(hidden);
        }
    }

    /// <summary>
    /// 把主图切到给定图案、压到物理 100% 居中，并等主图真的出一帧新内容与窗口落地
    /// （条纹/边缘测量要求取样内容确实已经跟上本次注入）。
    /// </summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="image">已创建的合成图（本方法只上屏，不接管租约）。</param>
    /// <param name="photo">主图视图。</param>
    private static async Task ShowGlassPatternAtPhysicalAsync(AndroidNativeRootView root, DecodedImage image,
        AndroidPhotoView photo)
    {
        int framesBefore = photo.RenderedFrames;
        await ShowColorSyntheticAsync(image);
        ZoomGlassPhotoToPhysical(photo);
        await WaitGlassFrameAsync(root, framesBefore, "图案切换");
        GlassHostNote($"图案注入后 {GlassProductMaterialDraws(root)}");
    }

    /// <summary>适应态切到物理 100% 居中并按整数像素对齐照片原点（避免线性采样混入相邻纹素）。</summary>
    /// <param name="photo">主图视图。</param>
    private static void ZoomGlassPhotoToPhysical(AndroidPhotoView photo)
    {
        photo.Viewport.ZoomToPhysical(1);
        photo.Viewport.CenterOnNormalizedPoint(0.5, 0.5);
        AlignGlassImageOrigin(photo);
        photo.Draw();
    }

    /// <summary>
    /// 等主图真的出一帧新内容并按一帧时长落地。**不把共享材质的 OnDraw 计数当硬条件**：
    /// 取样方与照片层共用同一个 <c>RenderNode</c> 引用，取样输出可以在不重新执行该 View 的 OnDraw 时更新，
    /// 计数不增并不等于材质没跟上（真机白底已正确、计数未增即为此）。是否跟随一律以渲染代际推进 + 后续
    /// 实际像素变化为准，计数只写进报告作诊断。
    /// </summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="framesBefore">调用前的主图渲染代际。</param>
    /// <param name="label">步骤名（超时时写进日志）。</param>
    private static async Task WaitGlassFrameAsync(AndroidNativeRootView root, int framesBefore, string label)
    {
        var photo = root.Photo;
        var deadline = DateTime.UtcNow.AddMilliseconds(GlassProductMaterialWaitMs);
        while (photo.RenderedFrames <= framesBefore && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        if (DateTime.UtcNow >= deadline)
            Console.WriteLine($"[NativeCheck] glass 产品 {label}：等待主图新帧超时"
                + $"（代际 {framesBefore}→{photo.RenderedFrames}）");
        await Task.Delay(GlassProductSettleMs);
    }

    /// <summary>
    /// 一个采样带的亮度统计（8 位 sRGB 亮度，权重与 colorControls 矩阵一致）。
    /// <see cref="Bright"/> / <see cref="Dark"/> 是接近满白/接近全黑的像素数，用来区分
    /// 「材质没盖住这块」与「衬底图没铺到」——黑底测量出现宽范围时靠它定位。
    /// </summary>
    private readonly record struct GlassProductStats(double Mean, double Min, double Max, double Amplitude,
        int Area, int Bright, int Dark)
    {
        /// <summary>报告用描述。</summary>
        internal string Describe()
            => $"均值={Mean:F1} 幅度={Amplitude:F1} 范围=[{Min:F0},{Max:F0}]"
                + $" 亮像素(≥150)={Percent(Bright)} 暗像素(≤60)={Percent(Dark)} 像素={Area}";

        private string Percent(int count) => Area <= 0 ? "n/a" : $"{100.0 * count / Area:F1}%";
    }

    /// <summary>一条 1D 亮度剖面的统计（含 10%→90% 边缘宽度）。</summary>
    private readonly record struct GlassProductProfile(double Mean, double Amplitude, double Rms, double EdgeWidthPx, int Length)
    {
        /// <summary>是否有可用剖面。</summary>
        internal bool HasSamples => Length > 0;

        /// <summary>报告用描述。</summary>
        internal string Describe()
            => $"n={Length} 均值={Mean:F1} 振幅={Amplitude:F1} rms={Rms:F1} 边缘={EdgeWidthPx:F1}px";
    }

    /// <summary>回读一个窗口矩形并给出平均亮度（用于黑白衬底的「取到照片」判定）。每次回读前先重新压住栏位内容。</summary>
    /// <param name="windowRect">窗口坐标矩形（px）。</param>
    /// <param name="hidden">隐藏清单（贯穿整段测量，收尾统一还原）。</param>
    /// <param name="keep">被测材质视图（自身与祖先链保持可见）。</param>
    private static async Task<GlassProductStats> MeasureGlassRectAsync(Rect windowRect,
        List<(View View, ViewStates Visibility)> hidden, View keep)
    {
        await RehideGlassBarContentsAsync(hidden, keep);
        var capture = await CaptureGlassRectAsync(windowRect);
        try
        {
            if (capture.Pixels == null || capture.Capture == null) throw new InvalidOperationException($"回读失败：{capture.Detail}");
            var rect = GlassProductLocalRect(windowRect, capture.Capture);
            double sum = 0, min = 255, max = 0;
            int count = 0, bright = 0, dark = 0;
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                for (int x = rect.Left; x < rect.Right; x++)
                {
                    if (!capture.Pixels.TryGetRgb8(x, y, out byte r, out byte g, out byte b)) continue;
                    double luma = GlassLuma(r, g, b);
                    sum += luma;
                    if (luma < min) min = luma;
                    if (luma > max) max = luma;
                    if (luma >= 150) bright++;
                    if (luma <= 60) dark++;
                    count++;
                }
            }
            if (count == 0) throw new InvalidOperationException($"采样区为空：{windowRect}");
            return new GlassProductStats(sum / count, min, max, max - min, count, bright, dark);
        }
        finally
        {
            capture.Pixels?.Dispose();
        }
    }

    /// <summary>
    /// 回读一个窗口矩形并取一条 1D 亮度剖面（跨 3–5 条线平均，降噪不改变振幅口径）。
    /// 每次回读前先重新压住栏位内容：切图与布局会让产品栏位内容重新显示，污染测量。
    /// </summary>
    /// <param name="windowRect">窗口坐标矩形（px）。</param>
    /// <param name="horizontalProfile">true 沿水平方向取剖面（竖条纹/竖直边缘），false 沿垂直方向。</param>
    /// <param name="hidden">隐藏清单（贯穿整段测量，收尾统一还原）。</param>
    /// <param name="keep">被测材质视图（自身与祖先链保持可见）。</param>
    private static async Task<GlassProductProfile> MeasureGlassProfileAsync(Rect windowRect, bool horizontalProfile,
        List<(View View, ViewStates Visibility)> hidden, View keep, string? shotName = null)
    {
        await RehideGlassBarContentsAsync(hidden, keep);
        var capture = await CaptureGlassRectAsync(windowRect, shotName);
        try
        {
            if (capture.Pixels == null || capture.Capture == null) throw new InvalidOperationException($"回读失败：{capture.Detail}");
            var rect = GlassProductLocalRect(windowRect, capture.Capture);
            var samples = GlassProfileSamples(capture.Pixels, rect, horizontalProfile);
            if (samples.Length == 0) throw new InvalidOperationException($"剖面为空：{windowRect}");
            double sum = 0, min = double.MaxValue, max = double.MinValue;
            foreach (double value in samples)
            {
                sum += value;
                if (value < min) min = value;
                if (value > max) max = value;
            }
            double mean = sum / samples.Length;
            double squares = 0;
            foreach (double value in samples) squares += (value - mean) * (value - mean);
            return new GlassProductProfile(mean, max - min, Math.Sqrt(squares / samples.Length),
                GlassProfileEdgeWidth(samples), samples.Length);
        }
        finally
        {
            capture.Pixels?.Dispose();
        }
    }

    /// <summary>
    /// 取一条亮度剖面：沿剖面方向逐点取「垂直方向若干条线」的平均。每条线按剖面交叉轴均匀取 3 条（最多 5 条），
    /// 既压单像素噪声又不跨越带外区域。
    /// </summary>
    /// <param name="pixels">回读像素（本矩形坐标）。</param>
    /// <param name="rect">采样矩形（回读缓冲坐标）。</param>
    /// <param name="horizontalProfile">true 沿 x，false 沿 y。</param>
    /// <returns>亮度剖面；无有效像素为空数组。</returns>
    private static double[] GlassProfileSamples(GlassPixelBuffer pixels, Rect rect, bool horizontalProfile)
    {
        int length = horizontalProfile ? rect.Width() : rect.Height();
        int cross = horizontalProfile ? rect.Height() : rect.Width();
        if (length < 2 || cross < 1) return [];
        int lines = Math.Min(5, cross);
        var samples = new double[length];
        for (int index = 0; index < length; index++)
        {
            double sum = 0;
            int count = 0;
            for (int line = 0; line < lines; line++)
            {
                int crossIndex = cross == 1 ? 0 : (int)Math.Round(line * (cross - 1) / (double)(lines - 1));
                int x = horizontalProfile ? rect.Left + index : rect.Left + crossIndex;
                int y = horizontalProfile ? rect.Top + crossIndex : rect.Top + index;
                if (!pixels.TryGetRgb8(x, y, out byte r, out byte g, out byte b)) continue;
                sum += GlassLuma(r, g, b);
                count++;
            }
            samples[index] = count == 0 ? 0 : sum / count;
        }
        return samples;
    }

    /// <summary>
    /// 阶跃边缘的 10%→90% 展开宽度：以剖面首尾各 1/8 的均值当低/高电平，取 10% 与 90% 阈值上的跨界位置差。
    /// 同像素硬边（<c>at10 == at90</c>）就是 0px——栏外直通对照必须能读到 0，不能返回 NaN。
    /// 只有阈值找不到、或 90% 落在 10% 之前（剖面非单调阶跃）才返回 NaN；电平差不足 30 说明没有边缘，
    /// 同样返回 NaN，不把「没有边缘」当成通过。
    /// </summary>
    /// <param name="samples">亮度剖面。</param>
    /// <returns>边缘宽度（px，硬边为 0）；判不出边缘时为 NaN。</returns>
    private static double GlassProfileEdgeWidth(double[] samples)
    {
        if (samples.Length < 16) return double.NaN;
        int edgeCount = Math.Max(2, samples.Length / 8);
        double lowStart = 0, highStart = 0;
        for (int index = 0; index < edgeCount; index++)
        {
            lowStart += samples[index];
            highStart += samples[samples.Length - 1 - index];
        }
        lowStart /= edgeCount;
        highStart /= edgeCount;
        if (Math.Abs(highStart - lowStart) < 30) return double.NaN;
        bool rising = highStart > lowStart;
        double low = rising ? lowStart : highStart;
        double high = rising ? highStart : lowStart;
        int at10 = -1, at90 = -1;
        for (int index = 0; index < samples.Length; index++)
        {
            double value = samples[index];
            if (at10 < 0 && value >= low + (high - low) * 0.1) at10 = index;
            if (at90 < 0 && value >= low + (high - low) * 0.9)
            {
                at90 = index;
                break;
            }
        }
        if (at10 < 0 || at90 < 0 || at90 < at10) return double.NaN;
        return at90 - at10;
    }

    /// <summary>8 位 sRGB 亮度（权重与 colorControls 矩阵的 0.213/0.715/0.072 同源）。</summary>
    private static double GlassLuma(byte r, byte g, byte b) => 0.213 * r + 0.715 * g + 0.072 * b;

    /// <summary>回读一个窗口矩形（8 位、目标色域与当前表面一致）。</summary>
    /// <param name="windowRect">窗口坐标矩形。</param>
    private static async Task<(GlassPixelBuffer? Pixels, Rect? Capture, string Detail)> CaptureGlassRectAsync(
        Rect windowRect, string? shotName = null)
    {
        using var capture = await CaptureGlassWindowAsync(_activity!, GlassCapturePixelFormat.Argb8888,
            GlassWindowColorSpace(SurfaceImageColorSpace()), windowRect);
        if (!capture.Ok)
            return (null, null, $"{capture.Failure}：{capture.Message}");
        // 截图取的就是即将用于统计的这一张位图：诊断图与读数同一帧，不会各说各话。
        if (!string.IsNullOrEmpty(shotName) && capture.Bitmap != null)
            SaveGlassProductShotBitmap(capture.Bitmap, shotName);
        var pixels = GlassPixelBuffer.TryRead(capture.Bitmap!, out string error);
        return pixels == null
            ? (null, null, error)
            : (pixels, capture.SourceRect.ToRect(), $"{capture.SourceRect.Describe()} {capture.ElapsedMs:F1}ms");
    }

    /// <summary>
    /// 把**同一张**回读位图落成 PNG 供目检（与本次统计同源，避免两次回读各说各话）。
    /// 命名与目录沿用 <c>GlassBackdropSaveScreenshotAsync</c>：<c>files/PhotoViewer/&lt;名字&gt;-&lt;edition&gt;.png</c>。
    /// </summary>
    /// <param name="bitmap">本次回读的位图（所有权仍归调用方）。</param>
    /// <param name="name">文件名前缀。</param>
    private static void SaveGlassProductShotBitmap(Bitmap bitmap, string name)
    {
        try
        {
            var directory = IOPath.Combine(_activity!.FilesDir!.AbsolutePath, "PhotoViewer");
            Directory.CreateDirectory(directory);
            var path = IOPath.Combine(directory, $"{name}-{AndroidAppCapabilities.EditionName}.png");
            using var stream = File.Create(path);
            bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
            GlassHostNote($"glass 产品 {name} 截图（与统计同一回读）={path}");
        }
        catch (Exception exception)
        {
            GlassHostNote($"glass 产品 {name} 截图失败 — {exception.GetType().Name}：{exception.Message}");
        }
    }

    /// <summary>窗口坐标矩形 → 回读缓冲坐标（回读矩形可能被系统裁剪）。</summary>
    private static Rect GlassProductLocalRect(Rect windowRect, Rect capture)
        => new(windowRect.Left - capture.Left, windowRect.Top - capture.Top,
            windowRect.Right - capture.Left, windowRect.Bottom - capture.Top);

    /// <summary>
    /// 栏外直通对照区：与被测采样带同尺寸，沿剖面交叉轴让开**整个材质视图**（不是只让开采样带）并完全落在窗口内。
    /// 首选采样带下方/右侧，再换上方/左侧，最后贴着与本带同列（同行）的可见材质边缘再让一份间距。
    /// 候选必须与任何一个 <paramref name="avoid"/> 矩形都不相交：带衬底 EXIF 的上缘采样带本身就在块内，
    /// 只避开采样带时对照区会落进同一个 EXIF 块，量到的就是块内的模糊值（真机首轮即为此）。
    /// </summary>
    /// <param name="window">窗口矩形。</param>
    /// <param name="target">被测采样带（窗口坐标）。</param>
    /// <param name="avoid">需要整体绕开的材质视图矩形（栏底整块、EXIF 块整块；含被测视图自身）。</param>
    /// <param name="horizontalProfile">剖面方向（决定往哪个轴让开）。</param>
    /// <param name="control">对照区。</param>
    /// <returns>找不到合法对照区为 false。</returns>
    private static bool TryGlassControlRect(Rect window, Rect target, IReadOnlyList<Rect> avoid,
        bool horizontalProfile, out Rect control)
    {
        control = new Rect();
        const int gap = 24;
        var candidates = new List<Rect>(4);
        if (horizontalProfile)
        {
            candidates.Add(new Rect(target.Left, target.Bottom + gap, target.Right, target.Bottom + gap + target.Height()));
            candidates.Add(new Rect(target.Left, target.Top - gap - target.Height(), target.Right, target.Top - gap));
        }
        else
        {
            candidates.Add(new Rect(target.Right + gap, target.Top, target.Right + gap + target.Width(), target.Bottom));
            candidates.Add(new Rect(target.Left - gap - target.Width(), target.Top, target.Left - gap, target.Bottom));
        }

        // 兜底：与本带同列（同行）的可见材质整体让开——EXIF 上缘带离块边缘很近，单纯上下移仍在块内。
        int unionTop = int.MaxValue, unionBottom = int.MinValue;
        int unionLeft = int.MaxValue, unionRight = int.MinValue;
        foreach (var rect in avoid)
        {
            if (horizontalProfile)
            {
                if (rect.Right <= target.Left || rect.Left >= target.Right) continue;
                unionTop = Math.Min(unionTop, rect.Top);
                unionBottom = Math.Max(unionBottom, rect.Bottom);
            }
            else
            {
                if (rect.Bottom <= target.Top || rect.Top >= target.Bottom) continue;
                unionLeft = Math.Min(unionLeft, rect.Left);
                unionRight = Math.Max(unionRight, rect.Right);
            }
        }
        if (unionTop != int.MaxValue)
        {
            candidates.Add(new Rect(target.Left, unionBottom + gap, target.Right, unionBottom + gap + target.Height()));
            candidates.Add(new Rect(target.Left, unionTop - gap - target.Height(), target.Right, unionTop - gap));
        }
        if (unionLeft != int.MaxValue)
        {
            candidates.Add(new Rect(unionRight + gap, target.Top, unionRight + gap + target.Width(), target.Bottom));
            candidates.Add(new Rect(unionLeft - gap - target.Width(), target.Top, unionLeft - gap, target.Bottom));
        }

        foreach (var candidate in candidates)
        {
            var band = new Rect(
                Math.Max(candidate.Left, window.Left), Math.Max(candidate.Top, window.Top),
                Math.Min(candidate.Right, window.Right), Math.Min(candidate.Bottom, window.Bottom));
            int along = horizontalProfile ? band.Width() : band.Height();
            int cross = horizontalProfile ? band.Height() : band.Width();
            if (along < 32 || cross < 8) continue;
            if (Rect.Intersects(band, target)) continue;
            bool blocked = false;
            foreach (var rect in avoid)
            {
                if (!Rect.Intersects(band, rect)) continue;
                blocked = true;
                break;
            }
            if (blocked) continue;
            control = band;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 当前可见的材质视图矩形（6 个栏底 + 两个 EXIF 块）：栏外直通对照区不得与其中任何一个相交。
    /// 被测材质自身也在这份清单里，因此「避开整块」与「不与其他材质重叠」由同一份数据保证。
    /// </summary>
    /// <param name="root">主屏根视图。</param>
    private static List<Rect> GlassVisibleMaterialRects(AndroidNativeRootView root)
    {
        var rects = new List<Rect>();
        foreach (var (_, bar) in root.BarScrimsForChecks)
        {
            if (bar.Visibility == ViewStates.Visible && bar.Width > 0 && bar.Height > 0)
                rects.Add(GlassViewWindowRect(bar));
        }
        foreach (var block in new[] { root.PhoneExif, root.ControlBar.ExifBlock })
        {
            if (block.Visibility == ViewStates.Visible && block.Width > 0 && block.Height > 0)
                rects.Add(GlassViewWindowRect(block));
        }
        return rects;
    }

    /// <summary>两块区域的并集（用于把「采样带 + 对照区」一次截进同一张 PNG）；空矩形按 Android 语义忽略。</summary>
    /// <param name="a">区域 A。</param>
    /// <param name="b">区域 B（可为空）。</param>
    private static Rect GlassProductUnion(Rect a, Rect b)
    {
        var union = new Rect(a);
        union.Union(b);
        return union;
    }

    /// <summary>
    /// 带衬底 EXIF 块内的可信采样带：横排块（44dp 厚度）里文字对垂直居中，其上缘带既无文字也不受 12dp 圆角影响，
    /// 因此取「块内左右各让开一份圆角」的水平窄带。文字对高度按与视图相同的字体度量令牌计算，
    /// 算不出至少 6px 高的带宽时返回 false（由调用方记 NOTE，不用结构断言冒充像素验收）。
    /// </summary>
    /// <param name="block">EXIF 块视图。</param>
    /// <param name="strip">采样带（窗口坐标）。</param>
    /// <param name="detail">几何读数（写进报告）。</param>
    private static bool TryGlassExifStrip(View block, out Rect strip, out string detail)
    {
        strip = new Rect();
        var rect = GlassViewWindowRect(block);
        int radiusPx = (int)Math.Round(AndroidUiTheme.Dp(_activity!, DesignTokens.ToolbarExifCornerRadius));
        using var labelPaint = AndroidUiTheme.TextPaint(_activity!, DesignTokens.ToolbarExifLabelFontSize,
            AndroidUiTheme.TextSecondary);
        using var valuePaint = AndroidUiTheme.TextPaint(_activity!, DesignTokens.ToolbarExifValueFontSize,
            AndroidUiTheme.TextPrimary, bold: true);
        var labelMetrics = labelPaint.GetFontMetrics()!;
        var valueMetrics = valuePaint.GetFontMetrics()!;
        double pairHeight = labelMetrics.Bottom - labelMetrics.Top + valueMetrics.Bottom - valueMetrics.Top
            + AndroidUiTheme.Dp(_activity!, 1);
        double pairTop = (block.Height - pairHeight) / 2;
        int top = rect.Top + 4;
        int bottom = rect.Top + (int)Math.Floor(pairTop) - 2;
        int left = rect.Left + radiusPx + 2;
        int right = rect.Right - radiusPx - 2;
        detail = $"块={rect.Width()}x{rect.Height()} 圆角={radiusPx}px 文字对高={pairHeight:F1}px"
            + $" 上缘空隙={pairTop:F1}px 带=({left},{top})-({right},{bottom})";
        if (right - left < 32 || bottom - top < 6) return false;
        strip = new Rect(left, top, right, bottom);
        return true;
    }

    /// <summary>
    /// 生成合成图案（RGBA8、整幅铺开）。物理 100% 下与源 1:1，条纹/边缘位置因此可按窗口坐标精确投放。
    /// </summary>
    /// <param name="kind">图案种类。</param>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <param name="colorSpace">像素标签（与当前表面一致，避免显式转换）。</param>
    /// <param name="edgeX"><see cref="GlassProductPattern.Edge"/> 的竖直分界（图像坐标）。</param>
    /// <param name="edgeY"><see cref="GlassProductPattern.Edge"/> 的分界高（<c>edgeX</c> 为负时按 y 分界）。</param>
    /// <returns>合成图；失败为 null。</returns>
    private static DecodedImage? CreateGlassProductPattern(GlassProductPattern kind, int width, int height,
        ImageColorSpace colorSpace, int edgeX = -1, int edgeY = 0)
    {
        int stride = checked(width * 4);
        var pixels = DecodedImage.RentPixels(checked(stride * height));
        try
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte value = kind switch
                    {
                        GlassProductPattern.FlatBlack => 0,
                        GlassProductPattern.FlatWhite => 255,
                        // 50% 灰衬底：SHELL-12 三色校准的中间色，物理 100% 下与源 1:1。
                        GlassProductPattern.FlatGray => 128,
                        // 1 像素黑 / 1 像素白：物理 100% 直通时是纯高频，任何模糊都会把它压平。
                        GlassProductPattern.Stripes => (x & 1) == 0 ? (byte)255 : (byte)0,
                        GlassProductPattern.Edge => (edgeX >= 0 ? x >= edgeX : y >= edgeY) ? (byte)255 : (byte)0,
                        _ => (byte)255,
                    };
                    int at = row + x * 4;
                    pixels[at] = value;
                    pixels[at + 1] = value;
                    pixels[at + 2] = value;
                    pixels[at + 3] = 255;
                }
            }
            return DecodedImage.CreatePooled(pixels, width, height, stride, PixelEncoding.Rgba8, colorSpace,
                width, height, 1);
        }
        catch
        {
            ImageBufferPool.Return(pixels);
            throw;
        }
    }

    /// <summary>
    /// 挑一个真实可见、面积足够做剖面测量的栏底；同时给出它的窗口矩形。
    /// </summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="bar">选中的栏底。</param>
    /// <param name="rect">栏底窗口矩形（px）。</param>
    private static bool TryPickGlassBar(AndroidNativeRootView root, out (string Name, AndroidBarScrim Bar) bar,
        out Rect rect)
    {
        bar = (string.Empty, null!);
        rect = new Rect();
        double best = 0;
        foreach (var item in root.BarScrimsForChecks)
        {
            var candidate = item.Bar;
            if (candidate.Visibility != ViewStates.Visible) continue;
            if (candidate.Width < 48 || candidate.Height < 24) continue;
            double area = (double)candidate.Width * candidate.Height;
            if (area <= best) continue;
            best = area;
            bar = item;
            rect = GlassViewWindowRect(candidate);
        }
        return best > 0;
    }

    /// <summary>
    /// 把主屏上除照片层与被测材质视图（含其祖先链）以外的可见子视图临时设为不可见。
    /// 可重复调用：切图与布局会让产品栏位内容（胶片卡片、底部按钮等）重新显示，采样前必须再压一次，
    /// 否则测量会被这些内容污染；每次新压住的视图都追加进同一份 <paramref name="hidden"/>，收尾一起还原。
    /// </summary>
    /// <param name="hidden">原可见性清单。</param>
    /// <param name="keep">被测材质视图（自身与祖先链保留）。</param>
    /// <returns>本次新隐藏的视图数。</returns>
    private static int HideGlassBarContents(List<(View View, ViewStates Visibility)> hidden, View keep)
    {
        var root = _root!;
        var keepChain = new HashSet<View>();
        for (var current = keep; current != null && !ReferenceEquals(current, root); current = current.Parent as View)
            keepChain.Add(current);
        int added = 0;
        for (int index = 0; index < root.ChildCount; index++)
        {
            var child = root.GetChildAt(index);
            if (child == null || ReferenceEquals(child, root.PhotoLayer) || keepChain.Contains(child)) continue;
            if (child.Visibility != ViewStates.Visible) continue;
            hidden.Add((child, child.Visibility));
            child.Visibility = ViewStates.Invisible;
            added++;
        }
        return added;
    }

    /// <summary>
    /// 每次 PixelCopy 测量与诊断截图之前重新压住栏位内容，并在确有视图被重新隐藏时等界面落地。
    /// 只压可见性、不换视图结构，产品照片层与被测材质保持原样。
    /// </summary>
    /// <param name="hidden">隐藏清单。</param>
    /// <param name="keep">被测材质视图。</param>
    private static async Task RehideGlassBarContentsAsync(List<(View View, ViewStates Visibility)> hidden, View keep)
    {
        int added = HideGlassBarContents(hidden, keep);
        if (added == 0) return;
        GlassHostNote($"glass 产品 回读前重新隐藏栏位内容 {added} 个（切图/布局会重新显示它们）");
        await Task.Delay(80);
    }

    /// <summary>三色读数格式化（NaN 写 n/a，避免把缺读数当成 0）。</summary>
    /// <param name="value">亮度均值。</param>
    private static string GlassProductLuma(double value) => double.IsNaN(value) ? "n/a" : value.ToString("F1");

    /// <summary>共享材质绘制次数（诊断，不参与判定）。</summary>
    /// <param name="root">主屏根视图。</param>
    private static string GlassProductMaterialDraws(AndroidNativeRootView root)
        => $"材质OnDraw={root.PhotoMaterial.MaterialDrawCount}";

    /// <summary>诊断截图：回读前先重新压住栏位内容，再落 PNG（文件名与路径写进报告 NOTE）。</summary>
    /// <param name="region">窗口坐标区域。</param>
    /// <param name="name">文件名前缀。</param>
    /// <param name="hidden">隐藏清单。</param>
    /// <param name="keep">被测材质视图。</param>
    private static async Task SaveGlassProductShotAsync(Rect region, string name,
        List<(View View, ViewStates Visibility)> hidden, View keep)
    {
        await RehideGlassBarContentsAsync(hidden, keep);
        await GlassBackdropSaveScreenshotAsync(region, name);
    }

    /// <summary>视图在窗口坐标系里的矩形（px）。</summary>
    private static Rect GlassViewWindowRect(View view)
    {
        var location = new int[2];
        view.GetLocationInWindow(location);
        return new Rect(location[0], location[1], location[0] + view.Width, location[1] + view.Height);
    }

    /// <summary>某个视图是否是另一个视图的后代（用于证明主图确实在照片层内）。</summary>
    private static bool IsGlassDescendant(View parent, View child)
    {
        for (var current = child.Parent as View; current != null; current = current.Parent as View)
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }

    /// <summary>统计根视图里的 <see cref="AndroidPhotoView"/> 数量（证明没有第二个主图宿主）。</summary>
    private static int CountGlassPhotoViews(View view)
    {
        int count = view is AndroidPhotoView ? 1 : 0;
        if (view is not ViewGroup group) return count;
        for (int index = 0; index < group.ChildCount; index++)
        {
            var child = group.GetChildAt(index);
            if (child != null) count += CountGlassPhotoViews(child);
        }
        return count;
    }
}
