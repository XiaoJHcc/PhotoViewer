using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoViewer.Controls;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Settings;
using PhotoViewer.Views;
using PhotoViewer.Views.Main.File;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 迁移期旧壳(Avalonia 壳)布局对拍驱动器:仅显式 <c>--pv-legacy-layout &lt;绝对 fixture 路径&gt;</c> 激活,
/// 常规旧壳启动不解析此参数、立即返回原流程。隔离设置文件与特征数据库后,在固定内容区尺寸下按
/// Col/Row 两向渲染,把真实已布局控件的几何(统一换算到 MainView 坐标)写入 JSON,并导出控件渲染位图。
///
/// 输出(均在 release/native-p2-acceptance/):
/// - legacy-layout-{col|row}-{1200|830|700}.png — RenderTargetBitmap 的控件渲染导出(控件/GPU 参考,
///   不是系统窗口截图,不含系统标题栏、窗口阴影与跨屏色彩管理);
/// - legacy-layout-geometry.json — 每次运行的请求/实际内容区尺寸、渲染缩放与全部几何节点;
/// - settings-legacy-layout.json / photos-legacy-layout.db — 隔离设置与测试库,不触碰 NSUserDefaults 与用户照片库。
///
/// 固定内容区尺寸由 <see cref="Window"/> 的 Width/Height(TopLevel 口径即客户区)设置;取值拿不准的一律
/// 以实际测量值输出,不伪造基线;任何异步异常落 legacy-layout-error.txt 并以非 0 退出码关闭桌面应用。
/// </summary>
internal static class LegacyLayoutAcceptance
{
    /// <summary>激活参数名;缺少时整条测试路径完全惰性。</summary>
    private const string FixtureArgument = "--pv-legacy-layout";

    /// <summary>等待窗口/设置/照片/布局就绪的上限。</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>两向布局对拍顺序。</summary>
    private static readonly LayoutMode[] Modes = [LayoutMode.Col, LayoutMode.Row];

    /// <summary>固定内容区尺寸对拍顺序。</summary>
    private static readonly Size[] Sizes = [new(1200, 850), new(830, 650), new(700, 480)];

    /// <summary>测试命令白名单(与原生工具栏对拍一致)。</summary>
    private static readonly string[] CommandWhitelist = ["Previous", "Next", "Exchange", "Fit", "ZoomInPreset", "ZoomOutPreset"];

    /// <summary>测试 EXIF 白名单(与原生工具栏对拍一致)。</summary>
    private static readonly string[] ExifWhitelist = ["Aperture", "ExposureTime", "Iso", "EquivFocalLength"];

    /// <summary>
    /// 解析测试参数;命中时隔离设置/数据库并把异步对拍投递到 UI 线程,未命中立即返回。
    /// 由头工程在 <c>AppBuilder.AfterSetup</c> 调用 — 该回调早于 MainViewModel 创建,所以隔离存储
    /// 一定先于设置读取生效;不改变正常旧壳的任何一步。
    /// </summary>
    /// <param name="args">进程启动参数。</param>
    /// <exception cref="ArgumentException">传了参数但不是存在的绝对路径 fixture。</exception>
    internal static void TryActivate(string[] args)
    {
        int flag = Array.IndexOf(args, FixtureArgument);
        if (flag < 0)
        {
            return;
        }

        if (flag == args.Length - 1)
        {
            throw new ArgumentException($"{FixtureArgument} 缺少 fixture 路径参数");
        }

        string fixture = args[flag + 1];
        if (!Path.IsPathRooted(fixture) || !(File.Exists(fixture) || Directory.Exists(fixture)))
        {
            throw new ArgumentException($"{FixtureArgument} 需要存在的绝对 fixture 路径,收到:{fixture}");
        }

        string output = Path.Combine(ResolveWorkingDirectory(), "release", "native-p2-acceptance");
        Directory.CreateDirectory(output);
        string settingsPath = Path.Combine(output, "settings-legacy-layout.json");
        // 对拍从 SettingsModel 默认值起步:删掉隔离文件后 SettingsViewModel 读到 null 不 ApplyModel,
        // 而它异步加载结束时必定落盘一次,所以"文件出现"就是"设置加载已完成"的判据(见 RunAsync)。
        File.Delete(settingsPath);
        SettingsService.ConfigureStorage(new FileSettingsStorage(settingsPath));
        PhotoDatabase.Initialize(Path.Combine(output, "photos-legacy-layout.db"));
        Dispatcher.UIThread.Post(() => _ = RunAsync(fixture, output, settingsPath));
    }

    /// <summary>主窗口就绪后采集全部对拍尺寸,落盘报告并以退出码关闭桌面应用。</summary>
    private static async Task RunAsync(string fixture, string output, string settingsPath)
    {
        string errorPath = Path.Combine(output, "legacy-layout-error.txt");
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        try
        {
            if (lifetime is null)
            {
                throw new InvalidOperationException("旧壳布局对拍需要桌面应用生命周期");
            }

            var window = await WaitOnUiAsync(() => lifetime.MainWindow is { DataContext: MainViewModel } candidate ? candidate : null, "主窗口与 MainViewModel 就绪");
            var main = await OnUiAsync(() => (MainViewModel)window.DataContext!);
            var mainView = await WaitOnUiAsync(() => window.GetVisualDescendants().OfType<MainView>().FirstOrDefault(), "MainView 就绪");

            // 设置异步加载落盘后才写测试配置,避免迟到的 ApplyModel 覆盖(见 TryActivate 注释)。
            await WaitOnUiAsync(() => File.Exists(settingsPath) ? settingsPath : null, "设置加载完成");
            await OnUiAsync(() => ApplyTestConfiguration(main, fixture));
            await WaitOnUiAsync(() => main.CurrentFile is not null && main.ImageVM.SourceBitmap is not null ? main.CurrentFile : null, "fixture 首图与主图位图加载");
            await OnUiAsync(() => VerifySimilarityToggleVisible(mainView));
            await Task.Delay(300);
            await OnUiAsync(() => VerifyTestConfiguration(main));

            var runs = new List<LayoutRun>();
            foreach (var mode in Modes)
            {
                foreach (var size in Sizes)
                {
                    runs.Add(await CaptureAsync(window, main, mainView, output, fixture, mode, size));
                }
            }

            string reportPath = Path.Combine(output, "legacy-layout-geometry.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(runs, LegacyLayoutJsonContext.Default.ListLayoutRun));
            File.Delete(errorPath);
            Console.WriteLine($"[LegacyLayout] PASS {runs.Count} runs; geometry={reportPath}");
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(errorPath, exception.ToString());
            Console.Error.WriteLine("[LegacyLayout] FAIL " + exception);
            Environment.ExitCode = 1;
        }
        finally
        {
            if (lifetime is not null)
            {
                try
                {
                    await OnUiAsync(() => lifetime.Shutdown(Environment.ExitCode));
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("[LegacyLayout] 关闭桌面应用失败 " + exception.Message);
                }
            }
        }
    }

    /// <summary>按测试口径裁剪可见项,并投递 fixture 目录(必须在 UI 线程调用)。</summary>
    private static void ApplyTestConfiguration(MainViewModel main, string fixture)
    {
        main.IsAnalysisViewVisible = false;
        main.FileVM.FilterBar.IsSimilarityPanelOpen = false;
        main.IsThumbnailViewVisible = true;
        main.IsControlViewVisible = true;
        main.Settings.ShowRating = true;
        foreach (var entry in main.Settings.ExifDisplayItems)
        {
            entry.IsEnabled = ExifWhitelist.Contains(entry.PropertyName, StringComparer.Ordinal);
        }

        foreach (var hotkey in main.Settings.Hotkeys)
        {
            hotkey.IsDisplay = CommandWhitelist.Contains(hotkey.Command, StringComparer.Ordinal);
        }

        // fixture 是目录时经共享外部打开门面进文件夹(与原生桥 OpenFiles 同一入口);
        // 是单文件时命令行参数已由 MacExternalOpenBridge 投递,这里不重复投递。
        if (Directory.Exists(fixture))
        {
            ExternalOpenService.PublishFolder(new Uri(fixture), "LegacyLayoutAcceptance");
        }
    }

    /// <summary>核对视觉树内相似聚类 toggle 存在且未隐藏(P3 起恢复该入口,基线必须带上它一起对拍)。
    /// 行/列两套布局各持一份 toggle,只有生效布局那份处于 IsEffectivelyVisible,所以要求全部未被显式隐藏且至少一份实际可见。</summary>
    private static void VerifySimilarityToggleVisible(MainView mainView)
    {
        var toggles = mainView.GetVisualDescendants()
            .OfType<ToggleButton>()
            .Where(toggle => toggle.Classes.Contains("SimToggle"))
            .ToArray();
        if (toggles.Length == 0)
        {
            throw new InvalidOperationException("视觉树中找不到相似聚类 toggle");
        }

        if (toggles.Any(toggle => !toggle.IsVisible))
        {
            throw new InvalidOperationException("相似聚类 toggle 被隐藏,无法与旧壳基线对拍");
        }

        if (!toggles.Any(toggle => toggle.IsEffectivelyVisible))
        {
            throw new InvalidOperationException("生效布局内相似聚类 toggle 不可见,无法与旧壳基线对拍");
        }
    }

    /// <summary>核对测试配置确实生效;任何偏差都必须暴露,不允许带着用户设置出基线。</summary>
    private static void VerifyTestConfiguration(MainViewModel main)
    {
        string[] actualExif = main.Settings.ExifDisplayItems.Where(item => item.IsEnabled).Select(item => item.PropertyName).ToArray();
        string[] actualCommands = main.Settings.Hotkeys.Where(item => item.IsDisplay).Select(item => item.Command).ToArray();
        if (main.IsAnalysisViewVisible || main.FileVM.FilterBar.IsSimilarityPanelOpen || !main.Settings.ShowRating || !main.IsThumbnailViewVisible || !main.IsControlViewVisible)
        {
            throw new InvalidOperationException("面板/评分可见性未按测试配置生效");
        }

        if (!ExifWhitelist.SequenceEqual(actualExif, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("EXIF 项未按测试配置生效:" + string.Join(",", actualExif));
        }

        if (!CommandWhitelist.SequenceEqual(actualCommands, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("命令项未按测试配置生效:" + string.Join(",", actualCommands));
        }
    }

    /// <summary>切换布局方向与固定内容区尺寸,等布局稳定后采集几何并导出控件渲染位图。</summary>
    private static async Task<LayoutRun> CaptureAsync(Window window, MainViewModel main, MainView mainView, string output, string fixture, LayoutMode mode, Size size)
    {
        string modeName = mode.ToString().ToLowerInvariant();
        await OnUiAsync(() =>
        {
            main.Settings.LayoutMode = mode;
            // Window.Width/Height 在 TopLevel 口径下即客户区尺寸(HandleResized 反向同步 ClientSize)。
            window.Width = size.Width;
            window.Height = size.Height;
        });
        await WaitForLayoutAsync(window, mainView, mode);
        await OnUiAsync(() => VerifySimilarityToggleVisible(mainView));
        await Task.Delay(200);

        var run = await OnUiAsync(() =>
        {
            Size? frame = window.FrameSize;
            var value = new LayoutRun
            {
                Mode = modeName,
                Fixture = fixture,
                RequestedWidth = size.Width,
                RequestedHeight = size.Height,
                ClientWidth = Round(window.ClientSize.Width),
                ClientHeight = Round(window.ClientSize.Height),
                RenderScaling = window.RenderScaling,
                FrameWidth = Round(frame?.Width ?? 0),
                FrameHeight = Round(frame?.Height ?? 0),
                ExifLoaded = main.CurrentFile?.ExifData is not null,
                Snapshot = $"legacy-layout-{modeName}-{size.Width:0}.png",
            };
            CollectLayoutNodes(value.Nodes, mainView);
            return value;
        });

        string snapshotPath = Path.Combine(output, run.Snapshot);
        await OnUiAsync(() =>
        {
            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Round(run.ClientWidth * run.RenderScaling), (int)Math.Round(run.ClientHeight * run.RenderScaling)),
                new Vector(96 * run.RenderScaling, 96 * run.RenderScaling));
            bitmap.Render(mainView);
            bitmap.Save(snapshotPath);
        });

        Console.WriteLine($"[LegacyLayout] {run.Mode} 请求 {size.Width}x{size.Height} 实际 {run.ClientWidth}x{run.ClientHeight} 节点={run.Nodes.Count} -> {snapshotPath}");
        if (Environment.GetCommandLineArgs().Contains("--pv-layout-hold") && size.Width == 830)
        {
            Console.WriteLine($"[LegacyLayout] READY screenshot {mode}/830");
            await Task.Delay(30000);
        }
        return run;
    }

    /// <summary>等到 MainView 已有尺寸、布局方向生效且客户区与 MainView 尺寸连续多轮未再变化。</summary>
    private static async Task WaitForLayoutAsync(Window window, MainView mainView, LayoutMode mode)
    {
        bool expectRow = mode == LayoutMode.Row;
        Size previousClient = default;
        Size previousView = default;
        int stable = 0;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < ReadyTimeout)
        {
            var (settled, client, view) = await OnUiAsync(() =>
            {
                var currentClient = window.ClientSize;
                var currentView = mainView.Bounds.Size;
                bool isSettled = currentView.Width > 0
                    && currentView.Height > 0
                    && currentView == previousView
                    && currentClient == previousClient
                    && window.DataContext is MainViewModel { IsRowLayout: var isRow } && isRow == expectRow;
                return (isSettled, currentClient, currentView);
            });
            stable = settled ? stable + 1 : 0;
            if (stable >= 5)
            {
                return;
            }

            previousClient = client;
            previousView = view;
            await Task.Delay(50);
        }

        throw new TimeoutException($"布局在 {ReadyTimeout.TotalSeconds:0} 秒内未稳定({mode})");
    }

    /// <summary>采集 MainView 内所有对拍关注的真实已布局控件几何;坐标一律换算到 MainView。</summary>
    private static void CollectLayoutNodes(List<LayoutNode> nodes, MainView mainView)
    {
        Add(mainView, nodes, "mainView", mainView, "旧壳主界面根控件");

        var rootGrid = mainView.GetVisualDescendants().OfType<Grid>().First();
        if (rootGrid.Children.Count != 2 || rootGrid.Children[0] is not Grid rowGrid || rootGrid.Children[1] is not Grid colGrid)
        {
            throw new InvalidOperationException("MainView.axaml 根 Grid 不再是行/列两个布局网格");
        }

        foreach (var (name, grid, description) in new[]
                 {
                     ("layoutRow", rowGrid, "MainView.axaml 上中下 Grid(RowDefinitions=Auto,*,Auto,Auto)"),
                     ("layoutCol", colGrid, "MainView.axaml 左中右 Grid(ColumnDefinitions=Auto,*,Auto,Auto)"),
                 })
        {
            Add(mainView, nodes, name, grid, description);
            if (grid.Children.Count != 4 || grid.Children[1] is not ImageView)
            {
                throw new InvalidOperationException($"MainView.axaml {name} 子项不足 4 个,或第 2 项不再是 ImageView");
            }

            string[] partitions = ["thumbHost", "imageView", "analysisHost", "controlHost"];
            for (int index = 0; index < grid.Children.Count; index++)
            {
                Add(mainView, nodes, $"{name}/{partitions[index]}", grid.Children[index], $"MainView.axaml {name} 第 {index} 个子项");
            }
        }

        var activeGrid = rowGrid.IsVisible ? rowGrid : colGrid;
        string activeName = activeGrid == rowGrid ? "layoutRow" : "layoutCol";
        var imageView = (ImageView)activeGrid.Children[1];
        Add(mainView, nodes, $"{activeName}/imageView/mainGrid", imageView.FindControl<Grid>("MainGrid"), "图片区宿主(ClipToBounds=False)");
        CollectImageRect(mainView, nodes, $"{activeName}/imageView/previewImage", imageView.FindControl<Image>("PreviewImage"));

        var fileView = (activeGrid.Children[0] as Border)?.Child as FileView;
        Add(mainView, nodes, $"{activeName}/fileView", fileView, "生效文件栏(筛选条 + 缩略图列表)");
        CollectFilterBarNodes(mainView, nodes, fileView);
        CollectThumbnailNodes(mainView, nodes, fileView);
        CollectControlNodes(mainView, nodes, (activeGrid.Children[3] as Border)?.Child as ControlView);
    }

    /// <summary>记录筛选条面板及其真实子项(三个下拉框 / 计数文本 / 相似聚类 toggle)几何与内容。</summary>
    private static void CollectFilterBarNodes(Visual mainView, List<LayoutNode> nodes, FileView? fileView)
    {
        var filterBar = fileView?.GetVisualDescendants().OfType<FilterBarView>().FirstOrDefault();
        Add(mainView, nodes, "filterBar", filterBar, null);
        var panel = filterBar?.FindControl<StackPanel>("FilterBarPanel");
        if (panel is null)
        {
            Add(mainView, nodes, "filterBar/panel", null, "FilterBarPanel 缺失");
            return;
        }

        Add(mainView, nodes, "filterBar/panel", panel, $"Orientation={panel.Orientation} Spacing={panel.Spacing}");
        for (int index = 0; index < panel.Children.Count; index++)
        {
            Add(mainView, nodes, $"filterBar/panel[{index}]", panel.Children[index], DescribeFilterChild(panel.Children[index]));
        }
    }

    /// <summary>描述筛选条子项的真实内容,便于对拍时识别每个下标的语义。</summary>
    private static string DescribeFilterChild(Control child) => child switch
    {
        ComboBox combo => "ComboBox 当前选择=" + ((combo.SelectedItem as SortOption)?.DisplayName ?? combo.SelectedItem?.ToString() ?? "-"),
        TextBlock text => "Text=" + text.Text,
        ToggleButton toggle => "ToggleButton Content=" + toggle.Content,
        _ => "未识别子项",
    };

    /// <summary>记录缩略图列表宿主、卡片面板边距,以及每张已实现卡片的按钮/外框/缩略图框几何。</summary>
    private static void CollectThumbnailNodes(Visual mainView, List<LayoutNode> nodes, FileView? fileView)
    {
        var listView = fileView?.GetVisualDescendants().OfType<ThumbnailListView>().FirstOrDefault();
        Add(mainView, nodes, "thumbnail/scrollViewer", listView?.FindControl<ScrollViewer>("ThumbnailScrollViewer"), null);
        var itemsControl = listView?.FindControl<ItemsControl>("ThumbnailItemsControl");
        Add(mainView, nodes, "thumbnail/itemsControl", itemsControl, null);
        var itemPanel = itemsControl?.GetVisualDescendants().OfType<VirtualizingStackPanel>().FirstOrDefault();
        Add(mainView, nodes, "thumbnail/itemsPanel", itemPanel, "卡片面板(边距决定卡片起点)");
        if (itemsControl is null)
        {
            return;
        }

        var cards = itemsControl.GetVisualDescendants().OfType<ThumbnailCard>().ToArray();
        for (int index = 0; index < cards.Length; index++)
        {
            var button = cards[index].GetLogicalDescendants().OfType<Button>().FirstOrDefault();
            if (button is null)
            {
                throw new InvalidOperationException($"缩略图卡片 {index} 缺少根按钮");
            }

            var frame = button.GetLogicalDescendants().OfType<Border>().FirstOrDefault();
            if (frame is null)
            {
                throw new InvalidOperationException($"缩略图卡片 {index} 缺少 90×138 外框 Border");
            }

            var thumbnail = frame.GetLogicalDescendants().OfType<Border>().FirstOrDefault();
            Add(mainView, nodes, $"thumbnail/card[{index}]", cards[index], cards[index].File?.Name);
            Add(mainView, nodes, $"thumbnail/card[{index}]/button", button, "卡片点击区(声明 Margin=3)");
            Add(mainView, nodes, $"thumbnail/card[{index}]/frame", frame, $"BorderThickness={frame.BorderThickness} CornerRadius={frame.CornerRadius} Background={frame.Background}");
            Add(mainView, nodes, $"thumbnail/card[{index}]/thumbnail", thumbnail, thumbnail is null ? null : $"BorderThickness={thumbnail.BorderThickness} CornerRadius={thumbnail.CornerRadius}");
        }
    }

    /// <summary>记录控制栏三个分组、EXIF 小块、评分 Border 与命令按钮相对于父 WrapPanel 的边距与位置。</summary>
    private static void CollectControlNodes(Visual mainView, List<LayoutNode> nodes, ControlView? controlView)
    {
        Add(mainView, nodes, "controlView", controlView, null);
        if (controlView is null)
        {
            return;
        }

        var wrapPanel = controlView.GetLogicalDescendants().OfType<WrapPanel>().FirstOrDefault();
        Add(mainView, nodes, "controlView/wrapPanel", wrapPanel, wrapPanel is null
            ? "WrapPanel 缺失"
            : $"Orientation={wrapPanel.Orientation} ItemSpacing={wrapPanel.ItemSpacing} LineSpacing={wrapPanel.LineSpacing} ItemsAlignment={wrapPanel.ItemsAlignment}");
        if (wrapPanel is null || wrapPanel.Children.Count != 3)
        {
            throw new InvalidOperationException("ControlView.axaml 的 WrapPanel 不再是 EXIF/评分/命令三个分组");
        }

        string[] sections = ["exif", "rating", "commands"];
        for (int index = 0; index < wrapPanel.Children.Count; index++)
        {
            var section = wrapPanel.Children[index];
            Add(mainView, nodes, $"controlView/{sections[index]}", section, $"分组在父 WrapPanel 内的边距与位置(下标 {index})");
            switch (sections[index])
            {
                case "exif":
                    CollectExifNodes(mainView, nodes, section);
                    break;
                case "rating":
                    CollectRatingNodes(mainView, nodes, section);
                    break;
                default:
                    CollectCommandNodes(mainView, nodes, section);
                    break;
            }
        }
    }

    /// <summary>记录可见 EXIF 小块几何,并附带当前显示名/值便于对表。</summary>
    private static void CollectExifNodes(Visual mainView, List<LayoutNode> nodes, Control section)
    {
        var blocks = section.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("ExifBlockBorder")).ToArray();
        for (int index = 0; index < blocks.Length; index++)
        {
            var entry = blocks[index].DataContext as ExifDisplayEntry;
            Add(mainView, nodes, $"controlView/exif[{index}]", blocks[index], entry is null ? "未取到 ExifDisplayEntry" : $"{entry.DisplayName}={entry.DisplayValue}");
        }
    }

    /// <summary>记录评分分组 Border、其 DockPanel 边距与全部星级按钮位置(含 0 星)。</summary>
    private static void CollectRatingNodes(Visual mainView, List<LayoutNode> nodes, Control section)
    {
        var dock = section.GetLogicalDescendants().OfType<DockPanel>().FirstOrDefault();
        Add(mainView, nodes, "controlView/rating/dock", dock, "星级容器(边距随布局方向切换)");
        var stars = section.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("StarButton")).ToArray();
        for (int index = 0; index < stars.Length; index++)
        {
            Add(mainView, nodes, $"controlView/rating/star[{index}]", stars[index], "Tag=" + stars[index].Tag);
        }
    }

    /// <summary>记录命令分组内每个可见命令按钮的几何与命令名。</summary>
    private static void CollectCommandNodes(Visual mainView, List<LayoutNode> nodes, Control section)
    {
        var commands = section.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("ControlButton")).ToArray();
        for (int index = 0; index < commands.Length; index++)
        {
            Add(mainView, nodes, $"controlView/command[{index}]", commands[index], "Command=" + commands[index].Tag);
        }
    }

    /// <summary>记录主图元素经 RenderTransform(缩放 + 平移)后的实际显示矩形;Bounds 仍是全分辨率逻辑几何基准。</summary>
    private static void CollectImageRect(Visual mainView, List<LayoutNode> nodes, string node, Image? image)
    {
        if (image is null)
        {
            Add(mainView, nodes, node, null, "PreviewImage 缺失");
            return;
        }

        var matrix = image.TransformToVisual(mainView);
        if (matrix is null)
        {
            Add(mainView, nodes, node, image, "与 MainView 无共同祖先,未换算");
            return;
        }

        var rect = new Rect(image.Bounds.Size).TransformToAABB(matrix.Value);
        nodes.Add(new LayoutNode
        {
            Node = node,
            Type = image.GetType().Name,
            Visible = image.IsEffectivelyVisible,
            X = Round(rect.X),
            Y = Round(rect.Y),
            Width = Round(rect.Width),
            Height = Round(rect.Height),
            Note = $"逻辑尺寸={Round(image.Bounds.Width)}x{Round(image.Bounds.Height)} Scale={matrix.Value.M11:0.####} Translate=({matrix.Value.M31:0.##},{matrix.Value.M32:0.##})",
        });
    }

    /// <summary>记录一个真实控件的可见性与几何;位置换算到 MainView 坐标,尺寸为布局值。</summary>
    private static void Add(Visual mainView, List<LayoutNode> nodes, string node, Visual? visual, string? note)
    {
        if (visual is null)
        {
            nodes.Add(new LayoutNode { Node = node, Type = "missing", Note = note });
            return;
        }

        var position = visual.TranslatePoint(new Point(0, 0), mainView);
        nodes.Add(new LayoutNode
        {
            Node = node,
            Type = visual.GetType().Name,
            Visible = visual.IsEffectivelyVisible,
            X = Round(position?.X ?? 0),
            Y = Round(position?.Y ?? 0),
            Width = Round(visual.Bounds.Width),
            Height = Round(visual.Bounds.Height),
            Margin = (visual as Layoutable)?.Margin.ToString(),
            Note = position.HasValue ? note : "与 MainView 无共同祖先,坐标未换算",
        });
    }

    /// <summary>几何值统一保留两位小数,避免 JSON 混入浮点噪声。</summary>
    private static double Round(double value) => Math.Round(value, 2);

    /// <summary>把一次 UI 操作封送到 UI 线程执行并等待完成(不依赖环境 SynchronizationContext)。</summary>
    private static async Task OnUiAsync(Action body) => await Dispatcher.UIThread.InvokeAsync(body);

    /// <summary>把一次取值的 UI 操作封送到 UI 线程执行并等待结果。</summary>
    private static async Task<T> OnUiAsync<T>(Func<T> body) => await Dispatcher.UIThread.InvokeAsync(body);

    /// <summary>在 UI 线程上轮询探针,直到取得真实对象;等待期间不阻塞 UI 线程,超时即报错。</summary>
    private static async Task<T> WaitOnUiAsync<T>(Func<T?> probe, string name) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (await Dispatcher.UIThread.InvokeAsync(probe) is { } value)
            {
                return value;
            }

            if (stopwatch.Elapsed > ReadyTimeout)
            {
                throw new TimeoutException(name);
            }

            await Task.Delay(20);
        }
    }

    /// <summary>命令行相对输出路径按启动 PWD 解析:Avalonia/SDK 会改写进程工作目录。</summary>
    private static string ResolveWorkingDirectory()
    {
        string? pwd = Environment.GetEnvironmentVariable("PWD");
        return string.IsNullOrEmpty(pwd) ? Environment.CurrentDirectory : pwd;
    }
}

/// <summary>单个真实控件的布局记录;X/Y 为 MainView 坐标系下的设备无关像素。</summary>
public sealed class LayoutNode
{
    /// <summary>语义节点路径(如 layoutRow/controlView/exif[0])。</summary>
    public string Node { get; set; } = string.Empty;

    /// <summary>控件实际类型名;missing 表示该节点未找到。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>是否 IsEffectivelyVisible(父链与自身都可见)。</summary>
    public bool Visible { get; set; }

    /// <summary>相对 MainView 左上角的 X。</summary>
    public double X { get; set; }

    /// <summary>相对 MainView 左上角的 Y。</summary>
    public double Y { get; set; }

    /// <summary>布局宽度。</summary>
    public double Width { get; set; }

    /// <summary>布局高度。</summary>
    public double Height { get; set; }

    /// <summary>控件声明的 Margin。</summary>
    public string? Margin { get; set; }

    /// <summary>补充语义(选中项、命令名、圆角、变换等)。</summary>
    public string? Note { get; set; }
}

/// <summary>一次固定内容区尺寸下的完整布局快照。</summary>
public sealed class LayoutRun
{
    /// <summary>布局方向小写名:col / row。</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>本次使用的 fixture 绝对路径。</summary>
    public string Fixture { get; set; } = string.Empty;

    /// <summary>请求的内容区宽度。</summary>
    public double RequestedWidth { get; set; }

    /// <summary>请求的内容区高度。</summary>
    public double RequestedHeight { get; set; }

    /// <summary>实测客户区宽度(拿不准时以实测为准)。</summary>
    public double ClientWidth { get; set; }

    /// <summary>实测客户区高度。</summary>
    public double ClientHeight { get; set; }

    /// <summary>窗口渲染缩放(位图导出按此比例放大)。</summary>
    public double RenderScaling { get; set; }

    /// <summary>实测窗口 Frame 宽度(0 = 平台未上报);与客户区对比可判断装饰差异。</summary>
    public double FrameWidth { get; set; }

    /// <summary>实测窗口 Frame 高度。</summary>
    public double FrameHeight { get; set; }

    /// <summary>首图 EXIF 是否已加载(fixture 无 EXIF 时为 false,不影响几何)。</summary>
    public bool ExifLoaded { get; set; }

    /// <summary>控件渲染导出的文件名。</summary>
    public string Snapshot { get; set; } = string.Empty;

    /// <summary>本尺寸下的全部几何节点。</summary>
    public List<LayoutNode> Nodes { get; set; } = new();
}

/// <summary>旧壳布局对拍报告的 source-generated JSON 契约。</summary>
[JsonSerializable(typeof(List<LayoutRun>))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class LegacyLayoutJsonContext : JsonSerializerContext;
