using Avalonia.Controls;
using Avalonia.Data.Converters;
using PhotoViewer.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace PhotoViewer.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}

// 布局模式描述转换器
public class LayoutModeDescriptionConverter : IValueConverter
{
    public static readonly LayoutModeDescriptionConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ObservableCollection<SettingsViewModel.LayoutModeItem> layoutModes && 
            parameter is LayoutMode currentMode)
        {
            var item = layoutModes.FirstOrDefault(x => x.Value == currentMode);
            return item?.Description ?? "";
        }
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
