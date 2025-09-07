using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace PhotoViewer.Converters;

// 布局方向转换器
public class LayoutOrientationConverter : IValueConverter
{
    public static readonly LayoutOrientationConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? Orientation.Vertical : Orientation.Horizontal;
        }
        return Orientation.Horizontal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Orientation orientation)
        {
            return orientation == Orientation.Vertical;
        }
        return false;
    }
}

// 滚动条可见性转换器
public class ScrollBarVisibilityConverter : IValueConverter
{
    public static readonly ScrollBarVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            bool reverse = parameter?.ToString() == "Reverse";
            
            if (reverse)
            {
                return isVertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            }
            else
            {
                return isVertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            }
        }
        return ScrollBarVisibility.Auto;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下的水平对齐转换器
public class VerticalLayoutAlignmentConverter : IValueConverter
{
    public static readonly VerticalLayoutAlignmentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        }
        return HorizontalAlignment.Stretch;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下的垂直对齐转换器
public class VerticalLayoutVerticalAlignmentConverter : IValueConverter
{
    public static readonly VerticalLayoutVerticalAlignmentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        }
        return VerticalAlignment.Stretch;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 通用网格位置转换器 - 根据参数返回不同的行列位置
public class GridPositionConverter : IValueConverter
{
    public static readonly GridPositionConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical && parameter is string param)
        {
            // 参数格式: "VerticalRow,HorizontalColumn" 或 "VerticalColumn,HorizontalRow"
            // 例如: "0,0" 表示垂直布局时第0行，水平布局时第0列
            var parts = param.Split(',');
            if (parts.Length == 2 && 
                int.TryParse(parts[0], out int verticalValue) && 
                int.TryParse(parts[1], out int horizontalValue))
            {
                return isVertical ? verticalValue : horizontalValue;
            }
        }
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 通用边框转换器 - 根据参数返回不同的边框设置
public class BorderThicknessConverter : IValueConverter
{
    public static readonly BorderThicknessConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical && parameter is string param)
        {
            // 参数格式: "vertical|horizontal"
            // 例如: "0,0,0,1|0,0,1,0" 表示垂直布局时下边框，水平布局时右边框
            var parts = param.Split('|');
            if (parts.Length == 2)
            {
                var thicknessStr = isVertical ? parts[0] : parts[1];
                var values = thicknessStr.Split(',');
                if (values.Length == 4 &&
                    double.TryParse(values[0], out double left) &&
                    double.TryParse(values[1], out double top) &&
                    double.TryParse(values[2], out double right) &&
                    double.TryParse(values[3], out double bottom))
                {
                    return new Thickness(left, top, right, bottom);
                }
            }
        }
        return new Thickness(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 星级颜色转换器
public class StarColorConverter : IValueConverter
{
    public static readonly StarColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int currentRating && 
            parameter is string starIndexStr && 
            int.TryParse(starIndexStr, out int starIndex))
        {
            return currentRating >= starIndex ? 
                new SolidColorBrush(Colors.Gold) : 
                new SolidColorBrush(Colors.Gray);
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 网格尺寸转换器 - 根据布局方向返回不同的尺寸
public class GridSizeConverter : IValueConverter
{
    public static readonly GridSizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical && parameter is string param)
        {
            // 参数格式: "VerticalSize,HorizontalSize"
            // 例如: "80,Auto" 表示垂直布局时80像素，水平布局时自动
            // 例如: "*,80" 表示垂直布局时自适应，水平布局时80像素
            var parts = param.Split(',');
            if (parts.Length == 2)
            {
                var sizeStr = isVertical ? parts[0] : parts[1];
                
                if (sizeStr == "*")
                {
                    return new GridLength(1, GridUnitType.Star);
                }
                else if (sizeStr == "Auto")
                {
                    return new GridLength(1, GridUnitType.Auto);
                }
                else if (double.TryParse(sizeStr, out double pixels))
                {
                    return new GridLength(pixels, GridUnitType.Pixel);
                }
            }
        }
        return new GridLength(1, GridUnitType.Auto);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 星级按钮停靠方向转换器
public class StarDockConverter : IValueConverter
{
    public static readonly StarDockConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical && parameter is string param)
        {
            // 参数格式: "VerticalDock,HorizontalDock"
            // 例如: "Bottom,Left" 表示垂直布局时使用Bottom，水平布局时使用Left
            var parts = param.Split(',');
            if (parts.Length == 2)
            {
                var dockStr = isVertical ? parts[0] : parts[1];
                
                return dockStr switch
                {
                    "Left" => Dock.Left,
                    "Top" => Dock.Top,
                    "Right" => Dock.Right,
                    "Bottom" => Dock.Bottom,
                    _ => Dock.Left
                };
            }
        }
        return Dock.Left;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
