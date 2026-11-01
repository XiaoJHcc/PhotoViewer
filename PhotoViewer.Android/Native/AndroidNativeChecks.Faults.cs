using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using PhotoViewer.Android.Core;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using AndroidUri = Android.Net.Uri;
using IOPath = System.IO.Path;
using JavaFile = Java.IO.File;

namespace PhotoViewer.Android.Native;

/// <summary>
/// <c>--es pv-check faults</c>（验收专用）：用 App 自有素材与测试替身核对此前只能手验/代码核对的失败与边界项——
/// IMAGE-08 八方向、SCOPE-04 RAW 全分辨率、TOOLBAR-03 外部存储评分写回、RATING-04 冲突指示、
/// IMAGE-03 Full 失败、STATE-02/OPEN-03 打开失败、STATE-03 评分写回失败、SETTINGS-14 损坏设置、SIM-10 列表态错误标签、
/// ANALYSIS-10 DINO 失败、SCOPE-02 内存告警关闭增强、OPEN-06 外部打开。
/// 素材只写 App 内部目录与 <c>GetExternalFilesDir</c>；产品代码行为不作任何改动。
/// </summary>
internal static partial class AndroidNativeChecks
{
    /// <summary>失败注入组的步骤表；每步独立 try/catch，失败记 FAIL 后继续下一步。</summary>
    private static async Task RunFaultsChecksAsync()
    {
        await WaitForScreenAsync();
        await RunStepAsync("方向 1..8", CheckOrientationsAsync);
        await RunStepAsync("SCOPE-04", CheckRawFullResolutionAsync);
        await RunStepAsync("TOOLBAR-03", CheckExternalStorageWriteAsync);
        await RunStepAsync("RATING-04", CheckRatingConflictAsync);
        await RunStepAsync("RATING-04 写入", CheckRatingSourcesWriteAsync);
        await RunStepAsync("RATING-04 控制栏", CheckRatingControlBarAsync);
        await RunStepAsync("RATING-04 失败保留", CheckRatingSourcesFailureAsync);
        await RunStepAsync("IMAGE-03", CheckFullFailureAsync);
        await RunStepAsync("STATE-02/OPEN-03", CheckOpenFailureAsync);
        await RunStepAsync("STATE-03", CheckRatingFailureAsync);
        await RunStepAsync("SETTINGS-14", CheckCorruptSettingsAsync);
        await RunStepAsync("SIM-10", CheckSimilarityListErrorAsync);
        await RunStepAsync("ANALYSIS-10", CheckAnalysisFailureAsync);
        await RunStepAsync("SCOPE-03", CheckScopeBoundaryAsync);
        await RunStepAsync("STATE-04", CheckDatabaseUnavailableAsync);
        await RunStepAsync("SCOPE-02", CheckMemoryTrimAsync);
        await RunStepAsync("OPEN-06", CheckExternalOpenAsync);
    }

    /// <summary>IMAGE-08：运行时生成 8 张只有 EXIF 方向不同的 JPEG，逐个断言解码方向与显示朝向。</summary>
    private static async Task CheckOrientationsAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var root = _root!;
        string folder = EnsureOrientationFolder(activity);
        await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folder));
        await WaitAsync(() => main.FileVM.ThumbnailList.FilteredFiles.Count >= 8 && main.CurrentFile != null && session.Image != null,
            "方向素材上屏", 25000);

        for (int orientation = 1; orientation <= 8; orientation++)
        {
            string name = $"orientation-{orientation}.jpg";
            var file = main.FileVM.ThumbnailList.FilteredFiles.FirstOrDefault(item => item.Name == name);
            if (file == null)
            {
                Check($"方向 {orientation} 解码与显示朝向", false, "素材未进入筛选列表");
                continue;
            }
            if (!ReferenceEquals(main.CurrentFile, file)) main.FileVM.ThumbnailList.SelectImageCommand(file);
            await WaitAsync(() => ReferenceEquals(main.CurrentFile, file) && session.Image?.Orientation == orientation,
                $"{name} 上屏", 15000);
            await Task.Delay(120);

            bool swap = orientation >= 5;
            double expectedWidth = swap ? 200 : 320, expectedHeight = swap ? 320 : 200;
            var image = session.Image;
            var viewport = root.Photo.Viewport;
            bool ok = image != null && image.Orientation == orientation && image.SourceWidth == 320 && image.SourceHeight == 200
                && viewport.Orientation == orientation
                && viewport.ImageWidth == expectedWidth && viewport.ImageHeight == expectedHeight;
            Check($"方向 {orientation} 解码与显示朝向", ok,
                $"image={image?.Width}x{image?.Height} source={image?.SourceWidth}x{image?.SourceHeight} "
                + $"orientation={image?.Orientation}/{viewport.Orientation} logical={viewport.ImageWidth:0}x{viewport.ImageHeight:0} "
                + $"expected={expectedWidth:0}x{expectedHeight:0}");
        }
    }

    /// <summary>SCOPE-04：RAW 走 LibRaw 全分辨率开发，放大到物理 100% 后展示尺寸必须等于源尺寸。</summary>
    private static async Task CheckRawFullResolutionAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var root = _root!;
        string folder = IOPath.Combine(activity.FilesDir!.AbsolutePath, "pv-check-raw");
        string? raw = Directory.Exists(folder)
            ? Directory.GetFiles(folder).FirstOrDefault(path => path.EndsWith(".arw", StringComparison.OrdinalIgnoreCase))
            : null;
        if (raw == null)
        {
            Check("SCOPE-04 RAW 全分辨率", false, $"RAW 夹具缺失：{folder}");
            return;
        }

        try
        {
            await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folder));
            await WaitAsync(() => main.CurrentFile != null && session.Image != null, "RAW 首图上屏", 30000);
            var intro = session.Image!;
            if (intro.SourceWidth < 6000 || intro.SourceHeight < 4000)
            {
                Check("SCOPE-04 RAW 全分辨率", false,
                    $"源尺寸过小：{intro.SourceWidth}x{intro.SourceHeight}（期望 ≥6000×4000，{IOPath.GetFileName(raw)}）");
                return;
            }

            root.Photo.ZoomToPhysical(1);
            await WaitAsync(() => !session.IsProxy, "RAW Full 升级", 45000);
            var full = session.Image!;
            Check("SCOPE-04 RAW 全分辨率", full.Width == full.SourceWidth && full.Height == full.SourceHeight,
                $"{IOPath.GetFileName(raw)} source={full.SourceWidth}x{full.SourceHeight} "
                + $"displayed={full.Width}x{full.Height} proxy={session.IsProxy}");
        }
        catch (TimeoutException exception)
        {
            Check("SCOPE-04 RAW 全分辨率", false,
                $"{exception.Message}；当前 {session.Image?.Width}x{session.Image?.Height} proxy={session.IsProxy}");
        }
    }

    /// <summary>TOOLBAR-03 边界：<c>GetExternalFilesDir</c> 下的评分写回要落到文件（XMP 字节回读一致）。</summary>
    private static async Task CheckExternalStorageWriteAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        string? external = activity.GetExternalFilesDir(null)?.AbsolutePath;
        if (external == null)
        {
            Check("TOOLBAR-03 外部存储评分写回", false, "GetExternalFilesDir 不可用");
            return;
        }

        string folder = IOPath.Combine(external, "pv-check-write");
        Directory.CreateDirectory(folder);
        string path = IOPath.Combine(folder, "external-rating.jpg");
        // 每次重写素材：星级断言从 0 起跑，重复运行结论一致。
        WriteCheckJpeg(path, 800, 600, rating: 0);
        var target = new AndroidFileRef(path);
        await main.FolderVM.OpenImageAsync(target);
        await WaitAsync(() => main.CurrentFile?.File.StorageId.Value == target.StorageId.Value && session.Image != null, "外部存储素材上屏", 20000);

        var file = main.CurrentFile!;
        await main.SetRatingAsync(file, 4);
        int readBack = (await XmpRatingReader.ReadAsync(new AndroidFileRef(path))).ValueOrZero;
        byte[] bytes = File.ReadAllBytes(path);
        bool xmpMarker = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096))
            .Contains("xmp:Rating=\"4\"", StringComparison.Ordinal);
        // 写回成功后共享层直接回填逐来源真值、不清空 EXIF，模型星级应即时收敛到 4。
        await WaitAsync(() => file.Rating == 4, "内存星级同步", 8000);
        Check("TOOLBAR-03 外部存储评分写回", readBack == 4 && file.Rating == 4 && xmpMarker,
            $"readback={readBack} model={file.Rating} xmpMarker={xmpMarker} path={path}");
    }

    /// <summary>
    /// RATING-04：组级冲突时卡片与控制栏 0 号槽位显示红色警示三角；点卡片三角打开来源浮窗
    /// （不写值、不切主图），浮窗按真实锚点向下展开并列出两个来源的名称、标签与状态。
    /// </summary>
    private static async Task CheckRatingConflictAsync()
    {
        var main = _main!;
        var root = _root!;
        var (folderPath, path, sidecarPath) = EnsureConflictFixture(2, 4);
        var target = new AndroidFileRef(path);
        await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folderPath));
        try
        {
            await WaitAsync(
                () => main.CurrentFile?.File.StorageId.Value == target.StorageId.Value && main.CurrentFile.HasRatingConflict,
                "冲突素材上屏", 25000);
        }
        catch (TimeoutException exception)
        {
            Check("RATING-04 冲突指示", false,
                $"{exception.Message} current={main.CurrentFile?.Name ?? "null"} "
                + $"conflict={main.CurrentFile?.HasRatingConflict} sources={main.CurrentFile?.RatingSources.Count ?? 0}");
            return;
        }

        var file = main.CurrentFile;
        if (file == null || !file.HasRatingConflict)
        {
            Check("RATING-04 冲突指示", false,
                $"current={file?.Name ?? "null"} conflict={file?.HasRatingConflict} "
                + $"sources={file?.RatingSources.Count ?? 0}");
            return;
        }

        await WaitForListSettledAsync(main);
        file = main.CurrentFile;
        if (file == null || !file.HasRatingConflict)
        {
            Check("RATING-04 冲突指示", false, "列表重算后冲突消失");
            return;
        }
        int index = main.FileVM.ThumbnailList.FilteredFiles.IndexOf(file);
        var card = index >= 0 ? root.Filmstrip.CardAt(index) : null;
        Check("RATING-04 冲突指示",
            card?.ShowsRatingConflict == true && root.ControlBar.RatingConflictForChecks,
            $"card={card?.ShowsRatingConflict} control={root.ControlBar.RatingConflictForChecks} "
            + $"sources={file.RatingSources.Count} rating={file.Rating}");

        if (card == null || !main.IsRowLayout)
        {
            Check("RATING-04 卡片三角打开来源浮窗", false, "行布局冲突卡片不可用");
            return;
        }

        var popup = root.RatingSourcesPopup;
        try
        {
            byte[] embeddedBefore = File.ReadAllBytes(path);
            byte[] sidecarBefore = File.ReadAllBytes(sidecarPath);
            var slot = card.StarSlotRectPx(0) ?? throw new InvalidOperationException("卡片 0 号槽位不可用");
            var location = new int[2];
            card.GetLocationOnScreen(location);
            var expectedAnchor = new Rect(location[0] + slot.Left, location[1] + slot.Top,
                location[0] + slot.Right, location[1] + slot.Bottom);
            TapLocal(card, slot.CenterX(), slot.CenterY());
            await WaitAsync(() => popup.IsOpen && main.RatingSources.IsOpen, "卡片三角打开浮窗", 5000);
            await Task.Delay(250);
            bool stillOpen = popup.IsOpen && main.RatingSources.IsOpen;
            double density = AndroidUiTheme.Density(root.Context!);
            var items = main.RatingSources.Items;
            bool twoSources = items.Count == 2
                && items[0].Name == "conflict.jpg" && items[0].Kind == "JPG" && items[0].Status == "2 星"
                && items[1].Name == "conflict.xmp" && items[1].Kind == "XMP" && items[1].Status == "4 星";
            // 打开浮窗不写评分、不切图：主图身份与两个来源的磁盘字节都必须原样保留。
            bool unchanged = ReferenceEquals(main.CurrentFile, file) && file.Rating == 2
                && File.ReadAllBytes(path).SequenceEqual(embeddedBefore)
                && File.ReadAllBytes(sidecarPath).SequenceEqual(sidecarBefore);
            bool anchored = SameRect(popup.AnchorRectForChecks, expectedAnchor);
            bool sized = popup.WidthPxForChecks == (int)Math.Round(AndroidRatingSourcesPopup.WindowWidthDp * density)
                && popup.HeightPxForChecks > 0;
            Check("RATING-04 卡片三角打开来源浮窗",
                twoSources && unchanged && anchored && sized && stillOpen && popup.DirectionForChecks == "down",
                $"open={stillOpen} sources={items.Count} [{string.Join(" / ", items.Select(item => $"{item.Name}:{item.Kind}:{item.Status}"))}] "
                + $"current={main.CurrentFile?.Name} rating={file.Rating} anchor={popup.AnchorRectForChecks} "
                + $"expectedAnchor={expectedAnchor} width={popup.WidthPxForChecks} height={popup.HeightPxForChecks} "
                + $"direction={popup.DirectionForChecks}");
        }
        finally
        {
            popup.Close();
        }
    }

    /// <summary>
    /// RATING-04：点浮窗内任一条目的任意值都整组写入全部来源；写入成功且冲突解除后浮窗关闭，
    /// 两个来源的磁盘真值都收敛到目标值。
    /// </summary>
    private static async Task CheckRatingSourcesWriteAsync()
    {
        var main = _main!;
        var root = _root!;
        var (folderPath, path, sidecarPath) = EnsureConflictFixture(2, 4);
        await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folderPath));
        await WaitAsync(() => main.CurrentFile?.HasRatingConflict == true, "冲突素材上屏", 25000);
        await WaitForListSettledAsync(main);
        var file = main.CurrentFile!;
        var popup = root.RatingSourcesPopup;
        try
        {
            int index = main.FileVM.ThumbnailList.FilteredFiles.IndexOf(file);
            var card = index >= 0 ? root.Filmstrip.CardAt(index) : null;
            if (card?.StarSlotRectPx(0) is not { } cardSlot)
                throw new InvalidOperationException("行布局冲突卡片不可用");
            TapLocal(card, cardSlot.CenterX(), cardSlot.CenterY());
            await WaitAsync(() => popup.IsOpen, "来源浮窗打开", 5000);
            await Task.Delay(250);
            if (!popup.IsOpen) throw new InvalidOperationException("浮窗打开后立即被关闭");
            int entryIndex = Math.Max(0, main.RatingSources.Items.Count - 1);
            var target = popup.SlotRectPx(entryIndex, 5);
            if (target.Width() <= 0 || popup.ContentViewForChecks is not { } content)
                throw new InvalidOperationException("浮窗槽位不可用");
            TapLocal(content, target.CenterX(), target.CenterY());
            await WaitAsync(() => !popup.IsOpen && !main.RatingSources.IsOpen && !file.HasRatingConflict,
                "整组写入后浮窗关闭", 15000);
            await WaitAsync(() => file.RatingSources.All(source =>
                    source.State == XmpRatingState.Rated && source.Value == 5), "两来源收敛", 8000);
            // sidecar 是独立 XMP 包（不是照片容器），按字节核对而不是再走容器解析。
            bool sidecar = File.ReadAllText(sidecarPath).Contains("xmp:Rating=\"5\"", StringComparison.Ordinal);
            bool embedded = (await XmpRatingReader.ReadAsync(new AndroidFileRef(path))).ValueOrZero == 5;
            bool cardConverged = card.ShowsRatingConflict == false;
            Check("RATING-04 整组写入收敛并关闭浮窗",
                embedded && sidecar && cardConverged,
                $"embedded={embedded} sidecar={sidecar} conflict={file.HasRatingConflict} "
                + $"rating={file.Rating} popup={popup.IsOpen} shared={main.RatingSources.IsOpen}");
        }
        finally
        {
            popup.Close();
        }
    }

    /// <summary>
    /// RATING-04：控制栏评分组 0 号按钮打开的是当前照片组的来源浮窗，横排控制栏向上展开；
    /// 打开本身不写值、不切图。
    /// </summary>
    private static async Task CheckRatingControlBarAsync()
    {
        var main = _main!;
        var root = _root!;
        var (folder, path, _) = EnsureConflictFixture(2, 4);
        await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folder));
        await WaitAsync(() => main.CurrentFile?.HasRatingConflict == true, "冲突素材上屏", 25000);
        await WaitForListSettledAsync(main);
        var file = main.CurrentFile!;
        var popup = root.RatingSourcesPopup;
        try
        {
            var node = await WaitForRatingNodeAsync(root, 0);
            double density = AndroidUiTheme.Density(root.Context!);
            TapLocal(root.ControlBar, (float)((node.X + node.Width / 2) * density),
                (float)((node.Y + node.Height / 2) * density));
            await WaitAsync(() => popup.IsOpen && main.RatingSources.IsOpen, "控制栏三角打开浮窗", 5000);
            await Task.Delay(250);
            bool stillOpen = popup.IsOpen && main.RatingSources.IsOpen;
            var items = main.RatingSources.Items;
            Check("RATING-04 控制栏三角打开浮窗",
                stillOpen && items.Count == 2 && items[0].Name == "conflict.jpg"
                && ReferenceEquals(main.CurrentFile, file) && file.Rating == 2
                && popup.DirectionForChecks == "up",
                $"open={stillOpen} sources={items.Count} current={main.CurrentFile?.Name} rating={file.Rating} "
                + $"direction={popup.DirectionForChecks} anchor={popup.AnchorRectForChecks}");
        }
        finally
        {
            popup.Close();
        }
    }

    /// <summary>
    /// RATING-04：写入失败时保留浮窗并显示刷新后的来源状态（不因失败收起），评分真值不变。
    /// </summary>
    private static async Task CheckRatingSourcesFailureAsync()
    {
        var main = _main!;
        var root = _root!;
        var (folder, path, sidecarPath) = EnsureConflictFixture(2, 4);
        SetFileReadOnly(path, true);
        SetFileReadOnly(sidecarPath, true);
        SetDirectoryReadOnly(folder, true);
        var popup = root.RatingSourcesPopup;
        try
        {
            await main.FolderVM.OpenFolderAsync(new AndroidFolderRef(folder));
            await WaitAsync(() => main.CurrentFile?.HasRatingConflict == true, "只读冲突素材上屏", 25000);
            await WaitForListSettledAsync(main);
            var file = main.CurrentFile!;
            root.ResetStatusForChecks();
            var card = root.Filmstrip.CardAt(main.FileVM.ThumbnailList.FilteredFiles.IndexOf(file));
            if (card?.StarSlotRectPx(0) is not { } cardSlot)
                throw new InvalidOperationException("只读冲突卡片不可用");
            TapLocal(card, cardSlot.CenterX(), cardSlot.CenterY());
            await WaitAsync(() => popup.IsOpen, "只读来源浮窗打开", 5000);
            await Task.Delay(250);
            if (!popup.IsOpen) throw new InvalidOperationException("浮窗打开后立即被关闭");
            var target = popup.SlotRectPx(0, 4);
            if (target.Width() <= 0 || popup.ContentViewForChecks is not { } content)
                throw new InvalidOperationException("浮窗槽位不可用");
            TapLocal(content, target.CenterX(), target.CenterY());
            await WaitAsync(() => root.StatusVisibleForChecks
                && (root.StatusTextForChecks ?? string.Empty).Contains(file.Name, StringComparison.Ordinal),
                "写入失败提示条", 15000);
            bool kept = popup.IsOpen && main.RatingSources.IsOpen;
            bool unchanged = file.HasRatingConflict && file.Rating == 2;
            Check("RATING-04 写入失败保留浮窗",
                kept && unchanged,
                $"popup={popup.IsOpen} shared={main.RatingSources.IsOpen} conflict={file.HasRatingConflict} "
                + $"rating={file.Rating} status={root.StatusTextForChecks}");
        }
        finally
        {
            popup.Close();
            SetFileReadOnly(path, false);
            SetFileReadOnly(sidecarPath, false);
            SetDirectoryReadOnly(folder, false);
        }
    }

    /// <summary>等待筛选列表稳定（后台来源补齐/评分扫描不再替换列表），避免列表替换关闭浮窗。</summary>
    private static async Task WaitForListSettledAsync(MainViewModel main)
    {
        IReadOnlyList<ImageFile>? last = null;
        int stable = 0;
        for (int attempt = 0; attempt < 40 && stable < 4; attempt++)
        {
            await Task.Delay(120);
            var files = main.FileVM.ThumbnailList.FilteredFiles;
            if (last != null && files.Count == last.Count && files.SequenceEqual(last)) stable++;
            else stable = 0;
            last = files.ToArray();
        }
    }

    /// <summary>重建冲突素材：内嵌 2 与同名 sidecar 4 不一致，形成组级冲突。</summary>
    /// <param name="embedded">内嵌星级。</param>
    /// <param name="sidecar">sidecar 星级。</param>
    /// <returns>目录、JPEG 与 sidecar 路径。</returns>
    private static (string Folder, string Path, string Sidecar) EnsureConflictFixture(int embedded, int sidecar)
    {
        string folder = IOPath.Combine(_activity!.FilesDir!.AbsolutePath, "pv-check-conflict");
        Directory.CreateDirectory(folder);
        string path = IOPath.Combine(folder, "conflict.jpg");
        string sidecarPath = IOPath.Combine(folder, "conflict.xmp");
        if (File.Exists(path)) SetFileReadOnly(path, false);
        if (File.Exists(sidecarPath)) SetFileReadOnly(sidecarPath, false);
        SetDirectoryReadOnly(folder, false);
        File.Delete(path);
        File.Delete(sidecarPath);
        WriteCheckJpeg(path, 640, 480, rating: embedded);
        File.WriteAllBytes(sidecarPath, BuildXmpPacket(sidecar));
        return (folder, path, sidecarPath);
    }

    /// <summary>在视图本地坐标点按（验收用，与真实触摸同一条消费路径）。</summary>
    /// <param name="view">目标视图。</param>
    /// <param name="xPx">横坐标（px）。</param>
    /// <param name="yPx">纵坐标（px）。</param>
    private static void TapLocal(View view, float xPx, float yPx)
    {
        long now = SystemClock.UptimeMillis();
        using var down = MotionEvent.Obtain(now, now, MotionEventActions.Down, xPx, yPx, 0);
        using var up = MotionEvent.Obtain(now, now, MotionEventActions.Up, xPx, yPx, 0);
        view.DispatchTouchEvent(down);
        view.DispatchTouchEvent(up);
    }

    /// <summary>卡片某个槽位在屏幕上的矩形（与产品锚点同一计算）。</summary>
    private static Rect? ScreenRectOfCardSlot(AndroidThumbnailCard card)
    {
        if (!card.IsAttachedToWindow || card.StarSlotRectPx(0) is not { } slot) return null;
        var location = new int[2];
        card.GetLocationOnScreen(location);
        return new Rect(location[0] + slot.Left, location[1] + slot.Top,
            location[0] + slot.Right, location[1] + slot.Bottom);
    }

    /// <summary>等待控制栏评分组第 <paramref name="slot"/> 个虚拟节点排布完成并返回其本地 dp 矩形。</summary>
    private static async Task<(string Name, string Kind, double X, double Y, double Width, double Height, bool Enabled)>
        WaitForRatingNodeAsync(AndroidNativeRootView root, int slot)
    {
        (string, string, double, double, double, double, bool)? found = null;
        await WaitAsync(() =>
        {
            int seen = 0;
            foreach (var node in root.ControlBar.NodesForChecks())
            {
                if (node.Kind != "Rating") continue;
                if (seen++ != slot) continue;
                if (node.Width <= 0 || node.Height <= 0) return false;
                found = node;
                return true;
            }
            return false;
        }, "控制栏评分槽位排布", 8000);
        return found!.Value;
    }

    private static bool SameRect(Rect left, Rect right)
        => left.Left == right.Left && left.Top == right.Top
            && left.Right == right.Right && left.Bottom == right.Bottom;


    /// <summary>IMAGE-03：Full 解码失败时保留屏上代理并给出非阻塞提示条，不覆盖主图错误态。</summary>
    private static async Task CheckFullFailureAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var root = _root!;
        var current = CurrentPresentationDecoder();
        if (current == null)
        {
            Check("IMAGE-03 Full 失败保留代理并提示", false, "展示解码器未注入，无法注入 Full 失败");
            return;
        }

        string path = EnsureFaultFullFailJpeg(activity);
        var original = SwapPresentationDecoder(new FullFailingDecoder(current));
        try
        {
            await main.FolderVM.OpenImageAsync(new AndroidFileRef(path));
            await WaitAsync(() => main.CurrentFile?.Name == IOPath.GetFileName(path) && session.Image != null,
                "Full 失败素材代理上屏", 30000);
            bool proxy = session.IsProxy;
            var intro = session.Image;
            root.ResetStatusForChecks();
            root.Photo.ZoomToPhysical(1);
            await WaitAsync(() => session.FullError.Length > 0, "Full 失败提示", 25000);
            bool kept = session.IsProxy && ReferenceEquals(session.Image, intro);
            string text = root.StatusTextForChecks ?? string.Empty;
            Check("IMAGE-03 Full 失败保留代理并提示",
                proxy && kept && root.StatusVisibleForChecks && text.Contains("全分辨率", StringComparison.Ordinal),
                $"proxy={proxy} kept={kept} visible={root.StatusVisibleForChecks} status={text} fullError={session.FullError}");
        }
        catch (TimeoutException exception)
        {
            Check("IMAGE-03 Full 失败保留代理并提示", false,
                $"{exception.Message}；proxy={session.IsProxy} fullError={session.FullError} status={root.StatusTextForChecks}");
        }
        finally
        {
            RestorePresentationDecoder(original);
        }
    }

    /// <summary>STATE-02 / OPEN-03：父目录枚举失败要显示对象与原因，并回退成单图打开目标照片。</summary>
    private static async Task CheckOpenFailureAsync()
    {
        var main = _main!;
        var root = _root!;
        string good = EnsureFaultGoodJpeg(_activity!);
        var target = new AndroidFileRef(good);
        // 用不存在的子目录做“枚举必失败”的父目录：与当前目录 Path 不同，才会真正走枚举与失败回退。
        var parent = new FailingFolderRef("fault-folder", IOPath.Combine(IOPath.GetDirectoryName(good)!, "fault-folder"));
        string? message = null;
        void OnOpenFailed(string text) => message = text;
        main.OpenFailed += OnOpenFailed;
        try
        {
            await main.FolderVM.OpenImageAsync(new ParentFailingFileRef(target, parent));
            await WaitAsync(() => message != null, "打开失败事件", 15000);
            Check("STATE-02 打开失败提示条",
                message != null && message.Contains(parent.Name, StringComparison.Ordinal)
                && root.StatusVisibleForChecks && root.StatusTextForChecks == message,
                $"message={message} visible={root.StatusVisibleForChecks} status={root.StatusTextForChecks}");
            Check("OPEN-03 枚举失败回退单图",
                main.CurrentFile?.File.StorageId.Value == target.StorageId.Value && main.FolderVM.AllFiles.Count == 1,
                $"current={main.CurrentFile?.File.StorageId.Value} count={main.FolderVM.AllFiles.Count} expected={target.StorageId.Value}");
        }
        finally
        {
            main.OpenFailed -= OnOpenFailed;
        }
    }

    /// <summary>STATE-03：只读副本的评分写回失败要保留原星级并显示文件名。</summary>
    private static async Task CheckRatingFailureAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var root = _root!;
        string source = EnsureFaultGoodJpeg(activity);
        string folder = IOPath.GetDirectoryName(source)!;
        string path = IOPath.Combine(folder, "readonly.jpg");
        File.Copy(source, path, true);
        SetFileReadOnly(path, true);
        SetDirectoryReadOnly(folder, true);
        try
        {
            await main.FolderVM.OpenImageAsync(new AndroidFileRef(path));
            await WaitAsync(() => main.CurrentFile?.Name == "readonly.jpg" && session.Image != null, "只读素材上屏", 20000);
            var file = main.CurrentFile!;
            int before = file.Rating;
            int wanted = before == 3 ? 2 : 3;
            string? failedName = null;
            void OnRatingWriteFailed(ImageFile failed, int rating) => failedName = failed.Name;
            main.RatingWriteFailed += OnRatingWriteFailed;
            root.ResetStatusForChecks();
            try
            {
                await main.SetRatingAsync(file, wanted);
                await WaitAsync(() => failedName != null, "评分写回失败事件", 12000);
                await WaitAsync(() => root.StatusVisibleForChecks
                    && (root.StatusTextForChecks ?? string.Empty).Contains(file.Name, StringComparison.Ordinal), "评分失败提示条", 8000);
                Check("STATE-03 评分写回失败提示", file.Rating == before && failedName == file.Name,
                    $"before={before} after={file.Rating} failed={failedName} status={root.StatusTextForChecks}");
            }
            finally
            {
                main.RatingWriteFailed -= OnRatingWriteFailed;
            }
        }
        finally
        {
            SetFileReadOnly(path, false);
            SetDirectoryReadOnly(folder, false);
        }
    }

    /// <summary>SETTINGS-14：损坏的设置 JSON 先备份成偏好键再以默认值启动，持久化诊断状态记录“损坏”。</summary>
    private static async Task CheckCorruptSettingsAsync()
    {
        const string settingsKey = "settings_json";
        const string preferenceName = "cc.xiaojh.PhotoViewer.Settings.checks";
        var prefs = Application.Context.GetSharedPreferences(preferenceName, FileCreationMode.Private)!;
        string? original = prefs.GetString(settingsKey, null);
        var backupKeys = new List<string>();
        SettingsViewModel? viewModel = null;
        SettingsRepository? repository = null;
        AndroidSettingsStorage.UseIsolatedProfile("checks");
        try
        {
            using (var editor = prefs.Edit()!)
            {
                editor.PutString(settingsKey, "{ this is not valid json");
                editor.Apply();
            }

            repository = new SettingsRepository(
                new AndroidSettingsStorage(),
                new AndroidPerformanceBudget(),
                appleKeyboard: false,
                automaticCacheLimit: true);
            await repository.InitializeAsync();
            viewModel = new SettingsViewModel(repository, null);
            await WaitAsync(() => viewModel.PersistenceStatus != null, "损坏设置诊断状态", 15000);

            string? error = repository.LastLoadError;
            bool backupOk = error != null && error.Contains(".corrupt-", StringComparison.Ordinal);
            string? backupKey = null;
            if (backupOk)
            {
                int start = error!.IndexOf("已备份为 ", StringComparison.Ordinal);
                if (start >= 0) backupKey = error[(start + "已备份为 ".Length)..].Split(' ')[0];
            }
            bool stored = backupKey != null && prefs.GetString(backupKey, null) != null;
            if (stored) backupKeys.Add(backupKey!);
            Check("SETTINGS-14 损坏设置备份", backupOk && stored,
                $"outcome={repository.LoadOutcome} error={error} backupKey={backupKey} stored={stored}");

            // 仅核对共享 ViewModel 的诊断状态，不涉及任何 UI 呈现。
            Check("SETTINGS-14 诊断状态记录损坏",
                viewModel.PersistenceStatus != null && viewModel.PersistenceStatus.Contains("损坏", StringComparison.Ordinal),
                $"status={viewModel.PersistenceStatus}");
        }
        finally
        {
            viewModel?.Dispose();
            // 隔离档内的延迟保存可能仍在飞行：先等在保存结束再把档位切回用户档，避免默认值写进用户设置。
            await Task.Delay(700);
            AndroidSettingsStorage.UseIsolatedProfile(string.Empty);
            using (var editor = prefs.Edit()!)
            {
                foreach (string key in backupKeys) editor.Remove(key);
                if (original == null) editor.Remove(settingsKey);
                else editor.PutString(settingsKey, original);
                editor.Apply();
            }
        }
    }

    /// <summary>SIM-10：列表态（有相似项）也要把提取错误显示在停靠动作区。</summary>
    private static async Task CheckSimilarityListErrorAsync()
    {
        var main = _main!;
        var root = _root!;
        var panel = main.FileVM.SimilarityPanel;
        var current = main.CurrentFile ?? throw new InvalidOperationException("缺少当前照片");
        var originalItems = panel.SimilarItems;
        int originalUnindexed = panel.UnindexedCount;
        string? originalError = panel.IndexError;
        bool wasOpen = main.FileVM.FilterBar.IsSimilarityPanelOpen;
        try
        {
            // 列表态 = 有相似项；停靠动作区只在“有未提取项”或“提取中”时占位，故一并注入。
            SetViewModelProperty(panel, nameof(panel.SimilarItems), new[] { new SimilarityItem(current, 0.9) });
            SetViewModelProperty(panel, nameof(panel.UnindexedCount), 1);
            SetViewModelProperty(panel, nameof(panel.IndexError), "注入的提取失败");
            main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
            await Task.Delay(700);
            Check("SIM-10 列表态错误标签",
                root.SimilarityPane.DockErrorVisibleForChecks && root.SimilarityPane.DockErrorTextForChecks == "注入的提取失败",
                $"visible={root.SimilarityPane.DockErrorVisibleForChecks} text={root.SimilarityPane.DockErrorTextForChecks} "
                + $"items={root.SimilarityPane.ItemCount} pane={root.SimilarityPane.Visibility}");
        }
        finally
        {
            main.FileVM.FilterBar.IsSimilarityPanelOpen = wasOpen;
            SetViewModelProperty(panel, nameof(panel.IndexError), originalError);
            SetViewModelProperty(panel, nameof(panel.SimilarItems), originalItems);
            SetViewModelProperty(panel, nameof(panel.UnindexedCount), originalUnindexed);
            await Task.Delay(300);
        }
    }

    /// <summary>ANALYSIS-10：DINO 失败后清掉诊断图并显示独立失败文案，不停在“计算中…”。</summary>
    private static async Task CheckAnalysisFailureAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var root = _root!;
        string source = EnsureFaultGoodJpeg(activity);
        string path = IOPath.Combine(IOPath.GetDirectoryName(source)!, "analysis-fail.jpg");
        File.Copy(source, path, true);
        byte[]? originalModel = DinoModelResources.TryLoadModelBytes();
        bool previousImmediate = main.Settings.AnalysisImmediateCompute;
        bool previousVisible = main.IsAnalysisViewVisible;
        try
        {
            DinoFeatureExtractor.ConfigureModelOverride([1, 2, 3]);
            // 覆盖只在会话首次创建前生效：先卸载已有会话，保证下一次推理用注入的坏模型建会话。
            DinoFeatureExtractor.UnloadSession();
            main.Settings.AnalysisImmediateCompute = true;
            main.IsAnalysisViewVisible = true;
            await main.FolderVM.OpenImageAsync(new AndroidFileRef(path));
            await WaitAsync(() => main.CurrentFile?.Name == "analysis-fail.jpg" && session.Image != null, "分析失败素材上屏", 25000);

            var tiles = root.AnalysisPresenterForChecks.Tiles;
            var diagnostics = tiles.Where(tile => tile.Kind == AnalysisTileKind.Diagnostic).ToList();
            await WaitAsync(() => diagnostics.Count > 0 && diagnostics.All(tile => tile.Placeholder == "分析失败"),
                "分析失败占位", 35000);
            bool noSource = diagnostics.All(tile => tile.Source == null);
            Check("ANALYSIS-10 DINO 失败清图并显示文案", noSource,
                $"diagnostics={diagnostics.Count} placeholders={string.Join("/", diagnostics.Select(tile => tile.Placeholder))} "
                + $"withSource={diagnostics.Count(tile => tile.Source != null)}");
        }
        finally
        {
            main.IsAnalysisViewVisible = previousVisible;
            main.Settings.AnalysisImmediateCompute = previousImmediate;
            if (originalModel != null) DinoFeatureExtractor.ConfigureModelOverride(originalModel);
            // 丢掉用坏模型建起来的会话，后续推理按恢复后的模型重建。
            DinoFeatureExtractor.UnloadSession();
        }
    }

    /// <summary>
    /// SCOPE-03：只有用户显式批量提取才写特征库。即时计算与「雾天增强」的产物只进进程缓存——
    /// 不入库、不写 XMP sidecar、不改照片字节；显式提取后三路齐备；清库同时清进程缓存，
    /// 诊断瓦片回到「未提取」而不再现算。
    /// </summary>
    private static async Task CheckScopeBoundaryAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var root = _root!;
        bool previousImmediate = main.Settings.AnalysisImmediateCompute;
        bool previousVisible = main.IsAnalysisViewVisible;
        var (folder, first, second) = EnsureScopeFixture(activity);
        try
        {
            main.Settings.AnalysisImmediateCompute = true;
            main.IsAnalysisViewVisible = true;
            await OpenFolderAndWaitAsync(folder);
            Console.WriteLine("[NativeCheck] SCOPE-03 素材上屏");

            // 指纹组按产品同一套分组口径取（同次曝光的 RAW/HIF/JPG 会合并成一组）。
            var groups = await FolderFeatureIndexer.GroupByFingerprintAsync(main.FolderVM.AllFiles);
            var current = main.CurrentFile ?? throw new InvalidOperationException("缺少当前照片");
            var group = groups.FirstOrDefault(candidate => candidate.Files.Contains(current))
                ?? throw new InvalidOperationException("当前照片没有指纹组");
            string fingerprint = group.Fingerprint;
            if (string.IsNullOrEmpty(fingerprint)) throw new InvalidOperationException("指纹为空");

            var diagnostics = root.AnalysisPresenterForChecks.Tiles
                .Where(tile => tile.Kind == AnalysisTileKind.Diagnostic).ToList();
            try
            {
                await WaitAsync(() => diagnostics.Count > 0
                    && diagnostics.All(tile => tile.Placeholder == null)
                    && diagnostics.Count(tile => tile.Source != null) >= 3,
                    "即时计算诊断瓦片出图", 90000);
            }
            catch (TimeoutException exception)
            {
                Check("SCOPE-03 即时计算诊断瓦片出图", false,
                    $"{exception.Message} placeholders=[{DescribeDiagnostics(diagnostics)}] "
                    + $"immediate={main.Settings.AnalysisImmediateCompute} visible={main.IsAnalysisViewVisible}");
                return;
            }
            Console.WriteLine($"[NativeCheck] SCOPE-03 诊断瓦片出图 [{DescribeDiagnostics(diagnostics)}]");

            byte[] firstBefore = File.ReadAllBytes(first);
            byte[] secondBefore = File.ReadAllBytes(second);
            var (photosImmediate, featuresImmediate, patchesImmediate) = await CountFingerprintRowsAsync(fingerprint);
            bool xmpImmediate = HasXmpSidecar(first) || HasXmpSidecar(second);
            Check("SCOPE-03 即时计算不入库",
                photosImmediate == 0 && featuresImmediate == 0 && patchesImmediate == 0 && !xmpImmediate,
                $"photos={photosImmediate} features={featuresImmediate} patches={patchesImmediate} "
                + $"xmp={xmpImmediate} tiles=[{DescribeDiagnostics(diagnostics)}] fp={fingerprint}");
            Console.WriteLine("[NativeCheck] SCOPE-03 即时计算边界完成");

            // 雾天增强对增强图实时重算：产物同样只进进程缓存。
            Console.WriteLine("[NativeCheck] SCOPE-03 增强开始");
            session.ToggleEnhanced();
            await WaitAsync(() => session.IsEnhanced, "开启增强", 15000);
            // 内存压力可能在上屏后释放增强图，用闩锁记住“确实看到过上屏”这一事实。
            bool enhancedSeen = false;
            await WaitAsync(() =>
            {
                bool on = session.EnhancedDisplay;
                if (on) enhancedSeen = true;
                return on;
            }, "增强图上屏", 90000);
            await WaitAsync(() => diagnostics.All(tile => tile.Placeholder == null), "增强图诊断瓦片出图", 90000);
            var (photosEnhanced, featuresEnhanced, patchesEnhanced) = await CountFingerprintRowsAsync(fingerprint);
            bool xmpEnhanced = HasXmpSidecar(first) || HasXmpSidecar(second);
            bool bytesSame = File.ReadAllBytes(first).SequenceEqual(firstBefore)
                && File.ReadAllBytes(second).SequenceEqual(secondBefore);
            Check("SCOPE-03 增强产物不入库不写 XMP",
                enhancedSeen && photosEnhanced == 0 && featuresEnhanced == 0 && patchesEnhanced == 0
                && !xmpEnhanced && bytesSame,
                $"photos={photosEnhanced} features={featuresEnhanced} patches={patchesEnhanced} "
                + $"xmp={xmpEnhanced} bytesSame={bytesSame} enhancedSeen={enhancedSeen} "
                + $"enhancedNow={session.EnhancedDisplay} tiles=[{DescribeDiagnostics(diagnostics)}]");
            Console.WriteLine("[NativeCheck] SCOPE-03 增强边界完成");

            // 显式批量提取（共享 indexer）才写库：三路齐备，且没有缺失。
            Console.WriteLine("[NativeCheck] SCOPE-03 显式提取开始");
            var indexer = new FolderFeatureIndexer();
            await AwaitOrTimeoutAsync(indexer.RunAsync(main.FolderVM.AllFiles), "批量提取", 300000);
            Console.WriteLine($"[NativeCheck] SCOPE-03 显式提取结束 error={indexer.LastError ?? "none"}");
            var missing = await FolderFeatureIndexer.EvaluateMissingPartsAsync(fingerprint);
            var (photosIndexed, featuresIndexed, patchesIndexed) = await CountFingerprintRowsAsync(fingerprint);
            Check("SCOPE-03 显式提取三路入库",
                !missing.AnyMissing && photosIndexed > 0 && featuresIndexed > 0 && patchesIndexed > 0,
                $"missing=cls{missing.NeedCls}/patch{missing.NeedPatches}/cv{missing.NeedCv} "
                + $"photos={photosIndexed} features={featuresIndexed} patches={patchesIndexed} "
                + $"error={indexer.LastError ?? "none"}");

            // 产品清库入口：删库 + 清 DinoFeatureCache / ShakeFlagService / AnalysisResultCache，不重启应用。
            main.Settings.AnalysisImmediateCompute = false;
            Console.WriteLine("[NativeCheck] SCOPE-03 清库开始");
            await AwaitOrTimeoutAsync(main.Settings.ClearFeatureDatabaseConfirmedAsync(), "清库", 120000);
            Console.WriteLine($"[NativeCheck] SCOPE-03 清库结束 status={main.Settings.ClearDbStatus}");
            long photosTotal = await PhotoDatabase.CountAsync();
            var (photosCleared, featuresCleared, patchesCleared) = await CountFingerprintRowsAsync(fingerprint);
            Check("SCOPE-03 清库清空特征库",
                photosTotal == 0 && photosCleared == 0 && featuresCleared == 0 && patchesCleared == 0,
                $"status={main.Settings.ClearDbStatus} photosTotal={photosTotal} "
                + $"row={photosCleared}/{featuresCleared}/{patchesCleared}");

            await WaitAsync(() => diagnostics.All(tile => tile.Placeholder == "未提取" && tile.Source == null),
                "清库后诊断瓦片回到未提取", 30000);
            // 即时计算已关闭：清库后不得退化成后台现算，抽样观察一小段时间保持「未提取」。
            int recomputed = 0;
            for (int sample = 0; sample < 15; sample++)
            {
                await Task.Delay(100);
                if (diagnostics.Any(tile => tile.Placeholder != "未提取" || tile.Source != null)) recomputed++;
            }
            Check("SCOPE-03 清库后诊断瓦片回到未提取", recomputed == 0,
                $"placeholders=[{DescribeDiagnostics(diagnostics)}] recomputes={recomputed} "
                + $"immediate={main.Settings.AnalysisImmediateCompute}");
            bool anyShake = main.FolderVM.AllFiles.Any(file => file.IsShake == true);
            Check("SCOPE-03 清库后抖动徽标消失", !anyShake,
                $"shake={anyShake} flags=[{string.Join("/", main.FolderVM.AllFiles.Select(file => file.IsShake?.ToString() ?? "null"))}]");
        }
        finally
        {
            main.Settings.AnalysisImmediateCompute = previousImmediate;
            if (session.IsEnhanced)
            {
                session.ToggleEnhanced();
                await WaitAsync(() => !session.IsEnhanced, "关闭增强", 10000);
            }
            main.IsAnalysisViewVisible = previousVisible;
            await Task.Delay(300);
        }
    }

    /// <summary>
    /// STATE-04：特征库不可用时界面进入明确的「未提取」状态——不长期停在「计算中」、不崩溃、
    /// 不显示伪造分数或徽标。注入是真实的打开失败（把库路径指到普通文件之下，SQLite 打开即抛错），
    /// 不靠延时或吞异常冒充；收尾恢复原路径并核对库重新可用。
    /// </summary>
    private static async Task CheckDatabaseUnavailableAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var root = _root!;
        string source = EnsureFaultGoodJpeg(activity);
        string path = IOPath.Combine(IOPath.GetDirectoryName(source)!, "db-down.jpg");
        File.Copy(source, path, true);
        bool previousImmediate = main.Settings.AnalysisImmediateCompute;
        bool previousVisible = main.IsAnalysisViewVisible;
        string databasePath = PhotoDatabase.DatabasePath;
        string blocker = IOPath.Combine(EnsureFaultsFolder(activity), "db-blocker");
        File.WriteAllText(blocker, "blocked by STATE-04 check");
        string blockedPath = IOPath.Combine(blocker, "photos.db");
        try
        {
            SetDatabasePathForChecks(blockedPath);
            string? openError = null;
            try
            {
                using var unused = PhotoDatabase.OpenConnection();
            }
            catch (Exception exception)
            {
                openError = exception.GetType().Name + "：" + exception.Message;
            }
            Check("STATE-04 数据库不可用注入", openError != null, openError ?? "OpenConnection 未失败");

            // 即时计算关闭：库里读不到就停在「未提取」，而不是抢算或伪造结果。
            main.Settings.AnalysisImmediateCompute = false;
            main.IsAnalysisViewVisible = true;
            await main.FolderVM.OpenImageAsync(new AndroidFileRef(path));
            await WaitAsync(() => main.CurrentFile?.Name == "db-down.jpg" && session.Image != null, "数据库不可用素材上屏", 25000);

            var diagnostics = root.AnalysisPresenterForChecks.Tiles
                .Where(tile => tile.Kind == AnalysisTileKind.Diagnostic).ToList();
            bool computingSeen = false;
            bool settled = false;
            for (int sample = 0; sample < 80; sample++)
            {
                await Task.Delay(100);
                if (diagnostics.Any(tile => (tile.Placeholder ?? string.Empty).Contains("计算中", StringComparison.Ordinal)))
                    computingSeen = true;
                if (diagnostics.All(tile => tile.Placeholder == "未提取" && tile.Source == null))
                {
                    settled = true;
                    break;
                }
            }
            Check("STATE-04 诊断瓦片未提取且不停留计算中", settled && !computingSeen,
                $"settled={settled} computingSeen={computingSeen} placeholders=[{DescribeDiagnostics(diagnostics)}]");

            var file = main.CurrentFile;
            Check("STATE-04 无伪造徽标且未崩溃",
                session.Image != null && file != null && file.IsShake != true && !file.HasRatingConflict,
                $"image={session.Image?.Width}x{session.Image?.Height} shake={file?.IsShake?.ToString() ?? "null"} "
                + $"conflict={file?.HasRatingConflict}");

            // 相似面板同一失败路径：如实给未提取计数与空列表，不显示伪造分数。
            main.FileVM.FilterBar.IsSimilarityPanelOpen = true;
            await WaitAsync(() => main.FileVM.SimilarityPanel.UnindexedCount >= 1, "相似面板未提取计数", 15000);
            var panel = main.FileVM.SimilarityPanel;
            Check("STATE-04 相似面板如实未提取",
                panel.IndexError == null && panel.SimilarItems.Count == 0,
                $"unindexed={panel.UnindexedCount} items={panel.SimilarItems.Count} error={panel.IndexError ?? "none"} "
                + $"pane={root.SimilarityPane.Visibility}");
        }
        finally
        {
            SetDatabasePathForChecks(databasePath);
            bool restored = false;
            string restoreDetail;
            try
            {
                long photos = await PhotoDatabase.CountAsync();
                using var connection = PhotoDatabase.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM photos;";
                restored = command.ExecuteScalar() != null;
                restoreDetail = $"photos={photos}";
            }
            catch (Exception exception)
            {
                restoreDetail = exception.GetType().Name + "：" + exception.Message;
            }
            Check("STATE-04 收尾恢复可用库", restored, restoreDetail);
            main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
            main.Settings.AnalysisImmediateCompute = previousImmediate;
            main.IsAnalysisViewVisible = previousVisible;
            await Task.Delay(300);
        }
    }

    /// <summary>
    /// SCOPE-03 自建素材：两张自有 JPG（名称带时间戳，保证指纹组每轮都是新的、库里没有历史行）。
    /// </summary>
    /// <param name="activity">宿主 Activity（提供内部目录）。</param>
    /// <returns>目录与两个文件路径。</returns>
    private static (string Folder, string First, string Second) EnsureScopeFixture(Activity activity)
    {
        string folder = IOPath.Combine(activity.FilesDir!.AbsolutePath, "pv-check-scope");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string suffix = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        string first = IOPath.Combine(folder, $"scope-a-{suffix}.jpg");
        string second = IOPath.Combine(folder, $"scope-b-{suffix}.jpg");
        WriteCheckJpeg(first, 1200, 800);
        WriteCheckJpeg(second, 1200, 800);
        return (folder, first, second);
    }

    /// <summary>照片同名 <c>.xmp</c> sidecar 是否存在（SCOPE-03：显示态产物不写 XMP）。</summary>
    private static bool HasXmpSidecar(string photoPath) => File.Exists(IOPath.ChangeExtension(photoPath, ".xmp"));

    /// <summary>把诊断瓦片的占位/成图状态压成一行，供报告人工复核。</summary>
    private static string DescribeDiagnostics(IReadOnlyList<AnalysisTile> tiles)
        => string.Join("/", tiles.Select(tile => tile.Placeholder ?? (tile.Source != null ? "图" : "空")));

    /// <summary>查询某指纹在三张表里的行数（验收只读查询，走产品自己的连接入口）。</summary>
    private static async Task<(long Photos, long Features, long Patches)> CountFingerprintRowsAsync(string fingerprint)
    {
        await using var connection = PhotoDatabase.OpenConnection();
        async Task<long> CountAsync(string table)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE fingerprint = $fp;";
            command.Parameters.AddWithValue("$fp", fingerprint);
            var value = await command.ExecuteScalarAsync();
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        long photos = await CountAsync("photos");
        long features = await CountAsync("photo_features");
        long patches = await CountAsync("photo_patches");
        return (photos, features, patches);
    }

    /// <summary>
    /// 验收注入：把 <see cref="PhotoDatabase"/> 的库路径闸门改到指定路径。保持「已初始化」让
    /// <c>Initialize()</c> 的早退分支不再把路径改回默认值，后续每次打开都用注入路径（真实抛错）。
    /// </summary>
    /// <param name="databasePath">目标库路径。</param>
    private static void SetDatabasePathForChecks(string databasePath)
    {
        var pathField = typeof(PhotoDatabase).GetField("_dbPath", StaticNonPublic)
            ?? throw new InvalidOperationException("PhotoDatabase 缺少 _dbPath 字段");
        var initializedField = typeof(PhotoDatabase).GetField("_initialized", StaticNonPublic)
            ?? throw new InvalidOperationException("PhotoDatabase 缺少 _initialized 字段");
        pathField.SetValue(null, databasePath);
        initializedField.SetValue(null, true);
    }

    /// <summary>给可能长时间不返回的共享任务加上限等待：超时抛 <see cref="TimeoutException"/>，由步骤记 FAIL 而不挂住整套检查。</summary>
    /// <param name="task">被等待的任务。</param>
    /// <param name="label">超时说明（写进报告）。</param>
    /// <param name="timeoutMs">最长等待（ms）。</param>
    private static async Task AwaitOrTimeoutAsync(Task task, string label, int timeoutMs)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(true);
        if (!ReferenceEquals(completed, task)) throw new TimeoutException($"等待超时：{label}");
        await task.ConfigureAwait(true);
    }

    /// <summary>SCOPE-02：内存告警自动关闭增强预览，并用一次性提示条告知用户。</summary>
    private static async Task CheckMemoryTrimAsync()
    {
        var activity = _activity!;
        var main = _main!;
        var session = _session!;
        var root = _root!;
        string source = EnsureFaultGoodJpeg(activity);
        string path = IOPath.Combine(IOPath.GetDirectoryName(source)!, "memory-trim.jpg");
        File.Copy(source, path, true);
        await main.FolderVM.OpenImageAsync(new AndroidFileRef(path));
        await WaitAsync(() => main.CurrentFile?.Name == "memory-trim.jpg" && session.Image != null, "内存告警素材上屏", 20000);

        if (session.IsEnhanced)
        {
            session.ToggleEnhanced();
            await WaitAsync(() => !session.IsEnhanced, "关闭既有增强", 8000);
        }
        session.ToggleEnhanced();
        await WaitAsync(() => session.IsEnhanced, "开启增强", 10000);
        await WaitAsync(() => session.EnhancedDisplay, "增强图上屏", 45000);

        string? notice = null;
        void OnNotice(string text) => notice = text;
        session.Notice += OnNotice;
        try
        {
            root.ResetStatusForChecks();
            session.Trim(PressureLevel.Warning);
            await WaitAsync(() => !session.IsEnhanced && notice != null, "内存告警关闭增强", 15000);
            Check("SCOPE-02 内存告警关闭增强并提示",
                notice == StatusMessages.EnhanceClosedForMemory
                && root.StatusVisibleForChecks && root.StatusTextForChecks == StatusMessages.EnhanceClosedForMemory,
                $"notice={notice} enhanced={session.IsEnhanced} visible={root.StatusVisibleForChecks} status={root.StatusTextForChecks}");
        }
        finally
        {
            session.Notice -= OnNotice;
        }
    }

    /// <summary>OPEN-06：ACTION_VIEW 外部打开要经请求通道定位到目标文件（与 SAF/本地路径同一 StorageId 口径）。</summary>
    private static async Task CheckExternalOpenAsync()
    {
        var activity = _activity!;
        var main = _main!;
        string path = EnsureFaultGoodJpeg(activity);
        string target = new AndroidFileRef(path).StorageId.Value;
        // 新链路 Intent → OpenRequestChannel → OpenCoordinator：等待本次请求处理完再断言，不靠固定延时。
        await AndroidNativeShell.HandleIntentAsync(new Intent(Intent.ActionView, AndroidUri.FromFile(new JavaFile(path))!));
        Check("OPEN-06 外部打开定位", main.CurrentFile?.File.StorageId.Value == target,
            $"current={main.CurrentFile?.File.StorageId.Value} expected={target}");
    }

    /// <summary>把截图/素材目录权限改成只读或可写（评分写回失败的注入手段）。</summary>
    /// <param name="path">文件全路径。</param>
    /// <param name="readOnly">true 设只读。</param>
    private static void SetFileReadOnly(string path, bool readOnly)
        => File.SetUnixFileMode(path, readOnly
            ? UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

    /// <summary>把目录权限改成只读或可写。</summary>
    /// <param name="path">目录全路径。</param>
    /// <param name="readOnly">true 设只读。</param>
    private static void SetDirectoryReadOnly(string path, bool readOnly)
        => File.SetUnixFileMode(path, readOnly
            ? UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

    /// <summary>按名字写共享 VM 的私有 setter（验收注入用；不新增产品侧公开 API）。</summary>
    /// <param name="target">目标对象。</param>
    /// <param name="name">属性名。</param>
    /// <param name="value">新值。</param>
    private static void SetViewModelProperty(object target, string name, object? value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"缺少属性 {name}");
        property.SetValue(target, value);
    }

    /// <summary>只在全分辨率解码（代理长边为 null）时失败的解码器替身。</summary>
    private sealed class FullFailingDecoder(IPresentationImageDecoder inner) : IPresentationImageDecoder
    {
        /// <summary>代理转发；目标文件的全分辨率解码直接失败。</summary>
        public Task<DecodedImage?> DecodeAsync(IFileRef file, int? maxLongEdge, PixelEncoding encoding, CancellationToken cancellationToken)
            => maxLongEdge == null && file.Name.Contains("fault-full-fail", StringComparison.Ordinal)
                ? Task.FromException<DecodedImage?>(new InvalidOperationException("injected full decode failure"))
                : inner.DecodeAsync(file, maxLongEdge, encoding, cancellationToken);

        /// <summary>缩略图直接转发。</summary>
        public Task<DecodedImage?> DecodeThumbnailAsync(IFileRef file, int minShortEdge, CancellationToken cancellationToken)
            => inner.DecodeThumbnailAsync(file, minShortEdge, cancellationToken);
    }

    /// <summary>父目录固定的文件引用：把 <c>GetParentAsync</c> 指向一个枚举必失败的文件夹。</summary>
    private sealed class ParentFailingFileRef(IFileRef inner, IFolderRef parent) : IFileRef
    {
        /// <summary>显示名。</summary>
        public string Name => inner.Name;
        /// <summary>平台路径。</summary>
        public Uri Path => inner.Path;
        /// <summary>本地路径。</summary>
        public string? TryGetLocalPath() => inner.TryGetLocalPath();
        /// <summary>读取流。</summary>
        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => inner.OpenReadAsync(cancellationToken);
        /// <summary>基本属性。</summary>
        public Task<StorageItemBasicProperties> GetBasicPropertiesAsync(CancellationToken cancellationToken = default)
            => inner.GetBasicPropertiesAsync(cancellationToken);
        /// <summary>始终返回注入的失败父目录。</summary>
        public Task<IFolderRef?> GetParentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IFolderRef?>(parent);
    }

    /// <summary>枚举必然抛异常的文件夹引用（STATE-02 / OPEN-03 注入）。</summary>
    private sealed class FailingFolderRef(string name, string path) : IFolderRef
    {
        /// <summary>文件夹名（打开失败文案的对象）。</summary>
        public string Name => name;
        /// <summary>平台路径。</summary>
        public Uri Path { get; } = new(path);
        /// <summary>本地路径。</summary>
        public string? TryGetLocalPath() => path;

        /// <summary>枚举直接抛 <see cref="UnauthorizedAccessException"/>。</summary>
        public async IAsyncEnumerable<IStorageItemRef> GetItemsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new UnauthorizedAccessException("injected folder enumeration failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
