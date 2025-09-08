using System;
using System.Globalization;
using Avalonia.Data.Converters;
using MetadataExtractor;

namespace PhotoViewer.Converters;

/// <summary>
/// 光圈值转换器
/// </summary>
public class ApertureConverter : IValueConverter
{
    public static readonly ApertureConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Rational aperture)
        {
            return $"f/{(double)aperture.Numerator/aperture.Denominator}";
        }
        return "--";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 快门速度转换器
/// </summary>
public class ExposureTimeConverter : IValueConverter
{
    public static readonly ExposureTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Rational exposureTime)
        {
            if (exposureTime.Numerator == 1)
                return $"1/{exposureTime.Denominator}";
            else
                return $"{(double)exposureTime.Numerator/exposureTime.Denominator}s";
        }
        return "--";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 焦距转换器
/// </summary>
public class FocalLengthConverter : IValueConverter
{
    public static readonly FocalLengthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Rational focalLength)
        {
            return $"{focalLength.Numerator/focalLength.Denominator}mm";
        }
        return "--";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 日期时间转换器
/// </summary>
public class DateTimeConverter : IValueConverter
{
    public static readonly DateTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dateTime)
        {
            return dateTime.ToString("yyyy-MM-dd HH:mm");
        }
        return "--";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

