using System;
using System.Globalization;
using MetadataExtractor;

namespace PhotoViewer.Core.Exif;

/// <summary>
/// 控制栏 EXIF 展示值格式化。原 UI 转换器随旧壳删除，这里保留同一套文本口径供共享 VM 使用。
/// </summary>
public class ApertureConverter
{
    public static readonly ApertureConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Rational aperture)
        {
            return $"f/{(double)aperture.Numerator / aperture.Denominator}";
        }
        return "--";
    }
}

/// <summary>
/// 快门速度格式化。
/// </summary>
public class ExposureTimeConverter
{
    public static readonly ExposureTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Rational exposureTime)
        {
            if (exposureTime.Numerator == 1)
                return $"1/{exposureTime.Denominator}";
            else
                return $"{(double)exposureTime.Numerator / exposureTime.Denominator}s";
        }
        return "--";
    }
}

/// <summary>
/// 焦距格式化。
/// </summary>
public class FocalLengthConverter
{
    public static readonly FocalLengthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Rational focalLength)
        {
            return $"{focalLength.Numerator / focalLength.Denominator}mm";
        }
        return "--";
    }
}

/// <summary>
/// 曝光补偿格式化。
/// </summary>
public class ExposureBiasConverter
{
    public static readonly ExposureBiasConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Rational exposureBias)
        {
            var biasValue = (double)exposureBias.Numerator / exposureBias.Denominator;
            var sign = biasValue >= 0 ? "+" : "";
            return $"{sign}{biasValue:F1} EV";
        }
        return "--";
    }
}

/// <summary>
/// 日期时间格式化。
/// </summary>
public class DateTimeConverter
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
}
