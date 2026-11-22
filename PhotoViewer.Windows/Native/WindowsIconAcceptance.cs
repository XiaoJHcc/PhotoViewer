using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.Core.Design;
using PhotoViewer.Shared;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsMainWindow
{
    /// <summary>ICON-05 独立图标验收：主窗口自绘槽与原生菜单，另核对设置/工具窗口的标题栏与分类图标。</summary>
    /// <param name="settingsPresenter">设置窗口展示器（<c>--pv-native-icons</c> 传入）。</param>
    /// <param name="toolsPresenter">工具窗口展示器（<c>--pv-native-icons</c> 传入）。</param>
    /// <returns>任务。</returns>
    internal async Task RunIconChecksAsync(
        WindowsNativeSettingsPresenter? settingsPresenter = null,
        WindowsNativeToolsPresenter? toolsPresenter = null)
    {
        var checks = new List<string>();
        try
        {
            PlaceSmokeWindowInWorkArea();
            // 像素核对要按 XamlRoot 的光栅化倍率换算，先等首帧布局落定。
            await WaitForChromeIconsAsync(_root, "主窗口");
            await CheckIconInkAlignmentAsync(checks);
            await CheckContextMenuIconsAsync(checks);
            checks.Add("PASS MENU-06 native flyout uses catalog icons for actions and toggles");
            if (settingsPresenter != null)
            {
                await CheckSettingsWindowIconsAsync(checks, settingsPresenter);
            }

            if (toolsPresenter != null)
            {
                await CheckToolsWindowIconsAsync(checks, toolsPresenter);
            }
        }
        catch (Exception ex)
        {
            checks.Add("FAIL " + ex);
            Environment.ExitCode = 1;
        }

        File.WriteAllLines(Path.Combine(SmokeOutputDirectory, "icon-checks.txt"), checks);
    }

    /// <summary>
    /// ICON-05：回读原生 WinUI 字形与原生菜单宿主的实际像素。墨迹中心按"槽中心 + 生成字形的设计锚点偏移"
    /// 核对，尺寸按同字号超大槽的对照渲染核对，模板缩放、对称裁切与槽不足都会暴露出来。
    /// </summary>
    /// <param name="checks">报告行收集器。</param>
    /// <returns>任务。</returns>
    private async Task CheckIconInkAlignmentAsync(List<string> checks)
    {
        // 自绘槽：命令圆按钮、评分槽与胶片卡片星形的按钮几何是共享令牌，槽尺寸与命中区独立于字形。
        var samples = new[]
        {
            new WindowsIconMetrics.IconSample(
                "命令 " + IconCatalog.CommandOpen, IconCatalog.FluentGlyph(IconCatalog.CommandOpen),
                DesignTokens.ToolbarCommandGlyphFontSize, ToolbarFlowLayout.CommandSize, false, "自绘命令圆按钮"),
            new WindowsIconMetrics.IconSample(
                "命令 " + IconCatalog.CommandNext, IconCatalog.FluentGlyph(IconCatalog.CommandNext),
                DesignTokens.ToolbarCommandGlyphFontSize, ToolbarFlowLayout.CommandSize, false, "自绘命令圆按钮"),
            new WindowsIconMetrics.IconSample(
                "评分槽 " + IconCatalog.RatingStarFilled, IconCatalog.FluentGlyph(IconCatalog.RatingStarFilled),
                DesignTokens.ToolbarRatingGlyphFontSize, RatingSourcesLayout.SlotSize, true, "自绘评分槽"),
            new WindowsIconMetrics.IconSample(
                "评分槽 " + IconCatalog.RatingWarning, IconCatalog.FluentGlyph(IconCatalog.RatingWarning),
                DesignTokens.ToolbarRatingGlyphFontSize, RatingSourcesLayout.SlotSize, true, "自绘评分槽"),
            new WindowsIconMetrics.IconSample(
                "评分槽 " + IconCatalog.RatingClear, IconCatalog.FluentGlyph(IconCatalog.RatingClear),
                DesignTokens.ToolbarRatingClearGlyphFontSize, RatingSourcesLayout.SlotSize, true, "自绘评分槽"),
            new WindowsIconMetrics.IconSample(
                "胶片卡片 " + IconCatalog.RatingStar, IconCatalog.FluentGlyph(IconCatalog.RatingStar),
                DesignTokens.FilmCardStarFontSize, DesignTokens.FilmCardStarSize, true, "自绘卡片星形"),
        };
        foreach (var sample in samples)
        {
            await WindowsIconMetrics.CheckAsync(_root, sample, RequireSmoke, checks);
        }
    }

    /// <summary>打开设置窗口，核对标题栏与五个分类的图标像素。</summary>
    /// <param name="checks">报告行收集器。</param>
    /// <param name="presenter">设置窗口展示器。</param>
    /// <returns>任务。</returns>
    private async Task CheckSettingsWindowIconsAsync(List<string> checks, WindowsNativeSettingsPresenter presenter)
    {
        _main.OpenSettingWindow();
        var window = presenter.Window ?? throw new InvalidOperationException("设置窗口未创建");
        await WaitForChromeIconsAsync(window.Root, "设置窗口");
        var navItems = WindowsSettingsWindow.NavigationCategories
            .Select(entry => (entry.Title, WindowsIcons.ForCategory(entry.Key)))
            .ToArray();
        await WindowsIconMetrics.CheckWindowChromeAsync(
            window.Root,
            window.TitleBarControl,
            window.Navigation,
            new WindowsIconMetrics.ChromeExpectations(
                "设置窗口",
                WindowsIcons.SettingsGlyph,
                WindowsLayout.WindowTitleIconFontSize,
                WindowsLayout.WindowNavIconFontSize,
                WindowsLayout.WindowNavIconSlot),
            navItems,
            RequireSmoke,
            checks);
        window.Close();
    }

    /// <summary>打开工具窗口，核对标题栏与三个分类的图标像素。</summary>
    /// <param name="checks">报告行收集器。</param>
    /// <param name="presenter">工具窗口展示器。</param>
    /// <returns>任务。</returns>
    private async Task CheckToolsWindowIconsAsync(List<string> checks, WindowsNativeToolsPresenter presenter)
    {
        _main.OpenToolsWindow();
        var window = presenter.Window ?? throw new InvalidOperationException("工具窗口未创建");
        await WaitForChromeIconsAsync(window.Root, "工具窗口");
        var navItems = WindowsToolsWindow.NavigationCategories
            .Select(entry => (entry.Title, WindowsIcons.ForToolCategory(entry.Key)))
            .ToArray();
        await WindowsIconMetrics.CheckWindowChromeAsync(
            window.Root,
            window.TitleBarControl,
            window.Navigation,
            new WindowsIconMetrics.ChromeExpectations(
                "工具窗口",
                WindowsIcons.ToolsGlyph,
                WindowsLayout.WindowTitleIconFontSize,
                WindowsLayout.WindowNavIconFontSize,
                WindowsLayout.WindowNavIconSlot),
            navItems,
            RequireSmoke,
            checks);
        window.Close();
    }

    /// <summary>等设置/工具窗口完成首帧布局（模板要跑完才有标题栏图标与分类 IconBox）。</summary>
    /// <param name="root">窗口根元素。</param>
    /// <param name="label">窗口名称（失败提示）。</param>
    /// <returns>任务。</returns>
    private static async Task WaitForChromeIconsAsync(Grid root, string label)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (root.XamlRoot != null && root.ActualWidth > 0 && root.ActualHeight > 0)
            {
                root.UpdateLayout();
                await Task.Delay(60);
                return;
            }

            await Task.Delay(50);
        }

        RequireSmoke(false, $"{label}没有完成首次布局");
    }
}
