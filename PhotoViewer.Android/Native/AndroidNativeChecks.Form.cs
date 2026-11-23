using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.Views;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Settings;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 形态判定与主屏静态几何检查（验收专用）：宽形态按共享 <see cref="ShellPaneLayout"/>、手机形态按
/// <see cref="PhoneScreenLayout"/> 逐槽位对照真实视图 frame；<c>form</c> 独立模式核对原生尺寸类别映射与旋转不重建会话。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary><c>form</c> 独立模式：记录当前窗口/尺寸类别/设备类型，核对形态映射与旋转后的形态和会话代际。</summary>
    private static async Task RunFormChecksAsync()
    {
        var root = _root!;
        var main = _main!;
        var activity = _activity!;
        var session = _session!;
        await WaitForScreenAsync();
        double density = AndroidUiTheme.Density(activity);
        var metrics = AndroidScreenForm.Read(activity);
        Check("form 原生尺寸类别可读", metrics.WidthDp > 0 && metrics.HeightDp > 0,
            $"window={metrics.WidthDp:0.#}x{metrics.HeightDp:0.#}dp horizontalCompact={metrics.HorizontalCompact} "
            + $"verticalCompact={metrics.VerticalCompact} phoneDevice={metrics.PhoneDevice} class={metrics.SizeClassText}");
        Check("form 手机设备阈值（smallestScreenWidthDp < 600）",
            metrics.PhoneDevice == (activity.Resources?.Configuration?.SmallestScreenWidthDp ?? int.MaxValue) < 600,
            $"smallestWidth={activity.Resources?.Configuration?.SmallestScreenWidthDp} phone={metrics.PhoneDevice}");
        Check("form 平台声明支持手机形态", LayoutCapabilities.PhoneLayoutSupported, $"supported={LayoutCapabilities.PhoneLayoutSupported}");

        var mode = main.Settings.LayoutMode;
        var expected = ScreenFormResolver.Resolve(metrics.HorizontalCompact, metrics.VerticalCompact,
            metrics.WidthDp, metrics.HeightDp, metrics.PhoneDevice, mode, LayoutCapabilities.PhoneLayoutSupported);
        Check("form 共享判定与当前形态一致", root.Form == expected,
            $"form={root.Form} expected={expected} mode={mode} phone={metrics.PhoneDevice} "
            + $"compact={metrics.HorizontalCompact}/{metrics.VerticalCompact}");
        Check("form 手机设备忽略用户模式", !metrics.PhoneDevice
                || ScreenFormResolver.Resolve(metrics.HorizontalCompact, metrics.VerticalCompact,
                    metrics.WidthDp, metrics.HeightDp, true, LayoutMode.Row, true) != ScreenForm.Wide,
            $"mode={mode} phone={metrics.PhoneDevice}");
        Check("form 自动模式任一轴紧凑进入手机",
            ScreenFormResolver.Resolve(true, false, 900, 1200, false, LayoutMode.Auto, true) != ScreenForm.Wide
            && ScreenFormResolver.Resolve(false, true, 1200, 900, false, LayoutMode.Auto, true) != ScreenForm.Wide,
            "compactWidth/compactHeight → phone");
        Check("form 自动两轴常规为宽形态",
            ScreenFormResolver.Resolve(false, false, 1200, 900, false, LayoutMode.Auto, true) == ScreenForm.Wide,
            "regular/regular → wide");

        var raw = root.WindowInsetsForChecks;
        Check("form 公开 Insets 读数", true,
            $"cutouts={raw.DescribeCutouts()} corners={raw.Corners} gesture={raw.IsGestureNavigation} safe={root.SafeInsets}");
        Check("form 模拟器圆角读数（如实登记）", true,
            $"corners={raw.Corners.TopLeft:0.#}/{raw.Corners.TopRight:0.#}/{raw.Corners.BottomRight:0.#}/{raw.Corners.BottomLeft:0.#}");

        if (root.Form != ScreenForm.Wide)
        {
            var nav = root.PhoneNavigation;
            var commands = root.PhoneCommands;
            double button = DesignTokens.ShellGlassButtonSize * density;
            double command = DesignTokens.ShellPhoneCommandSize * density;
            var expectedCommands = PhoneControlCommands.Resolve(
                main.ControlVM.EnabledControls.Select(item => item.Command), main.Settings.ShowNavigationButtons);
            bool commandSizes = commands.SegmentCount == expectedCommands.Length
                && commands.SegmentsForChecks.All(segment => Near(segment.Width, command) && Near(segment.Height, command));
            Check("form 手机合体段 44×44、可选命令段 44×44",
                Near(nav.First.Width, button) && Near(nav.First.Height, button)
                && Near(nav.Second.Width, button) && Near(nav.Second.Height, button) && commandSizes,
                $"nav={nav.Width}x{nav.Height} first={nav.First.Width}x{nav.First.Height} second={nav.Second.Width}x{nav.Second.Height} "
                + $"commands={commands.SegmentCount}/{expectedCommands.Length} segments="
                + string.Join("/", commands.SegmentsForChecks.Select(segment => $"{segment.Width}x{segment.Height}")));
        }

        if (root.Form != ScreenForm.Wide)
        {
            // 手机形态开关各管各的栏：EXIF / 上下张 / 评分关闭后真实槽位释放。
            bool savedExif = main.IsExifViewVisible;
            bool savedNav = main.Settings.ShowNavigationButtons;
            bool savedRating = main.Settings.ShowRating;
            double ratingBefore = root.PhoneSlots.Rating.Width;
            try
            {
                main.IsExifViewVisible = false;
                await Task.Delay(500);
                if (root.Form == ScreenForm.PhonePortrait)
                {
                    Check("form 关闭 EXIF 顶行贴状态栏下缘", root.PhoneExif.Visibility != ViewStates.Visible
                            && Near(root.PhoneSlots.Filter.Y, root.SafeInsets.Top)
                            && Near(root.PhoneSlots.More.Y, root.SafeInsets.Top),
                        $"exif={root.PhoneExif.Visibility} filter={root.PhoneSlots.Filter} more={root.PhoneSlots.More} "
                        + $"safeTop={root.SafeInsets.Top:0.#}");
                }
                else
                {
                    Check("form 横屏不应用竖屏顶行规则", root.PhoneExif.Visibility != ViewStates.Visible
                            && Near(root.PhoneSlots.Filter.Y, root.PhoneSlots.More.Y),
                        $"exif={root.PhoneExif.Visibility} filter={root.PhoneSlots.Filter} more={root.PhoneSlots.More}");
                }
                main.Settings.ShowNavigationButtons = false;
                await Task.Delay(500);
                Check("form 关闭上下张释放合体", !Has(root.PhoneSlots.Navigation)
                        && root.PhoneNavigation.Visibility != ViewStates.Visible,
                    $"nav={root.PhoneSlots.Navigation} rating={root.PhoneSlots.Rating}");
                main.Settings.ShowRating = false;
                await Task.Delay(500);
                Check("form 关闭评分释放槽位", !Has(root.PhoneSlots.Rating)
                        && root.PhoneRating.Visibility != ViewStates.Visible,
                    $"rating={root.PhoneSlots.Rating} before={ratingBefore:0.#}");
            }
            finally
            {
                main.IsExifViewVisible = savedExif;
                main.Settings.ShowNavigationButtons = savedNav;
                main.Settings.ShowRating = savedRating;
                await Task.Delay(500);
            }
        }

        CheckSharedWidgetGeometry();

#if PHOTOVIEWER_DINO
        bool savedSimilarity = main.FileVM.FilterBar.IsSimilarityPanelOpen;
        try
        {
            main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
            await Task.Delay(500);
            if (root.Form != ScreenForm.Wide)
                Check("AI 手机相似状态保留但面板隐藏",
                    main.FileVM.FilterBar.IsSimilarityPanelOpen
                    && root.SimilarityPane.Visibility != ViewStates.Visible,
                    $"state={main.FileVM.FilterBar.IsSimilarityPanelOpen} panel={root.SimilarityPane.Visibility}");
            else
                Check("AI 宽形态相似状态回归",
                    main.FileVM.FilterBar.IsSimilarityPanelOpen
                    && root.SimilarityPane.Visibility == ViewStates.Visible,
                    $"state={main.FileVM.FilterBar.IsSimilarityPanelOpen} panel={root.SimilarityPane.Visibility}");
            main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
            await Task.Delay(400);
            Check("AI 关闭相似状态后面板收起",
                !main.FileVM.FilterBar.IsSimilarityPanelOpen
                && root.SimilarityPane.Visibility != ViewStates.Visible,
                $"state={main.FileVM.FilterBar.IsSimilarityPanelOpen} panel={root.SimilarityPane.Visibility}");
        }
        finally
        {
            main.FileVM.FilterBar.IsSimilarityPanelOpen = savedSimilarity;
            await Task.Delay(300);
        }
#endif

        int generation = session.Generation;
        var previousOrientation = activity.RequestedOrientation;
        try
        {
            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Landscape;
            await WaitAsync(() => root.Width > root.Height, "横屏形态", 8000);
            await Task.Delay(400);
            var landscapeMetrics = AndroidScreenForm.Read(activity);
            var landscapeExpected = ScreenFormResolver.Resolve(landscapeMetrics.HorizontalCompact,
                landscapeMetrics.VerticalCompact, landscapeMetrics.WidthDp, landscapeMetrics.HeightDp,
                landscapeMetrics.PhoneDevice, mode, LayoutCapabilities.PhoneLayoutSupported);
            Check("form 旋转后按新窗口重判形态", root.Form == landscapeExpected,
                $"form={root.Form} expected={landscapeExpected} window={landscapeMetrics.WidthDp:0.#}x{landscapeMetrics.HeightDp:0.#}");
            Check("form 旋转不重建照片会话", session.Generation == generation,
                $"generation={generation}->{session.Generation} image={session.Image?.Width}x{session.Image?.Height}");
            if (!landscapeMetrics.PhoneDevice)
                Check("form 宽形态横屏布局模式", landscapeExpected == ScreenForm.Wide
                        && main.IsRowLayout == ExpectedRowLayout(main, landscape: true),
                    $"form={root.Form} row={main.IsRowLayout} mode={mode}");
        }
        finally
        {
            activity.RequestedOrientation = previousOrientation;
            await Task.Delay(400);
        }
    }

    /// <summary>用相同星形的普通叠画验证紧凑星簇确实减淡下层，且擦除不会穿透玻璃底色。</summary>
    private static void CheckCompactRatingOcclusion(Activity activity, double density)
    {
        int width = (int)Math.Round(RatingControlGeometry.Thickness * density);
        int height = (int)Math.Round(RatingControlGeometry.CompactMinLength * density);
        using var rating = new AndroidRatingControl(activity);
        rating.SetOrientation(false);
        rating.Layout(0, 0, width, height);

        using var actual = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
        using var actualCanvas = new Canvas(actual);
        rating.DrawGlassContent(actualCanvas);

        using var reference = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
        using var referenceCanvas = new Canvas(reference);
        using var paint = AndroidUiTheme.Paint(AndroidUiTheme.StarInactive);
        paint.TextSize = (float)(RatingControlGeometry.CompactGlyph * density);
        paint.SetTypeface(AndroidUiTheme.IconTypeface(activity));
        for (int value = 5; value >= 1; value--)
        {
            double offset = RatingControlGeometry.CompactOffset(value, false, false, height / density);
            AndroidUiTheme.DrawGlyphCentered(referenceCanvas, AndroidIcons.Star, paint,
                width / 2f, (float)(height / 2.0 + offset * density));
        }

        var background = Color.Rgb(23, 61, 119);
        using var composite = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
        using var compositeCanvas = new Canvas(composite);
        compositeCanvas.DrawColor(background);
        rating.DrawGlassContent(compositeCanvas);

        int faded = 0;
        int fullyOpaque = 0;
        bool backgroundIntact = composite.GetPixel(0, 0) == background.ToArgb();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int actualAlpha = (int)((uint)actual.GetPixel(x, y) >> 24);
                int referenceAlpha = (int)((uint)reference.GetPixel(x, y) >> 24);
                if (actualAlpha > 0 && actualAlpha + 8 < referenceAlpha) faded++;
                if (actualAlpha == 255) fullyOpaque++;
                backgroundIntact &= (uint)composite.GetPixel(x, y) >> 24 == 255;
            }
        }
        Check("RATING-07 紧凑星簇局部羽化且保留底层玻璃", faded > 0 && fullyOpaque > 0 && backgroundIntact,
            $"faded={faded} opaque={fullyOpaque} backgroundIntact={backgroundIntact}");
    }

    /// <summary>共享控件真实节点：紧凑/展开评分、槽宽 &gt;224 的绘制/命中原点、6 项 EXIF 裁到 2 项。</summary>
    private static void CheckSharedWidgetGeometry()
    {
        var activity = _activity!;
        double density = AndroidUiTheme.Density(activity);

        // 紧凑 / 展开两档：控制栏真实节点。评分组显隐受用户设置影响，本段固定打开并在结束时还原。
        bool showRating = _main!.Settings.ShowRating;
        _main.Settings.ShowRating = true;
        var bar = new AndroidControlBar(activity, _main!);
        try
        {
            bar.Wire(_ => { }, _ => { }, null, null);
            bar.Arrange(true, 88);
            var compactFrames = bar.GroupFramesForChecks();
            var compactKinds = bar.GroupKindsForChecks;
            var compactNodes = bar.NodesForChecks()
                .Where(node => node.Kind == ControlBarGroupKind.Rating.ToString() && node.Width > 0).ToList();
            int compactIndex = compactKinds.ToList().IndexOf(ControlBarGroupKind.Rating);
            var compactFrame = compactIndex >= 0 ? compactFrames[compactIndex] : default;
            bool compactInside = compactFrame.Width > 0;
            foreach (var node in compactNodes)
                if (node.X < compactFrame.X - .6 || node.Y < compactFrame.Y - .6
                    || node.X + node.Width > compactFrame.MaxX + .6
                    || node.Y + node.Height > compactFrame.MaxY + .6
                    || node.Width > RatingControlGeometry.CompactGlyph + .6
                    || node.Height > RatingControlGeometry.CompactGlyph + .6) compactInside = false;
            Check("评分紧凑静态星簇", Near(compactFrame.Width, RatingControlGeometry.CompactMinLength)
                    && compactNodes.Count == 5 && compactInside
                    && Near(bar.RatingGlyphSizeForChecks, RatingControlGeometry.CompactGlyph),
                $"frame={compactFrame} nodes={compactNodes.Count} glyph={bar.RatingGlyphSizeForChecks:0.#} "
                + $"sizes={string.Join("/", compactNodes.Select(n => $"{n.Width:0.#}x{n.Height:0.#}"))}");

            CheckCompactRatingOcclusion(activity, density);

            bar.Arrange(true, RatingControlGeometry.ExpandedLength + ToolbarFlowLayout.Padding * 2);
            var expandedFrames = bar.GroupFramesForChecks();
            var expandedKinds = bar.GroupKindsForChecks;
            var expandedNodes = bar.NodesForChecks()
                .Where(node => node.Kind == ControlBarGroupKind.Rating.ToString() && node.Width > 0).ToList();
            int expandedIndex = expandedKinds.ToList().IndexOf(ControlBarGroupKind.Rating);
            var expandedFrame = expandedIndex >= 0 ? expandedFrames[expandedIndex] : default;
            bool expandedSlots = expandedNodes.Count == 6;
            foreach (var node in expandedNodes)
                if (!Near(node.Width, RatingControlGeometry.Slot) || !Near(node.Height, RatingControlGeometry.Thickness))
                    expandedSlots = false;
            Check("评分展开 224 六格", Near(expandedFrame.Width, RatingControlGeometry.ExpandedLength)
                    && expandedSlots && Near(bar.RatingGlyphSizeForChecks, DesignTokens.ToolbarRatingGlyphFontSize),
                $"frame={expandedFrame} nodes={expandedNodes.Count} glyph={bar.RatingGlyphSizeForChecks:0.#} "
                + $"sizes={string.Join("/", expandedNodes.Select(n => $"{n.Width:0.#}x{n.Height:0.#}"))}");
        }
        finally
        {
            bar.Dispose();
            _main.Settings.ShowRating = showRating;
        }

        // 槽宽 > 224：ControlFrame 居中偏移必须同时进入绘制与命中；横/纵两向都要对。
        void CheckRatingOrientation(bool horizontal, double mainDp, double crossDp, string label)
        {
            var rating = new AndroidRatingControl(activity) { Horizontal = horizontal };
            try
            {
                int width = (int)Math.Round((horizontal ? mainDp : crossDp) * density);
                int height = (int)Math.Round((horizontal ? crossDp : mainDp) * density);
                rating.Measure(global::Android.Views.View.MeasureSpec.MakeMeasureSpec(width, global::Android.Views.MeasureSpecMode.Exactly),
                    global::Android.Views.View.MeasureSpec.MakeMeasureSpec(height, global::Android.Views.MeasureSpecMode.Exactly));
                rating.Layout(0, 0, width, height);
                rating.SetState(0, false, true);
                var slotRect = new LayoutRect(0, 0, width / density, height / density);
                var control = RatingControlGeometry.ControlFrame(slotRect, horizontal);
                double rowLength = horizontal ? control.Width : control.Height;
                double rowCross = horizontal ? control.Height : control.Width;
                var slot = RatingControlGeometry.SlotRect(3, rowLength, rowCross, horizontal, RatingControlGeometry.Slot);
                var actual = rating.SlotRectPx(3);
                double expectedX = (control.X + slot.X + slot.Width / 2) * density;
                double expectedY = (control.Y + slot.Y + slot.Height / 2) * density;
                int rated = -1;
                void OnRate(int value) => rated = value;
                rating.RateRequested += OnRate;
                long now = global::Android.OS.SystemClock.UptimeMillis();
                using (var down = MotionEvent.Obtain(now, now, MotionEventActions.Down, actual.CenterX(), actual.CenterY(), 0))
                    rating.OnTouchEvent(down);
                using (var up = MotionEvent.Obtain(now, now + 10, MotionEventActions.Up, actual.CenterX(), actual.CenterY(), 0))
                    rating.OnTouchEvent(up);
                rating.RateRequested -= OnRate;
                Check($"评分槽 {label} 绘制与命中同一原点",
                    Near(actual.CenterX(), expectedX) && Near(actual.CenterY(), expectedY) && rated == 3,
                    $"actual=({actual.CenterX():0.#},{actual.CenterY():0.#}) expected=({expectedX:0.#},{expectedY:0.#}) "
                    + $"control={control} slot={slot} rated={rated}");
            }
            finally
            {
                rating.Dispose();
            }
        }

        CheckRatingOrientation(true, 300, 44, "300×44 横向");
        CheckRatingOrientation(false, 300, 44, "44×300 竖向");
        CheckRatingOrientation(false, 224, 44, "44×224 竖向");

        var smallCard = new AndroidThumbnailCard(activity);
        try
        {
            smallCard.SetTier(FilmCardTier.Small);
            var small = smallCard.SmallFramesForChecks();
            Check("小档内部节点基准（占位面/照片/文件名/抖动点）",
                Near(small.Surface.X, 2) && Near(small.Surface.Y, 2)
                && Near(small.Surface.Width, 60) && Near(small.Surface.Height, 72)
                && Near(small.Image.X, 5) && Near(small.Image.Y, 5)
                && Near(small.Image.Width, 54) && Near(small.Image.Height, 54)
                && Near(small.Name.X, 2) && Near(small.Name.Y, 61)
                && Near(small.Name.Width, 60) && Near(small.Name.Height, 11)
                && Near(small.ShakeDot.X, 48) && Near(small.ShakeDot.Y, 8)
                && Near(small.ShakeDot.Width, 8) && Near(small.ShakeDot.Height, 8),
                $"surface={small.Surface} image={small.Image} name={small.Name} dot={small.ShakeDot}");
        }
        finally
        {
            smallCard.Dispose();
        }

        // 无衬底纵排：6 项只显示 2 项时，每项仍按 36dp 整项计算，不被可见高度压缩。
        var items = new List<ExifDisplayEntry>(6);
        for (int index = 0; index < 6; index++)
            items.Add(new ExifDisplayEntry($"标签{index + 1}", $"Property{index + 1}", $"值{index + 1}"));
        var exif = new AndroidExifBlock(activity);
        try
        {
            exif.SetContent(items, horizontal: false, bare: true, visibleCount: 2);
            int width = (int)Math.Round(44 * density), height = (int)Math.Round(74 * density);
            exif.Measure(global::Android.Views.View.MeasureSpec.MakeMeasureSpec(width, global::Android.Views.MeasureSpecMode.Exactly),
                global::Android.Views.View.MeasureSpec.MakeMeasureSpec(height, global::Android.Views.MeasureSpecMode.Exactly));
            exif.Layout(0, 0, width, height);
            var frames = exif.ItemFramesForChecks();
            bool fullItems = frames.Count == 2
                && Near(frames[0].Height, ExifBlockGeometry.ItemHeight)
                && Near(frames[1].Height, ExifBlockGeometry.ItemHeight)
                && Near(frames[0].Y, 0)
                && Near(frames[1].Y, ExifBlockGeometry.ItemHeight + ExifBlockGeometry.ItemSpacing);
            Check("EXIF 裸排 6 项裁到 2 项仍整项 36dp", fullItems,
                $"count={frames.Count} frames={string.Join(" | ", frames)} intrinsic={ExifBlockGeometry.Size(6, false, true).Height:0.#}");
        }
        finally
        {
            exif.Dispose();
        }
    }

    /// <summary>主屏静态几何：按形态核对真实视图 frame 与共享几何。</summary>
    /// <param name="root">主屏根视图。</param>
    /// <param name="main">共享主 VM。</param>
    /// <param name="density">屏幕密度。</param>
    /// <param name="phase">阶段名。</param>
    private static void CheckMainScreenGeometry(AndroidNativeRootView root, MainViewModel main, double density, string phase)
    {
        if (root.Form == ScreenForm.Wide) CheckWideGeometry(root, main, density, phase);
        else CheckPhoneGeometry(root, main, density, phase);
    }

    /// <summary>SHELL-12：主屏各栏底使用同一 <c>color.bar.overlay</c> 与 <c>shell.bar.overlay.opacity</c> 色层。</summary>
    private static void CheckBarScrimLayers(AndroidNativeRootView root, string phase)
    {
        var expected = ExpectedBarOverlay();
        var bars = root.BarScrimsForChecks;
        Check($"{phase} 栏底独立色层取 color.bar.overlay/opacity",
            bars.Count > 0 && bars.All(item => item.Bar.ColorForChecks == expected),
            $"expected={expected} actual={string.Join("/", bars.Select(item => $"{item.Name}:{item.Bar.ColorForChecks}"))}");
    }

    /// <summary>宽形态：文件栏/主图/控制栏分区、筛选外框、控制栏分组与卡片大档。</summary>
    private static void CheckWideGeometry(AndroidNativeRootView root, MainViewModel main, double density, string phase)
    {
        double widthDp = root.Width / density, heightDp = root.Height / density;
        var panes = root.Panes;
        bool row = main.IsRowLayout;
        Check($"{phase} 宽形态分区", true, $"row={row} panes={Describe(panes)} screen={widthDp:0.#}x{heightDp:0.#}");
        CheckBarScrimLayers(root, phase);

        var frames = new (string Name, LayoutRect Rect)[]
        {
            ("fileBar", panes.FileBar), ("fileContent", panes.FileContent), ("fileDivider", panes.FileDivider),
            ("photo", panes.Photo), ("controlDivider", panes.ControlDivider),
            ("controlBackground", panes.ControlBackground), ("controlContent", panes.ControlContent),
        };
        bool inside = true;
        var detail = new StringBuilder();
        foreach (var (name, rect) in frames)
        {
            detail.Append(name).Append('=').Append(rect).Append(' ');
            if (rect.Width < 0 || rect.Height < 0 || rect.X < -.6 || rect.Y < -.6
                || rect.MaxX > widthDp + .6 || rect.MaxY > heightDp + .6) inside = false;
        }
        Check($"{phase} 宽形态分区在屏幕内", inside, detail.ToString().TrimEnd());

        bool adjoin = row
            ? Near(panes.FileBar.MaxY, panes.FileDivider.Y) && Near(panes.FileDivider.MaxY, panes.Photo.Y)
                && Near(panes.Photo.MaxY, panes.ControlDivider.Y)
            : Near(panes.FileBar.MaxX, panes.FileDivider.X) && Near(panes.FileDivider.MaxX, panes.Photo.X)
                && Near(panes.Photo.MaxX, panes.ControlDivider.X);
        Check($"{phase} 宽形态分区边界相接", adjoin,
            $"row={row} file={panes.FileBar} divider={panes.FileDivider} photo={panes.Photo} control={panes.ControlBackground}");

        // 主图视图铺满窗口，适应区等于分区主图矩形。
        Check($"{phase} 主图视图满窗口", root.Photo.Width == root.Width && root.Photo.Height == root.Height,
            $"photo={root.Photo.Width}x{root.Photo.Height} root={root.Width}x{root.Height}");
        Check($"{phase} 主图适应区=分区主图", Near(root.FitArea.X, panes.Photo.X) && Near(root.FitArea.Y, panes.Photo.Y)
                && Near(root.FitArea.Width, panes.Photo.Width) && Near(root.FitArea.Height, panes.Photo.Height),
            $"fit={root.FitArea} photoPane={panes.Photo}");

        var filter = root.FilterBar;
        Check($"{phase} 宽形态筛选高度 44", Near(filter.ControlFrame.Height, MobileFilterLayout.Segment),
            $"control={filter.ControlFrame} height={filter.ControlFrame.Height:0.#}");
        Check($"{phase} 宽形态筛选在内容区", root.Panes.FileContent.Width > 0
                && filter.ControlFrame.X >= -0.6 && filter.ControlFrame.MaxX <= root.Panes.FileContent.Width + .6,
            $"control={filter.ControlFrame} content={root.Panes.FileContent}");
        if (AndroidFilterBar.ShowsSimilarityToggle)
        {
            var toggle = filter.SimilarityFrame;
            double toggleGap = row ? toggle.X - filter.ControlFrame.MaxX : toggle.Y - filter.ControlFrame.MaxY;
            double toggleWidth = row ? filter.ToggleNaturalWidthForChecks : FilmCardGeometry.Width(FilmCardTier.Large);
            Check($"{phase} AI 开关行自然宽/列 96×44 与 8 间距",
                Near(toggle.Width, toggleWidth) && Near(toggle.Height, MobileFilterLayout.Segment)
                && Near(toggleGap, MobileFilterLayout.Gap),
                $"toggle={toggle} expectedWidth={toggleWidth:0.#} gap={toggleGap:0.#} expectedGap={MobileFilterLayout.Gap:0.#}");
        }
        // FILTER-07：宽形态合体必须与共享 MobileFilterLayout 的结果逐项一致（不复制公式）。
        double filterLeading = (row ? 0 : root.SafeInsets.Top) + MainScreenLayout.FilterMargin;
        double filterContent = row
            ? Math.Max(1, widthDp - root.SafeInsets.Left - root.SafeInsets.Right)
            : FilmCardGeometry.BandThickness(FilmCardTier.Large, false);
        var filmSide = FilmSideGeometry.Measure(root.SafeInsets.Left, filterContent,
            FilmCardGeometry.CrossLead(FilmCardTier.Large, false), FilmCardGeometry.CrossTrail(FilmCardTier.Large, false),
            MobileFilterLayout.GroupWidth);
        double expectedToggle = !AndroidFilterBar.ShowsSimilarityToggle ? 0
            : row ? filter.ToggleNaturalWidthForChecks : FilmCardGeometry.Width(FilmCardTier.Large);
        var expectedFilter = MobileFilterLayout.Compute(row, filterContent, filterLeading, expectedToggle,
            row ? null : filmSide.ContentTrailing - root.Panes.FileContent.X);
        bool filterMatches = Near(filter.ControlFrame.X, expectedFilter.Control.X)
            && Near(filter.ControlFrame.Y, expectedFilter.Control.Y)
            && Near(filter.ControlFrame.Width, MobileFilterLayout.GroupWidth)
            && Near(filter.ControlFrame.Height, MobileFilterLayout.Segment)
            && Near(filter.PhoneFilterFrame.X - expectedFilter.Control.X, expectedFilter.Filter.X)
            && Near(filter.PhoneFilterFrame.Width, MobileFilterLayout.SegmentWidth)
            && Near(filter.PhoneMoreFrame.X - expectedFilter.Control.X, expectedFilter.More.X)
            && Near(filter.PhoneMoreFrame.Width, MobileFilterLayout.SegmentWidth)
            && Near(filter.MainLengthForChecks, expectedFilter.MainLength);
        Check($"{phase} 宽形态筛选合体等于 MobileFilterLayout", filterMatches,
            $"control={filter.ControlFrame} expected={expectedFilter.Control} filter={filter.PhoneFilterFrame} "
            + $"more={filter.PhoneMoreFrame} main={filter.MainLengthForChecks:0.#}/{expectedFilter.MainLength:0.#} row={row}");

        var card = root.Filmstrip.CardAt(0);
        Check($"{phase} 宽形态卡片大档 96×144", card != null && card.Tier == FilmCardTier.Large
                && Near(card.Width / density, 96) && Near(card.Height / density, 144),
            card == null ? "首卡未创建" : $"{card.Width / density:0.#}×{card.Height / density:0.#} tier={card.Tier}");
        double filterMain = MobileFilterLayout.MainLength(row, row ? 0 : filterLeading,
            AndroidFilterBar.ShowsSimilarityToggle);
        var filmFrame = FrameOf(root.Filmstrip, root, density);
        if (card != null)
        {
            var cardFrame = FrameOf(card, root, density);
            double expectedCardY = row
                ? panes.FileContent.Y + filterMain + FilmCardGeometry.CrossLead(FilmCardTier.Large, true)
                : panes.FileContent.Y + filterMain;
            Check($"{phase} 宽形态首卡 Y 与筛选外框相接", Near(cardFrame.Y, expectedCardY),
                $"cardY={cardFrame.Y:0.#} expected={expectedCardY:0.#} filterMain={filterMain:0.#} row={row}");
        }
        Check($"{phase} 宽形态胶片条全高", row
                ? Near(filmFrame.Y, panes.FileContent.Y + filterMain)
                    && Near(filmFrame.Height, panes.FileContent.Height - filterMain)
                : Near(filmFrame.Y, panes.FileContent.Y) && Near(filmFrame.Height, panes.FileContent.Height),
            $"film={filmFrame} content={panes.FileContent} filterMain={filterMain:0.#} row={row}");

        var nodes = root.ControlBar.NodesForChecks();
        var groups = root.ControlBar.GroupFramesForChecks();
        var groupKinds = root.ControlBar.GroupKindsForChecks;
        var expectedKinds = new List<ControlBarGroupKind>();
        if (root.ControlBar.HasExifGroup) expectedKinds.Add(ControlBarGroupKind.Exif);
        if (root.ControlBar.HasRatingGroup) expectedKinds.Add(ControlBarGroupKind.Rating);
        if (root.ControlBar.Commands.Count > 0) expectedKinds.Add(ControlBarGroupKind.Command);
        Check($"{phase} 控制栏 EXIF→评分→命令",
            groupKinds.SequenceEqual(expectedKinds) && groups.Count == expectedKinds.Count,
            $"kinds={string.Join(",", groupKinds)} expected={string.Join(",", expectedKinds)} frames={string.Join(" | ", groups)}");
        bool commands44 = true;
        double lastEnd = double.NaN;
        double spacing = 0;
        foreach (var node in nodes)
        {
            if (node.Kind == ControlBarGroupKind.Command.ToString())
            {
                if (!Near(node.Width, 44) || !Near(node.Height, 44)) commands44 = false;
                if (!double.IsNaN(lastEnd))
                {
                    double gap = row ? node.X - lastEnd : node.Y - lastEnd;
                    if (gap > 0) spacing = gap;
                }
                lastEnd = row ? node.X + node.Width : node.Y + node.Height;
            }
        }
        Check($"{phase} 命令 44/间距 8", commands44 && (nodes.Count == 0 || Math.Abs(spacing - 8) < .6 || spacing == 0),
            $"commands44={commands44} spacing={spacing:0.#} nodes={nodes.Count}");
        bool exifItemOk = true;
        var exifDetail = new StringBuilder();
        foreach (var node in nodes)
        {
            if (node.Kind != ControlBarGroupKind.Exif.ToString()) continue;
            exifDetail.Append($"{node.Width:0.#}x{node.Height:0.#}/");
            // 横排项 52×44；无衬底纵排是 52 宽容器 + 整项 36 高。
            if (root.ControlBar.ExifBare)
            {
                if (!Near(node.Width, ExifBlockGeometry.ItemWidth)
                    || !Near(node.Height, ExifBlockGeometry.ItemHeight)) exifItemOk = false;
            }
            else if (!Near(node.Width, ExifBlockGeometry.ItemWidth)
                || !Near(node.Height, ExifBlockGeometry.Thickness)) exifItemOk = false;
        }
        Check($"{phase} EXIF 项尺寸", exifItemOk,
            $"bare={root.ControlBar.ExifBare} sizes={exifDetail.ToString().TrimEnd('/')}");

        // TOOLBAR-02：命令是真实 Material 按钮，行/列交叉轴居中。
        bool commandViewsOk = true;
        var commandDetail = new StringBuilder();
        foreach (var (command, view) in root.ControlBar.CommandViewsForChecks)
        {
            var node = nodes.FirstOrDefault(item => item.Kind == ControlBarGroupKind.Command.ToString()
                && item.Name == view.ContentDescription);
            bool insideFrame = root.ControlBar.GroupFramesForChecks().Any(frame =>
                node.X >= frame.X - .6 && node.X + node.Width <= frame.MaxX + .6
                && node.Y >= frame.Y - .6 && node.Y + node.Height <= frame.MaxY + .6);
            bool ok = view is AndroidGlassButton && Near(view.Width / density, 44) && Near(view.Height / density, 44)
                && node.Width > 0 && insideFrame
                && !string.IsNullOrEmpty(view.ContentDescription);
            if (!ok) commandViewsOk = false;
            commandDetail.Append($"{command}:{view.Width / density:0.#}x{view.Height / density:0.#}@{node.X:0.#},{node.Y:0.#} ");
        }
        bool commandsCentered = commandViewsOk && nodes.Where(node => node.Kind == ControlBarGroupKind.Command.ToString())
            .All(node =>
            {
                var kindFrame = root.ControlBar.GroupFramesForChecks()[groupKinds.ToList().IndexOf(ControlBarGroupKind.Command)];
                double center = row ? kindFrame.Y + kindFrame.Height / 2 : kindFrame.X + kindFrame.Width / 2;
                double nodeCenter = row ? node.Y + node.Height / 2 : node.X + node.Width / 2;
                return Near(nodeCenter, center);
            });
        Check($"{phase} 命令为真实 Material 按钮且在交叉轴居中", commandsCentered,
            $"views={root.ControlBar.CommandViewsForChecks.Count} nodes={nodes.Count} row={row} {commandDetail}");

        // RATING-07：评分胶囊自身是分组内的真实控件，交叉轴恒为 44（不被行高拉宽）。
        var ratingView = root.ControlBar.RatingControl;
        if (root.ControlBar.HasRatingGroup)
        {
            double ratingCross = row ? ratingView.Height / density : ratingView.Width / density;
            var fill = ExpectedGlassButtonFill();
            var toggle = root.FilterBar.ToggleForChecks;
            Check($"{phase} 评分胶囊交叉轴 44", Near(ratingCross, RatingControlGeometry.Thickness),
                $"view={ratingView.Width / density:0.#}x{ratingView.Height / density:0.#} cross={ratingCross:0.#} row={row}");
            Check($"{phase} 评分胶囊、合体与命令按钮同底",
                ratingView.FillColorForChecks == fill
                && root.ControlBar.CommandViewsForChecks.Values.All(view => view.FillColorForChecks == fill)
                && root.FilterBar.ComboForChecks.FillColorForChecks == fill
                && (toggle == null || toggle.FillColorForStateForChecks([]) == fill),
                $"rating={ratingView.FillColorForChecks} expected={fill} combo={root.FilterBar.ComboForChecks.FillColorForChecks} "
                + $"toggle={toggle?.FillColorForStateForChecks([]).ToString() ?? "none"} "
                + $"views={string.Join("/", root.ControlBar.CommandViewsForChecks.Values.Select(v => v.FillColorForChecks))}");
            var stateLayer = AndroidX.Core.Content.ContextCompat.GetColorStateList(_activity!, Resource.Color.pv_glass_state_layer);
            var pressedLayer = stateLayer == null ? Color.Transparent
                : new(stateLayer.GetColorForState([global::Android.Resource.Attribute.StatePressed], Color.White));
            bool rippleOk = true;
            var rippleDetail = new StringBuilder();
            for (int slot = 0; slot < RatingControlGeometry.SlotCount; slot++)
            {
                var ripple = ratingView.SlotRippleForChecks(slot);
                var circle = ratingView.HoverCircleBoundsForChecks(slot);
                var slotRect = ratingView.SlotRectPx(slot);
                bool ok = ripple != null && ratingView.HighlightColorForChecks == pressedLayer
                    && Math.Abs(circle.Width() - Math.Min(slotRect.Width(), slotRect.Height())) <= 1
                    && Math.Abs(circle.Height() - Math.Min(slotRect.Width(), slotRect.Height())) <= 1
                    && Near(circle.CenterX(), slotRect.CenterX()) && Near(circle.CenterY(), slotRect.CenterY());
                if (!ok) rippleOk = false;
                rippleDetail.Append($"{slot}:{circle.Width()}x{circle.Height()} ");
            }
            Check($"{phase} 星级逐格状态层取按钮状态层色板", rippleOk && pressedLayer.A != 0,
                $"stateLayer={pressedLayer} ratingHighlight={ratingView.HighlightColorForChecks} circles={rippleDetail}");
        }

        // TOOLBAR-10：合体与命令按钮的字形必须与同一字号的无界参照一致（字号不被槽位缩放、墨迹不被裁切）。
        Check($"{phase} 筛选/更多合体字形与参照一致（未裁切）", ComboGlyphsMatch(root.FilterBar.ComboForChecks, density, out var comboDetail), comboDetail);
        bool commandGlyphs = true;
        var commandGlyphDetail = new StringBuilder();
        foreach (var (command, view) in root.ControlBar.CommandViewsForChecks)
        {
            bool ok = ButtonGlyphMatches(view, density, out var glyphDetail);
            if (!ok) commandGlyphs = false;
            commandGlyphDetail.Append(command).Append(':').Append(glyphDetail).Append(" | ");
        }
        Check($"{phase} 命令按钮字形与参照一致（未裁切）", commandGlyphs, commandGlyphDetail.ToString());

        // TOOLBAR-05 / TOOLBAR-10：EXIF 块底色取 color.toolbar.block.background，竖排无衬底、横排带衬底。
        var exifBlock = root.ControlBar.ExifBlock;
        Check($"{phase} EXIF 底色取 color.toolbar.block.background",
            exifBlock.BackdropColorForChecks == AndroidUiTheme.FromToken(DesignTokens.ColorToolbarBlockBackground)
            && exifBlock.HasBackdropForChecks == row,
            $"row={row} backdrop={exifBlock.HasBackdropForChecks} color={exifBlock.BackdropColorForChecks}");

        var controlDoc = root.ControlBar.DocumentSizeForChecks;
        var controlViewport = row ? panes.ControlContent.Width : panes.ControlContent.Height;
        int documentPx = root.ControlScrollHostForChecks.DocumentMainForChecks;
        Check($"{phase} 控制栏文档与滚动范围一致",
            Near(documentPx / density, controlDoc.Main)
            && (controlDoc.Main <= controlViewport + .5 || root.ControlScrollHostForChecks.IsScrollable),
            $"doc={controlDoc.Main:0.#} host={documentPx / density:0.#} viewport={controlViewport:0.#} "
            + $"scrollable={root.ControlScrollHostForChecks.IsScrollable} maxScroll={root.ControlScrollHostForChecks.MaxScrollForChecks}");

        var barKinds = root.ControlBar.GroupKindsForChecks.ToList();
        int ratingGroup = barKinds.IndexOf(ControlBarGroupKind.Rating);
        var ratingNodes = nodes.Where(node => node.Kind == ControlBarGroupKind.Rating.ToString()).ToList();
        if (ratingGroup >= 0)
        {
            var ratingFrame = groups[ratingGroup];
            double slotMain = row ? ratingFrame.Width : ratingFrame.Height;
            var ratingForm = RatingControlGeometry.ResolveForm(slotMain);
            bool ratingInside = true;
            foreach (var node in ratingNodes)
                if (node.X < ratingFrame.X - .6 || node.Y < ratingFrame.Y - .6
                    || node.X + node.Width > ratingFrame.MaxX + .6
                    || node.Y + node.Height > ratingFrame.MaxY + .6) ratingInside = false;
            bool formOk;
            if (ratingForm == RatingControlForm.Expanded)
            {
                formOk = ratingNodes.Count == 6 && Near(slotMain, RatingControlGeometry.ExpandedLength);
                foreach (var node in ratingNodes)
                    if (!Near(row ? node.Width : node.Height, RatingControlGeometry.Slot)
                        || !Near(row ? node.Height : node.Width, RatingControlGeometry.Thickness)) formOk = false;
            }
            else
            {
                formOk = ratingNodes.Count is 5 or 6 && slotMain < RatingControlGeometry.ExpandedLength;
                foreach (var node in ratingNodes)
                    if (node.Width > RatingControlGeometry.CompactGlyph + .6
                        || node.Height > RatingControlGeometry.CompactGlyph + .6) formOk = false;
            }
            Check($"{phase} 评分外框与节点形态", formOk && ratingInside,
                $"form={ratingForm} slot={slotMain:0.#} frame={ratingFrame} nodes={ratingNodes.Count} "
                + $"sizes={string.Join("/", ratingNodes.Select(n => $"{n.Width:0.#}x{n.Height:0.#}"))}");
        }
    }

    /// <summary>手机形态：筛选/更多、胶片条、分析栏、底行/控制列与主图避让逐个对照共享槽位。</summary>
    private static void CheckPhoneGeometry(AndroidNativeRootView root, MainViewModel main, double density, string phase)
    {
        bool portrait = root.Form == ScreenForm.PhonePortrait;
        var input = root.PhoneSlotsInput;
        // 独立从槽位输入重算 expected，再叠加 Android 状态栏顶行平台修正；不复用 root.PhoneSlots 自证。
        var slots = AndroidScreenForm.ApplyStatusBarTopRowShift(PhoneScreenLayout.Compute(in input), input);
        double widthDp = root.Width / density, heightDp = root.Height / density;
        Check($"{phase} 手机形态", root.Form != ScreenForm.Wide, $"portrait={portrait} form={root.Form} notchOnLeft={root.NotchOnLeft} "
            + $"window={widthDp:0.#}x{heightDp:0.#} safe={root.SafeInsets} fit={root.FitInsets}");
        CheckBarScrimLayers(root, phase);

        Check($"{phase} 主图满窗口", root.Photo.Width == root.Width && root.Photo.Height == root.Height,
            $"photo={root.Photo.Width}x{root.Photo.Height} root={root.Width}x{root.Height}");
        Check($"{phase} 适应区等于槽位避让", SameEdge(root.FitInsets, slots.PhotoAvoidance),
            $"fit={root.FitInsets} slots={slots.PhotoAvoidance}");
        Check($"{phase} 手机 Panes 不复用胶片/控制字段",
            !Has(root.Panes.ControlContent) && !Has(root.Panes.ControlBackground)
            && !Has(root.Panes.FileContent) && !Has(root.Panes.FileBar),
            $"panes={Describe(root.Panes)} film={slots.Filmstrip} trailing={slots.TrailingBar}");

        var filterFrame = FrameOf(root.FilterBar, root, density);
        var expectedFilter = portrait ? slots.Filter : slots.FilterMoreGroup;
        Check($"{phase} 筛选入口落位", SameFrame(filterFrame, expectedFilter),
            $"actual={filterFrame} expected={expectedFilter} phone={root.FilterBar.IsPhone}");
        if (portrait)
        {
            var more = FrameOf(root.PhoneMoreButton, root, density);
            Check($"{phase} 更多按钮落位", SameFrame(more, slots.More), $"actual={more} expected={slots.More}");
            Check($"{phase} 顶行筛选/EXIF/更多同高", Near(slots.Filter.Y, slots.More.Y)
                    && (!Has(slots.Exif) || Near(slots.Exif.Y + slots.Exif.Height / 2, slots.Filter.Y + 22)),
                $"filter={slots.Filter} exif={slots.Exif} more={slots.More}");
            var filterButton = FrameOf(root.FilterBar.FilterButtonForChecks, root, density);
            Check($"{phase} 筛选圆按钮为原生按钮且与槽位同形", SameFrame(filterButton, slots.Filter)
                    && Near(root.FilterBar.FilterButtonForChecks.ContentDescription is { Length: > 0 } ? 1 : 0, 1),
                $"button={filterButton} slot={slots.Filter} desc={root.FilterBar.FilterButtonForChecks.ContentDescription}");
        }
        else
        {
            var combo = root.FilterBar.ComboForChecks;
            var comboFrame = FrameOf(combo, root, density);
            var first = FrameOf(combo.First, root, density);
            var second = FrameOf(combo.Second, root, density);
            bool segments = SameFrame(first, slots.Filter) && SameFrame(second, slots.More);
            bool insideCombo = first.X >= comboFrame.X - .6 && first.MaxX <= comboFrame.MaxX + .6
                && second.X >= comboFrame.X - .6 && second.MaxX <= comboFrame.MaxX + .6;
            Check($"{phase} 筛选/更多 88 合体为原生分段", Near(slots.FilterMoreGroup.Width, 88)
                    && Near(slots.FilterMoreGroup.Height, 44) && SameFrame(comboFrame, slots.FilterMoreGroup),
                $"group={slots.FilterMoreGroup} combo={comboFrame} filter={slots.Filter} more={slots.More}");
            // FILTER-07：刘海在右镜像后两段位置随真实槽位交换，既分隔线也没有越出胶囊的多余装饰。
            Check($"{phase} 镜像下合体分段随槽位且无越界装饰",
                segments && insideCombo && combo.ChildCount == 2,
                $"first={first} second={second} expected={slots.Filter}/{slots.More} "
                + $"combo={comboFrame} mirrored={combo.Mirrored} children={combo.ChildCount}");
            Check($"{phase} 刘海侧列入口/上下张共用末端", !Has(slots.Navigation)
                    || Near(slots.FilterMoreGroup.MaxX, slots.Navigation.MaxX),
                $"entry={slots.FilterMoreGroup} nav={slots.Navigation} leadStrip={PhoneScreenLayout.LeadStripWidth(in input):0.#}");
        }

        if (portrait)
        {
            Check($"{phase} 筛选圆按钮字形与参照一致（未裁切）",
                ButtonGlyphMatches(root.FilterBar.FilterButtonForChecks, density, out var buttonDetail), buttonDetail);
        }
        else
        {
            Check($"{phase} 筛选/更多合体字形与参照一致（未裁切）",
                ComboGlyphsMatch(root.FilterBar.ComboForChecks, density, out var comboDetail), comboDetail);
        }
        Check($"{phase} 上下张合体字形与参照一致（未裁切）",
            ComboGlyphsMatch(root.PhoneNavigation, density, out var navDetail), navDetail);
        Check($"{phase} 可选命令组字形与参照一致（未裁切）",
            ComboGlyphsMatch(root.PhoneCommands, density, out var commandDetail), commandDetail);

        Check($"{phase} 胶片条贯穿主轴", SameFrame(FrameOf(root.Filmstrip, root, density), slots.Filmstrip),
            $"actual={FrameOf(root.Filmstrip, root, density)} expected={slots.Filmstrip} tier={root.Filmstrip.Tier}");
        var tier = FilmCardGeometry.ResolveTier(portrait ? widthDp : heightDp, portrait);
        var card = root.Filmstrip.CardAt(0);
        Check($"{phase} 卡片档位与尺寸", root.Filmstrip.Tier == tier && card != null
                && Near(card.Width / density, FilmCardGeometry.Width(tier))
                && Near(card.Height / density, FilmCardGeometry.Height(tier)),
            $"tier={root.Filmstrip.Tier} expected={tier} card={(card == null ? "null" : $"{card.Width / density:0.#}×{card.Height / density:0.#}")} "
            + $"capacity={root.Filmstrip.VisibleCapacity}");
        Check($"{phase} 可见容量 ≥5", root.Filmstrip.VisibleCapacity >= FilmCardGeometry.MinVisible,
            $"capacity={root.Filmstrip.VisibleCapacity} main={(portrait ? widthDp : heightDp):0.#}");

        if (main.IsAnalysisViewVisible)
        {
            Check($"{phase} 分析栏落位", SameFrame(FrameOf(root.AnalysisPane, root, density), slots.Analysis),
                $"actual={FrameOf(root.AnalysisPane, root, density)} expected={slots.Analysis}");
        }

        if (Has(slots.Navigation))
            Check($"{phase} 上下张落位", SameFrame(FrameOf(root.PhoneNavigation, root, density), slots.Navigation),
                $"actual={FrameOf(root.PhoneNavigation, root, density)} expected={slots.Navigation}");
        if (Has(slots.Commands))
            Check($"{phase} 可选命令组落位", SameFrame(FrameOf(root.PhoneCommands, root, density), slots.Commands),
                $"actual={FrameOf(root.PhoneCommands, root, density)} expected={slots.Commands}");
        if (Has(slots.Rating) && main.Settings.ShowRating)
        {
            var rating = FrameOf(root.PhoneRating, root, density);
            Check($"{phase} 星级控件落位", SameFrame(rating, slots.Rating),
                $"actual={rating} expected={slots.Rating} form={root.PhoneRating.Form}");
            var stateLayer = AndroidX.Core.Content.ContextCompat.GetColorStateList(_activity!, Resource.Color.pv_glass_state_layer);
            var pressedLayer = stateLayer == null ? Color.Transparent
                : new(stateLayer.GetColorForState([global::Android.Resource.Attribute.StatePressed], Color.White));
            var fill = ExpectedGlassButtonFill();
            bool rippleOk = true;
            var circleDetail = new StringBuilder();
            for (int slot = 0; slot < RatingControlGeometry.SlotCount; slot++)
            {
                var slotRect = root.PhoneRating.SlotRectPx(slot);
                if (slotRect.Width() <= 0) continue;
                var ripple = root.PhoneRating.SlotRippleForChecks(slot);
                var circle = root.PhoneRating.HoverCircleBoundsForChecks(slot);
                bool ok = ripple != null && root.PhoneRating.HighlightColorForChecks == pressedLayer
                    && Math.Abs(circle.Width() - Math.Min(slotRect.Width(), slotRect.Height())) <= 1
                    && Math.Abs(circle.Height() - Math.Min(slotRect.Width(), slotRect.Height())) <= 1
                    && Near(circle.CenterX(), slotRect.CenterX()) && Near(circle.CenterY(), slotRect.CenterY());
                if (!ok) rippleOk = false;
                circleDetail.Append($"{slot}:{circle.Width()}x{circle.Height()} ");
            }
            Check($"{phase} 手机玻璃控件同底且用同一状态层色板",
                root.PhoneRating.FillColorForChecks == fill && rippleOk
                && root.PhoneNavigation.FillColorForChecks == fill
                && root.PhoneCommands.FillColorForChecks == fill
                && (portrait
                    ? root.FilterBar.FilterButtonForChecks.FillColorForChecks == fill
                        && root.PhoneMoreButton.FillColorForChecks == fill
                    : root.FilterBar.ComboForChecks.FillColorForChecks == fill)
                && root.PhoneNavigation.First.FillColorForChecks == Color.Transparent,
                $"rating={root.PhoneRating.FillColorForChecks} expected={fill} "
                + $"filter={(portrait ? root.FilterBar.FilterButtonForChecks.FillColorForChecks.ToString() : root.FilterBar.ComboForChecks.FillColorForChecks.ToString())} "
                + $"nav={root.PhoneNavigation.FillColorForChecks} commands={root.PhoneCommands.FillColorForChecks} "
                + $"stateLayer={pressedLayer} circles={circleDetail}");
        }
        if (Has(slots.Exif) && slots.ExifItems > 0)
            Check($"{phase} EXIF 块落位", SameFrame(FrameOf(root.PhoneExif, root, density), slots.Exif),
                $"actual={FrameOf(root.PhoneExif, root, density)} expected={slots.Exif} items={slots.ExifItems} bare={!portrait}");
        Check($"{phase} 命中区 ≥44", HitTargetsAtLeast44(root, slots),
            $"filter={slots.Filter} more={slots.More} rating={slots.Rating} commands={slots.Commands} nav={slots.Navigation}");
    }

    private static bool HitTargetsAtLeast44(AndroidNativeRootView root, PhoneLayout slots)
    {
        foreach (var rect in new[] { slots.Filter, slots.More, slots.Rating, slots.Commands, slots.Navigation, slots.Exif })
        {
            if (!Has(rect)) continue;
            if (rect.Width < 43.4 || rect.Height < 43.4) return false;
        }
        return true;
    }

    private static bool Has(LayoutRect rect) => rect.Width > 0 && rect.Height > 0;

    /// <summary>胶囊内每段字形与同一字号的无界参照字形一致，且墨迹完整落在分段内。</summary>
    private static bool ComboGlyphsMatch(AndroidGlassCombo combo, double density, out string detail)
    {
        bool ok = combo.SegmentsForChecks.Count > 0;
        var details = new List<string>(combo.SegmentsForChecks.Count);
        foreach (var button in combo.SegmentsForChecks)
        {
            if (!ButtonGlyphMatches(button, density, out var segment)) ok = false;
            details.Add(segment);
        }
        detail = $"segments={string.Join(" | ", details)} mirrored={combo.Mirrored}";
        return ok;
    }

    /// <summary>
    /// 单个按钮的字形：把按钮真实绘制结果与同一字号、无边界约束的参照字形按设计锚点（排版框中心）对齐比较。
    /// 不要求墨迹中心等于按钮中心（字形本身可以不对称），但字号、墨迹四边余量、面积与逐像素覆盖必须与参照一致，
    /// 且墨迹完整落在图标槽与按钮内，因此对称裁切或按槽位缩放都会失败。
    /// </summary>
    private static bool ButtonGlyphMatches(AndroidGlassButton button, double density, out string detail)
    {
        if (button.IconForChecks is not { } icon)
        {
            detail = $"{button.Width}x{button.Height} 非字形图标";
            return true;
        }
        if (button.Width <= 0 || button.Height <= 0)
        {
            detail = "按钮未布局";
            return false;
        }
        var product = AndroidGlyphProbe.Capture(button.Width, button.Height, button.DrawContentForChecks);
        var slot = button.IconSlotForChecks;
        var reference = AndroidGlyphProbe.Reference(button.Context!, icon.Glyph, icon.SolidGlyph, icon.Digit,
            icon.FontSizePx, icon.DigitFontSizePx);
        var (ok, note) = AndroidGlyphProbe.Compare(product, reference,
            (slot.Left + slot.Right) / 2f, (slot.Top + slot.Bottom) / 2f, slot,
            button.Width, button.Height, density);
        // 字号必须是命令字形令牌的精确 dp 像素：槽位收窄时不得靠缩字适配。
        float expectedFont = AndroidUiTheme.Dp(button.Context!, DesignTokens.ToolbarCommandGlyphFontSize);
        bool font = Math.Abs(icon.FontSizePx - expectedFont) <= .01;
        detail = $"{button.Width}x{button.Height} font={icon.FontSizePx:0.#}px expected={expectedFont:0.#}px "
            + $"slot={icon.SlotPx}px {note}";
        return ok && font;
    }

    /// <summary>真实视图在根视图坐标中的 dp 矩形。</summary>
    private static LayoutRect FrameOf(View view, AndroidNativeRootView root, double density)
    {
        var child = new int[2];
        var owner = new int[2];
        view.GetLocationInWindow(child);
        root.GetLocationInWindow(owner);
        return new LayoutRect((child[0] - owner[0]) / density, (child[1] - owner[1]) / density,
            view.Width / density, view.Height / density);
    }

    private static bool SameFrame(LayoutRect a, LayoutRect b)
        => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Width, b.Width) && Near(a.Height, b.Height);

    private static bool SameEdge(EdgeInsets a, EdgeInsets b)
        => Near(a.Left, b.Left) && Near(a.Top, b.Top) && Near(a.Right, b.Right) && Near(a.Bottom, b.Bottom);

    private static string Describe(AndroidPaneFrames panes)
        => $"file={panes.FileBar} content={panes.FileContent} photo={panes.Photo} analysis={panes.Analysis} "
            + $"control={panes.ControlBackground}/{panes.ControlContent}";
}
