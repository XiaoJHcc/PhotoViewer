using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PhotoViewer.Controls;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Views;

public partial class ControlSettingsView : UserControl
{
    public ControlSettingsView()
    {
        InitializeComponent();
    }

    private void OnPrimaryHotkeyChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is HotkeyButton hotkeyButton && hotkeyButton.DataContext is SettingsViewModel.HotkeyItem item)
        {
            item.PrimaryHotkey = hotkeyButton.Hotkey;
        }
    }

    private void OnSecondaryHotkeyChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is HotkeyButton hotkeyButton && hotkeyButton.DataContext is SettingsViewModel.HotkeyItem item)
        {
            item.SecondaryHotkey = hotkeyButton.Hotkey;
        }
    }
}