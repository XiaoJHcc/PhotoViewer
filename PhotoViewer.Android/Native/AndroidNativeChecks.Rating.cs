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
using PhotoViewer.Core.Image;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using IOPath = System.IO.Path;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check faults</c> 的星级弹窗组（验收专用）：非冲突单条目弹窗、点星整组写回、
/// 卡片长按入口，以及紧凑星级的起手/拖动预览/抬手提交。全部经真实视图派发触摸，
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
                && popup.ModeForChecks == RatingPopupMode.Single
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
            bool focusInPanel = panel != null && focused != null && IsDescendantOf(panel, focused);
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
        try
        {
            long now = SystemClock.UptimeMillis();
            using (var down = MotionEvent.Obtain(now, now, MotionEventActions.Down,
                       entryRect.CenterX(), entryRect.CenterY(), 0))
                control.DispatchTouchEvent(down);
            await WaitAsync(() => popup.IsOpen, "紧凑星级起手打开弹窗", 5000);
            await Task.Delay(200);
            bool opened = popup.IsOpen && popup.ModeForChecks == RatingPopupMode.Single
                && popup.AnchorKindForChecks == RatingPopupAnchor.Control
                && SameRect(popup.AnchorRectForChecks, entryRect);
            bool noPreviewYet = file.Rating == 2;
            if (popup.ContentViewForChecks is not { } content)
            {
                Check("RATING-07 紧凑星级起手与拖动提交", false, $"open={popup.IsOpen} 内容视图不可用");
                return;
            }
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
            bool previewOnly = file.Rating == 2 && popup.IsOpen;
            using (var up = MotionEvent.Obtain(now, now + 140, MotionEventActions.Up, targetX, targetY, 0))
                control.DispatchTouchEvent(up);
            await WaitAsync(() => !popup.IsOpen && file.Rating == 4, "紧凑星级抬手提交并收起", 15000);
            bool disk = (await XmpRatingReader.ReadAsync(new AndroidFileRef(path))).ValueOrZero == 4;
            Check("RATING-07 紧凑星级起手与拖动提交",
                opened && noPreviewYet && previewOnly && disk && file.Rating == 4,
                $"open={opened} entry={entryRect} anchor={popup.AnchorRectForChecks} "
                + $"noPreview={noPreviewYet} previewOnly={previewOnly} disk={disk} rating={file.Rating} "
                + $"popup={popup.IsOpen}");
        }
        finally
        {
            popup.Close();
        }
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
