using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace PhotoViewer.Views;

/// <summary>缩略图当前项边框转换器:选中项返回 DodgerBlue,其它返回透明。</summary>
public class BoolToCurrentBorderConverter : IValueConverter
{
    public static readonly BoolToCurrentBorderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isCurrent && isCurrent)
        {
            return new SolidColorBrush(Colors.DodgerBlue);
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>拍摄日期格式转换器:DateTimeOffset → "MM-dd HH:mm"。</summary>
public class PhotoDateConverter : IValueConverter
{
    public static readonly PhotoDateConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dateTime)
        {
            return dateTime.ToString("MM-dd HH:mm");
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>缓存状态边框转换器:已缓存返回半透明蓝色,否则透明。</summary>
public class BoolToCachedBorderConverter : IValueConverter
{
    public static readonly BoolToCachedBorderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isInCache && isInCache)
        {
            return new SolidColorBrush(Color.FromArgb(180, 70, 130, 180));
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>水平翻转转换器:true → -1.0(翻转 ScaleX),false → 1.0。</summary>
public class FlipTransformConverter : IValueConverter
{
    public static readonly FlipTransformConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool needsFlip && needsFlip)
        {
            return -1.0;
        }
        return 1.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
