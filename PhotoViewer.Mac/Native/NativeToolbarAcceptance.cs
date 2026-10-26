using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Apple;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Settings;
using SkiaSharp;
using System.ComponentModel;
using SortOrder = PhotoViewer.ViewModels.Main.SortOrder;

namespace PhotoViewer.Mac.Native;

/// <summary>在真实窗口中验证分组、尺寸、排序、快捷键提示、按钮事件和自有素材的评分写回。</summary>
internal static class NativeToolbarAcceptance
{
    /// <summary>使用隔离设置与自有测试照片运行控制栏回归，结束后恢复设置工作副本。</summary>
    internal static async Task RunAsync(MacMainWindow window, string output)
    {
        var settings = window.Main.Settings;
        var snapshot = settings.CreateSnapshot();
        var first = window.Main.CurrentFile ?? throw new InvalidOperationException("工具栏验收缺少首图");
        var observations = new List<string>();
        try
        {
            settings.ShowRating = true;
            window.Main.IsControlViewVisible = true;
            foreach (var hotkey in settings.Hotkeys)
                hotkey.IsDisplay = hotkey.Command is "Previous" or "Next" or "Exchange" or "Fit" or "ZoomInPreset" or "ZoomOutPreset";
            foreach (var entry in settings.ExifDisplayItems)
                entry.IsEnabled = entry.PropertyName is "Aperture" or "ExposureTime" or "Iso" or "EquivFocalLength";
            await Task.Delay(250);
            VerifyReferenceLayouts(window, observations);
            VerifyRatingGroupColors(window);
            observations.Add("rating group: lit stars use color.star.on, hollow stars color.star.off, clear cross color.star.clear (no system secondary colour)");
            foreach (var mode in new[] { LayoutMode.Col, LayoutMode.Row })
            {
                settings.LayoutMode = mode;
                foreach (var size in new[] { new CGSize(830, 650), new CGSize(700, 480), new CGSize(1200, 850) })
                {
                    window.Window.SetContentSize(size);
                    await Task.Delay(150);
                    VerifyGeometry(window, observations);
                    NativeSnapshots.SaveMain(window, System.IO.Path.Combine(output, $"toolbar-{mode}-{size.Width:0}.png"));
                }
            }
            VerifyButtonSurface(window.ControlBar.Commands["Next"], output);
            var next = settings.Hotkeys.Single(hotkey => hotkey.Command == "Next");
            next.IsDisplay = false;
            await Task.Delay(150);
            if (window.ControlBar.Commands.ContainsKey("Next")) throw new InvalidOperationException("控制命令显隐未刷新");
            next.IsDisplay = true;
            settings.Hotkeys.Move(settings.Hotkeys.IndexOf(next), 0);
            next.PrimaryHotkey = AppGesture.FromKey(new AppKeyGesture(GestureKey.Right, GestureModifiers.Control));
            next.SecondaryHotkey = AppGesture.FromMouse(new MouseGestureEx(MouseAction.WheelDown));
            settings.UseAppleKeyboard = true;
            settings.MapCommandTarget = AppleMappingTarget.Ctrl;
            await Task.Delay(150);
            var expected = settings.Hotkeys.Where(hotkey => hotkey.IsDisplay && AppleSymbolNames.ForCommand(hotkey.Command) != null).Select(hotkey => hotkey.Command);
            if (!window.ControlBar.Commands.Keys.SequenceEqual(expected)) throw new InvalidOperationException("工具栏未保留设置排序");
            string tooltip = window.ControlBar.Commands["Next"].ToolTip ?? "";
            string spoken = window.ControlBar.Commands["Next"].AccessibilityLabel ?? "";
            if (!tooltip.Contains(MacInput.FormatGesture(next.PrimaryHotkey, settings), StringComparison.Ordinal) ||
                !tooltip.Contains(MacInput.FormatGesture(next.SecondaryHotkey, settings), StringComparison.Ordinal) || tooltip.Contains("未设置", StringComparison.Ordinal))
                throw new InvalidOperationException("工具栏提示未正确显示苹果主次快捷键");
            if (spoken != tooltip)
                throw new InvalidOperationException($"命令读屏名称未包含当前主次绑定：{spoken}");
            observations.Add("display/order bindings and both Apple gesture tooltips update without changing photos; accessibility label matches tooltip");

            window.Main.CurrentFile = window.Main.FileVM.ThumbnailList.FilteredFiles[0];
            await Task.Delay(100);
            var before = window.Main.CurrentFile;
            var nextButton = window.ControlBar.Commands["Next"];
            nextButton.PerformClick(nextButton);
            await Task.Delay(150);
            if (window.Main.CurrentFile == before) throw new InvalidOperationException("真实下一张按钮未切换照片");
            var previousButton = window.ControlBar.Commands["Previous"];
            previousButton.PerformClick(previousButton);
            await Task.Delay(150);
            if (window.Main.CurrentFile != before) throw new InvalidOperationException("真实上一张按钮未恢复照片");
            var keyboardButton = window.ControlBar.Commands["Next"];
            window.Window.MakeKeyAndOrderFront(null);
            if (!window.Window.MakeFirstResponder(keyboardButton)) throw new InvalidOperationException("工具按钮不能取得键盘焦点");
            foreach (var type in new[] { NSEventType.KeyDown, NSEventType.KeyUp })
            {
                var key = NSEvent.KeyEvent(type, CGPoint.Empty, 0, NSProcessInfo.ProcessInfo.SystemUptime, window.Window.WindowNumber, null!, " ", " ", false, 49)!;
                NSApplication.SharedApplication.PostEvent(key, false);
            }
            await Task.Delay(250);
            if (window.Main.CurrentFile == before) throw new InvalidOperationException("工具按钮键盘空格未触发原生动作");
            window.ControlBar.Commands["Previous"].PerformClick(window.ControlBar.Commands["Previous"]);
            await Task.Delay(150);
            if (window.Main.CurrentFile != before) throw new InvalidOperationException("键盘按钮动作改变了导航语义");
            observations.Add("native NSButton keyboard focus and Space dispatch the same command; hover / press have distinct complete-square surfaces");

            settings.ShowRating = false;
            await Task.Delay(150);
            if (window.ControlBar.Ratings.Count != 0 || window.ControlBar.Groups.Count != 2) throw new InvalidOperationException("隐藏评分仍保留空分组");
            var enabledExif = settings.ExifDisplayItems.Where(entry => entry.IsEnabled).ToArray();
            var enabledCommands = settings.Hotkeys.Where(hotkey => hotkey.IsDisplay).ToArray();
            foreach (var entry in enabledExif) entry.IsEnabled = false;
            foreach (var hotkey in enabledCommands) hotkey.IsDisplay = false;
            await Task.Delay(150);
            if (window.ControlBar.Groups.Count != 0) throw new InvalidOperationException("空工具栏仍保留占位分组");
            foreach (bool horizontal in new[] { true, false })
            {
                var empty = window.ControlBar.Measure(horizontal, 400);
                if (empty.Width != 20 || empty.Height != 20) throw new InvalidOperationException("空工具栏除外边距之外仍保留组间占位");
            }
            foreach (var entry in enabledExif) entry.IsEnabled = true;
            foreach (var hotkey in enabledCommands) hotkey.IsDisplay = true;
            settings.ShowRating = true;
            var ratingCommand = settings.Hotkeys.Single(hotkey => hotkey.Command == "SetRating3");
            ratingCommand.IsDisplay = true;
            var clearRatingCommand = settings.Hotkeys.Single(hotkey => hotkey.Command == "SetRating0");
            clearRatingCommand.IsDisplay = true;
            await Task.Delay(150);
            VerifyRatingCommand(window.ControlBar.Commands["SetRating3"], "3");
            VerifyClearRatingCommand(window.ControlBar.Commands["SetRating0"]);
            NativeSnapshots.SaveView(window.ControlBar, System.IO.Path.Combine(output, "toolbar-rating-commands.png"));
            observations.Add("rating shortcut commands draw a star.fill / clear cross with the 9pt digit stacked in the centre (TOOLBAR-04)");
            settings.Hotkeys.Single(hotkey => hotkey.Command == "Open").IsDisplay = true;
            window.Main.CurrentFile = null;
            var mainMenu = NSApplication.SharedApplication.MainMenu ?? throw new InvalidOperationException("缺少系统菜单栏");
            var actionItems = mainMenu.Items.Select(item => item.Submenu).Single(submenu => submenu?.Title == "操作")!.Items;
            var enhanceItem = mainMenu.Items.Select(item => item.Submenu).Single(submenu => submenu?.Title == "显示")!.Items.Single(item => item.Title == "雾天增强");
            if (enhanceItem.Enabled || actionItems.Any(item => item.Title != "打开文件" && item.Enabled) || !actionItems.Single(item => item.Title == "打开文件").Enabled)
                throw new InvalidOperationException("照片切空后系统菜单未在同一轮立即禁用");
            await Task.Delay(150);
            if (window.ControlBar.Ratings.Any(button => button.Enabled) || !window.ControlBar.Commands["Open"].Enabled ||
                window.ControlBar.Commands.Where(pair => pair.Key != "Open").Any(pair => pair.Value.Enabled))
                throw new InvalidOperationException("空照片状态的工具栏启用状态错误");
            observations.Add("rating group hides without blank space; numbered rating commands remain distinguishable; empty state disables toolbar and menu actions synchronously");

            window.Main.CurrentFile = first;
            await Task.Delay(200);
            if (System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(first.File.RequireLocalPath())) == "native-p2-toolbar-fixtures")
            {
                int targetRating = first.Rating == 3 ? 2 : 3;
                var ratingButton = window.ControlBar.Ratings[targetRating];
                ratingButton.PerformClick(ratingButton);
                for (int attempt = 0; attempt < 100 && first.Rating != targetRating; attempt++) await Task.Delay(50);
                var exif = await ExifLoader.LoadExifDataAsync(new LocalFileRef(first.File.RequireLocalPath()));
                if (first.Rating != targetRating || exif?.Rating != targetRating || window.Main.CurrentFile != first)
                    throw new InvalidOperationException("评分按钮写回/主图身份错误");
                observations.Add($"real rating button changes owned fixture to {targetRating} stars and preserves current photo");
            }
            await VerifyRatingContinuityAsync(window, observations);
            await VerifyRatingConflictAsync(window, output, observations);
            await VerifyRatingSourcesLiveAsync(window, output, observations);
            foreach (var entry in settings.ExifDisplayItems) entry.IsEnabled = true;
            foreach (var hotkey in settings.Hotkeys) hotkey.IsDisplay = true;
            settings.LayoutMode = LayoutMode.Row;
            window.Window.SetContentSize(new CGSize(650, 480));
            await Task.Delay(150);
            VerifyGeometry(window, observations);
            if (!window.ControlScroll.HasHorizontalScroller || window.ControlBar.Frame.Width <= window.ControlScroll.ContentView.Bounds.Width)
                throw new InvalidOperationException("超长命令分组没有可操作的横向滚动");
            window.ControlScroll.ContentView.ScrollToPoint(new CGPoint(window.ControlBar.Frame.Width - window.ControlScroll.ContentView.Bounds.Width, 0));
            if (window.ControlScroll.ContentView.Bounds.X <= 0) throw new InvalidOperationException("超长分组不能滚入");
            await VerifyControlScrollOffsetAsync(window, settings, observations);
            observations.Add("all enabled commands remain grouped and scroll instead of clipping inaccessible actions");
        }
        finally
        {
            settings.ApplySnapshot(snapshot);
            window.Main.CurrentFile = first;
            await Task.Delay(200);
        }
        await File.WriteAllLinesAsync(System.IO.Path.Combine(output, "toolbar-ui-checks.txt"), observations);
    }

    /// <summary>核对真实分组的外边距、信息层级、按钮形状及评分方向。</summary>
    private static void VerifyGeometry(MacMainWindow window, List<string> observations)
    {
        var bar = window.ControlBar;
        bool horizontal = window.Main.IsRowLayout;
        if (bar.Groups.Count != 3 || bar.Ratings.Count != 6) throw new InvalidOperationException("EXIF/评分/命令未分为三个完整区域");
        var bounds = bar.Bounds;
        foreach (var group in bar.Groups)
        {
            var frame = group.Frame;
            if (frame.X < 10 || frame.Y < 10 || frame.X + frame.Width > bounds.Width - 10 + .01 || frame.Y + frame.Height > bounds.Height - 10 + .01)
                throw new InvalidOperationException($"工具栏分组未保留 10 点外边距：{frame}/{bounds}");
            foreach (var other in bar.Groups.Where(other => other != group))
                if (frame.IntersectsWith(other.Frame)) throw new InvalidOperationException("工具栏分组重叠");
        }
        VerifyGroupContents(bar, horizontal);
        VerifyControlScroller(window);
        foreach (var button in bar.Commands.Values)
            if (button is not MacControlBar.ToolbarButton || button.Bordered || button.Layer?.MasksToBounds == true)
                throw new InvalidOperationException("工具按钮没有使用单层完整方形边框的原生 NSButton");
        foreach (var tile in bar.Groups[0].Subviews)
        {
            var labels = tile.Subviews.OfType<NSTextField>().ToArray();
            if (labels.Length != 2 || tile.ToolTip == null || labels[1].Font.PointSize <= labels[0].Font.PointSize)
                throw new InvalidOperationException("EXIF 标签/值层级丢失");
        }
        observations.Add($"{window.Main.Settings.LayoutMode}/{window.Window.ContentView!.Bounds.Size}: groups={string.Join(";", bar.Groups.Select(group => group.Frame))}; clip={window.ControlScroll.ContentView.Bounds}; document={bar.Frame}; padding/child margins/stretch retained");
    }

    private static void VerifyGroupContents(MacControlBar bar, bool horizontal)
    {
        for (int groupIndex = 0; groupIndex < bar.Groups.Count; groupIndex++)
        {
            var group = bar.Groups[groupIndex];
            var children = group.Subviews;
            double width = groupIndex == 0 ? 50 : groupIndex == 1 ? 30 : 40;
            double height = groupIndex == 1 ? 30 : 40;
            double childMain = horizontal ? width : height;
            double childCross = horizontal ? height : width;
            double inset = groupIndex == 0 ? 4 : groupIndex == 1 ? 8 : 0;
            double spacing = groupIndex == 0 ? 4 : groupIndex == 1 ? 0 : 8;
            double expectedMain = children.Length * childMain + (children.Length - 1) * spacing + inset * 2 * (groupIndex == 0 ? children.Length : 1);
            double actualMain = horizontal ? (double)group.Frame.Width : (double)group.Frame.Height;
            double actualCross = horizontal ? (double)group.Frame.Height : (double)group.Frame.Width;
            if (Math.Abs(actualMain - expectedMain) > .01 || actualCross < childCross)
                throw new InvalidOperationException($"分组主轴尺寸或交叉轴拉伸错误：{group.Frame}");
            for (int index = 0; index < children.Length; index++)
            {
                int position = groupIndex == 1 && !horizontal ? children.Length - index - 1 : index;
                double main = inset + position * (childMain + spacing + (groupIndex == 0 ? inset * 2 : 0));
                double cross = (actualCross - childCross) / 2;
                RequireFrame(children[index].Frame, new CGRect(horizontal ? main : cross, horizontal ? cross : main, width, height), $"组 {groupIndex} 子项 {index}");
            }
        }
    }

    private static void VerifyReferenceLayouts(MacMainWindow window, List<string> observations)
    {
        var bar = window.ControlBar;
        if (bar.Groups.Count != 3 || bar.Groups[0].Subviews.Length != 4 || bar.Ratings.Count != 6 || bar.Commands.Count != 6)
            throw new InvalidOperationException("固定几何验收需要 4 个 EXIF、6 个评分和 6 个命令");
        (bool Horizontal, double Main, double Cross, CGRect[] Frames)[] cases =
        [
            (true, 760, 60, [new(10, 10, 244, 40), new(264, 10, 196, 40), new(470, 10, 280, 40)]),
            (true, 759, 110, [new(154.5, 10, 244, 40), new(408.5, 10, 196, 40), new(239.5, 60, 280, 40)]),
            (true, 470, 110, [new(10, 10, 244, 40), new(264, 10, 196, 40), new(95, 60, 280, 40)]),
            (true, 469, 150, [new(112.5, 10, 244, 40), new(136.5, 60, 196, 30), new(94.5, 100, 280, 40)]),
            (false, 720, 70, [new(10, 10, 50, 204), new(10, 224, 50, 196), new(10, 430, 50, 280)]),
            (false, 719, 120, [new(10, 154.5, 50, 204), new(10, 368.5, 50, 196), new(70, 219.5, 40, 280)]),
            (false, 430, 120, [new(10, 10, 50, 204), new(10, 224, 50, 196), new(70, 75, 40, 280)]),
            (false, 429, 160, [new(10, 112.5, 50, 204), new(70, 116.5, 30, 196), new(110, 74.5, 40, 280)])
        ];
        try
        {
            foreach (var test in cases)
            {
                var measured = bar.Measure(test.Horizontal, test.Main);
                double measuredCross = test.Horizontal ? (double)measured.Height : (double)measured.Width;
                if (Math.Abs(measuredCross - test.Cross) > .01) throw new InvalidOperationException("固定换行阈值的交叉轴尺寸错误");
                bar.Arrange(test.Horizontal ? new CGSize(test.Main, 180) : new CGSize(180, test.Main));
                for (int index = 0; index < test.Frames.Length; index++)
                    RequireFrame(bar.Groups[index].Frame, test.Frames[index], $"{test.Horizontal}/{test.Main} 组 {index}");
                VerifyGroupContents(bar, test.Horizontal);
            }
        }
        finally
        {
            var viewport = window.ControlScroll.ContentView.Bounds.Size;
            bar.Measure(window.Main.IsRowLayout, window.Main.IsRowLayout ? (double)viewport.Width : (double)viewport.Height);
            bar.Arrange(viewport);
        }
        observations.Add("fixed geometry: row wrap thresholds 760/470, column thresholds 720/430; exact fit and one point below; 10 point padding/spacing; per-tile EXIF margins; main-axis rating insets; line cross-axis stretch");
    }

    /// <summary>
    /// TOOLBAR-08：控制栏宿主的裁剪区不得被常驻竖向滚动条挤窄。这里把样式强制设成 legacy 再排布一次，
    /// 宿主必须锁回 Overlay（接入鼠标的系统偏好就等效于 legacy），且竖向滚动条只出现在列布局且文档溢出时。
    /// </summary>
    private static void VerifyControlScroller(MacMainWindow window)
    {
        var scroll = window.ControlScroll;
        // 自然状态（进程带 -AppleShowScrollBars Always 时系统偏好就是 legacy）：宿主应在排布时已锁回 Overlay。
        if (scroll.ScrollerStyle != NSScrollerStyle.Overlay)
            throw new InvalidOperationException($"控制栏滚动条样式不是 Overlay：{scroll.ScrollerStyle}");
        RequireNoGutter(scroll, "自然排布");
        // 再把样式强制成 legacy 后重新排布一次，验证宿主每次都锁回 Overlay。
        scroll.ScrollerStyle = NSScrollerStyle.Legacy;
        scroll.Tile();
        if (scroll.ScrollerStyle != NSScrollerStyle.Overlay)
            throw new InvalidOperationException($"控制栏滚动条样式未锁回 Overlay：{scroll.ScrollerStyle}");
        RequireNoGutter(scroll, "legacy 偏好强制后");
        bool overflow = (double)window.ControlBar.Frame.Height > (double)scroll.ContentView.Bounds.Height + .5;
        bool expectedScroller = !window.Main.IsRowLayout && overflow;
        if (scroll.HasVerticalScroller != expectedScroller)
            throw new InvalidOperationException(
                $"控制栏竖向滚动条开关与布局/溢出不符：row={window.Main.IsRowLayout}, overflow={overflow}, has={scroll.HasVerticalScroller}");
    }

    /// <summary>裁剪区宽度必须等于滚动宿主宽度（竖向滚动条不得挤窄内容）。</summary>
    private static void RequireNoGutter(NSScrollView scroll, string stage)
    {
        double gutter = (double)scroll.Bounds.Width - (double)scroll.ContentView.Bounds.Width;
        if (Math.Abs(gutter) > .5)
            throw new InvalidOperationException($"{stage}控制栏裁剪区被竖向滚动条挤窄 {gutter:0.##} 点：clip {scroll.ContentView.Bounds.Width} / bar {scroll.Bounds.Width}");
    }

    /// <summary>TOOLBAR-03/RATING-04：点亮星 color.star.on、空心星 color.star.off、清零叉中性 color.star.clear；冲突时 0 号取 color.star.conflict。</summary>
    private static void VerifyRatingGroupColors(MacMainWindow window)
    {
        var bar = window.ControlBar;
        if (bar.Ratings.Count != 6) throw new InvalidOperationException("评分组按钮数量不是 6");
        int rating = window.Main.CurrentFile?.Rating ?? 0;
        if (window.Main.CurrentFile?.HasRatingConflict == true)
        {
            if (!ReferenceEquals(bar.Ratings[0].Image, MacSymbols.Get(MacSymbols.RatingConflict)))
                throw new InvalidOperationException("评分组冲突时 0 号未换成警示三角");
            RequireColor(bar.Ratings[0].ContentTintColor, MacDesignColors.Argb(DesignTokens.ColorStarConflict), "评分组冲突三角");
        }
        else
        {
            if (!ReferenceEquals(bar.Ratings[0].Image, MacSymbols.Get("xmark")))
                throw new InvalidOperationException("评分组无冲突时 0 号不是清零叉");
            RequireColor(bar.Ratings[0].ContentTintColor, MacDesignColors.Argb(DesignTokens.ColorStarClear), "评分组清零叉");
        }
        for (int value = 1; value < 6; value++)
            RequireColor(bar.Ratings[value].ContentTintColor,
                MacDesignColors.Argb(value <= rating ? DesignTokens.ColorStarOn : DesignTokens.ColorStarOff), $"评分组 {value} 星");
    }

    /// <summary>TOOLBAR-04：评分快捷命令为星形内叠数字，数字 9 点半粗、取按钮底色并居中，且不拦截按钮点击。</summary>
    private static void VerifyRatingCommand(NSButton button, string digit)
    {
        var label = button.Subviews.OfType<NSTextField>().SingleOrDefault(field => field.StringValue == digit)
            ?? throw new InvalidOperationException($"评分快捷命令 {digit} 缺少星形内叠数字");
        if (Math.Abs((double)label.Font.PointSize - DesignTokens.ToolbarRatingDigitFontSize) > .01)
            throw new InvalidOperationException($"评分快捷命令数字字号 {label.Font.PointSize} 不是 {DesignTokens.ToolbarRatingDigitFontSize}");
        RequireColor(label.TextColor, MacDesignColors.Argb(DesignTokens.ColorToolbarCommandBackground), "评分快捷命令数字");
        if (Math.Abs((double)(label.Frame.X + label.Frame.Width / 2) - (double)button.Bounds.Width / 2) > .75
            || Math.Abs((double)(label.Frame.Y + label.Frame.Height / 2) - (double)button.Bounds.Height / 2) > .75)
            throw new InvalidOperationException($"评分快捷命令数字未叠在星形中心：{label.Frame} / {button.Bounds}");
        if (button.Image == null || button.Title.Length != 0)
            throw new InvalidOperationException("评分快捷命令仍把数字作为并排标题显示");
        var center = button.ConvertPointToView(new CGPoint(button.Bounds.Width / 2, button.Bounds.Height / 2), null);
        var hit = button.Window?.ContentView?.HitTest(center);
        if (!ReferenceEquals(hit, button))
            throw new InvalidOperationException($"评分快捷命令中心命中不到按钮本身（星内数字拦截了点击）：{hit?.GetType().Name ?? "null"}");
        if (!ReferenceEquals(button.ContentTintColor, NSColor.Label))
            throw new InvalidOperationException("评分快捷命令星形未取主文字色 color.text.primary");
    }

    /// <summary>TOOLBAR-04：0 星是清零叉，不带叠层数字。</summary>
    private static void VerifyClearRatingCommand(NSButton button)
    {
        if (button.Image == null || button.Subviews.OfType<NSTextField>().Any())
            throw new InvalidOperationException("0 星评分快捷命令不是纯清零叉");
        if (!ReferenceEquals(button.ContentTintColor, NSColor.Label))
            throw new InvalidOperationException("0 星评分快捷命令未取主文字色 color.text.primary");
    }

    /// <summary>把两个 AppKit 颜色都转到 sRGB 后逐分量比较（令牌颜色经 MacDesignColors 生成，同一色空间）。</summary>
    private static void RequireColor(NSColor? actual, NSColor expected, string context)
    {
        var lhs = actual?.UsingColorSpace(NSColorSpace.SRGBColorSpace)?.CGColor.Components;
        var rhs = expected.UsingColorSpace(NSColorSpace.SRGBColorSpace)?.CGColor.Components;
        if (lhs == null || rhs == null || lhs.Length < 4 || rhs.Length < 4)
            throw new InvalidOperationException($"{context}颜色无法换算到 sRGB");
        for (int channel = 0; channel < 4; channel++)
            if (Math.Abs((double)lhs[channel] - (double)rhs[channel]) > .004)
                throw new InvalidOperationException($"{context}颜色不符：{actual} 预期 {expected}");
    }

    private static void RequireFrame(CGRect actual, CGRect expected, string context)
    {
        if (Math.Abs((double)(actual.X - expected.X)) > .01 || Math.Abs((double)(actual.Y - expected.Y)) > .01 ||
            Math.Abs((double)(actual.Width - expected.Width)) > .01 || Math.Abs((double)(actual.Height - expected.Height)) > .01)
            throw new InvalidOperationException($"{context}：实际 {actual}，预期 {expected}");
    }

    /// <summary>TOOLBAR-08：重建不重置用户滚动位置，换向或尺寸变化后只夹取到合法范围。</summary>
    private static async Task VerifyControlScrollOffsetAsync(MacMainWindow window, SettingsViewModel settings, List<string> observations)
    {
        var scroll = window.ControlScroll;
        double rowOffset = (double)scroll.ContentView.Bounds.X;
        if (rowOffset <= 0) throw new InvalidOperationException("控制栏横向滚动未生效，无法核对偏移保持");
        var next = settings.Hotkeys.Single(hotkey => hotkey.Command == "Next");
        var originalPrimary = next.PrimaryHotkey;
        next.PrimaryHotkey = AppGesture.FromKey(new AppKeyGesture(GestureKey.D9, GestureModifiers.Control));
        await Task.Delay(150);
        next.PrimaryHotkey = originalPrimary;
        await Task.Delay(200);
        double rebuiltOffset = (double)scroll.ContentView.Bounds.X;
        if (Math.Abs(rebuiltOffset - rowOffset) > 1)
            throw new InvalidOperationException($"控制栏重建把滚动位置从 {rowOffset:0.##} 重置为 {rebuiltOffset:0.##}");

        // 换向前把位置推到行布局的末端；换向后的文档尺寸不同，必须夹到列布局的合法范围。
        double rowMaximum = Math.Max(0, (double)window.ControlBar.Frame.Width - (double)scroll.ContentView.Bounds.Width);
        scroll.ContentView.ScrollToPoint(new CGPoint(rowMaximum, 0));
        scroll.ReflectScrolledClipView(scroll.ContentView);
        settings.LayoutMode = LayoutMode.Col;
        window.Window.SetContentSize(new CGSize(650, 480));
        await Task.Delay(250);
        var bounds = scroll.ContentView.Bounds;
        double maximumX = Math.Max(0, (double)window.ControlBar.Frame.Width - (double)bounds.Width);
        double maximumY = Math.Max(0, (double)window.ControlBar.Frame.Height - (double)bounds.Height);
        if (bounds.X < -.5 || bounds.X > maximumX + .5 || bounds.Y < -.5 || bounds.Y > maximumY + .5)
            throw new InvalidOperationException($"控制栏换向后的滚动偏移未被夹取：offset={bounds.Location}, max=({maximumX:0.##},{maximumY:0.##})");
        observations.Add($"scroll offset kept across rebuild ({rowOffset:0.##}→{rebuiltOffset:0.##}) and clamped after direction change to ({bounds.X:0.##},{bounds.Y:0.##}) within ({maximumX:0.##},{maximumY:0.##})");
    }

    /// <summary>读取真实按钮绘制像素，避免 40×40 外框通过但系统边框仍只画成矮条。</summary>
    private static void VerifyButtonSurface(NSButton button, string output)
    {
        string path = System.IO.Path.Combine(output, "toolbar-button-surface.png");
        NativeSnapshots.SaveView(button, path);
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidOperationException("按钮绘制截图无法读取");
        var painted = Enumerable.Range(0, bitmap.Height).Where(row =>
        {
            var color = bitmap.GetPixel(bitmap.Width / 2, row);
            return color.Alpha > 0 && Math.Max(color.Red, Math.Max(color.Green, color.Blue)) > 25;
        }).ToArray();
        if (painted.Length == 0 || painted[^1] - painted[0] < bitmap.Height * .8)
            throw new InvalidOperationException("系统按钮的实际边框没有覆盖方形区域");
        using var hover = NSEvent.MouseEvent(NSEventType.MouseMoved, CGPoint.Empty, 0, NSProcessInfo.ProcessInfo.SystemUptime, button.Window!.WindowNumber, null!, 1, 1, 0)!;
        button.MouseEntered(hover);
        string hovered = Path.Combine(output, "toolbar-button-hover.png");
        NativeSnapshots.SaveView(button, hovered);
        if (File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(hovered))) throw new InvalidOperationException("按钮悬停无可见反馈");
        button.Cell!.Highlighted = true;
        button.NeedsDisplay = true;
        string pressed = Path.Combine(output, "toolbar-button-pressed.png");
        NativeSnapshots.SaveView(button, pressed);
        if (File.ReadAllBytes(hovered).SequenceEqual(File.ReadAllBytes(pressed))) throw new InvalidOperationException("按钮按下无可见反馈");
        button.Cell.Highlighted = false;
        button.MouseExited(hover);
    }

    /// <summary>
    /// FILM-06：用胶片栏卡片的真实星级按钮改写自有可写副本，核对文件级写回、主图身份与筛选连续性的两半
    /// （当前照片改星后落出筛选仍暂留、用户切走后才移除），收尾还原评级与进入本检查时的打开状态。
    /// </summary>
    private static async Task VerifyRatingContinuityAsync(MacMainWindow window, List<string> observations)
    {
        var settings = window.Main.Settings;
        var filter = window.Main.FileVM.FilterBar;
        var thumbnailList = window.Main.FileVM.ThumbnailList;
        var entryFile = window.Main.CurrentFile ?? throw new InvalidOperationException("评分连续性回归缺少进入时的当前照片");
        string entryPath = entryFile.File.RequireLocalPath();
        var entryId = entryFile.File.StorageId;
        bool entryThumbnailVisible = window.Main.IsThumbnailViewVisible;
        string entryFilter = filter.SelectedRatingFilter;
        string fixtures = System.IO.Path.GetFullPath("release/native-p2-toolbar-fixtures");
        var entryRatings = new Dictionary<string, int>(StringComparer.Ordinal);
        var entryLocalPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        int entryRating = 0;
        // 星级按钮只在卡片创建时按设置显隐，故先打开胶片栏与评分组再建卡片。
        settings.ShowRating = true;
        window.Main.IsThumbnailViewVisible = true;
        await Task.Delay(250);

        async Task RestoreAsync()
        {
            filter.SelectedRatingFilter = entryFilter;
            foreach (var pair in entryRatings)
            {
                var candidate = window.Main.FolderVM.AllFiles.FirstOrDefault(item => item.File.StorageId.Value == pair.Key);
                if (candidate != null && candidate.Rating != pair.Value) await window.Main.SetRatingAsync(candidate, pair.Value);
            }
            window.Main.IsThumbnailViewVisible = entryThumbnailVisible;
            if (window.Main.CurrentFile?.File.StorageId != entryId) await window.Main.FolderVM.OpenImageAsync(new LocalFileRef(entryPath));
        }

        try
        {
            // 只对自有可写副本操作：调用方传入别的图片时先切到副本目录。
            if (!IsOwnedFixtureFile(window.Main.CurrentFile)) await window.Main.FolderVM.OpenFolderAsync(new LocalFolderRef(fixtures));
            await WaitAsync(() => window.Main.CurrentFile != null && thumbnailList.FilteredFiles.Count == 3, "自有评分副本载入");
            var file = window.Main.CurrentFile!;
            if (!IsOwnedFixtureFile(file)) throw new InvalidOperationException("评分连续性回归未落在自有可写副本上");
            foreach (var candidate in window.Main.FolderVM.AllFiles)
            {
                string candidatePath = candidate.File.RequireLocalPath();
                string candidateKey = candidate.File.StorageId.Value;
                entryLocalPaths[candidateKey] = candidatePath;
                entryRatings[candidateKey] = (await ExifLoader.LoadExifDataAsync(new LocalFileRef(candidatePath)))?.Rating ?? 0;
            }
            entryRating = entryRatings[file.File.StorageId.Value];
            int target = entryRating == 3 ? 2 : 3;
            var other = window.Main.FolderVM.AllFiles.First(candidate => !ReferenceEquals(candidate, file));
            // 另一张副本先与当前照片同星级，才能同时构成“当前照片改星后落出筛选”和“列表里还有符合筛选的照片”。
            if (entryRatings[other.File.StorageId.Value] != entryRating) await window.Main.SetRatingAsync(other, entryRating);
            await WaitAsync(() => file.Rating == entryRating && other.Rating == entryRating, "自有副本评级就绪");
            string key = $"Eq{entryRating}";
            filter.SelectedRatingFilter = key;
            await Task.Delay(250);
            if (!thumbnailList.FilteredFiles.Contains(file) || !thumbnailList.FilteredFiles.Contains(other))
                throw new InvalidOperationException($"筛选键 {key} 与自有副本评级不一致，无法核对暂留规则");

            window.Main.FileVM.ThumbnailList.ScrollToCurrent();
            var card = await WaitForCardAsync(window, file);
            var star = Descendants(card.View).OfType<NSButton>().SingleOrDefault(button => button.ToolTip == $"{target} 星")
                ?? throw new InvalidOperationException("胶片栏卡片缺少目标星级的按钮");
            if (star.Hidden) throw new InvalidOperationException("胶片栏卡片星级按钮未按设置显示");
            var starCenter = new CGPoint(star.Bounds.Width / 2, star.Bounds.Height / 2);
            var cardHit = card.View.HitTest(star.ConvertPointToView(starCenter, card.View.Superview));
            var windowHit = window.Window.ContentView!.HitTest(star.ConvertPointToView(starCenter, null));
            if (cardHit is not NSButton || windowHit is not NSButton)
                throw new InvalidOperationException($"星级按钮未独立命中，点星会选中父卡片：card={cardHit?.GetType().Name ?? "null"}, window={windowHit?.GetType().Name ?? "null"}");
            star.PerformClick(star);
            await WaitAsync(() => file.Rating == target, $"胶片栏星级按钮写回 {target} 星");
            var written = await ExifLoader.LoadExifDataAsync(new LocalFileRef(file.File.RequireLocalPath()));
            if (file.Rating != target || written?.Rating != target)
                throw new InvalidOperationException($"胶片栏星级按钮未写回文件：memory={file.Rating}, file={written?.Rating}");
            if (!ReferenceEquals(window.Main.CurrentFile, file))
                throw new InvalidOperationException("胶片栏星级按钮改变了当前主图身份");
            if (!thumbnailList.FilteredFiles.Contains(file))
                throw new InvalidOperationException("改星后当前照片未按 FILM-06 暂留，选片进度瞬间丢失");
            observations.Add($"real filmstrip star button writes {target} stars to the owned fixture XMP without changing the current photo; the card stays listed although it no longer matches {key}");

            window.Main.CurrentFile = other;
            await WaitAsync(() => !thumbnailList.FilteredFiles.Contains(file), "切走后移除不符合筛选的照片");
            int expected = window.Main.FolderVM.AllFiles.Count(candidate => candidate.Rating == entryRating);
            if (thumbnailList.FilteredFiles.Contains(file) || thumbnailList.FilteredFiles.Count != expected)
                throw new InvalidOperationException($"切走后筛选列表与 {key} 不一致：count={thumbnailList.FilteredFiles.Count}, expected={expected}");

            await RestoreAsync();
            bool restored = filter.SelectedRatingFilter == entryFilter && window.Main.CurrentFile?.File.StorageId == entryId;
            foreach (var pair in entryRatings)
                restored &= (await ExifLoader.LoadExifDataAsync(new LocalFileRef(entryLocalPaths[pair.Key])))?.Rating == pair.Value;
            if (!restored)
                throw new InvalidOperationException("评分连续性回归未还原自有副本评级与进入时的打开状态");
            observations.Add($"leaving the photo drops it from {key} with the filtered count matching the shared filter; owned fixture stars and filter restored to {entryRating}/{entryFilter}");
        }
        finally
        {
            try { await RestoreAsync(); }
            catch (Exception exception) { Console.Error.WriteLine("[NativeToolbar] 评分连续性收尾还原失败：" + exception.Message); }
        }
    }

    /// <summary>
    /// RATING-04：用自有 JPG 副本 + 自建同名 sidecar 形成真实的两来源冲突，核对胶片栏与控制栏
    /// 0 号槽位的警示三角（几何不变）、纵向来源条目、三种实际展开方向、连续重开与指针移入、
    /// 任一条目整组统一写入、冲突解除后关闭且不误报失败，以及卡片入口不切换当前主图。
    /// </summary>
    private static async Task VerifyRatingConflictAsync(MacMainWindow window, string output, List<string> observations)
    {
        var settings = window.Main.Settings;
        var entry = window.Main.CurrentFile ?? throw new InvalidOperationException("评分冲突回归缺少进入时的当前照片");
        string entryPath = entry.File.RequireLocalPath();
        bool entryShowRating = settings.ShowRating;
        bool entryThumbnailVisible = window.Main.IsThumbnailViewVisible;
        string root = System.IO.Path.GetFullPath("release/native-p2-acceptance/rating-conflict-fixtures");
        string photoPath = System.IO.Path.Combine(root, "conflict.jpg");
        string cleanPath = System.IO.Path.Combine(root, "clean.jpg");
        string sidecarPath = System.IO.Path.Combine(root, "conflict.xmp");
        int failures = 0;
        void OnFailed(ImageFile failed, int rating) => failures++;
        try
        {
            settings.ShowRating = true;
            window.Main.IsThumbnailViewVisible = true;
            Directory.CreateDirectory(root);
            File.Copy(System.IO.Path.GetFullPath("release/native-p2-toolbar-fixtures/toolbar-01.jpg"), photoPath, true);
            File.Copy(System.IO.Path.GetFullPath("release/native-p2-toolbar-fixtures/toolbar-02.jpg"), cleanPath, true);
            File.Delete(sidecarPath);
            await window.Main.FolderVM.OpenFolderAsync(new LocalFolderRef(root));
            await WaitAsync(() => window.Main.CurrentFile != null && window.Main.FolderVM.AllFiles.Count == 2, "冲突夹具载入");
            var conflict = window.Main.FolderVM.AllFiles.Single(file => file.Name == "conflict.jpg");
            var clean = window.Main.FolderVM.AllFiles.Single(file => file.Name == "clean.jpg");
            window.Main.CurrentFile = conflict;
            await window.Main.SetRatingAsync(conflict, 2);
            await WaitAsync(() => conflict.Rating == 2, "内嵌来源写回 2 星");
            // sidecar 是独立来源：同目录刷新会重读它，两份不同事实即构成组级冲突。
            await File.WriteAllTextAsync(sidecarPath, SidecarPacket(1));
            await window.Main.FolderVM.OpenFolderAsync(new LocalFolderRef(root));
            await WaitAsync(() => window.Main.CurrentFile != null && window.Main.CurrentFile.HasRatingConflict
                && window.Main.CurrentFile.RatingSources.Count == 2, "来源冲突就绪");
            conflict = window.Main.CurrentFile!;
            await Task.Delay(150);

            // 控制栏 0 号：三角 + color.star.conflict，几何与命中区仍是 30×30。
            var clear = window.ControlBar.Ratings[0];
            if (clear.Tag != 0 || Math.Abs((double)clear.Frame.Width - 30) > .01 || Math.Abs((double)clear.Frame.Height - 30) > .01)
                throw new InvalidOperationException($"冲突三角改变了评分组 0 号按钮几何：{clear.Frame}");
            if (!ReferenceEquals(clear.Image, MacSymbols.Get(MacSymbols.RatingConflict)))
                throw new InvalidOperationException("控制栏 0 号槽位未显示冲突警示三角");
            RequireColor(clear.ContentTintColor, MacDesignColors.Argb(DesignTokens.ColorStarConflict), "控制栏冲突三角");
            if (clear.ToolTip != "评分来源冲突")
                throw new InvalidOperationException($"控制栏冲突入口提示错误：{clear.ToolTip}");

            window.Main.RatingWriteFailed += OnFailed;
            var currentBefore = window.Main.CurrentFile;
            var popover = window.RatingSourcesPopover;
            var sources = window.Main.RatingSources.Items;
            string[] starTips = ["无星级", "1 星", "2 星", "3 星", "4 星", "5 星"];
            static double MidY(CGRect frame) => (double)(frame.Y + frame.Height / 2);
            var shortLayout = MacRatingSourcesPopover.MeasureEntryLayout("A6702544.ARW", "ARW", "1 星");
            if (shortLayout.TwoLines
                || Math.Abs(MidY(shortLayout.NameFrame) - MidY(shortLayout.StatusFrame)) > .5
                || Math.Abs(MidY(shortLayout.BadgeFrame) - MidY(shortLayout.StatusFrame)) > .5
                || (double)shortLayout.NameFrame.X >= (double)shortLayout.BadgeFrame.X
                || (double)shortLayout.BadgeFrame.X >= (double)shortLayout.StatusFrame.X
                || Math.Abs((double)(shortLayout.StatusFrame.Right - shortLayout.NameFrame.X)
                    - MacRatingSourcesPopover.EntryContentWidth) > .01)
                throw new InvalidOperationException("来源三信息没有按文件名居左、来源与状态居右同行排列");
            var longLayout = MacRatingSourcesPopover.MeasureEntryLayout(
                "rating-conflict-source-with-a-deliberately-long-name.jpg", "JPG", "1 星");
            if (!longLayout.TwoLines
                || Math.Abs((double)longLayout.NameFrame.Width - MacRatingSourcesPopover.EntryContentWidth) > .01
                || (double)longLayout.NameFrame.Y >= (double)longLayout.BadgeFrame.Y
                || Math.Abs(MidY(longLayout.BadgeFrame) - MidY(longLayout.StatusFrame)) > .5
                || Math.Abs((double)(longLayout.StatusFrame.Right - longLayout.NameFrame.X)
                    - MacRatingSourcesPopover.EntryContentWidth) > .01)
                throw new InvalidOperationException("长文件名没有改为双行信息布局或文件名越出浮窗宽度");
            observations.Add("RATING-04: source popover keeps three info items on one line and switches to two lines for long names");
            CGRect[]? reopenedFrames = null;
            NSView? previousContent = null;
            foreach (var mode in new[] { LayoutMode.Row, LayoutMode.Col })
            {
                settings.LayoutMode = mode;
                await Task.Delay(200);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    clear = window.ControlBar.Ratings[0];
                    var anchorScreen = window.Window.ConvertRectToScreen(clear.ConvertRectToView(clear.Bounds, null));
                    clear.PerformClick(clear);
                    await WaitAsync(() => popover.IsOpen, $"来源浮窗展开 {mode}/{attempt}");
                    await Task.Delay(60);
                    if (previousContent != null && ReferenceEquals(previousContent, popover.Content))
                        throw new InvalidOperationException("重开复用了已随旧浮窗释放的内容视图");
                    previousContent = popover.Content;
                    if (popover.Content.Layer is { BackgroundColor: not null } || (popover.Content.Layer?.BorderWidth ?? 0) > 0)
                        throw new InvalidOperationException("来源浮窗重复绘制了自定义背景或描边，覆盖了原生毛玻璃");
                    sources = window.Main.RatingSources.Items;
                    var expectedEdge = mode == LayoutMode.Row ? NSRectEdge.MinYEdge : NSRectEdge.MinXEdge;
                    if (popover.LastPreferredEdge != expectedEdge)
                        throw new InvalidOperationException($"{mode} 控制栏浮窗展开方向错误：{popover.LastPreferredEdge}");
                    var popoverFrame = (popover.Content.Window ?? throw new InvalidOperationException("来源浮窗没有原生窗口")).Frame;
                    double anchorCenterX = (double)(anchorScreen.X + anchorScreen.Width / 2);
                    double anchorCenterY = (double)(anchorScreen.Y + anchorScreen.Height / 2);
                    double popoverCenterX = (double)(popoverFrame.X + popoverFrame.Width / 2);
                    double popoverCenterY = (double)(popoverFrame.Y + popoverFrame.Height / 2);
                    if (mode == LayoutMode.Row && popoverCenterY <= anchorCenterY
                        || mode == LayoutMode.Col && popoverCenterX >= anchorCenterX)
                        throw new InvalidOperationException($"{mode} 浮窗实际位置方向错误：anchor={anchorScreen}, popover={popoverFrame}");
                    if (!popover.IsAnchoredToControlBar)
                        throw new InvalidOperationException("重开后旧关闭通知清空了控制栏锚点标记");
                    if (popover.EntryViews.Count != 2 || window.Main.RatingSources.Items.Count != 2)
                        throw new InvalidOperationException($"来源浮窗条目数错误：{popover.EntryViews.Count}");
                    if (popover.DividerViews.Count != popover.EntryViews.Count - 1)
                        throw new InvalidOperationException($"来源分割线数量错误：{popover.DividerViews.Count}");
                    for (int index = 0; index < popover.DividerViews.Count; index++)
                    {
                        var divider = popover.DividerViews[index];
                        var above = popover.EntryViews[index];
                        var below = popover.EntryViews[index + 1];
                        if (divider.Frame.Height > 1.01 || divider.Frame.Width < above.Frame.Width - .01
                            || divider.Frame.Y <= above.Frame.Y + above.Frame.Height
                            || divider.Frame.Y + divider.Frame.Height >= below.Frame.Y)
                            throw new InvalidOperationException($"来源分割线没有落在两个条目之间：{divider.Frame}");
                        if (divider.BoxType != NSBoxType.NSBoxSeparator)
                            throw new InvalidOperationException("来源分割线不是标准分隔控件");
                    }
                    var frames = popover.EntryViews.Select(entry => entry.Frame).ToArray();
                    if (reopenedFrames == null) reopenedFrames = frames;
                    for (int index = 0; index < popover.EntryViews.Count; index++)
                    {
                        var sourceEntry = popover.EntryViews[index];
                        if (index > 0)
                        {
                            var previous = popover.EntryViews[index - 1];
                            if (Math.Abs((double)sourceEntry.Frame.X - (double)previous.Frame.X) > .01
                                || (double)sourceEntry.Frame.Y <= (double)previous.Frame.Y)
                                throw new InvalidOperationException("来源条目没有按相同宽度纵向排列");
                        }
                        if (sourceEntry.Frame.X < 0 || sourceEntry.Frame.Y < 0
                            || (double)(sourceEntry.Frame.X + sourceEntry.Frame.Width) > (double)popover.Content.Bounds.Width + .01
                            || (double)(sourceEntry.Frame.Y + sourceEntry.Frame.Height) > (double)popover.Content.Bounds.Height + .01)
                            throw new InvalidOperationException($"重开后来源条目越出浮窗：{sourceEntry.Frame} / {popover.Content.Bounds}");
                        if (reopenedFrames[index] != sourceEntry.Frame)
                            throw new InvalidOperationException($"连续重开改变了条目布局：{reopenedFrames[index]} → {sourceEntry.Frame}");
                        if (sourceEntry.Name.StringValue != sources[index].Name
                            || sourceEntry.Kind.StringValue != sources[index].Kind
                            || sourceEntry.Status.StringValue != sources[index].Status)
                            throw new InvalidOperationException($"来源条目文字错误：{sourceEntry.Name.StringValue}/{sourceEntry.Kind.StringValue}/{sourceEntry.Status.StringValue}");
                        if (sourceEntry.TwoLines)
                            throw new InvalidOperationException("短文件名来源误用了双行信息布局");
                        double nameMid = MidY(sourceEntry.Name.Frame);
                        double badgeMid = MidY(sourceEntry.Badge.Frame);
                        double statusMid = MidY(sourceEntry.Status.Frame);
                        double kindMid = (double)sourceEntry.Badge.Frame.Y + MidY(sourceEntry.Kind.Frame);
                        if (Math.Abs(nameMid - statusMid) > .5 || Math.Abs(badgeMid - statusMid) > .5
                            || Math.Abs(kindMid - badgeMid) > .5
                            || sourceEntry.Name.Frame.X >= sourceEntry.Badge.Frame.X
                            || sourceEntry.Badge.Frame.X >= sourceEntry.Status.Frame.X)
                            throw new InvalidOperationException($"来源首行元素没有按同一中线垂直对齐：name={sourceEntry.Name.Frame}/{nameMid} badge={sourceEntry.Badge.Frame}/{badgeMid} status={sourceEntry.Status.Frame}/{statusMid} kind={sourceEntry.Kind.Frame}/{kindMid}");
                        if (sourceEntry.Badge.BoxType != NSBoxType.NSBoxCustom || sourceEntry.Badge.FillColor is null
                            || sourceEntry.Kind.Frame.Width >= sourceEntry.Badge.Frame.Width - .01)
                            throw new InvalidOperationException("来源标签缺少包住文字的标签式背景");
                        var stars = sourceEntry.Stars.ToArray();
                        if (stars.Length != 6 || !stars.Select(button => button.ToolTip).SequenceEqual(starTips)
                            || stars.Any(button => (double)button.Frame.Y <= (double)sourceEntry.Name.Frame.Y))
                            throw new InvalidOperationException("来源第二行缺少完整的 0–5 星级控件");
                        var barStars = window.ControlBar.Ratings;
                        if (barStars.Count != 6)
                            throw new InvalidOperationException($"控制栏评分组按钮数错误：{barStars.Count}");
                        double barStep = Math.Abs(mode == LayoutMode.Row
                            ? barStars[1].Frame.X - barStars[0].Frame.X
                            : barStars[1].Frame.Y - barStars[0].Frame.Y);
                        double entryStep = stars[1].Frame.X - stars[0].Frame.X;
                        if (Math.Abs(entryStep - barStep) > .01
                            || Math.Abs((double)(stars[0].Frame.Width - barStars[0].Frame.Width)) > .01)
                            throw new InvalidOperationException($"来源星排与控制栏评分组间距不一致：{entryStep} / {barStep}");
                        double starSpan = stars[^1].Frame.Right - stars[0].Frame.X;
                        double contentInset = sourceEntry.Name.Frame.X;
                        if (Math.Abs(starSpan - (sourceEntry.Frame.Width - contentInset * 2)) > .01)
                            throw new InvalidOperationException("浮窗宽度不是由星级控件整体宽度决定");
                        if (Math.Abs((double)popover.Content.Bounds.Width
                            - (sourceEntry.Frame.Width + DesignTokens.PopupPadding * 2)) > .01)
                            throw new InvalidOperationException("浮窗内容宽不等于条目宽加内边距");
                        if (attempt == 0 && mode == LayoutMode.Row && index == 0)
                        {
                            var hoverStar = stars[1];
                            string normalPath = System.IO.Path.Combine(output, "rating-source-star-normal.png");
                            string hoverPath = System.IO.Path.Combine(output, "rating-source-star-hover.png");
                            NativeSnapshots.SaveView(hoverStar, normalPath);
                            using var hoverEvent = NSEvent.MouseEvent(NSEventType.MouseMoved, CGPoint.Empty, 0,
                                NSProcessInfo.ProcessInfo.SystemUptime,
                                (hoverStar.Window ?? throw new InvalidOperationException("来源星按钮没有原生窗口")).WindowNumber,
                                null!, 1, 1, 0) ?? throw new InvalidOperationException("来源星按钮悬停事件创建失败");
                            hoverStar.MouseEntered(hoverEvent);
                            NativeSnapshots.SaveView(hoverStar, hoverPath);
                            if (File.ReadAllBytes(normalPath).SequenceEqual(File.ReadAllBytes(hoverPath)))
                                throw new InvalidOperationException("来源星按钮悬停没有可见反馈");
                            hoverStar.MouseExited(hoverEvent);
                            observations.Add("RATING-04: source star buttons use the control bar rating hover surface");
                        }
                    }
                    if (attempt == 0 && mode == LayoutMode.Row)
                    {
                        NativeSnapshots.SaveView(popover.Content, System.IO.Path.Combine(output, "rating-sources-popover.png"));
                        NativeSnapshots.SaveView(window.ControlBar, System.IO.Path.Combine(output, "toolbar-rating-conflict.png"));
                    }
                    if (attempt == 1)
                    {
                        var popoverWindow = popover.Content.Window ?? throw new InvalidOperationException("来源浮窗没有原生窗口");
                        using var move = NSEvent.MouseEvent(NSEventType.MouseMoved, new CGPoint(20, 20), 0,
                            NSProcessInfo.ProcessInfo.SystemUptime, popoverWindow.WindowNumber, null!, 1, 1, 0)
                            ?? throw new InvalidOperationException("浮窗指针移动事件创建失败");
                        NSApplication.SharedApplication.PostEvent(move, false);
                        await Task.Delay(100);
                        if (!popover.IsOpen) throw new InvalidOperationException("指针移入后来源浮窗自动关闭");
                    }
                    popover.Close();
                    await WaitAsync(() => !popover.IsOpen, $"来源浮窗关闭 {mode}/{attempt}");
                }
            }
            if (window.Main.CurrentFile?.File.StorageId != currentBefore?.File.StorageId)
                throw new InvalidOperationException("控制栏冲突入口切换了当前主图");

            settings.LayoutMode = LayoutMode.Row;
            await Task.Delay(200);
            clear = window.ControlBar.Ratings[0];
            clear.PerformClick(clear);
            await WaitAsync(() => popover.IsOpen, "写入前来源浮窗展开");
            var targetStar = popover.EntryViews[0].Stars.Single(button => button.ToolTip == "3 星");
            targetStar.PerformClick(targetStar);
            await WaitAsync(() => window.Main.RatingSources.Items.Count == 2
                && window.Main.RatingSources.Items.All(item => item.State.Value == 3), "浮窗来源统一写回 3 星");
            await WaitAsync(() => !popover.IsOpen, "成功且冲突解除后来源浮窗关闭");
            var embedded = await ExifLoader.LoadExifDataAsync(new LocalFileRef(photoPath));
            var written = await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(sidecarPath));
            if (embedded?.Rating != 3 || written?.State != XmpRatingState.Rated || written.Value != 3)
                throw new InvalidOperationException($"浮窗评分没有同时写入内嵌与 sidecar：{embedded?.Rating}/{written?.State}/{written?.Value}");
            if (failures != 0 || window.Main.LastRatingWriteError != null)
                throw new InvalidOperationException($"成功写回被误报为失败：{failures} / {window.Main.LastRatingWriteError}");
            if (window.StatusToast.IsVisible)
                throw new InvalidOperationException($"成功写回弹出了失败提示：{window.StatusToast.Text}");
            if (conflict.HasRatingConflict)
                throw new InvalidOperationException("统一下发成功后仍保持冲突状态");
            await WaitAsync(() => ReferenceEquals(window.ControlBar.Ratings[0].Image, MacSymbols.Get("xmark")), "成功后恢复清零叉");
            RequireColor(window.ControlBar.Ratings[0].ContentTintColor, MacDesignColors.Argb(DesignTokens.ColorStarClear), "成功后清零叉颜色");
            observations.Add("RATING-04: conflict slot is unchanged; source rows share one vertical centre line with separators between entries; vertical source entries survive repeated reopening and pointer movement; bottom control opens upward, right control opens left, and any entry writes the whole group");

            // 卡片入口：非当前卡片的 0 号槽位只打开浮窗，不切主图、不写值。
            window.Main.CurrentFile = clean;
            await Task.Delay(200);
            if (!ReferenceEquals(window.ControlBar.Ratings[0].Image, MacSymbols.Get("xmark")))
                throw new InvalidOperationException("当前照片无冲突时控制栏 0 号未回到清零叉");
            // 重新制造第二张卡片的冲突（写回成功后已收敛，这里用内存真值构造两份不同事实的展示态）。
            var conflictCard = await WaitForCardAsync(window, conflict);
            if (!ReferenceEquals(conflictCard.ClearSlot.Image, MacSymbols.Get("xmark")))
                throw new InvalidOperationException("无冲突的胶片栏卡片仍显示警示三角");
            conflict.ApplyRatingSources([
                new RatingSourceState(XmpRatingSource.Embedded, conflict.File, new XmpRatingSnapshot(XmpRatingState.Rated, 2, XmpRatingSource.Embedded)),
                new RatingSourceState(XmpRatingSource.Sidecar, new LocalFileRef(sidecarPath), new XmpRatingSnapshot(XmpRatingState.Rated, 4, XmpRatingSource.Sidecar)),
            ]);
            await WaitAsync(() => ReferenceEquals(conflictCard.ClearSlot.Image, MacSymbols.Get(MacSymbols.RatingConflict)), "胶片栏冲突三角");
            RequireColor(conflictCard.ClearSlot.ContentTintColor, MacDesignColors.Argb(DesignTokens.ColorStarConflict), "胶片栏冲突三角");
            var valuesBefore = conflict.RatingSources.Select(source => source.Value).ToArray();
            NativeSnapshots.SaveView(conflictCard.View, System.IO.Path.Combine(output, "filmstrip-rating-conflict.png"));
            NSView? cardPreviousContent = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                conflictCard = await WaitForCardAsync(window, conflict);
                var anchorScreen = window.Window.ConvertRectToScreen(conflictCard.ClearSlot.ConvertRectToView(conflictCard.ClearSlot.Bounds, null));
                conflictCard.ClearSlot.PerformClick(conflictCard.ClearSlot);
                await WaitAsync(() => popover.IsOpen && ReferenceEquals(window.Main.RatingSources.Target, conflict), "卡片冲突入口");
                if (cardPreviousContent != null && ReferenceEquals(cardPreviousContent, popover.Content))
                    throw new InvalidOperationException("卡片重开复用了已释放的内容视图");
                cardPreviousContent = popover.Content;
                // 横排卡片入口默认向下；底部卡片在屏幕下方放不下时向上（RATING-04）。
                var expectedEdge = ExpectedCardEdge(window.Window, anchorScreen, (double)popover.Content.Frame.Height);
                if (popover.LastPreferredEdge != expectedEdge)
                    throw new InvalidOperationException($"横排卡片浮窗展开方向错误：{popover.LastPreferredEdge}，预期 {expectedEdge}");
                var popoverFrame = (popover.Content.Window ?? throw new InvalidOperationException("卡片来源浮窗没有原生窗口")).Frame;
                bool opensUp = expectedEdge == NSRectEdge.MinYEdge;
                bool above = (double)(popoverFrame.Y + popoverFrame.Height / 2) > (double)(anchorScreen.Y + anchorScreen.Height / 2);
                if (above != opensUp)
                    throw new InvalidOperationException($"卡片浮窗实际位置与展开方向不符：edge={expectedEdge}, anchor={anchorScreen}, popover={popoverFrame}");
                var stableFrames = popover.EntryViews.Select(entry => entry.Frame).ToArray();
                await Task.Delay(1300);
                if (!popover.IsOpen || !window.Main.RatingSources.IsOpen || !popover.OwnsWindow(window.Window))
                    throw new InvalidOperationException($"卡片浮窗停留一秒后自动关闭或丢失稳定宿主：{attempt}");
                if (!stableFrames.SequenceEqual(popover.EntryViews.Select(entry => entry.Frame)))
                    throw new InvalidOperationException($"卡片浮窗停留后内容错位：{attempt}");
                if (attempt == 0)
                    NativeSnapshots.SaveView(popover.Content, System.IO.Path.Combine(output, "rating-sources-popover-card.png"));
                if (!ReferenceEquals(window.Main.CurrentFile, clean))
                    throw new InvalidOperationException("卡片冲突入口切换了当前主图");
                if (!conflict.RatingSources.Select(source => source.Value).SequenceEqual(valuesBefore))
                    throw new InvalidOperationException("冲突三角本身写入了评分");
                popover.Close();
                await WaitAsync(() => !window.Main.RatingSources.IsOpen, "关闭浮窗同步共享状态");
            }
            observations.Add("RATING-04: a non-current filmstrip card opens downward when the screen has room below (upward when the entry sits at the bottom) without selecting or writing; three reopen cycles each remain stable for 1.3s with fresh native content and no stale anchor bounds access");
        }
        finally
        {
            window.Main.RatingWriteFailed -= OnFailed;
            settings.ShowRating = entryShowRating;
            window.Main.IsThumbnailViewVisible = entryThumbnailVisible;
            try { await window.Main.FolderVM.OpenImageAsync(new LocalFileRef(entryPath)); }
            catch (Exception exception) { Console.Error.WriteLine("[NativeToolbar] 评分冲突回归收尾还原失败：" + exception.Message); }
            await Task.Delay(150);
        }
    }

    /// <summary>
    /// RATING-04：用自有副本核对来源浮窗与共享来源状态的实时联动——非当前卡片在外部整批刷新
    /// （含同名组替换）后条目立即更新；写入失败或写入成功但冲突仍未解除时保留并按磁盘真值刷新；
    /// Esc 与切图关闭；首卡默认向下、底部卡片向上；锚点随栏位隐藏、列表重载、布局变化或滚动失效时关闭。
    /// </summary>
    private static async Task VerifyRatingSourcesLiveAsync(MacMainWindow window, string output, List<string> observations)
    {
        var settings = window.Main.Settings;
        var filter = window.Main.FileVM.FilterBar;
        var popover = window.RatingSourcesPopover;
        var entry = window.Main.CurrentFile ?? throw new InvalidOperationException("来源实时回归缺少进入时的当前照片");
        string entryPath = entry.File.RequireLocalPath();
        var entryId = entry.File.StorageId;
        string entryFilter = filter.SelectedRatingFilter;
        var entrySort = filter.SortOrder;
        var entryLayout = settings.LayoutMode;
        bool entryThumbnailVisible = window.Main.IsThumbnailViewVisible;
        bool entryShowRating = settings.ShowRating;
        var entryFrame = window.Window.Frame;
        var pngFormat = settings.FileFormats.FirstOrDefault(item => item.Id == "png");
        bool entryPngEnabled = pngFormat?.IsEnabled ?? true;
        string root = System.IO.Path.GetFullPath("release/native-p2-acceptance/rating-sources-live-fixtures");
        string livePath = System.IO.Path.Combine(root, "live.jpg");
        string otherPath = System.IO.Path.Combine(root, "other.jpg");
        string failPath = System.IO.Path.Combine(root, "fail.jpg");
        string failPngPath = System.IO.Path.Combine(root, "fail.png");
        string liveSidecar = System.IO.Path.Combine(root, "live.xmp");
        string failSidecar = System.IO.Path.Combine(root, "fail.xmp");
        int failures = 0;
        void OnFailed(ImageFile failed, int rating) => failures++;
        try
        {
            settings.ShowRating = true;
            if (pngFormat != null) pngFormat.IsEnabled = true;
            window.Main.IsThumbnailViewVisible = true;
            settings.LayoutMode = LayoutMode.Row;
            filter.SelectedRatingFilter = "All";
            Directory.CreateDirectory(root);
            File.Copy(System.IO.Path.GetFullPath("release/native-p2-toolbar-fixtures/toolbar-01.jpg"), livePath, true);
            File.Copy(System.IO.Path.GetFullPath("release/native-p2-toolbar-fixtures/toolbar-02.jpg"), otherPath, true);
            File.Copy(System.IO.Path.GetFullPath("release/native-p2-toolbar-fixtures/toolbar-01.jpg"), failPath, true);
            File.Delete(liveSidecar);
            File.Delete(failSidecar);
            File.Delete(failPngPath);
            window.Main.RatingWriteFailed += OnFailed;
            await window.Main.FolderVM.OpenFolderAsync(new LocalFolderRef(root));
            await WaitAsync(() => window.Main.CurrentFile != null && window.Main.FolderVM.AllFiles.Count == 3, "来源实时夹具载入");
            var live = window.Main.FolderVM.AllFiles.Single(file => file.Name == "live.jpg");
            var other = window.Main.FolderVM.AllFiles.Single(file => file.Name == "other.jpg");
            var fail = window.Main.FolderVM.AllFiles.Single(file => file.Name == "fail.jpg");
            await window.Main.SetRatingAsync(live, 2);
            await window.Main.SetRatingAsync(fail, 3);
            await WaitAsync(() => live.Rating == 2 && fail.Rating == 3, "来源实时夹具初始内嵌评级");
            // 真实 sidecar 与不可写的 PNG 成员：前者形成冲突，后者让整组在预检阶段失败。
            await File.WriteAllTextAsync(liveSidecar, SidecarPacket(1));
            await File.WriteAllTextAsync(failSidecar, SidecarPacket(1));
            using (var bitmap = new SKBitmap(16, 16))
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("PNG 夹具生成失败"))
                await File.WriteAllBytesAsync(failPngPath, data.ToArray());
            await window.Main.FolderVM.OpenFolderAsync(new LocalFolderRef(root));
            await WaitAsync(() => window.Main.FolderVM.AllFiles.Count == 4, "来源实时夹具补齐 PNG 成员");
            live = window.Main.FolderVM.AllFiles.Single(file => file.Name == "live.jpg");
            other = window.Main.FolderVM.AllFiles.Single(file => file.Name == "other.jpg");
            fail = window.Main.FolderVM.AllFiles.Single(file => file.Name == "fail.jpg");
            if (!live.HasRatingConflict || !fail.HasRatingConflict)
                throw new InvalidOperationException($"来源实时夹具没有形成冲突：live={live.HasRatingConflict}, fail={fail.HasRatingConflict}");

            // 1) 非当前卡片的浮窗：外部整批刷新 + 组替换后条目实时跟随，不依赖再次 Show 或写评分。
            window.Main.CurrentFile = other;
            await Task.Delay(150);
            var liveGroupBefore = live.Group ?? throw new InvalidOperationException("live.jpg 未参与同名分组");
            var liveCard = await WaitForCardAsync(window, live);
            liveCard.ClearSlot.PerformClick(liveCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen && ReferenceEquals(window.Main.RatingSources.Target, live), "非当前卡片打开来源浮窗");
            await File.WriteAllTextAsync(liveSidecar, SidecarPacket(4));
            await window.Main.FolderVM.OpenFolderAsync(new LocalFolderRef(root));
            await WaitAsync(() => window.Main.RatingSources.Items.Any(item =>
                item.State.Source == XmpRatingSource.Sidecar && item.State.Value == 4), "外部整批刷新后来源条目实时跟随");
            if (!popover.IsOpen || !ReferenceEquals(window.Main.RatingSources.Target, live))
                throw new InvalidOperationException("外部整批刷新误关了来源浮窗或切换了目标");
            live = window.Main.FolderVM.AllFiles.Single(file => file.Name == "live.jpg");
            if (ReferenceEquals(liveGroupBefore, live.Group))
                throw new InvalidOperationException("目录重开没有替换同名组，实时订阅未被验证");
            await File.WriteAllTextAsync(liveSidecar, SidecarPacket(5));
            await live.RefreshMetadataAsync();
            await WaitAsync(() => window.Main.RatingSources.Items.Any(item =>
                item.State.Source == XmpRatingSource.Sidecar && item.State.Value == 5), "组替换后仍订阅目标来源");
            if (!popover.EntryViews.Select(entryView => entryView.Status.StringValue)
                .SequenceEqual(window.Main.RatingSources.Items.Select(item => item.Status)))
                throw new InvalidOperationException("来源条目没有随共享 Items 一起刷新");
            observations.Add("RATING-04: a non-current source popover follows an external full refresh and a replaced photo group without another Show/SetRating");

            // 2) 写入失败：按磁盘真值刷新后保留，并给出 STATE-03。
            popover.Close();
            await WaitAsync(() => !window.Main.RatingSources.IsOpen, "失败用例前收起来源浮窗");
            fail = window.Main.FolderVM.AllFiles.Single(file => file.Name == "fail.jpg");
            var failGroup = fail.Group ?? throw new InvalidOperationException("fail.jpg 未参与同名分组");
            if (!fail.HasRatingConflict || failGroup.Files.Count != 2)
                throw new InvalidOperationException($"失败夹具组不完整：files={failGroup.Files.Count}, conflict={fail.HasRatingConflict}");
            var failCard = await WaitForCardAsync(window, fail);
            failCard.ClearSlot.PerformClick(failCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen && ReferenceEquals(window.Main.RatingSources.Target, fail), "失败用例打开来源浮窗");
            var failItemsBefore = window.Main.RatingSources.Items
                .Select(item => (item.Name, item.Status, item.State.State)).ToArray();
            if (failItemsBefore.Single(item => item.Name == "fail.jpg").Status != "3 星"
                || failItemsBefore.Single(item => item.Name == "fail.png").Status != "未设置"
                || failItemsBefore.Single(item => item.Name == "fail.xmp").Status != "1 星")
                throw new InvalidOperationException($"来源状态没有区分已评分与未设置：{string.Join(";", failItemsBefore)}");
            int failuresBefore = failures;
            var failStar = popover.EntryViews[0].Stars.Single(button => button.ToolTip == "4 星");
            failStar.PerformClick(failStar);
            await WaitAsync(() => failures > failuresBefore, "只读 PNG 成员让整组写回失败");
            await Task.Delay(200);
            if (!popover.IsOpen || !ReferenceEquals(window.Main.RatingSources.Target, fail))
                throw new InvalidOperationException("写入失败后来源浮窗被关闭");
            if (!window.StatusToast.IsVisible)
                throw new InvalidOperationException("写入失败没有 STATE-03 提示条");
            var failItemsAfter = window.Main.RatingSources.Items
                .Select(item => (item.Name, item.Status, item.State.State)).ToArray();
            if (!failItemsAfter.SequenceEqual(failItemsBefore))
                throw new InvalidOperationException($"写入失败后的来源刷新改写了磁盘真值：{string.Join(";", failItemsAfter)}");
            if (window.Main.FolderVM.AllFiles.Single(file => file.Name == "fail.jpg").Rating != 3)
                throw new InvalidOperationException("预检失败的整组写回仍改写了内嵌来源");
            var failedSidecar = await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(failSidecar));
            if (failedSidecar.State != XmpRatingState.Rated || failedSidecar.Value != 1)
                throw new InvalidOperationException($"整组写回失败后 sidecar 被改写：{failedSidecar.State}/{failedSidecar.Value}");
            if (popover.EntryViews.Count != failItemsAfter.Length)
                throw new InvalidOperationException($"失败后来源条目数与 Items 不一致：{popover.EntryViews.Count}/{failItemsAfter.Length}");
            // 组内伴侣文件的来源变化同样实时进入浮窗：订阅的是组成员，不只是目标文件。
            var failCompanion = window.Main.FolderVM.AllFiles.Single(file => file.Name == "fail.png");
            failCompanion.ApplyRatingSources([
                new RatingSourceState(
                    XmpRatingSource.Embedded,
                    failCompanion.File,
                    new XmpRatingSnapshot(XmpRatingState.Rated, 5, XmpRatingSource.Embedded))]);
            await WaitAsync(() => window.Main.RatingSources.Items.Any(item =>
                item.Name == "fail.png" && item.Status == "5 星"), "组内伴侣来源变化实时更新");
            observations.Add("RATING-04: a failed whole-group write keeps the popover open with refreshed disk truth and a STATE-03 notice");

            // 3) 写入成功但刷新后冲突仍未解除：保留浮窗并显示刷新后的来源。
            popover.Close();
            await WaitAsync(() => !window.Main.RatingSources.IsOpen, "成功冲突用例前收起来源浮窗");
            live = window.Main.FolderVM.AllFiles.Single(file => file.Name == "live.jpg");
            window.Main.CurrentFile = other;
            await Task.Delay(150);
            var successCard = await WaitForCardAsync(window, live);
            successCard.ClearSlot.PerformClick(successCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen && ReferenceEquals(window.Main.RatingSources.Target, live), "成功冲突用例打开来源浮窗");
            int successFailures = failures;
            var successTarget = live;
            // 外部来源在写回刷新时出现：用共享 ApplyRatingSources 注入第三个来源，写入因此成功但组仍有冲突。
            void InjectLateSource(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(ImageFile.RatingSources)) return;
                if (successTarget.RatingSources.Any(source => source.File.Name == "external.xmp")) return;
                var sources = successTarget.RatingSources.ToList();
                sources.Add(new RatingSourceState(
                    XmpRatingSource.Sidecar,
                    new LocalFileRef(System.IO.Path.Combine(root, "external.xmp")),
                    new XmpRatingSnapshot(XmpRatingState.Rated, 5, XmpRatingSource.Sidecar)));
                successTarget.ApplyRatingSources(sources);
            }
            successTarget.PropertyChanged += InjectLateSource;
            try
            {
                var successStar = popover.EntryViews[0].Stars.Single(button => button.ToolTip == "3 星");
                successStar.PerformClick(successStar);
                await WaitAsync(() => window.Main.RatingSources.Items.Any(item =>
                    item.Name == "external.xmp" && item.Status == "5 星"), "写入成功但刷新后冲突仍在");
                await Task.Delay(200);
                if (!popover.IsOpen || !ReferenceEquals(window.Main.RatingSources.Target, successTarget))
                    throw new InvalidOperationException("写入成功但冲突仍未解除时关闭了来源浮窗");
                if (failures != successFailures || window.Main.LastRatingWriteError != null)
                    throw new InvalidOperationException("成功写入被误报为失败");
                var liveExif = await ExifLoader.LoadExifDataAsync(new LocalFileRef(livePath));
                if (liveExif?.Rating != 3)
                    throw new InvalidOperationException($"成功路径没有写回内嵌来源：{liveExif?.Rating}");
                var liveSidecarState = await RatingSourceReader.ReadSidecarAsync(new LocalFileRef(liveSidecar));
                if (liveSidecarState.State != XmpRatingState.Rated || liveSidecarState.Value != 3)
                    throw new InvalidOperationException($"成功路径没有写回 sidecar：{liveSidecarState.State}/{liveSidecarState.Value}");
                if (!popover.EntryViews.Select(entryView => entryView.Status.StringValue)
                    .SequenceEqual(window.Main.RatingSources.Items.Select(item => item.Status)))
                    throw new InvalidOperationException("写入成功后浮窗条目没有显示刷新后的来源状态");
            }
            finally
            {
                successTarget.PropertyChanged -= InjectLateSource;
            }
            observations.Add("RATING-04: a successful write keeps the popover open while the refreshed group still reports a conflict, showing the refreshed sources");

            // 4) Esc 先关浮窗；切图关闭。
            popover.Close();
            await WaitAsync(() => !window.Main.RatingSources.IsOpen, "Esc 用例前收起来源浮窗");
            window.Window.MakeKeyAndOrderFront(null);
            window.Window.MakeFirstResponder(window.Photo);
            live = window.Main.FolderVM.AllFiles.Single(file => file.Name == "live.jpg");
            var escCard = await WaitForCardAsync(window, live);
            escCard.ClearSlot.PerformClick(escCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen, "Esc 用例打开来源浮窗");
            await Task.Delay(150);
            if (!popover.IsOpen) throw new InvalidOperationException("Esc 用例浮窗在按键前已关闭");
            var popoverWindow = popover.Content.Window ?? throw new InvalidOperationException("来源浮窗没有原生窗口");
            PostKey(NSEventType.KeyDown, popoverWindow.WindowNumber, 53);
            try
            {
                await WaitAsync(() => !popover.IsOpen && !window.Main.RatingSources.IsOpen, "Esc 关闭来源浮窗", 2);
            }
            catch (TimeoutException)
            {
                PostKey(NSEventType.KeyDown, window.Window.WindowNumber, 53);
                await WaitAsync(() => !popover.IsOpen && !window.Main.RatingSources.IsOpen, "主窗口 Esc 关闭来源浮窗", 2);
            }
            var switchCard = await WaitForCardAsync(window, live);
            switchCard.ClearSlot.PerformClick(switchCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen, "切图用例打开来源浮窗");
            window.Main.CurrentFile = window.Main.FolderVM.AllFiles.First(file => !ReferenceEquals(file, window.Main.CurrentFile));
            await WaitAsync(() => !popover.IsOpen && !window.Main.RatingSources.IsOpen, "切图关闭来源浮窗");
            window.Main.CurrentFile = other;
            await Task.Delay(150);
            observations.Add("RATING-04: Esc closes the source popover first and switching photos closes it as well");

            // 5) 首卡默认向下；窗口贴屏幕底部时底部卡片向上。
            // 用单来源目标保持浮窗较低，任何支持 560×480 窗口的屏幕都能核对两侧空间。
            var screen = window.Window.Screen ?? NSScreen.MainScreen ?? throw new InvalidOperationException("没有可用屏幕");
            var visible = screen.VisibleFrame;
            double width = Math.Min(900, (double)visible.Width - 80);
            double height = Math.Min(480, (double)visible.Height - 80);
            if (height < 480 || width < 560)
                throw new InvalidOperationException($"屏幕可用区域不足以验证卡片展开方向：{visible}");
            settings.LayoutMode = LayoutMode.Row;
            window.Window.SetFrame(new CGRect(
                (double)visible.X + 40,
                (double)visible.Y + (double)visible.Height - height - 20,
                width,
                height), true);
            await Task.Delay(350);
            other = window.Main.FolderVM.AllFiles.Single(file => file.Name == "other.jpg");
            var topCard = await WaitForCardAsync(window, other);
            var topAnchor = window.Window.ConvertRectToScreen(topCard.ClearSlot.ConvertRectToView(topCard.ClearSlot.Bounds, null));
            window.ShowRatingSources(other, topCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen, "顶部卡片打开来源浮窗");
            double contentHeight = (double)popover.Content.Frame.Height;
            double below = (double)(topAnchor.Y - visible.Y);
            if (below < contentHeight + 12)
                throw new InvalidOperationException($"顶部窗口下方仍无展开空间，无法核对默认向下：{below}");
            if (popover.LastPreferredEdge != NSRectEdge.MaxYEdge)
                throw new InvalidOperationException($"首卡没有默认向下：{popover.LastPreferredEdge}");
            var topFrame = (popover.Content.Window ?? throw new InvalidOperationException("来源浮窗没有原生窗口")).Frame;
            if ((double)(topFrame.Y + topFrame.Height / 2) >= (double)(topAnchor.Y + topAnchor.Height / 2))
                throw new InvalidOperationException($"首卡浮窗实际不在入口下方：anchor={topAnchor}, popover={topFrame}");
            popover.Close();
            await WaitAsync(() => !popover.IsOpen, "收起首卡浮窗");

            settings.LayoutMode = LayoutMode.Col;
            window.Window.SetFrame(new CGRect((double)visible.X + 40, (double)visible.Y, width, height), true);
            await Task.Delay(350);
            other = window.Main.FolderVM.AllFiles.Single(file => file.Name == "other.jpg");
            window.Main.CurrentFile = other;
            window.Main.FileVM.ThumbnailList.ScrollToCurrent();
            await Task.Delay(500);
            var bottomCard = await WaitForCardAsync(window, other);
            var bottomAnchor = window.Window.ConvertRectToScreen(bottomCard.ClearSlot.ConvertRectToView(bottomCard.ClearSlot.Bounds, null));
            window.ShowRatingSources(other, bottomCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen, "底部卡片打开来源浮窗");
            contentHeight = (double)popover.Content.Frame.Height;
            below = (double)(bottomAnchor.Y - visible.Y);
            if (below >= contentHeight + 12)
                throw new InvalidOperationException($"底部窗口仍有展开空间，无法核对向上：{below}");
            if (popover.LastPreferredEdge != NSRectEdge.MinYEdge)
                throw new InvalidOperationException($"底部卡片没有向上展开：{popover.LastPreferredEdge}");
            var bottomFrame = (popover.Content.Window ?? throw new InvalidOperationException("来源浮窗没有原生窗口")).Frame;
            if ((double)(bottomFrame.Y + bottomFrame.Height / 2) <= (double)(bottomAnchor.Y + bottomAnchor.Height / 2))
                throw new InvalidOperationException($"底部卡片浮窗实际不在入口上方：anchor={bottomAnchor}, popover={bottomFrame}");
            NativeSnapshots.SaveView(popover.Content, System.IO.Path.Combine(output, "rating-sources-popover-upward.png"));
            popover.Close();
            await WaitAsync(() => !popover.IsOpen, "收起底部卡片浮窗");
            observations.Add("RATING-04: the first card opens downward with room below, and a card at the bottom of the screen opens upward using the same content size");

            // 6) 锚点失效：隐藏栏位、列表重载、布局变化、滚动移走卡片都关闭浮窗。
            window.Window.SetFrame(entryFrame, true);
            settings.LayoutMode = LayoutMode.Row;
            await Task.Delay(350);
            live = window.Main.FolderVM.AllFiles.Single(file => file.Name == "live.jpg");
            var hiddenCard = await WaitForCardAsync(window, live);
            hiddenCard.ClearSlot.PerformClick(hiddenCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen, "隐藏栏位用例打开来源浮窗");
            window.Main.IsThumbnailViewVisible = false;
            await WaitAsync(() => !popover.IsOpen && !window.Main.RatingSources.IsOpen, "隐藏胶片栏关闭来源浮窗");
            window.Main.IsThumbnailViewVisible = true;
            await Task.Delay(500);

            live = window.Main.FolderVM.AllFiles.Single(file => file.Name == "live.jpg");
            var reloadCard = await WaitForCardAsync(window, live);
            reloadCard.ClearSlot.PerformClick(reloadCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen, "列表重载用例打开来源浮窗");
            filter.SortOrder = filter.SortOrders
                .Select(option => (SortOrder)option.Value)
                .First(order => order != filter.SortOrder);
            await WaitAsync(() => !popover.IsOpen, "列表重载关闭来源浮窗");
            filter.SortOrder = entrySort;
            await Task.Delay(300);

            live = window.Main.FolderVM.AllFiles.Single(file => file.Name == "live.jpg");
            var resizeCard = await WaitForCardAsync(window, live);
            resizeCard.ClearSlot.PerformClick(resizeCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen, "布局变化用例打开来源浮窗");
            var contentSize = window.Window.ContentView?.Bounds.Size ?? new CGSize(1200, 850);
            window.Window.SetContentSize(new CGSize(
                Math.Max(600, (double)contentSize.Width - 120), Math.Max(500, (double)contentSize.Height - 100)));
            await WaitAsync(() => !popover.IsOpen, "布局变化关闭来源浮窗");
            window.Window.SetFrame(entryFrame, true);
            await Task.Delay(300);

            settings.LayoutMode = LayoutMode.Col;
            window.Window.SetContentSize(new CGSize(560, 480));
            await Task.Delay(350);
            window.Main.CurrentFile = other;
            await Task.Delay(150);
            window.Main.FileVM.ThumbnailList.ScrollToCurrent();
            await Task.Delay(400);
            var current = window.Main.CurrentFile ?? throw new InvalidOperationException("滚动用例缺少当前照片");
            var currentCard = await WaitForCardAsync(window, current);
            window.ShowRatingSources(current, currentCard.ClearSlot);
            await WaitAsync(() => popover.IsOpen, "滚动用例打开来源浮窗");
            await Task.Delay(200);
            if (!popover.IsOpen) throw new InvalidOperationException("滚动用例浮窗在滚动前就已关闭");
            var clip = window.FilmstripScroll.ContentView;
            // 列布局小窗口里胶片栏可纵向滚动；滚到与当前位置不同的偏移即代表锚点卡片移走。
            double targetOffset = (double)clip.Bounds.Y > 40 ? 0 : 120;
            clip.ScrollToPoint(new CGPoint((double)clip.Bounds.X, targetOffset));
            window.FilmstripScroll.ReflectScrolledClipView(clip);
            await WaitAsync(() => !popover.IsOpen && !window.Main.RatingSources.IsOpen, "滚动移走锚点后关闭来源浮窗");
            observations.Add("RATING-04: hiding the filmstrip, reloading the list, resizing the window or scrolling the anchor away closes the source popover instead of leaving it over a moved card");
            observations.Add($"RATING-04: closed popover presentations stay retained for the window lifetime ({popover.RetiredPresentationCount} after this regression) because AppKit reads anchor bounds after close; no early Dispose or reuse");
        }
        finally
        {
            window.Main.RatingWriteFailed -= OnFailed;
            try { popover.Close(); }
            catch (Exception exception) { Console.Error.WriteLine("[NativeToolbar] 来源实时回归收尾关闭浮窗失败：" + exception.Message); }
            settings.ShowRating = entryShowRating;
            if (pngFormat != null) pngFormat.IsEnabled = entryPngEnabled;
            window.Main.IsThumbnailViewVisible = entryThumbnailVisible;
            settings.LayoutMode = entryLayout;
            filter.SelectedRatingFilter = entryFilter;
            filter.SortOrder = entrySort;
            window.Window.SetFrame(entryFrame, true);
            try
            {
                if (window.Main.CurrentFile?.File.StorageId != entryId)
                    await window.Main.FolderVM.OpenImageAsync(new LocalFileRef(entryPath));
            }
            catch (Exception exception) { Console.Error.WriteLine("[NativeToolbar] 来源实时回归收尾还原失败：" + exception.Message); }
            await Task.Delay(200);
        }
    }

    /// <summary>写一份结构完整的最小 sidecar XMP 包，只含单个 <c>xap:Rating</c>。</summary>
    private static string SidecarPacket(int rating)
        => "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + $"<rdf:Description xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"{rating}\"/></rdf:RDF></x:xmpmeta>";

    /// <summary>
    /// 按屏幕可见区域独立推算卡片入口应有的展开方向：下方放得下就向下，否则上方放得下就向上，
    /// 两侧都不够时保留规范默认向下。与实现同口径但只用屏幕坐标推导，避免测试照抄实现变量。
    /// </summary>
    private static NSRectEdge ExpectedCardEdge(NSWindow window, CGRect anchorScreen, double contentHeight)
    {
        var screen = window.Screen ?? NSScreen.MainScreen;
        if (screen == null) return NSRectEdge.MaxYEdge;
        var visible = screen.VisibleFrame;
        double below = (double)(anchorScreen.Y - visible.Y);
        double above = (double)(visible.Y + visible.Height - (anchorScreen.Y + anchorScreen.Height));
        double required = contentHeight + 12;
        return below < required && above >= required ? NSRectEdge.MinYEdge : NSRectEdge.MaxYEdge;
    }

    /// <summary>把一次真实按键事件投进应用事件队列（本地监视器与产品路径一致）。</summary>
    private static void PostKey(NSEventType type, nint windowNumber, ushort keyCode)
    {
        var key = NSEvent.KeyEvent(type, CGPoint.Empty, 0, NSProcessInfo.ProcessInfo.SystemUptime,
            windowNumber, null!, "\u001b", "\u001b", false, keyCode)
            ?? throw new InvalidOperationException("按键事件创建失败");
        NSApplication.SharedApplication.PostEvent(key, false);
    }

    /// <summary>轮询等待真实状态并设定有限超时，失败不输出虚假的通过报告。</summary>
    private static async Task WaitAsync(Func<bool> condition, string name, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(name);
            await Task.Delay(25);
        }
    }

    /// <summary>等待胶片栏中目标照片的卡片被真实创建并绑定到该业务身份。</summary>
    private static async Task<MacThumbnailItem> WaitForCardAsync(MacMainWindow window, ImageFile file)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            int index = window.Main.FileVM.ThumbnailList.FilteredFiles.IndexOf(file);
            if (index >= 0)
            {
                window.Filmstrip.LayoutSubtreeIfNeeded();
                using (var path = NSIndexPath.FromItemSection(index, 0))
                    if (window.Filmstrip.GetItem(path) is MacThumbnailItem card && ReferenceEquals(card.BoundFile, file)) return card;
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("胶片栏卡片未创建");
            await Task.Delay(25);
        }
    }

    /// <summary>递归枚举视图树的全部后代，供查找卡片内部的原生按钮。</summary>
    private static IEnumerable<NSView> Descendants(NSView view)
        => view.Subviews.SelectMany(subview => new[] { subview }.Concat(Descendants(subview)));

    /// <summary>判断照片是否位于本套件自有的可写评分副本目录。</summary>
    private static bool IsOwnedFixtureFile(ImageFile? file)
        => file != null && System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file.File.RequireLocalPath())) == "native-p2-toolbar-fixtures";
}
