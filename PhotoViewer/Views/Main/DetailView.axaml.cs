using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class DetailView : UserControl
{
    private DetailViewModel? _viewModel;

    public DetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => UpdatePreviewSize();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.Items.CollectionChanged -= OnItemsCollectionChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as DetailViewModel;

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
        if (e.PropertyName == nameof(DetailViewModel.IsVerticalLayout))
        {
            UpdatePreviewSize();
        }
    }

    private void UpdatePreviewSize()
    {
        if (_viewModel == null)
        {
            return;
        }

        var available = _viewModel.IsVerticalLayout ? Bounds.Height : Bounds.Width;
        if (available <= 0)
        {
            return;
        }

        const double padding = 10;
        const double spacing = 8;
        var count = Math.Max(1, _viewModel.Items.Count);
        var usable = Math.Max(0, available - padding * 2 - spacing * (count - 1));
        var size = Math.Floor(usable / count);
        if (size < 1)
        {
            size = 1;
        }

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
