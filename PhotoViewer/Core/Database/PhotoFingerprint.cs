using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PhotoViewer.Core.Database;

/// <summary>
/// 指纹计算的输入载荷。参与哈希的字段分列缓存，方便以后微调算法而不用重算特征向量。
/// </summary>
public sealed class PhotoFingerprintInput
{
    /// <summary>文件名去扩展名（小写）。例如 "a6702747"。</summary>
    public string FilenameNoExt { get; init; } = "";

    /// <summary>拍摄时间（秒精度），来自 EXIF DateTimeOriginal；缺失则使用文件修改时间。</summary>
    public DateTime? CaptureTime { get; init; }

    /// <summary>拍摄时间的毫秒分量字符串（EXIF SubSecTimeOriginal），缺失时为 null。</summary>
    public string? CaptureSubSec { get; init; }
}

/// <summary>
/// 多字段组合哈希生成照片指纹。
/// 设计依据：同一次曝光导出的 RAW+HIF/JPG 在文件名、拍摄时间、毫秒戳上字节级一致 → 同指纹；
/// 连拍场景通过 SubSec 毫秒区分；文件名编号循环（9999→0001）由日期自然区隔。
/// </summary>
public static class PhotoFingerprint
{
    /// <summary>
    /// 依据输入计算指纹字符串（40 字符小写十六进制 SHA1）。输入任一字段变更都会得到不同指纹。
    /// </summary>
    /// <param name="input">参与哈希的规范化字段。</param>
    /// <returns>指纹字符串，可作为数据库主键。</returns>
    public static string Compute(PhotoFingerprintInput input)
    {
        var payload = Canonicalize(input);
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 将输入规范化为稳定字符串形式。字段以 NUL(\0) 分隔，避免任一字段含分隔符引发歧义。
    /// </summary>
    internal static string Canonicalize(PhotoFingerprintInput input)
    {
        var sb = new StringBuilder(96);
        sb.Append("v1\0");
        sb.Append(NormalizeFilename(input.FilenameNoExt)).Append('\0');
        sb.Append(NormalizeCaptureTime(input.CaptureTime)).Append('\0');
        sb.Append(NormalizeSubSec(input.CaptureSubSec));
        return sb.ToString();
    }

    /// <summary>文件名去扩展名 + 小写归一。调用方已做去扩展，但保险处理一次。</summary>
    private static string NormalizeFilename(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var noExt = Path.GetFileNameWithoutExtension(name);
        return noExt.ToLowerInvariant();
    }

    /// <summary>拍摄时间格式化为 ISO-8601 秒精度 UTC 形式，避免本地化差异。</summary>
    private static string NormalizeCaptureTime(DateTime? time)
    {
        if (!time.HasValue) return "";
        var t = time.Value;
        var utc = t.Kind switch
        {
            DateTimeKind.Utc => t,
            DateTimeKind.Local => t.ToUniversalTime(),
            _ => DateTime.SpecifyKind(t, DateTimeKind.Utc)
        };
        return utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>SubSec 字符串截取至毫秒精度（3 位），不足补零；空值原样。</summary>
    private static string NormalizeSubSec(string? subSec)
    {
        if (string.IsNullOrWhiteSpace(subSec)) return "";
        var digits = new StringBuilder(3);
        foreach (var c in subSec)
        {
            if (char.IsDigit(c)) digits.Append(c);
            if (digits.Length == 3) break;
        }
        if (digits.Length == 0) return "";
        while (digits.Length < 3) digits.Append('0');
        return digits.ToString();
    }

    /// <summary>
    /// 从 ExifData + 文件名方便地构建输入。外部调用层（如 ImageFile 加载后）可用此方法。
    /// </summary>
    /// <param name="filename">原始文件名（可含扩展名）。</param>
    /// <param name="exif">EXIF 数据，可空。</param>
    /// <param name="fallbackTime">EXIF 无拍摄时间时的回退，例如文件修改时间。</param>
    public static PhotoFingerprintInput BuildInput(string filename, ExifData? exif, DateTime? fallbackTime = null)
    {
        return new PhotoFingerprintInput
        {
            FilenameNoExt = Path.GetFileNameWithoutExtension(filename ?? ""),
            CaptureTime = exif?.DateTimeOriginal ?? fallbackTime,
            CaptureSubSec = exif?.SubSecTimeOriginal
        };
    }
}
