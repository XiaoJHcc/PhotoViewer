using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Desktop.Native;

internal sealed partial class WindowsSettingsPages
{
    /// <summary>AI 页:相似聚类、分析诊断、特征数据库清理。</summary>
    /// <returns>页面</returns>
    private UIElement BuildAiPage()
    {
        var thresholdValue = WindowsSettingsCard.ValueText(
            (_settings.SimilarityThreshold * 100).ToString("0") + "%");
        var threshold = NewSlider(75, 95, 1, _settings.SimilarityThreshold * 100,
            value => _settings.SimilarityThreshold = value / 100.0);
        void RefreshThreshold()
        {
            var percent = _settings.SimilarityThreshold * 100;
            threshold.Value = percent;
            thresholdValue.Text = percent.ToString("0") + "%";
        }

        OnSetting(nameof(SettingsViewModel.SimilarityThreshold), RefreshThreshold);

        var maxValue = WindowsSettingsCard.ValueText($"{_settings.SimilarityMaxResults} 张");
        var maxSlider = NewSlider(
            SettingsViewModel.SimilarityMaxResultsMin,
            SettingsViewModel.SimilarityMaxResultsMax,
            1,
            _settings.SimilarityMaxResults,
            value => _settings.SimilarityMaxResults = (int)Math.Round(value));
        void RefreshMaxResults()
        {
            maxSlider.Value = _settings.SimilarityMaxResults;
            maxValue.Text = $"{_settings.SimilarityMaxResults} 张";
        }

        OnSetting(nameof(SettingsViewModel.SimilarityMaxResults), RefreshMaxResults);

        var immediate = NewToggle(_settings.AnalysisImmediateCompute,
            value => _settings.AnalysisImmediateCompute = value);
        OnSetting(nameof(SettingsViewModel.AnalysisImmediateCompute),
            () => immediate.IsOn = _settings.AnalysisImmediateCompute);

        var clearButton = NewButton("清除特征数据库", () => _ = ClearDatabaseAsync());
        clearButton.IsEnabled = !_settings.ClearDbBusy;
        OnSetting(nameof(SettingsViewModel.ClearDbBusy),
            () => clearButton.IsEnabled = !_settings.ClearDbBusy);

        var statusText = new TextBlock
        {
            Text = _settings.ClearDbStatus,
            FontSize = WindowsLayout.SettingsRowDescriptionFontSize,
            Foreground = WindowsSettingsCard.Brush(WindowsLayout.ExifLabelForeground),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var statusContent = new StackPanel { Orientation = Orientation.Horizontal };
        statusContent.Children.Add(new FontIcon
        {
            Glyph = "\uE946",
            FontSize = 14,
            Foreground = WindowsSettingsCard.Brush(WindowsLayout.ExifLabelForeground),
            VerticalAlignment = VerticalAlignment.Center,
        });
        statusContent.Children.Add(statusText);
        var statusBorder = WindowsSettingsCard.Card(statusContent, new Thickness(12));
        statusBorder.Visibility = string.IsNullOrEmpty(_settings.ClearDbStatus)
            ? Visibility.Collapsed
            : Visibility.Visible;

        OnSetting(nameof(SettingsViewModel.ClearDbStatus), () =>
        {
            statusText.Text = _settings.ClearDbStatus;
            statusBorder.Visibility = string.IsNullOrEmpty(_settings.ClearDbStatus)
                ? Visibility.Collapsed
                : Visibility.Visible;
        });

        var clearRow = WindowsSettingsCard.Row(
            "清除已提取的 DINO / CV 特征(不可撤销)",
            "删除 photos.db 中的特征与派生缓存，之后可重新提取",
            clearButton);

        var similarityGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        similarityGroup.Children.Add(WindowsSettingsCard.SliderRow("相似度阈值", threshold, thresholdValue));
        similarityGroup.Children.Add(WindowsSettingsCard.SliderRow("最多数量", maxSlider, maxValue));

        var diagnosisGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        diagnosisGroup.Children.Add(WindowsSettingsCard.ToggleRow("即时计算DINO/CV", null, immediate));

        var databaseGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        databaseGroup.Children.Add(clearRow);
        databaseGroup.Children.Add(statusBorder);

        return Page(            "AI",
            WindowsSettingsCard.SectionHeader("相似聚类"),
            similarityGroup,
            WindowsSettingsCard.SectionHeader("分析诊断"),
            diagnosisGroup,
            WindowsSettingsCard.SectionHeader("特征数据库"),
            databaseGroup);
    }
    /// <summary>清库:ContentDialog 确认后走共享两段确认命令。</summary>
    /// <returns>任务</returns>
    private async Task ClearDatabaseAsync()
    {
        if (_settings.ClearDbBusy)
        {
            return;
        }

        var root = Root;
        if (root == null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "清除特征数据库",
            Content = "已提取的 DINO / CV 特征与派生缓存会被删除，无法撤销。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        // 共享命令是两段确认:第一次只 arm,第二次才真正执行。
        if (!_settings.ClearDbConfirming)
        {
            await _settings.ClearDatabaseCommand.Execute();
        }

        await _settings.ClearDatabaseCommand.Execute();
    }
}
