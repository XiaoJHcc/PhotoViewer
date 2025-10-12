using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        // 监听尺寸变化以检测屏幕方向
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateScreenOrientation(e.NewSize.Width, e.NewSize.Height);
        }
    }
}