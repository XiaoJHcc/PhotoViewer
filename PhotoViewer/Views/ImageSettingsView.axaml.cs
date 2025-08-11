using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ImageSettingsView : UserControl
{
    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;
    
    public ImageSettingsView()
    {
        InitializeComponent();
    }

    private void OnScalePresetLostFocus(object? sender, RoutedEventArgs e)
    {
        ViewModel.ApplyScalePreset();
    }

    private void OnScalePresetKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ViewModel.ApplyScalePreset();
    }
}