using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Converters;

/// <summary>
/// 把 <see cref="PerfInfoLevel"/> 语义级别映射为提示文字颜色
/// (Normal=深灰 / Warning=木色 / Error=鲑红,与原 VM 内硬编码颜色一致)。
/// </summary>
public sealed class PerfInfoLevelToBrushConverter : IValueConverter
{
    /// <summary>共享实例,供 x:Static 引用。</summary>
    public static readonly PerfInfoLevelToBrushConverter Instance = new();

    /// <summary>
    /// 将语义级别转换为前景画刷。
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            PerfInfoLevel.Warning => new SolidColorBrush(Colors.BurlyWood),
            PerfInfoLevel.Error => new SolidColorBrush(Colors.Salmon),
            _ => new SolidColorBrush(Colors.DarkGray),
        };

    /// <summary>
    /// 不支持反向转换。
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
