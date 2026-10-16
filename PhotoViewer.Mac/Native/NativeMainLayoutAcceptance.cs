using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Mac.Native;

/// <summary>真实窗口主界面布局验收：分区拼合、筛选条、胶片栏卡片、控制栏分组与主图导航几何。</summary>
internal static class NativeMainLayoutAcceptance
{
    /// <summary>代码内固定几何允许的误差。</summary>
    private const double Exact = .01;
    /// <summary>三种参考窗口尺寸，覆盖两向布局的宽窄两端。</summary>
    private static readonly CGSize[] WindowSizes = [new(1200, 850), new(830, 650), new(700, 480)];

    /// <summary>在隔离设置与只读素材下核对两向布局的三种窗口尺寸，输出实际几何，结束后恢复设置工作副本。</summary>
    internal static async Task RunAsync(MacMainWindow window, string output)
    {
        var settings = window.Main.Settings;
        var snapshot = settings.CreateSnapshot();
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
            // 旧壳基线（LegacyLayoutAcceptance）把相似聚类面板设为收起，原生侧必须同口径，
            // 否则文件栏宽度会多出面板的交叉轴长度，整组几何对不上。
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
            await VerifyCommandButtonsAsync(window, observations);
            observations.Add("空控制组状态未在本验收改写：无分组时 MacControlBar.Measure 的 legacy 20 点外边距保持原样，仍由 --pv-native-toolbar 覆盖");
        }
        finally
        {
            settings.ApplySnapshot(snapshot);
            window.Main.IsThumbnailViewVisible = filmVisible;
            window.Main.IsControlViewVisible = controlVisible;
            window.Main.CurrentFile = first;
            await Task.Delay(250);
            await File.WriteAllLinesAsync(System.IO.Path.Combine(output, "native-layout-geometry.txt"), geometry);
            await File.WriteAllLinesAsync(System.IO.Path.Combine(output, "native-layout-checks.txt"), observations);
        }
    }

    /// <summary>核对分区首尾相接、筛选条间距居中、胶片栏卡片几何与控制栏分组，并输出实际几何。</summary>
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
        if (controlIndex < 1 || controlIndex + 2 >= root.Subviews.Length) throw new InvalidOperationException($"{scope} 根视图分区顺序改变：控制栏索引 {controlIndex}");
        var fileDivider = root.Subviews[controlIndex + 1];
        var controlDivider = root.Subviews[controlIndex + 2];

        Record(geometry, scope, "root", root.Bounds);
        foreach (var view in root.Subviews)
        {
            int index = Array.IndexOf(root.Subviews, view);
            string role = ReferenceEquals(view, filePane) ? "filePane"
                : ReferenceEquals(view, photoPane) ? "photoPane"
                : ReferenceEquals(view, controlScroll) ? "controlScroll"
                : ReferenceEquals(view, fileDivider) ? "fileDivider"
                : ReferenceEquals(view, controlDivider) ? "controlDivider" : $"overlay{index}";
            Record(geometry, scope, "root." + role, view.Frame);
        }
        Record(geometry, scope, "photoViewport", window.Photo.ConvertRectToView(window.Photo.Bounds, root));

        double rootWidth = (double)root.Bounds.Width, rootHeight = (double)root.Bounds.Height;
        (NSView View, string Name)[] partitions =
        [
            (filePane, "文件栏"), (fileDivider, "文件栏分隔线"), (photoPane, "观片区"),
            (controlDivider, "控制栏分隔线"), (controlScroll, "控制栏"),
        ];
        double cursor = 0;
        foreach (var partition in partitions)
        {
            var frame = partition.View.Frame;
            double start = row ? (double)frame.Y : (double)frame.X;
            double length = row ? (double)frame.Height : (double)frame.Width;
            double crossStart = row ? (double)frame.X : (double)frame.Y;
            double crossLength = row ? (double)frame.Width : (double)frame.Height;
            if (Math.Abs(start - cursor) > Exact) throw new InvalidOperationException($"{scope} {partition.Name} 未首尾相接，出现空带或重叠：{frame}");
            if (length <= 0) throw new InvalidOperationException($"{scope} {partition.Name} 被压成零尺寸");
            if (Math.Abs(crossStart) > Exact || Math.Abs(crossLength - (row ? rootWidth : rootHeight)) > Exact)
                throw new InvalidOperationException($"{scope} {partition.Name} 未铺满交叉轴：{frame}");
            cursor += length;
        }
        if (Math.Abs(cursor - (row ? rootHeight : rootWidth)) > .5) throw new InvalidOperationException($"{scope} 分区总长不等于窗口主轴，存在额外空带");
        RequireFrame(window.Photo.Frame, photoPane.Bounds, $"{scope} 主图表面未铺满观片区（出现空带）", .5);

        VerifyControlBar(window, scope, geometry);
        VerifyFilterBar(row, scope, filePane, geometry);
        await VerifyFilmstripAsync(window, row, scope, filePane, geometry);
        observations.Add($"{scope}: 分区拼合无空带/重叠、主图铺满；筛选条 5 点间距与居中；胶片栏步长与卡片 96×144/(3,3,90,138)/图片 80 方形；控制栏 3 组文档覆盖视口");
    }

    /// <summary>核对控制栏文档覆盖裁剪视口、三组不重叠且保留 10 点外边距，并输出分组与子项几何。</summary>
    private static void VerifyControlBar(MacMainWindow window, string scope, List<string> geometry)
    {
        var bar = window.ControlBar;
        var clip = window.ControlScroll.ContentView.Bounds;
        Record(geometry, scope, "controlClip", clip);
        Record(geometry, scope, "controlDocument", bar.Frame);
        if ((double)bar.Frame.Width + Exact < (double)clip.Width || (double)bar.Frame.Height + Exact < (double)clip.Height)
            throw new InvalidOperationException($"{scope} 控制栏文档小于裁剪视口，右侧/底部出现空带：{bar.Frame}/{clip}");
        if (bar.Groups.Count != 3 || bar.Ratings.Count != 6 || bar.Commands.Count != 6)
            throw new InvalidOperationException($"{scope} 控制栏未保留 EXIF/评分/命令三个完整分组：{bar.Groups.Count}/{bar.Ratings.Count}/{bar.Commands.Count}");
        foreach (var command in new[] { "Previous", "Next", "Exchange", "Fit", "ZoomInPreset", "ZoomOutPreset" })
            if (!bar.Commands.ContainsKey(command)) throw new InvalidOperationException($"{scope} 控制栏缺少命令 {command}");
        int[] children = [4, 6, 6];
        for (int index = 0; index < bar.Groups.Count; index++)
        {
            var group = bar.Groups[index];
            Record(geometry, scope, $"control.group{index}", group.Frame);
            if (group.Subviews.Length != children[index]) throw new InvalidOperationException($"{scope} 控制组 {index} 子项数量为 {group.Subviews.Length}，预期 {children[index]}");
            for (int child = 0; child < group.Subviews.Length; child++)
                Record(geometry, scope, $"control.group{index}.child{child}", group.Subviews[child].Frame);
            var frame = group.Frame;
            if ((double)frame.X < 10 - Exact || (double)frame.Y < 10 - Exact ||
                (double)frame.X + (double)frame.Width > (double)bar.Bounds.Width - 10 + Exact ||
                (double)frame.Y + (double)frame.Height > (double)bar.Bounds.Height - 10 + Exact)
                throw new InvalidOperationException($"{scope} 控制组未保留 10 点外边距：{frame}/{bar.Bounds}");
            foreach (var other in bar.Groups.Where(other => other != group))
                if (frame.IntersectsWith(other.Frame)) throw new InvalidOperationException($"{scope} 控制组重叠：{frame} / {other.Frame}");
        }
    }

    /// <summary>核对筛选条 90×32、相似聚类 toggle 90×27、5 点间距与居中（左右布局中心 58、首控件顶边 34，上下布局贴顶 10）。</summary>
    private static void VerifyFilterBar(bool row, string scope, NSView filePane, List<string> geometry)
    {
        var popups = filePane.Subviews.OfType<NSPopUpButton>().ToArray();
        var count = filePane.Subviews.OfType<NSTextField>().Single();
        var toggle = filePane.Subviews.OfType<NSButton>().Single(button => button is not NSPopUpButton);
        if (popups.Length != 3) throw new InvalidOperationException($"{scope} 筛选下拉框数量为 {popups.Length}，预期 3");
        foreach (var popup in popups)
            if (Math.Abs((double)popup.Frame.Width - 90) > Exact || Math.Abs((double)popup.Frame.Height - 32) > Exact)
                throw new InvalidOperationException($"{scope} 筛选控件未保持 90×32：{popup.Frame}");
        if (Math.Abs((double)toggle.Frame.Width - 90) > Exact || Math.Abs((double)toggle.Frame.Height - 27) > Exact)
            throw new InvalidOperationException($"{scope} 相似聚类 toggle 未保持 90×27：{toggle.Frame}");
        Record(geometry, scope, "filter.sort", popups[0].Frame);
        Record(geometry, scope, "filter.order", popups[1].Frame);
        Record(geometry, scope, "filter.rating", popups[2].Frame);
        Record(geometry, scope, "filter.count", count.Frame);
        Record(geometry, scope, "filter.toggle", toggle.Frame);
        double paneWidth = (double)filePane.Frame.Width;
        if (row)
        {
            if (Math.Abs((double)toggle.Frame.X - (double)count.Frame.X - (double)count.Frame.Width - 5) > Exact)
                throw new InvalidOperationException($"{scope} 相似聚类 toggle 未紧随计数 5 点：{toggle.Frame}");
            foreach (var popup in popups)
                if (Math.Abs((double)popup.Frame.Y - 10) > Exact) throw new InvalidOperationException($"{scope} 上下布局筛选条未贴顶 10 点：{popup.Frame}");
            if (Math.Abs((double)popups[1].Frame.X - (double)popups[0].Frame.X - 95) > Exact ||
                Math.Abs((double)popups[2].Frame.X - (double)popups[1].Frame.X - 95) > Exact ||
                Math.Abs((double)count.Frame.X - (double)popups[2].Frame.X - 95) > Exact)
                throw new InvalidOperationException($"{scope} 筛选控件间距不是 5 点（应保持 95 点节距）");
            double center = ((double)popups[0].Frame.X + (double)toggle.Frame.X + (double)toggle.Frame.Width) / 2;
            if (Math.Abs(center - paneWidth / 2) > .5) throw new InvalidOperationException($"{scope} 筛选条未水平居中：{center}/{paneWidth}");
            if (Math.Abs((double)count.Frame.Y + (double)count.Frame.Height / 2 - (double)popups[0].Frame.Y - (double)popups[0].Frame.Height / 2) > .5 ||
                Math.Abs((double)toggle.Frame.Y + (double)toggle.Frame.Height / 2 - (double)popups[0].Frame.Y - (double)popups[0].Frame.Height / 2) > .5)
                throw new InvalidOperationException($"{scope} 计数/相似聚类 toggle 未与筛选控件垂直对齐：{count.Frame}/{toggle.Frame}");
            return;
        }
        if (Math.Abs(paneWidth - 116) > .5) throw new InvalidOperationException($"{scope} 左右布局胶片栏栏宽不是 116 点：{paneWidth}");
        foreach (var popup in popups)
            if (Math.Abs((double)popup.Frame.X - (paneWidth - 90) / 2) > Exact)
                throw new InvalidOperationException($"{scope} 左右布局筛选条未在 116 栏内居中：{popup.Frame}");
        if (Math.Abs((double)popups[0].Frame.Y - 34) > Exact) throw new InvalidOperationException($"{scope} 左右布局首控件未从 34 点起：{popups[0].Frame}");
        if (Math.Abs((double)popups[1].Frame.Y - (double)popups[0].Frame.Y - 37) > Exact ||
            Math.Abs((double)popups[2].Frame.Y - (double)popups[1].Frame.Y - 37) > Exact ||
            Math.Abs((double)count.Frame.Y - (double)popups[2].Frame.Y - 37) > Exact ||
            Math.Abs((double)toggle.Frame.Y - (double)count.Frame.Y - (double)count.Frame.Height - 5) > Exact)
            throw new InvalidOperationException($"{scope} 筛选控件纵向间距不是 5 点（应保持 37 点节距）");
        double popupCenter = (double)popups[0].Frame.X + (double)popups[0].Frame.Width / 2;
        double countCenter = (double)count.Frame.X + (double)count.Frame.Width / 2;
        double toggleCenter = (double)toggle.Frame.X + (double)toggle.Frame.Width / 2;
        if (Math.Abs(popupCenter - 58) > Exact || Math.Abs(countCenter - 58) > Exact || Math.Abs(toggleCenter - 58) > Exact)
            throw new InvalidOperationException($"{scope} 左右布局筛选条/计数/toggle 中心不是 58：{popupCenter}/{countCenter}/{toggleCenter}");
    }

    /// <summary>核对胶片栏步长与内边距、卡片 96×144 内部结构，并输出前两张卡片的实际几何。</summary>
    private static async Task VerifyFilmstripAsync(MacMainWindow window, bool row, string scope, NSView filePane, List<string> geometry)
    {
        if (MacFilmstripLayout.CardWidth != 96 || MacFilmstripLayout.CardHeight != 144 ||
            MacFilmstripLayout.HorizontalPadding != 10 || MacFilmstripLayout.TopPadding != 5 || MacFilmstripLayout.BottomPadding != 10)
            throw new InvalidOperationException($"{scope} 胶片栏不再使用已验证的 96×144 卡片与 10/5/10 内边距");
        var scroll = window.FilmstripScroll;
        var layout = window.Filmstrip.CollectionViewLayout as MacFilmstripLayout ?? throw new InvalidOperationException($"{scope} 胶片栏布局类型改变");
        if (layout.Horizontal != row) throw new InvalidOperationException($"{scope} 胶片栏方向未跟随布局");
        var popups = filePane.Subviews.OfType<NSPopUpButton>().ToArray();
        double filterBottom = Math.Max(popups.Max(popup => (double)popup.Frame.Y + (double)popup.Frame.Height),
            filePane.Subviews.OfType<NSTextField>().Max(label => (double)label.Frame.Y + (double)label.Frame.Height));
        filterBottom = Math.Max(filterBottom, filePane.Subviews.OfType<NSButton>().Where(button => button is not NSPopUpButton).Max(button => (double)button.Frame.Y + (double)button.Frame.Height));
        if ((double)scroll.Frame.Y < filterBottom - Exact) throw new InvalidOperationException($"{scope} 筛选条与胶片栏重叠：{scroll.Frame}");
        if (Math.Abs((double)scroll.Frame.Y + (double)scroll.Frame.Height - (double)filePane.Frame.Height) > .5)
            throw new InvalidOperationException($"{scope} 胶片栏未填满文件栏剩余空间，出现空带：{scroll.Frame}/{filePane.Frame}");
        if (Math.Abs((double)scroll.Frame.X) > Exact || Math.Abs((double)scroll.Frame.Width - (double)filePane.Frame.Width) > .5)
            throw new InvalidOperationException($"{scope} 胶片栏裁剪区未铺满文件栏：{scroll.Frame}");
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
        double step = row ? MacFilmstripLayout.CardWidth : MacFilmstripLayout.CardHeight;
        double leading = row ? MacFilmstripLayout.HorizontalPadding : MacFilmstripLayout.TopPadding;
        double trailing = row ? MacFilmstripLayout.HorizontalPadding : MacFilmstripLayout.BottomPadding;
        double contentLength = count * step + leading + trailing;
        double documentMain = row ? (double)document.Width : (double)document.Height;
        double viewportMain = row ? (double)clip.Width : (double)clip.Height;
        if (Math.Abs(documentMain - Math.Max((double)viewportMain, contentLength)) > .5)
            throw new InvalidOperationException($"{scope} 胶片栏文档主轴长度错误：{documentMain}");
        using var firstPath = NSIndexPath.FromItemSection(0, 0);
        using var secondPath = NSIndexPath.FromItemSection(1, 0);
        var firstCard = layout.GetLayoutAttributesForItem(firstPath)?.Frame ?? throw new InvalidOperationException($"{scope} 首卡几何缺失");
        var secondCard = layout.GetLayoutAttributesForItem(secondPath)?.Frame ?? throw new InvalidOperationException($"{scope} 第二卡几何缺失");
        double expectedLeft = row ? Math.Max(10, ((double)document.Width - count * 96) / 2) : 10;
        RequireFrame(firstCard, new CGRect(expectedLeft, 5, 96, 144), $"{scope} 首卡位置与卡片尺寸");
        double cardStep = row ? (double)(secondCard.X - firstCard.X) : (double)(secondCard.Y - firstCard.Y);
        if (Math.Abs(cardStep - step) > Exact) throw new InvalidOperationException($"{scope} 胶片栏步长错误：{cardStep} != {step}");
        if (row ? Math.Abs((double)(secondCard.Y - firstCard.Y)) > Exact : Math.Abs((double)(secondCard.X - firstCard.X)) > Exact)
            throw new InvalidOperationException($"{scope} 胶片栏出现换行或换列：{firstCard} / {secondCard}");
        double crossTail = row ? (double)scroll.Frame.Height - ((double)firstCard.Y + (double)firstCard.Height)
            : (double)scroll.Frame.Width - ((double)firstCard.X + (double)firstCard.Width);
        if (Math.Abs(crossTail - 10) > Exact) throw new InvalidOperationException($"{scope} 胶片栏交叉轴内边距不是 10 点：{crossTail}");
        if (contentLength > (double)viewportMain + .5)
        {
            using var lastPath = NSIndexPath.FromItemSection(count - 1, 0);
            var lastCard = layout.GetLayoutAttributesForItem(lastPath)?.Frame ?? throw new InvalidOperationException($"{scope} 末卡几何缺失");
            double tail = documentMain - (row ? (double)lastCard.X + (double)lastCard.Width : (double)lastCard.Y + (double)lastCard.Height);
            if (Math.Abs(tail - trailing) > .5) throw new InvalidOperationException($"{scope} 胶片栏末端内边距错误：{tail} != {trailing}");
        }
        foreach (int index in new[] { 0, 1 })
        {
            using var path = NSIndexPath.FromItemSection(index, 0);
            var expected = layout.GetLayoutAttributesForItem(path)?.Frame ?? throw new InvalidOperationException($"{scope} 第 {index} 卡几何缺失");
            if (window.Filmstrip.GetItem(path) is not MacThumbnailItem card) throw new InvalidOperationException($"{scope} 第 {index} 卡视图缺失");
            if (!ReferenceEquals(card.BoundFile, window.Main.FileVM.ThumbnailList.FilteredFiles[index]))
                throw new InvalidOperationException($"{scope} 第 {index} 卡绑定身份与业务顺序不同");
            RequireFrame(card.View.Frame, expected, $"{scope} 第 {index} 卡视图未落在布局位置", .5);
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

    /// <summary>隐藏胶片栏/控制栏分别核对释放主图对应轴空间，恢复后分区回到原值。</summary>
    private static async Task VerifyPaneReleaseAsync(MacMainWindow window, LayoutMode mode, List<string> observations)
    {
        bool row = mode == LayoutMode.Row;
        var root = window.Window.ContentView ?? throw new InvalidOperationException("原生内容视图缺失");
        var photoPane = root.Subviews.Single(view => view.Subviews.Contains(window.Photo));
        var filmPane = root.Subviews.Single(view => view.Subviews.Contains(window.FilmstripScroll));
        await Task.Delay(200);
        var beforeFilm = photoPane.Frame;
        double filmBand = row ? (double)beforeFilm.Y : (double)beforeFilm.X;
        if (filmBand <= 0) throw new InvalidOperationException($"{mode} 主图旁没有可释放的胶片栏分区：{beforeFilm}");
        window.Main.IsThumbnailViewVisible = false;
        await Task.Delay(300);
        if (!filmPane.Hidden || ((double)filmPane.Frame.Width > .5 && (double)filmPane.Frame.Height > .5))
            throw new InvalidOperationException($"{mode} 隐藏胶片栏未收起文件栏：{filmPane.Frame}");
        RequireSize(photoPane.Frame, beforeFilm, filmBand, row, $"{mode} 隐藏胶片栏未释放主图{(row ? "上方" : "左侧")}空间");
        observations.Add($"{mode}: 隐藏胶片栏释放{(row ? "上" : "左")}侧 {filmBand:0.##} 点，主图分区 {photoPane.Frame}");
        window.Main.IsThumbnailViewVisible = true;
        await Task.Delay(300);
        RequireFrame(photoPane.Frame, beforeFilm, $"{mode} 恢复胶片栏后主图分区未还原", .5);

        var beforeControl = photoPane.Frame;
        double controlBand = row ? (double)root.Bounds.Height - ((double)beforeControl.Y + (double)beforeControl.Height)
            : (double)root.Bounds.Width - ((double)beforeControl.X + (double)beforeControl.Width);
        if (controlBand <= 0) throw new InvalidOperationException($"{mode} 主图旁没有可释放的控制栏分区：{beforeControl}");
        window.Main.IsControlViewVisible = false;
        await Task.Delay(300);
        if (!window.ControlScroll.Hidden) throw new InvalidOperationException($"{mode} 隐藏控制栏未收起控制分区");
        RequireSize(photoPane.Frame, beforeControl, controlBand, row, $"{mode} 隐藏控制栏未释放主图{(row ? "下方" : "右侧")}空间", false);
        observations.Add($"{mode}: 隐藏控制栏释放{(row ? "下" : "右")}侧 {controlBand:0.##} 点，主图分区 {photoPane.Frame}");
        window.Main.IsControlViewVisible = true;
        await Task.Delay(300);
        RequireFrame(photoPane.Frame, beforeControl, $"{mode} 恢复控制栏后主图分区未还原", .5);
    }

    /// <summary>核对自动布局随窗口宽高方向切换上下/左右分栏，再回到横向。</summary>
    private static async Task VerifyAutoOrientationAsync(MacMainWindow window, List<string> geometry, List<string> observations)
    {
        var root = window.Window.ContentView ?? throw new InvalidOperationException("原生内容视图缺失");
        var photoPane = root.Subviews.Single(view => view.Subviews.Contains(window.Photo));
        window.Main.Settings.LayoutMode = LayoutMode.Auto;
        await SettleAsync(window, new CGSize(1200, 850));
        if (window.Main.IsRowLayout || (double)photoPane.Frame.X <= 0 || (double)photoPane.Frame.Y > Exact)
            throw new InvalidOperationException($"自动布局在横向窗口未选择左右分栏：{photoPane.Frame}");
        Record(geometry, "Auto-1200x850", "photoPane", photoPane.Frame);
        await SettleAsync(window, new CGSize(700, 850));
        if (!window.Main.IsRowLayout || (double)photoPane.Frame.Y <= 0 || (double)photoPane.Frame.X > Exact)
            throw new InvalidOperationException($"自动布局在纵向窗口未选择上下分栏：{photoPane.Frame}");
        Record(geometry, "Auto-700x850", "photoPane", photoPane.Frame);
        await SettleAsync(window, new CGSize(850, 700));
        if (window.Main.IsRowLayout || (double)photoPane.Frame.X <= 0)
            throw new InvalidOperationException($"自动布局回到横向窗口未切回左右分栏：{photoPane.Frame}");
        Record(geometry, "Auto-850x700", "photoPane", photoPane.Frame);
        observations.Add("自动布局：横向窗口=左右分栏，纵向窗口=上下分栏，回到横向=左右分栏");
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
