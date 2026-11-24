using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check faults</c> 的星级弹窗组（验收专用）：非冲突单条目弹窗、点星整组写回、
/// 卡片长按入口，以及紧凑星级的三区（相对步进、拖放绝对选星、取消区与跨区衔接）与抬手提交。全部经真实视图派发触摸，
/// 与产品入口同一条消费路径，不直接调用业务 setter。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>单条目弹窗素材目录 <c>files/pv-check-rating</c>：<c>popup.jpg</c> 与 <c>other.jpg</c> 两张。</summary>
    /// <param name="rating">popup.jpg 的初始星级。</param>
    /// <returns>目录路径。</returns>
    private static string EnsureRatingPopupFolder(int rating)
    {
        string folder = IOPath.Combine(_activity!.FilesDir!.AbsolutePath, "pv-check-rating");
        Directory.CreateDirectory(folder);
        string path = IOPath.Combine(folder, "popup.jpg");
        string other = IOPath.Combine(folder, "other.jpg");
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(other)) File.Delete(other);
        WriteCheckJpeg(path, 1200, 800, rating: rating);
        WriteCheckJpeg(other, 1200, 800, rating: 1);
        return folder;
    }

    /// <summary>打开单条目素材并等列表稳定；当前照片固定为 <c>popup.jpg</c>。</summary>
    /// <param name="rating">popup.jpg 的初始星级。</param>
    /// <returns>当前照片、它的路径，以及 <c>other.jpg</c> 的照片对象与路径。</returns>
    private static async Task<(ImageFile File, string Path, ImageFile Other, string OtherPath)>
        OpenRatingPopupFixtureAsync(int rating)
    {
        var main = _main!;
        string folder = EnsureRatingPopupFolder(rating);
        await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folder));
        await WaitAsync(() => main.FileVM.ThumbnailList.FilteredFiles.Count == 2, "单条目素材上屏", 25000);
        await WaitForListSettledAsync(main);
        var files = main.FileVM.ThumbnailList.FilteredFiles;
        var file = files.First(item => item.Name == "popup.jpg");
        var other = files.First(item => item.Name == "other.jpg");
        if (!ReferenceEquals(main.CurrentFile, file)) main.FileVM.ThumbnailList.SelectImageCommand(file);
        await WaitAsync(() => ReferenceEquals(main.CurrentFile, file), "当前照片固定为 popup.jpg", 15000);
        await Task.Delay(200);
        return (file, IOPath.Combine(folder, "popup.jpg"), other, IOPath.Combine(folder, "other.jpg"));
    }

    /// <summary>等待某张照片的卡片完成绑定与排布。</summary>
    /// <param name="main">共享主 VM。</param>
    /// <param name="file">目标照片。</param>
    /// <returns>卡片。</returns>
    private static async Task<AndroidThumbnailCard> WaitForCardAsync(MainViewModel main, ImageFile file)
    {
        AndroidThumbnailCard? card = null;
        await WaitAsync(() =>
        {
            int index = main.FileVM.ThumbnailList.FilteredFiles.IndexOf(file);
            card = index >= 0 ? _root!.Filmstrip.CardAt(index) : null;
            return card?.ScreenRect is not null;
        }, $"{file.Name} 卡片排布", 15000);
        return card!;
    }

    /// <summary>
    /// RATING-07 / RATING-04：卡片长按打开星级弹窗（不再是旧评分菜单），非冲突时只有一个条目
    /// （文件名/时间和星排），锚点是卡片真实矩形、方向取共享方向表；抬手保持弹窗打开，
    /// 再点弹窗里的 5 星整组写回并收起。
    /// </summary>
    private static async Task CheckRatingPopupCardAsync()
    {
        var main = _main!;
        var root = _root!;
        var popup = root.RatingPopup;
        var (file, _, other, otherPath) = await OpenRatingPopupFixtureAsync(2);
        // 长按的是非当前卡片：弹窗要作用于该卡片，同时不切图（旧卡片评分菜单已删除）。
        var card = await WaitForCardAsync(main, other);
        if (card.ScreenRect is not { } cardRect)
        {
            Check("RATING-07 卡片长按打开星级弹窗", false, "卡片或入口矩形不可用");
            return;
        }
        try
        {
            // 长按判定由 GestureDetector 在主线程按时间推进：先送下按，等待超过长按阈值再抬手。
            long now = SystemClock.UptimeMillis();
            using (var down = MotionEvent.Obtain(now, now, MotionEventActions.Down,
                       cardRect.CenterX(), cardRect.CenterY(), 0))
                card.DispatchTouchEvent(down);
            await WaitAsync(() => popup.IsOpen && main.RatingSources.IsOpen, "卡片长按打开星级弹窗", 5000);
            await Task.Delay(250);
            var expected = RatingControlGeometry.PopupDirection(root.Form, main.IsRowLayout, root.NotchOnLeft,
                RatingPopupAnchor.Card);
            bool opened = popup.IsOpen && main.RatingSources.IsOpen
                && popup.ModeForChecks == RatingPopupMode.Card
                && popup.AnchorKindForChecks == RatingPopupAnchor.Card
                && SameRect(popup.AnchorRectForChecks, cardRect)
                && popup.DirectionForChecks == DirectionText(expected)
                && popup.PitchDpForChecks >= RatingPopupLayout.MinSlot;
            bool notSelected = ReferenceEquals(main.CurrentFile, file) && other.Rating == 1;
            using (var up = MotionEvent.Obtain(now, now + 900, MotionEventActions.Up,
                       cardRect.CenterX(), cardRect.CenterY(), 0))
                card.DispatchTouchEvent(up);
            await Task.Delay(250);
            bool kept = popup.IsOpen && other.Rating == 1 && ReferenceEquals(main.CurrentFile, file);
            Check("RATING-07 卡片长按打开星级弹窗", opened && notSelected && kept,
                $"open={popup.IsOpen} mode={popup.ModeForChecks} anchor={popup.AnchorRectForChecks} card={cardRect} "
                + $"direction={popup.DirectionForChecks}/{DirectionText(expected)} pitch={popup.PitchDpForChecks:0.#} "
                + $"keptAfterTap={kept} current={main.CurrentFile?.Name} other={other.Rating}");

            if (popup.ContentViewForChecks is not { } content)
            {
                Check("RATING-07 星级弹窗在主窗口内", false, "内容视图不可用");
                return;
            }
            var panel = root.PopupLayer.SessionForChecks?.Panel;
            var focused = panel?.FindFocus();
            bool focusInPanel = panel != null && focused != null
                && (ReferenceEquals(panel, focused) || IsDescendantOf(panel, focused));
            bool inWindow = IsDescendantOf(root, content) && root.PopupLayer.IsOpen
                && popup.WidthPxForChecks > 0 && popup.WidthPxForChecks <= root.Width
                && popup.HeightPxForChecks <= root.Height && focusInPanel;
            Check("RATING-07 星级弹窗在主窗口内", inWindow,
                $"open={root.PopupLayer.IsOpen} size={popup.WidthPxForChecks}x{popup.HeightPxForChecks} "
                + $"root={root.Width}x{root.Height} content={content.GetType().Name} focus={focusInPanel}");

            var labels = PopupTextLabels(content).ToArray();
            var expectedTime = other.PhotoDate?.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            Check("RATING-04 拍摄时间固定为月日时分",
                expectedTime != null && labels.SequenceEqual(new[] { other.DisplayName, expectedTime }),
                $"expected={expectedTime} labels=[{string.Join(" / ", labels)}]");
            var target = popup.SlotRectPx(0, 5);
            if (target.Width() <= 0)
            {
                Check("RATING-07 弹窗点星整组写回", false, "弹窗槽位不可用");
                return;
            }
            TapLocal(content, target.CenterX(), target.CenterY());
            await WaitAsync(() => !popup.IsOpen && other.Rating == 5, "弹窗点星写回并收起", 15000);
            bool disk = (await XmpRatingReader.ReadAsync(new AndroidFileRef(otherPath))).ValueOrZero == 5;
            Check("RATING-07 弹窗点星整组写回",
                disk && other.Rating == 5 && !other.HasRatingConflict && !main.RatingSources.IsOpen
                && ReferenceEquals(main.CurrentFile, file) && file.Rating == 2,
                $"disk={disk} other={other.Rating} conflict={other.HasRatingConflict} "
                + $"popup={popup.IsOpen} shared={main.RatingSources.IsOpen} current={main.CurrentFile?.Name} "
                + $"currentRating={file.Rating}");
        }
        finally
        {
            popup.Close();
        }
    }

    /// <summary>
    /// RATING-07：紧凑星级控件起手即打开弹窗，越过点按阈值前不预览、不落盘；
    /// 按住移动到弹窗星排第 4 格后抬手只写一次并收起。入口与落点都用屏幕坐标派发，
    /// 与真实触摸同为「本地=屏幕」的换算前提。
    /// </summary>
    private static async Task CheckRatingPopupCompactAsync()
    {
        var root = _root!;
        var popup = root.RatingPopup;
        var (file, path, _, _) = await OpenRatingPopupFixtureAsync(2);
        // 手机形态用底部/侧列星级槽位，宽形态用控制栏评分组；取当前真正为紧凑态且可见的那一个。
        var candidates = new List<AndroidRatingControl>();
        if (root.Form != ScreenForm.Wide) candidates.Add(root.PhoneRating);
        candidates.Add(root.ControlBar.RatingControl);
        var control = candidates.FirstOrDefault(item => item.IsShown && item.Width > 0
            && item.Form == RatingControlForm.Compact);
        if (control == null)
        {
            Check("RATING-07 紧凑星级起手与拖动提交", false,
                $"form={root.Form} controls=[{string.Join(" / ", candidates.Select(
                    item => $"{item.Form}:shown={item.IsShown}"))}]");
            return;
        }
        var entryRect = ScreenRectOf(control);
        bool touchModeBefore = control.IsInTouchMode;
        try
        {
            long now = SystemClock.UptimeMillis();
            using (var down = MotionEvent.Obtain(now, now, MotionEventActions.Down,
                       entryRect.CenterX(), entryRect.CenterY(), 0))
                control.DispatchTouchEvent(down);
            await WaitAsync(() => popup.IsOpen, "紧凑星级起手打开弹窗", 5000);
            await Task.Delay(200);
            bool touchModeOpen = control.IsInTouchMode;
            bool opened = popup.IsOpen && popup.ModeForChecks == RatingPopupMode.Selector
                && popup.AnchorKindForChecks == RatingPopupAnchor.Control
                && SameRect(popup.AnchorRectForChecks, entryRect);
            var panel = root.PopupLayer.SessionForChecks?.Panel;
            var focused = panel?.FindFocus();
            bool focusInPanel = panel != null && focused != null
                && (ReferenceEquals(panel, focused) || IsDescendantOf(panel, focused));
            bool focusPolicy = touchModeOpen
                ? panel != null && ReferenceEquals(panel, focused)
                : panel != null && focused != null && !ReferenceEquals(panel, focused)
                    && ReferenceEquals(focused, FirstFocusableIn(panel));
            bool noPreviewYet = file.Rating == 2 && popup.CompactPreviewForChecks == null;
            if (popup.ContentViewForChecks is not { } content)
            {
                Check("RATING-07 紧凑星级起手与拖动提交", false, $"open={popup.IsOpen} 内容视图不可用");
                return;
            }
            var zero = popup.SlotRectPx(0, 0);
            var five = popup.SlotRectPx(0, 5);
            bool axisMatches = control.Horizontal
                ? zero.Top == five.Top && zero.Left < five.Left
                : zero.Left == five.Left && zero.Top > five.Top;
            Check("RATING-07 紧凑星级打开纯星级胶囊", axisMatches && !PopupTextLabels(content).Any(),
                $"horizontal={control.Horizontal} zero={zero} five={five}");
            var slot = popup.SlotRectPx(0, 4);
            if (slot.Width() <= 0)
            {
                Check("RATING-07 紧凑星级起手与拖动提交", false, "弹窗星排槽位不可用");
                return;
            }
            var location = new int[2];
            content.GetLocationOnScreen(location);
            float targetX = location[0] + slot.CenterX(), targetY = location[1] + slot.CenterY();
            using (var move = MotionEvent.Obtain(now, now + 60, MotionEventActions.Move, targetX, targetY, 0))
                control.DispatchTouchEvent(move);
            await Task.Delay(120);
            bool previewOnly = file.Rating == 2 && popup.IsOpen && popup.CompactPreviewForChecks == 4;
            bool dropVisual = popup.DropHoveredSlotForChecks == 4 && popup.ScaledSlotForChecks == 4;
            using (var up = MotionEvent.Obtain(now, now + 140, MotionEventActions.Up, targetX, targetY, 0))
                control.DispatchTouchEvent(up);
            await WaitAsync(() => !popup.IsOpen && file.Rating == 4, "紧凑星级抬手提交并收起", 15000);
            await WaitAsync(() => root.PopupLayer.Visibility == ViewStates.Gone, "星级弹窗淡出完成", 4000);
            bool disk = (await XmpRatingReader.ReadAsync(new AndroidFileRef(path))).ValueOrZero == 4;
            bool touchModeClosed = control.IsInTouchMode;
            bool focusRestored = touchModeClosed ? !control.IsFocused : control.IsFocused;
            bool focusRingCorrect = root.GlassControlLayer.TryGetControlForChecks(control, out var glass)
                && glass.FocusRingVisible == !touchModeClosed;
            bool defaultHighlightDisabled = !OperatingSystem.IsAndroidVersionAtLeast(26) || !control.DefaultFocusHighlightEnabled;
            bool touchModePreserved = touchModeBefore == touchModeOpen && touchModeOpen == touchModeClosed;
            Check("RATING-07 紧凑星级起手与拖动提交（关闭后无系统蓝框）",
                opened && noPreviewYet && previewOnly && dropVisual && disk && file.Rating == 4
                    && focusInPanel && focusPolicy && focusRestored && focusRingCorrect
                    && defaultHighlightDisabled && touchModePreserved,
                $"open={opened} entry={entryRect} anchor={popup.AnchorRectForChecks} "
                + $"noPreview={noPreviewYet} previewOnly={previewOnly} dropVisual={dropVisual} disk={disk} rating={file.Rating} "
                + $"popup={popup.IsOpen} touchMode={touchModeBefore}→{touchModeOpen}→{touchModeClosed} "
                + $"focusInPanel={focusInPanel} panelFocus={ReferenceEquals(panel, focused)} "
                + $"focusBack={control.IsFocused} focusRing={glass.FocusRingVisible} "
                + $"defaultHighlight={defaultHighlightDisabled}");
        }
        finally
        {
            popup.Close();
        }
    }

    /// <summary>
    /// RATING-07 紧凑态三区：竖屏与横屏各跑一遍滑动区相对步进、0/5 边界跟手、
    /// 拖放区绝对位置与 hover/放大、取消区清空预览和跨区衔接；抬手只写回一次。
    /// </summary>
    private static async Task CheckRatingPopupCompactZonesAsync()
    {
        var root = _root!;
        var main = _main!;
        var activity = _activity!;
        var previousOrientation = activity.RequestedOrientation;
        try
        {
            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Portrait;
            await WaitAsync(() => root.Height > root.Width, "紧凑三区竖屏布局", 20000);
            await Task.Delay(500);
            var portrait = FindCompactRatingControl(root);
            if (portrait == null)
            {
                Check("RATING-07 紧凑三区（竖屏）", false,
                    $"form={root.Form} phone={root.PhoneRating.Form} bar={root.ControlBar.RatingControl.Form}");
                return;
            }
            var portraitFile = await PrepareCompactZoneFileAsync(2);
            string portraitEvidence = await RunCompactZoneSuiteAsync(portrait, portraitFile, "竖屏");
            Check("RATING-07 紧凑三区（竖屏）", true, portraitEvidence);

            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Landscape;
            await WaitAsync(() => root.Width > root.Height, "紧凑三区横屏布局", 20000);
            await Task.Delay(600);
            var landscape = FindCompactRatingControl(root);
            if (landscape == null || landscape.Horizontal)
            {
                Check("RATING-07 紧凑三区（横屏竖排）", false,
                    $"form={root.Form} control={(landscape == null ? "null" : landscape.Form.ToString())} "
                    + $"horizontal={landscape?.Horizontal}");
                return;
            }
            string landscapeEvidence = await RunCompactZoneSuiteAsync(landscape, portraitFile, "横屏");
            Check("RATING-07 紧凑三区（横屏竖排）", true, landscapeEvidence);
        }
        finally
        {
            activity.RequestedOrientation = previousOrientation;
            try
            {
                await WaitAsync(() => Math.Abs(root.Width - root.Height) > 1, "紧凑三区方向还原", 20000);
            }
            catch (Exception exception)
            {
                Console.WriteLine("[Native] 星级三区验收方向还原失败：" + exception.Message);
            }
        }
    }

    /// <summary>取当前真正为紧凑态且可见的星级控件（手机槽位优先，其次控制栏）。</summary>
    /// <param name="root">原生根视图。</param>
    /// <returns>紧凑控件；没有时为 null。</returns>
    private static AndroidRatingControl? FindCompactRatingControl(AndroidNativeRootView root)
    {
        var candidates = new List<AndroidRatingControl>();
        if (root.Form != ScreenForm.Wide) candidates.Add(root.PhoneRating);
        candidates.Add(root.ControlBar.RatingControl);
        return candidates.FirstOrDefault(item => item.IsShown && item.Width > 0
            && item.Form == RatingControlForm.Compact);
    }

    /// <summary>打开固定星级素材并等待当前照片与星级真值就绪。</summary>
    /// <param name="rating">初始星级。</param>
    /// <returns>目标照片。</returns>
    private static async Task<ImageFile> PrepareCompactZoneFileAsync(int rating)
    {
        var (file, _, _, _) = await OpenRatingPopupFixtureAsync(rating);
        var main = _main!;
        await main.SetRatingAsync(file, rating);
        await WaitAsync(() => file.Rating == rating, $"紧凑三区素材 {rating} 星", 10000);
        return file;
    }

    /// <summary>在给定紧凑控件上跑完整三区序列并返回证据文本。</summary>
    /// <param name="control">紧凑星级控件。</param>
    /// <param name="file">目标照片。</param>
    /// <param name="tag">竖屏/横屏标记。</param>
    /// <returns>证据文本。</returns>
    private static async Task<string> RunCompactZoneSuiteAsync(AndroidRatingControl control, ImageFile file,
        string tag)
    {
        var root = _root!;
        var popup = root.RatingPopup;
        var density = (float)AndroidUiTheme.Density(_activity!);
        var entry = ScreenRectOf(control);
        float cx = entry.CenterX(), cy = entry.CenterY();
        double step = RatingControlGeometry.SlideStep;
        float stepX = control.Horizontal ? (float)(step * density) : 0;
        float stepY = control.Horizontal ? 0 : -(float)(step * density);
        float subRatio = (float)((RatingControlGeometry.TapSlop + step) / (2 * step));
        long now = SystemClock.UptimeMillis();
        popup.Close();
        await _main!.SetRatingAsync(file, 2);
        await WaitAsync(() => file.Rating == 2, $"{tag}三区起手复位 2 星", 10000);
        await Task.Delay(120);

        using (var down = MotionEvent.Obtain(now, now, MotionEventActions.Down, cx, cy, 0))
            control.DispatchTouchEvent(down);
        await WaitAsync(() => popup.IsOpen, $"{tag}紧凑三区弹窗打开", 5000);
        await Task.Delay(180);
        if (popup.CompactPreviewForChecks != null)
            throw new InvalidOperationException($"{tag}起手未越阈值就出现预览 {popup.CompactPreviewForChecks}");

        // 越过点按阈值但不足一步：预览保持原值。
        DispatchMove(control, now, now + 40, cx + stepX * subRatio, cy + stepY * subRatio);
        if (popup.CompactPreviewForChecks != 2)
            throw new InvalidOperationException($"{tag}不足一步预览 {popup.CompactPreviewForChecks} ≠ 2");

        // 1/2/4 步：3/4/5；5 步外拉后反向 1 步回到 4。
        DispatchMove(control, now, now + 60, cx + stepX, cy + stepY);
        if (popup.CompactPreviewForChecks != 3)
            throw new InvalidOperationException($"{tag}{step:0.#} 点预览 {popup.CompactPreviewForChecks} ≠ 3");
        DispatchMove(control, now, now + 80, cx + stepX * 2, cy + stepY * 2);
        if (popup.CompactPreviewForChecks != 4)
            throw new InvalidOperationException($"{tag}{step * 2:0.#} 点预览 {popup.CompactPreviewForChecks} ≠ 4");
        DispatchMove(control, now, now + 100, cx + stepX * 4, cy + stepY * 4);
        if (popup.CompactPreviewForChecks != 5)
            throw new InvalidOperationException($"{tag}{step * 4:0.#} 点预览 {popup.CompactPreviewForChecks} ≠ 5");
        DispatchMove(control, now, now + 120, cx + stepX * 5, cy + stepY * 5);
        if (popup.CompactPreviewForChecks != 5)
            throw new InvalidOperationException($"{tag}5 星继续外拉预览 {popup.CompactPreviewForChecks} ≠ 5");
        DispatchMove(control, now, now + 140, cx + stepX * 4, cy + stepY * 4);
        if (popup.CompactPreviewForChecks != 4)
            throw new InvalidOperationException($"{tag}5 星反向 {step:0.#} 点预览 {popup.CompactPreviewForChecks} ≠ 4");
        using (var up = MotionEvent.Obtain(now, now + 160, MotionEventActions.Up,
                   cx + stepX * 4, cy + stepY * 4, 0))
            control.DispatchTouchEvent(up);
        await WaitAsync(() => !popup.IsOpen && file.Rating == 4, $"{tag}相对滑动写回 4", 15000);

        // 拖放区：绝对位置、圆形 hover 与放大。
        popup.Close();
        await _main!.SetRatingAsync(file, 2);
        await WaitAsync(() => file.Rating == 2, $"{tag}复位 2 星", 10000);
        await Task.Delay(150);
        using (var down = MotionEvent.Obtain(now + 200, now + 200, MotionEventActions.Down, cx, cy, 0))
            control.DispatchTouchEvent(down);
        await WaitAsync(() => popup.IsOpen, $"{tag}拖放弹窗打开", 5000);
        await Task.Delay(180);
        var slot4 = popup.SlotRectPx(0, 4);
        if (slot4.Width() <= 0)
            throw new InvalidOperationException($"{tag}弹窗槽位不可用");
        var content = popup.ContentViewForChecks!;
        var location = new int[2];
        content.GetLocationOnScreen(location);
        float dropX = location[0] + slot4.CenterX(), dropY = location[1] + slot4.CenterY();
        DispatchMove(control, now + 200, now + 240, dropX, dropY);
        if (popup.CompactPreviewForChecks != 4 || popup.DropHoveredSlotForChecks != 4
            || popup.ScaledSlotForChecks != 4)
            throw new InvalidOperationException(
                $"{tag}拖放预览 {popup.CompactPreviewForChecks}、hover {popup.DropHoveredSlotForChecks}、"
                + $"放大 {popup.ScaledSlotForChecks}（期望 4/4/4）");
        await Task.Delay(300);
        float scale = popup.ScaledScaleForChecks;
        if (Math.Abs(scale - DesignTokens.MotionRatingPressScale) > .05)
            throw new InvalidOperationException($"{tag}拖放放大 {scale:0.###}× ≠ {DesignTokens.MotionRatingPressScale:0.##}×");
        var slot0 = popup.SlotRectPx(0, 0);
        DispatchMove(control, now + 200, now + 260, location[0] + slot0.CenterX(), location[1] + slot0.CenterY());
        if (popup.CompactPreviewForChecks != 0)
            throw new InvalidOperationException($"{tag}拖放 0 号格夹取失败（{popup.CompactPreviewForChecks}）");
        var slot5 = popup.SlotRectPx(0, 5);
        DispatchMove(control, now + 200, now + 280, location[0] + slot5.CenterX(), location[1] + slot5.CenterY());
        if (popup.CompactPreviewForChecks != 5)
            throw new InvalidOperationException($"{tag}拖放 5 号格夹取失败（{popup.CompactPreviewForChecks}）");
        using (var up = MotionEvent.Obtain(now + 200, now + 300, MotionEventActions.Up,
                   location[0] + slot5.CenterX(), location[1] + slot5.CenterY(), 0))
            control.DispatchTouchEvent(up);
        await WaitAsync(() => !popup.IsOpen && file.Rating == 5, $"{tag}拖放写回 5", 15000);

        // 跨区：下区 3 → 中区 5 → 下区以 5 为基值反向一步得 4。
        popup.Close();
        await _main.SetRatingAsync(file, 2);
        await WaitAsync(() => file.Rating == 2, $"{tag}跨区复位 2 星", 10000);
        await Task.Delay(150);
        using (var down = MotionEvent.Obtain(now + 400, now + 400, MotionEventActions.Down, cx, cy, 0))
            control.DispatchTouchEvent(down);
        await WaitAsync(() => popup.IsOpen, $"{tag}跨区弹窗打开", 5000);
        await Task.Delay(180);
        DispatchMove(control, now + 400, now + 420, cx + stepX, cy + stepY);
        DispatchMove(control, now + 400, now + 440,
            location[0] + slot5.CenterX(), location[1] + slot5.CenterY());
        if (popup.CompactPreviewForChecks != 5 || popup.DropHoveredSlotForChecks != 5)
            throw new InvalidOperationException($"{tag}下区→中区失败（{popup.CompactPreviewForChecks}）");
        float backX = control.Horizontal ? location[0] + slot5.CenterX() : cx;
        float backY = control.Horizontal ? cy : location[1] + slot5.CenterY();
        DispatchMove(control, now + 400, now + 460, backX, backY);
        if (popup.CompactPreviewForChecks != 5 || popup.DropHoveredSlotForChecks != null)
            throw new InvalidOperationException($"{tag}中区→下区基值或 hover 清除失败");
        DispatchMove(control, now + 400, now + 480, backX - stepX, backY - stepY);
        if (popup.CompactPreviewForChecks != 4)
            throw new InvalidOperationException($"{tag}中区→下区续滑预览 {popup.CompactPreviewForChecks} ≠ 4");
        using (var up = MotionEvent.Obtain(now + 400, now + 500, MotionEventActions.Up,
                   backX - stepX, backY - stepY, 0))
            control.DispatchTouchEvent(up);
        await WaitAsync(() => !popup.IsOpen && file.Rating == 4, $"{tag}跨区续滑写回 4", 15000);

        // 取消区：越过浮窗远端边缘清空预览、hover 与放大，抬手不写。
        popup.Close();
        await _main.SetRatingAsync(file, 3);
        await WaitAsync(() => file.Rating == 3, $"{tag}取消复位 3 星", 10000);
        await Task.Delay(150);
        using (var down = MotionEvent.Obtain(now + 600, now + 600, MotionEventActions.Down, cx, cy, 0))
            control.DispatchTouchEvent(down);
        await WaitAsync(() => popup.IsOpen, $"{tag}取消弹窗打开", 5000);
        await Task.Delay(180);
        var panel = popup.PanelRectForChecks;
        float cancelX = cx, cancelY = cy;
        switch (popup.DirectionForChecks)
        {
            case "up": cancelY = panel.Top - 10 * density; break;
            case "down": cancelY = panel.Bottom + 10 * density; break;
            case "left": cancelX = panel.Left - 10 * density; break;
            default: cancelX = panel.Right + 10 * density; break;
        }
        DispatchMove(control, now + 600, now + 620, cancelX, cancelY);
        if (popup.CompactPreviewForChecks != null || popup.DropHoveredSlotForChecks != null
            || popup.ScaledSlotForChecks != null)
            throw new InvalidOperationException(
                $"{tag}取消区未清空（预览 {popup.CompactPreviewForChecks}、hover {popup.DropHoveredSlotForChecks}）");
        using (var up = MotionEvent.Obtain(now + 600, now + 640, MotionEventActions.Up, cancelX, cancelY, 0))
            control.DispatchTouchEvent(up);
        await WaitAsync(() => !popup.IsOpen, $"{tag}取消后弹窗收起", 5000);
        if (file.Rating != 3)
            throw new InvalidOperationException($"{tag}取消区改写了星级（{file.Rating}）");

        return $"{tag}方向={popup.DirectionForChecks} horizontal={control.Horizontal}：28/56/112 点预览 3/4/5、"
            + $"5 星反向 {step:0.#} 点回 4；拖放 4 号格 hover+1.25×、两端夹 0/5；跨区以 5 为基值续滑到 4；"
            + "取消区清空预览与 hover、0 次写回";
    }

    /// <summary>向紧凑控件派发一次屏幕坐标的移动事件。</summary>
    private static void DispatchMove(AndroidRatingControl control, long down, long at, float x, float y)
    {
        using var move = MotionEvent.Obtain(down, at, MotionEventActions.Move, x, y, 0);
        control.DispatchTouchEvent(move);
    }

    private static IEnumerable<string> PopupTextLabels(View view)
    {
        if (view is TextView { Text: { Length: > 0 } text }) yield return text;
        if (view is not ViewGroup group) yield break;
        for (int i = 0; i < group.ChildCount; i++)
            if (group.GetChildAt(i) is { } child)
                foreach (string label in PopupTextLabels(child)) yield return label;
    }

    /// <summary>视图在屏幕上的矩形。</summary>
    /// <param name="view">目标视图。</param>
    /// <returns>屏幕矩形。</returns>
    private static Rect ScreenRectOf(View view)
    {
        var location = new int[2];
        view.GetLocationOnScreen(location);
        return new Rect(location[0], location[1], location[0] + view.Width, location[1] + view.Height);
    }

    /// <summary>展开方向的验收文本（与弹窗自报口径一致）。</summary>
    /// <param name="direction">方向。</param>
    /// <returns><c>up</c> / <c>down</c> / <c>left</c> / <c>right</c>。</returns>
    private static string DirectionText(RatingPopupDirection direction) => direction switch
    {
        RatingPopupDirection.Up => "up",
        RatingPopupDirection.Left => "left",
        RatingPopupDirection.Right => "right",
        _ => "down",
    };
}
