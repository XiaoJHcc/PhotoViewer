using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
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
            // 修改内存中的星级数据
            var newRatingByte = (byte)('0' + rating);
            fileData[ratingPosition] = newRatingByte;
            
            // 安全模式：校验修改前的数据
            if (enableSafeMode && backupData != null)
            {
                if (!VerifyFullFileModification(backupData, fileData, ratingPosition))
                {
                    Console.WriteLine($"[XMP Writer] Android: Data modification verification failed");
                    return false;
                }
            }
            
            // 使用多重写入策略确保立即同步
            bool writeSuccess = false;
            
            // 策略1: 标准写入 + 激进同步
            try
            {
                await using (var writeStream = await file.OpenWriteAsync())
                {
                    await writeStream.WriteAsync(fileData, 0, fileData.Length);
                    await writeStream.FlushAsync();
                    
                    if (writeStream is FileStream fs)
                    {
                        fs.Flush(true);
                    }
                }
                
                // 立即执行激进同步
                await AggressiveAndroidSync();
                writeSuccess = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[XMP Writer] Android strategy 1 failed: {ex.Message}");
            }
            
            // 策略2: 如果策略1失败，使用分块写入
            if (!writeSuccess)
            {
                try
                {
                    await using (var writeStream = await file.OpenWriteAsync())
                    {
                        // 分块写入并在每块后强制同步
                        const int chunkSize = 8192;
                        for (int i = 0; i < fileData.Length; i += chunkSize)
                        {
                            int currentChunkSize = Math.Min(chunkSize, fileData.Length - i);
                            await writeStream.WriteAsync(fileData, i, currentChunkSize);
                            await writeStream.FlushAsync();
                        }
                        
                        if (writeStream is FileStream fs)
                        {
                            fs.Flush(true);
                        }
                    }
                    
                    await AggressiveAndroidSync();
                    writeSuccess = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[XMP Writer] Android strategy 2 failed: {ex.Message}");
                }
            }
            
            if (!writeSuccess)
            {
                Console.WriteLine($"[XMP Writer] Android: All write strategies failed");
                return false;
            }
            
            // 安全模式验证
            if (enableSafeMode && backupData != null)
            {
                // 多次验证确保写入成功
                bool verificationPassed = false;
                
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await Task.Delay(50); // 短暂等待
                    
                    try
                    {
                        byte[] verifyData;
                        await using (var verifyStream = await file.OpenReadAsync())
                        {
                            verifyData = new byte[verifyStream.Length];
                            await verifyStream.ReadAsync(verifyData, 0, verifyData.Length);
                        }
                        
                        if (VerifyFullFileModification(backupData, verifyData, ratingPosition))
                        {
                            verificationPassed = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[XMP Writer] Android verification attempt {attempt + 1} failed: {ex.Message}");
                    }
                }
                
                if (!verificationPassed)
                {
                    Console.WriteLine($"[XMP Writer] Android: File verification failed after write");
                    
                    // 尝试恢复原始数据
                    try
                    {
                        await using (var restoreStream = await file.OpenWriteAsync())
                        {
                            await restoreStream.WriteAsync(backupData, 0, backupData.Length);
                            await restoreStream.FlushAsync();
                            
                            if (restoreStream is FileStream restoreFs)
                            {
                                restoreFs.Flush(true);
                            }
                        }
                        await AggressiveAndroidSync();
                    }
                    catch (Exception restoreEx)
                    {
                        Console.WriteLine($"[XMP Writer] Android: Failed to restore backup: {restoreEx.Message}");
                    }
                    
                    return false;
                }
            }
            
            // 最终激进同步
            await AggressiveAndroidSync();
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] Android: Unexpected error: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// 激进的安卓平台强制同步操作
    /// </summary>
    private static async Task AggressiveAndroidSync()
    {
        try
        {
            // 步骤1: 立即强制GC
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            
            // 步骤2: 尝试调用系统级同步命令
            await ExecuteSystemSync();
            
            // 步骤3: 尝试直接调用Linux系统调用 (如果可用)
            await TryLinuxSync();
            
            // 步骤4: 强制线程让步，让系统处理I/O
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(20);
                await Task.Yield();
            }
            
            // 步骤5: 再次执行系统同步
            await ExecuteSystemSync();
            
            Console.WriteLine($"[XMP Writer] Android: Aggressive sync completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] Android aggressive sync warning: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 执行系统级同步命令
    /// </summary>
    private static async Task ExecuteSystemSync()
    {
        try
        {
            // 方法1: 尝试执行sync命令
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "sync";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                
                if (process.Start())
                {
                    await process.WaitForExitAsync();
                    Console.WriteLine($"[XMP Writer] Android: sync command executed");
                }
            }
            catch
            {
                // sync命令可能不可用，尝试其他方法
            }
            
            // 方法2: 尝试sh -c sync
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "sh";
                process.StartInfo.Arguments = "-c sync";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                
                if (process.Start())
                {
                    await process.WaitForExitAsync();
                    Console.WriteLine($"[XMP Writer] Android: sh sync command executed");
                }
            }
            catch
            {
                // sh命令可能不可用
            }
            
            // 方法3: 尝试echo命令强制缓冲区刷新
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "sh";
                process.StartInfo.Arguments = "-c 'echo 3 > /proc/sys/vm/drop_caches 2>/dev/null || true'";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                
                if (process.Start())
                {
                    await process.WaitForExitAsync();
                }
            }
            catch
            {
                // 缓存清理可能没有权限，继续
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] System sync error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 尝试直接调用Linux系统调用
    /// </summary>
    private static async Task TryLinuxSync()
    {
        try
        {
            // 尝试通过P/Invoke调用fsync系统调用
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    // 这需要libc，在安卓上可能可用
                    LibC.sync();
                    Console.WriteLine($"[XMP Writer] Android: Direct sync() called");
                }
                catch (DllNotFoundException)
                {
                    // libc不可用，跳过
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[XMP Writer] Direct sync error: {ex.Message}");
                }
            }
            
            await Task.Delay(10); // 给系统时间处理同步
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XMP Writer] Linux sync error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Linux C库函数导入
    /// </summary>
    private static class LibC
    {
        [DllImport("libc", EntryPoint = "sync", SetLastError = true)]
        public static extern void sync();
        
        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        public static extern int fsync(int fd);
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
