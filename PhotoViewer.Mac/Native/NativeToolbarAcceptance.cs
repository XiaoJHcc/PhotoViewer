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
            await Task.Delay(150);
            if (window.ControlBar.Ratings.Any(button => button.Enabled) || !window.ControlBar.Commands["Open"].Enabled ||
                window.ControlBar.Commands.Where(pair => pair.Key != "Open").Any(pair => pair.Value.Enabled))
                throw new InvalidOperationException("空照片状态的工具栏启用状态错误");
            observations.Add("rating group hides without blank space; numbered rating commands remain distinguishable; empty state disables photo actions");

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

    /// <summary>TOOLBAR-03：点亮星 color.star.on、空心星 color.star.off、清零叉中性 color.star.clear。</summary>
    private static void VerifyRatingGroupColors(MacMainWindow window)
    {
        var bar = window.ControlBar;
        if (bar.Ratings.Count != 6) throw new InvalidOperationException("评分组按钮数量不是 6");
        int rating = window.Main.CurrentFile?.Rating ?? 0;
        RequireColor(bar.Ratings[0].ContentTintColor, MacDesignColors.Argb(DesignTokens.ColorStarClear), "评分组清零叉");
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
