using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>真实窗口主界面布局验收：分区拼合、筛选条、胶片栏卡片、控制栏分组与主图导航几何。</summary>
internal static class NativeMainLayoutAcceptance
{
    /// <summary>代码内固定几何允许的误差。</summary>
    private const double Exact = .01;
    /// <summary>四种参考窗口尺寸，覆盖两向布局、窄宽度常驻标题栏及恢复悬停覆盖层。</summary>
    private static readonly CGSize[] WindowSizes = [new(1200, 850), new(830, 650), new(560, 480), new(700, 480)];

    /// <summary>在隔离设置与只读素材下核对两向布局的四种窗口尺寸，输出实际几何，结束后恢复设置工作副本。</summary>
    internal static async Task RunAsync(MacMainWindow window, string output)
    {
        var settings = window.Main.Settings;
        var snapshot = settings.CreateSnapshot();
        var layoutMode = settings.LayoutMode;
        bool similarityOpen = window.Main.FileVM.FilterBar.IsSimilarityPanelOpen;
        bool filmVisible = window.Main.IsThumbnailViewVisible;
        bool controlVisible = window.Main.IsControlViewVisible;
        var geometry = new List<string>();
        var observations = new List<string>();
        var first = window.Main.CurrentFile ?? throw new InvalidOperationException("主界面布局验收缺少首图");
        Directory.CreateDirectory(output);
        try
        {
            if (window.Main.FileVM.ThumbnailList.FilteredFiles.Count < 2) throw new InvalidOperationException("主界面布局验收需要至少两张照片");
            settings.ShowRating = true;
            window.Main.IsThumbnailViewVisible = true;
            window.Main.IsControlViewVisible = true;
            // 旧壳基线（Design/baseline/avalonia/legacy-layout-geometry.json）把相似聚类面板设为收起，
            // 原生侧必须同口径，否则文件栏宽度会多出面板的交叉轴长度，整组几何对不上。
            window.Main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
            foreach (var entry in settings.ExifDisplayItems)
                entry.IsEnabled = entry.PropertyName is "Aperture" or "ExposureTime" or "Iso" or "EquivFocalLength";
            foreach (var hotkey in settings.Hotkeys)
                hotkey.IsDisplay = hotkey.Command is "Previous" or "Next" or "Exchange" or "Fit" or "ZoomInPreset" or "ZoomOutPreset";
            await Task.Delay(300);
            foreach (var mode in new[] { LayoutMode.Col, LayoutMode.Row })
            {
                settings.LayoutMode = mode;
                foreach (var size in WindowSizes)
                {
                    await SettleAsync(window, size);
                    await VerifyLayoutAsync(window, mode, size, geometry, observations);
                    NativeSnapshots.SaveMain(window, System.IO.Path.Combine(output, $"native-layout-{mode.ToString().ToLowerInvariant()}-{size.Width:0}.png"));
                    await VerifyNavigationAsync(window, observations);
                    if (Environment.GetCommandLineArgs().Contains("--pv-layout-hold") && size.Width == 830)
                    {
                        Console.WriteLine($"[NativeLayout] READY screenshot {mode}/830");
                        await Task.Delay(30000);
                    }
                }
                await VerifyPaneReleaseAsync(window, mode, observations);
            }
            await VerifyAutoOrientationAsync(window, geometry, observations);
            await VerifyStaticMatrixAsync(window, geometry, observations);
            await VerifyCommandButtonsAsync(window, observations);
            observations.Add("空控制组状态未在本验收改写：无分组时 MacControlBar.Measure 的 legacy 20 点外边距保持原样，仍由 --pv-native-toolbar 覆盖");
        }
        finally
        {
            window.Main.FileVM.FilterBar.IsSimilarityPanelOpen = similarityOpen;
            settings.LayoutMode = layoutMode;
            settings.ApplySnapshot(snapshot);
            window.Main.IsThumbnailViewVisible = filmVisible;
            window.Main.IsControlViewVisible = controlVisible;
            window.Main.CurrentFile = first;
            await Task.Delay(250);
            await File.WriteAllLinesAsync(System.IO.Path.Combine(output, "native-layout-geometry.txt"), geometry);
            await File.WriteAllLinesAsync(System.IO.Path.Combine(output, "native-layout-checks.txt"), observations);
        }
    }

    /// <summary>核对外壳分区、筛选/胶片/控制栏几何全部取自共享计算，并输出实际 frame。</summary>
    private static async Task VerifyLayoutAsync(MacMainWindow window, LayoutMode mode, CGSize size, List<string> geometry, List<string> observations)
    {
        bool row = mode == LayoutMode.Row;
        string scope = $"{mode}-{size.Width:0}x{size.Height:0}";
        var root = window.Window.ContentView ?? throw new InvalidOperationException("原生内容视图缺失");
        if (window.Main.IsRowLayout != row) throw new InvalidOperationException($"{scope} 布局方向未按设置生效");
        var filePane = root.Subviews.Single(view => view.Subviews.Contains(window.FilmstripScroll));
        var photoPane = root.Subviews.Single(view => view.Subviews.Contains(window.Photo));
        var controlScroll = window.ControlScroll;
        int controlIndex = Array.IndexOf(root.Subviews, controlScroll);
        if (controlIndex < 1 || controlIndex + 2 >= root.Subviews.Length)
            throw new InvalidOperationException($"{scope} 根视图分区顺序改变：控制栏索引 {controlIndex}");
        var fileDivider = root.Subviews[controlIndex + 1];
        var controlDivider = root.Subviews[controlIndex + 2];
        Record(geometry, scope, "root", root.Bounds);
        Record(geometry, scope, "photoViewport", window.Photo.ConvertRectToView(window.Photo.Bounds, root));
        Record(geometry, scope, "filePane", filePane.Frame);
        Record(geometry, scope, "photoFit", window.Photo.FitArea);
        Record(geometry, scope, "controlScroll", controlScroll.Frame);
        Record(geometry, scope, "analysis", window.Analysis.Frame);

        double width = (double)root.Bounds.Width, height = (double)root.Bounds.Height;
        var panes = ExpectedPanes(window, row, width, height);
        RequireFrame(filePane.Frame, ToRect(panes.FileBar), $"{scope} 文件栏未落在共享分区");
        RequireFrame(fileDivider.Frame, ToRect(panes.FileDivider), $"{scope} 文件栏分隔线未落在共享分区");
        RequireFrame(controlScroll.Frame, ToRect(panes.ControlContent), $"{scope} 控制栏内容未落在共享工具区");
        if (window.Main.IsAnalysisViewVisible) RequireFrame(window.Analysis.Frame, ToRect(panes.Analysis), $"{scope} 分析栏未落在共享分区");
        RequireFrame(photoPane.Frame, new CGRect(0, 0, width, height), $"{scope} 主图视图未铺满根视图");
        RequireFrame(window.Photo.Frame, photoPane.Bounds, $"{scope} 主图表面未铺满根视图");
        var insets = window.Photo.FitInsets;
        RequireFrame(new CGRect((double)insets.Left, (double)insets.Top,
                width - (double)insets.Left - (double)insets.Right, height - (double)insets.Top - (double)insets.Bottom),
            ToRect(panes.Photo), $"{scope} 主图适应区未等于共享主图分区");

        VerifyControlBar(window, scope, geometry, panes.ControlContent, row);
        VerifyFilterBar(window, row, scope, filePane, panes.FileContent, geometry);
        VerifyTitlebar(window, row, scope, filePane, panes, geometry);
        await VerifyFilmstripAsync(window, row, scope, filePane, panes, geometry);
        VerifyMaterialCondition(window, row, scope, panes, observations);
        window.ShowToast("布局提示");
        var toast = window.StatusToast.Frame;
        var photoFit = window.FitArea;
        if (Math.Abs((double)(toast.X + toast.Width / 2 - photoFit.X - photoFit.Width / 2)) > .5
            || toast.Y < photoFit.Y || toast.Y + toast.Height > photoFit.Y + photoFit.Height + .5)
            throw new InvalidOperationException($"{scope} 提示条未按适应区落位：{toast} / {photoFit}");
        window.StatusToast.Hidden = true;
        observations.Add($"{scope}: 分区/主图适应区/筛选/胶片/控制栏均与共享几何一致；胶片大档固定；工具区材质按分析或实际 EXIF 出现");
        observations.Add($"{scope}: EXIF 原生 NSBox 分组、横竖统一原生玻璃评分胶囊与 44 点原生玻璃圆钮均通过断言");
    }

    /// <summary>按共享输入重算本帧分区：文件栏/控制栏/分析栏与主图适应区。</summary>
    private static ShellPaneFrames ExpectedPanes(MacMainWindow window, bool row, double width, double height)
    {
        var main = window.Main;
        bool file = main.IsThumbnailViewVisible, control = main.IsControlViewVisible, analysis = main.IsAnalysisViewVisible;
        double filterMain = window.FilterLayout.MainLength;
        double similarityColumn = 0;
#if PHOTOVIEWER_DINO
        bool similarityOpen = file && main.FileVM.FilterBar.IsSimilarityPanelOpen;
        var panel = main.FileVM.SimilarityPanel;
        if (similarityOpen)
        {
            double dock = panel.HasItems && panel.ShowActionArea ? window.Similarity.DockMeasureWidth() : 0;
            similarityColumn = row
                ? SimilarityPanelGeometry.RowWidth(panel.HasItems, panel.SimilarItems.Count, width, dock)
                : (double)window.Similarity.CrossSize + SimilarityPanelGeometry.Margin;
        }
#endif
        double fileMain = file ? MainScreenLayout.FileBarMainLength(row, filterMain, similarityColumn) : 0;
        bool joined = control && analysis;
        var measure = control ? window.ControlBar.Measure(row, row ? width : height, joined) : CGSize.Empty;
        double controlMain = control ? MainScreenLayout.ControlBarMainLength(row ? (double)measure.Height : (double)measure.Width) : 0;
        double analysisMain = row ? width : height;
        double analysisCross = analysis ? window.Analysis.CrossSize(analysisMain, joined) : 0;
        double titlebarInset = window.IsTitlebarPersistent ? DesignTokens.ShellTitlebarHeight : 0;
        return ShellPaneLayout.Partition(row, width, height, new EdgeInsets(0, titlebarInset, 0, 0),
            fileMain, controlMain, analysisCross);
    }

    /// <summary>共享矩形转原生 frame。</summary>
    private static CGRect ToRect(LayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>核对控制栏文档按共享分组尺寸换行/居中，子控件落入共享矩形。</summary>
    private static void VerifyControlBar(MacMainWindow window, string scope, List<string> geometry, LayoutRect content, bool row)
    {
        var bar = window.ControlBar;
        var clip = window.ControlScroll.ContentView.Bounds;
        Record(geometry, scope, "controlClip", clip);
        Record(geometry, scope, "controlDocument", bar.Frame);
        if ((double)bar.Frame.Width + Exact < (double)clip.Width || (double)bar.Frame.Height + Exact < (double)clip.Height)
            throw new InvalidOperationException($"{scope} 控制栏文档小于裁剪视口，右侧/底部出现空带：{bar.Frame}/{clip}");
        bool includeRating = window.Main.Settings.ShowRating;
        if (!bar.Commands.ContainsKey("Previous") || bar.Commands.Count < 2)
            throw new InvalidOperationException($"{scope} 控制栏缺少命令按钮");
        int expectedGroups = (bar.ExifItemCount > 0 ? 1 : 0) + (includeRating ? 1 : 0) + (bar.Commands.Count > 0 ? 1 : 0);
        if (bar.Groups.Count != expectedGroups) throw new InvalidOperationException($"{scope} 控制栏分组数 {bar.Groups.Count}，预期 {expectedGroups}");
        var sizes = new List<ToolbarGroupSize>();
        foreach (var kind in bar.GroupKinds)
            sizes.Add(kind switch
            {
                ControlBarGroupKind.Exif => ToolbarFlowLayout.Exif(bar.ExifItemCount, row, bare: !row),
                ControlBarGroupKind.Rating => ToolbarFlowLayout.Rating(RatingControlGeometry.ExpandedLength),
                _ => ToolbarFlowLayout.Commands(bar.Commands.Count),
            });
        bool joined = window.Main.IsControlViewVisible && window.Main.IsAnalysisViewVisible;
        var document = ToolbarFlowLayout.Arrange(sizes, row, row ? (double)clip.Width : (double)clip.Height, joined ? 0 : null);
        for (int index = 0; index < bar.Groups.Count; index++)
        {
            var group = bar.Groups[index];
            var expected = ToRect(document.Groups[index]);
            Record(geometry, scope, $"control.group{index}", group.Frame);
            RequireFrame(group.Frame, expected, $"{scope} 控制组 {index} 未落在共享流式矩形");
            VerifyControlGroupContents(bar, index, row, geometry, scope);
        }
    }

    /// <summary>核对 EXIF 项/评分槽位/命令按钮的实际 frame。</summary>
    private static void VerifyControlGroupContents(MacControlBar bar, int index, bool row, List<string> geometry, string scope)
    {
        var group = bar.Groups[index];
        var kind = bar.GroupKinds[index];
        switch (kind)
        {
            case ControlBarGroupKind.Exif:
                double blockMain = row ? (double)group.Bounds.Width : (double)group.Bounds.Height;
                var exif = group as MacControlBar.ExifBlockView
                    ?? throw new InvalidOperationException($"{scope} EXIF 未使用原生 NSBox 分组");
                for (int item = 0; item < exif.Tiles.Count; item++)
                {
                    var expected = ExifBlockGeometry.ItemRect(item, exif.Tiles.Count, row, blockMain, bare: !row);
                    Record(geometry, scope, $"control.group{index}.item{item}", exif.Tiles[item].Frame);
                    RequireFrame(exif.Tiles[item].Frame, ToRect(expected), $"{scope} EXIF 项 {item}");
                }
                if (exif.BoxType != NSBoxType.NSBoxPrimary || exif.TitlePosition != NSTitlePosition.NoTitle
                    || exif.ContentViewMargins != CGSize.Empty || exif.Appearance?.Name != NSAppearance.NameDarkAqua
                    || exif.Bare == row)
                    throw new InvalidOperationException($"{scope} EXIF 原生分组外观或竖排无衬底规则错误，或叠加了第二份边距");
                break;
            case ControlBarGroupKind.Rating:
                var control = group.Subviews[0];
                var form = RatingControlGeometry.ResolveForm(row ? (double)group.Bounds.Width : (double)group.Bounds.Height);
                var controlFrame = RatingControlGeometry.ControlFrame(
                    new LayoutRect(0, 0, (double)group.Bounds.Width, (double)group.Bounds.Height), row);
                Record(geometry, scope, $"control.group{index}.rating", control.Frame);
                RequireFrame(control.Frame, ToRect(controlFrame), $"{scope} 评分控件外框未取共享槽位");
                if (control is not MacControlBar.RatingControlView ratingControl)
                    throw new InvalidOperationException($"{scope} 评分组缺少星级控件");
                if (ratingControl.Style != NSGlassEffectViewStyle.Regular
                    || Math.Abs((double)ratingControl.CornerRadius - RatingControlGeometry.Thickness / 2) > Exact
                    || ratingControl.ContentView is not { } content)
                    throw new InvalidOperationException($"{scope} 评分控件外框未使用系统玻璃胶囊");
                double capsuleCross = row ? (double)control.Bounds.Height : (double)control.Bounds.Width;
                if (Math.Abs((double)ratingControl.CornerRadius * 2 - capsuleCross) > Exact)
                    throw new InvalidOperationException($"{scope} 评分胶囊圆角未等于控件厚度的一半：{ratingControl.CornerRadius}/{capsuleCross}");
                if (form == RatingControlForm.Expanded)
                {
                    if (bar.Ratings.Count != 6) throw new InvalidOperationException($"{scope} 展开态评分槽位不是 6 个");
                    double main = row ? (double)control.Bounds.Width : (double)control.Bounds.Height;
                    double slotCross = row ? (double)control.Bounds.Height : (double)control.Bounds.Width;
                    foreach (var button in bar.Ratings)
                    {
                        var slot = RatingControlGeometry.SlotRect((int)button.Tag, main, slotCross, row, RatingControlGeometry.Slot);
                        if (button.Hidden) throw new InvalidOperationException($"{scope} 展开态评分槽位被隐藏");
                        Record(geometry, scope, $"control.group{index}.rating{button.Tag}", button.Frame);
                        RequireFrame(button.Frame, ToRect(slot), $"{scope} 评分槽位 {button.Tag}");
                        if (!ReferenceEquals(button.Superview, content) || button is not MacControlBar.ToolbarButton native
                            || !native.Bordered || !native.ShowsBorderOnlyWhileMouseInside
                            || native.BezelStyle != NSBezelStyle.Rounded || native.BorderShape != NSControlBorderShape.Circle)
                            throw new InvalidOperationException($"{scope} 评分槽位 {button.Tag} 未使用玻璃内容区里的原生圆形悬停按钮");
                    }
                }
                else
                    throw new InvalidOperationException($"{scope} 桌面评分未保持展开态");
                break;
            case ControlBarGroupKind.Command:
                double cross = row ? (double)group.Bounds.Height : (double)group.Bounds.Width;
                double offset = Math.Max(0, (cross - ToolbarFlowLayout.CommandSize) / 2);
                for (int button = 0; button < group.Subviews.Length; button++)
                {
                    double main = ToolbarFlowLayout.CommandOffset(button);
                    var expected = row
                        ? new CGRect(main, offset, ToolbarFlowLayout.CommandSize, ToolbarFlowLayout.CommandSize)
                        : new CGRect(offset, main, ToolbarFlowLayout.CommandSize, ToolbarFlowLayout.CommandSize);
                    RequireFrame(group.Subviews[button].Frame, expected, $"{scope} 命令按钮 {button}");
                    if (group.Subviews[button] is not NSGlassEffectView glass
                        || glass.ContentView is not MacControlBar.ToolbarButton command
                        || command.BezelStyle != NSBezelStyle.Rounded || command.BorderShape != NSControlBorderShape.Circle
                        || !command.ShowsBorderOnlyWhileMouseInside
                        || glass.Style != NSGlassEffectViewStyle.Regular)
                        throw new InvalidOperationException($"{scope} 命令按钮 {button} 未使用原生玻璃视图与圆钮");
                }
                break;
        }
    }

    /// <summary>核对筛选控件按共享 WideFilterLayout 落位、toggle 同宽与标题栏挖空联合范围。</summary>
    private static void VerifyFilterBar(MacMainWindow window, bool row, string scope, NSView filePane, LayoutRect content, List<string> geometry)
    {
        var frames = window.FilterLayout;
        NSView filterView;
        if (window.FilterCapsuleMode)
        {
            var popups = filePane.Subviews.OfType<NSGlassEffectView>().Select(glass => glass.ContentView).OfType<NSPopUpButton>().Where(popup => !popup.Hidden).ToArray();
            if (popups.Length != 1) throw new InvalidOperationException($"{scope} 单胶囊回退时有 {popups.Length} 个可见下拉");
            filterView = window.FilterCapsuleGlass;
            RequireFrame(filterView.Frame, new CGRect(content.X + frames.Control.X, content.Y + frames.Control.Y, frames.Control.Width, frames.Control.Height),
                $"{scope} 单胶囊外框");
            geometry.Add($"{scope}\tfilter.capsule\t({filterView.Frame.X:0.##},{filterView.Frame.Y:0.##},{filterView.Frame.Width:0.##},{filterView.Frame.Height:0.##})");
        }
        else
        {
            var segmented = filePane.Subviews.OfType<NSGlassEffectView>().Select(glass => glass.ContentView).OfType<NSSegmentedControl>().SingleOrDefault()
                ?? throw new InvalidOperationException($"{scope} 三合体未使用原生 NSSegmentedControl");
            filterView = window.FilterSegmentedGlass;
            if (segmented.SegmentCount != 3 || segmented.BorderShape != NSControlBorderShape.Capsule)
                throw new InvalidOperationException($"{scope} 筛选连续控件段数或胶囊外形错误");
            RequireFrame(filterView.Frame, new CGRect(content.X + frames.Control.X, content.Y + frames.Control.Y,
                frames.Control.Width, frames.Control.Height), $"{scope} 筛选连续控件外框");
            var expected = new[] { frames.Sort, frames.Order, frames.Rating };
            var names = new[] { "sort", "order", "rating" };
            for (int index = 0; index < 3; index++)
            {
                if (Math.Abs((double)segmented.GetWidth(index) - expected[index].Width) > Exact)
                    throw new InvalidOperationException($"{scope} 筛选段 {index} 宽度错误");
                var frame = new CGRect(segmented.Frame.X + (nfloat)expected[index].X,
                    segmented.Frame.Y + (nfloat)expected[index].Y, (nfloat)expected[index].Width, (nfloat)expected[index].Height);
                Record(geometry, scope, $"filter.{names[index]}", frame);
            }
            geometry.Add($"{scope}\tfilter.segments\t({filterView.Frame.X:0.##},{filterView.Frame.Y:0.##},{filterView.Frame.Width:0.##},{filterView.Frame.Height:0.##})");
        }
        if (Math.Abs((double)filterView.Frame.Height - (double)frames.Control.Height) > Exact)
            throw new InvalidOperationException($"{scope} 筛选控件高不是 {frames.Control.Height}：{filterView.Frame}");
#if PHOTOVIEWER_DINO
        if (window.SimilarityToggle.Hidden)
            throw new InvalidOperationException($"{scope} 文件栏可见时相似开关被隐藏");
        var toggle = window.SimilarityToggleGlass;
        RequireFrame(toggle.Frame, new CGRect(content.X + frames.Toggle.X, content.Y + frames.Toggle.Y, frames.Toggle.Width, frames.Toggle.Height),
            $"{scope} 相似聚类开关");
        double expectedToggleWidth = row ? DesignTokens.FilterToggleWidth : FilmCardGeometry.Width(FilmCardTier.Large);
        if (Math.Abs((double)toggle.Frame.Width - expectedToggleWidth) > Exact || Math.Abs((double)toggle.Frame.Height - WideFilterLayout.Height) > Exact)
            throw new InvalidOperationException($"{scope} 相似聚类开关不是 {expectedToggleWidth}×{WideFilterLayout.Height}：{toggle.Frame}");
        geometry.Add($"{scope}\tfilter.toggle\t({toggle.Frame.X:0.##},{toggle.Frame.Y:0.##},{toggle.Frame.Width:0.##},{toggle.Frame.Height:0.##})");
        if (row && Math.Abs((double)toggle.Frame.Y - (double)filterView.Frame.Y) > Exact)
            throw new InvalidOperationException($"{scope} 行布局 toggle 未与筛选控件同轴");
        if (!row)
        {
            if (Math.Abs((double)filterView.Frame.Width - 96) > Exact || Math.Abs((double)filterView.Frame.X - 10) > Exact)
                throw new InvalidOperationException($"{scope} 列布局筛选外框不是 96 宽居中");
            if (Math.Abs((double)filterView.Frame.Y - DesignTokens.ShellFilterLeadingInset) > Exact)
                throw new InvalidOperationException($"{scope} 列布局筛选控件 y 不是 {DesignTokens.ShellFilterLeadingInset}");
            if (Math.Abs((double)toggle.Frame.Y - (double)(filterView.Frame.Y + 44 + 8)) > Exact)
                throw new InvalidOperationException($"{scope} 列布局开关未在筛选组下 8 点");
        }
#else
        if (filePane.Subviews.OfType<NSButton>().Any(button => !button.Hidden && button.Title == "相似聚类"))
            throw new InvalidOperationException($"{scope} 标准版残留相似开关");
#endif
        var count = window.CountView;
        if (count.Hidden || count.Superview is not { } countHost
            || count.Frame.X < countHost.Bounds.X || count.Frame.Y < countHost.Bounds.Y
            || count.Frame.X + count.Frame.Width > countHost.Bounds.X + countHost.Bounds.Width
            || count.Frame.Y + count.Frame.Height > countHost.Bounds.Y + countHost.Bounds.Height)
            throw new InvalidOperationException($"{scope} 计数未完整显示在冻结筛选外框内：{count.Frame} / {count.Superview?.Bounds}");
        var union = window.FilterUnionFrame;
        if (Math.Abs((double)union.X - (content.X + frames.Control.X)) > Exact || union.Width <= 0)
            throw new InvalidOperationException($"{scope} 标题挖空联合范围与筛选控件不一致：{union}");
    }

    /// <summary>核对文件栏/工具区材质出现条件与唯一外边界。</summary>
    private static void VerifyMaterialCondition(MacMainWindow window, bool row, string scope, ShellPaneFrames panes, List<string> observations)
    {
        if (!(window.FileBarBackground is NSVisualEffectView fileMaterial) || fileMaterial.Hidden && window.Main.IsThumbnailViewVisible)
            throw new InvalidOperationException($"{scope} 文件栏未使用可见的系统材质宿主");
        bool showsTool = window.Main.IsAnalysisViewVisible
            || window.Main.IsControlViewVisible && window.ControlBar.ExifItemCount > 0;
        if ((window.ToolBackground.Hidden == showsTool))
            throw new InvalidOperationException($"{scope} 工具区材质出现条件错误：shows={showsTool}, hidden={window.ToolBackground.Hidden}");
        if (showsTool)
        {
            RequireFrame(window.ToolBackground.Frame, ToRect(panes.ControlBackground), $"{scope} 工具区材质宿主未铺满共同区域");
            if (window.ToolDivider.Hidden) throw new InvalidOperationException($"{scope} 工具区缺少朝主图外边界");
            var divider = window.ToolDivider.Frame;
            if (row ? Math.Abs((double)divider.Y - (double)panes.ControlBackground.Y) > .5 || Math.Abs((double)divider.Height - MainScreenLayout.Divider) > .5
                : Math.Abs((double)divider.X - (double)panes.ControlBackground.X) > .5 || Math.Abs((double)divider.Width - MainScreenLayout.Divider) > .5)
                throw new InvalidOperationException($"{scope} 工具区外边界未落在朝主图一侧：{divider}");
        }
        else if (!window.ToolDivider.Hidden)
            throw new InvalidOperationException($"{scope} 纯评分/命令时仍画工具区外边界");
        if (!showsTool && window.Main.IsControlViewVisible
            && window.ControlBar.GroupKinds.Any(kind => kind is not (ControlBarGroupKind.Rating or ControlBarGroupKind.Command)))
            throw new InvalidOperationException($"{scope} 无 EXIF 与分析栏时控制栏仍带其他分组：{string.Join(",", window.ControlBar.GroupKinds)}");
        observations.Add($"{scope}: 文件栏材质={fileMaterial.Material}, 工具区材质出现={showsTool}");
    }

    /// <summary>
    /// SHELL-02：标题栏跨度、毛玻璃背景与标题文字。默认覆盖层高 shell.titlebar.height 且贴合窗口顶边；
    /// 行布局宽度足够时挖空新筛选组联合范围，窄宽度改为占布局高度的常驻通栏；列布局为通栏悬停覆盖层。
    /// </summary>
    private static void VerifyTitlebar(MacMainWindow window, bool row, string scope, NSView filePane,
        ShellPaneFrames panes, List<string> geometry)
    {
        var root = window.Window.ContentView ?? throw new InvalidOperationException("原生内容视图缺失");
        double width = (double)root.Bounds.Width, height = DesignTokens.ShellTitlebarHeight;
        bool persistent = window.IsTitlebarPersistent;
        var zones = window.DragZones;
        if (zones.Count != 2) throw new InvalidOperationException($"{scope} 拖动区数量为 {zones.Count}，预期 2");
        var material = window.TitlebarMaterialView;
        var label = window.TitlebarLabel;
        var union = window.FilterUnionFrame;
        foreach (var zone in zones)
        {
            if (Math.Abs((double)zone.Frame.Y) > Exact || Math.Abs((double)zone.Frame.Height - height) > Exact)
                throw new InvalidOperationException($"{scope} 拖动区未贴合窗口顶边且高 {height} 点：{zone.Frame}");
            Record(geometry, scope, ReferenceEquals(zone, zones[0]) ? "titlebar.dragLeft" : "titlebar.dragRight", zone.Frame);
        }
        if (Math.Abs((double)zones[0].Frame.X) > Exact) throw new InvalidOperationException($"{scope} 左侧拖动区未从窗口左缘起：{zones[0].Frame}");
        if (Math.Abs((double)zones[1].Frame.X + (double)zones[1].Frame.Width - width) > Exact)
            throw new InvalidOperationException($"{scope} 右侧拖动区未止于窗口右缘：{zones[1].Frame}");
        if (persistent)
        {
            if (Math.Abs((double)zones[0].Frame.Width - width) > Exact || Math.Abs((double)zones[1].Frame.Width) > Exact)
                throw new InvalidOperationException($"{scope} 窄宽度常驻栏未提供完整通栏拖动区：{zones[0].Frame}/{zones[1].Frame}");
        }
        else if (row && window.Main.IsThumbnailViewVisible)
        {
            if (Math.Abs((double)zones[0].Frame.Width - union.X) > Exact)
                throw new InvalidOperationException($"{scope} 左侧拖动区未止于新筛选组左缘：{zones[0].Frame}/{union}");
            if (Math.Abs((double)zones[1].Frame.X - (double)union.X - (double)union.Width) > Exact)
                throw new InvalidOperationException($"{scope} 右侧拖动区未从新筛选组右缘起：{zones[1].Frame}/{union}");
        }
        RequireFrame(material.Frame, new CGRect(0, 0, width, height), $"{scope} 标题栏毛玻璃层未铺满窗口宽 {width}×{height}");
        Record(geometry, scope, "titlebar.material", material.Frame);
        Record(geometry, scope, "titlebar.title", label.Frame);

        int materialIndex = Array.IndexOf(root.Subviews, material);
        int leftIndex = Array.IndexOf(root.Subviews, zones[0]);
        int rightIndex = Array.IndexOf(root.Subviews, zones[1]);
        int labelIndex = Array.IndexOf(root.Subviews, label);
        int photoIndex = Array.IndexOf(root.Subviews, window.Photo.Superview);
        if (photoIndex >= materialIndex || materialIndex >= leftIndex || leftIndex >= rightIndex || rightIndex >= labelIndex)
            throw new InvalidOperationException($"{scope} 标题栏层序不是 内容 < 毛玻璃 < 拖动区 < 标题文字：{photoIndex}/{materialIndex}/{leftIndex}/{rightIndex}/{labelIndex}");

        var zoom = window.Window.StandardWindowButton(NSWindowButton.ZoomButton)
            ?? throw new InvalidOperationException($"{scope} 窗口缺少缩放按钮，无法核对标题文字让位");
        var zoomFrame = root.ConvertRectFromView(zoom.ConvertRectToView(zoom.Bounds, null), null);
        double zoomRight = (double)zoomFrame.X + (double)zoomFrame.Width;
        double expectedLeft = zoomRight + DesignTokens.ShellTitlebarTitleLeftInset;
        if ((double)label.Frame.X < expectedLeft - Exact)
            throw new InvalidOperationException($"{scope} 标题文字压住了交通灯或未留 12 点间距：{label.Frame} / 交通灯 {zoomFrame}");
        double labelCentre = (double)label.Frame.Y + (double)label.Frame.Height / 2;
        double zoomCentre = (double)zoomFrame.Y + (double)zoomFrame.Height / 2;
        if (Math.Abs(labelCentre - zoomCentre) > .5)
            throw new InvalidOperationException($"{scope} 标题文字中线未对齐交通灯中线：{labelCentre} / {zoomCentre}");
        double groupLeft = union.X;
        if (persistent)
        {
            if (zoomRight <= groupLeft)
                throw new InvalidOperationException($"{scope} 常驻标题栏触发条件不成立：交通灯 {zoomRight} 未与新筛选组 {groupLeft} 相交");
            if (Math.Abs((double)filePane.Frame.Y - height) > Exact)
                throw new InvalidOperationException($"{scope} 文件栏未下移到常驻标题栏下方：{filePane.Frame}");
        }
        else if (row && window.Main.IsThumbnailViewVisible && (double)label.Frame.Width >= 24
            && (double)label.Frame.X + (double)label.Frame.Width > groupLeft - DesignTokens.ShellTitlebarTitleLeftInset + Exact)
        {
            throw new InvalidOperationException($"{scope} 行布局标题文字压住了筛选控件：{label.Frame} / 筛选组左缘 {groupLeft}");
        }

        window.ShowTitlebarForTesting(true);
        try
        {
            bool backdrop = !row || persistent;
            if (material.Hidden != !backdrop)
                throw new InvalidOperationException($"{scope} 毛玻璃背景显隐与跨度不符（{(backdrop ? "应有" : "应无")}背景）：{material.Hidden}");
            foreach (var zone in zones)
                if (zone.Hidden) throw new InvalidOperationException($"{scope} 悬停热区后拖动区仍收起");
            if (material.HitTest(new CGPoint(material.Bounds.Width / 2, material.Bounds.Height / 2)) != null)
                throw new InvalidOperationException($"{scope} 标题栏毛玻璃背景拦截了命中");
            if (label.HitTest(new CGPoint(label.Bounds.Width / 2, label.Bounds.Height / 2)) != null)
                throw new InvalidOperationException($"{scope} 标题文字拦截了命中，点击落不到拖动区");
            if (!label.Hidden && label.StringValue != window.Window.Title)
                throw new InvalidOperationException($"{scope} 标题文字与系统窗口标题不一致：{label.StringValue} / {window.Window.Title}");
            bool fits = (double)label.Frame.Width >= 24;
            if (label.Hidden == fits)
                throw new InvalidOperationException($"{scope} 标题文字显隐未按可用宽度 {label.Frame.Width:0.##} 判定：Hidden={label.Hidden}");
        }
        finally { window.ShowTitlebarForTesting(false); }
        if (persistent)
        {
            if (material.Hidden || zones.Any(zone => zone.Hidden))
                throw new InvalidOperationException($"{scope} 热区退出后窄宽度常驻栏被收起");
        }
        else if (!material.Hidden || !label.Hidden || zones.Any(zone => !zone.Hidden))
        {
            throw new InvalidOperationException($"{scope} 收起标题栏后仍有可见的覆盖层");
        }
    }

    /// <summary>核对胶片栏大档固定、首卡内容留白、文档长度与卡片内部结构，并输出前两张卡片实际几何。</summary>
    private static async Task VerifyFilmstripAsync(MacMainWindow window, bool row, string scope, NSView filePane,
        ShellPaneFrames panes, List<string> geometry)
    {
        if (FilmCardGeometry.Width(FilmCardTier.Large) != 96 || FilmCardGeometry.Height(FilmCardTier.Large) != 144
            || FilmCardGeometry.MainLead(FilmCardTier.Large, true) != 10 || FilmCardGeometry.MainLead(FilmCardTier.Large, false) != 5
            || FilmCardGeometry.MainTrail(FilmCardTier.Large, false) != 10)
            throw new InvalidOperationException($"{scope} 胶片大档必须使用 96×144 与 10/5/10 内边距");
        var scroll = window.FilmstripScroll;
        var layout = window.Filmstrip.CollectionViewLayout as MacFilmstripLayout ?? throw new InvalidOperationException($"{scope} 胶片栏布局类型改变");
        if (layout.Horizontal != row) throw new InvalidOperationException($"{scope} 胶片栏方向未跟随布局");
        if (layout.Tier != FilmCardTier.Large) throw new InvalidOperationException($"{scope} 宽形态胶片栏降为小档");
        var content = panes.FileContent;
        double filterMain = window.FilterLayout.MainLength;
        double similarityColumn = 0;
#if PHOTOVIEWER_DINO
        bool similarityOpen = window.Main.IsThumbnailViewVisible && window.Main.FileVM.FilterBar.IsSimilarityPanelOpen;
        if (similarityOpen)
        {
            var panel = window.Main.FileVM.SimilarityPanel;
            double dock = panel.HasItems && panel.ShowActionArea ? window.Similarity.DockMeasureWidth() : 0;
            similarityColumn = row
                ? SimilarityPanelGeometry.RowWidth(panel.HasItems, panel.SimilarItems.Count, (double)filePane.Frame.Width, dock)
                : 0;
        }
#endif
        var expectedScroll = row
            ? new CGRect(content.X, content.Y + filterMain, Math.Max(1, content.Width - similarityColumn), Math.Max(1, content.Height - filterMain))
            : new CGRect(content.X, 0, content.Width, filePane.Frame.Height);
        RequireFrame(scroll.Frame, expectedScroll, $"{scope} 胶片栏裁剪区");
        double edge = row ? 0 : Math.Max(0, filterMain - FilmCardGeometry.MainLead(FilmCardTier.Large, false));
        if (Math.Abs(layout.EdgePadding - edge) > Exact)
            throw new InvalidOperationException($"{scope} 列布局首卡内容留白 {layout.EdgePadding} 未等于筛选带 {edge}");
        scroll.ContentView.ScrollToPoint(CGPoint.Empty);
        scroll.ReflectScrolledClipView(scroll.ContentView);
        await Task.Delay(150);
        window.Filmstrip.LayoutSubtreeIfNeeded();
        var document = layout.CollectionViewContentSize;
        var clip = scroll.ContentView.Bounds;
        Record(geometry, scope, "filmstripClip", clip);
        Record(geometry, scope, "filmstripDocument", new CGRect(CGPoint.Empty, document));
        Record(geometry, scope, "filmstripScroll", scroll.Frame);
        int count = window.Main.FileVM.ThumbnailList.FilteredFiles.Count;
        double step = FilmCardGeometry.Step(FilmCardTier.Large, row);
        double leading = FilmCardGeometry.MainLead(FilmCardTier.Large, row);
        double trailing = FilmCardGeometry.MainTrail(FilmCardTier.Large, row);
        double contentLength = count == 0 ? 0 : count * step - FilmCardGeometry.Spacing(FilmCardTier.Large) + leading + trailing;
        double documentMain = row ? (double)document.Width : (double)document.Height;
        double viewportMain = row ? (double)clip.Width : (double)clip.Height;
        if (Math.Abs(documentMain - Math.Max(viewportMain, contentLength + edge)) > .5)
            throw new InvalidOperationException($"{scope} 胶片栏文档主轴长度错误：{documentMain}");
        var firstPath = NSIndexPath.FromItemSection(0, 0);
        var secondPath = NSIndexPath.FromItemSection(1, 0);
        var firstCard = layout.GetLayoutAttributesForItem(firstPath)?.Frame ?? throw new InvalidOperationException($"{scope} 首卡几何缺失");
        var secondCard = layout.GetLayoutAttributesForItem(secondPath)?.Frame ?? throw new InvalidOperationException($"{scope} 第二卡几何缺失");
        var item = FilmCardGeometry.ItemFrame(FilmCardTier.Large, row, 0, count, (double)document.Width);
        var expectedFirst = row
            ? new CGRect(item.X + edge, item.Y, item.Width, item.Height)
            : new CGRect(item.X, item.Y + edge, item.Width, item.Height);
        RequireFrame(firstCard, expectedFirst, $"{scope} 首卡位置与卡片尺寸");
        double cardStep = row ? (double)(secondCard.X - firstCard.X) : (double)(secondCard.Y - firstCard.Y);
        if (Math.Abs(cardStep - step) > Exact) throw new InvalidOperationException($"{scope} 胶片栏步长错误：{cardStep} != {step}");
        if (row ? Math.Abs((double)(secondCard.Y - firstCard.Y)) > Exact : Math.Abs((double)(secondCard.X - firstCard.X)) > Exact)
            throw new InvalidOperationException($"{scope} 胶片栏出现换行或换列：{firstCard} / {secondCard}");
        double crossTail = row ? (double)scroll.Frame.Height - ((double)firstCard.Y + (double)firstCard.Height)
            : (double)scroll.Frame.Width - ((double)firstCard.X + (double)firstCard.Width);
        if (Math.Abs(crossTail - FilmCardGeometry.CrossTrail(FilmCardTier.Large, row)) > Exact)
            throw new InvalidOperationException($"{scope} 胶片栏交叉轴内边距不是 {FilmCardGeometry.CrossTrail(FilmCardTier.Large, row)} 点：{crossTail}");
        if (count > 0 && contentLength + edge > (double)viewportMain + .5)
        {
            var lastPath = NSIndexPath.FromItemSection(count - 1, 0);
            var lastCard = layout.GetLayoutAttributesForItem(lastPath)?.Frame ?? throw new InvalidOperationException($"{scope} 末卡几何缺失");
            double tail = documentMain - (row ? (double)lastCard.X + (double)lastCard.Width : (double)lastCard.Y + (double)lastCard.Height);
            if (Math.Abs(tail - trailing) > .5) throw new InvalidOperationException($"{scope} 胶片栏末端内边距错误：{tail} != {trailing}");
        }
        if (count < 2) return;
        foreach (int index in new[] { 0, 1 })
        {
            var path = NSIndexPath.FromItemSection(index, 0);
            var expected = layout.GetLayoutAttributesForItem(path)?.Frame ?? throw new InvalidOperationException($"{scope} 第 {index} 卡几何缺失");
            if (window.Filmstrip.GetItem(path) is not MacThumbnailItem card) throw new InvalidOperationException($"{scope} 第 {index} 卡视图缺失");
            if (!ReferenceEquals(card.BoundFile, window.Main.FileVM.ThumbnailList.FilteredFiles[index]))
                throw new InvalidOperationException($"{scope} 第 {index} 卡绑定身份与业务顺序不同");
            RequireFrame(card.View.Frame, expected, $"{scope} 第 {index} 卡视图未落在布局位置", .5);
            if (card.View.IsHiddenOrHasHiddenAncestor)
                throw new InvalidOperationException($"{scope} 第 {index} 卡视图被隐藏或其祖先被隐藏");
            VerifyCard(card, index, scope, geometry);
        }
    }

    /// <summary>核对单张卡片根视图、占位区、80 方形图片、文字与星级几何，并写入几何报告。</summary>
    private static void VerifyCard(MacThumbnailItem card, int index, string scope, List<string> geometry)
    {
        var surface = card.View.Subviews.SingleOrDefault(view => view.Subviews.OfType<NSTextField>().Any())
            ?? throw new InvalidOperationException($"{scope} 第 {index} 卡找不到承载文字/星级的占位视图");
        Record(geometry, scope, $"card{index}.root", card.View.Frame);
        Record(geometry, scope, $"card{index}.surface", surface.Frame);
        RequireFrame(surface.Frame, new CGRect(3, 3, 90, 138), $"{scope} 第 {index} 卡占位区（96×144 内部）");
        var image = surface.Subviews.OfType<NSImageView>().Single(view => Math.Abs((double)view.Frame.Width - 80) < Exact);
        Record(geometry, scope, $"card{index}.image", image.Frame);
        RequireFrame(image.Frame, new CGRect(5, 5, 80, 80), $"{scope} 第 {index} 卡图片非 80 正方形");
        var texts = surface.Subviews.OfType<NSTextField>().ToArray();
        var name = texts.Single(field => Math.Abs((double)field.Frame.Y - 89) < Exact);
        var date = texts.Single(field => Math.Abs((double)field.Frame.Y - 103) < Exact);
        var shake = texts.Single(field => Math.Abs((double)field.Frame.Y - 67) < Exact);
        Record(geometry, scope, $"card{index}.name", name.Frame);
        Record(geometry, scope, $"card{index}.date", date.Frame);
        Record(geometry, scope, $"card{index}.shake", shake.Frame);
        RequireFrame(name.Frame, new CGRect(2, 89, 86, 12), $"{scope} 第 {index} 卡文件名");
        RequireFrame(date.Frame, new CGRect(5, 103, 80, 11), $"{scope} 第 {index} 卡日期");
        RequireFrame(shake.Frame, new CGRect(67, 67, 16, 16), $"{scope} 第 {index} 卡抖动徽标");
        var stars = surface.Subviews.OfType<NSButton>().ToArray();
        if (stars.Length != 6) throw new InvalidOperationException($"{scope} 第 {index} 卡星级数量为 {stars.Length}，预期 6");
        for (int rating = 0; rating < stars.Length; rating++)
        {
            Record(geometry, scope, $"card{index}.star{rating}", stars[rating].Frame);
            RequireFrame(stars[rating].Frame, new CGRect(3 + rating * 14, 116, 14, 14), $"{scope} 第 {index} 卡星级 {rating}");
            if (stars[rating].Hidden) throw new InvalidOperationException($"{scope} 第 {index} 卡星级未随 ShowRating 展示");
        }
    }

    /// <summary>核对导航图长边 150 与照片比例、物理 100% 升级 Full 后的视口包含关系，并用真实 Fit 按钮恢复。</summary>
    private static async Task VerifyNavigationAsync(MacMainWindow window, List<string> observations)
    {
        var photo = window.Photo;
        var image = window.Session.Image ?? throw new InvalidOperationException("导航验收缺少主图");
        var (imageWidth, imageHeight) = image.Orientation >= 5 ? (image.SourceHeight, image.SourceWidth) : (image.SourceWidth, image.SourceHeight);
        if (imageWidth <= 0 || imageHeight <= 0) throw new InvalidOperationException("主图缺少源尺寸，无法核对导航比例");
        photo.FitToView();
        await Task.Delay(150);
        var navigation = photo.NavigationSize;
        if (Math.Abs(Math.Max((double)navigation.Width, (double)navigation.Height) - 150) > .5)
            throw new InvalidOperationException($"导航图长边不是 150 逻辑点：{navigation}");
        if (Math.Abs((double)navigation.Width / (double)navigation.Height - (double)imageWidth / imageHeight) > .01)
            throw new InvalidOperationException($"导航图比例与显示朝向照片不符：{navigation} 对 {imageWidth}x{imageHeight}");
        var fitViewport = photo.NavigationViewport;
        RequireFrame(fitViewport, new CGRect(0, 0, (double)navigation.Width, (double)navigation.Height), "适应状态下导航框未覆盖整幅照片", .5);
        photo.ZoomToPhysical(1);
        await WaitAsync(() => window.Session.Image != null && !window.Session.IsProxy, "物理 100% 升级 Full");
        if (Math.Abs(photo.PhysicalScale - 1) > .001) throw new InvalidOperationException($"物理 100% 倍率错误：{photo.PhysicalScale}");
        var upgraded = photo.NavigationSize;
        if (Math.Abs((double)upgraded.Width - (double)navigation.Width) > .5 || Math.Abs((double)upgraded.Height - (double)navigation.Height) > .5)
            throw new InvalidOperationException($"代理升级 Full 后导航几何跳变：{navigation} → {upgraded}");
        var viewport = photo.NavigationViewport;
        if ((double)viewport.X < -Exact || (double)viewport.Y < -Exact || (double)viewport.Width <= 0 || (double)viewport.Height <= 0 ||
            (double)viewport.X + (double)viewport.Width > (double)navigation.Width + Exact ||
            (double)viewport.Y + (double)viewport.Height > (double)navigation.Height + Exact)
            throw new InvalidOperationException($"放大后导航框超出 NavigationSize：{viewport}/{navigation}");

        // IMAGE-06：白框拖动定位走共享 CenterOnNormalizedPoint，拖动后视口中心落在目标归一化点附近。
        // 小素材在物理 100% 下可能整幅装进视口、无处可平移，先放到 4 倍保证两轴都能移动。
        photo.ZoomToPhysical(4);
        await Task.Delay(120);
        photo.CenterOnNormalizedPoint(.62, .58);
        await Task.Delay(120);
        var center = photo.ViewCenter;
        if (Math.Abs(center.U - .62) > .03 || Math.Abs(center.V - .58) > .03)
            throw new InvalidOperationException($"导航图拖动定位未生效：{center}");
        double backing = (double)window.Window.BackingScaleFactor;
        using var capture = photo.CaptureLinearFrame(navigation: true);
        if (Math.Abs(Math.Max((double)capture.Width, (double)capture.Height) - 150 * backing) > 1.5)
            throw new InvalidOperationException($"导航图实际帧长边错误：{capture.Width}x{capture.Height} @{backing}x");
        if (Math.Abs((double)capture.Width / capture.Height - (double)navigation.Width / (double)navigation.Height) > .01)
            throw new InvalidOperationException($"导航图实际帧比例错误：{capture.Width}x{capture.Height} 对 {navigation}");
        var fit = window.ControlBar.Commands["Fit"];
        fit.PerformClick(fit);
        await Task.Delay(200);
        if (!photo.IsFit) throw new InvalidOperationException("真实 Fit 按钮未恢复适应状态");
        observations.Add($"navigation: 长边 {Math.Max((double)navigation.Width, (double)navigation.Height):0.##}、比例 {(double)navigation.Width:0.#}x{(double)navigation.Height:0.#}；100% 视口 {viewport} 包含于 NavigationSize；nav 帧 {capture.Width}x{capture.Height}@{backing}x；Fit 按钮已恢复");
    }

    /// <summary>隐藏胶片栏/控制栏分别核对适应区对应轴释放空间，恢复后分区回到原值。</summary>
    private static async Task VerifyPaneReleaseAsync(MacMainWindow window, LayoutMode mode, List<string> observations)
    {
        bool row = mode == LayoutMode.Row;
        var root = window.Window.ContentView ?? throw new InvalidOperationException("原生内容视图缺失");
        var filmPane = root.Subviews.Single(view => view.Subviews.Contains(window.FilmstripScroll));
        await Task.Delay(200);
        var beforeFilm = window.Photo.FitArea;
        double filmBand = row ? (double)beforeFilm.Y : (double)beforeFilm.X;
        if (filmBand <= 0) throw new InvalidOperationException($"{mode} 主图适应区旁没有可释放的胶片栏分区：{beforeFilm}");
        // 放大并离开画面中心，证明栏位开关只改变适应区，切图倍率与视图中心不跟着重排跑掉。
        window.Photo.ZoomBy(2);
        window.Photo.CenterOnNormalizedPoint(.6, .5);
        await Task.Delay(150);
        double heldScale = window.Photo.PhysicalScale;
        var heldCenter = window.Photo.ViewCenter;
        window.Main.IsThumbnailViewVisible = false;
        await Task.Delay(300);
        if (!filmPane.Hidden || ((double)filmPane.Frame.Width > .5 && (double)filmPane.Frame.Height > .5))
            throw new InvalidOperationException($"{mode} 隐藏胶片栏未收起文件栏：{filmPane.Frame}");
        RequireSize(window.Photo.FitArea, beforeFilm, filmBand, row, $"{mode} 隐藏胶片栏未释放主图{(row ? "上方" : "左侧")}空间");
        RequireZoomHeld(window, heldScale, heldCenter, $"{mode} 隐藏胶片栏");
        // SHELL-02：文件栏隐藏后无论哪种布局都是整幅宽通栏带，并且带毛玻璃背景。
        var zones = window.DragZones;
        window.ShowTitlebarForTesting(true);
        try
        {
            if (Math.Abs((double)zones[0].Frame.Width - (double)root.Bounds.Width) > Exact ||
                Math.Abs((double)zones[1].Frame.Width) > Exact)
                throw new InvalidOperationException($"{mode} 文件栏隐藏后拖动区未通栏：{zones[0].Frame}/{zones[1].Frame}");
            if (window.TitlebarMaterialView.Hidden)
                throw new InvalidOperationException($"{mode} 文件栏隐藏后标题栏未显示毛玻璃背景");
        }
        finally { window.ShowTitlebarForTesting(false); }
        observations.Add($"{mode}: 隐藏胶片栏释放{(row ? "上" : "左")}侧 {filmBand:0.##} 点，适应区 {window.Photo.FitArea}；倍率 {heldScale:0.###} 与中心保持；标题栏转为通栏带 + 毛玻璃");
        window.Main.IsThumbnailViewVisible = true;
        await Task.Delay(300);
        RequireFrame(window.Photo.FitArea, beforeFilm, $"{mode} 恢复胶片栏后适应区未还原", .5);
        RequireZoomHeld(window, heldScale, heldCenter, $"{mode} 恢复胶片栏");

        var beforeControl = window.Photo.FitArea;
        var viewport = root.Bounds;
        double controlBand = row ? (double)viewport.Height - (double)beforeControl.Y - (double)beforeControl.Height
            : (double)viewport.Width - (double)beforeControl.X - (double)beforeControl.Width;
        if (controlBand <= 0) throw new InvalidOperationException($"{mode} 主图旁没有可释放的控制栏分区：{beforeControl}");
        window.Main.IsControlViewVisible = false;
        await Task.Delay(300);
        if (!window.ControlScroll.Hidden) throw new InvalidOperationException($"{mode} 隐藏控制栏未收起控制分区");
        RequireSize(window.Photo.FitArea, beforeControl, controlBand, row, $"{mode} 隐藏控制栏未释放主图{(row ? "下方" : "右侧")}空间", false);
        RequireZoomHeld(window, heldScale, heldCenter, $"{mode} 隐藏控制栏");
        observations.Add($"{mode}: 隐藏控制栏释放{(row ? "下" : "右")}侧 {controlBand:0.##} 点，适应区 {window.Photo.FitArea}");
        window.Main.IsControlViewVisible = true;
        await Task.Delay(300);
        RequireFrame(window.Photo.FitArea, beforeControl, $"{mode} 恢复控制栏后适应区未还原", .5);
        RequireZoomHeld(window, heldScale, heldCenter, $"{mode} 恢复控制栏");
        window.Photo.FitToView();
        await Task.Delay(120);
    }

    /// <summary>核对自动布局随窗口宽高方向切换上下/左右分栏，再回到横向。</summary>
    private static async Task VerifyAutoOrientationAsync(MacMainWindow window, List<string> geometry, List<string> observations)
    {
        window.Main.Settings.LayoutMode = LayoutMode.Auto;
        await SettleAsync(window, new CGSize(1200, 850));
        if (window.Main.IsRowLayout || (double)window.Photo.FitArea.X <= 0 || (double)window.Photo.FitArea.Y > Exact)
            throw new InvalidOperationException($"自动布局在横向窗口未选择左右分栏：{window.Photo.FitArea}");
        Record(geometry, "Auto-1200x850", "photoFit", window.Photo.FitArea);
        await SettleAsync(window, new CGSize(700, 850));
        if (!window.Main.IsRowLayout || (double)window.Photo.FitArea.Y <= 0 || (double)window.Photo.FitArea.X > Exact)
            throw new InvalidOperationException($"自动布局在纵向窗口未选择上下分栏：{window.Photo.FitArea}");
        Record(geometry, "Auto-700x850", "photoFit", window.Photo.FitArea);
        await SettleAsync(window, new CGSize(850, 700));
        if (window.Main.IsRowLayout || (double)window.Photo.FitArea.X <= 0)
            throw new InvalidOperationException($"自动布局回到横向窗口未切回左右分栏：{window.Photo.FitArea}");
        Record(geometry, "Auto-850x700", "photoFit", window.Photo.FitArea);
        observations.Add("自动布局：横向窗口=左右分栏，纵向窗口=上下分栏，回到横向=左右分栏");
    }

    /// <summary>宽形态静态矩阵：全开/全关/独立栏位/分析/评分与 EXIF 开关、AI 相似面板开合都有实际 frame 断言。</summary>
    private static async Task VerifyStaticMatrixAsync(MacMainWindow window, List<string> geometry, List<string> observations)
    {
        var settings = window.Main.Settings;
        var filter = window.Main.FileVM.FilterBar;
        var exif = settings.ExifDisplayItems;
        foreach (var mode in new[] { LayoutMode.Row, LayoutMode.Col })
        {
            settings.LayoutMode = mode;
            foreach (var size in new[] { new CGSize(1200, 850), new CGSize(560, 480) })
            {
                await SettleAsync(window, size);
                foreach (var config in StaticConfigs())
                {
                    window.Main.IsThumbnailViewVisible = config.Film;
                    window.Main.IsControlViewVisible = config.Control;
                    window.Main.IsAnalysisViewVisible = config.Analysis;
                    settings.ShowRating = config.Rating;
                    foreach (var entry in exif) entry.IsEnabled = config.Exif;
#if PHOTOVIEWER_DINO
                    filter.IsSimilarityPanelOpen = config.Similarity;
#else
                    if (config.Similarity) continue;
#endif
                    await Task.Delay(260);
                    string scope = $"matrix-{mode}-{size.Width:0}x{size.Height:0}-{config.Name}";
                    VerifyStaticFrame(window, scope, geometry, observations);
                }
            }
        }
        await Task.Delay(200);
    }

    /// <summary>静态矩阵配置：栏位、分析、评分、EXIF 与相似面板开合。</summary>
    private static IEnumerable<(string Name, bool Film, bool Control, bool Analysis, bool Rating, bool Exif, bool Similarity)> StaticConfigs()
    {
        yield return ("all-on", true, true, true, true, true, false);
        yield return ("all-off", false, false, false, false, false, false);
        yield return ("film-only", true, false, false, true, true, false);
        yield return ("control-only", false, true, false, true, true, false);
        yield return ("analysis-only", false, false, true, true, true, false);
        yield return ("rating-off", true, true, true, false, true, false);
        yield return ("exif-off", true, true, true, true, false, false);
        yield return ("control-rating-only", false, true, false, true, false, false);
        yield return ("similarity-on", true, true, false, true, true, true);
    }

    /// <summary>单帧矩阵断言：分区、适应区、筛选外框、胶片首卡、控制分组与工具区材质。</summary>
    private static void VerifyStaticFrame(MacMainWindow window, string scope, List<string> geometry, List<string> observations)
    {
        var root = window.Window.ContentView ?? throw new InvalidOperationException("原生内容视图缺失");
        var filePane = root.Subviews.Single(view => view.Subviews.Contains(window.FilmstripScroll));
        double width = (double)root.Bounds.Width, height = (double)root.Bounds.Height;
        var panes = ExpectedPanes(window, window.Main.IsRowLayout, width, height);
        if (!window.Main.IsThumbnailViewVisible)
        {
            if (!filePane.Hidden) throw new InvalidOperationException($"{scope} 隐藏文件栏后仍可见");
        }
        else
        {
            RequireFrame(filePane.Frame, ToRect(panes.FileBar), $"{scope} 文件栏");
            RequireFrame(window.FilmstripScroll.Frame, new CGRect(panes.FileContent.X,
                    window.Main.IsRowLayout ? panes.FileContent.Y + window.FilterLayout.MainLength : 0,
                    window.Main.IsRowLayout ? Math.Max(1, panes.FileContent.Width - SimilarityColumn(window)) : panes.FileContent.Width,
                    window.Main.IsRowLayout ? Math.Max(1, panes.FileContent.Height - window.FilterLayout.MainLength) : height),
                $"{scope} 胶片栏裁剪区");
            var layout = (MacFilmstripLayout)window.Filmstrip.CollectionViewLayout!;
            var first = layout.GetLayoutAttributesForItem(NSIndexPath.FromItemSection(0, 0))?.Frame
                ?? throw new InvalidOperationException($"{scope} 首卡几何缺失");
            double edge = window.Main.IsRowLayout ? 0 : Math.Max(0, window.FilterLayout.MainLength - FilmCardGeometry.MainLead(FilmCardTier.Large, false));
            double docWidth = (double)layout.CollectionViewContentSize.Width;
            var item = FilmCardGeometry.ItemFrame(FilmCardTier.Large, window.Main.IsRowLayout, 0,
                window.Main.FileVM.ThumbnailList.FilteredFiles.Count, docWidth);
            var expected = window.Main.IsRowLayout
                ? new CGRect(item.X + edge, item.Y, item.Width, item.Height)
                : new CGRect(item.X, item.Y + edge, item.Width, item.Height);
            RequireFrame(first, expected, $"{scope} 胶片首卡位置");
            var frames = window.FilterLayout;
            if (window.FilterCapsuleMode)
            {
                var visible = filePane.Subviews.OfType<NSGlassEffectView>().Select(glass => glass.ContentView).OfType<NSPopUpButton>().Where(popup => !popup.Hidden).ToArray();
                RequireFrame(window.FilterCapsuleGlass.Frame, new CGRect(panes.FileContent.X + frames.Control.X,
                    panes.FileContent.Y + frames.Control.Y, frames.Control.Width, frames.Control.Height), $"{scope} 单胶囊");
            }
            else
            {
                var segmented = filePane.Subviews.OfType<NSGlassEffectView>().Select(glass => glass.ContentView).OfType<NSSegmentedControl>().SingleOrDefault()
                    ?? throw new InvalidOperationException($"{scope} 文件栏可见时原生筛选连续控件被隐藏");
                RequireFrame(window.FilterSegmentedGlass.Frame, new CGRect(panes.FileContent.X + frames.Control.X,
                    panes.FileContent.Y + frames.Control.Y, frames.Control.Width, frames.Control.Height), $"{scope} 筛选连续控件");
                var expectedFrames = new[] { frames.Sort, frames.Order, frames.Rating };
                for (int index = 0; index < 3; index++)
                    if (Math.Abs((double)segmented.GetWidth(index) - expectedFrames[index].Width) > Exact)
                        throw new InvalidOperationException($"{scope} 筛选段 {index} 宽度错误");
            }
        }
        if (window.Main.IsControlViewVisible)
        {
            RequireFrame(window.ControlScroll.Frame, ToRect(panes.ControlContent), $"{scope} 控制栏内容");
            VerifyControlBar(window, scope, geometry, panes.ControlContent, window.Main.IsRowLayout);
        }
        else if (!window.ControlScroll.Hidden) throw new InvalidOperationException($"{scope} 隐藏控制栏后仍可见");
        if (window.Main.IsAnalysisViewVisible)
            RequireFrame(window.Analysis.Frame, ToRect(panes.Analysis), $"{scope} 分析栏");
        else if (!window.Analysis.Hidden) throw new InvalidOperationException($"{scope} 隐藏分析栏后仍可见");
        var insets = window.Photo.FitInsets;
        RequireFrame(new CGRect((double)insets.Left, (double)insets.Top,
                width - (double)insets.Left - (double)insets.Right, height - (double)insets.Top - (double)insets.Bottom),
            ToRect(panes.Photo), $"{scope} 主图适应区");
        VerifyMaterialCondition(window, window.Main.IsRowLayout, scope, panes, observations);
        observations.Add($"{scope}: 文件栏={panes.FileBar.Width:0}×{panes.FileBar.Height:0}, 控制内容={panes.ControlContent.Width:0}×{panes.ControlContent.Height:0}, 分析={panes.Analysis.Width:0}×{panes.Analysis.Height:0}, 适应区={panes.Photo.Width:0}×{panes.Photo.Height:0}");
    }

    /// <summary>当前相似面板占用的横向/纵向附加长度。</summary>
    private static double SimilarityColumn(MacMainWindow window)
    {
#if PHOTOVIEWER_DINO
        var filter = window.Main.FileVM.FilterBar;
        if (!window.Main.IsThumbnailViewVisible || !filter.IsSimilarityPanelOpen) return 0;
        var panel = window.Main.FileVM.SimilarityPanel;
        var row = window.Main.IsRowLayout;
        double dock = panel.HasItems && panel.ShowActionArea ? window.Similarity.DockMeasureWidth() : 0;
        return row
            ? SimilarityPanelGeometry.RowWidth(panel.HasItems, panel.SimilarItems.Count, (double)window.Window.ContentView!.Bounds.Width, dock)
            : 0;
#else
        return 0;
#endif
    }

    /// <summary>用已有工具栏的真实按钮核对上一张/下一张与预设缩放语义，不新建重复按钮。</summary>
    private static async Task VerifyCommandButtonsAsync(MacMainWindow window, List<string> observations)
    {
        var files = window.Main.FileVM.ThumbnailList.FilteredFiles;
        window.Main.CurrentFile = files[0];
        await Task.Delay(300);
        var next = window.ControlBar.Commands["Next"];
        next.PerformClick(next);
        await Task.Delay(300);
        if (!ReferenceEquals(window.Main.CurrentFile, files[1])) throw new InvalidOperationException("真实下一张按钮未切换照片");
        var previous = window.ControlBar.Commands["Previous"];
        previous.PerformClick(previous);
        await Task.Delay(300);
        if (!ReferenceEquals(window.Main.CurrentFile, files[0])) throw new InvalidOperationException("真实上一张按钮未回到原照片");
        var exchange = window.ControlBar.Commands["Exchange"];
        exchange.PerformClick(exchange);
        await Task.Delay(300);
        if (!ReferenceEquals(window.Main.CurrentFile, files[1])) throw new InvalidOperationException("真实交换按钮未切回上一张");
        previous = window.ControlBar.Commands["Previous"];
        previous.PerformClick(previous);
        await Task.Delay(300);

        var presets = window.Main.Settings.ScalePresets.Select(preset => preset.Value).ToArray();
        window.Photo.FitToView();
        await Task.Delay(150);
        double fit = window.Photo.PhysicalScale;
        var zoomIn = window.ControlBar.Commands["ZoomInPreset"];
        zoomIn.PerformClick(zoomIn);
        await Task.Delay(200);
        double zoomed = window.Photo.PhysicalScale;
        if (zoomed <= fit || !presets.Any(value => Math.Abs(value - zoomed) < .001))
            throw new InvalidOperationException($"真实放大（预设）按钮未跳到更高预设：{fit} → {zoomed}");
        var zoomOut = window.ControlBar.Commands["ZoomOutPreset"];
        zoomOut.PerformClick(zoomOut);
        await Task.Delay(200);
        double reduced = window.Photo.PhysicalScale;
        if (reduced >= zoomed || !presets.Any(value => Math.Abs(value - reduced) < .001))
            throw new InvalidOperationException($"真实缩小（预设）按钮未跳回更低预设：{zoomed} → {reduced}");
        var fitCommand = window.ControlBar.Commands["Fit"];
        fitCommand.PerformClick(fitCommand);
        await Task.Delay(200);
        if (!window.Photo.IsFit) throw new InvalidOperationException("真实 Fit 命令未恢复适应状态");
        observations.Add($"真实工具栏按钮：Next/Previous/Exchange 切换正确；ZoomInPreset {fit:0.###}→{zoomed:0.###}、ZoomOutPreset→{reduced:0.###}、Fit 恢复适应");
    }

    /// <summary>设置窗口内容尺寸并等待根视图到位与一轮原生重排，不用固定等待掩盖失败。</summary>
    private static async Task SettleAsync(MacMainWindow window, CGSize size)
    {
        window.Window.SetContentSize(size);
        var root = window.Window.ContentView ?? throw new InvalidOperationException("原生内容视图缺失");
        for (int attempt = 0; attempt < 80; attempt++)
        {
            if (Math.Abs((double)root.Bounds.Width - size.Width) < .5 && Math.Abs((double)root.Bounds.Height - size.Height) < .5) break;
            await Task.Delay(25);
        }
        if (Math.Abs((double)root.Bounds.Width - size.Width) > .5 || Math.Abs((double)root.Bounds.Height - size.Height) > .5)
            throw new InvalidOperationException($"窗口内容尺寸未生效：{root.Bounds.Size} != {size}");
        await Task.Delay(220);
        root.LayoutSubtreeIfNeeded();
        window.Filmstrip.LayoutSubtreeIfNeeded();
    }

    /// <summary>栏位重排只改变适应区：当前物理倍率与视图中心必须留在原处。</summary>
    private static void RequireZoomHeld(MacMainWindow window, double scale, (double U, double V) center, string context)
    {
        var photo = window.Photo;
        if (Math.Abs(photo.PhysicalScale - scale) > .001)
            throw new InvalidOperationException($"{context} 改变了照片倍率：{scale:0.####} → {photo.PhysicalScale:0.####}");
        var now = photo.ViewCenter;
        if (Math.Abs(now.U - center.U) > .01 || Math.Abs(now.V - center.V) > .01)
            throw new InvalidOperationException($"{context} 移动了照片视图中心：({center.U:0.###},{center.V:0.###}) → ({now.U:0.###},{now.V:0.###})");
    }

    /// <summary>隐藏栏位后核对主轴变长；隐藏起始栏时归零原点，隐藏末端栏时保持原点。</summary>
    private static void RequireSize(CGRect actual, CGRect before, double released, bool row, string context, bool leading = true)
    {
        double expectedMain = (row ? (double)before.Height : (double)before.Width) + released;
        double actualMain = row ? (double)actual.Height : (double)actual.Width;
        double origin = row ? (double)actual.Y : (double)actual.X;
        double expectedOrigin = leading ? 0 : row ? (double)before.Y : (double)before.X;
        if (Math.Abs(actualMain - expectedMain) > .5 || Math.Abs(origin - expectedOrigin) > .5)
            throw new InvalidOperationException($"{context}：{before} → {actual}");
    }

    /// <summary>轮询真实状态并设有限超时，失败不输出虚假通过。</summary>
    private static async Task WaitAsync(Func<bool> condition, string name, double seconds = 30)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(name);
            await Task.Delay(20);
        }
    }

    /// <summary>按允许误差比较原生 frame，失效时报告实际与预期。</summary>
    private static void RequireFrame(CGRect actual, CGRect expected, string context, double tolerance = Exact)
    {
        if (Math.Abs((double)(actual.X - expected.X)) > tolerance || Math.Abs((double)(actual.Y - expected.Y)) > tolerance ||
            Math.Abs((double)(actual.Width - expected.Width)) > tolerance || Math.Abs((double)(actual.Height - expected.Height)) > tolerance)
            throw new InvalidOperationException($"{context}：实际 {actual}，预期 {expected}");
    }

    /// <summary>把实际几何写入报告，保留小数便于复核分数点。</summary>
    private static void Record(List<string> geometry, string scope, string name, CGRect frame)
        => geometry.Add($"{scope}\t{name}\t({(double)frame.X:0.##},{(double)frame.Y:0.##},{(double)frame.Width:0.##},{(double)frame.Height:0.##})");
}
