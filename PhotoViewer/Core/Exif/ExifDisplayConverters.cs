using System;
using System.Globalization;
using MetadataExtractor;

namespace PhotoViewer.Core.Exif;

/// <summary>
/// 控制栏 EXIF 摘要的取值格式（TOOLBAR-05），四端与 Web 同一口径：
/// 光圈 <c>f/2.8</c>、快门 <c>1/250</c> / <c>0.3s</c> / <c>1s</c>、焦距 <c>10.5mm</c>、曝光补偿 <c>+0.7 EV</c>、时间 <c>yyyy-MM-dd HH:mm</c>；缺失或非法值为 <c>--</c>。
/// </summary>
public static class ExifDisplayFormat
{
    /// <summary>缺失值占位。</summary>
    public const string Missing = "--";

    /// <summary>不足 1 秒的快门与 1/n 的最大相对偏差。</summary>
    private const double ShutterFractionTolerance = 0.02;

    /// <summary>光圈：至多 1 位小数、去尾零。</summary>
    public static string Aperture(Rational? value)
        => ToDouble(value) is double f && f > 0 ? $"f/{Trim(f, 1)}" : Missing;

    /// <summary>快门：不足 1 秒且接近 1/n（偏差 ≤2%）写分数，否则写至多 2 位小数的秒数。</summary>
    public static string ExposureTime(Rational? value)
    {
        if (ToDouble(value) is not double seconds || seconds <= 0) return Missing;
        if (seconds >= 1) return $"{Trim(seconds, 2)}s";
        double reciprocal = Math.Round(1 / seconds, MidpointRounding.AwayFromZero);
        if (reciprocal >= 1 && Math.Abs(1 / reciprocal - seconds) <= seconds * ShutterFractionTolerance)
            return $"1/{reciprocal.ToString("0", CultureInfo.InvariantCulture)}";
        return $"{Trim(seconds, 2)}s";
    }

    /// <summary>焦距：至多 1 位小数、去尾零。</summary>
    public static string FocalLength(Rational? value)
        => ToDouble(value) is double mm && mm > 0 ? $"{Trim(mm, 1)}mm" : Missing;

    /// <summary>曝光补偿：至多 1 位小数，正值带 <c>+</c>。</summary>
    public static string ExposureBias(Rational? value)
    {
        if (ToDouble(value) is not double ev) return Missing;
        string text = Trim(ev, 1);
        return $"{(ev > 0 && text != "0" ? "+" : "")}{text} EV";
    }

    /// <summary>拍摄时间摘要。</summary>
    public static string DateTime(DateTime? value)
        => value is { } dateTime ? dateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : Missing;

    private static double? ToDouble(Rational? value)
    {
        if (value is not { } rational || rational.Denominator == 0) return null;
        double result = (double)rational.Numerator / rational.Denominator;
        return double.IsFinite(result) ? result : null;
    }

    private static string Trim(double value, int digits)
    {
        double rounded = Math.Round(value, digits, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0;
        return rounded.ToString(digits == 1 ? "0.#" : "0.##", CultureInfo.InvariantCulture);
    }
}
