using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace PhotoViewer.Converters;

// 布局方向转换器：isRow=true（按行布局，上下分栏）→ Horizontal；isRow=false（按列布局，左右分栏）→ Vertical
public class LayoutOrientationConverter : IValueConverter
{
    public static readonly LayoutOrientationConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isRow)
        {
            return isRow ? Orientation.Horizontal : Orientation.Vertical;
        }
        return Orientation.Horizontal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Orientation orientation)
        {
            return orientation == Orientation.Horizontal;
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
        if (value is bool isRow)
        {
            bool reverse = parameter?.ToString() == "Reverse";

            if (reverse)
            {
                // 按列布局（isRow=false）竖向滚动 → Auto
                return isRow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            }
            else
            {
                // 按行布局（isRow=true）横向滚动 → Auto
                return isRow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            }
        }
        return ScrollBarVisibility.Auto;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 行布局下的水平对齐转换器
// isRow=false（按列布局，控件纵向堆叠）= 侧边布局，返回参数指定值（默认 Stretch）；
// isRow=true（按行布局，控件横向排列）= 顶部布局，返回 Center。
public class VerticalLayoutAlignmentConverter : IValueConverter
{
    public static readonly VerticalLayoutAlignmentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isRow)
        {
            if (isRow) return HorizontalAlignment.Center;
            return parameter?.ToString() switch
            {
                "Left" => HorizontalAlignment.Left,
                "Center" => HorizontalAlignment.Center,
                "Right" => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Stretch
            };
        }
        return HorizontalAlignment.Stretch;
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
        if (value is bool isRow && parameter is string param)
        {
            // 参数格式: "RowValue,ColValue"
            var parts = param.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int rowValue) &&
                int.TryParse(parts[1], out int colValue))
            {
                return isRow ? rowValue : colValue;
            }
        }
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 通用边框边距转换器 - 根据布局方向返回不同的外边距
// Margin / Padding / BorderThickness 通用
// 顺序为 Web CSS 规范 (上, 右, 下, 左)
// 参数格式: "row|col"，isRow=true 取第一段，isRow=false 取第二段
public class WebMarginConverter : IValueConverter
{
    public static readonly WebMarginConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isRow && parameter is string param)
        {
            var parts = param.Split('|');
            if (parts.Length == 2)
            {
                var marginStr = isRow ? parts[0] : parts[1];
                var values = marginStr.Split(',');
                double top, right, bottom, left;
                switch (values.Length)
                {
                    case 4 when
                        double.TryParse(values[0], out top) &&
                        double.TryParse(values[1], out right) &&
                        double.TryParse(values[2], out bottom) &&
                        double.TryParse(values[3], out left):
                        return new Thickness(left, top, right, bottom);
                    case 3 when
                        double.TryParse(values[0], out top) &&
                        double.TryParse(values[1], out right) &&
                        double.TryParse(values[2], out bottom):
                        return new Thickness(right, top, right, bottom);
                    case 2 when
                        double.TryParse(values[0], out top) &&
                        double.TryParse(values[1], out right):
                        return new Thickness(right, top, right, top);
                    case 1 when
                        double.TryParse(values[0], out top):
                        return new Thickness(top);
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

// 网格尺寸转换器 - 根据布局方向返回不同的尺寸
// 参数格式: "RowSize,ColSize"，isRow=true 取第一段，isRow=false 取第二段
public class GridSizeConverter : IValueConverter
{
    public static readonly GridSizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isRow && parameter is string param)
        {
            var parts = param.Split(',');
            if (parts.Length == 2)
            {
                var sizeStr = isRow ? parts[0] : parts[1];
                
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
// 参数格式: "RowDock,ColDock"，isRow=true 取第一段，isRow=false 取第二段
public class DockConverter : IValueConverter
{
    public static readonly DockConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isRow && parameter is string param)
        {
            var parts = param.Split(',');
            if (parts.Length == 2)
            {
                var dockStr = isRow ? parts[0] : parts[1];
                
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