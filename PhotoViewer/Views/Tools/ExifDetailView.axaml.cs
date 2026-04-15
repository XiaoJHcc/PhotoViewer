using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ExifDetailView : UserControl
{
    private double _savedScrollY = 0;
    private bool _pendingScrollRestore = false;
    private ExifDetailViewModel? _boundVm;

    public ExifDetailView()
    {
        InitializeComponent();

        // 只在非恢复期间跟踪滚动位置，防止内容重建时 Y=0 覆盖保存值
        ScrollContent.ScrollChanged += (_, _) =>
        {
            if (!_pendingScrollRestore)
                _savedScrollY = ScrollContent.Offset.Y;
        };

        // 等待布局稳定后恢复：仅当 Extent 已足够高时才执行，避免过早恢复被 clamp 到 0
        ScrollContent.LayoutUpdated += (_, _) =>
        {
            if (!_pendingScrollRestore) return;
            if (_savedScrollY <= 0)
            {
                _pendingScrollRestore = false;
                return;
            }
            var maxScroll = ScrollContent.Extent.Height - ScrollContent.Viewport.Height;
            if (maxScroll > 0)
            {
                _pendingScrollRestore = false;
                ScrollContent.Offset = new Vector(ScrollContent.Offset.X, Math.Min(_savedScrollY, maxScroll));
            }
        };

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm != null)
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = DataContext as ExifDetailViewModel;
        if (_boundVm != null)
            _boundVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ExifDetailViewModel.Groups)) return;
        _pendingScrollRestore = true;
    }
}
