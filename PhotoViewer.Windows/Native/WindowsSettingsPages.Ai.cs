using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Windows.Native;

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

        var maxResults = NewNumberBox(_settings.SimilarityMaxResults,
            SettingsViewModel.SimilarityMaxResultsMin,
            SettingsViewModel.SimilarityMaxResultsMax,
            1,
            value => _settings.SimilarityMaxResults = (int)Math.Round(value));
        OnSetting(nameof(SettingsViewModel.SimilarityMaxResults),
            () => maxResults.Value = _settings.SimilarityMaxResults);

        var immediate = NewToggle(_settings.AnalysisImmediateCompute,
            value => _settings.AnalysisImmediateCompute = value);
        OnSetting(nameof(SettingsViewModel.AnalysisImmediateCompute),
            () => immediate.IsOn = _settings.AnalysisImmediateCompute);

        var clearButton = NewButton("清除特征数据库", () => _ = ClearDatabaseAsync());
        clearButton.IsEnabled = !_settings.ClearDbBusy;
        OnSetting(nameof(SettingsViewModel.ClearDbBusy),
            () => clearButton.IsEnabled = !_settings.ClearDbBusy);

        var statusBar = new InfoBar
        {
            IsOpen = !string.IsNullOrEmpty(_settings.ClearDbStatus),
            Message = _settings.ClearDbStatus,
            Severity = InfoBarSeverity.Informational,
            IsClosable = false,
        };
        OnSetting(nameof(SettingsViewModel.ClearDbStatus), () =>
        {
            statusBar.Message = _settings.ClearDbStatus;
            statusBar.IsOpen = !string.IsNullOrEmpty(_settings.ClearDbStatus);
        });

        var clearRow = WindowsSettingsCard.Row(
            "清除已提取的 DINO / CV 特征(不可撤销)",
            "删除 photos.db 中的特征与派生缓存，之后可重新提取",
            clearButton);

        var similarityGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        similarityGroup.Children.Add(WindowsSettingsCard.SliderRow("相似度阈值", threshold, thresholdValue));
        similarityGroup.Children.Add(WindowsSettingsCard.Row(
            "最多数量", "相似聚类面板最多显示的相似照片数", NumberBoxWithUnit(maxResults, "张")));

        var diagnosisGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        diagnosisGroup.Children.Add(WindowsSettingsCard.ToggleRow("即时计算DINO/CV", null, immediate));

        var databaseGroup = new StackPanel { Spacing = WindowsLayout.SettingsCardGap };
        databaseGroup.Children.Add(clearRow);
        databaseGroup.Children.Add(statusBar);

        return Page(
            "AI",
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

        // SETTINGS-10：已有系统确认对话框，直接调用共享“已确认”入口，不进入不可见的 arm 态。
        await _settings.ClearFeatureDatabaseConfirmedAsync();
    }
}
