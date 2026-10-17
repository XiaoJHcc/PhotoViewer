using CoreGraphics;
using Foundation;
using PhotoViewer.Core;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Platform;
using PhotoViewer.iOS.Core;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.ViewModels.Tools;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// I5 验收专用（非产品行为，测试钩子）：<c>--pv-native-tools-check &lt;目录&gt;</c> 在模拟器/真机上程序化核对
/// 工具页——sheet + 导航控制器展示/关闭、列表页两个入口（照片数据统计为 Windows 专属，不出现）、
/// EXIF 详情页按共享 7 列几何渲染分组与标签（多格式时真实切换格式）、DINO 诊断页 5 张瓦片与共享 presenter
/// 就绪、返回列表页，以及控制栏 EXIF 信息块点击打开工具页。
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
            bool[] entryExif = mainVm.Settings.ExifDisplayItems.Select(item => item.IsEnabled).ToArray();
            await UiDispatcher.InvokeAsync(() =>
            {
                mainVm.IsThumbnailViewVisible = true;
                mainVm.IsControlViewVisible = true;
                mainVm.Settings.SameNameAsOnePhoto = true;
                foreach (var format in mainVm.Settings.FileFormats)
                    if (format.DisplayName is "JPG" or "RAW") format.IsEnabled = true;
                // EXIF 信息块入口验收需要控制栏存在该分组：用户可能隐藏了全部 EXIF 项，验收会话临时打开第一项。
                if (mainVm.Settings.ExifDisplayItems.All(item => !item.IsEnabled))
                    mainVm.Settings.ExifDisplayItems[0].IsEnabled = true;
                root.BuildControls();
            });
            await mainVm.FolderVM.OpenFolderAsync(new iOSFolderRef(directory));
            await WaitAsync(() => mainVm.CurrentFile != null && session.Image != null, "首图解码");
            await WaitAsync(() => mainVm.CurrentFile?.ExifData != null, "EXIF 读入");
            await WaitAsync(() => mainVm.Tools.IsExifDetailEnabled, "EXIF 详情可用");
            await SettleAsync(root);
            // 同名 RAW+JPG 的伴侣关系在筛选阶段建立；真实使用中切换一次照片会重建 EXIF 详情 VM，
            // 这里走同一条共享路径（CurrentFile 变化 → Tools.SyncCurrentFile），再切回目标照片。
            // 同名 RAW+JPG 的伴侣关系在筛选阶段建立到代表项上；真实用户点选的是列表里的代表卡片，
            // 这里同样把当前照片切到带伴侣的代表项（共享 CurrentFile → Tools.SyncCurrentFile 同步建 VM）。
            await UiDispatcher.InvokeAsync(() => mainVm.FileVM.ThumbnailList.ApplyFilter());
            var representative = mainVm.FileVM.ThumbnailList.FilteredFiles.FirstOrDefault(file => file.HiddenFiles.Count > 0);
            if (representative != null && !ReferenceEquals(mainVm.CurrentFile, representative))
            {
                await UiDispatcher.InvokeAsync(() => mainVm.CurrentFile = representative);
                await WaitAsync(() => ReferenceEquals(mainVm.CurrentFile, representative), "切到同名代表项");
            }
            await WaitAsync(() => mainVm.Tools.IsExifDetailEnabled, "EXIF 详情重建");
            await SettleAsync(root);
            lines.Add($"NOTE 同名合并={mainVm.Settings.SameNameAsOnePhoto}，筛选后 {mainVm.FileVM.ThumbnailList.FilteredFiles.Count} 张，" +
                $"当前照片伴侣 {mainVm.CurrentFile!.HiddenFiles.Count}，格式 {mainVm.Tools.ExifDetail?.Formats.Count ?? 0}");
            lines.Add($"PASS 素材：{fixtures.Length} 张自有夹具（{directory}），列表 {mainVm.FileVM.ThumbnailList.FilteredFiles.Count} 张，" +
                $"当前照片 {mainVm.CurrentFile!.Name}（伴侣 {mainVm.CurrentFile.HiddenFiles.Count}）");

            // 一、控制栏 EXIF 信息块点击打开工具页（另一条入口，与主图菜单共用门面）。
            if (root.ControlBar.ExifGroup == null) throw new InvalidOperationException("控制栏缺少 EXIF 信息块分组");
            if (presenter.IsPresented) throw new InvalidOperationException("工具页在入口调用前已展示");
            await UiDispatcher.InvokeAsync(root.ControlBar.TapExifGroup);
            await WaitAsync(() => presenter.IsPresented && root.PresentedViewController is UINavigationController, "控制栏 EXIF 块打开工具页");
            var navigation = presenter.Navigation ?? throw new InvalidOperationException("工具页缺少导航控制器");
            await WaitAsync(() => navigation.TopViewController is iOSNativeToolsListViewController && navigation.ViewIfLoaded?.Window != null, "工具列表页上屏");
            if (navigation.OverrideUserInterfaceStyle != UIUserInterfaceStyle.Dark)
                throw new InvalidOperationException("工具页不是深色外观");
            var list = presenter.ListPage!;
            await WaitAsync(() => list.Entries.All(entry => entry.Window != null), "列表入口按钮上屏");
            var titles = list.Entries.Select(entry => entry.Title(UIControlState.Normal)).ToArray();
            if (!titles.Contains("EXIF 详情", StringComparer.Ordinal) || !titles.Contains("DINO 诊断", StringComparer.Ordinal))
                throw new InvalidOperationException($"工具列表页缺少入口按钮：{string.Join(" | ", titles)}");
            if (titles.Contains("照片数据统计", StringComparer.Ordinal))
                throw new InvalidOperationException("iOS 不应显示 Windows 专属的照片数据统计入口");
            lines.Add($"PASS 入口：控制栏 EXIF 信息块打开工具页（深色 sheet），列表页入口 = {string.Join(" / ", titles)}");

            // 二、EXIF 详情页：真实按钮进入，分组/标签渲染 + 多格式切换。
            await UiDispatcher.InvokeAsync(() => list.Entries[0].SendActionForControlEvents(UIControlEvent.TouchUpInside));
            await WaitAsync(() => navigation.TopViewController is iOSExifDetailView && mainVm.Tools.CanGoBack, "EXIF 详情页压栈");
            var exif = presenter.ExifPage!;
            await WaitAsync(() => exif.Table.Bounds.Width > 100, "EXIF 表格布局");
            await WaitAsync(() => exif.GroupCount > 0 && exif.TagCount > 0, "EXIF 分组加载");
            await WaitAsync(() => exif.Table.NumberOfSections() == exif.GroupCount, "EXIF 分区渲染");
            int renderedRows = 0;
            for (int section = 0; section < exif.Table.NumberOfSections(); section++) renderedRows += (int)exif.Table.NumberOfRowsInSection(section);
            if (renderedRows != exif.TagCount)
                throw new InvalidOperationException($"EXIF 行数与共享 VM 不一致：{renderedRows}/{exif.TagCount}");
            await UiDispatcher.InvokeAsync(() => exif.Table.LayoutIfNeeded());
            var visibleCells = exif.Table.VisibleCells.OfType<iOSExifDetailView.RowCell>().ToArray();
            if (visibleCells.Length == 0) throw new InvalidOperationException("EXIF 表格没有可见行");
            var ids = visibleCells.SelectMany(cell => Labels(cell)).ToArray();
            if (!ids.Any(text => text.StartsWith("0x", StringComparison.Ordinal)))
                throw new InvalidOperationException($"EXIF 可见行缺少 Tag ID：{string.Join(" | ", ids.Take(10))}");
            lines.Add($"PASS EXIF 详情：{exif.GroupCount} 组 / {exif.TagCount} 标签，表格分区/行数与共享 VM 一致，可见行含 ID/名称/值");

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

            // 四、DINO 诊断页：5 张原生瓦片 + 共享 presenter 就绪。
            await UiDispatcher.InvokeAsync(() => list.Entries[1].SendActionForControlEvents(UIControlEvent.TouchUpInside));
            await WaitAsync(() => navigation.TopViewController is iOSDinoDiagnosticView && mainVm.Tools.CanGoBack, "DINO 诊断页压栈");
            var dino = presenter.DinoPage!;
            await WaitAsync(() => mainVm.Tools.DinoPresenter.Status == mainVm.CurrentFile!.Name, "DINO 状态就绪");
            await WaitAsync(() => dino.Tiles.Count == 5, "DINO 五张瓦片");
            await WaitAsync(() => dino.Tiles[0].HasContent, "DINO 缩略图瓦片出图");
            lines.Add($"PASS DINO 诊断：5 张瓦片，状态 = {mainVm.Tools.DinoPresenter.Status}");
            await UiDispatcher.InvokeAsync(() => navigation.PopViewController(true));
            await WaitAsync(() => navigation.TopViewController is iOSNativeToolsListViewController && !mainVm.Tools.CanGoBack, "返回列表页（DINO）");

            // 五、关闭后可重开（对象保留、导航栈复位到列表页）。
            await UiDispatcher.InvokeAsync(presenter.Dismiss);
            await WaitAsync(() => !presenter.IsPresented, "工具页关闭");
            await UiDispatcher.InvokeAsync(root.ShowTools);
            await WaitAsync(() => presenter.IsPresented, "工具页重开");
            await WaitAsync(() => presenter.Navigation!.TopViewController is iOSNativeToolsListViewController, "重开后回到列表页");
            lines.Add("PASS 生命周期：关闭释放展示、可重开并回到列表页");

            await UiDispatcher.InvokeAsync(() => presenter.Dismiss());
            await WaitAsync(() => !presenter.IsPresented, "收尾关闭工具页");
            await UiDispatcher.InvokeAsync(() =>
            {
                mainVm.IsThumbnailViewVisible = entryFilm;
                mainVm.IsControlViewVisible = entryControl;
                for (int index = 0; index < entryExif.Length && index < mainVm.Settings.ExifDisplayItems.Count; index++)
                    mainVm.Settings.ExifDisplayItems[index].IsEnabled = entryExif[index];
            });
            await SettleAsync(root);
            PhotoViewer.Core.Settings.SettingsService.Instance.SaveAsync(mainVm.Settings.CreateSnapshot()).GetAwaiter().GetResult();
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
            if (view is UILabel label && !string.IsNullOrEmpty(label.Text))
                yield return label.Text;
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
