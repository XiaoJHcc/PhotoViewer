using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Tools;
using Windows.Foundation;

namespace PhotoViewer.Desktop.Native;

/// <summary>
/// 工具窗口定向验收:真实窗口、三分类切换、EXIF 表格、DINO 瓦片、照片统计导出与单例/重开。
/// 由 <c>--pv-native-tools</c> 触发,只读自建隔离设置/数据库与自有素材。
/// </summary>
internal static class WindowsToolsAcceptance
{
    /// <summary>运行工具窗口验收。</summary>
    /// <param name="main">主视图模型</param>
    /// <param name="mainWindow">主窗口(打开自有素材)</param>
    /// <param name="presenter">工具窗口展示器</param>
    /// <returns>任务</returns>
    public static async Task RunAsync(
        MainViewModel main,
        WindowsMainWindow mainWindow,
        WindowsNativeToolsPresenter presenter)
    {
        var checks = new List<string>();
        try
        {
            Directory.CreateDirectory(WindowsMainWindow.SmokeOutputDirectory);
            var fixture = Path.Combine(WindowsMainWindow.SmokeOutputDirectory, "fixtures", "tools-exif.jpg");
            WindowsMainWindow.CreateExifFixture(fixture, 1);
            await mainWindow.OpenPathsAsync(new[] { fixture });
            await WaitAsync(() => main.CurrentFile?.ExifData != null && main.Tools.IsExifDetailEnabled, "EXIF 读取");
            await Task.Delay(120);

            main.OpenToolsWindow();
            var window = presenter.Window ?? throw new InvalidOperationException("工具窗口未创建");
            await WaitAsync(() => window.Root.XamlRoot != null && window.Root.ActualWidth > 0, "工具窗口布局");
            await WaitAsync(() => window.ExifPage.GroupCount > 0, "EXIF 分组渲染");
            window.Root.UpdateLayout();
            var scale = window.Root.XamlRoot!.RasterizationScale;
            var windowWidth = window.Window.AppWindow.Size.Width / scale;

            Require(window.Root.RequestedTheme == ElementTheme.Dark, "工具窗口不是深色主题");
            Require(window.Window.SystemBackdrop is MicaBackdrop, "工具窗口没有 Mica 背景");
            Require(window.Navigation is NavigationView, "工具窗口分类不是 NavigationView");
            Require(window.TitleBarControl is TitleBar, "工具窗口没有 TitleBar 控件");
            Require(window.Navigation.MenuItems.Count == 3, "工具窗口分类数不是 3");
            Require(window.Pages.Count == 3, "工具窗口页面数不是 3");
            Require(!window.Window.AppWindow.IsShownInSwitchers, "工具窗口不应进入任务栏");
            Require(Math.Abs(windowWidth - WindowsLayout.ToolsWindowWidth) < 4,
                $"工具窗口宽度不是 960 DIP: {windowWidth:0.0}");
            Require(window.SelectedPage == ToolsPage.Exif, "默认页不是 EXIF 详情");
            checks.Add($"PASS window: dark, Mica, NavigationView + TitleBar, 3 categories, window={windowWidth:0} DIP");

            Require(window.ExifPage.FileNameText == Path.GetFileNameWithoutExtension(fixture),
                "EXIF 页文件名不含后缀");
            Require(window.ExifPage.GroupCount > 0 && window.ExifPage.TagCount > 0,
                "EXIF 页没有渲染分组/标签");
            Require(window.ExifPage.FormatCount == 1 && !window.ExifPage.FormatSelectorVisible,
                "单格式素材不应显示格式切换器");
            checks.Add($"PASS EXIF detail: {window.ExifPage.GroupCount} groups / {window.ExifPage.TagCount} tags, single format label");

            window.Navigation.SelectedItem = window.Navigation.MenuItems[(int)ToolsPage.Dino];
            await WaitAsync(() => window.SelectedPage == ToolsPage.Dino && main.Tools.CurrentTool is DinoDebugViewModel, "切到 DINO 诊断");
            await WaitAsync(() => main.Tools.DinoPresenter.Status == Path.GetFileName(fixture), "DINO 诊断计算", 120);
            await WaitAsync(() => window.DinoPage.HasAllTiles, "DINO 五张瓦片出图", 30);
            Require(window.DinoPage.TileCount == 5, "DINO 瓦片数不是 5");
            Require(window.DinoPage.HasThumbnail, "DINO 缩略图瓦片没有位图");

            // 作者 2026-10-17 反馈:两排瓦片大小不同、无说明文字的缩略图被垂直居中。
            // 五张瓦片按 3+2 同宽网格排列,两排正方形上沿必须齐平。
            window.Root.UpdateLayout();
            var dinoTiles = window.DinoPage.Tiles;
            double minSide = dinoTiles.Min(tile => tile.Side);
            double maxSide = dinoTiles.Max(tile => tile.Side);
            Require(minSide > 0 && maxSide - minSide <= 1,
                $"DINO 瓦片宽度不一致: {minSide:0.#}..{maxSide:0.#}");
            double[] TopOfRow(int start, int count) => dinoTiles
                .Skip(start)
                .Take(count)
                .Select(tile => tile.TransformToVisual(window.DinoPage).TransformPoint(new Point(0, 0)).Y)
                .ToArray();
            var firstRowTops = TopOfRow(0, 3);
            var secondRowTops = TopOfRow(3, 2);
            Require(firstRowTops.Max() - firstRowTops.Min() <= 1, "DINO 第一排瓦片未顶部对齐");
            Require(secondRowTops.Max() - secondRowTops.Min() <= 1, "DINO 第二排瓦片未顶部对齐");

            main.Tools.DinoPresenter.OnTileClicked(0.25, 0.25);
            await WaitAsync(() => main.Tools.DinoPresenter.RefGridX == 8 && main.Tools.DinoPresenter.RefGridY == 8,
                "DINO 瓦片点击重算 cosine 参考点");
            checks.Add($"PASS DINO diagnostics: 5 tiles ({maxSide:0}px wide, 3+2 top-aligned), status = {window.DinoPage.StatusText}, click sets reference patch 8,8");

            window.Navigation.SelectedItem = window.Navigation.MenuItems[(int)ToolsPage.Stats];
            await WaitAsync(() => window.SelectedPage == ToolsPage.Stats && main.Tools.CurrentTool is PhotoStatsViewModel, "切到照片数据统计");
            window.Root.UpdateLayout();
            Require(window.StatsPage.FolderRowCount == 0, "统计页初始文件夹列表不为空");

            var folder = Path.GetDirectoryName(fixture)!;
            main.Tools.PhotoStats.AddFolder(folder);
            await WaitAsync(() => window.StatsPage.FolderRowCount == 1, "统计页文件夹行");
            window.StatsPage.PatternBox.Text = "*.jpg";
            await WaitAsync(() => main.Tools.PhotoStats.Patterns == "*.jpg", "通配符写回统计 VM");

            var csv = Path.Combine(WindowsMainWindow.SmokeOutputDirectory, "tools-stats.csv");
            if (File.Exists(csv))
            {
                File.Delete(csv);
            }

            await main.Tools.PhotoStats.ExportAsync(csv);
            Require(File.Exists(csv), "CSV 未生成");
            Require(window.StatsPage.StatusText.Contains("完成", StringComparison.Ordinal),
                $"统计页状态文本未显示完成: {window.StatsPage.StatusText}");

            window.StatsPage.SelectFolderForAcceptance(0);
            window.StatsPage.RemoveSelectedFolder();
            await WaitAsync(() => window.StatsPage.FolderRowCount == 0, "移除统计文件夹");
            checks.Add("PASS photo stats: folder rows, pattern write-back, CSV export, remove");

            var previous = window;
            previous.Close();
            await WaitAsync(() => !presenter.IsOpen, "工具窗口关闭");
            main.OpenToolsWindow();
            var reopened = presenter.Window ?? throw new InvalidOperationException("工具窗口未重开");
            await WaitAsync(() => reopened.Root.XamlRoot != null && reopened.Root.ActualWidth > 0, "工具窗口重开布局");
            Require(ReferenceEquals(reopened, previous) == false, "重开应创建新窗口");
            Require(reopened.SelectedPage == ToolsPage.Exif, "重开后默认页不是 EXIF 详情");
            Require(reopened.StatsPage.FolderRowCount == 0, "重开后共享统计状态未保留");
            checks.Add("PASS lifecycle: singleton via presenter, close + reopen returns to default page with shared state");

            presenter.CloseIfOpen();
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            checks.Add("FAIL " + exception);
            NativeStartupLog.Fail("native tools acceptance", exception);
            presenter.CloseIfOpen();
        }
        finally
        {
            File.WriteAllText(
                Path.Combine(WindowsMainWindow.SmokeOutputDirectory, "tools-checks.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>保持工具窗口打开,供人工目视与截图;不写验收报告。</summary>
    /// <param name="main">主视图模型</param>
    /// <param name="mainWindow">主窗口(打开自有素材)</param>
    /// <param name="presenter">工具窗口展示器</param>
    /// <returns>任务</returns>
    public static async Task PrepareHoldAsync(
        MainViewModel main,
        WindowsMainWindow mainWindow,
        WindowsNativeToolsPresenter presenter)
    {
        var fixture = Path.Combine(WindowsMainWindow.SmokeOutputDirectory, "fixtures", "tools-hold.jpg");
        WindowsMainWindow.CreateExifFixture(fixture, 1);
        await mainWindow.OpenPathsAsync(new[] { fixture });
        await WaitAsync(() => main.CurrentFile?.ExifData != null, "工具窗口素材 EXIF");
        main.OpenToolsWindow();
        var window = presenter.Window ?? throw new InvalidOperationException("工具窗口未创建");
        await WaitAsync(() => window.Root.XamlRoot != null && window.Root.ActualWidth > 0, "工具窗口布局");
    }

    /// <summary>轮询等待条件成立。</summary>
    /// <param name="condition">条件</param>
    /// <param name="what">失败描述</param>
    /// <param name="timeoutSeconds">超时秒数</param>
    /// <returns>任务</returns>
    private static async Task WaitAsync(Func<bool> condition, string what, int timeoutSeconds = 30)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutSeconds * 1000L)
            {
                throw new TimeoutException("等待超时:" + what);
            }

            await Task.Delay(50);
        }
    }

    /// <summary>断言。</summary>
    /// <param name="condition">条件</param>
    /// <param name="message">失败描述</param>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
