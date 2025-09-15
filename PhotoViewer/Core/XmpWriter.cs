using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// XMP 写入器，用于修改 JPG 文件中的单个 XMP 星级字符
/// </summary>
public static class XmpWriter
{
    private const byte JpegMarkerStart = 0xFF;
    private const byte App1Marker = 0xE1;
    
    // 备份缓存管理
    private static string? _lastBackupPath;
    private static string? _lastBackupOriginalPath;
    
    /// <summary>
    /// 检查是否为安卓平台
    /// </summary>
    private static bool IsAndroid => OperatingSystem.IsAndroid();
    
    /// <summary>
    /// 安全地写入 XMP 星级到 JPG 文件，只修改星级数字
    /// </summary>
    /// <param name="file">要修改的存储文件</param>
    /// <param name="rating">星级值 (0-5)</param>
    /// <param name="enableSafeMode">是否启用安全模式（备份和校验）</param>
    /// <returns>成功返回 true，失败或文件不符合要求返回 false</returns>
    public static async Task<bool> WriteRatingAsync(IStorageFile file, int rating, bool enableSafeMode = true)
    {
        // 验证输入参数
        if (rating < 0 || rating > 5)
        {
            Console.WriteLine($"[XMP Writer] Invalid rating value {rating}");
            return false;
        }
        
        var fileName = file.Name;
        if (!fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && 
            !fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[XMP Writer] File {fileName} is not a JPG file");
            return false;
        }
        
        try
        {
            // 清理上一次的备份缓存
            CleanupPreviousBackup();
            
            // 全量读取文件数据查找星级位置
            byte[] fileData;
            await using (var stream = await file.OpenReadAsync())
            {
                fileData = new byte[stream.Length];
                await stream.ReadAsync(fileData, 0, fileData.Length);
            }
            
            // 查找 XMP 星级位置
            var ratingPosition = FindXmpRatingPosition(fileData);
            if (ratingPosition == -1)
            {
                Console.WriteLine($"[XMP Writer] No XMP Rating found in {fileName}");
                return false;
            }
            
            // 获取当前星级值
            var currentRatingByte = fileData[ratingPosition];
            var currentRating = currentRatingByte - '0';
            
            // 检查当前星级是否为 -1 (未评级)，如果是则按不符合要求处理
            if (currentRatingByte == '1' && ratingPosition > 0 && fileData[ratingPosition - 1] == '-')
            {
                Console.WriteLine($"[XMP Writer] Unsupported rating format (-1) in {fileName}");
                return false;
            }
            
            if (currentRating < 0 || currentRating > 5)
            {
                Console.WriteLine($"[XMP Writer] Invalid current rating {currentRating}");
                return false;
            }
            
            // 如果星级相同，无需修改
            if (currentRating == rating)
            {
                return true;
            }
            
            byte[]? backupData = null;
            
            // 安全模式：创建备份
            if (enableSafeMode)
            {
                backupData = new byte[fileData.Length];
                Array.Copy(fileData, backupData, fileData.Length);
            }
            
            try
            {
                // 安卓平台特殊处理
                if (IsAndroid)
                {
                    return await WriteRatingAndroidAsync(file, fileData, ratingPosition, rating, enableSafeMode, backupData);
                }
                else
                {
                    return await WriteRatingDesktopAsync(file, ratingPosition, rating, enableSafeMode, backupData);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[XMP Writer] {ex.Message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// 安卓平台的写入处理
    /// </summary>
    private static async Task<bool> WriteRatingAndroidAsync(IStorageFile file, byte[] fileData, int ratingPosition, int rating, bool enableSafeMode, byte[]? backupData)
    {
        try
        {
            // 安卓端也只修改单个字符，但需要通过重写整个文件实现
            var newRatingByte = (byte)('0' + rating);
            
            // 创建修改后的数据副本
            var modifiedData = new byte[fileData.Length];
            Array.Copy(fileData, modifiedData, fileData.Length);
            modifiedData[ratingPosition] = newRatingByte;
            
            // 尝试以追加模式打开文件来检查是否支持随机访问
            bool supportsRandomAccess = false;
            try
            {
                await using (var testStream = await file.OpenWriteAsync())
                {
                    if (testStream.CanSeek)
                    {
                        supportsRandomAccess = true;
                    }
                }
            }
            catch
            {
                supportsRandomAccess = false;
            }
            
            if (supportsRandomAccess)
            {
                // 支持随机访问，直接修改单个字符
                await using (var writeStream = await file.OpenWriteAsync())
                {
                    if (writeStream.CanSeek && writeStream.Length >= ratingPosition + 1)
                    {
                        writeStream.Seek(ratingPosition, SeekOrigin.Begin);
                        writeStream.WriteByte(newRatingByte);
                        await writeStream.FlushAsync();
                    }
                    else
                    {
                        // 回退到全文件写入
                        writeStream.SetLength(0);
                        await writeStream.WriteAsync(modifiedData, 0, modifiedData.Length);
                        await writeStream.FlushAsync();
                    }
                }
            }
            else
            {
                // 不支持随机访问，使用全文件替换
                // 先创建临时文件
                var tempFileName = $"{file.Name}.tmp";
                var folder = await file.GetParentAsync();
                if (folder != null)
                {
                    try
                    {
                        // 创建临时文件
                        var tempFile = await folder.CreateFileAsync(tempFileName);
                        await using (var tempStream = await tempFile.OpenWriteAsync())
                        {
                            await tempStream.WriteAsync(modifiedData, 0, modifiedData.Length);
                            await tempStream.FlushAsync();
                        }
                        
                        // 验证临时文件写入成功
                        byte[] tempData;
                        await using (var verifyStream = await tempFile.OpenReadAsync())
                        {
                            tempData = new byte[verifyStream.Length];
                            await verifyStream.ReadAsync(tempData, 0, tempData.Length);
                        }
                        
                        if (tempData.Length == modifiedData.Length)
                        {
                            // 删除原文件并重命名临时文件
                            await file.DeleteAsync();
                            // 注意：由于 Avalonia 的限制，我们无法直接重命名文件
                            // 所以我们需要重新创建原文件
                            var newFile = await folder.CreateFileAsync(file.Name);
                            await using (var newStream = await newFile.OpenWriteAsync())
                            {
                                await newStream.WriteAsync(tempData, 0, tempData.Length);
                                await newStream.FlushAsync();
                            }
                        }
                        
                        // 清理临时文件
                        try { await tempFile.DeleteAsync(); } catch { }
                    }
                    catch (Exception)
                    {
                        // 临时文件方法失败，尝试直接覆盖
                        await using (var writeStream = await file.OpenWriteAsync())
                        {
                            // 尝试写入修改后的数据
                            await writeStream.WriteAsync(modifiedData, 0, modifiedData.Length);
                            await writeStream.FlushAsync();
                        }
                    }
                }
                else
                {
                    throw new InvalidOperationException("Cannot access parent folder");
                }
            }
            
            // 安全模式：校验修改结果
            if (enableSafeMode && backupData != null)
            {
                // 重新读取文件进行校验
                byte[] writtenData;
                await using (var verifyStream = await file.OpenReadAsync())
                {
                    writtenData = new byte[verifyStream.Length];
                    await verifyStream.ReadAsync(writtenData, 0, writtenData.Length);
                }
                
                if (!VerifyFullFileModification(backupData, writtenData, ratingPosition))
                {
                    // 校验失败，还原备份
                    Console.WriteLine($"[XMP Writer] Verification failed, restoring backup");
                    
                    // 尝试恢复备份
                    try
                    {
                        if (supportsRandomAccess)
                        {
                            await using (var restoreStream = await file.OpenWriteAsync())
                            {
                                restoreStream.SetLength(0);
                                await restoreStream.WriteAsync(backupData, 0, backupData.Length);
                                await restoreStream.FlushAsync();
                            }
                        }
                        else
                        {
                            // 使用临时文件方式恢复
                            var folder = await file.GetParentAsync();
                            if (folder != null)
                            {
                                var tempFileName = $"{file.Name}.restore";
                                var tempFile = await folder.CreateFileAsync(tempFileName);
                                await using (var tempStream = await tempFile.OpenWriteAsync())
                                {
                                    await tempStream.WriteAsync(backupData, 0, backupData.Length);
                                    await tempStream.FlushAsync();
                                }
                                
                                await file.DeleteAsync();
                                var restoredFile = await folder.CreateFileAsync(file.Name);
                                await using (var restoredStream = await restoredFile.OpenWriteAsync())
                                {
                                    await restoredStream.WriteAsync(backupData, 0, backupData.Length);
                                    await restoredStream.FlushAsync();
                                }
                                
                                try { await tempFile.DeleteAsync(); } catch { }
                            }
                        }
                    }
                    catch (Exception restoreEx)
                    {
                        Console.WriteLine($"[XMP Writer] {restoreEx.Message}");
                    }
                    
                    return false;
                }
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] Android write error: {ex.Message}");
            
            // 如果有备份且启用安全模式，尝试还原
            if (enableSafeMode && backupData != null)
            {
                try
                {
                    await using (var restoreStream = await file.OpenWriteAsync())
                    {
                        await restoreStream.WriteAsync(backupData, 0, backupData.Length);
                        await restoreStream.FlushAsync();
                    }
                }
                catch (Exception restoreEx)
                {
                    Console.WriteLine($"[XMP Writer] {restoreEx.Message}");
                }
            }
            
            return false;
        }
    }
    
    /// <summary>
    /// 桌面平台的写入处理
    /// </summary>
    private static async Task<bool> WriteRatingDesktopAsync(IStorageFile file, int ratingPosition, int rating, bool enableSafeMode, byte[]? backupData)
    {
        var filePath = file.Path.LocalPath;
        string? backupPath = null;
        
        try
        {
            // 安全模式：创建文件备份
            if (enableSafeMode)
            {
                backupPath = filePath + ".xmp_backup";
                File.Copy(filePath, backupPath, true);
            }
            
            // 直接修改文件中的单个字符
            var newRatingByte = (byte)('0' + rating);
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write))
            {
                stream.Seek(ratingPosition, SeekOrigin.Begin);
                stream.WriteByte(newRatingByte);
                stream.Flush();
            }
            
            // 安全模式：校验修改结果
            if (enableSafeMode && backupPath != null)
            {
                if (!await VerifyFullFileModificationFromFiles(backupPath, filePath, ratingPosition))
                {
                    // 校验失败，还原备份
                    Console.WriteLine($"[XMP Writer] Verification failed, restoring backup");
                    File.Copy(backupPath, filePath, true);
                    return false;
                }
                
                // 校验通过，立即删除备份文件
                File.Delete(backupPath);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] Desktop write error: {ex.Message}");
            
            // 如果有备份且启用安全模式，尝试还原
            if (enableSafeMode && backupPath != null && File.Exists(backupPath))
            {
                try
                {
                    File.Copy(backupPath, filePath, true);
                    File.Delete(backupPath);
                }
                catch (Exception restoreEx)
                {
                    Console.WriteLine($"[XMP Writer] {restoreEx.Message}");
                }
            }
            
            return false;
        }
        finally
        {
            // 确保备份文件被清理
            if (backupPath != null && File.Exists(backupPath))
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[XMP Writer] {ex.Message}");
                }
            }
        }
    }
    
    /// <summary>
    /// 清理上一次的备份缓存
    /// </summary>
    private static void CleanupPreviousBackup()
    {
        if (!IsAndroid && _lastBackupPath != null && File.Exists(_lastBackupPath))
        {
            try
            {
                File.Delete(_lastBackupPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[XMP Writer] {ex.Message}");
            }
            finally
            {
                _lastBackupPath = null;
                _lastBackupOriginalPath = null;
            }
        }
    }
    
    /// <summary>
    /// 在文件数据中查找 XMP 星级数字的位置
    /// </summary>
    private static int FindXmpRatingPosition(byte[] data)
    {
        try
        {
            // 查找 APP1 XMP 段
            for (int i = 0; i < data.Length - 1; i++)
            {
                if (data[i] == JpegMarkerStart && data[i + 1] == App1Marker)
                {
                    // 检查是否为 XMP 段
                    if (i + 4 >= data.Length) continue;
                    
                    var segmentLength = (data[i + 2] << 8) | data[i + 3];
                    var segmentStart = i + 4;
                    var segmentEnd = segmentStart + segmentLength - 2;
                    
                    if (segmentEnd > data.Length) continue;
                    
                    // 检查 XMP 标识符
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
                    
                    // 如果没有找到标识符，尝试直接搜索（有些 XMP 段可能格式不标准）
                    if (!isXmpSegment)
                    {
                        xmpDataStart = segmentStart;
                    }
                    
                    // 在此段中搜索星级模式
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
            Console.WriteLine($"[XMP Writer] {ex.Message}");
            return -1;
        }
    }
    
    /// <summary>
    /// 在 XMP 数据中查找星级数字
    /// </summary>
    private static int FindRatingInXmpData(byte[] data, int start, int end)
    {
        try
        {
            // 扩展的 XMP 星级模式
            var ratingPatterns = new[]
            {
                // 属性格式
                "xmp:Rating=\"",
                "xap:Rating=\"", 
                ":Rating=\"",
                "Rating=\"",
                "rating=\"",
                
                // XML 元素格式
                "<xmp:Rating>",
                "<xap:Rating>",
                "<Rating>",
                "<rating>",
                
                // 命名空间变体
                "photoshop:Rating=\"",
                "ps:Rating=\"",
                "tiff:Rating=\"",
                
                // RDF 格式
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
                        
                        // 验证下一个字符是有效的星级数字 (0-5)
                        if (ratingOffset < end)
                        {
                            var ratingChar = data[ratingOffset];
                            
                            if (ratingChar >= '0' && ratingChar <= '5')
                            {
                                return ratingOffset;
                            }
                            // 如果遇到 -1 格式，直接跳过不处理
                            else if (ratingChar == '-' && ratingOffset + 1 < end && data[ratingOffset + 1] == '1')
                            {
                                continue; // 跳过 -1 格式
                            }
                        }
                    }
                }
            }
            
            // 通过 XML 结束标签进行替代搜索
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
                        // 向前查找星级数字
                        for (int k = i - 1; k >= Math.Max(start, i - 10); k--)
                        {
                            var ratingChar = data[k];
                            if (ratingChar >= '0' && ratingChar <= '5')
                            {
                                // 检查是否为 -1 格式
                                if (k > start && data[k - 1] == '-' && ratingChar == '1')
                                {
                                    continue; // 跳过 -1 格式
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
    /// 全文件字节校验，确保除指定位置外所有字节都相同（内存版本）
    /// </summary>
    private static bool VerifyFullFileModification(byte[] backupData, byte[] modifiedData, long ratingPosition)
    {
        try
        {
            if (backupData.Length != modifiedData.Length)
            {
                Console.WriteLine($"[XMP Writer] File length changed");
                return false;
            }
            
            int changedBytes = 0;
            var changedPositions = new List<long>();
            
            // 逐字节比较整个文件
            for (long i = 0; i < backupData.Length; i++)
            {
                if (backupData[i] != modifiedData[i])
                {
                    changedBytes++;
                    changedPositions.Add(i);
                    
                    // 如果有超过1个字节被修改，立即记录并失败
                    if (changedBytes > 1)
                    {
                        Console.WriteLine($"[XMP Writer] Multiple bytes changed");
                        return false;
                    }
                }
            }
            
            // 检查修改情况
            if (changedBytes == 0)
            {
                // 没有字节被修改（可能是相同的星级值）
                return true;
            }
            else if (changedBytes == 1)
            {
                var changedPosition = changedPositions[0];
                if (changedPosition == ratingPosition)
                {
                    // 只有预期的星级位置被修改
                    var oldRating = (char)backupData[ratingPosition];
                    var newRating = (char)modifiedData[ratingPosition];
                    
                    // 验证修改的字符都是有效的星级字符
                    if (oldRating >= '0' && oldRating <= '5' && newRating >= '0' && newRating <= '5')
                    {
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"[XMP Writer] Invalid rating characters");
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine($"[XMP Writer] Unexpected change at position {changedPosition}");
                    return false;
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// 全文件字节校验，确保除指定位置外所有字节都相同（文件版本）
    /// </summary>
    private static async Task<bool> VerifyFullFileModificationFromFiles(string backupPath, string modifiedPath, long ratingPosition)
    {
        try
        {
            // 读取备份文件和修改后的文件
            var backupData = await File.ReadAllBytesAsync(backupPath);
            var modifiedData = await File.ReadAllBytesAsync(modifiedPath);
            
            return VerifyFullFileModification(backupData, modifiedData, ratingPosition);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// 从文件中读取当前 XMP 星级，不修改文件
    /// </summary>
    public static async Task<int?> ReadRatingAsync(IStorageFile file)
    {
        try
        {
            var fileName = file.Name;
            if (!fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && 
                !fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            
            // 全量读取文件查找星级
            byte[] fileData;
            await using (var stream = await file.OpenReadAsync())
            {
                fileData = new byte[stream.Length];
                await stream.ReadAsync(fileData, 0, fileData.Length);
            }
            
            var ratingPosition = FindXmpRatingPosition(fileData);
            if (ratingPosition == -1) return null;
            
            var ratingByte = fileData[ratingPosition];
            
            // 检查是否为 -1 格式，如果是则返回 null
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
