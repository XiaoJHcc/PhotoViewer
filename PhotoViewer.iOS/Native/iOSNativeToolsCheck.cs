using CoreGraphics;
using Foundation;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 验收专用（非产品行为，测试钩子）：<c>--pv-native-tools-check &lt;目录&gt;</c> 在模拟器/真机上程序化核对
/// 工具页——sheet + 导航控制器展示/关闭、列表页两个入口（照片数据统计为 Windows 专属，不出现）、
/// EXIF 详情页按共享 7 列几何渲染分组与标签（多格式时真实切换格式），并在 320–1024 点七档宽度下核对卡片
/// 内缩只取系统 InsetGrouped 一次边距、7 列与行高按实际卡片宽重算、行内无自绘圆角；当前发行版的诊断页
/// （AI 版 DINO 五瓦片、标准版 CV 三瓦片）与共享 presenter 就绪、返回列表页，以及 EXIF 信息块直达
/// EXIF 详情（宽形态取控制栏 EXIF 组、手机形态取手机 EXIF 块，同一组件；工具 sheet 与设置页同配置）。
/// 素材要求：目录内至少 1 张自有 jpg 且能读到 EXIF；只读素材，不写用户照片或 TestCase。
/// 报告写 Documents/PhotoViewer/native-tools-checks.txt。
/// </summary>
internal static class iOSNativeToolsCheck
{
    /// <summary>验收开关。</summary>
    private const string Switch = "--pv-native-tools-check";

    /// <summary>布局/动画沉淀等待。</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>解析验收开关；命中即以给定目录启动异步核对。</summary>
    /// <param name="args">进程启动参数。</param>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    public static void MaybeStart(string[] args, iOSNativeRootViewController root, PhotoLoadSession session)
    {
        string? directory = null;
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], Switch, StringComparison.Ordinal)) directory = args[index + 1];
        }
        if (directory == null) return;
        _ = RunAsync(root, session, ResolveDocumentsPath(directory));
    }

    /// <summary>相对路径按沙盒 Documents 解析（iOS 无启动工作目录语义）。</summary>
    /// <param name="path">绝对或相对路径。</param>
    /// <returns>绝对路径。</returns>
    private static string ResolveDocumentsPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);

    /// <summary>核对主流程：等主屏与素材就绪 → 逐项断言 → 落盘并打印结论。</summary>
    /// <param name="root">原生根控制器。</param>
    /// <param name="session">原生展示会话。</param>
    /// <param name="directory">自有夹具目录（绝对路径）。</param>
    private static async Task RunAsync(iOSNativeRootViewController root, PhotoLoadSession session, string directory)
    {
        var lines = new List<string>();
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "native-tools-checks.txt");
        var presenter = iOSNativeToolsPresenter.Current
            ?? throw new InvalidOperationException("原生工具页宿主未初始化（壳启动顺序异常）");
        try
        {
            var main = iOSNativeShell.Main ?? throw new InvalidOperationException("原生壳主 VM 未就绪");
            if (!Path.GetFullPath(directory).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.Ordinal))
                throw new InvalidOperationException($"工具验收只允许使用 App 沙盒内的自有夹具目录：{directory}");
            string[] fixtures = Directory.GetFiles(directory, "*.jpg");
            if (fixtures.Length == 0) throw new InvalidOperationException($"工具验收需要自有 jpg 夹具：{directory}");
            await WaitAsync(() => root.View!.Bounds.Width > 100 && root.Photo.Bounds.Width > 100, "主屏布局");
            var mainVm = main;
            bool entryFilm = mainVm.IsThumbnailViewVisible, entryControl = mainVm.IsControlViewVisible;
            bool entryExifVisible = mainVm.IsExifViewVisible;
            bool[] entryExif = mainVm.Settings.ExifDisplayItems.Select(item => item.IsEnabled).ToArray();
            await UiDispatcher.InvokeAsync(() =>
            {
                mainVm.IsThumbnailViewVisible = true;
                mainVm.IsControlViewVisible = true;
                // 手机形态的 EXIF 块只由手机 EXIF 开关与显示项共同决定：验收入口需要开关打开。
                mainVm.IsExifViewVisible = true;
                mainVm.Settings.SameNameAsOnePhoto = true;
                foreach (var format in mainVm.Settings.FileFormats)
                    if (format.DisplayName is "JPG" or "RAW") format.IsEnabled = true;
                // EXIF 信息块入口验收需要控制栏存在该分组：用户可能隐藏了全部 EXIF 项，验收会话临时打开第一项。
                if (mainVm.Settings.ExifDisplayItems.All(item => !item.IsEnabled))
                    mainVm.Settings.ExifDisplayItems[0].IsEnabled = true;
                root.BuildControls();
            });
            await mainVm.FolderVM.OpenFolderAsync(new LocalFolderRef(directory));
            await WaitAsync(() => mainVm.CurrentFile != null && session.Image != null, "首图解码");
            await WaitAsync(() => mainVm.CurrentFile?.ExifData != null, "EXIF 读入");
            await WaitAsync(() => mainVm.Tools.IsExifDetailEnabled, "EXIF 详情可用");
            await SettleAsync(root);
            // 同名 RAW+JPG 的伴侣关系在筛选阶段建立；真实使用中切换一次照片会重建 EXIF 详情 VM，
            // 这里走同一条共享路径（CurrentFile 变化 → Tools.SyncCurrentFile），再切回目标照片。
            // 同名 RAW+JPG 的伴侣关系在筛选阶段建立到代表项上；真实用户点选的是列表里的代表卡片，
            // 这里同样把当前照片切到带伴侣的代表项（共享 CurrentFile → Tools.SyncCurrentFile 同步建 VM）。
            await UiDispatcher.InvokeAsync(() => mainVm.FileVM.ThumbnailList.ApplyFilter());
            var representative = mainVm.FileVM.ThumbnailList.FilteredFiles.FirstOrDefault(file => (file.Group?.Companions.Count ?? 0) > 0);
            if (representative != null && !ReferenceEquals(mainVm.CurrentFile, representative))
            {
                await UiDispatcher.InvokeAsync(() => mainVm.CurrentFile = representative);
                await WaitAsync(() => ReferenceEquals(mainVm.CurrentFile, representative), "切到同名代表项");
            }
            await WaitAsync(() => mainVm.Tools.IsExifDetailEnabled, "EXIF 详情重建");
            await SettleAsync(root);
            lines.Add($"NOTE 同名合并={mainVm.Settings.SameNameAsOnePhoto}，筛选后 {mainVm.FileVM.ThumbnailList.FilteredFiles.Count} 张，" +
                $"当前照片伴侣 {mainVm.CurrentFile!.Group?.Companions.Count ?? 0}，格式 {mainVm.Tools.ExifDetail?.Formats.Count ?? 0}");
            lines.Add($"PASS 素材：{fixtures.Length} 张自有夹具（{directory}），列表 {mainVm.FileVM.ThumbnailList.FilteredFiles.Count} 张，" +
                $"当前照片 {mainVm.CurrentFile!.Name}（伴侣 {mainVm.CurrentFile.Group?.Companions.Count ?? 0}）");

            // 一、EXIF 信息块直达 EXIF 详情（另一条入口，与主图菜单共用同一个展示器）。
            // 宽形态取控制栏 EXIF 组，手机形态取手机 EXIF 块（同一组件），两种形态都必须直达详情。
            if (root.Form == ScreenForm.Wide)
            {
                if (root.ControlBar.ExifGroup == null) throw new InvalidOperationException("控制栏缺少 EXIF 信息块分组");
            }
            else if (root.PhoneExifBlock.Hidden || root.PhoneExifBlock.ItemLabels.Count == 0)
            {
                throw new InvalidOperationException("手机形态 EXIF 块不可见或没有显示项");
            }
            if (presenter.IsPresented) throw new InvalidOperationException("工具页在入口调用前已展示");
            await UiDispatcher.InvokeAsync(() => TapExifEntry(root));
            await WaitAsync(() => presenter.IsPresented && root.PresentedViewController is UINavigationController, "控制栏 EXIF 块打开工具页并压入 EXIF 详情");
            // TOOLS-01：展示时写共享展示态，主窗口入口据此只前置。
            if (!mainVm.Tools.IsPresented) throw new InvalidOperationException("工具页展示后共享 ToolsViewModel.IsPresented 仍为 false");
            var navigation = presenter.Navigation ?? throw new InvalidOperationException("工具页缺少导航控制器");
            // TOOLS-01：工具 sheet 与设置页同一份原生配置（detents/grabber/圆角/滚动扩展策略）。
            VerifySheetMatchesSettings(navigation, "首次展示");
            await WaitAsync(() => navigation.TopViewController is iOSExifDetailView && navigation.ViewIfLoaded?.Window != null,
                "控制栏 EXIF 块直达 EXIF 详情");
            if (mainVm.Tools.CurrentTool is not ExifDetailViewModel)
                throw new InvalidOperationException("控制栏 EXIF 块进入详情后共享导航状态不是 EXIF 详情");
            if (DiagnosticStatus(mainVm) != "未加载")
                throw new InvalidOperationException("控制栏 EXIF 块入口启动了诊断任务");
            var list = presenter.ListPage!;
            if (list.TableView.Style != UITableViewStyle.InsetGrouped) throw new InvalidOperationException("工具根列表不是InsetGrouped");
            if (navigation.ViewControllers?.FirstOrDefault() is not iOSNativeToolsListViewController)
                throw new InvalidOperationException("EXIF 直达展示时导航栈缺少工具列表返回目标");
            await UiDispatcher.InvokeAsync(() => navigation.PopViewController(true));
            await WaitAsync(() => navigation.TopViewController is iOSNativeToolsListViewController && !mainVm.Tools.CanGoBack, "从 EXIF 详情返回工具列表");
            var titles = list.Entries.ToArray();
            if (!titles.Contains("EXIF 详情", StringComparer.Ordinal) || !titles.Contains(iOSAppCapabilities.DiagnosticName, StringComparer.Ordinal))
                throw new InvalidOperationException($"工具列表页缺少入口按钮：{string.Join(" | ", titles)}");
            if (titles.Contains("照片数据统计", StringComparer.Ordinal))
                throw new InvalidOperationException("iOS 不应显示 Windows 专属的照片数据统计入口");
            string unexpected = iOSAppCapabilities.HasDino ? "CV 诊断" : "DINO 诊断";
            if (titles.Contains(unexpected, StringComparer.Ordinal))
                throw new InvalidOperationException($"工具列表页出现另一发行版的诊断入口：{unexpected}");
            lines.Add($"PASS 入口：控制栏 EXIF 信息块直达 EXIF 详情（列表页保留为返回目标），诊断未加载；工具 sheet 与设置页同配置；入口 = {string.Join(" / ", titles)}");
            await iOSNativeSettingsCheck.SnapshotStageAsync("tools-list");

            // 二、EXIF 详情页：真实按钮进入，分组/标签渲染 + 多格式切换。
            await UiDispatcher.InvokeAsync(() => list.RowSelected(list.TableView, NSIndexPath.FromRowSection(0, 0)));
            await WaitAsync(() => navigation.TopViewController is iOSExifDetailView && mainVm.Tools.CanGoBack, "EXIF 详情页压栈");
            var exif = presenter.ExifPage!;
            await WaitAsync(() => exif.Table.Bounds.Width > 100, "EXIF 表格布局");
            await WaitAsync(() => exif.GroupCount > 0 && exif.TagCount > 0, "EXIF 分组加载");
            // TOOLS-01：已展示时再次从主窗口入口打开只前置，保留当前页（不回列表）。
            await UiDispatcher.InvokeAsync(root.ShowTools);
            await Task.Delay(150);
            if (navigation.TopViewController is not iOSExifDetailView || !mainVm.Tools.CanGoBack)
                throw new InvalidOperationException("已打开工具页时再次打开被复位到列表（TOOLS-01）");
            lines.Add("PASS TOOLS-01：已展示时主窗口入口只前置并保留当前 EXIF 详情页");
            await WaitAsync(() => exif.Table.NumberOfSections() == exif.GroupCount, "EXIF 分区渲染");
            int renderedRows = 0;
            for (int section = 0; section < exif.Table.NumberOfSections(); section++) renderedRows += (int)exif.Table.NumberOfRowsInSection(section);
            if (renderedRows != exif.TagCount)
                throw new InvalidOperationException($"EXIF 行数与共享 VM 不一致：{renderedRows}/{exif.TagCount}");
            await UiDispatcher.InvokeAsync(() =>
            {
                exif.Table.LayoutIfNeeded();
                double contentTop = exif.Table.ConvertPointToView(CGPoint.Empty, navigation.View).Y;
                double barBottom = navigation.NavigationBar.ConvertPointToView(new CGPoint(0, navigation.NavigationBar.Bounds.Height), navigation.View).Y;
                if (contentTop < barBottom - .5)
                    throw new InvalidOperationException($"EXIF文件名/格式头被原生导航栏覆盖：content={contentTop} barBottom={barBottom} safe={exif.View!.SafeAreaInsets.Top}");
            });
            var visibleCells = exif.Table.VisibleCells.OfType<iOSExifDetailView.RowCell>().ToArray();
            if (visibleCells.Length == 0) throw new InvalidOperationException("EXIF 表格没有可见行");
            var ids = visibleCells.SelectMany(cell => Labels(cell)).ToArray();
            if (!ids.Any(text => text.StartsWith("0x", StringComparison.Ordinal)))
                throw new InvalidOperationException($"EXIF 可见行缺少 Tag ID：{string.Join(" | ", ids.Take(10))}");
            var selectable = visibleCells.SelectMany(Descendants).OfType<iOSExifDetailView.SelectableTextView>().FirstOrDefault();
            if (selectable is not { Selectable: true, Editable: false })
                throw new InvalidOperationException("TOOLS-03：EXIF 标签文本不是可长按选择的只读文本");
            lines.Add($"PASS EXIF 详情：{exif.GroupCount} 组 / {exif.TagCount} 标签，表格分区/行数与共享 VM 一致，可见行含 ID/名称/值且可长按选择复制");
            // TOOLS-03：卡片左右内缩只能来自系统 InsetGrouped 的一次布局边距。旧实现把系统 inset 与自绘 12 点边距相加
            // （iPhone 两侧达 28 点、表头与卡片错位），并用固定 6 点自绘圆角盖在系统卡片圆角上，窄窗口下双层圆角更显眼。
            await UiDispatcher.InvokeAsync(() =>
            {
                var table = exif.Table;
                var original = table.Frame;
                var originalOffset = table.ContentOffset;
                table.LayoutIfNeeded();
                table.TableHeaderView?.LayoutIfNeeded();
                // 表头（文件名 + 格式组）同样只按系统卡片左缘内缩，不再固定 12 点。
                var headerSpan = iOSExifDetailView.CardSpan(table);
                var nameFrame = exif.FileNameFrame;
                if (nameFrame.X < headerSpan.Left - .5
                    || nameFrame.GetMaxX() > (double)table.Bounds.Width - headerSpan.Left + .5)
                    throw new InvalidOperationException(
                        $"EXIF 表头未收在卡片左右边距内：name={nameFrame} cardLeft={headerSpan.Left} width={table.Bounds.Width}");
                try
                {
                    // 320–1024 点覆盖窄 iPad 分屏、iPhone 竖屏与 iPad 全屏：边距、卡片宽、7 列与行高都必须按系统卡片宽重算。
                    foreach (double width in new double[] { 320, 375, 402, 500, 600, 744, 1024 })
                    {
                        table.Frame = new CGRect(original.X, original.Y, (nfloat)width, original.Height);
                        table.ReloadData();
                        table.LayoutIfNeeded();
                        var margins = table.LayoutMargins;
                        var span = iOSExifDetailView.CardSpan(table);
                        if (Math.Abs(span.Left - (double)margins.Left) > .5 || Math.Abs(span.Left - (double)margins.Right) > .5)
                            throw new InvalidOperationException(
                                $"W={width}：EXIF 卡片左缘未按系统 inset：left={span.Left} margins=({margins.Left},{margins.Right})");
                        if (span.Left < 12 || span.Left > 40)
                            throw new InvalidOperationException($"W={width}：EXIF 卡片左右边距不在合理范围：{span.Left}");
                        if (Math.Abs(span.Width - ((double)width - (double)margins.Left - (double)margins.Right)) > .5)
                            throw new InvalidOperationException($"W={width}：EXIF 卡片宽度重复扣除了左右边距：{span.Width}");
                        var columns = ExifTableGeometry.Compute(span.Width);
                        double total = ExifTableGeometry.IdColumnWidth + ExifTableGeometry.DividerThickness * 3
                            + columns.NameColumn + columns.ChineseColumn + columns.ValueColumn;
                        if (Math.Abs(total - span.Width) > .5)
                            throw new InvalidOperationException($"W={width}：EXIF 7 列合计 {total} ≠ 卡片宽 {span.Width}");
                        if (columns.NameTextWidth < 1 || columns.ChineseTextWidth < 1 || columns.ValueTextWidth < 1)
                            throw new InvalidOperationException($"W={width}：EXIF 文本列退化为零宽（卡片 {span.Width}）");
                        var cell = table.VisibleCells.OfType<iOSExifDetailView.RowCell>().FirstOrDefault()
                            ?? throw new InvalidOperationException($"W={width}：EXIF 表格没有可见行");
                        if (Math.Abs((double)cell.ContentView.Bounds.Width - span.Width) > 1)
                            throw new InvalidOperationException(
                                $"W={width}：EXIF 行内容宽 {cell.ContentView.Bounds.Width} ≠ 系统卡片宽 {span.Width}（重复内缩）");
                        // 系统 cell / contentView 自身也带圆角；这里只禁止应用子视图再叠一层固定半径。
                        foreach (var view in Descendants(cell.ContentView).Where(view =>
                                     view.GetType().Namespace?.StartsWith("PhotoViewer.iOS", StringComparison.Ordinal) == true))
                        {
                            if ((double)view.Layer.CornerRadius > .01)
                                throw new InvalidOperationException(
                                    $"W={width}：EXIF 行自绘视图 {view.GetType().Name} 圆角 {view.Layer.CornerRadius}，会与系统卡片圆角叠加");
                        }
                        var indexPath = table.IndexPathForCell(cell)
                            ?? throw new InvalidOperationException($"W={width}：EXIF 可见行没有索引路径");
                        var group = exif.ViewModel!.Groups![(int)indexPath.Section];
                        double expected = iOSExifDetailView.RowHeight(group.Tags[(int)indexPath.Row], span.Width)
                            + ExifTableGeometry.DividerThickness;
                        double actual = (double)cell.Frame.Height;
                        if (Math.Abs(actual - expected) > 1)
                            throw new InvalidOperationException(
                                $"W={width}：EXIF 行高 {actual} 与按卡片宽 {span.Width} 的测量值 {expected} 不一致");
                    }
                }
                finally
                {
                    table.Frame = original;
                    table.ReloadData();
                    table.LayoutIfNeeded();
                    table.SetContentOffset(originalOffset, false);
                }
            });
            lines.Add("PASS EXIF 卡片几何：320–1024 点七档宽度下左右内缩只取系统 InsetGrouped 一次边距（对称 12–40 点）、" +
                "7 列与行高按实际卡片宽重算、行内无自绘圆角（圆角由系统卡片随宽度提供）");
            await iOSNativeSettingsCheck.SnapshotStageAsync("tools-exif");

            if (exif.ViewModel?.HasMultipleFormats == true)
            {
                int index = exif.ViewModel.Formats.Select((entry, position) => (entry, position))
                    .First(pair => !pair.entry.IsSelected).position;
                var target = exif.ViewModel.Formats[index];
                await UiDispatcher.InvokeAsync(() => exif.FormatButtons[index].SendActionForControlEvents(UIControlEvent.TouchUpInside));
                await WaitAsync(() => ReferenceEquals(exif.ViewModel.SelectedFormat, target), "格式切换写回共享 VM");
                await WaitAsync(() => exif.ViewModel.SelectedFormat.Groups is { Count: > 0 }, "新格式分组加载");
                lines.Add($"PASS 多格式切换：{exif.ViewModel.SelectedFormat.Label}（{exif.ViewModel.Formats.Count} 个格式）");
            }
            else
            {
                lines.Add("NOTE 当前照片只有单一格式，格式切换按钮未生成（按共享 VM 口径跳过真实切换）");
            }

            // 三、返回列表页：系统返回路径（Pop）走同一导航状态。
            await UiDispatcher.InvokeAsync(() => navigation.PopViewController(true));
            await WaitAsync(() => navigation.TopViewController is iOSNativeToolsListViewController && !mainVm.Tools.CanGoBack, "返回工具列表页");

            // 四、诊断页：AI 版 5 张瓦片（DINO patch + CV），标准版 3 张 CV 瓦片；共享 presenter 就绪。
            await UiDispatcher.InvokeAsync(() => list.RowSelected(list.TableView, NSIndexPath.FromRowSection(1, 0)));
            await WaitAsync(() => navigation.TopViewController is iOSDinoDiagnosticView && mainVm.Tools.CanGoBack, "诊断页压栈");
            var diagnostic = presenter.DiagnosticPage!;
            await WaitAsync(() => DiagnosticStatus(mainVm) == mainVm.CurrentFile!.Name, "诊断状态就绪");
#if PHOTOVIEWER_DINO
            await WaitAsync(() => diagnostic.Tiles.Count == 5, "DINO 五张瓦片");
#else
            await WaitAsync(() => diagnostic.Tiles.Count == 3, "CV 三张瓦片");
#endif
            await WaitAsync(() => diagnostic.Tiles[0].HasContent, "诊断缩略图瓦片出图");
            await UiDispatcher.InvokeAsync(() =>
            {
                diagnostic.View!.LayoutIfNeeded();
                foreach (var tile in diagnostic.Tiles)
                {
                    iOSNativeTapProbe.Tap(tile, .65, .65);
                    if (diagnostic.Presenter.CrosshairU is not { } u || Math.Abs(u - .65) > .001)
                        throw new InvalidOperationException("诊断工具瓦片事件未落共享准星");
                    tile.TapAt(CGPoint.Empty);
                    if (diagnostic.Presenter.CrosshairU != null) throw new InvalidOperationException("诊断工具瓦片边框未清准星");
                }
                diagnostic.View.Subviews.OfType<UIScrollView>().Single().SetContentOffset(CGPoint.Empty, false);
            });
            lines.Add($"PASS 诊断瓦片：{diagnostic.Tiles.Count} 张；Window.HitTest与瓦片事件处理注入落共享准星/边框清除（非系统触摸注入）");
            double pageWidth = (double)diagnostic.View!.Bounds.Width;
            // TOOLS-05：刚体拟合文本是网格下方的整宽块（左右 16 点页边距），不占瓦片格。
            if (Math.Abs((double)diagnostic.RigidTitleFrame.X - 16) > .5
                || Math.Abs((double)diagnostic.RigidTitleFrame.Width - Math.Max(1, pageWidth - 32)) > .5
                || (double)diagnostic.RigidTitleFrame.Y < diagnostic.SecondRowBottom)
                throw new InvalidOperationException($"TOOLS-05：刚体拟合文本未以整宽块位于网格下方：{diagnostic.RigidTitleFrame}（页宽 {pageWidth}）");
            lines.Add($"PASS {iOSAppCapabilities.DiagnosticName}：{diagnostic.Tiles.Count} 张瓦片，状态 = {DiagnosticStatus(mainVm)}；刚体拟合文本为网格下方整宽块");
            await iOSNativeSettingsCheck.SnapshotStageAsync(iOSAppCapabilities.HasDino ? "tools-dino" : "tools-cv");

            // TOOLBAR-06：已展示诊断页时控制栏 EXIF 入口仍明确切到 EXIF 详情，并取消在途诊断计算。
            await UiDispatcher.InvokeAsync(() => TapExifEntry(root));
            await WaitAsync(() => navigation.TopViewController is iOSExifDetailView && mainVm.Tools.CurrentTool is ExifDetailViewModel,
                "已展示诊断页时 EXIF 入口切回详情");
            if (DiagnosticBusy(mainVm)) throw new InvalidOperationException("切到 EXIF 后诊断仍在计算");
            lines.Add("PASS TOOLBAR-06：已展示诊断页时控制栏 EXIF 入口切到 EXIF 详情并取消在途诊断");
            await UiDispatcher.InvokeAsync(() => navigation.PopViewController(true));
            await WaitAsync(() => navigation.TopViewController is iOSNativeToolsListViewController && !mainVm.Tools.CanGoBack, "返回列表页（诊断）");

            // 关闭后复用页面，重开回分组列表，不触发诊断现算。
            await UiDispatcher.InvokeAsync(presenter.Dismiss);
            await WaitAsync(() => !presenter.IsPresented && !mainVm.Tools.IsPresented, "工具页关闭并复位共享展示态");
            await WaitAsync(() => DiagnosticStatus(mainVm) == "未加载", "关闭工具页取消在途诊断");
            lines.Add("PASS TOOLS-06：关闭工具页写回 IsPresented=false 并取消在途诊断任务");
            await UiDispatcher.InvokeAsync(root.ShowTools);
            await WaitAsync(() => presenter.IsPresented, "工具页重开");
            await WaitAsync(() => presenter.Navigation!.ViewIfLoaded?.Window != null, "工具页重开上屏");
            // TOOLS-01：关闭后复用同一导航控制器，重开的 sheet 仍按设置页同一份配置。
            VerifySheetMatchesSettings(presenter.Navigation!, "关闭后重开");
            await WaitAsync(() => presenter.Navigation!.TopViewController is iOSNativeToolsListViewController && mainVm.Tools.CurrentTool == null, "重开后返回分组列表");
            if (DiagnosticStatus(mainVm) != "未加载") throw new InvalidOperationException("重开列表触发了诊断计算");
            lines.Add("PASS 生命周期：关闭释放展示、保留详情对象，重开回分组列表且诊断未加载");

            // TOOLBAR-06 / TOOLS-02：EXIF 加载中不算缺 EXIF；真正无照片时才回退工具列表。
            var loadedFile = mainVm.CurrentFile!;
            await UiDispatcher.InvokeAsync(() => mainVm.Tools.SyncCurrentFile(new ImageFile(loadedFile.File)));
            if (!mainVm.Tools.IsExifDetailEnabled) throw new InvalidOperationException("EXIF 加载中被当作缺 EXIF");
            await UiDispatcher.InvokeAsync(() => TapExifEntry(root));
            await WaitAsync(() => navigation.TopViewController is iOSExifDetailView && mainVm.Tools.CurrentTool is ExifDetailViewModel,
                "加载中 EXIF 入口直达详情");
            lines.Add("PASS TOOLBAR-06：EXIF 加载中仍可进入详情（不按缺 EXIF 回退）");

            await UiDispatcher.InvokeAsync(() => mainVm.CurrentFile = null);
            await WaitAsync(() => !mainVm.Tools.IsExifDetailEnabled, "无照片时 EXIF 不可用");
            await UiDispatcher.InvokeAsync(() => TapExifEntry(root));
            await WaitAsync(() => navigation.TopViewController is iOSNativeToolsListViewController && mainVm.Tools.CurrentTool == null,
                "无照片 EXIF 入口回退工具列表");
            lines.Add("PASS TOOLS-02：无照片时控制栏 EXIF 入口回退工具列表");
            await UiDispatcher.InvokeAsync(() => mainVm.CurrentFile = loadedFile);

            await UiDispatcher.InvokeAsync(() => presenter.Dismiss());
            await WaitAsync(() => !presenter.IsPresented && !mainVm.Tools.IsPresented, "收尾关闭工具页");
            await UiDispatcher.InvokeAsync(() =>
            {
                mainVm.IsThumbnailViewVisible = entryFilm;
                mainVm.IsControlViewVisible = entryControl;
                mainVm.IsExifViewVisible = entryExifVisible;
                for (int index = 0; index < entryExif.Length && index < mainVm.Settings.ExifDisplayItems.Count; index++)
                    mainVm.Settings.ExifDisplayItems[index].IsEnabled = entryExif[index];
            });
            await SettleAsync(root);
            var repository = mainVm.SettingsRepository;
            repository.CommitAsync(mainVm.Settings.CreateSnapshot()).GetAwaiter().GetResult();
            repository.FlushAsync().GetAwaiter().GetResult();
            await File.WriteAllLinesAsync(report, lines);
            Console.WriteLine($"[NativeSmoke] PASS tools ({lines.Count} checks)");
        }
        catch (Exception exception)
        {
            lines.Add($"FAIL {exception}");
            await File.WriteAllLinesAsync(report, lines);
            Console.Error.WriteLine("[NativeSmoke] FAIL tools " + exception);
        }
    }

    /// <summary>视图树里的全部标签文本。</summary>
    /// <param name="root">根视图。</param>
    /// <returns>标签文本。</returns>
    private static IEnumerable<string> Labels(UIView root)
    {
        foreach (var view in Descendants(root))
        {
            // TOOLS-03 起元数据行用可选文本视图承载标签，验收同时收集其文本。
            if (view is UILabel label && !string.IsNullOrEmpty(label.Text)) yield return label.Text;
            else if (view is UITextView text && !string.IsNullOrEmpty(text.Text)) yield return text.Text;
        }
    }

    /// <summary>深度遍历视图树。</summary>
    /// <param name="root">根视图。</param>
    /// <returns>全部子孙视图。</returns>
    private static IEnumerable<UIView> Descendants(UIView root)
    {
        foreach (var child in root.Subviews)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>
    /// TOOLS-01：工具 sheet 与设置页取同一份原生展示配置（System PageSheet / 固定深色 /
    /// medium+large detents / grabber / 圆角 / 滚动扩展策略），每次展示都重新挂。
    /// </summary>
    /// <param name="navigation">工具导航控制器。</param>
    /// <param name="scope">检查阶段标签。</param>
    private static void VerifySheetMatchesSettings(UINavigationController navigation, string scope)
    {
        if (navigation.ModalPresentationStyle != UIModalPresentationStyle.PageSheet)
            throw new InvalidOperationException($"{scope} 工具页不是原生 PageSheet：{navigation.ModalPresentationStyle}");
        if (navigation.OverrideUserInterfaceStyle != UIUserInterfaceStyle.Dark)
            throw new InvalidOperationException($"{scope} 工具页不是固定深色");
        var sheet = navigation.SheetPresentationController
            ?? throw new InvalidOperationException($"{scope} 工具页缺少 sheet 展示控制器");
        var detents = sheet.Detents;
        // 与设置页同一份 ConfigureSheetPresentation：medium + large 两档。
        if (detents == null || detents.Length != 2)
            throw new InvalidOperationException($"{scope} 工具 sheet 不是按设置页配置的 medium+large 两档：{detents?.Length}");
        if (!sheet.PrefersGrabberVisible || sheet.PrefersScrollingExpandsWhenScrolledToEdge
            || Math.Abs((double)sheet.PreferredCornerRadius - iOSNativeSettingsPresenter.SheetCornerRadius) > .5)
            throw new InvalidOperationException(
                $"{scope} 工具 sheet 外观与设置页不一致：grabber={sheet.PrefersGrabberVisible} " +
                $"expand={sheet.PrefersScrollingExpandsWhenScrolledToEdge} corner={sheet.PreferredCornerRadius}");
    }

    /// <summary>发行版诊断 presenter 的状态文本（AI 版 DINO，标准版 CV）。</summary>
    private static string DiagnosticStatus(MainViewModel main)
#if PHOTOVIEWER_DINO
        => main.Tools.DinoPresenter.Status;
#else
        => main.Tools.CvPresenter.Status;
#endif

    /// <summary>发行版诊断 presenter 是否在计算。</summary>
    private static bool DiagnosticBusy(MainViewModel main)
#if PHOTOVIEWER_DINO
        => main.Tools.DinoPresenter.IsBusy;
#else
        => main.Tools.CvPresenter.IsBusy;
#endif

    /// <summary>
    /// EXIF 块入口的点按（TOOLBAR-06）：宽形态取控制栏 EXIF 组，手机形态取手机 EXIF 块——两处是同一个
    /// <see cref="iOSExifBlock"/> 组件，都必须直达 EXIF 详情（无可用 EXIF 时回退工具列表）。
    /// </summary>
    /// <param name="root">原生根控制器。</param>
    private static void TapExifEntry(iOSNativeRootViewController root)
    {
        if (root.Form == ScreenForm.Wide)
        {
            if (root.ControlBar.ExifGroup == null)
                throw new InvalidOperationException("控制栏缺少 EXIF 信息块分组");
            root.ControlBar.TapExifGroup();
            return;
        }
        root.PhoneExifBlock.Tap();
    }

    /// <summary>轮询等待条件成立，超时即失败。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="label">等待标签。</param>
    private static async Task WaitAsync(Func<bool> condition, string label)
    {
        for (int attempt = 0; attempt < 2400; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"等待超时：{label}");
    }

    /// <summary>等待 UIKit 一轮布局与共享 VM 通知沉淀。</summary>
    /// <param name="root">原生根控制器。</param>
    private static async Task SettleAsync(iOSNativeRootViewController root)
    {
        await Task.Delay(Settle);
        await UiDispatcher.InvokeAsync(() => root.View!.LayoutIfNeeded());
        await Task.Delay(Settle);
    }
}
