using System;
using System.Globalization;
using Avalonia.Data.Converters;
using PhotoViewer.Core.Image;
using PhotoViewer.Platform;

namespace PhotoViewer.Converters;

/// <summary>
/// 把 <see cref="DecodedImage"/> 转换为可绑定的 Avalonia 位图(经 <see cref="DecodedImageAdapter"/> 缓存,同一实例只转一次)。
/// </summary>
public sealed class DecodedImageToBitmapConverter : IValueConverter
{
    /// <summary>共享实例,供 x:Static 引用。</summary>
    public static readonly DecodedImageToBitmapConverter Instance = new();

    /// <summary>
    /// 转换为位图;null 或失败返回 null。
    /// </summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DecodedImage image ? DecodedImageAdapter.ToBitmap(image) : null;

    /// <summary>
    /// 不支持反向转换。
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
