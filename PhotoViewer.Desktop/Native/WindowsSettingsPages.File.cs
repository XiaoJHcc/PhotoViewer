using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Desktop.Native;

internal sealed partial class WindowsSettingsPages
{
    /// <summary>文件页:支持格式、同名合并、缓存与预加载。</summary>
    /// <returns>页面</returns>
    private UIElement BuildFilePage()
    {
        var formats = new WindowsSettingsList(
            _settings.FileFormats,
            item => BuildFormatRow((SettingsViewModel.FileFormatItem)item),
            _settings.MoveFileFormat,
            WindowsLayout.SettingsListRowHeightTall);
        _detach.Add(formats.Detach);

        var sameName = NewToggle(_settings.SameNameAsOnePhoto, value => _settings.SameNameAsOnePhoto = value);
        OnSetting(nameof(SettingsViewModel.SameNameAsOnePhoto), () => sameName.IsOn = _settings.SameNameAsOnePhoto);

        var nativeThreads = NewSlider(1, _settings.NativePreloadParallelismMaximum, 1,
            _settings.NativePreloadParallelism,
            value => _settings.NativePreloadParallelism = (int)Math.Round(value));
        var nativeThreadsValue = WindowsSettingsCard.ValueText($"{_settings.NativePreloadParallelism} 线程");
        OnSetting(nameof(SettingsViewModel.NativePreloadParallelism), () =>
        {
            nativeThreads.Value = _settings.NativePreloadParallelism;
            nativeThreadsValue.Text = $"{_settings.NativePreloadParallelism} 线程";
        });

        var cpuThreads = NewSlider(1, _settings.CpuPreloadParallelismMaximum, 1,
            _settings.CpuPreloadParallelism,
            value => _settings.CpuPreloadParallelism = (int)Math.Round(value));
        var cpuThreadsValue = WindowsSettingsCard.ValueText($"{_settings.CpuPreloadParallelism} 线程");
        OnSetting(nameof(SettingsViewModel.CpuPreloadParallelism), () =>
        {
            cpuThreads.Value = _settings.CpuPreloadParallelism;
            cpuThreadsValue.Text = $"{_settings.CpuPreloadParallelism} 线程";
        });

        var memoryValue = WindowsSettingsCard.ValueText($"{_settings.BitmapCacheMaxMemory} MB");
        var memorySlider = NewSlider(0, 1, 0.01, _settings.BitmapCacheMaxMemoryExp,
            value => _settings.BitmapCacheMaxMemoryExp = value);
        OnSetting(nameof(SettingsViewModel.BitmapCacheMaxMemory),
            () => memoryValue.Text = $"{_settings.BitmapCacheMaxMemory} MB");

        var countValue = WindowsSettingsCard.ValueText($"{_settings.BitmapCacheMaxCount} 张");
        var countSlider = NewSlider(0, 1, 0.01, _settings.BitmapCacheMaxCountExp,
            value => _settings.BitmapCacheMaxCountExp = value);
        OnSetting(nameof(SettingsViewModel.BitmapCacheMaxCount),
            () => countValue.Text = $"{_settings.BitmapCacheMaxCount} 张");

        var forward = NewSlider(0, _settings.PreloadMaximum, 1, _settings.PreloadForwardCount,
            value => _settings.PreloadForwardCount = (int)Math.Round(value));
        var backward = NewSlider(0, _settings.PreloadMaximum, 1, _settings.PreloadBackwardCount,
            value => _settings.PreloadBackwardCount = (int)Math.Round(value));
        var center = NewSlider(0, _settings.PreloadMaximum, 1, _settings.VisibleCenterPreloadCount,
            value => _settings.VisibleCenterPreloadCount = (int)Math.Round(value));
        var forwardValue = WindowsSettingsCard.ValueText($"{_settings.PreloadForwardCount} 张");
        var backwardValue = WindowsSettingsCard.ValueText($"{_settings.PreloadBackwardCount} 张");
        var centerValue = WindowsSettingsCard.ValueText($"{_settings.VisibleCenterPreloadCount} 张");
        OnSetting(nameof(SettingsViewModel.PreloadForwardCount), () =>
        {
            forward.Value = _settings.PreloadForwardCount;
            forwardValue.Text = $"{_settings.PreloadForwardCount} 张";
        });
        OnSetting(nameof(SettingsViewModel.PreloadBackwardCount), () =>
        {
            backward.Value = _settings.PreloadBackwardCount;
            backwardValue.Text = $"{_settings.PreloadBackwardCount} 张";
        });
        OnSetting(nameof(SettingsViewModel.VisibleCenterPreloadCount), () =>
        {
            center.Value = _settings.VisibleCenterPreloadCount;
            centerValue.Text = $"{_settings.VisibleCenterPreloadCount} 张";
        });
        OnSetting(nameof(SettingsViewModel.PreloadMaximum), () =>
        {
            forward.Maximum = _settings.PreloadMaximum;
            backward.Maximum = _settings.PreloadMaximum;
            center.Maximum = _settings.PreloadMaximum;
        });

        var delayValue = WindowsSettingsCard.ValueText($"{_settings.VisibleCenterDelayMs} ms");
        var delaySlider = NewSlider(0, 1, 0.01, _settings.VisibleCenterDelayExp,
            value => _settings.VisibleCenterDelayExp = value);
        OnSetting(nameof(SettingsViewModel.VisibleCenterDelayMs),
            () => delayValue.Text = $"{_settings.VisibleCenterDelayMs} ms");

        var budgetHint = WindowsSettingsCard.Hint(
            _settings.PerformanceBudgetInfo, _settings.PerformanceBudgetInfoLevel);
        void UpdateBudget()
        {
            budgetHint.Text = _settings.PerformanceBudgetInfo;
            budgetHint.Foreground = WindowsSettingsCard.Brush(
                WindowsSettingsCard.LevelColor(_settings.PerformanceBudgetInfoLevel));
        }

        OnSetting(nameof(SettingsViewModel.PerformanceBudgetInfo), UpdateBudget);
        OnSetting(nameof(SettingsViewModel.PerformanceBudgetInfoLevel), UpdateBudget);

        var countHint = WindowsSettingsCard.Hint(
            _settings.BitmapCacheCountInfo, _settings.BitmapCacheCountInfoLevel);
        void UpdateCountHint()
        {
            countHint.Text = _settings.BitmapCacheCountInfo;
            countHint.Foreground = WindowsSettingsCard.Brush(
                WindowsSettingsCard.LevelColor(_settings.BitmapCacheCountInfoLevel));
        }

        OnSetting(nameof(SettingsViewModel.BitmapCacheCountInfo), UpdateCountHint);
        OnSetting(nameof(SettingsViewModel.BitmapCacheCountInfoLevel), UpdateCountHint);

        var cacheCards = CardGroup();
        cacheCards.Children.Add(WindowsSettingsCard.SliderRow("原生解码线程", nativeThreads, nativeThreadsValue));
        cacheCards.Children.Add(WindowsSettingsCard.SliderRow("软件解码线程", cpuThreads, cpuThreadsValue));
        cacheCards.Children.Add(WindowsSettingsCard.SliderRow("缓存内存上限", memorySlider, memoryValue));
        cacheCards.Children.Add(WindowsSettingsCard.SliderRow("缓存数量上限", countSlider, countValue));
        cacheCards.Children.Add(WindowsSettingsCard.SliderRow("下一张预载数量", forward, forwardValue));
        cacheCards.Children.Add(WindowsSettingsCard.SliderRow("上一张预载数量", backward, backwardValue));
        cacheCards.Children.Add(WindowsSettingsCard.SliderRow("列表滚动预载数量", center, centerValue));
        cacheCards.Children.Add(WindowsSettingsCard.SliderRow("滚动预载延时", delaySlider, delayValue));

        var formatGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        formatGroup.Children.Add(WindowsSettingsCard.Card(formats, new Thickness(8)));

        var toggleGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        toggleGroup.Children.Add(WindowsSettingsCard.ToggleRow(
            "同名文件视为同一张图片",
            "按列表排序优先级高的作为代表显示图片，修改星级时同步至所有同名文件",
            sameName));

        return Page(
            "文件",
            WindowsSettingsCard.SectionHeader("支持的文件格式"),
            formatGroup,
            WindowsSettingsCard.SectionHeader("同名文件"),
            toggleGroup,
            WindowsSettingsCard.SectionHeader("缓存与预加载"),
            cacheCards,
            budgetHint,
            countHint);
    }

    /// <summary>文件格式行:勾选 + 格式名 + 扩展名 + 把手。</summary>
    /// <param name="format">格式项</param>
    /// <returns>行元素</returns>
    private WindowsSettingsRowElement BuildFormatRow(SettingsViewModel.FileFormatItem format)
    {
        var checkbox = new CheckBox
        {
            IsChecked = format.IsEnabled,
            Width = 28,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var updating = false;
        checkbox.Checked += (_, _) =>
        {
            if (!updating)
            {
                format.IsEnabled = true;
            }
        };
        checkbox.Unchecked += (_, _) =>
        {
            if (!updating)
            {
                format.IsEnabled = false;
            }
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(WindowsSettingsCard.RowTitle(format.DisplayName));
        text.Children.Add(WindowsSettingsCard.RowDescription(format.ExtensionsText));

        var grid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(checkbox);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var handle = WindowsSettingsCard.DragHandle();
        Grid.SetColumn(handle, 2);
        grid.Children.Add(handle);

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(SettingsViewModel.FileFormatItem.IsEnabled))
            {
                return;
            }

            updating = true;
            checkbox.IsChecked = format.IsEnabled;
            updating = false;
        };
        format.PropertyChanged += handler;

        var row = new WindowsSettingsRowElement(() => format.PropertyChanged -= handler);
        row.Children.Add(grid);
        return row;
    }
}
