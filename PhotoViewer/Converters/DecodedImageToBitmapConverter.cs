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
    /// P1-M3 租约安全:拷贝全程持租约,已归还池的图像返回 null(绑定回占位符)。
    /// </summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DecodedImage image) return null;
        if (!image.TryAcquire()) return null;
        try
        {
            return DecodedImageAdapter.ToBitmap(image);
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>
    /// 不支持反向转换。
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
