using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using PhotoViewer.Controls;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Views;

/// <summary>
/// 分析栏视图。承载 6 项(对焦点 / 中心 / PCA / Cosine / 锐度 / 抖动);DiagnosticTile 共享准星与
/// cosine 参考点;DetailPreview 自己处理 hover/double-tap → 主图绿框联动。
/// </summary>
public partial class AnalysisView : UserControl
{
    private AnalysisViewModel? _viewModel;

    /// <summary>初始化分析栏视图。</summary>
    public AnalysisView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => UpdatePreviewSize();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>诊断瓦片点击:转发到 VM 落准星 + 重算 cosine。</summary>
    private void OnTileClicked(object? sender, TileClickedEventArgs e)
    {
        if (DataContext is not AnalysisViewModel vm) return;
        vm.OnTileClicked(e.U, e.V);
    }

    /// <summary>列表外背景点击:清空准星(瓦片内点击已被 Handled)。</summary>
    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        if (DataContext is not AnalysisViewModel vm) return;
        vm.ClearCrosshair();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.Items.CollectionChanged -= OnItemsCollectionChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as AnalysisViewModel;

        if (_viewModel != null)
        {
            _viewModel.Items.CollectionChanged += OnItemsCollectionChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdatePreviewSize();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdatePreviewSize();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalysisViewModel.IsRowLayout))
        {
            UpdatePreviewSize();
        }
    }

    /// <summary>
    /// 行布局占满宽度,列布局占满高度;按项数均分,确保单列/单行不裁切。
    /// </summary>
    private void UpdatePreviewSize()
    {
        if (_viewModel == null) return;

        var available = _viewModel.IsRowLayout ? Bounds.Width : Bounds.Height;
        if (available <= 0) return;

        const double padding = 10;
        const double spacing = 8;
        var count = Math.Max(1, _viewModel.Items.Count);
        var usable = Math.Max(0, available - padding * 2 - spacing * (count - 1));
        var size = Math.Floor(usable / count);
        if (size < 1) size = 1;

        if (Math.Abs(_viewModel.PreviewSize - size) > 0.5)
        {
            _viewModel.PreviewSize = size;
        }

        var cropSize = Math.Max(1, (int)Math.Round(size));
        if (_viewModel.CropSize != cropSize)
        {
            _viewModel.CropSize = cropSize;
        }
    }
}
