using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhotoViewer.ViewModels;

namespace PhotoViewer.Core;

/// <summary>
/// 平台 XMP 字节写入器。
/// 共享层只负责定位待修改字节，实际持久化由平台实现处理。
/// </summary>
public interface IXmpPlatformWriter
{
    /// <summary>
    /// 将目标文件指定偏移处的单个字节改写为新值，并在需要时完成平台侧校验。
    /// </summary>
    /// <param name="file">目标文件</param>
    /// <param name="bytePosition">目标字节在文件中的偏移</param>
    /// <param name="expectedByte">写入前预期的原始字节</param>
    /// <param name="newByte">新的目标字节</param>
    /// <param name="enableSafeMode">是否启用安全校验</param>
    /// <returns>写入是否成功</returns>
    Task<bool> TryWriteByteAsync(IStorageFile file, long bytePosition, byte expectedByte, byte newByte, bool enableSafeMode);
}

/// <summary>
/// 本地文件路径 XMP 字节写入辅助方法。
/// 用于 Windows、macOS、iOS 等可直接拿到真实路径的平台复用原位写入逻辑。
/// </summary>
public static class XmpLocalFileWriter
{
    /// <summary>
    /// 尝试从存储文件中提取可直接写入的本地路径。
    /// </summary>
    /// <param name="file">目标存储文件</param>
    /// <param name="localPath">成功时输出真实本地路径</param>
    /// <returns>是否成功获取</returns>
    public static bool TryGetWritableLocalPath(IStorageFile file, out string localPath)
    {
        localPath = file.Path?.LocalPath ?? string.Empty;
        return !string.IsNullOrWhiteSpace(localPath)
               && file.Path != null
               && file.Path.IsFile
               && File.Exists(localPath);
    }

    /// <summary>
    /// 对本地文件执行单字节原位写入。
    /// </summary>
    /// <param name="filePath">真实文件路径</param>
    /// <param name="bytePosition">目标字节偏移</param>
    /// <param name="expectedByte">写入前预期字节</param>
    /// <param name="newByte">新字节值</param>
    /// <param name="enableSafeMode">是否启用安全校验</param>
    /// <param name="logPrefix">日志前缀</param>
    /// <param name="useWriteThrough">是否启用写穿透选项</param>
    /// <returns>写入是否成功</returns>
    public static bool TryWriteByte(string filePath, long bytePosition, byte expectedByte, byte newByte, bool enableSafeMode, string logPrefix, bool useWriteThrough)
    {
        string? backupPath = null;

        try
        {
            if (enableSafeMode)
            {
                var existingByte = ReadByte(filePath, bytePosition);
                if (existingByte != expectedByte)
                {
                    Console.WriteLine($"[XMP Writer] {logPrefix} pre-check mismatch: expected={(char)expectedByte}, actual={(char)existingByte}");
                    return false;
                }

                backupPath = filePath + ".xmp_backup";
                File.Copy(filePath, backupPath, true);
            }

            var options = useWriteThrough ? FileOptions.WriteThrough : FileOptions.None;
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1, options))
            {
                stream.Seek(bytePosition, SeekOrigin.Begin);
                stream.WriteByte(newByte);
                stream.Flush(true);
            }

            var verificationPassed = !enableSafeMode || ReadByte(filePath, bytePosition) == newByte;
            if (!verificationPassed)
            {
                Console.WriteLine($"[XMP Writer] {logPrefix} post-check mismatch");
                RestoreBackup(filePath, backupPath, logPrefix);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] {logPrefix} write error: {ex.Message}");
            RestoreBackup(filePath, backupPath, logPrefix);
            return false;
        }
        finally
        {
            DeleteBackup(backupPath, logPrefix);
        }
    }

    /// <summary>
    /// 从本地文件读取指定偏移处的单个字节。
    /// </summary>
    /// <param name="filePath">真实文件路径</param>
    /// <param name="bytePosition">目标字节偏移</param>
    /// <returns>读取到的字节</returns>
    public static byte ReadByte(string filePath, long bytePosition)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
        stream.Seek(bytePosition, SeekOrigin.Begin);
        var value = stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException("Failed to read target byte from local file");
        }

        return (byte)value;
    }

    /// <summary>
    /// 尝试从备份恢复原文件。
    /// </summary>
    /// <param name="filePath">目标文件路径</param>
    /// <param name="backupPath">备份文件路径</param>
    /// <param name="logPrefix">日志前缀</param>
    private static void RestoreBackup(string filePath, string? backupPath, string logPrefix)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            return;
        }

        try
        {
            File.Copy(backupPath, filePath, true);
        }
        catch (Exception restoreEx)
        {
            Console.WriteLine($"[XMP Writer] {logPrefix} restore failed: {restoreEx.Message}");
        }
    }

    /// <summary>
    /// 删除临时备份文件。
    /// </summary>
    /// <param name="backupPath">备份文件路径</param>
    /// <param name="logPrefix">日志前缀</param>
    private static void DeleteBackup(string? backupPath, string logPrefix)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            return;
        }

        try
        {
            File.Delete(backupPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] {logPrefix} cleanup failed: {ex.Message}");
        }
    }
}

/// <summary>
/// XMP 写入器，用于修改 JPG 文件中的单个 XMP 星级字符。
/// </summary>
public static class XmpWriter
{
    private const byte JpegMarkerStart = 0xFF;
    private const byte App1Marker = 0xE1;

    private static IXmpPlatformWriter _platformWriter = new NoopXmpPlatformWriter();

    /// <summary>
    /// 由平台启动时注入具体的 XMP 写入实现。
    /// </summary>
    /// <param name="writer">平台写入器</param>
    public static void Initialize(IXmpPlatformWriter writer)
    {
        _platformWriter = writer ?? new NoopXmpPlatformWriter();
    }

    /// <summary>
    /// 检查文件是否为支持的格式。
    /// </summary>
    /// <param name="fileName">文件名</param>
    /// <returns>是否支持</returns>
    private static bool IsSupportedFormat(string fileName)
    {
        return true;
    }

    /// <summary>
    /// 安全地写入 XMP 星级到文件，只修改星级数字。
    /// </summary>
    /// <param name="file">要修改的存储文件</param>
    /// <param name="rating">星级值 (0-5)</param>
    /// <param name="enableSafeMode">是否启用安全模式（备份和校验）</param>
    /// <returns>成功返回 true，失败或文件不符合要求返回 false</returns>
    public static async Task<bool> WriteRatingAsync(IStorageFile file, int rating, bool enableSafeMode = true)
    {
        var totalSw = Stopwatch.StartNew();
        var stepSw = Stopwatch.StartNew();

        if (rating < 0 || rating > 5)
        {
            Console.WriteLine($"[XMP Writer] Invalid rating value {rating}");
            return false;
        }

        var fileName = file.Name;
        if (!IsSupportedFormat(fileName))
        {
            Console.WriteLine($"[XMP Writer] File {fileName} is not a supported format");
            return false;
        }

        Console.WriteLine($"[XMP Writer] === BEGIN WriteRating({fileName}, {rating}, safe={enableSafeMode}) ===");

        try
        {
            stepSw.Restart();
            const int headerSize = 256 * 1024;
            byte[] headerData;
            byte[] ratingSearchData;
            long fileLength;
            await using (var stream = await file.OpenReadAsync())
            {
                fileLength = stream.Length;
                var readSize = (int)Math.Min(fileLength, headerSize);
                headerData = new byte[readSize];
                await stream.ReadExactlyAsync(headerData, 0, readSize);
            }

            ratingSearchData = headerData;
            Console.WriteLine($"[XMP Writer] [1] ReadHeader: {stepSw.ElapsedMilliseconds}ms ({headerData.Length / 1024}KB of {fileLength / 1024}KB)");

            stepSw.Restart();
            var ratingPosition = FindXmpRatingPositionFast(headerData);
            Console.WriteLine($"[XMP Writer] [2] FindXmpPos: {stepSw.ElapsedMilliseconds}ms (pos={ratingPosition})");

            if (ratingPosition == -1 && fileLength > headerSize)
            {
                stepSw.Restart();
                byte[] fullData;
                await using (var stream = await file.OpenReadAsync())
                {
                    fullData = new byte[stream.Length];
                    await stream.ReadExactlyAsync(fullData, 0, fullData.Length);
                }

                ratingPosition = FindXmpRatingPosition(fullData);
                ratingSearchData = fullData;
                Console.WriteLine($"[XMP Writer] [2b] FullScan fallback: {stepSw.ElapsedMilliseconds}ms (pos={ratingPosition})");
            }

            if (ratingPosition == -1)
            {
                Console.WriteLine($"[XMP Writer] No XMP Rating found in {fileName}");
                return false;
            }

            var currentRatingByte = ratingSearchData[ratingPosition];
            var currentRating = currentRatingByte - '0';

            if (currentRatingByte == '1' && ratingPosition > 0 && ratingSearchData[ratingPosition - 1] == '-')
            {
                Console.WriteLine($"[XMP Writer] Unsupported rating format (-1) in {fileName}");
                return false;
            }

            if (currentRating < 0 || currentRating > 5)
            {
                Console.WriteLine($"[XMP Writer] Invalid current rating {currentRating}");
                return false;
            }

            if (currentRating == rating)
            {
                Console.WriteLine($"[XMP Writer] Rating unchanged ({currentRating}), skip. Total: {totalSw.ElapsedMilliseconds}ms");
                return true;
            }

            stepSw.Restart();
            var newRatingByte = (byte)('0' + rating);
            bool result = await _platformWriter.TryWriteByteAsync(file, ratingPosition, currentRatingByte, newRatingByte, enableSafeMode);
            Console.WriteLine($"[XMP Writer] [3] PlatformWrite: {stepSw.ElapsedMilliseconds}ms => {(result ? "OK" : "FAIL")}");
            Console.WriteLine($"[XMP Writer] === END WriteRating({fileName}): {(result ? "OK" : "FAIL")} Total: {totalSw.ElapsedMilliseconds}ms ===");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 快速查找 XMP 星级位置，使用 Span 进行 SIMD 加速搜索。
    /// 优先搜索最常见的 Rating 模式，命中后立即返回。
    /// </summary>
    private static int FindXmpRatingPositionFast(byte[] data)
    {
        var span = data.AsSpan();

        ReadOnlySpan<byte> ratingAttr = "xmp:Rating=\""u8;
        ReadOnlySpan<byte> ratingAttr2 = "xap:Rating=\""u8;
        ReadOnlySpan<byte> ratingAttr3 = ":Rating=\""u8;
        ReadOnlySpan<byte> ratingElem = "<xmp:Rating>"u8;
        ReadOnlySpan<byte> ratingElem2 = "<xap:Rating>"u8;

        int pos = SpanIndexOf(span, ratingAttr);
        if (pos != -1) { var r = ValidateRatingDigit(data, pos + ratingAttr.Length); if (r != -1) return r; }

        pos = SpanIndexOf(span, ratingAttr2);
        if (pos != -1) { var r = ValidateRatingDigit(data, pos + ratingAttr2.Length); if (r != -1) return r; }

        pos = SpanIndexOf(span, ratingAttr3);
        if (pos != -1) { var r = ValidateRatingDigit(data, pos + ratingAttr3.Length); if (r != -1) return r; }

        pos = SpanIndexOf(span, ratingElem);
        if (pos != -1) { var r = ValidateRatingDigit(data, pos + ratingElem.Length); if (r != -1) return r; }

        pos = SpanIndexOf(span, ratingElem2);
        if (pos != -1) { var r = ValidateRatingDigit(data, pos + ratingElem2.Length); if (r != -1) return r; }

        return -1;
    }

    /// <summary>
    /// 在 Span 中查找子序列的首次出现位置（SIMD 加速）。
    /// </summary>
    private static int SpanIndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern)
    {
        return data.IndexOf(pattern);
    }

    /// <summary>
    /// 验证指定偏移处是否为有效的星级数字（0~5），排除 -1 格式。
    /// </summary>
    private static int ValidateRatingDigit(byte[] data, int offset)
    {
        if (offset >= data.Length) return -1;
        var ch = data[offset];
        if (ch >= '0' && ch <= '5')
        {
            if (ch == '1' && offset > 0 && data[offset - 1] == '-') return -1;
            return offset;
        }

        return -1;
    }

    /// <summary>
    /// 在文件数据中查找 XMP 星级数字的位置（通用方法，支持多种文件格式）。
    /// </summary>
    private static int FindXmpRatingPosition(byte[] data)
    {
        try
        {
            var jpegXmpPosition = FindXmpRatingInJpegApp1(data);
            if (jpegXmpPosition != -1)
            {
                return jpegXmpPosition;
            }

            return FindXmpRatingInEntireFile(data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 在 JPEG APP1 段中查找 XMP 星级（保持向后兼容）。
    /// </summary>
    private static int FindXmpRatingInJpegApp1(byte[] data)
    {
        try
        {
            for (int i = 0; i < data.Length - 1; i++)
            {
                if (data[i] == JpegMarkerStart && data[i + 1] == App1Marker)
                {
                    if (i + 4 >= data.Length) continue;

                    var segmentLength = (data[i + 2] << 8) | data[i + 3];
                    var segmentStart = i + 4;
                    var segmentEnd = segmentStart + segmentLength - 2;

                    if (segmentEnd > data.Length) continue;

                    var xmpIdentifiers = new[]
                    {
                        "http://ns.adobe.com/xap/1.0/\0",
                        "adobe:ns:meta/",
                        "http://ns.adobe.com/photoshop/1.0/",
                        "<?xpacket"
                    };

                    bool isXmpSegment = false;
                    int xmpDataStart = segmentStart;

                    foreach (var identifier in xmpIdentifiers)
                    {
                        var identifierBytes = Encoding.ASCII.GetBytes(identifier);
                        if (segmentLength < identifierBytes.Length + 2) continue;

                        bool matches = true;
                        for (int j = 0; j < identifierBytes.Length; j++)
                        {
                            if (segmentStart + j >= data.Length || data[segmentStart + j] != identifierBytes[j])
                            {
                                matches = false;
                                break;
                            }
                        }

                        if (matches)
                        {
                            isXmpSegment = true;
                            xmpDataStart = segmentStart + identifierBytes.Length;
                            break;
                        }
                    }

                    if (!isXmpSegment)
                    {
                        xmpDataStart = segmentStart;
                    }

                    var ratingPos = FindRatingInXmpData(data, xmpDataStart, segmentEnd);
                    if (ratingPos != -1)
                    {
                        return ratingPos;
                    }
                }
            }

            return -1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] JPEG APP1 search error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 在整个文件中搜索 XMP 星级（通用方法）。
    /// </summary>
    private static int FindXmpRatingInEntireFile(byte[] data)
    {
        try
        {
            var xmpPacketIdentifiers = new[]
            {
                "<?xpacket",
                "<x:xmpmeta",
                "<rdf:RDF",
                "http://ns.adobe.com/xap/1.0/",
                "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
                "adobe:ns:meta/"
            };

            var xmpRegions = new List<(int start, int end)>();

            foreach (var identifier in xmpPacketIdentifiers)
            {
                var identifierBytes = Encoding.UTF8.GetBytes(identifier);

                for (int i = 0; i <= data.Length - identifierBytes.Length; i++)
                {
                    bool matches = true;
                    for (int j = 0; j < identifierBytes.Length; j++)
                    {
                        if (data[i + j] != identifierBytes[j])
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                    {
                        int regionStart = Math.Max(0, i - 1000);
                        int regionEnd = Math.Min(data.Length, i + 50000);
                        xmpRegions.Add((regionStart, regionEnd));
                    }
                }
            }

            if (xmpRegions.Count == 0)
            {
                xmpRegions.Add((0, data.Length));
            }

            foreach (var (start, end) in xmpRegions)
            {
                var ratingPos = FindRatingInXmpData(data, start, end);
                if (ratingPos != -1)
                {
                    return ratingPos;
                }
            }

            return -1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] Entire file search error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 在 XMP 数据中查找星级数字。
    /// </summary>
    private static int FindRatingInXmpData(byte[] data, int start, int end)
    {
        try
        {
            var ratingPatterns = new[]
            {
                "xmp:Rating=\"",
                "xap:Rating=\"",
                ":Rating=\"",
                "Rating=\"",
                "rating=\"",
                "<xmp:Rating>",
                "<xap:Rating>",
                "<Rating>",
                "<rating>",
                "photoshop:Rating=\"",
                "ps:Rating=\"",
                "tiff:Rating=\"",
                "rdf:Rating=\"",
                "dc:Rating=\""
            };

            foreach (var pattern in ratingPatterns)
            {
                var patternBytes = Encoding.UTF8.GetBytes(pattern);

                for (int i = start; i <= end - patternBytes.Length - 1; i++)
                {
                    bool matches = true;
                    for (int j = 0; j < patternBytes.Length; j++)
                    {
                        if (data[i + j] != patternBytes[j])
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                    {
                        var ratingOffset = i + patternBytes.Length;
                        if (ratingOffset < end)
                        {
                            var ratingChar = data[ratingOffset];
                            if (ratingChar >= '0' && ratingChar <= '5')
                            {
                                return ratingOffset;
                            }

                            if (ratingChar == '-' && ratingOffset + 1 < end && data[ratingOffset + 1] == '1')
                            {
                                continue;
                            }
                        }
                    }
                }
            }

            var xmlEndPatterns = new[]
            {
                "</xmp:Rating>",
                "</xap:Rating>",
                "</Rating>",
                "</rating>",
                "</photoshop:Rating>",
                "</ps:Rating>",
                "</tiff:Rating>",
                "</rdf:Rating>",
                "</dc:Rating>"
            };

            foreach (var endPattern in xmlEndPatterns)
            {
                var endPatternBytes = Encoding.UTF8.GetBytes(endPattern);

                for (int i = start; i <= end - endPatternBytes.Length; i++)
                {
                    bool matches = true;
                    for (int j = 0; j < endPatternBytes.Length; j++)
                    {
                        if (data[i + j] != endPatternBytes[j])
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                    {
                        for (int k = i - 1; k >= Math.Max(start, i - 10); k--)
                        {
                            var ratingChar = data[k];
                            if (ratingChar >= '0' && ratingChar <= '5')
                            {
                                if (k > start && data[k - 1] == '-' && ratingChar == '1')
                                {
                                    continue;
                                }

                                return k;
                            }
                        }
                    }
                }
            }

            return -1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 从文件中读取当前 XMP 星级，不修改文件。
    /// </summary>
    public static async Task<int?> ReadRatingAsync(IStorageFile file)
    {
        try
        {
            var fileName = file.Name;
            if (!IsSupportedFormat(fileName))
            {
                return null;
            }

            byte[] fileData;
            await using (var stream = await file.OpenReadAsync())
            {
                fileData = new byte[stream.Length];
                await stream.ReadExactlyAsync(fileData, 0, fileData.Length);
            }

            var ratingPosition = FindXmpRatingPosition(fileData);
            if (ratingPosition == -1) return null;

            var ratingByte = fileData[ratingPosition];
            if (ratingByte == '1' && ratingPosition > 0 && fileData[ratingPosition - 1] == '-')
            {
                return null;
            }

            if (ratingByte >= '0' && ratingByte <= '5')
            {
                return ratingByte - '0';
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// 未注入平台实现时的空写入器。
/// </summary>
internal sealed class NoopXmpPlatformWriter : IXmpPlatformWriter
{
    /// <summary>
    /// 未注入平台实现时直接返回失败，避免落到不明确的回退路径。
    /// </summary>
    public Task<bool> TryWriteByteAsync(IStorageFile file, long bytePosition, byte expectedByte, byte newByte, bool enableSafeMode)
    {
        Console.WriteLine("[XMP Writer] No platform writer registered");
        return Task.FromResult(false);
    }
}