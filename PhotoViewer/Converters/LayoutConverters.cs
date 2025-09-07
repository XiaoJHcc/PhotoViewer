using System;
using System.Globalization;
using Avalonia;
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

// 垂直布局下控制区域行位置转换器
public class VerticalControlAreaRowConverter : IValueConverter
{
    public static readonly VerticalControlAreaRowConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? 0 : 0; // 垂直布局时在第一行，水平布局时也在第一行
        }
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下控制区域列位置转换器
public class VerticalControlAreaColumnConverter : IValueConverter
{
    public static readonly VerticalControlAreaColumnConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? 0 : 0; // 垂直布局时在第一列，水平布局时也在第一列
        }
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下EXIF区域行位置转换器
public class VerticalExifAreaRowConverter : IValueConverter
{
    public static readonly VerticalExifAreaRowConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? 1 : 0; // 垂直布局时在第二行，水平布局时在第一行
        }
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下EXIF区域列位置转换器
public class VerticalExifAreaColumnConverter : IValueConverter
{
    public static readonly VerticalExifAreaColumnConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? 0 : 1; // 垂直布局时在第一列，水平布局时在第二列
        }
        return 1;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下评分区域行位置转换器
public class VerticalRatingAreaRowConverter : IValueConverter
{
    public static readonly VerticalRatingAreaRowConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? 2 : 0; // 垂直布局时在第三行，水平布局时在第一行
        }
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下评分区域列位置转换器
public class VerticalRatingAreaColumnConverter : IValueConverter
{
    public static readonly VerticalRatingAreaColumnConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? 0 : 2; // 垂直布局时在第一列，水平布局时在第三列
        }
        return 2;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下控制区域边框转换器
public class VerticalControlBorderConverter : IValueConverter
{
    public static readonly VerticalControlBorderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0); // 垂直布局时下边框，水平布局时右边框
        }
        return new Thickness(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下EXIF区域边框转换器
public class VerticalExifBorderConverter : IValueConverter
{
    public static readonly VerticalExifBorderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return isVertical ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0); // 垂直布局时下边框，水平布局时右边框
        }
        return new Thickness(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 垂直布局下评分区域边框转换器
public class VerticalRatingBorderConverter : IValueConverter
{
    public static readonly VerticalRatingBorderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVertical)
        {
            return new Thickness(0); // 最后一个区域不需要边框
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
