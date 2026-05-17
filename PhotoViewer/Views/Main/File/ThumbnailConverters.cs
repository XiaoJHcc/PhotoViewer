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

/// <summary>相似度分数(0~1)→ "97%" 形式的百分比文本。</summary>
public class SimilarityScoreConverter : IValueConverter
{
    public static readonly SimilarityScoreConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double score)
        {
            var pct = (int)Math.Round(Math.Clamp(score, 0.0, 1.0) * 100);
            return pct + "%";
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 相似度分数(0~1)映射到柱状条占比(0~1):75% 以下为 0,100% 为 1,中间线性插值。
/// 配合 <see cref="ProgressFractionToWidthConverter"/> 在卡片第二行绘制柱状进度条。
/// </summary>
public class SimilarityScoreToProgressFractionConverter : IValueConverter
{
    public static readonly SimilarityScoreToProgressFractionConverter Instance = new();

    /// <summary>柱状条对应的下限分数(75%)。</summary>
    private const double LowerBound = 0.75;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double score)
        {
            var clamped = Math.Clamp(score, LowerBound, 1.0);
            return (clamped - LowerBound) / (1.0 - LowerBound);
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 把进度占比([0,1])与总宽度参数相乘,产出柱状条的宽度像素。
/// null 输入返回 0,由 IsVisible 单独控制柱状条显隐。
/// </summary>
public class ProgressFractionToWidthConverter : IValueConverter
{
    public static readonly ProgressFractionToWidthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double fraction &&
            parameter is string s &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
        {
            return Math.Max(0.0, Math.Min(1.0, fraction)) * total;
        }
        return 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
