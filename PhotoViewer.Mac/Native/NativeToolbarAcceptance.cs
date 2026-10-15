using AppKit;
using CoreGraphics;
using PhotoViewer.Core;
using PhotoViewer.Core.Input;
using PhotoViewer.Mac.Core;
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
            var expected = settings.Hotkeys.Where(hotkey => hotkey.IsDisplay && MacSymbols.ForCommand(hotkey.Command) != null).Select(hotkey => hotkey.Command);
            if (!window.ControlBar.Commands.Keys.SequenceEqual(expected)) throw new InvalidOperationException("工具栏未保留设置排序");
            string tooltip = window.ControlBar.Commands["Next"].ToolTip ?? "";
            if (!tooltip.Contains(MacInput.FormatGesture(next.PrimaryHotkey, settings), StringComparison.Ordinal) ||
                !tooltip.Contains(MacInput.FormatGesture(next.SecondaryHotkey, settings), StringComparison.Ordinal) || tooltip.Contains("未设置", StringComparison.Ordinal))
                throw new InvalidOperationException("工具栏提示未正确显示苹果主次快捷键");
            observations.Add("display/order bindings and both Apple gesture tooltips update without changing photos");

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
            await Task.Delay(150);
            if (window.ControlBar.Commands["SetRating3"].Title != "3") throw new InvalidOperationException("评分快捷命令丢失星数语义");
            settings.Hotkeys.Single(hotkey => hotkey.Command == "Open").IsDisplay = true;
            window.Main.CurrentFile = null;
            await Task.Delay(150);
            if (window.ControlBar.Ratings.Any(button => button.Enabled) || !window.ControlBar.Commands["Open"].Enabled ||
                window.ControlBar.Commands.Where(pair => pair.Key != "Open").Any(pair => pair.Value.Enabled))
                throw new InvalidOperationException("空照片状态的工具栏启用状态错误");
            observations.Add("rating group hides without blank space; numbered rating commands remain distinguishable; empty state disables photo actions");

            window.Main.CurrentFile = first;
            await Task.Delay(200);
            if (System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(first.File.PathKey)) == "native-p2-toolbar-fixtures")
            {
                int targetRating = first.Rating == 3 ? 2 : 3;
                var ratingButton = window.ControlBar.Ratings[targetRating];
                ratingButton.PerformClick(ratingButton);
                for (int attempt = 0; attempt < 100 && first.Rating != targetRating; attempt++) await Task.Delay(50);
                var exif = await ExifLoader.LoadExifDataAsync(new MacFileRef(first.File.PathKey));
                if (first.Rating != targetRating || exif?.Rating != targetRating || window.Main.CurrentFile != first)
                    throw new InvalidOperationException("评分按钮写回/主图身份错误");
                observations.Add($"real rating button changes owned fixture to {targetRating} stars and preserves current photo");
            }
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
        foreach (var button in bar.Commands.Values)
            if (button.BezelStyle != NSBezelStyle.SmallSquare)
                throw new InvalidOperationException("工具按钮使用了固定高度边框");
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

    private static void RequireFrame(CGRect actual, CGRect expected, string context)
    {
        if (Math.Abs((double)(actual.X - expected.X)) > .01 || Math.Abs((double)(actual.Y - expected.Y)) > .01 ||
            Math.Abs((double)(actual.Width - expected.Width)) > .01 || Math.Abs((double)(actual.Height - expected.Height)) > .01)
            throw new InvalidOperationException($"{context}：实际 {actual}，预期 {expected}");
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
    }
}
