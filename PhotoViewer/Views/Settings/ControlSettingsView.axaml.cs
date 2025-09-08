using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PhotoViewer.Controls;
using PhotoViewer.ViewModels;
using Avalonia.Data.Converters;

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
            
            // 触发冲突检测
            if (DataContext is SettingsViewModel settingsViewModel)
            {
                settingsViewModel.CheckHotkeyConflicts();
            }
        }
    }

    private void OnSecondaryHotkeyChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is HotkeyButton hotkeyButton && hotkeyButton.DataContext is SettingsViewModel.HotkeyItem item)
        {
            item.SecondaryHotkey = hotkeyButton.Hotkey;
            
            // 触发冲突检测
            if (DataContext is SettingsViewModel settingsViewModel)
            {
                settingsViewModel.CheckHotkeyConflicts();
            }
        }
    }
}