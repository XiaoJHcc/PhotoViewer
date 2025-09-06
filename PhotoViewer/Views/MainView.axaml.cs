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

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            Loaded += (_, _) =>
            {

                var insetsManager = TopLevel.GetTopLevel(this).InsetsManager;
                insetsManager.DisplayEdgeToEdgePreference = true;
                insetsManager.IsSystemBarVisible = false;
            };
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateScreenOrientation(e.NewSize.Width, e.NewSize.Height);
        }
    }
}